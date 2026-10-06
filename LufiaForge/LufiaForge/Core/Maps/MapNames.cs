using System.IO;
using System.Text.Json;

namespace LufiaForge.Core.Maps;

/// <summary>
/// Readable names for maps. The game only has name text for towns, so names are built from:
/// <list type="bullet">
/// <item>the map → town table at $01:E93E (values 01-1E are towns, see <see cref="Lufia1Constants.TownNames"/>),</item>
/// <item>the map's tileset, which tells what kind of place it is (castle, cave, tower, shrine…),</item>
/// <item>exits: maps that connect to each other (other than towns) form one place, named after where it is
///   entered — a world map entrance (“Cave near Treck”, nearest town entrance) or a town (“Medan Castle”).</item>
/// </list>
/// Names the user types are kept in %AppData%\LufiaForge\mapnames.json and win over generated ones.
/// </summary>
public static class MapNames
{
    public const int TownTable = 0xE93E;
    public const int PrologueFortress = 0x97;

    /// <summary>What kind of place a tileset shows (checked against renders of every map).</summary>
    public static string PlaceType(int tileset) => tileset switch
    {
        0 or 1 => "Town",
        2 => "Village",
        3 => "Castle",
        4 => "Palace",
        5 => "Shrine",
        6 => "Cave",
        7 => "Tower",
        8 => "Island",
        9 => "Ruins",
        10 => "Fortress",
        _ => "Place",
    };

    public static Dictionary<int, string> Generate(RomBuffer rom, IReadOnlyList<int> mapIds)
    {
        var names = new Dictionary<int, string>();
        var exits = new Dictionary<int, List<MapExit>>();
        var tileset = new Dictionary<int, int>();
        foreach (int id in mapIds)
        {
            try { exits[id] = LufiaMap.ReadExits(rom, id); } catch { exits[id] = new(); }
            tileset[id] = LufiaMap.IsValidMap(rom, id, out var d, out _) && d != null ? d[8] : -1;
        }
        int Code(int id) => rom.ReadByte(TownTable + id);
        bool IsTown(int id) => Code(id) is >= 1 and < 0x80 && Lufia1Constants.TownNames.ContainsKey((byte)Code(id));
        string TownOf(int id) => Lufia1Constants.TownNames[(byte)Code(id)];

        names[LufiaMap.WorldMap] = "World map";

        // towns (a town split over several maps gets numbered)
        foreach (var g in mapIds.Where(IsTown).GroupBy(Code))
        {
            var ids = g.OrderBy(i => i).ToList();
            for (int i = 0; i < ids.Count; i++) names[ids[i]] = ids.Count == 1 ? TownOf(ids[i]) : $"{TownOf(ids[i])} ({i + 1})";
        }

        // links between maps (undirected for grouping, directed for entrances)
        var places = mapIds.Where(id => id != LufiaMap.WorldMap && !IsTown(id)).ToHashSet();
        var neighbours = places.ToDictionary(id => id, _ => new HashSet<int>());
        var entrances = places.ToDictionary(id => id, _ => new List<(int from, MapExit exit)>());
        foreach (var (src, list) in exits)
            foreach (var e in list.Where(e => !e.IsUnused))
            {
                int dest = e.DestMap == 0 ? src : e.DestMap;
                if (dest == src || !places.Contains(dest)) continue;
                if (places.Contains(src))
                {
                    // only maps of the same kind form one place (a castle's cellar cave is its own place)
                    if (PlaceType(tileset.GetValueOrDefault(src, -1)) == PlaceType(tileset.GetValueOrDefault(dest, -1)))
                    { neighbours[src].Add(dest); neighbours[dest].Add(src); }
                    else entrances[dest].Add((src, e));
                }
                else entrances[dest].Add((src, e));
            }

        // maps sharing a dungeon code (80+) in the town table are floors of one place
        foreach (var g in places.Where(id => Code(id) >= 0x80).GroupBy(Code))
        {
            var ids = g.ToList();
            for (int i = 1; i < ids.Count; i++) { neighbours[ids[0]].Add(ids[i]); neighbours[ids[i]].Add(ids[0]); }
        }

        // world positions of town entrances, for "near <town>"
        var worldExits = exits.GetValueOrDefault(LufiaMap.WorldMap) ?? new();
        var townSpots = worldExits.Where(e => !e.IsUnused && mapIds.Contains(e.DestMap) && IsTown(e.DestMap))
            .Select(e => (x: (e.X1 + e.X2) / 2.0, y: (e.Y1 + e.Y2) / 2.0, town: TownOf(e.DestMap))).ToList();
        string? NearestTown(MapExit e)
        {
            if (townSpots.Count == 0) return null;
            double cx = (e.X1 + e.X2) / 2.0, cy = (e.Y1 + e.Y2) / 2.0;
            return townSpots.OrderBy(t => (t.x - cx) * (t.x - cx) + (t.y - cy) * (t.y - cy)).First().town;
        }
        string TypeOf(int id) => PlaceType(tileset.GetValueOrDefault(id, -1));

        // connected groups of non-town maps = one place each
        var seen = new HashSet<int>();
        var groups = new List<List<int>>();
        foreach (int start in places.OrderBy(i => i))
        {
            if (seen.Contains(start)) continue;
            var comp = new List<int>();
            var q = new Queue<int>(); q.Enqueue(start); seen.Add(start);
            while (q.Count > 0)
            {
                int m = q.Dequeue(); comp.Add(m);
                foreach (int n in neighbours[m].OrderBy(i => i)) if (seen.Add(n)) q.Enqueue(n);
            }
            groups.Add(comp);
        }

        var placeNames = new List<(List<int> maps, string name)>();
        foreach (var comp in groups)
        {
            // entry map: one entered from the world map, else from a town, else the lowest number
            var fromWorld = comp.SelectMany(m => entrances[m].Where(x => x.from == LufiaMap.WorldMap).Select(x => (m, x.exit))).FirstOrDefault();
            var fromTown = comp.SelectMany(m => entrances[m].Where(x => x.from != LufiaMap.WorldMap && IsTown(x.from)).Select(x => (m, x.from))).FirstOrDefault();
            var fromPlace = comp.SelectMany(m => entrances[m].Where(x => x.from != LufiaMap.WorldMap && !IsTown(x.from)).Select(x => (m, x.from))).FirstOrDefault();
            int entry; string name;
            if (fromWorld.exit != null)
            {
                entry = fromWorld.m;
                string? near = NearestTown(fromWorld.exit);
                name = near != null ? $"{TypeOf(entry)} near {near}" : TypeOf(entry);
            }
            else if (fromTown.from != 0)
            {
                entry = fromTown.m;
                name = $"{names.GetValueOrDefault(fromTown.from, $"map {fromTown.from:X2}")} {TypeOf(entry)}";
            }
            else if (fromPlace.from != 0)
            {
                // entered from another (non-town) place: resolved after that place has a name
                entry = fromPlace.m;
                name = $"@{fromPlace.from}|{TypeOf(entry)}";
            }
            else
            {
                entry = comp.Min();
                name = TypeOf(entry) switch
                {
                    "Fortress" => "Fortress of Doom",
                    "Island" => "Opening scene",
                    var t => $"Unconnected {t.ToLowerInvariant()}",
                };
            }
            if (TypeOf(entry) == "Fortress")
                name = "Fortress of Doom" + (comp.Contains(PrologueFortress) ? " (prologue)" : " (finale)");

            // order the parts by distance from the entry
            var order = new List<int>();
            var q = new Queue<int>(); var s2 = new HashSet<int> { entry }; q.Enqueue(entry);
            while (q.Count > 0)
            {
                int m = q.Dequeue(); order.Add(m);
                foreach (int n in neighbours[m].OrderBy(i => i)) if (s2.Add(n)) q.Enqueue(n);
            }
            order.AddRange(comp.Where(m => !s2.Contains(m)).OrderBy(i => i));
            placeNames.Add((order, name));
        }

        // places entered from another place: "<that place> – <kind>" (a few passes for chains)
        for (int pass = 0; pass < 6; pass++)
            for (int i = 0; i < placeNames.Count; i++)
            {
                var (maps, name) = placeNames[i];
                if (!name.StartsWith('@')) continue;
                var parts = name[1..].Split('|');
                int from = int.Parse(parts[0]);
                var parent = placeNames.FirstOrDefault(p => p.maps.Contains(from));
                if (parent.name == null || parent.name.StartsWith('@')) continue;
                placeNames[i] = (maps, $"{parent.name} – {parts[1].ToLowerInvariant()}");
            }
        for (int i = 0; i < placeNames.Count; i++)
            if (placeNames[i].name.StartsWith('@')) placeNames[i] = (placeNames[i].maps, "Connected " + placeNames[i].name.Split('|')[1].ToLowerInvariant());

        // places with the same name get numbered, then parts within a place
        foreach (var same in placeNames.GroupBy(p => p.name))
        {
            int k = 1;
            foreach (var (maps, name) in same.OrderBy(p => p.maps.Min()))
            {
                string n = same.Count() > 1 ? $"{name} {k++}" : name;
                for (int i = 0; i < maps.Count; i++) names[maps[i]] = maps.Count == 1 ? n : $"{n} – part {i + 1}";
            }
        }
        return names;
    }

    // ── user names ──────────────────────────────────────────────────────────

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "mapnames.json");

    public static Dictionary<int, string> LoadCustom()
    {
        try
        {
            if (!File.Exists(StorePath)) return new();
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? new();
            return raw.Where(kv => int.TryParse(kv.Key, System.Globalization.NumberStyles.HexNumber, null, out _))
                      .ToDictionary(kv => int.Parse(kv.Key, System.Globalization.NumberStyles.HexNumber), kv => kv.Value);
        }
        catch { return new(); }
    }

    public static void SaveCustom(Dictionary<int, string> names)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var raw = names.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key.ToString("X2"), kv => kv.Value.Trim());
            File.WriteAllText(StorePath, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not fatal: names are a convenience */ }
    }
}
