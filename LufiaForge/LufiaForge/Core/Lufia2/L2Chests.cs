namespace LufiaForge.Core.Lufia2;

// lufia 2 treasure (US rom, read from the code 2026-10-23 and 2026-10-27). $91:B8B5 (file 0x8B8B5): a u16 offset per
// map (from the table) to 3-byte records ended by FF:
// - b0 = treasure spot id (& 1F, the map info's treasure list +26 holds its place) | 20 (left out of the
//   opened-chests list $8E:BCAD builds) | 40 (item + 0x100) | 80 (opened flag + 0x100)
// - b1 = the opened flag (a bit at $7E:093B, 512 of them, $8E:C338 / $8E:C34F)
// - b2 = the item (low byte)
//
// how a chest works: when a map loads the game marks every treasure spot's block (bit 0 of $7E:4000, $80:EE36).
// pressing A facing that block ($83:BEB0 -> $8E:C05F) finds the spot, then that spot's record here. if its flag is
// clear it gives the item ($8E:C1DE: "found ...") and sets the flag, so it's only found once. the spot's 4th byte
// names a record of the map info's list +04 (10 bytes: id, layers, the opened look's source x, y, w, h, the closed
// look's source x, y, w, h, all in blocks of the same map): the blocks copied onto the spot when it opens, and again
// when the map loads while the flag is set ($83:86EA). the closed chest is just painted on the map. a spot whose look
// copies the spot onto itself shows nothing: a hidden treasure, found by pressing A facing that block
//
// the game's lists fill the table up to the next map info block. the offsets get added to $91:B8B5 as 24-bit sums, so
// offsets from 0xC74B reach $92:8000 and on: that's where map 00's (the world map's) info block is. the first time a
// list has to grow we move that block to the added space (any bank works for an info block) and put grown lists
// there. maps 0 and 1 (world maps) have no treasure
public static class L2Chests
{
    public const int Table = 0x8B8B5, TableSnes = 0x91B8B5, FlagCount = 0x200;
    // file offset of $92:8000, the first byte past bank $91 the table's offsets reach
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

    // one record of a map's list, the way the editor keeps it
    public sealed record Rec(int Spot, int Flag, int Item, bool Special)
    {
        public byte[] Bytes => new[] { (byte)(Spot & 0x1F | (Special ? 0x20 : 0) | (Item & 0x100) >> 2 | (Flag & 0x100) >> 1), (byte)Flag, (byte)Item };
        public static Rec From(byte b0, byte b1, byte b2) => new(b0 & 0x1F, b1 | (b0 & 0x80) << 1, b2 | (b0 & 0x40) << 2, (b0 & 0x20) != 0);
    }

    // the u16 offset of a map's list
    public static int Offset(RomBuffer rom, int map) => rom.ReadUInt16Le(Table + map * 2);

    // the file offset the game reads for list offset listOffset (-1 if that's RAM, not rom)
    public static int FileOf(int listOffset)
    {
        int snes = TableSnes + listOffset;
        return (snes & 0x8000) == 0 ? -1 : L2Rom.SnesToFile(snes);
    }

    // the list offset that reaches file offset file (-1 if none does)
    public static int OffsetOf(int file)
    {
        int listOffset = L2Rom.FileToSnes(file) - TableSnes;
        return listOffset is >= 0 and <= 0xFFFF && FileOf(listOffset) == file ? listOffset : -1;
    }

    public static List<Rec> Records(RomBuffer rom, int map)
    {
        var records = new List<Rec>();
        int recordAt = FileOf(Offset(rom, map));
        for (int guard = 0; recordAt >= 0 && guard < 64 && recordAt + 2 < rom.Length && rom.ReadByte(recordAt) != 0xFF; guard++, recordAt += 3)
            records.Add(Rec.From(rom.ReadByte(recordAt), rom.ReadByte(recordAt + 1), rom.ReadByte(recordAt + 2)));
        return records;
    }

    public static List<Chest> Read(RomBuffer rom, int map)
    {
        var list = new List<Chest>();
        if (map < 2) return list;
        var spots = L2MapInfo.Read(rom, map).Where(e => e.Kind == "Treasure").GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        int recordAt = FileOf(Offset(rom, map));
        for (int guard = 0; recordAt >= 0 && guard < 64 && rom.ReadByte(recordAt) != 0xFF; guard++, recordAt += 3)
        {
            var record = Rec.From(rom.ReadByte(recordAt), rom.ReadByte(recordAt + 1), rom.ReadByte(recordAt + 2));
            spots.TryGetValue(record.Spot, out var spot);
            list.Add(new Chest { At = recordAt, Spot = record.Spot, Flag = record.Flag, Special = record.Special, Item = record.Item, X = spot?.X1 ?? -1, Y = spot?.Y1 ?? -1 });
        }
        return list;
    }

    // writes a chest's item (0-0x1FF) in place
    public static void WriteItem(RomBuffer rom, Chest chest)
    {
        int b0 = rom.ReadByte(chest.At);
        rom.WriteByte(chest.At, (byte)(b0 & ~0x40 | (chest.Item & 0x100) >> 2));
        rom.WriteByte(chest.At + 2, (byte)chest.Item);
    }

    // the opened flags every map's treasure uses
    public static HashSet<int> UsedFlags(RomBuffer rom, int exceptMap = -1)
    {
        var used = new HashSet<int>();
        for (int m = 0; m < L2Maps.Count(rom); m++)
            if (m != exceptMap) foreach (var record in Records(rom, m)) used.Add(record.Flag);
        return used;
    }

    // the lowest opened flag no treasure uses (besides alsoUsed), -1 if all 512 are taken
    public static int FreeFlag(RomBuffer rom, IEnumerable<int> alsoUsed, int map)
    {
        var used = UsedFlags(rom, map);
        used.UnionWith(alsoUsed);
        for (int flag = 0; flag < FlagCount; flag++) if (!used.Contains(flag)) return flag;
        return -1;
    }

    // the end of the space after $92:8000 (the next map info block there)
    private static int SpaceEnd(RomBuffer rom)
    {
        int end = Space + 0x8000;
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            int infoBlock = L2MapInfo.Block(rom, m);
            if (infoBlock > Space && infoBlock < end) end = infoBlock;
        }
        return end;
    }

    // moves the world map's info block off $92:8000 (to the added space) so lists can go there. true if it got moved now
    public static bool MakeSpace(RomBuffer rom)
    {
        var mapsThere = Enumerable.Range(0, L2Maps.Count(rom)).Where(m => L2MapInfo.Block(rom, m) == Space).ToList();
        if (mapsThere.Count == 0) return false;
        int size = rom.ReadUInt16Le(Space);
        var data = rom.ReadBytes(Space, size);
        int to = L2Rom.Allocate(rom, size + 1, upper: true);
        rom.WriteBytes(to, data.Append((byte)0).ToArray());
        int snes = L2Rom.FileToSnes(to);
        foreach (int m in mapsThere) rom.WriteBytes(L2MapInfo.PointerAt(rom, m), new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        return true;
    }

    // writes a map's list: over its old bytes if it isn't longer and no other map shares it, else (after MakeSpace)
    // in the space from $92:8000 on
    public static void WriteList(RomBuffer rom, int map, IReadOnlyList<Rec> recs)
    {
        if (map < 2) throw new InvalidOperationException("The world maps have no treasure.");
        if (recs.Count > 32) throw new InvalidOperationException("A map can have at most 32 treasures (spot numbers 00-1F).");
        var bytes = recs.SelectMany(r => r.Bytes).Append((byte)0xFF).ToArray();
        int listOffset = Offset(rom, map), listAt = FileOf(listOffset);
        int oldCount = Records(rom, map).Count;
        bool shared = Enumerable.Range(0, L2Maps.Count(rom)).Any(m => m != map && Offset(rom, m) == listOffset);
        if (listAt >= 0 && !shared && recs.Count <= oldCount)
        {
            rom.WriteBytes(listAt, bytes);
            return;
        }
        MakeSpace(rom);
        int end = SpaceEnd(rom), next = Space;
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            if (m == map) continue;
            int otherAt = FileOf(Offset(rom, m));
            if (otherAt >= Space && otherAt < end) next = Math.Max(next, otherAt + Records(rom, m).Count * 3 + 1);
        }
        if (next + bytes.Length > end) throw new InvalidOperationException("The space for added treasure lists is full.");
        rom.WriteBytes(next, bytes);
        int newOffset = OffsetOf(next);
        if (newOffset < 0) throw new InvalidOperationException("The treasure list can't be reached from the game's table.");
        rom.WriteUInt16Le(Table + map * 2, (ushort)newOffset);
    }
}
