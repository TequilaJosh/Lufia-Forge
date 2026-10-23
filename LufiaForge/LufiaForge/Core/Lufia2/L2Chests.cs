namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II treasure chests (US ROM, read from the code 2026-10-23). $91:B8B5 (file 0x8B8B5): a u16 offset per map
/// (from the table) to 3-byte records ended by FF: b0 = treasure spot id (&amp; 1F, the map info's treasure list +0x26
/// holds its place) | 20 (left out of the opened-chests list $8E:BCAD builds: probably not a plain chest) | 40 (item +
/// 0x100, $8E:C36D) | 80 (opened flag + 0x100); b1 = the opened flag (a bit at $7E:093B, $8E:C338); b2 = the item
/// (low byte). On load the map redraws the chests whose flag is set ($83:86EA). Maps 0 and 1 (world maps) have none.
/// </summary>
public static class L2Chests
{
    public const int Table = 0x8B8B5;

    public sealed class Chest
    {
        public int At { get; init; }
        public int Spot { get; init; }
        public int Flag { get; init; }
        public bool Special { get; init; }
        public int Item { get; set; }
        public int X { get; init; } = -1;
        public int Y { get; init; } = -1;
    }

    public static List<Chest> Read(RomBuffer rom, int map)
    {
        var list = new List<Chest>();
        if (map < 2) return list;
        var spots = L2MapInfo.Read(rom, map).Where(e => e.Kind == "Treasure").GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        int p = Table + rom.ReadUInt16Le(Table + map * 2);
        for (int guard = 0; guard < 64 && rom.ReadByte(p) != 0xFF; guard++, p += 3)
        {
            int b0 = rom.ReadByte(p), b1 = rom.ReadByte(p + 1), b2 = rom.ReadByte(p + 2);
            spots.TryGetValue(b0 & 0x1F, out var s);
            list.Add(new Chest
            {
                At = p, Spot = b0 & 0x1F, Flag = b1 | (b0 & 0x80) << 1, Special = (b0 & 0x20) != 0,
                Item = b2 | (b0 & 0x40) << 2, X = s?.X1 ?? -1, Y = s?.Y1 ?? -1,
            });
        }
        return list;
    }

    /// <summary>Writes a chest's item (0-0x1FF) in place.</summary>
    public static void WriteItem(RomBuffer rom, Chest c)
    {
        int b0 = rom.ReadByte(c.At);
        rom.WriteByte(c.At, (byte)(b0 & ~0x40 | (c.Item & 0x100) >> 2));
        rom.WriteByte(c.At + 2, (byte)c.Item);
    }
}
