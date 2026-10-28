namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II shops (US ROM, found 2026-10-22: event command 18 xx opens shop xx; its routine at $82:E729 reads a u16
/// offset per shop from $97:EE9F, file 0xBEE9F; read by $82:E297). A shop: a byte (its look), a u16 mask of its menu
/// (bit 1 coins, 2 items, 3 weapons, 4 armor, 5 spells, 7 sell; bit 0 = a list the game builds itself ($82:E433):
/// every item with a count in the 4-bit table at $7F:F080, probably items sold earlier; it has no data), then a list for
/// each of bits 1-4 (u16 item numbers, 0000 ends it) and for bit 5 (spell numbers, FF ends it), in bit order.
/// Checked in the game: shop 01 = Elcid's item shop, shop 02 = Elcid's spell shop (Strong, Spark, Poison).
/// <para>
/// New shops and longer lists (found 2026-10-27): bank $97 has no room left, so the first time a shop is added or a list
/// grows, Lufia Forge copies the shop table and every shop to a bank of its own in the added space, at the same
/// addresses ($xx:EE9F on), and the three places in the shop code that name bank $97 ($82:E72E, the pointer to the
/// shop; $82:E391 and $82:E404, the item and spell lists, which the game keeps only as 16-bit addresses) name that bank
/// instead. The game reads every shop through those long pointers, so nothing else changes; the bank's first part
/// ($xx:8010-$xx:EE9E, about 28 KB) holds new shops and lists that grew. The shop id is a byte (AND #$00FF), and the
/// table has room for every id up to where the first shop's data starts.
/// </para>
/// </summary>
public static class L2Shops
{
    public const int Table = 0xBEE9F, Count = 0x100;
    /// <summary>The US game's last shop id (its shops are 01-3E, a few of them empty).</summary>
    public const int LastOriginal = 0x3E;
    /// <summary>The most entries a list can have here (the game keeps a list in a 512-entry buffer and scrolls it).</summary>
    public const int MaxEntries = 40;

    public static readonly string[] SectionNames = { "", "Coins", "Items", "Weapons", "Armor", "Spells" };

    // the code that names the shop table and its bank (the same in the US game and the fan hacks)
    private const int TableSite = 0x16729, BankSite = 0x1672F, ListBankSite1 = 0x16392, ListBankSite2 = 0x16405;
    private static readonly byte[] TableSiteOriginal = { 0xA2, 0x9F, 0xEE, 0x86, 0x08, 0xA9, 0x97, 0x85, 0x0A };
    private static readonly byte[] ListSiteOriginal = { 0xA9, 0x97, 0x85, 0xFE };
    private const string Magic = "LF2SH";
    private const int NextAt = 6, DataStart = 0x10;

    public sealed class Section
    {
        public int Bit { get; init; }
        public string Name => SectionNames[Bit];
        /// <summary>File offset of the list's first entry.</summary>
        public int At { get; init; }
        public bool IsSpells => Bit == 5;
        public List<int> Entries { get; init; } = new();
    }

    public sealed class Shop
    {
        public int Id { get; init; }
        public int At { get; init; }
        public int Look { get; init; }
        public int Mask { get; init; }
        public bool Sells => (Mask & 0x80) != 0;
        /// <summary>Bit 0: a list the game builds from the items at $7F:F080 (no data in the ROM).</summary>
        public bool IsComposite => (Mask & 1) != 0;
        public List<Section> Sections { get; init; } = new();
        /// <summary>Bytes the shop's data takes.</summary>
        public int Length { get; init; }
    }

    /// <summary>File offset of the shop table the game reads (its own at $97:EE9F, or Lufia Forge's copy).</summary>
    public static int TableAt(RomBuffer rom)
    {
        if (rom.ReadByte(TableSite) != 0xA2) return Table;
        int addr = rom.ReadUInt16Le(TableSite + 1), bank = rom.ReadByte(BankSite);
        return L2Rom.SnesToFile(bank << 16 | addr);
    }

    /// <summary>File offset of Lufia Forge's shop bank, or -1.</summary>
    public static int Block(RomBuffer rom)
    {
        int bank = rom.ReadByte(BankSite);
        if (bank == 0x97 || rom.ReadByte(TableSite) != 0xA2) return -1;
        int at = L2Rom.SnesToFile(bank << 16 | 0x8000);
        return at + 0x8000 <= rom.Length && rom.ReadAscii(at, Magic.Length) == Magic ? at : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>Whether the shops can move to a bank of their own (the shop code is still the game's).</summary>
    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        bool Same(int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);
        if (!Same(TableSite, TableSiteOriginal) || !Same(ListBankSite1 - 1, ListSiteOriginal) || !Same(ListBankSite2 - 1, ListSiteOriginal))
        {
            why = "This ROM's shop code isn't the US game's (another patch or hack changed it), so new shops can't be added.";
            return false;
        }
        if (TableGuard.LockReason(rom, "Shops") is { } locked) { why = locked; return false; }
        return true;
    }

    /// <summary>How many entries the shop table has in this ROM. The game takes any id 00-FF (AND #$00FF at $82:E736), but
    /// the shops' data starts right after the table: the US game's 0x3F entries are followed by zeros up to its first
    /// shop; Spekkio and Kureji Lufia put their added shops (3F-41, 3C/3D/3F) in those zeros, so the table ends where the
    /// first shop's data starts (0xB0 and 0xA6 entries there, the ids past the used ones still 0000).</summary>
    public static int CountIn(RomBuffer rom)
    {
        int table = TableAt(rom), end = Count * 2;
        for (int i = 0; i * 2 < end; i++)
        {
            int off = rom.ReadUInt16Le(table + i * 2);
            int at = L2Rom.InBank(table, off);
            // offsets that reach the table's own space from below (Lufia Forge's shops before it) don't end it
            if (off != 0 && off < end && at > table) end = Math.Max(off, (i + 1) * 2);
        }
        return end / 2;
    }

    public static Shop? Read(RomBuffer rom, int id)
    {
        if (id < 0 || id >= CountIn(rom)) return null;
        int table = TableAt(rom);
        int off = rom.ReadUInt16Le(table + id * 2);
        if (off == 0) return null;
        int at = L2Rom.InBank(table, off);
        if (at < 0) return null;
        int mask = rom.ReadUInt16Le(at + 1);
        var sections = new List<Section>();
        int p = at + 3;
        for (int bit = 1; bit <= 5; bit++)
        {
            if ((mask & 1 << bit) == 0) continue;
            var sec = new Section { Bit = bit, At = p };
            if (bit == 5)
                for (int guard = 0; guard < 64 && rom.ReadByte(p) != 0xFF; guard++, p++) sec.Entries.Add(rom.ReadByte(p));
            else
                for (int guard = 0; guard < 64 && rom.ReadUInt16Le(p) != 0; guard++, p += 2) sec.Entries.Add(rom.ReadUInt16Le(p));
            p += bit == 5 ? 1 : 2;
            sections.Add(sec);
        }
        return new Shop { Id = id, At = at, Look = rom.ReadByte(at), Mask = mask, Sections = sections, Length = p - at };
    }

    public static List<Shop> All(RomBuffer rom) => Enumerable.Range(0, CountIn(rom)).Select(i => Read(rom, i)).Where(s => s != null).Cast<Shop>().ToList();

    /// <summary>Writes a section's entries back in place (as many as it has; <see cref="Rewrite"/> changes their number).</summary>
    public static void WriteSection(RomBuffer rom, Section sec)
    {
        for (int i = 0; i < sec.Entries.Count; i++)
        {
            if (sec.IsSpells) rom.WriteByte(sec.At + i, (byte)sec.Entries[i]);
            else rom.WriteUInt16Le(sec.At + i * 2, (ushort)sec.Entries[i]);
        }
    }

    /// <summary>Maps whose events open shop <paramref name="id"/> (an 18 xx right after a line of text).</summary>
    public static List<int> UsedOn(RomBuffer rom, int id)
    {
        var maps = new List<int>();
        for (int m = 0; m < L2Maps.Count; m++)
        {
            int b = L2Events.Block(rom, m);
            if (b < 0) continue;
            int end = b + L2Events.BlockSize(rom, m, b);
            for (int p = b; p < end && p + 2 < rom.Length; p++)
                if (rom.ReadByte(p) == 1 && rom.ReadByte(p + 1) == 0x18 && rom.ReadByte(p + 2) == id) { maps.Add(m); break; }
        }
        return maps;
    }

    // ── new shops ────────────────────────────────────────────────────────────

    /// <summary>Copies the shop table and every shop to a bank of Lufia Forge's (once; see the class). Returns the bank's
    /// file offset.</summary>
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        int table = TableAt(rom);
        int end = table + CountIn(rom) * 2;
        foreach (var s in All(rom)) end = Math.Max(end, s.At + s.Length);
        if (end > table / 0x8000 * 0x8000 + 0x8000) throw new InvalidOperationException("The shops run past the end of their bank, so they can't be copied safely.");
        int bank = L2Rom.Allocate(rom, 0x8000);
        if (bank % 0x8000 != 0) throw new InvalidOperationException("No whole bank of added space was free for the shops.");
        rom.WriteBytes(bank, new byte[0x8000]);
        int copyAt = bank + table % 0x8000;
        rom.WriteBytes(copyAt, rom.ReadBytes(table, end - table));
        rom.WriteBytes(bank, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(bank + Magic.Length, 1);
        rom.WriteUInt16Le(bank + NextAt, (ushort)(0x8000 + DataStart));
        byte b = (byte)(L2Rom.FileToSnes(bank) >> 16);
        rom.WriteByte(BankSite, b);
        rom.WriteByte(ListBankSite1, b);
        rom.WriteByte(ListBankSite2, b);
        return bank;
    }

    /// <summary>Bytes still free for shops in Lufia Forge's bank (the whole room when it isn't in yet).</summary>
    public static int Room(RomBuffer rom)
    {
        int b = Block(rom);
        int tableAddr = TableAt(rom) % 0x8000 + 0x8000;
        if (b < 0) return (Table % 0x8000 + 0x8000) - (0x8000 + DataStart);
        return tableAddr - rom.ReadUInt16Le(b + NextAt);
    }

    /// <summary>The id a new shop gets: the first free one past the shops the ROM has, else the lowest free one (01 up).</summary>
    public static int NextId(RomBuffer rom)
    {
        int n = CountIn(rom), table = TableAt(rom);
        bool Free(int i) => rom.ReadUInt16Le(table + i * 2) == 0;
        int last = Enumerable.Range(1, n - 1).LastOrDefault(i => !Free(i));
        for (int i = last + 1; i < n; i++) if (Free(i)) return i;
        for (int i = 1; i < n; i++) if (Free(i)) return i;
        return -1;
    }

    /// <summary>The bytes of a shop: its look, menu mask, then its lists.</summary>
    public static byte[] Bytes(int look, int mask, IReadOnlyDictionary<int, IReadOnlyList<int>> lists)
    {
        var b = new List<byte> { (byte)look, (byte)mask, (byte)(mask >> 8) };
        for (int bit = 1; bit <= 5; bit++)
        {
            if ((mask & 1 << bit) == 0) continue;
            var l = lists.TryGetValue(bit, out var have) ? have : Array.Empty<int>();
            if (l.Count > MaxEntries) throw new ArgumentException($"A list can have up to {MaxEntries} entries here.");
            if (bit == 5)
            {
                if (l.Any(v => v is < 0 or >= 0xFF)) throw new ArgumentException("A spell list can't hold spell FF.");
                b.AddRange(l.Select(v => (byte)v)); b.Add(0xFF);
            }
            else
            {
                if (l.Any(v => v is <= 0 or > 0x1FF)) throw new ArgumentException("An item list can't hold item 000 (it ends the list).");
                foreach (int v in l) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
                b.Add(0); b.Add(0);
            }
        }
        return b.ToArray();
    }

    /// <summary>Writes shop <paramref name="id"/> anew (any id the table has): its look, mask and lists, of any length.
    /// It goes where it was when it fits there and that place is Lufia Forge's, else to the free part of Lufia Forge's
    /// bank; the table entry follows. Moves the shops to their bank first when they aren't there yet.</summary>
    public static void Rewrite(RomBuffer rom, int id, int look, int mask, IReadOnlyDictionary<int, IReadOnlyList<int>> lists)
    {
        if (id <= 0 || id > 0xFF) throw new ArgumentOutOfRangeException(nameof(id), "Shop ids go from 01 to FF.");
        var bytes = Bytes(look, mask, lists);
        int block = Install(rom);
        if (id >= CountIn(rom)) throw new InvalidOperationException($"Shop {id:X2} is past the end of this ROM's shop table.");
        int table = TableAt(rom);
        int tableAddr = table % 0x8000 + 0x8000;
        var old = Read(rom, id);
        int at;
        if (old != null && old.At >= block && old.At < block + 0x8000 && L2Rom.FileToSnes(old.At) % 0x10000 < tableAddr && bytes.Length <= old.Length)
            at = old.At;
        else
        {
            int next = rom.ReadUInt16Le(block + NextAt);
            if (next + bytes.Length > tableAddr) throw new InvalidOperationException("The room for new shops is full.");
            at = block + (next - 0x8000);
            rom.WriteUInt16Le(block + NextAt, (ushort)(next + bytes.Length));
        }
        rom.WriteBytes(at, bytes);
        rom.WriteUInt16Le(table + id * 2, (ushort)L2Rom.OffsetInBank(table, at));
    }

    /// <summary>A new shop: a copy of shop <paramref name="from"/> (its look, menu and lists) with an id of its own.
    /// Returns the id; events open it with 18 xx ("Open a shop" on the Events page).</summary>
    public static int Add(RomBuffer rom, int from)
    {
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        var src = Read(rom, from) ?? throw new ArgumentException($"There is no shop {from:X2}.");
        int id = NextId(rom);
        if (id < 0) throw new InvalidOperationException("Every id of the shop table is used.");
        Rewrite(rom, id, src.Look, src.Mask, Lists(src));
        return id;
    }

    /// <summary>A shop's lists by bit.</summary>
    public static Dictionary<int, IReadOnlyList<int>> Lists(Shop s) => s.Sections.ToDictionary(x => x.Bit, x => (IReadOnlyList<int>)x.Entries.ToList());

    /// <summary>Whether the shop is one Lufia Forge added (its data in Lufia Forge's bank, before the copied table).</summary>
    public static bool IsAdded(RomBuffer rom, Shop s)
    {
        int b = Block(rom);
        return b >= 0 && s.At >= b && s.At < TableAt(rom);
    }
}
