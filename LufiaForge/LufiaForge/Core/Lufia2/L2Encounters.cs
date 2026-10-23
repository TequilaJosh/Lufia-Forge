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
}
