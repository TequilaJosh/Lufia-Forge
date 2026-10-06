using CommunityToolkit.Mvvm.ComponentModel;

namespace LufiaForge.Core.Maps;

/// <summary>One entry in the list of maps.</summary>
public partial class MapInfo : ObservableObject
{
    public int MapId { get; }
    public int ResourceId { get; }
    public int Tileset { get; }
    public int Width { get; }
    public int Height { get; }
    public int ExitCount { get; }
    public int NpcCount { get; }

    /// <summary>Readable name (generated, or typed by the user).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _name = "";

    /// <summary>True when <see cref="Name"/> was typed by the user.</summary>
    [ObservableProperty] private bool _isCustomName;

    public MapInfo(int mapId, int resourceId, int tileset, int width, int height, int exitCount, int npcCount)
    {
        MapId = mapId; ResourceId = resourceId; Tileset = tileset; Width = width; Height = height;
        ExitCount = exitCount; NpcCount = npcCount;
    }

    public string Label => string.IsNullOrEmpty(Name) ? Details : $"{MapId:X2}  {Name}";

    public string Details => MapId == LufiaMap.WorldMap
        ? $"{MapId:X2}  World map  {Width}x{Height}"
        : $"{MapId:X2}  {Width}x{Height}  ts{Tileset}  {NpcCount} NPCs";
}

/// <summary>Finds every map in the ROM (117 in the US ROM).</summary>
public static class MapCatalog
{
    public static List<MapInfo> Scan(RomBuffer rom)
    {
        var list = new List<MapInfo>();
        for (int m = 0; m < 256; m++)
        {
            if (!LufiaMap.IsValidMap(rom, m, out var data, out _) || data == null) continue;
            int res = LufiaMap.MapDataResource(rom, m);
            int w = data[0x12] | (data[0x13] << 8), h = data[0x14] | (data[0x15] << 8);
            int ob = data[0x30] | (data[0x31] << 8);
            int exits = 0, npcs = 0;
            if (m != LufiaMap.WorldMap && ob > 0 && ob + 8 < data.Length) { exits = data[ob]; npcs = data[ob + 2]; }
            list.Add(new MapInfo(m, res, data[8], w, h, exits, npcs));
        }
        return list;
    }

    /// <summary>Scan and give every map a name (user names win over generated ones).</summary>
    public static List<MapInfo> ScanNamed(RomBuffer rom)
    {
        var list = Scan(rom);
        var generated = MapNames.Generate(rom, list.Select(m => m.MapId).ToList());
        var custom = MapNames.LoadCustom();
        foreach (var m in list)
        {
            if (custom.TryGetValue(m.MapId, out var c) && !string.IsNullOrWhiteSpace(c)) { m.Name = c; m.IsCustomName = true; }
            else m.Name = generated.GetValueOrDefault(m.MapId, "");
        }
        return list;
    }
}
