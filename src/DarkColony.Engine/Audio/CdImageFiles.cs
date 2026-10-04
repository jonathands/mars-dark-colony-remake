using System.Buffers.Binary;
using System.Text;

namespace DarkColony.Engine.Audio;

/// <summary>
/// The ISO 9660 file system on a CD image's first data track (MODE1/2352:
/// each raw sector carries 2048 user bytes after a 16-byte sync header).
/// The original reads what the installation lacks, such as <c>avi/</c>,
/// from the disc's <c>dc</c> folder.
/// </summary>
public sealed class CdImageFiles
{
    private const int UserBytes = 2048;
    private const int UserOffset = 16;
    private const int VolumeDescriptorSector = 16;

    private readonly CueTrack _track;
    private readonly Dictionary<string, (uint Extent, uint Size, bool Directory)> _entries = new(StringComparer.OrdinalIgnoreCase);

    private CdImageFiles(CueTrack track) => _track = track;

    public static CdImageFiles? Open(CueSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var track = sheet.Tracks.FirstOrDefault(candidate => !candidate.Audio);
        if (track is null) return null;
        var files = new CdImageFiles(track);
        using var stream = File.OpenRead(track.FilePath);
        var descriptor = files.ReadSector(stream, VolumeDescriptorSector);
        if (descriptor[0] != 1 || Encoding.ASCII.GetString(descriptor, 1, 5) != "CD001") return null;
        files.Index(stream, "", ReadRecord(descriptor, 156).Extent, ReadRecord(descriptor, 156).Size, depth: 0);
        return files;
    }

    public bool Exists(string path) => _entries.TryGetValue(Normalize(path), out var entry) && !entry.Directory;

    /// <summary>A read-only stream over the file at <paramref name="path"/> (like <c>dc/avi/intro.avi</c>), or null.</summary>
    public Stream? OpenFile(string path)
    {
        if (!_entries.TryGetValue(Normalize(path), out var entry) || entry.Directory) return null;
        return new SectorStream(File.OpenRead(_track.FilePath), _track.Offset, entry.Extent, entry.Size);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private void Index(Stream stream, string prefix, uint extent, uint size, int depth)
    {
        if (depth > 8) return;
        for (var sector = 0u; sector * UserBytes < size; sector++)
        {
            var data = ReadSector(stream, extent + sector);
            for (var offset = 0; offset < UserBytes && data[offset] != 0; offset += data[offset])
            {
                var record = ReadRecord(data, offset);
                var nameLength = data[offset + 32];
                if (nameLength == 1 && data[offset + 33] is 0 or 1) continue;
                var name = Encoding.ASCII.GetString(data, offset + 33, nameLength);
                var version = name.IndexOf(';');
                if (version >= 0) name = name[..version];
                name = name.TrimEnd('.');
                var path = prefix.Length == 0 ? name : $"{prefix}/{name}";
                var directory = (data[offset + 25] & 2) != 0;
                _entries[path] = (record.Extent, record.Size, directory);
                if (directory) Index(stream, path, record.Extent, record.Size, depth + 1);
            }
        }
    }

    private static (uint Extent, uint Size) ReadRecord(byte[] data, int offset) => (
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 2)),
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 10)));

    private byte[] ReadSector(Stream stream, uint sector)
    {
        var data = new byte[UserBytes];
        stream.Position = _track.Offset + (long)sector * CueSheet.SectorBytes + UserOffset;
        stream.ReadExactly(data);
        return data;
    }

    private sealed class SectorStream(FileStream file, long trackOffset, uint extent, uint size) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => size;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, size);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (count > 0 && _position < size)
            {
                var sector = _position / UserBytes;
                var within = (int)(_position % UserBytes);
                var length = (int)Math.Min(Math.Min(count, UserBytes - within), size - _position);
                file.Position = trackOffset + (extent + sector) * CueSheet.SectorBytes + UserOffset + within;
                var read = file.Read(buffer, offset, length);
                if (read == 0) break;
                _position += read;
                offset += read;
                count -= read;
                total += read;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => size + offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }
    }
}
