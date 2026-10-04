using System.Text;

namespace DarkColony.Engine.Data;

/// <summary>
/// The encyclopedia's fixed labels, which dc.exe keeps as string literals
/// (set at <c>0x4026A4</c>-<c>0x4026DC</c>, <c>0x402DA6</c> and <c>0x402ED7</c>).
/// The text gadgets 12 and 13 read "Earth" and "Mars", and gadget 16 holds
/// the category title. They are read from the user's executable.
/// </summary>
public sealed record NativeEncyclopediaLabels(string Earth, string Mars, string HumanForces, string GrayForces, string AlienArtifacts)
{
    public static NativeEncyclopediaLabels Load(string executablePath) => FromImage(PeImage.Load(executablePath));

    public static NativeEncyclopediaLabels FromImage(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new NativeEncyclopediaLabels(
            Read(image, 0x4721C0), Read(image, 0x4721C8), Read(image, 0x4721D0), Read(image, 0x4721E8), Read(image, 0x4721F4));
    }

    /// <summary>The title for the port's category index (0 Grays, 1 Humans, 2 Artifacts).</summary>
    public string CategoryTitle(int category) => category switch
    {
        0 => GrayForces,
        1 => HumanForces,
        _ => AlienArtifacts,
    };

    private static string Read(PeImage image, uint address)
    {
        var bytes = image.AtVirtualAddress(address, 32);
        var end = bytes.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException($"No string at 0x{address:X}.");
        return Encoding.ASCII.GetString(bytes[..end]);
    }
}
