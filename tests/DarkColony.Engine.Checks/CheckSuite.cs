using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

/// <summary>What a check needs, and how it may run.</summary>
[Flags]
internal enum CheckTags
{
    None = 0,
    /// <summary>Reads the original installation; skipped without one.</summary>
    Data = 1,
    /// <summary>Takes seconds: whole-scenario runs.</summary>
    Slow = 2,
    /// <summary>Runs alone after the others: it waits on sockets with timeouts.</summary>
    Serial = 4,
}

internal sealed record CheckCase(string Group, string Name, CheckTags Tags, Action Run)
{
    /// <summary>The derived <c>fast</c> tag: needs no installation and is not slow.</summary>
    public bool IsFast => (Tags & (CheckTags.Data | CheckTags.Slow)) == 0;

    public bool HasTag(string tag) => tag.ToLowerInvariant() switch
    {
        "fast" => IsFast,
        "data" => Tags.HasFlag(CheckTags.Data),
        "slow" => Tags.HasFlag(CheckTags.Slow),
        "serial" => Tags.HasFlag(CheckTags.Serial),
        _ => throw new ArgumentException($"Unknown tag '{tag}'; use fast, data, slow or serial."),
    };

    public string TagList => string.Join(',', new[] { IsFast ? "fast" : null, Tags.HasFlag(CheckTags.Data) ? "data" : null,
        Tags.HasFlag(CheckTags.Slow) ? "slow" : null, Tags.HasFlag(CheckTags.Serial) ? "serial" : null }.Where(tag => tag is not null));
}

internal sealed record CheckResult(CheckCase Case, TimeSpan Elapsed, string? Failure, bool Skipped, string Output);

/// <summary>Command-line selection and run options for <see cref="CheckSuite.Run"/>.</summary>
internal sealed record CheckOptions(
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> SkipTags,
    string? Filter,
    string? Group,
    bool List,
    bool Sequential,
    int Jobs,
    string? JUnitPath)
{
    public static CheckOptions Parse(string[] args)
    {
        string? Value(string option) => Array.IndexOf(args, option) is var index and >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        IReadOnlyList<string> Values(string option) => args
            .Select((argument, index) => (argument, index))
            .Where(pair => pair.argument == option && pair.index + 1 < args.Length)
            .Select(pair => args[pair.index + 1]).ToArray();
        return new CheckOptions(
            Values("--tag"),
            Values("--skip-tag"),
            Value("--filter") ?? Environment.GetEnvironmentVariable("DARKCOLONY_CHECK_FILTER"),
            Value("--group"),
            args.Contains("--list"),
            args.Contains("--sequential"),
            int.TryParse(Value("--jobs"), out var jobs) && jobs > 0 ? jobs : Environment.ProcessorCount,
            Value("--junit"));
    }
}

/// <summary>
/// Registry and runner for the engine checks. Each topic file registers its
/// checks; the runner selects them by tag, group and name, runs them in
/// parallel (serial ones last, alone), and prints the results in
/// registration order with their times. A check's console output is
/// buffered and printed under its result line.
/// </summary>
internal sealed class CheckSuite(string dataPath)
{
    private readonly List<CheckCase> cases = [];
    private readonly HashSet<string> names = [];

    public string DataPath { get; } = dataPath;
    public bool HasData => File.Exists(Path.Combine(DataPath, "dc.exe"));
    public IReadOnlyList<CheckCase> Cases => cases;

    public void Add(string group, string name, CheckTags tags, Action run)
    {
        if (!names.Add(name)) throw new InvalidOperationException($"Duplicate check name: {name}");
        cases.Add(new CheckCase(group, name, tags, run));
    }

    public int Run(CheckOptions options)
    {
        var selected = cases.Where(check =>
                (options.Tags.Count == 0 || options.Tags.Any(check.HasTag)) &&
                !options.SkipTags.Any(check.HasTag) &&
                (options.Group is null || check.Group.Equals(options.Group, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(options.Filter) || check.Name.Contains(options.Filter, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (options.List)
        {
            foreach (var check in selected) Console.WriteLine($"{check.Group,-16} {check.TagList,-16} {check.Name}");
            Console.WriteLine($"{selected.Length} check(s).");
            return 0;
        }

        var started = Stopwatch.StartNew();
        var results = new CheckResult[selected.Length];
        var console = Console.Out;
        Console.SetOut(new RoutingWriter(console));
        try
        {
            // Slow checks start first, so the short ones fill in around them.
            var parallel = Enumerable.Range(0, selected.Length).Where(index => !selected[index].Tags.HasFlag(CheckTags.Serial))
                .OrderBy(index => selected[index].Tags.HasFlag(CheckTags.Slow) ? 0 : 1).ToArray();
            var serial = Enumerable.Range(0, selected.Length).Where(index => selected[index].Tags.HasFlag(CheckTags.Serial)).ToArray();
            // One check at a time per worker: an array would be split into fixed ranges.
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(parallel, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
                new ParallelOptions { MaxDegreeOfParallelism = options.Sequential ? 1 : options.Jobs },
                index => results[index] = Execute(selected[index]));
            foreach (var index in serial) results[index] = Execute(selected[index]);
        }
        finally
        {
            Console.SetOut(console);
        }

        foreach (var result in results)
        {
            var status = result.Skipped ? "SKIP" : result.Failure is null ? "PASS" : "FAIL";
            Console.WriteLine($"{status} {result.Elapsed.TotalMilliseconds,6:0} ms  {result.Case.Name}");
            if (result.Output.Length != 0) Console.Write(result.Output);
        }
        if (!HasData && results.Any(result => result.Skipped))
            Console.WriteLine($"SKIP original-data checks: no dc.exe in {DataPath}");

        var failed = results.Where(result => result.Failure is not null).ToArray();
        var skipped = results.Count(result => result.Skipped);
        Console.WriteLine();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine("Slowest: " + string.Join("; ", results.OrderByDescending(result => result.Elapsed).Take(5)
            .Select(result => string.Create(invariant, $"{result.Case.Name} {result.Elapsed.TotalSeconds:0.0}s"))));
        var checkSeconds = results.Sum(result => result.Elapsed.TotalSeconds);
        var jobs = options.Sequential ? 1 : options.Jobs;
        Console.WriteLine(string.Create(invariant,
            $"{results.Length - failed.Length - skipped} passed, {failed.Length} failed, {skipped} skipped in {started.Elapsed.TotalSeconds:0.0}s (checks took {checkSeconds:0.0}s, {jobs} at a time)."));
        if (options.JUnitPath is { } junit) WriteJUnit(junit, results, started.Elapsed);

        if (failed.Length != 0)
        {
            Console.Error.WriteLine($"{failed.Length} check(s) failed:");
            foreach (var result in failed) Console.Error.WriteLine($"FAIL {result.Case.Name}: {result.Failure}");
            return 1;
        }
        Console.WriteLine("All engine checks passed.");
        return 0;
    }

    private CheckResult Execute(CheckCase check)
    {
        if (check.Tags.HasFlag(CheckTags.Data) && !HasData) return new CheckResult(check, TimeSpan.Zero, null, true, "");
        var output = new StringWriter();
        RoutingWriter.Current.Value = output;
        var watch = Stopwatch.StartNew();
        string? failure = null;
        try
        {
            check.Run();
        }
        catch (Exception error)
        {
            failure = error.Message;
        }
        finally
        {
            RoutingWriter.Current.Value = null;
        }
        return new CheckResult(check, watch.Elapsed, failure, false, output.ToString());
    }

    private static void WriteJUnit(string path, IReadOnlyList<CheckResult> results, TimeSpan elapsed)
    {
        var suite = new XElement("testsuite",
            new XAttribute("name", "DarkColony.Engine.Checks"),
            new XAttribute("tests", results.Count),
            new XAttribute("failures", results.Count(result => result.Failure is not null)),
            new XAttribute("skipped", results.Count(result => result.Skipped)),
            new XAttribute("time", elapsed.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)),
            results.Select(result => new XElement("testcase",
                new XAttribute("classname", result.Case.Group),
                new XAttribute("name", result.Case.Name),
                new XAttribute("time", result.Elapsed.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)),
                result.Skipped ? new XElement("skipped") : null,
                result.Failure is { } failure ? new XElement("failure", new XAttribute("message", failure)) : null,
                result.Output.Length != 0 ? new XElement("system-out", result.Output) : null)));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
        new XDocument(new XElement("testsuites", suite)).Save(path);
    }

    /// <summary>
    /// Sends console output to the running check's buffer, which flows to
    /// the tasks the check starts itself; output outside a check passes through.
    /// </summary>
    private sealed class RoutingWriter(TextWriter fallback) : TextWriter
    {
        public static readonly AsyncLocal<StringWriter?> Current = new();

        public override Encoding Encoding => fallback.Encoding;

        public override void Write(char value)
        {
            if (Current.Value is { } buffer)
                lock (buffer) buffer.Write(value);
            else
                fallback.Write(value);
        }

        public override void Write(string? value)
        {
            if (Current.Value is { } buffer)
                lock (buffer) buffer.Write(value);
            else
                fallback.Write(value);
        }

        public override void WriteLine(string? value)
        {
            if (Current.Value is { } buffer)
                lock (buffer) buffer.WriteLine(value);
            else
                fallback.WriteLine(value);
        }
    }
}
