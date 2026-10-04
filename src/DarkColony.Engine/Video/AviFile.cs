using System.Buffers.Binary;
using System.Text;

namespace DarkColony.Engine.Video;

/// <summary>A PCM audio stream's format (WAVEFORMAT).</summary>
public sealed record AviAudioFormat(int Channels, int SamplesPerSecond, int BitsPerSample);

/// <summary>
/// A RIFF AVI as the original's videos use it: one video stream (Cinepak,
/// <c>cvid</c>) and an optional PCM audio stream. The original reads them through
/// AVIFile and decompresses with the system codec (<c>avi.c</c>, <c>0x406A00</c>-<c>0x408E90</c>).
/// </summary>
public sealed class AviFile
{
    private AviFile(string videoHandler, int width, int height, int microsecondsPerFrame,
        IReadOnlyList<byte[]> videoFrames, AviAudioFormat? audioFormat, byte[] audio)
    {
        VideoHandler = videoHandler;
        Width = width;
        Height = height;
        MicrosecondsPerFrame = microsecondsPerFrame;
        VideoFrames = videoFrames;
        AudioFormat = audioFormat;
        Audio = audio;
    }

    /// <summary>The video codec FOURCC (BITMAPINFOHEADER compression), such as <c>cvid</c>.</summary>
    public string VideoHandler { get; }
    public int Width { get; }
    public int Height { get; }
    public int MicrosecondsPerFrame { get; }

    /// <summary>The compressed video frames in stream order (empty chunks repeat the previous frame).</summary>
    public IReadOnlyList<byte[]> VideoFrames { get; }

    public AviAudioFormat? AudioFormat { get; }

    /// <summary>The whole audio stream, its chunks joined.</summary>
    public byte[] Audio { get; }

    public static AviFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Parse(memory.GetBuffer().AsSpan(0, (int)memory.Length));
    }

    public static AviFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || Tag(data, 0) != "RIFF" || Tag(data, 8) != "AVI ")
            throw new InvalidDataException("Not a RIFF AVI file.");

        var streamTypes = new List<string>();
        string? handler = null;
        int width = 0, height = 0, microsecondsPerFrame = 0;
        AviAudioFormat? audioFormat = null;
        var frames = new List<byte[]>();
        using var audio = new MemoryStream();

        void Walk(ReadOnlySpan<byte> span, int start, int end)
        {
            var offset = start;
            while (offset + 8 <= end)
            {
                var id = Tag(span, offset);
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 4)..]);
                var body = offset + 8;
                var bodyEnd = (int)Math.Min((long)body + size, end);
                if (id is "LIST" or "RIFF")
                {
                    Walk(span, body + 4, bodyEnd);
                }
                else if (id == "avih" && size >= 4)
                {
                    microsecondsPerFrame = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[body..]);
                }
                else if (id == "strh" && size >= 8)
                {
                    streamTypes.Add(Tag(span, body));
                }
                else if (id == "strf" && streamTypes.Count > 0)
                {
                    if (streamTypes[^1] == "vids" && size >= 40)
                    {
                        width = BinaryPrimitives.ReadInt32LittleEndian(span[(body + 4)..]);
                        height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(span[(body + 8)..]));
                        handler = Tag(span, body + 16);
                    }
                    else if (streamTypes[^1] == "auds" && size >= 16)
                    {
                        var format = BinaryPrimitives.ReadUInt16LittleEndian(span[body..]);
                        if (format != 1) throw new InvalidDataException($"AVI audio format {format} is not PCM.");
                        audioFormat = new AviAudioFormat(
                            BinaryPrimitives.ReadUInt16LittleEndian(span[(body + 2)..]),
                            (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(body + 4)..]),
                            BinaryPrimitives.ReadUInt16LittleEndian(span[(body + 14)..]));
                    }
                }
                else if (id.Length == 4 && char.IsAsciiDigit(id[0]) && char.IsAsciiDigit(id[1]))
                {
                    var index = (id[0] - '0') * 10 + (id[1] - '0');
                    var type = index < streamTypes.Count ? streamTypes[index] : string.Empty;
                    if (type == "vids" && id[2..] is "dc" or "db") frames.Add(span[body..bodyEnd].ToArray());
                    else if (type == "auds" && id[2..] == "wb") audio.Write(span[body..bodyEnd]);
                }
                offset = body + size + (size & 1);
            }
        }

        Walk(data, 12, data.Length);
        if (handler is null) throw new InvalidDataException("AVI has no video stream format.");
        return new AviFile(handler, width, height, microsecondsPerFrame, frames, audioFormat, audio.ToArray());
    }

    private static string Tag(ReadOnlySpan<byte> data, int offset) => Encoding.ASCII.GetString(data.Slice(offset, 4));
}
