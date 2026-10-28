namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II treasure (US ROM, read from the code 2026-10-23 and 2026-10-27). $91:B8B5 (file 0x8B8B5): a u16 offset per
/// map (from the table) to 3-byte records ended by FF: b0 = treasure spot id (&amp; 1F, the map info's treasure list +26
/// holds its place) | 20 (left out of the opened-chests list $8E:BCAD builds) | 40 (item + 0x100) | 80 (opened flag +
/// 0x100); b1 = the opened flag (a bit at $7E:093B, 512 of them, $8E:C338 / $8E:C34F); b2 = the item (low byte).
///
/// How a chest works: on loading a map the game marks every treasure spot's block (bit 0 of $7E:4000, $80:EE36). Pressing
/// A facing such a block ($83:BEB0 -> $8E:C05F) finds the spot, then the record of that spot here; when its flag is
/// clear it gives the item ($8E:C1DE: "found ...") and sets the flag, so it is found once. The spot's 4th byte names a
/// record of the map info's list +04 (10 bytes: id, layers, the opened look's source x, y, w, h, the closed look's source
/// x, y, w, h, all in blocks of the same map): the blocks copied onto the spot when it is opened, and again on loading
/// the map while the flag is set ($83:86EA). The closed chest is just painted on the map. A spot whose look copies the
/// spot onto itself shows nothing: a hidden treasure, found by pressing A facing that block.
///
/// The lists of the game fill the table up to the next map info block. The offsets are added to $91:B8B5 as 24-bit
/// sums, so offsets from 0xC74B reach $92:8000 on: that is where map 00's (the world map's) info block is; Lufia Forge
/// moves that block to the added space (any bank works for an info block) the first time a list has to grow, and puts
/// grown lists there. Maps 0 and 1 (world maps) have no treasure.
/// </summary>
public static class L2Chests
{
    public const int Table = 0x8B8B5, TableSnes = 0x91B8B5, FlagCount = 0x200;
    /// <summary>File offset of $92:8000, the first byte past bank $91 the table's offsets reach.</summary>
    public const int Space = 0x90000;

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

    /// <summary>One record of a map's list, as the editor keeps it.</summary>
    public sealed record Rec(int Spot, int Flag, int Item, bool Special)
    {
        public byte[] Bytes => new[] { (byte)(Spot & 0x1F | (Special ? 0x20 : 0) | (Item & 0x100) >> 2 | (Flag & 0x100) >> 1), (byte)Flag, (byte)Item };
        public static Rec From(byte b0, byte b1, byte b2) => new(b0 & 0x1F, b1 | (b0 & 0x80) << 1, b2 | (b0 & 0x40) << 2, (b0 & 0x20) != 0);
    }

    /// <summary>The u16 offset of a map's list.</summary>
    public static int Offset(RomBuffer rom, int map) => rom.ReadUInt16Le(Table + map * 2);

    /// <summary>The file offset the game reads for list offset <paramref name="off"/> (-1 when that is RAM, not ROM).</summary>
    public static int FileOf(int off)
    {
        int snes = TableSnes + off;
        return (snes & 0x8000) == 0 ? -1 : L2Rom.SnesToFile(snes);
    }

    /// <summary>The list offset that reaches file offset <paramref name="file"/> (-1 when none does).</summary>
    public static int OffsetOf(int file)
    {
        int off = L2Rom.FileToSnes(file) - TableSnes;
        return off is >= 0 and <= 0xFFFF && FileOf(off) == file ? off : -1;
    }

    public static List<Rec> Records(RomBuffer rom, int map)
    {
        var l = new List<Rec>();
        int p = FileOf(Offset(rom, map));
        for (int guard = 0; p >= 0 && guard < 64 && p + 2 < rom.Length && rom.ReadByte(p) != 0xFF; guard++, p += 3)
            l.Add(Rec.From(rom.ReadByte(p), rom.ReadByte(p + 1), rom.ReadByte(p + 2)));
        return l;
    }

    public static List<Chest> Read(RomBuffer rom, int map)
    {
        var list = new List<Chest>();
        if (map < 2) return list;
        var spots = L2MapInfo.Read(rom, map).Where(e => e.Kind == "Treasure").GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        int p = FileOf(Offset(rom, map));
        for (int guard = 0; p >= 0 && guard < 64 && rom.ReadByte(p) != 0xFF; guard++, p += 3)
        {
            var r = Rec.From(rom.ReadByte(p), rom.ReadByte(p + 1), rom.ReadByte(p + 2));
            spots.TryGetValue(r.Spot, out var s);
            list.Add(new Chest { At = p, Spot = r.Spot, Flag = r.Flag, Special = r.Special, Item = r.Item, X = s?.X1 ?? -1, Y = s?.Y1 ?? -1 });
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

    /// <summary>The opened flags every map's treasure uses.</summary>
    public static HashSet<int> UsedFlags(RomBuffer rom, int exceptMap = -1)
    {
        var used = new HashSet<int>();
        for (int m = 0; m < L2Maps.Count; m++)
            if (m != exceptMap) foreach (var r in Records(rom, m)) used.Add(r.Flag);
        return used;
    }

    /// <summary>The lowest opened flag no treasure uses (besides <paramref name="alsoUsed"/>), or -1 when all 512 are taken.</summary>
    public static int FreeFlag(RomBuffer rom, IEnumerable<int> alsoUsed, int map)
    {
        var used = UsedFlags(rom, map);
        used.UnionWith(alsoUsed);
        for (int f = 0; f < FlagCount; f++) if (!used.Contains(f)) return f;
        return -1;
    }

    /// <summary>The end of the space after $92:8000 (the next map info block there).</summary>
    private static int SpaceEnd(RomBuffer rom)
    {
        int end = Space + 0x8000;
        for (int m = 0; m < L2Maps.Count; m++)
        {
            int b = L2MapInfo.Block(rom, m);
            if (b > Space && b < end) end = b;
        }
        return end;
    }

    /// <summary>Moves the world map's info block off $92:8000 (to the added space) so lists can go there; true when it
    /// was moved now.</summary>
    public static bool MakeSpace(RomBuffer rom)
    {
        var at = Enumerable.Range(0, L2Maps.Count).Where(m => L2MapInfo.Block(rom, m) == Space).ToList();
        if (at.Count == 0) return false;
        int size = rom.ReadUInt16Le(Space);
        var data = rom.ReadBytes(Space, size);
        int to = L2Rom.Allocate(rom, size + 1, upper: true);
        rom.WriteBytes(to, data.Append((byte)0).ToArray());
        int snes = L2Rom.FileToSnes(to);
        foreach (int m in at) rom.WriteBytes(L2MapInfo.Pointers + m * 3, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        return true;
    }

    /// <summary>Writes a map's list: over its old bytes when it isn't longer and no other map shares it, else (after
    /// <see cref="MakeSpace"/>) in the space from $92:8000 on.</summary>
    public static void WriteList(RomBuffer rom, int map, IReadOnlyList<Rec> recs)
    {
        if (map < 2) throw new InvalidOperationException("The world maps have no treasure.");
        if (recs.Count > 32) throw new InvalidOperationException("A map can have at most 32 treasures (spot numbers 00-1F).");
        var bytes = recs.SelectMany(r => r.Bytes).Append((byte)0xFF).ToArray();
        int off = Offset(rom, map), at = FileOf(off);
        int old = Records(rom, map).Count;
        bool shared = Enumerable.Range(0, L2Maps.Count).Any(m => m != map && Offset(rom, m) == off);
        if (at >= 0 && !shared && recs.Count <= old)
        {
            rom.WriteBytes(at, bytes);
            return;
        }
        MakeSpace(rom);
        int end = SpaceEnd(rom), next = Space;
        for (int m = 0; m < L2Maps.Count; m++)
        {
            if (m == map) continue;
            int f = FileOf(Offset(rom, m));
            if (f >= Space && f < end) next = Math.Max(next, f + Records(rom, m).Count * 3 + 1);
        }
        if (next + bytes.Length > end) throw new InvalidOperationException("The space for added treasure lists is full.");
        rom.WriteBytes(next, bytes);
        int no = OffsetOf(next);
        if (no < 0) throw new InvalidOperationException("The treasure list can't be reached from the game's table.");
        rom.WriteUInt16Le(Table + map * 2, (ushort)no);
    }
}
