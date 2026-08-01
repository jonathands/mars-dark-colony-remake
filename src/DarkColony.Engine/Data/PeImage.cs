using System.Buffers.Binary;

namespace DarkColony.Engine.Data;

/// <summary>Small, read-only PE32 address translator for recovered data tables.</summary>
public sealed class PeImage
{
    private readonly byte[] data;
    private readonly Section[] sections;

    private PeImage(byte[] data, uint imageBase, Section[] sections)
    {
        this.data = data;
        ImageBase = imageBase;
        this.sections = sections;
    }

    public uint ImageBase { get; }

    public static PeImage Load(string path) => Parse(File.ReadAllBytes(path));

    public static PeImage Parse(byte[] data)
    {
        if (data.Length < 0x40 || data[0] != 'M' || data[1] != 'Z')
            throw new InvalidDataException("File is not an MZ executable.");

        var peOffset = ReadUInt32(data, 0x3c);
        if (peOffset > int.MaxValue || peOffset + 24 > data.Length)
            throw new InvalidDataException("PE header lies outside the file.");

        var pe = (int)peOffset;
        if (ReadUInt32(data, pe) != 0x00004550)
            throw new InvalidDataException("Executable has no PE signature.");

        var sectionCount = ReadUInt16(data, pe + 6);
        var optionalSize = ReadUInt16(data, pe + 20);
        var optional = pe + 24;
        if (optional + optionalSize > data.Length || ReadUInt16(data, optional) != 0x10b)
            throw new InvalidDataException("Expected a PE32 optional header.");

        var imageBase = ReadUInt32(data, optional + 28);
        var sectionTable = optional + optionalSize;
        var sections = new Section[sectionCount];
        for (var index = 0; index < sections.Length; index++)
        {
            var position = sectionTable + index * 40;
            if (position + 40 > data.Length) throw new InvalidDataException("Truncated PE section table.");
            sections[index] = new Section(
                ReadUInt32(data, position + 12),
                ReadUInt32(data, position + 8),
                ReadUInt32(data, position + 20),
                ReadUInt32(data, position + 16));
        }

        return new PeImage(data, imageBase, sections);
    }

    public ReadOnlySpan<byte> AtVirtualAddress(uint address, int length)
    {
        if (address < ImageBase || length < 0) throw new InvalidDataException("Invalid PE virtual-address request.");
        var rva = address - ImageBase;
        foreach (var section in sections)
        {
            var mappedSize = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || (ulong)rva + (uint)length > (ulong)section.VirtualAddress + mappedSize) continue;
            var offset = (ulong)section.RawOffset + rva - section.VirtualAddress;
            if (offset + (uint)length > (ulong)data.Length) throw new InvalidDataException("PE virtual address has no file data.");
            return data.AsSpan((int)offset, length);
        }

        throw new InvalidDataException($"PE virtual address 0x{address:x8} is not mapped.");
    }

    private static ushort ReadUInt16(byte[] source, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, 4));

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize);
}
