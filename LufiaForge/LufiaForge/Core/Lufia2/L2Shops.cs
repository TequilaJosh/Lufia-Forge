namespace LufiaForge.Core.Lufia2;

// lufia 2 shops (US rom, found 2026-10-22). event command 18 xx opens shop xx, its routine at $82:E729 reads a u16
// offset per shop from $97:EE9F (file 0xBEE9F), read by $82:E297.
// a shop is a byte (its look), a u16 mask of its menu (bit 1 coins, 2 items, 3 weapons, 4 armor, 5 spells, 7 sell.
// bit 0 = a list the game builds itself ($82:E433): every item with a count in the 4-bit table at $7F:F080, probably
// items sold earlier, it has no data), then a list for each of bits 1-4 (u16 item numbers, 0000 ends it) and for bit 5
// (spell numbers, FF ends it), in bit order.
// checked in the game: shop 01 = Elcid's item shop, shop 02 = Elcid's spell shop (Strong, Spark, Poison).
//
// new shops and longer lists (found 2026-10-27): bank $97 has no room left, so the first time a shop is added or a list
// grows, we copy the shop table and every shop to a bank of our own in the added space, at the same addresses
// ($xx:EE9F on), and the three places in the shop code that name bank $97 ($82:E72E, the pointer to the shop, and
// $82:E391 and $82:E404, the item and spell lists, which the game only keeps as 16-bit addresses) name that bank
// instead. the game reads every shop through those long pointers, so nothing else changes. the bank's first part
// ($xx:8010-$xx:EE9E, about 28 KB) holds new shops and lists that grew. the shop id is a byte (AND #$00FF), and the
// table has room for every id up to where the first shop's data starts
public static class L2Shops
{
    public const int Table = 0xBEE9F, Count = 0x100;
    // the US game's last shop id (its shops are 01-3E, a few of them empty)
    public const int LastOriginal = 0x3E;
    // the most entries a list can have here (the game keeps a list in a 512-entry buffer and scrolls it)
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
        // file offset of the list's first entry
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
        // bit 0: a list the game builds from the items at $7F:F080 (no data in the rom)
        public bool IsComposite => (Mask & 1) != 0;
        public List<Section> Sections { get; init; } = new();
        // how many bytes the shop's data takes
        public int Length { get; init; }
    }

    // file offset of the shop table the game reads (its own at $97:EE9F, or our copy)
    public static int TableAt(RomBuffer rom)
    {
        if (rom.ReadByte(TableSite) != 0xA2) return Table;
        int address = rom.ReadUInt16Le(TableSite + 1), bank = rom.ReadByte(BankSite);
        return L2Rom.SnesToFile(bank << 16 | address);
    }

    // file offset of our shop bank, or -1
    public static int Block(RomBuffer rom)
    {
        int bank = rom.ReadByte(BankSite);
        if (bank == 0x97 || rom.ReadByte(TableSite) != 0xA2) return -1;
        int bankAt = L2Rom.SnesToFile(bank << 16 | 0x8000);
        return bankAt + 0x8000 <= rom.Length && rom.ReadAscii(bankAt, Magic.Length) == Magic ? bankAt : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    // true if the shops can move to a bank of their own (the shop code is still the game's)
    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        bool Same(int at, byte[] expected) => at + expected.Length <= rom.Length && rom.ReadBytes(at, expected.Length).SequenceEqual(expected);
        if (!Same(TableSite, TableSiteOriginal) || !Same(ListBankSite1 - 1, ListSiteOriginal) || !Same(ListBankSite2 - 1, ListSiteOriginal))
        {
            why = "This ROM's shop code isn't the US game's (another patch or hack changed it), so new shops can't be added.";
            return false;
        }
        if (TableGuard.LockReason(rom, "Shops") is { } locked) { why = locked; return false; }
        return true;
    }

    // how many entries the shop table has in this rom. the game takes any id 00-FF (AND #$00FF at $82:E736), but the
    // shops' data starts right after the table: the US game's 0x3F entries are followed by zeros up to its first shop.
    // Spekkio and Kureji Lufia put their added shops (3F-41, 3C/3D/3F) in those zeros, so the table ends where the
    // first shop's data starts (0xB0 and 0xA6 entries there, the ids past the used ones still 0000)
    public static int CountIn(RomBuffer rom)
    {
        int table = TableAt(rom), count = Scan(rom, table);
        int block = Block(rom);
        if (block < 0) return count;
        // in our bank the table can't end where the first shop's data starts. a shop that grew or got rewritten moves
        // to the bank's first part, and the bytes it leaves behind (right after the table in Spekkio and Kureji Lufia)
        // would read as more entries. so the count is the game's own table's (bank $97 is left as it was), and further
        // only up to the last id that names a shop we put in our part of the bank
        int game = Scan(rom, Table), last = game - 1;
        for (int i = game; i < count; i++) if (IsOwn(rom, block, table, i)) last = i;
        return Math.Min(count, last + 1);
    }

    // true if entry i names a shop in our part of the bank: an address from $xx:8010 up to the free part (left-behind
    // bytes like 2800 wrap round the bank to below $8000)
    private static bool IsOwn(RomBuffer rom, int block, int table, int i)
    {
        int offset = rom.ReadUInt16Le(table + i * 2), address = (L2Rom.FileToSnes(table) + offset) & 0xFFFF;
        return offset != 0 && address >= 0x8000 + DataStart && address < rom.ReadUInt16Le(block + NextAt);
    }

    // true if entry id isn't a shop even though it isn't 0000: it's past the game's own table in our bank and names
    // none of our shops (bytes a moved shop left behind)
    private static bool Stale(RomBuffer rom, int id)
    {
        int block = Block(rom);
        return block >= 0 && id >= Scan(rom, Table) && !IsOwn(rom, block, TableAt(rom), id);
    }

    // the ids of a table whose entries end where the first shop's data starts
    private static int Scan(RomBuffer rom, int table)
    {
        int end = Count * 2;
        for (int i = 0; i * 2 < end; i++)
        {
            int offset = rom.ReadUInt16Le(table + i * 2);
            int shopAt = L2Rom.InBank(table, offset);
            // offsets that reach the table's own space from below (our shops before it) don't end it
            if (offset != 0 && offset < end && shopAt > table) end = Math.Max(offset, (i + 1) * 2);
        }
        return end / 2;
    }

    public static Shop? Read(RomBuffer rom, int id)
    {
        if (id < 0 || id >= CountIn(rom)) return null;
        int table = TableAt(rom);
        int offset = rom.ReadUInt16Le(table + id * 2);
        if (offset == 0 || Stale(rom, id)) return null;
        int at = L2Rom.InBank(table, offset);
        if (at < 0) return null;
        int mask = rom.ReadUInt16Le(at + 1);
        var sections = new List<Section>();
        int p = at + 3;
        for (int bit = 1; bit <= 5; bit++)
        {
            if ((mask & 1 << bit) == 0) continue;
            var section = new Section { Bit = bit, At = p };
            if (bit == 5)
                for (int guard = 0; guard < 64 && rom.ReadByte(p) != 0xFF; guard++, p++) section.Entries.Add(rom.ReadByte(p));
            else
                for (int guard = 0; guard < 64 && rom.ReadUInt16Le(p) != 0; guard++, p += 2) section.Entries.Add(rom.ReadUInt16Le(p));
            p += bit == 5 ? 1 : 2;
            sections.Add(section);
        }
        return new Shop { Id = id, At = at, Look = rom.ReadByte(at), Mask = mask, Sections = sections, Length = p - at };
    }

    public static List<Shop> All(RomBuffer rom) => Enumerable.Range(0, CountIn(rom)).Select(i => Read(rom, i)).Where(s => s != null).Cast<Shop>().ToList();

    // writes a section's entries back in place (as many as it has, Rewrite changes how many)
    public static void WriteSection(RomBuffer rom, Section sec)
    {
        for (int i = 0; i < sec.Entries.Count; i++)
        {
            if (sec.IsSpells) rom.WriteByte(sec.At + i, (byte)sec.Entries[i]);
            else rom.WriteUInt16Le(sec.At + i * 2, (ushort)sec.Entries[i]);
        }
    }

    // maps whose events open shop id (an 18 xx right after a line of text)
    public static List<int> UsedOn(RomBuffer rom, int id)
    {
        var maps = new List<int>();
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            int block = L2Events.Block(rom, m);
            if (block < 0) continue;
            int end = block + L2Events.BlockSize(rom, m, block);
            for (int p = block; p < end && p + 2 < rom.Length; p++)
                if (rom.ReadByte(p) == 1 && rom.ReadByte(p + 1) == 0x18 && rom.ReadByte(p + 2) == id) { maps.Add(m); break; }
        }
        return maps;
    }

    // new shops

    // copies the shop table and every shop to a bank of ours (once, see the top of the class). returns the bank's
    // file offset
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        int table = TableAt(rom);
        int end = table + CountIn(rom) * 2;
        foreach (var shop in All(rom)) end = Math.Max(end, shop.At + shop.Length);
        if (end > table / 0x8000 * 0x8000 + 0x8000) throw new InvalidOperationException("The shops run past the end of their bank, so they can't be copied safely.");
        int bank = L2Rom.Allocate(rom, 0x8000);
        if (bank % 0x8000 != 0) throw new InvalidOperationException("No whole bank of added space was free for the shops.");
        rom.WriteBytes(bank, new byte[0x8000]);
        int copyAt = bank + table % 0x8000;
        rom.WriteBytes(copyAt, rom.ReadBytes(table, end - table));
        rom.WriteBytes(bank, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(bank + Magic.Length, 1);
        rom.WriteUInt16Le(bank + NextAt, (ushort)(0x8000 + DataStart));
        byte bankByte = (byte)(L2Rom.FileToSnes(bank) >> 16);
        rom.WriteByte(BankSite, bankByte);
        rom.WriteByte(ListBankSite1, bankByte);
        rom.WriteByte(ListBankSite2, bankByte);
        return bank;
    }

    // bytes still free for shops in our bank (the whole room if it isn't in yet)
    public static int Room(RomBuffer rom)
    {
        int block = Block(rom);
        int tableAddr = TableAt(rom) % 0x8000 + 0x8000;
        if (block < 0) return (Table % 0x8000 + 0x8000) - (0x8000 + DataStart);
        return tableAddr - rom.ReadUInt16Le(block + NextAt);
    }

    // the id a new shop gets: the first free one past the shops the rom has, otherwise the lowest free one (01 up)
    public static int NextId(RomBuffer rom)
    {
        int count = CountIn(rom), table = TableAt(rom);
        bool Free(int i) => rom.ReadUInt16Le(table + i * 2) == 0 || Stale(rom, i);
        int last = Enumerable.Range(1, count - 1).LastOrDefault(i => !Free(i));
        for (int i = last + 1; i < count; i++) if (Free(i)) return i;
        for (int i = 1; i < count; i++) if (Free(i)) return i;
        return -1;
    }

    // the bytes of a shop: its look, menu mask, then its lists
    public static byte[] Bytes(int look, int mask, IReadOnlyDictionary<int, IReadOnlyList<int>> lists)
    {
        var bytes = new List<byte> { (byte)look, (byte)mask, (byte)(mask >> 8) };
        for (int bit = 1; bit <= 5; bit++)
        {
            if ((mask & 1 << bit) == 0) continue;
            var list = lists.TryGetValue(bit, out var have) ? have : Array.Empty<int>();
            if (list.Count > MaxEntries) throw new ArgumentException($"A list can have up to {MaxEntries} entries here.");
            if (bit == 5)
            {
                if (list.Any(v => v is < 0 or >= 0xFF)) throw new ArgumentException("A spell list can't hold spell FF.");
                bytes.AddRange(list.Select(v => (byte)v)); bytes.Add(0xFF);
            }
            else
            {
                if (list.Any(v => v is <= 0 or > 0x1FF)) throw new ArgumentException("An item list can't hold item 000 (it ends the list).");
                foreach (int item in list) { bytes.Add((byte)item); bytes.Add((byte)(item >> 8)); }
                bytes.Add(0); bytes.Add(0);
            }
        }
        return bytes.ToArray();
    }

    // writes shop id from scratch (any id the table has): its look, mask and lists, any length. it goes where it was if
    // it fits there and that place is ours, otherwise into the free part of our bank, and the table entry follows.
    // moves the shops to their bank first if they aren't there yet
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

    // a new shop: a copy of shop from (its look, menu and lists) with an id of its own. returns the id, events open it
    // with 18 xx ("Open a shop" on the Events page)
    public static int Add(RomBuffer rom, int from)
    {
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        var source = Read(rom, from) ?? throw new ArgumentException($"There is no shop {from:X2}.");
        int id = NextId(rom);
        if (id < 0) throw new InvalidOperationException("Every id of the shop table is used.");
        Rewrite(rom, id, source.Look, source.Mask, Lists(source));
        return id;
    }

    // a shop's lists by bit
    public static Dictionary<int, IReadOnlyList<int>> Lists(Shop shop) => shop.Sections.ToDictionary(x => x.Bit, x => (IReadOnlyList<int>)x.Entries.ToList());

    // true if it's a shop we added (its data in our bank, before the copied table)
    public static bool IsAdded(RomBuffer rom, Shop shop)
    {
        int block = Block(rom);
        return block >= 0 && shop.At >= block && shop.At < TableAt(rom);
    }
}
