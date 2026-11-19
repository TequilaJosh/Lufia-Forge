namespace LufiaForge.Core.Lufia2;

// lufia 2 encounters (US rom, found 2026-10-22 from the loader at $81:839F and its caller at $81:8372): 0xD8 monster
// groups of 8 monster numbers (FF = empty) at $97:BE93 (file 0xBBE93). for each map a list of group numbers, one per
// monster placed on the map, at $97:B9AC + the map's u16 (file 0xBB9AC, maps without monsters share a list).
// edited in place
public static class L2Encounters
{
    public const int Groups = 0xBBE93, GroupCount = 0xD8, GroupSize = 8, MapLists = 0xBB9AC;

    // how the game reads a group ($81:839F): places 1 and 2 (bytes 0, 1) get swapped at random, the other places
    // shuffled. the first 3 kinds of monster found (the first from place 1 or 2) are the battle's kinds, and the number
    // of monsters is random by party size ($85:9EA1: 1, 2, 3, 4, 4 for 0-4 heroes, about 1-2 alone, up to 7).
    // with place 1 empty and place 2 filled the group is a fixed battle ($81:84CF): place 2's monster, and the other
    // bytes copied as they are
    public static bool IsFixed(int[] group) => group[0] == 0xFF && group[1] != 0xFF;

    public static int[] Group(RomBuffer rom, int group) => Enumerable.Range(0, GroupSize).Select(i => (int)rom.ReadByte(Groups + group * GroupSize + i)).ToArray();

    public static void WriteGroup(RomBuffer rom, int group, int[] monsters)
    {
        for (int i = 0; i < GroupSize; i++) rom.WriteByte(Groups + group * GroupSize + i, (byte)(i < monsters.Length ? monsters[i] : 0xFF));
    }

    // file offset of map map's u16 list offset (from MapLists, New map moves the offsets, not the lists)
    public static int IndexAt(RomBuffer rom, int map) => L2NewMaps.Entry(rom, L2MapTable.Encounters, map);

    // where a map's list starts (file offset) and how long it is (up to the next list)
    public static (int At, int Length) MapList(RomBuffer rom, int map)
    {
        var starts = Enumerable.Range(0, L2Maps.Count(rom)).Select(m => (int)rom.ReadUInt16Le(IndexAt(rom, m))).Distinct().OrderBy(v => v).ToList();
        int listOffset = rom.ReadUInt16Le(IndexAt(rom, map));
        int next = starts.FirstOrDefault(v => v > listOffset, listOffset);
        int length = next > listOffset ? next - listOffset : 0;
        return (MapLists + listOffset, Math.Min(length, 32));
    }

    // maps that share map map's list (it changes for all of them)
    public static List<int> SharingWith(RomBuffer rom, int map)
    {
        int listOffset = rom.ReadUInt16Le(IndexAt(rom, map));
        return Enumerable.Range(0, L2Maps.Count(rom)).Where(m => m != map && rom.ReadUInt16Le(IndexAt(rom, m)) == listOffset).ToList();
    }

    // world map battle zones

    // the world map's battle zones (read from the battle check at $86:9CF3, US rom): the zone map is packed file 0x193
    // (64 x 64 bytes, one per 2 x 2 big blocks, copied to $7F:E000). zone z is 5 (group, weight) pairs at $97:C493 +
    // z * 10 (file 0xBC493). on each battle the game adds up the 5 weights (0 = no battles in the zone) and picks a
    // group in proportion to its weight. the weights don't change how often battles come. 17 zones (0-16) fit before
    // the next table ($97:C53D), zone 0 has none
    //
    // more zones: the zone byte can be any value 0-255 and the check reads its pairs as $97:C493 + zone * 10 (the
    // multiply is the SNES's 8 x 8 unit, so up to 2550) with three long loads ($86:9D22 and $86:9D3E: +1, $86:9D59: +0).
    // a zone past 16 would read the next table, so we move the table to the added space (room for all 256 zones,
    // "LF2ZN" and the zone count 16 bytes before it) and point those three loads at it. nothing else reads it
    public const int ZoneTable = 0xBC493, ZoneCount = 17, ZoneSlots = 5, ZoneFile = 0x193, MaxZones = 256;
    private static readonly int[] ZoneSites = { 0x31D22, 0x31D3E, 0x31D59 };
    private static readonly int[] ZoneSiteOffsets = { 1, 1, 0 };
    private const string ZoneMagic = "LF2ZN";

    // file offset of the zone table the game reads now (its own, or our moved one)
    public static int ZoneTableAt(RomBuffer rom)
    {
        int movedAt = MovedZoneTable(rom);
        return movedAt >= 0 ? movedAt : ZoneTable;
    }

    // the moved zone table's block (file offset, its 16-byte head first), or -1
    internal static int ZoneBlock(RomBuffer rom) => MovedZoneTable(rom) is var movedAt and >= 0 ? movedAt - 0x10 : -1;

    // moves the zone table's block to to (room for ZoneBlockSize bytes) and points the battle check at it (L2HighBanks)
    internal static void MoveZoneBlock(RomBuffer rom, int to)
    {
        int block = ZoneBlock(rom);
        if (block < 0) return;
        rom.WriteBytes(to, rom.ReadBytes(block, ZoneBlockSize));
        int snes = L2Rom.FileToSnes(to + 0x10);
        for (int i = 0; i < 3; i++)
        {
            int addr = snes + ZoneSiteOffsets[i];
            rom.WriteBytes(ZoneSites[i] + 1, new[] { (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16) });
        }
    }

    internal const int ZoneBlockSize = 0x10 + MaxZones * 10;

    // the moved table (file offset), -1 if the game reads its own
    private static int MovedZoneTable(RomBuffer rom)
    {
        if (rom.Length <= 0x31D5D || rom.ReadByte(ZoneSites[2]) != 0xBF) return -1;
        int snes = (int)rom.ReadUInt24Le(ZoneSites[2] + 1);
        if (snes == 0x97C493) return -1;
        int tableAt = L2Rom.SnesToFile(snes);
        return tableAt >= 0x10 && tableAt + MaxZones * 10 <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(tableAt - 0x10, ZoneMagic.Length)) == ZoneMagic ? tableAt : -1;
    }

    // whether we moved the zone table (so the battle check reads it somewhere else)
    public static bool ZonesMoved(RomBuffer rom) => MovedZoneTable(rom) >= 0;

    // how many zones there are: the game's 17, or as many as were added
    public static int ZoneCountIn(RomBuffer rom)
    {
        int movedAt = MovedZoneTable(rom);
        return movedAt < 0 ? ZoneCount : Math.Clamp(rom.ReadByte(movedAt - 0x10 + ZoneMagic.Length) + 1, ZoneCount, MaxZones);
    }

    // why zones can't be added in this rom, or null
    public static string? CantAddZones(RomBuffer rom)
    {
        if (ZonesMoved(rom)) return ZoneCountIn(rom) >= MaxZones ? $"There are {MaxZones} zones already (a zone is one byte on the zone map)." : null;
        var original = new[] { "BF 94 C4 97", "BF 94 C4 97", "BF 93 C4 97" };
        for (int i = 0; i < 3; i++)
            if (BitConverter.ToString(rom.ReadBytes(ZoneSites[i], 4)).Replace("-", " ") != original[i])
                return "This ROM's battle check isn't the US game's (another hack changed it), so the zone table can't be moved to make room.";
        return null;
    }

    // adds a zone (no battles yet). the first time, the table moves to the added space. returns its number
    public static int AddZone(RomBuffer rom)
    {
        if (CantAddZones(rom) is { } why) throw new InvalidOperationException(why);
        int tableAt = MovedZoneTable(rom);
        if (tableAt < 0)
        {
            int block = L2Rom.Allocate(rom, 0x10 + MaxZones * 10, upper: true);
            tableAt = block + 0x10;
            rom.WriteBytes(block, new byte[0x10 + MaxZones * 10]);
            rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(ZoneMagic));
            rom.WriteBytes(tableAt, rom.ReadBytes(ZoneTable, ZoneCount * 10));
            rom.WriteByte(block + ZoneMagic.Length, ZoneCount - 1);
            int snes = L2Rom.FileToSnes(tableAt);
            for (int i = 0; i < 3; i++)
            {
                int addr = snes + ZoneSiteOffsets[i];
                rom.WriteBytes(ZoneSites[i] + 1, new[] { (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16) });
            }
        }
        int zone = ZoneCountIn(rom);
        rom.WriteBytes(tableAt + zone * 10, new byte[10]);
        rom.WriteByte(tableAt - 0x10 + ZoneMagic.Length, (byte)zone);
        return zone;
    }

    public sealed record ZoneSlot(int Group, int Weight);

    public static ZoneSlot[] Zone(RomBuffer rom, int zone)
    {
        int table = ZoneTableAt(rom);
        return Enumerable.Range(0, ZoneSlots).Select(i => new ZoneSlot(rom.ReadByte(table + zone * 10 + i * 2), rom.ReadByte(table + zone * 10 + i * 2 + 1))).ToArray();
    }

    public static void WriteZone(RomBuffer rom, int zone, IReadOnlyList<ZoneSlot> slots)
    {
        if (zone >= ZoneCountIn(rom)) throw new InvalidOperationException($"Zone {zone:X2} doesn't exist (add zones on the Encounters page).");
        int table = ZoneTableAt(rom);
        for (int i = 0; i < ZoneSlots; i++)
        {
            var slot = i < slots.Count ? slots[i] : new ZoneSlot(0, 0);
            rom.WriteByte(table + zone * 10 + i * 2, (byte)(slot.Weight == 0 ? 0 : slot.Group));
            rom.WriteByte(table + zone * 10 + i * 2 + 1, (byte)Math.Clamp(slot.Weight, 0, 255));
        }
    }

    // the zone's groups that can come up (weight above 0), with their share of the zone's battles (0-1)
    public static List<(int Group, int Weight, double Share)> ZoneGroups(RomBuffer rom, int zone)
    {
        var slots = Zone(rom, zone).Where(s => s.Weight > 0).ToList();
        double sum = slots.Sum(s => s.Weight);
        return slots.Select(s => (s.Group, s.Weight, sum > 0 ? s.Weight / sum : 0)).ToList();
    }

    // how many 2 x 2 big-block cells of the zone map each zone has
    public static int[] ZoneSizes(byte[] zoneMap)
    {
        var sizes = new int[256];
        foreach (var zone in zoneMap) sizes[zone]++;
        return sizes;
    }

    // who uses group group: the zones (with their share) and the maps whose field monsters fight it
    public static (List<(int Zone, double Share)> Zones, List<int> Maps) UsersOf(RomBuffer rom, int group)
    {
        var zones = new List<(int, double)>();
        for (int zone = 1; zone < ZoneCountIn(rom); zone++)
            foreach (var slot in ZoneGroups(rom, zone)) if (slot.Group == group) zones.Add((zone, slot.Share));
        var maps = new List<int>();
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            var (listAt, length) = MapList(rom, m);
            for (int i = 0; i < length; i++) if (rom.ReadByte(listAt + i) == group) { maps.Add(m); break; }
        }
        return (zones, maps);
    }

    // story battles

    // the story battles event command 53 xx starts (found 2026-10-27, table $97:C53D, file 0xBC53D, read by $81:82C6):
    // a u16 offset per battle from the table, the battles 26 bytes each: six monster ids (FF = none), their places on
    // the screen (x, y each), then the battle's settings (a skipped byte, two words, three bytes, left as they are).
    // battles 00-26, some share a record (00, 03, 06... point at the same one)
    // new battles (AddStoryBattle): the battle number is a byte ($7FF8A4), so up to 256. the table and its records get
    // moved once to a block of ours ("LF2SB", count, then the table with room for 256 offsets, then the records at
    // their new offsets) and the nine reads of $97:C53D in $81:82C6-8343 get pointed there. battles sharing a record
    // still share it
    public const int StoryBattles = 0xBC53D, GameStoryBattleCount = 0x27, StorySlots = 6, StoryRecordSize = 26;

    // the operands of the nine "LDA $97C53D,X" of the story battle loader
    private static readonly int[] StorySites = { 0x82C7, 0x82D3, 0x8303, 0x830B, 0x8320, 0x8329, 0x8334, 0x833C, 0x8344 };
    private const string StoryMagic = "LF2SB";
    private const int StoryBlockSize = 0x2000, StoryHeader = 8, StoryMax = 256;

    // file offset of the story battle table: the game's, or the moved one
    public static int StoryTable(RomBuffer rom)
    {
        int snes = (int)rom.ReadUInt24Le(StorySites[0]);
        if (snes == 0x97C53D) return StoryBattles;
        int tableAt = L2Rom.SnesToFile(snes);
        return tableAt - StoryHeader >= 0 && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(tableAt - StoryHeader, StoryMagic.Length)) == StoryMagic ? tableAt : StoryBattles;
    }

    // how many story battles there are (39 in the game)
    public static int StoryBattleCount(RomBuffer rom)
    {
        int table = StoryTable(rom);
        return table == StoryBattles ? GameStoryBattleCount : rom.ReadByte(table - StoryHeader + 5) + 1;
    }

    public static int StoryBattleAt(RomBuffer rom, int battle) { int table = StoryTable(rom); return table + rom.ReadUInt16Le(table + battle * 2); }

    // why no story battle can be added, or null
    public static string? CantAddStoryBattle(RomBuffer rom)
    {
        if (StoryTable(rom) != StoryBattles) return StoryBattleCount(rom) >= StoryMax ? "There are 256 story battles, the most the game can number." : null;
        return StorySites.All(a => rom.ReadUInt24Le(a) == 0x97C53D) ? null : "This ROM's story battle code isn't the US game's.";
    }

    // adds a story battle: a copy of battle from (its monsters, places and settings) with a record of its own.
    // returns its number (53 xx starts it)
    public static int AddStoryBattle(RomBuffer rom, int from)
    {
        if (CantAddStoryBattle(rom) is { } why) throw new InvalidOperationException(why);
        int count = StoryBattleCount(rom);
        int table = StoryTable(rom);
        if (table == StoryBattles)
        {
            // move: the table with room for 256, then each record once (sharing kept), at new offsets
            int block = L2Rom.Allocate(rom, StoryBlockSize);
            rom.WriteBytes(block, new byte[StoryBlockSize]);
            rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(StoryMagic));
            int newTable = block + StoryHeader, next = StoryMax * 2;
            var placed = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                int oldOffset = rom.ReadUInt16Le(StoryBattles + i * 2);
                if (!placed.TryGetValue(oldOffset, out int newOffset))
                {
                    newOffset = next; next += StoryRecordSize; placed[oldOffset] = newOffset;
                    rom.WriteBytes(newTable + newOffset, rom.ReadBytes(StoryBattles + oldOffset, StoryRecordSize));
                }
                rom.WriteBytes(newTable + i * 2, new[] { (byte)newOffset, (byte)(newOffset >> 8) });
            }
            WriteStoryUsed(rom, newTable, count, next);
            int snes = L2Rom.FileToSnes(newTable);
            foreach (int site in StorySites) rom.WriteBytes(site, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
            table = newTable;
        }
        int used = rom.ReadUInt16Le(table - StoryHeader + 6);
        if (table + used + StoryRecordSize > table - StoryHeader + StoryBlockSize) throw new InvalidOperationException("The story battle block is full.");
        rom.WriteBytes(table + used, rom.ReadBytes(StoryBattleAt(rom, from), StoryRecordSize));
        rom.WriteBytes(table + count * 2, new[] { (byte)used, (byte)(used >> 8) });
        WriteStoryUsed(rom, table, count + 1, used + StoryRecordSize);
        return count;
    }

    private static void WriteStoryUsed(RomBuffer rom, int table, int count, int used)
    {
        rom.WriteByte(table - StoryHeader + 5, (byte)(count - 1));
        rom.WriteBytes(table - StoryHeader + 6, new[] { (byte)used, (byte)(used >> 8) });
    }

    // a story battle's six slots: monster (FF = none) and its place
    public static (int Monster, int X, int Y)[] StoryBattle(RomBuffer rom, int battle)
    {
        int recordAt = StoryBattleAt(rom, battle);
        return Enumerable.Range(0, StorySlots).Select(i => ((int)rom.ReadByte(recordAt + i), (int)rom.ReadByte(recordAt + 6 + i * 2), (int)rom.ReadByte(recordAt + 7 + i * 2))).ToArray();
    }

    public static void WriteStoryBattle(RomBuffer rom, int battle, IReadOnlyList<(int Monster, int X, int Y)> slots)
    {
        int recordAt = StoryBattleAt(rom, battle);
        for (int i = 0; i < StorySlots; i++)
        {
            var (monster, x, y) = i < slots.Count ? slots[i] : (0xFF, 0, 0);
            rom.WriteByte(recordAt + i, (byte)monster);
            rom.WriteByte(recordAt + 6 + i * 2, (byte)x);
            rom.WriteByte(recordAt + 7 + i * 2, (byte)y);
        }
    }

    // battles that use the same record as battle battle (a change there changes them too)
    public static List<int> StorySharing(RomBuffer rom, int battle) =>
        Enumerable.Range(0, StoryBattleCount(rom)).Where(other => other != battle && StoryBattleAt(rom, other) == StoryBattleAt(rom, battle)).ToList();
}
