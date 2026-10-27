namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II shops (US ROM, found 2026-10-22: event command 18 xx opens shop xx; its routine at $82:E729 reads a u16
/// offset per shop from $97:EE9F, file 0xBEE9F; read by $82:E297). A shop: a byte (its look), a u16 mask of its menu
/// (bit 1 coins, 2 items, 3 weapons, 4 armor, 5 spells, 7 sell; bit 0 = a list the game builds itself ($82:E433):
/// every item with a count in the 4-bit table at $7F:F080, probably items sold earlier; it has no data), then a list for
/// each of bits 1-4 (u16 item numbers, 0000 ends it) and for bit 5 (spell numbers, FF ends it), in bit order.
/// Checked in the game: shop 01 = Elcid's item shop, shop 02 = Elcid's spell shop (Strong, Spark, Poison).
/// </summary>
public static class L2Shops
{
    public const int Table = 0xBEE9F, Count = 0x100;
    /// <summary>The US game's last shop id (its shops are 01-3E, a few of them empty).</summary>
    public const int LastOriginal = 0x3E;

    public static readonly string[] SectionNames = { "", "Coins", "Items", "Weapons", "Armor", "Spells" };

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
        public int Mask { get; init; }
        public bool Sells => (Mask & 0x80) != 0;
        /// <summary>Bit 0: a list the game builds from the items at $7F:F080 (no data in the ROM).</summary>
        public bool IsComposite => (Mask & 1) != 0;
        public List<Section> Sections { get; init; } = new();
    }

    /// <summary>How many entries the shop table has in this ROM. The game takes any id 00-FF (AND #$00FF at $82:E736), but
    /// the shops' data starts right after the table: the US game's 0x3F entries are followed by zeros up to its first
    /// shop; Spekkio and Kureji Lufia put their added shops (3F-41, 3C/3D/3F) in those zeros, so the table ends where the
    /// first shop's data starts (0xB0 and 0xA6 entries there, the ids past the used ones still 0000).</summary>
    public static int CountIn(RomBuffer rom)
    {
        int end = Count * 2;
        for (int i = 0; i * 2 < end; i++)
        {
            int off = rom.ReadUInt16Le(Table + i * 2);
            if (off != 0 && off < end) end = Math.Max(off, (i + 1) * 2);
        }
        return end / 2;
    }

    public static Shop? Read(RomBuffer rom, int id)
    {
        if (id < 0 || id >= CountIn(rom)) return null;
        int off = rom.ReadUInt16Le(Table + id * 2);
        if (off == 0) return null;
        int at = L2Rom.InBank(Table, off);
        if (at < 0) return null;
        int mask = rom.ReadUInt16Le(at + 1);
        var shop = new Shop { Id = id, At = at, Mask = mask };
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
            shop.Sections.Add(sec);
        }
        return shop;
    }

    public static List<Shop> All(RomBuffer rom) => Enumerable.Range(0, CountIn(rom)).Select(i => Read(rom, i)).Where(s => s != null).Cast<Shop>().ToList();

    /// <summary>Writes a section's entries back in place (as many as it has: lists don't grow or shrink).</summary>
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
}
