using CommunityToolkit.Mvvm.ComponentModel;

namespace LufiaForge.Core.Maps;

// one entry in the list of maps
public partial class MapInfo : ObservableObject
{
    public int MapId { get; }
    public int ResourceId { get; }
    public int Tileset { get; }
    public int Width { get; }
    public int Height { get; }
    public int ExitCount { get; }
    public int NpcCount { get; }

    // readable name (generated, or typed in by the user)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _name = "";

    // true when the user typed Name in
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

// finds every map in the rom (117 in the US rom)
public static class MapCatalog
{
    public static List<MapInfo> Scan(RomBuffer rom)
    {
        var list = new List<MapInfo>();
        for (int mapId = 0; mapId < 256; mapId++)
        {
            if (!LufiaMap.IsValidMap(rom, mapId, out var data, out _) || data == null) continue;
            int resource = LufiaMap.MapDataResource(rom, mapId);
            int width = data[0x12] | (data[0x13] << 8), height = data[0x14] | (data[0x15] << 8);
            int objects = data[0x30] | (data[0x31] << 8);
            int exits = 0, npcs = 0;
            if (mapId != LufiaMap.WorldMap && objects > 0 && objects + 8 < data.Length) { exits = data[objects]; npcs = data[objects + 2]; }
            list.Add(new MapInfo(mapId, resource, data[8], width, height, exits, npcs));
        }
        return list;
    }

    // scans and gives every map a name (user names win over generated ones)
    public static List<MapInfo> ScanNamed(RomBuffer rom)
    {
        var list = Scan(rom);
        var generated = MapNames.Generate(rom, list.Select(m => m.MapId).ToList());
        var custom = MapNames.LoadCustom();
        foreach (var map in list)
        {
            if (custom.TryGetValue(map.MapId, out var customName) && !string.IsNullOrWhiteSpace(customName)) { map.Name = customName; map.IsCustomName = true; }
            else map.Name = generated.GetValueOrDefault(map.MapId, "");
        }
        return list;
    }
}
