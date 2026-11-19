using System.IO;
using System.Text.Json;

namespace LufiaForge.Core.Maps;

// presets the user made from a piece of a map (Area tool, Save as preset). they're kept for every project
// (%APPDATA%\LufiaForge\presets.json) and stored by what their blocks are, not by block numbers: each cell is a block
// record ("ground/overlay" for a roof or tree top), and the tileset's graphics checksum says where they fit.
// Prefabs.Placer finds the blocks again on the map they get placed on
public static class CustomPresets
{
    public sealed record Preset(string Id, string Name, int W, int H, string[] Cells, uint Graphics, DateTime Made, string From)
    {
        public int Count => Cells.Count(c => c.Length > 0);
    }

    private static string PresetsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "presets.json");

    public static List<Preset> Load()
    {
        try { return File.Exists(PresetsFile) ? JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(PresetsFile)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void Save(List<Preset> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PresetsFile)!);
        File.WriteAllText(PresetsFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Add(Preset preset) { var presets = Load(); presets.Add(preset); Save(presets); }
    public static void Remove(string id) { var presets = Load(); presets.RemoveAll(p => p.Id == id); Save(presets); }
    public static void Rename(string id, string name) { var presets = Load(); int i = presets.FindIndex(p => p.Id == id); if (i >= 0) { presets[i] = presets[i] with { Name = name }; Save(presets); } }

    // graphics checksum of a tileset (presets only go on maps whose tileset has the same graphics)
    public static uint GraphicsOf(RomBuffer rom, int tileset) =>
        CommunityPatches.Crc32(LufiaCompression.DecompressResource(rom, Tilesets.TilesResource(rom, tileset), out _));

    // a preset from a rectangle of a map, every block in it exactly as selected
    public static Preset FromArea(RomBuffer rom, LufiaMap map, int x1, int y1, int width, int height, string name)
    {
        if (map.IsWorld) throw new InvalidOperationException("The world map's blocks can't be made into presets.");
        var tileset = MapTileset.ForMap(rom, map);
        string Record(int block) => block * BlockSet.Size + BlockSet.Size <= tileset.Blocks.Length ? Convert.ToHexString(tileset.Blocks, block * BlockSet.Size, BlockSet.Size) : "";
        string Key(int block) => tileset.CompositeParts(block) is var (ground, overlay) ? Record(ground) + "/" + Record(overlay) : Record(block);
        var cells = new string[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                cells[y * width + x] = Key(map.GetTile(x1 + x, y1 + y) & 0x3FF);
        return new Preset(Guid.NewGuid().ToString("N"), name, width, height, cells, GraphicsOf(rom, map.Tileset), DateTime.Now, $"map {map.MapId:X2} ({x1}, {y1})");
    }
}
