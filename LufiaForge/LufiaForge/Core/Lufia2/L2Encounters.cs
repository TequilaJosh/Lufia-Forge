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
    /// before the next table ($97:C53D); zone 0 has none.
    ///
    /// More zones: the zone byte is any value 0-255 and the check reads its pairs as $97:C493 + zone * 10 (the multiply
    /// is the SNES's 8 x 8 unit, so up to 2550) with three long loads ($86:9D22 and $86:9D3E: +1, $86:9D59: +0). A zone
    /// past 16 would read the next table, so Lufia Forge moves the table to the added space (room for all 256 zones;
    /// "LF2ZN" and the zone count 16 bytes before it) and points those three loads at it. Nothing else reads it.</summary>
    public const int ZoneTable = 0xBC493, ZoneCount = 17, ZoneSlots = 5, ZoneFile = 0x193, MaxZones = 256;
    private static readonly int[] ZoneSites = { 0x31D22, 0x31D3E, 0x31D59 };
    private static readonly int[] ZoneSiteOffsets = { 1, 1, 0 };
    private const string ZoneMagic = "LF2ZN";

    /// <summary>File offset of the zone table the game reads now (its own, or Lufia Forge's moved one).</summary>
    public static int ZoneTableAt(RomBuffer rom)
    {
        int at = MovedZoneTable(rom);
        return at >= 0 ? at : ZoneTable;
    }

    /// <summary>The moved table (file offset), or -1 when the game reads its own.</summary>
    private static int MovedZoneTable(RomBuffer rom)
    {
        if (rom.Length <= 0x31D5D || rom.ReadByte(ZoneSites[2]) != 0xBF) return -1;
        int snes = (int)rom.ReadUInt24Le(ZoneSites[2] + 1);
        if (snes == 0x97C493) return -1;
        int at = L2Rom.SnesToFile(snes);
        return at >= 0x10 && at + MaxZones * 10 <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(at - 0x10, ZoneMagic.Length)) == ZoneMagic ? at : -1;
    }

    /// <summary>Whether Lufia Forge moved the zone table (so the battle check reads it elsewhere).</summary>
    public static bool ZonesMoved(RomBuffer rom) => MovedZoneTable(rom) >= 0;

    /// <summary>How many zones there are: the game's 17, or as many as were added.</summary>
    public static int ZoneCountIn(RomBuffer rom)
    {
        int at = MovedZoneTable(rom);
        return at < 0 ? ZoneCount : Math.Clamp(rom.ReadByte(at - 0x10 + ZoneMagic.Length) + 1, ZoneCount, MaxZones);
    }

    /// <summary>Why zones can't be added in this ROM, or null.</summary>
    public static string? CantAddZones(RomBuffer rom)
    {
        if (ZonesMoved(rom)) return ZoneCountIn(rom) >= MaxZones ? $"There are {MaxZones} zones already (a zone is one byte on the zone map)." : null;
        var orig = new[] { "BF 94 C4 97", "BF 94 C4 97", "BF 93 C4 97" };
        for (int i = 0; i < 3; i++)
            if (BitConverter.ToString(rom.ReadBytes(ZoneSites[i], 4)).Replace("-", " ") != orig[i])
                return "This ROM's battle check isn't the US game's (another hack changed it), so the zone table can't be moved to make room.";
        return null;
    }

    /// <summary>Adds a zone (no battles yet): the first time, the table moves to the added space. Returns its number.</summary>
    public static int AddZone(RomBuffer rom)
    {
        if (CantAddZones(rom) is { } why) throw new InvalidOperationException(why);
        int at = MovedZoneTable(rom);
        if (at < 0)
        {
            int block = L2Rom.Allocate(rom, 0x10 + MaxZones * 10, upper: true);
            at = block + 0x10;
            rom.WriteBytes(block, new byte[0x10 + MaxZones * 10]);
            rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(ZoneMagic));
            rom.WriteBytes(at, rom.ReadBytes(ZoneTable, ZoneCount * 10));
            rom.WriteByte(block + ZoneMagic.Length, ZoneCount - 1);
            int snes = L2Rom.FileToSnes(at);
            for (int i = 0; i < 3; i++)
            {
                int a = snes + ZoneSiteOffsets[i];
                rom.WriteBytes(ZoneSites[i] + 1, new[] { (byte)a, (byte)(a >> 8), (byte)(a >> 16) });
            }
        }
        int z = ZoneCountIn(rom);
        rom.WriteBytes(at + z * 10, new byte[10]);
        rom.WriteByte(at - 0x10 + ZoneMagic.Length, (byte)z);
        return z;
    }

    public sealed record ZoneSlot(int Group, int Weight);

    public static ZoneSlot[] Zone(RomBuffer rom, int z)
    {
        int t = ZoneTableAt(rom);
        return Enumerable.Range(0, ZoneSlots).Select(i => new ZoneSlot(rom.ReadByte(t + z * 10 + i * 2), rom.ReadByte(t + z * 10 + i * 2 + 1))).ToArray();
    }

    public static void WriteZone(RomBuffer rom, int z, IReadOnlyList<ZoneSlot> slots)
    {
        if (z >= ZoneCountIn(rom)) throw new InvalidOperationException($"Zone {z:X2} doesn't exist (add zones on the Encounters page).");
        int t = ZoneTableAt(rom);
        for (int i = 0; i < ZoneSlots; i++)
        {
            var s = i < slots.Count ? slots[i] : new ZoneSlot(0, 0);
            rom.WriteByte(t + z * 10 + i * 2, (byte)(s.Weight == 0 ? 0 : s.Group));
            rom.WriteByte(t + z * 10 + i * 2 + 1, (byte)Math.Clamp(s.Weight, 0, 255));
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
        for (int z = 1; z < ZoneCountIn(rom); z++)
            foreach (var s in ZoneGroups(rom, z)) if (s.Group == g) zones.Add((z, s.Share));
        var maps = new List<int>();
        for (int m = 0; m < L2Maps.Count; m++)
        {
            var (at, len) = MapList(rom, m);
            for (int i = 0; i < len; i++) if (rom.ReadByte(at + i) == g) { maps.Add(m); break; }
        }
        return (zones, maps);
    }

    // ── story battles ─────────────────────────────────────────────────────────

    /// <summary>The story battles event command 53 xx starts (found 2026-10-27; table $97:C53D, file 0xBC53D, read by
    /// $81:82C6): a u16 offset per battle from the table, the battles 26 bytes each: six monster ids (FF = none), their
    /// places on the screen (x, y each), then the battle's settings (a skipped byte, two words, three bytes; left as they
    /// are). Battles 00-26; some share a record (00, 03, 06... point at the same one).
    /// <para>New battles (<see cref="AddStoryBattle"/>): the battle number is a byte ($7FF8A4), so up to 256. The table
    /// and its records are moved once to a block of Lufia Forge's ("LF2SB", count, then the table with room for 256
    /// offsets, then the records at their new offsets) and the nine reads of $97:C53D in $81:82C6-8343 are pointed there;
    /// battles sharing a record still share it.</para></summary>
    public const int StoryBattles = 0xBC53D, GameStoryBattleCount = 0x27, StorySlots = 6, StoryRecordSize = 26;

    /// <summary>The operands of the nine "LDA $97C53D,X" of the story battle loader.</summary>
    private static readonly int[] StorySites = { 0x82C7, 0x82D3, 0x8303, 0x830B, 0x8320, 0x8329, 0x8334, 0x833C, 0x8344 };
    private const string StoryMagic = "LF2SB";
    private const int StoryBlockSize = 0x2000, StoryHeader = 8, StoryMax = 256;

    /// <summary>File offset of the story battle table: the game's, or the moved one.</summary>
    public static int StoryTable(RomBuffer rom)
    {
        int snes = (int)rom.ReadUInt24Le(StorySites[0]);
        if (snes == 0x97C53D) return StoryBattles;
        int at = L2Rom.SnesToFile(snes);
        return at - StoryHeader >= 0 && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(at - StoryHeader, StoryMagic.Length)) == StoryMagic ? at : StoryBattles;
    }

    /// <summary>How many story battles there are (39 in the game).</summary>
    public static int StoryBattleCount(RomBuffer rom)
    {
        int t = StoryTable(rom);
        return t == StoryBattles ? GameStoryBattleCount : rom.ReadByte(t - StoryHeader + 5) + 1;
    }

    public static int StoryBattleAt(RomBuffer rom, int b) { int t = StoryTable(rom); return t + rom.ReadUInt16Le(t + b * 2); }

    /// <summary>Why no story battle can be added, or null.</summary>
    public static string? CantAddStoryBattle(RomBuffer rom)
    {
        if (StoryTable(rom) != StoryBattles) return StoryBattleCount(rom) >= StoryMax ? "There are 256 story battles, the most the game can number." : null;
        return StorySites.All(a => rom.ReadUInt24Le(a) == 0x97C53D) ? null : "This ROM's story battle code isn't the US game's.";
    }

    /// <summary>Adds a story battle: a copy of battle <paramref name="from"/> (its monsters, places and settings) with a
    /// record of its own. Returns its number (53 xx starts it).</summary>
    public static int AddStoryBattle(RomBuffer rom, int from)
    {
        if (CantAddStoryBattle(rom) is { } why) throw new InvalidOperationException(why);
        int count = StoryBattleCount(rom);
        int t = StoryTable(rom);
        if (t == StoryBattles)
        {
            // move: the table with room for 256, then each record once (sharing kept), at new offsets
            int b = L2Rom.Allocate(rom, StoryBlockSize);
            rom.WriteBytes(b, new byte[StoryBlockSize]);
            rom.WriteBytes(b, System.Text.Encoding.ASCII.GetBytes(StoryMagic));
            int nt = b + StoryHeader, next = StoryMax * 2;
            var placed = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                int old = rom.ReadUInt16Le(StoryBattles + i * 2);
                if (!placed.TryGetValue(old, out int off))
                {
                    off = next; next += StoryRecordSize; placed[old] = off;
                    rom.WriteBytes(nt + off, rom.ReadBytes(StoryBattles + old, StoryRecordSize));
                }
                rom.WriteBytes(nt + i * 2, new[] { (byte)off, (byte)(off >> 8) });
            }
            WriteStoryUsed(rom, nt, count, next);
            int snes = L2Rom.FileToSnes(nt);
            foreach (int a in StorySites) rom.WriteBytes(a, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
            t = nt;
        }
        int used = rom.ReadUInt16Le(t - StoryHeader + 6);
        if (t + used + StoryRecordSize > t - StoryHeader + StoryBlockSize) throw new InvalidOperationException("The story battle block is full.");
        rom.WriteBytes(t + used, rom.ReadBytes(StoryBattleAt(rom, from), StoryRecordSize));
        rom.WriteBytes(t + count * 2, new[] { (byte)used, (byte)(used >> 8) });
        WriteStoryUsed(rom, t, count + 1, used + StoryRecordSize);
        return count;
    }

    private static void WriteStoryUsed(RomBuffer rom, int table, int count, int used)
    {
        rom.WriteByte(table - StoryHeader + 5, (byte)(count - 1));
        rom.WriteBytes(table - StoryHeader + 6, new[] { (byte)used, (byte)(used >> 8) });
    }

    /// <summary>A story battle's six slots: monster (FF = none) and its place.</summary>
    public static (int Monster, int X, int Y)[] StoryBattle(RomBuffer rom, int b)
    {
        int at = StoryBattleAt(rom, b);
        return Enumerable.Range(0, StorySlots).Select(i => ((int)rom.ReadByte(at + i), (int)rom.ReadByte(at + 6 + i * 2), (int)rom.ReadByte(at + 7 + i * 2))).ToArray();
    }

    public static void WriteStoryBattle(RomBuffer rom, int b, IReadOnlyList<(int Monster, int X, int Y)> slots)
    {
        int at = StoryBattleAt(rom, b);
        for (int i = 0; i < StorySlots; i++)
        {
            var (m, x, y) = i < slots.Count ? slots[i] : (0xFF, 0, 0);
            rom.WriteByte(at + i, (byte)m);
            rom.WriteByte(at + 6 + i * 2, (byte)x);
            rom.WriteByte(at + 7 + i * 2, (byte)y);
        }
    }

    /// <summary>Battles that use the same record as battle <paramref name="b"/> (a change there changes them too).</summary>
    public static List<int> StorySharing(RomBuffer rom, int b) =>
        Enumerable.Range(0, StoryBattleCount(rom)).Where(o => o != b && StoryBattleAt(rom, o) == StoryBattleAt(rom, b)).ToList();
}
