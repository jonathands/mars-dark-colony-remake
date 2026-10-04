using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DarkColony.App.Diagnostics;

/// <summary>
/// Plain-text session log for errors and lifecycle events that would otherwise
/// only appear in a modal dialog. Written to <c>--log &lt;path&gt;</c>, then
/// <c>DARKCOLONY_LOG</c>, then <c>%LOCALAPPDATA%\DarkColony.Port\logs\latest.log</c>;
/// the previous session is kept as <c>previous.log</c> beside it.
/// </summary>
internal static class RuntimeLog
{
    public const int ExitUnhandledException = 1;
    public const int ExitDataError = 2;

    private static readonly object Gate = new();
    private static StreamWriter? writer;

    public static string? FilePath { get; private set; }

    /// <summary>
    /// When set (<c>--no-dialogs</c> or <c>DARKCOLONY_NO_DIALOGS=1</c>), failures are
    /// logged and the process exits instead of blocking on a message box.
    /// </summary>
    public static bool DialogsSuppressed { get; private set; }

    public static void Initialize(IReadOnlyList<string> arguments)
    {
        DialogsSuppressed = arguments.Any(argument => argument.Equals("--no-dialogs", StringComparison.OrdinalIgnoreCase)) ||
            Environment.GetEnvironmentVariable("DARKCOLONY_NO_DIALOGS") == "1";
        var path = ArgumentValue(arguments, "--log") ??
            Environment.GetEnvironmentVariable("DARKCOLONY_LOG") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DarkColony.Port", "logs", "latest.log");
        try
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, "previous.log"), overwrite: true);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer = new StreamWriter(stream) { AutoFlush = true };
            FilePath = path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Logging must never prevent the game from starting.
            writer = null;
            FilePath = null;
        }

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        Info($"Dark Colony port {version} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · pid {Environment.ProcessId}");
        Info($"Arguments: {string.Join(' ', arguments.Select(Quote))}");
        Info($"Working directory: {Environment.CurrentDirectory}");
        if (DialogsSuppressed) Info("Dialogs suppressed; failures exit with a non-zero code.");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? error = null) =>
        Write("ERROR", error is null ? message : $"{message}{Environment.NewLine}{error}");

    /// <summary>Installs handlers so unhandled exceptions always reach the log.</summary>
    public static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => Fatal("Unhandled UI-thread exception", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Fatal("Unhandled exception", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Error("Unobserved task exception", eventArgs.Exception);
            eventArgs.SetObserved();
        };
    }

    /// <summary>Shows a blocking message unless dialogs are suppressed; always logs it.</summary>
    public static void ShowError(string title, string message)
    {
        Error($"{title}: {message}");
        if (DialogsSuppressed) return;
        var suffix = FilePath is null ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}Log: {FilePath}";
        MessageBox.Show(message + suffix, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static void Fatal(string context, Exception? error)
    {
        Error(context, error);
        ShowError("Dark Colony crashed", error?.Message ?? context);
        Info($"Exiting with code {ExitUnhandledException}.");
        Environment.Exit(ExitUnhandledException);
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}";
        lock (Gate)
        {
            try
            {
                writer?.WriteLine(line);
            }
            catch (IOException)
            {
                // A full disk or revoked handle must not turn logging into a crash.
            }
        }
    }

    private static string? ArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return arguments[index + 1];
        }
        return null;
    }

    private static string Quote(string argument) => argument.Contains(' ') ? $"\"{argument}\"" : argument;
}
