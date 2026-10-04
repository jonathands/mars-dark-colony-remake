using System.Buffers.Binary;
using System.IO.Compression;
using DarkColony.Engine.Data;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Terrain;

/// <summary>
/// Renders a whole scenario map at 1/4 scale with markers, to check visually how
/// SCN placements, PTH regions, and MAP terrain line up. Red squares mark
/// ground units at their SCN cell; blue squares mark the same units with Z
/// mirrored; dark tint marks cells whose PTH region (read in file order) is 0.
/// </summary>
internal static class MapDiagnostics
{
    private const int Scale = 4;

    public static void Render(GameInstallation installation, SimulationRules rules, string scenario, string output)
    {
        var file = installation.DataFile(["scenario", .. scenario.Split('/')]) + ".scn";
        var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
        var definition = ScenarioDefinition.Load(file);
        var tileset = BtsTileset.Load(installation.DataFile("scenario", definition.Tileset));
        var pthBytes = File.ReadAllBytes(Path.ChangeExtension(file, ".pth"));
        var regions = pthBytes.AsSpan(65536).ToArray();
        var full = TerrainRasterizer.RenderViewport(map, tileset, 0, 0, map.Width * 32, map.Height * 32);
        var width = full.Width / (32 / Scale);
        var height = full.Height / (32 / Scale);
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var source = ((y * (32 / Scale)) * full.Width + x * (32 / Scale)) * 4;
            Array.Copy(full.Rgba, source, rgba, (y * width + x) * 4, 4);
            rgba[(y * width + x) * 4 + 3] = 255;
            var cellX = x / Scale;
            var cellZ = y / Scale;
            if (regions[cellZ * map.Width + cellX] == 0)
            {
                var offset = (y * width + x) * 4;
                for (var channel = 0; channel < 3; channel++) rgba[offset + channel] = (byte)(rgba[offset + channel] / 3);
            }
        }

        foreach (var placement in definition.Placements)
        {
            if (placement.Team < 0 || placement.EntityId >= rules.Entities.Entities.Count) continue;
            var entity = rules.Entities.Entities[placement.EntityId];
            if (entity.MovementSpeed <= 0 || entity.MovementClass != 0) continue;
            Mark(placement.X, placement.Z, 255, 40, 40);
            Mark(placement.X, map.Height - 1 - placement.Z, 40, 120, 255);
        }

        File.WriteAllBytes(output, EncodePng(width, height, rgba));
        Console.WriteLine($"Wrote {width}x{height} map diagnostic to {output}");

        void Mark(int cellX, int cellZ, byte red, byte green, byte blue)
        {
            for (var dy = 0; dy < Scale; dy++)
            for (var dx = 0; dx < Scale; dx++)
            {
                var px = cellX * Scale + dx;
                var py = cellZ * Scale + dy;
                if ((uint)px >= width || (uint)py >= height) continue;
                var offset = (py * width + px) * 4;
                rgba[offset] = red;
                rgba[offset + 1] = green;
                rgba[offset + 2] = blue;
            }
        }
    }

    private static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);
        using (var raw = new MemoryStream())
        {
            using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(rgba, y * width * 4, width * 4);
                }
            }
            WriteChunk(output, "IDAT", raw.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. typeBytes, .. data]));
        output.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xffffffffu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        return ~crc;
    }
}
