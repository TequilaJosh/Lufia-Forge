using System.IO;
using System.Text.Json;

namespace LufiaForge.Core.Maps;

/// <summary>
/// Presets the user made from a piece of a map (Area tool, Save as preset). They're kept for every project
/// (%APPDATA%\LufiaForge\presets.json) and stored by what their blocks are, not by block numbers: each cell is a block
/// record ("ground/overlay" for a roof or tree top), and the tileset's graphics checksum says where they
/// fit. <see cref="Prefabs.Placer"/> finds the blocks again on the map they're placed on.
/// </summary>
public static class CustomPresets
{
    public sealed record Preset(string Id, string Name, int W, int H, string[] Cells, uint Graphics, DateTime Made, string From)
    {
        public int Count => Cells.Count(c => c.Length > 0);
    }

    private static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "presets.json");

    public static List<Preset> Load()
    {
        try { return File.Exists(File_) ? JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(File_)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void Save(List<Preset> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Add(Preset p) { var l = Load(); l.Add(p); Save(l); }
    public static void Remove(string id) { var l = Load(); l.RemoveAll(p => p.Id == id); Save(l); }
    public static void Rename(string id, string name) { var l = Load(); int i = l.FindIndex(p => p.Id == id); if (i >= 0) { l[i] = l[i] with { Name = name }; Save(l); } }

    /// <summary>The graphics checksum of a tileset (presets only go on maps whose tileset has the same graphics).</summary>
    public static uint GraphicsOf(RomBuffer rom, int tileset) =>
        CommunityPatches.Crc32(LufiaCompression.DecompressResource(rom, Tilesets.TilesResource(rom, tileset), out _));

    /// <summary>A preset from a rectangle of a map: every block in it, exactly as selected.</summary>
    public static Preset FromArea(RomBuffer rom, LufiaMap map, int x1, int y1, int w, int h, string name)
    {
        if (map.IsWorld) throw new InvalidOperationException("The world map's blocks can't be made into presets.");
        var ts = MapTileset.ForMap(rom, map);
        string Rec(int b) => b * BlockSet.Size + BlockSet.Size <= ts.Blocks.Length ? Convert.ToHexString(ts.Blocks, b * BlockSet.Size, BlockSet.Size) : "";
        string Key(int b) => ts.CompositeParts(b) is var (g, o) ? Rec(g) + "/" + Rec(o) : Rec(b);
        var cells = new string[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                cells[y * w + x] = Key(map.GetTile(x1 + x, y1 + y) & 0x3FF);
        return new Preset(Guid.NewGuid().ToString("N"), name, w, h, cells, GraphicsOf(rom, map.Tileset), DateTime.Now, $"map {map.MapId:X2} ({x1}, {y1})");
    }
}
