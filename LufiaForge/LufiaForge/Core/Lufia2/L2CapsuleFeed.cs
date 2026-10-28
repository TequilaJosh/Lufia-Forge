namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Feeding Lufia II's capsule monsters (US ROM, Capsule > Feed; code $82:C683, read 2026-10-27, checked in the game).
/// <list type="bullet">
/// <item>Feeding fills a meter of 32 points ($7E:11C2 + monster); full, the monster moves up a form. It isn't EXP (the
/// capsule's level comes from battles); it is what feeding gives.</item>
/// <item>An item counts by its price: under 200 (<see cref="MinPriceAt"/>) never; else its tier is the first of 15
/// prices ($8E:E466, <see cref="Thresholds"/>) it is at or under (tier 15 above them all). The points are
/// <see cref="Gains"/>[(form - 1) * 4 + tier / 4] ($8E:E48A).</item>
/// <item>Each monster has a pickiness ($7E:11A6 + monster): an item whose tier is below it is refused (the pickiness then
/// drops by one); an item at or above it is eaten and the pickiness becomes the item's tier. After moving up a form,
/// or eating its own fruit, the pickiness is <see cref="StartPickiness"/>[highest form] ($8E:E485).</item>
/// <item>The item it asks for ("it wants ...") is drawn at random from the list of its pickiness tier
/// (<see cref="ListsAt"/>: 16 x u16 offset, u16 count, then the item ids; $95:FF16 in the US game, read by the long
/// addresses at $82:C55A, $82:C55F, $82:C572). Eating it multiplies the points (x3 in the game, the code at $82:CC24)
/// and a new item is drawn.</item>
/// <item>Fruits: its own fruit gives 1 point and resets the pickiness, the Magic fruit (item 0x35) gives 8. A third
/// fruit, fed in a certain form, turns it into its 5th form; its own fruit then turns it back.</item>
/// <item>Only the highest form reached, up to form 3, can be fed for points (form 4 and 5 can't, $82:CC3C).</item>
/// <item>One feeding gives at most 16 points (<see cref="MaxPerFeeding"/>): a wished tier-12 item in form 1 (8 x 3)
/// gives 16.</item>
/// </list>
/// </summary>
public static class L2CapsuleFeed
{
    public const int MeterFull = 0x20, Tiers = 16, ThresholdCount = 15, FeedForms = 3, Groups = 4;
    public const int Thresholds = 0x76466, StartPickiness = 0x76485, Gains = 0x7648A;
    public const int Form5Fruit = 0x7649A, OwnFruit = 0x764A8, FruitForm = 0x764B6;
    public const int MinPriceAt = 0x146D9, OwnFruitPointsAt = 0x14BDF, MagicFruitPointsAt = 0x14BEB, MagicFruitAt = 0x1464A;
    /// <summary>The code that multiplies a wished item's points ($82:CC24, 5 bytes).</summary>
    public const int WishBonusAt = 0x14C24;
    /// <summary>The operands of CMP #$20 and LDA #$20 at $82:CB1D / CB21 (the longest sparkle animation, 2 steps a point).</summary>
    public const int MaxStepsAt = 0x14B1E;
    /// <summary>The three long addresses of the wish lists (offset, count, item) in the code at $82:C55A.</summary>
    public static readonly int[] ListRefs = { 0x1455B, 0x14560, 0x14573 };
    public const int OriginalLists = 0xAFF16, OriginalListsSize = 0xD8;

    /// <summary>The wish bonus code for x1 to x4 (the game's is x3: ASL, CLC, ADC $154F).</summary>
    private static readonly byte[][] BonusCode =
    {
        new byte[] { 0xEA, 0xEA, 0xEA, 0xEA, 0xEA },
        new byte[] { 0x0A, 0xEA, 0xEA, 0xEA, 0xEA },
        new byte[] { 0x0A, 0x18, 0x6D, 0x4F, 0x15 },
        new byte[] { 0x0A, 0x0A, 0xEA, 0xEA, 0xEA },
    };

    /// <summary>Why feeding can't be edited in this ROM (its code isn't the US game's), or null.</summary>
    public static string? Unsupported(RomBuffer rom)
    {
        if (rom.Length < 0x80000) return "The ROM is too small.";
        bool ok = rom.ReadByte(ListRefs[0] - 1) == 0xBF && rom.ReadByte(ListRefs[1] - 1) == 0xBF && rom.ReadByte(ListRefs[2] - 1) == 0xBF &&
                  rom.ReadByte(MinPriceAt - 1) == 0xC9 && rom.ReadByte(OwnFruitPointsAt - 1) == 0xA9 && rom.ReadByte(MagicFruitPointsAt - 1) == 0xA9 &&
                  rom.ReadByte(MagicFruitAt - 1) == 0xA9 && WishBonus(rom) > 0 &&
                  rom.ReadByte(MaxStepsAt - 1) == 0xC9 && rom.ReadByte(MaxStepsAt + 3) == 0xA9 && rom.ReadByte(MaxStepsAt) == rom.ReadByte(MaxStepsAt + 4) &&
                  rom.ReadUInt24Le(ListRefs[1]) == rom.ReadUInt24Le(ListRefs[0]) + 2 && rom.ReadUInt24Le(ListRefs[2]) == rom.ReadUInt24Le(ListRefs[0]);
        return ok ? null : "This ROM's capsule feeding code isn't the US game's (another hack changed it), so it is shown but not edited.";
    }

    // ---------------------------------------------------------------- wish lists

    /// <summary>File offset of the wish lists' table.</summary>
    public static int ListsAt(RomBuffer rom) => L2Rom.SnesToFile((int)rom.ReadUInt24Le(ListRefs[0]));

    /// <summary>The 16 lists of items a monster asks for, by its pickiness tier.</summary>
    public static List<int>[] ReadLists(RomBuffer rom)
    {
        int t = ListsAt(rom);
        return Enumerable.Range(0, Tiers).Select(i =>
        {
            int o = rom.ReadUInt16Le(t + i * 4), n = rom.ReadUInt16Le(t + i * 4 + 2);
            return Enumerable.Range(0, Math.Min(n, 255)).Select(k => rom.ReadUInt16Le(t + o + k * 2) & 0x1FF).ToList();
        }).ToArray();
    }

    /// <summary>Bytes the table and its lists take.</summary>
    public static int ListsSize(IEnumerable<IReadOnlyCollection<int>> lists) => Tiers * 4 + lists.Sum(l => l.Count) * 2;

    /// <summary>How many items fit where the game keeps the lists (more go to the added space).</summary>
    public const int OriginalCapacity = (OriginalListsSize - Tiers * 4) / 2;

    /// <summary>Writes the 16 lists (up to 255 items each): where the game keeps them when they fit (76 items), else in
    /// the added space, the code's three addresses pointing there. An empty list points at the next list that has items
    /// (the game then asks for that list's first item, as it does with its own empty lists). Returns the file offset.</summary>
    public static int WriteLists(RomBuffer rom, IReadOnlyList<IReadOnlyList<int>> lists)
    {
        if (lists.Count != Tiers) throw new ArgumentException("16 lists are needed.");
        if (lists.Any(l => l.Count > 255)) throw new ArgumentException("A list holds up to 255 items.");
        int size = ListsSize(lists.Select(l => (IReadOnlyCollection<int>)l.ToList()));
        int at = ListsAt(rom);
        int room = at == OriginalLists ? OriginalListsSize : Room(rom, at);
        if (size <= OriginalListsSize) at = OriginalLists;
        else if (at == OriginalLists || size > room)
        {
            // the added space: "LFWL", u16 room, then the table; with room for 64 more items so adding one by one
            // doesn't take new space each time
            int cap = Math.Min(size + 128, Tiers * 4 + 255 * 2 * Tiers);
            int block = L2Rom.Allocate(rom, cap + 6);
            rom.WriteBytes(block, new[] { (byte)'L', (byte)'F', (byte)'W', (byte)'L', (byte)cap, (byte)(cap >> 8) });
            at = block + 6;
        }
        var data = new byte[size];
        int p = Tiers * 4;
        var start = new int[Tiers];
        for (int i = 0; i < Tiers; i++) { start[i] = p; foreach (int item in lists[i]) { data[p] = (byte)item; data[p + 1] = (byte)(item >> 8); p += 2; } }
        for (int i = 0; i < Tiers; i++)
        {
            int o = start[i];
            if (lists[i].Count == 0)
            {
                int j = Enumerable.Range(i + 1, Tiers - i - 1).Concat(Enumerable.Range(0, i).Reverse()).FirstOrDefault(k => lists[k].Count > 0, -1);
                o = j >= 0 ? start[j] : Tiers * 4 - 4;
            }
            data[i * 4] = (byte)o; data[i * 4 + 1] = (byte)(o >> 8);
            data[i * 4 + 2] = (byte)lists[i].Count; data[i * 4 + 3] = 0;
        }
        if (at == OriginalLists && size < OriginalListsSize) rom.WriteBytes(at + size, new byte[OriginalListsSize - size]);
        rom.WriteBytes(at, data);
        int snes = L2Rom.FileToSnes(at);
        void Ref(int where, int addr) { rom.WriteByte(where, (byte)addr); rom.WriteByte(where + 1, (byte)(addr >> 8)); rom.WriteByte(where + 2, (byte)(addr >> 16)); }
        Ref(ListRefs[0], snes); Ref(ListRefs[1], snes + 2); Ref(ListRefs[2], snes);
        return at;
    }

    /// <summary>Bytes the lists at <paramref name="at"/> (in the added space) may take: the room Lufia Forge noted before
    /// them, else (put there by something else) as far as they reach.</summary>
    private static int Room(RomBuffer rom, int at)
    {
        if (at >= 6 && rom.ReadByte(at - 6) == 'L' && rom.ReadByte(at - 5) == 'F' && rom.ReadByte(at - 4) == 'W' && rom.ReadByte(at - 3) == 'L')
            return rom.ReadUInt16Le(at - 2);
        int end = Enumerable.Range(0, Tiers).Max(i => rom.ReadUInt16Le(at + i * 4) + rom.ReadUInt16Le(at + i * 4 + 2) * 2);
        return Math.Max(end, Tiers * 4);
    }

    // ---------------------------------------------------------------- prices and points

    public static int[] ReadThresholds(RomBuffer rom) => Enumerable.Range(0, ThresholdCount).Select(i => (int)rom.ReadUInt16Le(Thresholds + i * 2)).ToArray();

    public static void WriteThreshold(RomBuffer rom, int i, int value) => rom.WriteUInt16Le(Thresholds + i * 2, (ushort)Math.Clamp(value, 0, 65534));

    public static int MinPrice(RomBuffer rom) => rom.ReadUInt16Le(MinPriceAt);
    public static void WriteMinPrice(RomBuffer rom, int value) => rom.WriteUInt16Le(MinPriceAt, (ushort)Math.Clamp(value, 0, 65535));

    /// <summary>The tier of an item of <paramref name="price"/> (0-15), or -1 when it is under the lowest price that counts.</summary>
    public static int Tier(RomBuffer rom, int price)
    {
        if (price < MinPrice(rom)) return -1;
        var th = ReadThresholds(rom);
        for (int i = 0; i < th.Length; i++) if (th[i] >= price) return i;
        return ThresholdCount;
    }

    /// <summary>The lowest and highest price of tier <paramref name="tier"/>.</summary>
    public static (int Low, int High) TierPrices(RomBuffer rom, int tier)
    {
        var th = ReadThresholds(rom);
        int low = tier == 0 ? MinPrice(rom) : Math.Max(MinPrice(rom), th[tier - 1] + 1);
        int high = tier < ThresholdCount ? th[tier] : 65535;
        return (low, high);
    }

    /// <summary>Points for tiers 4g to 4g+3 in <paramref name="form"/> (1-3).</summary>
    public static int Gain(RomBuffer rom, int form, int group) => rom.ReadByte(Gains + (form - 1) * Groups + group);
    public static void WriteGain(RomBuffer rom, int form, int group, int value) => rom.WriteByte(Gains + (form - 1) * Groups + group, (byte)Math.Clamp(value, 0, MeterFull));

    /// <summary>Pickiness a monster starts with once its highest form is <paramref name="form"/> (1-3).</summary>
    public static int Pickiness(RomBuffer rom, int form) => rom.ReadByte(StartPickiness + form);
    public static void WritePickiness(RomBuffer rom, int form, int value) => rom.WriteByte(StartPickiness + form, (byte)Math.Clamp(value, 0, ThresholdCount));

    /// <summary>The wished item's multiplier, 1-4 (0: the code isn't one Lufia Forge knows).</summary>
    public static int WishBonus(RomBuffer rom)
    {
        var b = rom.ReadBytes(WishBonusAt, 5);
        for (int i = 0; i < BonusCode.Length; i++) if (b.SequenceEqual(BonusCode[i])) return i + 1;
        return 0;
    }

    public static void WriteWishBonus(RomBuffer rom, int times) => rom.WriteBytes(WishBonusAt, BonusCode[Math.Clamp(times, 1, 4) - 1]);

    /// <summary>The most points one feeding gives (16 in the game): the meter goes up by one every second step of the
    /// sparkle animation ($82:CB10), which runs 4 x points + 4 steps but at most 2 x this many (CMP #$20 / LDA #$20).
    /// Each step puts a sparkle in one of 32 sprite slots, so it can only be lowered: more steps write past the slots and
    /// the game crashes (seen in the game with 32).</summary>
    public static int MaxPerFeeding(RomBuffer rom) => rom.ReadByte(MaxStepsAt) / 2;
    public const int MaxSparkles = 16;
    public static void WriteMaxPerFeeding(RomBuffer rom, int points)
    {
        byte steps = (byte)(Math.Clamp(points, 1, MaxSparkles) * 2);
        rom.WriteByte(MaxStepsAt, steps); rom.WriteByte(MaxStepsAt + 4, steps);
    }

    public static int OwnFruitPoints(RomBuffer rom) => rom.ReadByte(OwnFruitPointsAt);
    public static void WriteOwnFruitPoints(RomBuffer rom, int v) => rom.WriteByte(OwnFruitPointsAt, (byte)Math.Clamp(v, 0, MeterFull));
    public static int MagicFruitPoints(RomBuffer rom) => rom.ReadByte(MagicFruitPointsAt);
    public static void WriteMagicFruitPoints(RomBuffer rom, int v) => rom.WriteByte(MagicFruitPointsAt, (byte)Math.Clamp(v, 0, MeterFull));
    public static int MagicFruit(RomBuffer rom) => rom.ReadUInt16Le(MagicFruitAt);
    public static void WriteMagicFruit(RomBuffer rom, int item) => rom.WriteUInt16Le(MagicFruitAt, (ushort)(item & 0x1FF));

    /// <summary>Points an item of <paramref name="price"/> gives a monster whose highest form is <paramref name="form"/>
    /// (1-3) when eaten, or -1 when it is refused at that pickiness.</summary>
    public static int Points(RomBuffer rom, int form, int price, int pickiness, bool wished)
    {
        int tier = Tier(rom, price);
        if (tier < 0 || tier < pickiness) return -1;
        int p = Gain(rom, form, tier / 4);
        return Math.Min(wished ? p * Math.Max(1, WishBonus(rom)) : p, MaxPerFeeding(rom));
    }

    // ---------------------------------------------------------------- fruits (per monster)

    /// <summary>The fruit that turns monster <paramref name="m"/> into its 5th form.</summary>
    public static int Form5FruitOf(RomBuffer rom, int m) => rom.ReadUInt16Le(Form5Fruit + m * 2);
    public static void WriteForm5Fruit(RomBuffer rom, int m, int item) => rom.WriteUInt16Le(Form5Fruit + m * 2, (ushort)(item & 0x1FF));
    /// <summary>Its own fruit (1 point, pickiness reset; turns the 5th form back).</summary>
    public static int OwnFruitOf(RomBuffer rom, int m) => rom.ReadUInt16Le(OwnFruit + m * 2);
    public static void WriteOwnFruit(RomBuffer rom, int m, int item) => rom.WriteUInt16Le(OwnFruit + m * 2, (ushort)(item & 0x1FF));
    /// <summary>The form it must be in for the 5th-form fruit (and goes back to).</summary>
    public static int FruitFormOf(RomBuffer rom, int m) => rom.ReadUInt16Le(FruitForm + m * 2);
    public static void WriteFruitForm(RomBuffer rom, int m, int form) => rom.WriteUInt16Le(FruitForm + m * 2, (ushort)Math.Clamp(form, 1, 4));
}
