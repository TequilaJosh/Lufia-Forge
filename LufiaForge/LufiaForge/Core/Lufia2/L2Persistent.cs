namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II's persistent characters: actors 10-4F (Tia 10 in Elcid, the elder of Chaed 2D, ...). No map script makes
/// them; the game keeps where each one is in WRAM ($085E + n: map, $089E + n: spot, $08DE + n: sprite, $081E + n: FF =
/// none) and puts it on that map at the map's character start with that number (map info +10; Dragon Mountain C7's
/// start 2 is where the elder stands). A new game fills those from a table in bank $87 ($83:ADDF: the word at $87:8003
/// points at it from $87:8000; 64 records of 4 bytes: state (FF = none), sprite, map, spot). Event command 35 id map
/// spot sends one to another map ($80:B2B1; it is hidden at once when it is on the map shown).
/// </summary>
public static class L2Persistent
{
    public const int First = 0x10, Count = 0x40;

    /// <summary>A place a persistent character stands: on <see cref="Map"/> at character start <see cref="Spot"/>;
    /// <see cref="From"/> is -1 for where a new game puts it, else the map whose event sends it there.</summary>
    public sealed record Place(int Id, int Sprite, int Map, int Spot, int From);

    /// <summary>File offset of the new-game table.</summary>
    public static int Table(RomBuffer rom) => L2Events.HeaderTable + rom.ReadUInt16Le(L2Events.HeaderTable + 3);

    /// <summary>Where a new game puts each persistent character (id, sprite, map, spot).</summary>
    public static List<Place> Start(RomBuffer rom)
    {
        var list = new List<Place>();
        int t = Table(rom);
        if (t < 0 || t + Count * 4 > rom.Length) return list;
        for (int i = 0; i < Count; i++)
        {
            var e = rom.ReadBytes(t + i * 4, 4);
            if (e[0] != 0xFF) list.Add(new Place(First + i, e[1], e[2], e[3], -1));
        }
        return list;
    }

    /// <summary>Every place a persistent character stands: where a new game puts it and every map an event (35) sends
    /// it to, with its sprite from the table. Reads every map's scripts, so it is kept per ROM until it changes.</summary>
    public static List<Place> All(RomBuffer rom)
    {
        var start = Start(rom);
        var sprite = start.ToDictionary(p => p.Id, p => p.Sprite);
        var list = new List<Place>(start);
        for (int m = 0; m < L2Maps.Count; m++)
        {
            List<(string, List<L2Script.Op>)> scripts;
            try { scripts = L2Script.MapScripts(rom, m); } catch (Exception) { continue; }
            foreach (var (_, ops) in scripts)
                foreach (var o in ops)
                    if (!o.IsText && o.Length == 4 && rom.ReadByte(o.At) == 0x35 && rom.ReadByte(o.At + 1) is var id && sprite.TryGetValue(id, out int s))
                    {
                        var p = new Place(id, s, rom.ReadByte(o.At + 2), rom.ReadByte(o.At + 3), m);
                        if (!list.Any(x => x.Id == p.Id && x.Map == p.Map && x.Spot == p.Spot)) list.Add(p);
                    }
        }
        return list;
    }

    /// <summary>The persistent characters that stand on <paramref name="map"/> at some point, by character start.</summary>
    public static List<Place> On(IEnumerable<Place> all, int map) => all.Where(p => p.Map == map).ToList();
}
