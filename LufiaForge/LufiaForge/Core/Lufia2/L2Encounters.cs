namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II encounters (US ROM, found 2026-10-22 from the loader at $81:839F and its caller at $81:8372):
/// 0xD8 monster groups of 8 monster numbers (FF = empty) at $97:BE93 (file 0xBBE93); for each map a list of group
/// numbers, one per monster placed on the map, at $97:B9AC + the map's u16 (file 0xBB9AC; maps without monsters
/// share a list). Edited in place.
/// </summary>
public static class L2Encounters
{
    public const int Groups = 0xBBE93, GroupCount = 0xD8, GroupSize = 8, MapLists = 0xBB9AC;

    /// <summary>How the game reads a group ($81:839F): places 1 and 2 (bytes 0, 1) are swapped at random, the other
    /// places shuffled; the first 3 kinds of monster found (the first from place 1 or 2) are the battle's kinds, and the
    /// number of monsters is random by party size ($85:9EA1: 1, 2, 3, 4, 4 for 0-4 heroes; about 1-2 alone, up to 7).
    /// With place 1 empty and place 2 filled the group is a fixed battle ($81:84CF): place 2's monster, and the other
    /// bytes copied as they are.</summary>
    public static bool IsFixed(int[] group) => group[0] == 0xFF && group[1] != 0xFF;

    public static int[] Group(RomBuffer rom, int g) => Enumerable.Range(0, GroupSize).Select(i => (int)rom.ReadByte(Groups + g * GroupSize + i)).ToArray();

    public static void WriteGroup(RomBuffer rom, int g, int[] monsters)
    {
        for (int i = 0; i < GroupSize; i++) rom.WriteByte(Groups + g * GroupSize + i, (byte)(i < monsters.Length ? monsters[i] : 0xFF));
    }

    /// <summary>Where a map's list starts (file offset) and how long it is (up to the next list).</summary>
    public static (int At, int Length) MapList(RomBuffer rom, int map)
    {
        var starts = Enumerable.Range(0, L2Maps.Count).Select(m => (int)rom.ReadUInt16Le(MapLists + m * 2)).Distinct().OrderBy(v => v).ToList();
        int p = rom.ReadUInt16Le(MapLists + map * 2);
        int next = starts.FirstOrDefault(v => v > p, p);
        int len = next > p ? next - p : 0;
        return (MapLists + p, Math.Min(len, 32));
    }

    /// <summary>Maps that share map <paramref name="map"/>'s list (it changes for all of them).</summary>
    public static List<int> SharingWith(RomBuffer rom, int map)
    {
        int p = rom.ReadUInt16Le(MapLists + map * 2);
        return Enumerable.Range(0, L2Maps.Count).Where(m => m != map && rom.ReadUInt16Le(MapLists + m * 2) == p).ToList();
    }

    // ---------------------------------------------------------------- world map battle zones

    /// <summary>The world map's battle zones (read from the battle check at $86:9CF3, US ROM): the zone map is packed file
    /// 0x193 (64 x 64 bytes, one per 2 x 2 big blocks, copied to $7F:E000); zone z is 5 (group, weight) pairs at
    /// $97:C493 + z * 10 (file 0xBC493). On each battle the game adds up the 5 weights (0 = no battles in the zone) and
    /// picks a group in proportion to its weight; the weights don't change how often battles come. 17 zones (0-16) fit
    /// before the next table ($97:C53D); zone 0 has none.</summary>
    public const int ZoneTable = 0xBC493, ZoneCount = 17, ZoneSlots = 5, ZoneFile = 0x193;

    public sealed record ZoneSlot(int Group, int Weight);

    public static ZoneSlot[] Zone(RomBuffer rom, int z) =>
        Enumerable.Range(0, ZoneSlots).Select(i => new ZoneSlot(rom.ReadByte(ZoneTable + z * 10 + i * 2), rom.ReadByte(ZoneTable + z * 10 + i * 2 + 1))).ToArray();

    public static void WriteZone(RomBuffer rom, int z, IReadOnlyList<ZoneSlot> slots)
    {
        for (int i = 0; i < ZoneSlots; i++)
        {
            var s = i < slots.Count ? slots[i] : new ZoneSlot(0, 0);
            rom.WriteByte(ZoneTable + z * 10 + i * 2, (byte)(s.Weight == 0 ? 0 : s.Group));
            rom.WriteByte(ZoneTable + z * 10 + i * 2 + 1, (byte)Math.Clamp(s.Weight, 0, 255));
        }
    }

    /// <summary>The zone's groups that can come up (weight above 0), with their share of the zone's battles (0-1).</summary>
    public static List<(int Group, int Weight, double Share)> ZoneGroups(RomBuffer rom, int z)
    {
        var slots = Zone(rom, z).Where(s => s.Weight > 0).ToList();
        double sum = slots.Sum(s => s.Weight);
        return slots.Select(s => (s.Group, s.Weight, sum > 0 ? s.Weight / sum : 0)).ToList();
    }

    /// <summary>How many 2 x 2 big-block cells of the zone map each zone has.</summary>
    public static int[] ZoneSizes(byte[] zoneMap)
    {
        var n = new int[256];
        foreach (var b in zoneMap) n[b]++;
        return n;
    }

    /// <summary>Who uses group <paramref name="g"/>: the zones (with their share) and the maps whose field monsters fight it.</summary>
    public static (List<(int Zone, double Share)> Zones, List<int> Maps) UsersOf(RomBuffer rom, int g)
    {
        var zones = new List<(int, double)>();
        for (int z = 1; z < ZoneCount; z++)
            foreach (var s in ZoneGroups(rom, z)) if (s.Group == g) zones.Add((z, s.Share));
        var maps = new List<int>();
        for (int m = 0; m < L2Maps.Count; m++)
        {
            var (at, len) = MapList(rom, m);
            for (int i = 0; i < len; i++) if (rom.ReadByte(at + i) == g) { maps.Add(m); break; }
        }
        return (zones, maps);
    }
}
