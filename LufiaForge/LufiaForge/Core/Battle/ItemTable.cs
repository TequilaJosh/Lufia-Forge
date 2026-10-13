namespace LufiaForge.Core.Battle;

/// <summary>
/// Item records: u16 offsets from $0A:D800 (file 0x55800). The game reads records only in $08:F999 (unpack) and
/// $08:FA70 (type byte), both through $08:FAD0 (16-bit $D800 + offset, data bank 0A). An equipment record is 21 fixed
/// bytes and a list of (id, a, b) properties ended by 00; a usable item has a mask at +20 and record-relative data
/// pointers that the battle reads in bank 0A ($08:C2DD, $08:EED4). Records are packed back to back.
/// <para>Without the item bank patch, a record whose list grows moves to the free end of bank 0A (file 0x57D4B-0x57FFF).
/// With it (<see cref="MoveToItemBank"/>), the two readers are replaced (JML) by copies that read records from an item
/// bank in the added space: the bank holds a copy of bank 0A's $D800-$FFFF at the same addresses (so every record and
/// offset stays valid) and $8200-$D7FF is free for grown and new records (their offsets wrap: $D800 + offset).
/// Usable items' data pointers keep pointing at the data in bank 0A.</para>
/// </summary>
public static class ItemTable
{
    public const int Table = 0x55800, Count = 256, FixedSize = 21;
    public const int FreeStart = 0x57D4B, FreeEnd = 0x58000;
    private const int UnpackSite = 0x47999, TypeSite = 0x47A70;
    private static readonly byte[] UnpackOriginal = Convert.FromHexString("088BE220C210DA5A"), TypeOriginal = Convert.FromHexString("088BE220C210DA48");
    private const int CodeAt = 0x0000, MagicAt = 0x00F0, Lengths = 0x0100, BankFree = 0x0200, BankFreeEnd = 0x5800;
    private static readonly byte[] Magic = "LFIB"u8.ToArray();
    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    /// <summary>File offset of the item bank (0 = not moved).</summary>
    public static int ItemBank(RomBuffer rom)
    {
        if (rom.ReadByte(UnpackSite) != 0x5C) return 0;
        int b = FileOffset(rom.ReadByte(UnpackSite + 1) | rom.ReadByte(UnpackSite + 2) << 8 | rom.ReadByte(UnpackSite + 3) << 16) & ~0x7FFF;
        return b > 0 && b + 0x8000 <= rom.Length && rom.ReadBytes(b + MagicAt, 4).SequenceEqual(Magic) ? b : 0;
    }
    public static bool IsMoved(RomBuffer rom) => ItemBank(rom) > 0;
    public static bool IsOriginal(RomBuffer rom) => rom.ReadBytes(UnpackSite, 8).SequenceEqual(UnpackOriginal) && rom.ReadBytes(TypeSite, 8).SequenceEqual(TypeOriginal);

    /// <summary>The 16-bit address the game reads a record at ($D800 + offset, wrapping).</summary>
    private static int Address(RomBuffer rom, int id) => (0xD800 + rom.ReadUInt16Le(Table + id * 2)) & 0xFFFF;
    /// <summary>A record that lives only in the item bank (grown or new), not in bank 0A.</summary>
    public static bool InItemBank(RomBuffer rom, int id) => IsMoved(rom) && Address(rom, id) < 0xD800;

    /// <summary>File offset of the record the game reads (bank 0A, or the item bank for grown / new records).</summary>
    public static int RecordOffset(RomBuffer rom, int id)
    {
        int a = Address(rom, id);
        return InItemBank(rom, id) ? ItemBank(rom) + (a - 0x8000) : 0x50000 + (a - 0x8000);
    }

    /// <summary>After editing a record in bank 0A: the item bank's copy (which the game reads) gets the same bytes.</summary>
    public static void Sync(RomBuffer rom, int id)
    {
        int b = ItemBank(rom);
        if (b == 0 || InItemBank(rom, id)) return;
        int o = RecordOffset(rom, id), len = Room(rom, id);
        rom.WriteBytes(b + (o - 0x50000), rom.ReadBytes(o, len));
    }

    /// <summary>The record's properties, or null when it has no plain property list (usable items).</summary>
    public static List<(int Id, int A, int B)>? Properties(RomBuffer rom, int id)
    {
        int o = RecordOffset(rom, id), p = o + FixedSize;
        // byte +20 is a mask of extra data pointers (usable items): those records aren't a plain list
        if (rom.ReadByte(o + 20) != 0) return null;
        var list = new List<(int, int, int)>();
        for (int n = 0; n < 16; n++, p += 3)
        {
            if (p >= rom.Length) return null;
            int k = rom.ReadByte(p);
            if (k == 0) return list;
            list.Add((k, rom.ReadByte(p + 1), rom.ReadByte(p + 2)));
        }
        return null;
    }

    /// <summary>The bytes a record has (up to the next record, or its own length).</summary>
    public static int Length(RomBuffer rom, int id) => Room(rom, id);

    /// <summary>Where the next record starts (the room this one has), or the record's own end for moved records.</summary>
    private static int Room(RomBuffer rom, int id)
    {
        int o = RecordOffset(rom, id), next = int.MaxValue;
        if (InItemBank(rom, id)) return OwnLength(rom, id);
        for (int i = 0; i < Count; i++) { if (InItemBank(rom, i)) continue; int r = RecordOffset(rom, i); if (r > o && r < next) next = r; }
        if (o >= FreeStart || next == int.MaxValue) return FixedSize + ((Properties(rom, id)?.Count ?? 0) * 3) + 1;
        return next - o;
    }

    /// <summary>Writes a record's property list: in place when it fits, else the record moves to the end of bank 0A.</summary>
    public static void SetProperties(RomBuffer rom, int id, IReadOnlyList<(int Id, int A, int B)> props)
    {
        if (Properties(rom, id) == null) throw new InvalidOperationException("This item has no property list.");
        if (props.Any(p => p.Id <= 0 || p.Id >= 0x18)) throw new InvalidOperationException("Property ids are 01-17 (higher ones would write over the game's memory).");
        var tail = new List<byte>();
        foreach (var (k, a, b) in props) { tail.Add((byte)k); tail.Add((byte)a); tail.Add((byte)b); }
        tail.Add(0);
        int o = RecordOffset(rom, id);
        int room = Room(rom, id);
        if (FixedSize + tail.Count <= room)
        {
            // (zeros up to the next record: the editor still sees a plain property list)
            while (FixedSize + tail.Count < room && o < FreeStart && !InItemBank(rom, id)) tail.Add(0);
            rom.WriteBytes(o + FixedSize, tail.ToArray());
            Sync(rom, id);
            rom.FixChecksum();
            return;
        }
        var record = rom.ReadBytes(o, FixedSize).Concat(tail).ToArray();
        // the item bank gives 22 KB instead of bank 0A's last 693 bytes
        if (!IsMoved(rom) && IsOriginal(rom)) MoveToItemBank(rom);
        if (IsMoved(rom)) { Place(rom, id, record); return; }
        int at = FindFree(rom, record.Length, id);
        if (at < 0) throw new InvalidOperationException("No room left at the end of bank 0A for a longer item record.");
        rom.WriteBytes(at, record);
        rom.WriteUInt16Le(Table + id * 2, (ushort)(at - Table));
        rom.FixChecksum();
    }

    private static int FindFree(RomBuffer rom, int len, int skip)
    {
        var used = new List<(int S, int E)>();
        for (int i = 0; i < Count; i++)
        {
            if (i == skip) continue;
            int r = RecordOffset(rom, i);
            if (r >= FreeStart) used.Add((r, r + Room(rom, i)));
        }
        for (int p = FreeStart; p + len <= FreeEnd; p++)
        {
            int q = p;
            var hit = used.Where(u => u.S < q + len && u.E > q).Select(u => u.E).DefaultIfEmpty(-1).Max();
            if (hit < 0) return p;
            p = hit - 1;
        }
        return -1;
    }

    // ── the item bank ─────────────────────────────────────────────────────

    /// <summary>Length of a record that lives in the item bank (kept in a table there).</summary>
    private static int OwnLength(RomBuffer rom, int id) => rom.ReadByte(ItemBank(rom) + Lengths + id);

    /// <summary>Moves the game's item reading to an item bank (see the class notes). Records and offsets don't change.</summary>
    public static void MoveToItemBank(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's item reading code isn't the original, so items can't be moved safely.");
        if (rom.Length < Maps.MapWriter.ExpandedSize) rom.Expand(Maps.MapWriter.ExpandedSize);
        var space = Maps.ExpansionSpace.Load(rom);
        int b = space.AllocateInOneBank(0x8000, Maps.ExpansionSpace.Kind.Patch, 20);
        space.Save();
        var bank = new byte[0x8000];
        rom.ReadBytes(0x55800, 0x2800).CopyTo(bank, 0x5800);   // $D800-$FFFF at the same addresses
        int bb = SnesAddress(b) >> 16;
        var code = new List<byte>();
        var labels = new Dictionary<string, int>();
        var fix = new List<(int At, string Label)>();
        void Op(params int[] x) { foreach (int v in x) code.Add((byte)v); }
        void L(string n) => labels[n] = code.Count;
        void Br(int op, string n) { Op(op, 0); fix.Add((code.Count - 1, n)); }
        void LdaX(int addr) => Op(0xBF, addr & 0xFF, addr >> 8, bb);                 // LDA item bank:addr,X
        void Lookup() => Op(0xEB, 0xA9, 0x00, 0xEB, 0xC2, 0x20, 0x0A, 0xAA,             // (as $08:FAD0, DB = 0A)
                            0xBD, 0x00, 0xD8, 0x18, 0x69, 0x00, 0xD8, 0xAA, 0xE2, 0x20);
        // ── unpack (replaces $08:F999) ──
        L("unpack");
        Op(0x08, 0x8B, 0xE2, 0x20, 0xC2, 0x10, 0xDA, 0x5A, 0x8D, 0xA6, 0x17);         // PHP PHB SEP REP PHX PHY STA $17A6
        Op(0xA9, 0x0A, 0x48, 0xAB);                                                   // DB = 0A
        Op(0xA0, 0x50, 0x00, 0xA2, 0x00, 0x00, 0xA9, 0x00);
        L("clr"); Op(0x9D, 0xA7, 0x17, 0xE8, 0x88); Br(0xD0, "clr");
        Op(0xE2, 0x20, 0xAD, 0xA6, 0x17); Lookup();
        Op(0x8E, 0xEC, 0x14, 0xA0, 0x00, 0x00);                                       // STX $14EC; LDY #0
        L("n1"); LdaX(0x0000); Op(0xC9, 0x20); Br(0xD0, "n1a"); Op(0xA9, 0x00);
        L("n1a"); Op(0x99, 0xA7, 0x17, 0xE8, 0xC8, 0xC0, 0x0C, 0x00); Br(0xD0, "n1");
        Op(0xC8);
        L("n2"); LdaX(0x0000); Op(0x99, 0xA7, 0x17, 0xE8, 0xC8, 0xC0, 0x16, 0x00); Br(0xD0, "n2");
        Op(0xA0, 0x00, 0x00, 0xAD, 0xBC, 0x17);
        L("m1"); Op(0x4A); Br(0x90, "m2"); Op(0xEB); LdaX(0x0000);
        Op(0x18, 0x6D, 0xEC, 0x14, 0x99, 0xBD, 0x17); LdaX(0x0001);
        Op(0x6D, 0xED, 0x14, 0x99, 0xBE, 0x17, 0xEB, 0xE8, 0xE8);
        L("m2"); Op(0xC8, 0xC8, 0xC0, 0x0C, 0x00); Br(0xD0, "m1");
        L("p1"); LdaX(0x0000); Br(0xF0, "end");
        Op(0x3A, 0xEB, 0xA9, 0x00, 0xEB, 0xC2, 0x20, 0x0A, 0xA8); LdaX(0x0001);
        Op(0x99, 0xC9, 0x17, 0xE2, 0x20, 0xE8, 0xE8, 0xE8); Br(0x80, "p1");
        L("end"); Op(0x7A, 0xFA, 0xAB, 0x28, 0x6B);
        // ── type (replaces $08:FA70): A = item, returns its +0C ──
        L("type");
        Op(0x08, 0x8B, 0xE2, 0x20, 0xC2, 0x10, 0xDA, 0x48, 0xA9, 0x0A, 0x48, 0xAB, 0x68); Lookup();
        LdaX(0x000C); Op(0xFA, 0xAB, 0x28, 0x6B);
        foreach (var (at, n) in fix)
        {
            int rel = labels[n] - (at + 1);
            if (rel < -128 || rel > 127) throw new InvalidOperationException("Item bank code: branch too far.");
            code[at] = (byte)rel;
        }
        if (code.Count > MagicAt) throw new InvalidOperationException("Item bank code too long.");
        code.CopyTo(bank, CodeAt);
        Magic.CopyTo(bank, MagicAt);
        rom.WriteBytes(b, bank);
        int u = SnesAddress(b + labels["unpack"]), t = SnesAddress(b + labels["type"]);
        rom.WriteBytes(UnpackSite, new byte[] { 0x5C, (byte)u, (byte)(u >> 8), (byte)(u >> 16) });
        rom.WriteBytes(TypeSite, new byte[] { 0x5C, (byte)t, (byte)(t >> 8), (byte)(t >> 16) });
        rom.FixChecksum();
    }

    /// <summary>Writes <paramref name="record"/> into the item bank's free space as item <paramref name="id"/>.</summary>
    private static void Place(RomBuffer rom, int id, byte[] record)
    {
        int b = ItemBank(rom);
        if (record.Length > 255) throw new InvalidOperationException("An item record is at most 255 bytes.");
        var used = new List<(int S, int E)>();
        for (int i = 0; i < Count; i++)
            if (i != id && InItemBank(rom, i)) { int r = RecordOffset(rom, i) - b; used.Add((r, r + OwnLength(rom, i))); }
        int at = -1;
        for (int p = BankFree; p + record.Length <= BankFreeEnd; p++)
        {
            int q = p;
            var hit = used.Where(u => u.S < q + record.Length && u.E > q).Select(u => u.E).DefaultIfEmpty(-1).Max();
            if (hit < 0) { at = p; break; }
            p = hit - 1;
        }
        if (at < 0) throw new InvalidOperationException("The item bank is full.");
        rom.WriteBytes(b + at, record);
        rom.WriteByte(b + Lengths + id, (byte)record.Length);
        rom.WriteUInt16Le(Table + id * 2, (ushort)((0x8000 + at - 0xD800) & 0xFFFF));
        rom.FixChecksum();
    }

    /// <summary>Bytes still free in the item bank (or in bank 0A's tail without it).</summary>
    public static int Free(RomBuffer rom)
    {
        int b = ItemBank(rom);
        if (b == 0)
        {
            int usedTail = 0;
            for (int i = 0; i < Count; i++) if (RecordOffset(rom, i) >= FreeStart) usedTail += Room(rom, i);
            return FreeEnd - FreeStart - usedTail;
        }
        int used = 0;
        for (int i = 0; i < Count; i++) if (InItemBank(rom, i)) used += OwnLength(rom, i);
        return BankFreeEnd - BankFree - used;
    }

    /// <summary>Item numbers the game leaves unused ("Dummy", "Not Use"; FF stays the empty marker) that nothing gives
    /// out (shops, chests, events): they can become new items.</summary>
    public static List<int> FreeIds(RomBuffer rom, ISet<int> referenced)
    {
        var list = new List<int>();
        for (int i = 1; i < 0xFF; i++)
        {
            string n = new string(rom.ReadBytes(RecordOffset(rom, i), 12).Select(c => c == '@' ? ' ' : (char)c).ToArray()).Trim();
            if ((n.Equals("Dummy", StringComparison.OrdinalIgnoreCase) || n.Equals("Not Use", StringComparison.OrdinalIgnoreCase)) && !referenced.Contains(i))
                list.Add(i);
        }
        return list;
    }

    /// <summary>
    /// Makes item <paramref name="id"/> (a free number) a copy of <paramref name="template"/>, in the item bank. A usable
    /// item's data pointers are pointed back at the template's data in bank 0A.
    /// </summary>
    public static void NewItem(RomBuffer rom, int id, int template)
    {
        MoveToItemBank(rom);
        int src = RecordOffset(rom, template), len = Room(rom, template);
        var record = rom.ReadBytes(src, len);
        int b = ItemBank(rom);
        // where the copy will go decides its data pointers: place it first, then fix them
        Place(rom, id, record);
        int at = RecordOffset(rom, id);
        int oldAddr = Address(rom, template), newAddr = Address(rom, id);
        int mask = record[20], p = FixedSize;
        for (int bit = 0; bit < 6; bit++)
        {
            if ((mask >> bit & 1) == 0) continue;
            int rel = record[p] | record[p + 1] << 8;
            int abs = (oldAddr + rel) & 0xFFFF;
            rom.WriteUInt16Le(at + p, (ushort)((abs - newAddr) & 0xFFFF));
            p += 2;
        }
        rom.FixChecksum();
    }
}
