namespace LufiaForge.Core.Battle;

// item records: u16 offsets from $0A:D800 (file 0x55800). the game only reads records in $08:F999 (unpack) and
// $08:FA70 (type byte), both through $08:FAD0 (16-bit $D800 + offset, data bank 0A). an equipment record is 21 fixed
// bytes plus a list of (id, a, b) properties ended by 00. a usable item has a mask at +20 and record-relative data
// pointers that the battle reads in bank 0A ($08:C2DD, $08:EED4). records are packed back to back.
// without the item bank patch, a record whose list grows moves to the free end of bank 0A (file 0x57D4B-0x57FFF).
// with it (MoveToItemBank) the two readers get replaced (JML) by copies that read records from an item bank in the
// added space. that bank holds a copy of bank 0A's $D800-$FFFF at the same addresses (so every record and offset
// stays valid) and $8200-$D7FF is free for grown and new records (their offsets wrap: $D800 + offset).
// usable items' data pointers keep pointing at the data in bank 0A
public static class ItemTable
{
    public const int Table = 0x55800, Count = 256, FixedSize = 21;
    public const int FreeStart = 0x57D4B, FreeEnd = 0x58000;
    private const int UnpackSite = 0x47999, TypeSite = 0x47A70;
    private static readonly byte[] UnpackOriginal = Convert.FromHexString("088BE220C210DA5A"), TypeOriginal = Convert.FromHexString("088BE220C210DA48");
    private const int CodeAt = 0x0000, MagicAt = 0x00F0, Lengths = 0x0100, BankFree = 0x0200, BankFreeEnd = 0x5800;
    private static readonly byte[] Magic = "LFIB"u8.ToArray();
    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);
    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);

    // file offset of the item bank (0 = not moved)
    public static int ItemBank(RomBuffer rom)
    {
        if (rom.ReadByte(UnpackSite) != 0x5C) return 0;
        int bankOffset = FileOffset(rom.ReadByte(UnpackSite + 1) | rom.ReadByte(UnpackSite + 2) << 8 | rom.ReadByte(UnpackSite + 3) << 16) & ~0x7FFF;
        return bankOffset > 0 && bankOffset + 0x8000 <= rom.Length && rom.ReadBytes(bankOffset + MagicAt, 4).SequenceEqual(Magic) ? bankOffset : 0;
    }
    public static bool IsMoved(RomBuffer rom) => ItemBank(rom) > 0;
    public static bool IsOriginal(RomBuffer rom) => rom.ReadBytes(UnpackSite, 8).SequenceEqual(UnpackOriginal) && rom.ReadBytes(TypeSite, 8).SequenceEqual(TypeOriginal);

    // the 16-bit address the game reads a record at ($D800 + offset, wrapping)
    private static int Address(RomBuffer rom, int id) => (0xD800 + rom.ReadUInt16Le(Table + id * 2)) & 0xFFFF;
    // a record that only lives in the item bank (grown or new), not in bank 0A
    public static bool InItemBank(RomBuffer rom, int id) => IsMoved(rom) && Address(rom, id) < 0xD800;

    // file offset of the record the game reads (bank 0A, or the item bank for grown / new records)
    public static int RecordOffset(RomBuffer rom, int id)
    {
        int address = Address(rom, id);
        return InItemBank(rom, id) ? ItemBank(rom) + (address - 0x8000) : 0x50000 + (address - 0x8000);
    }

    // after editing a record in bank 0A: the item bank's copy (the one the game reads) gets the same bytes
    public static void Sync(RomBuffer rom, int id)
    {
        int bankOffset = ItemBank(rom);
        if (bankOffset == 0 || InItemBank(rom, id)) return;
        int recordOffset = RecordOffset(rom, id), length = Room(rom, id);
        rom.WriteBytes(bankOffset + (recordOffset - 0x50000), rom.ReadBytes(recordOffset, length));
    }

    // the record's properties, or null when it has no plain property list (usable items)
    public static List<(int Id, int A, int B)>? Properties(RomBuffer rom, int id)
    {
        int recordOffset = RecordOffset(rom, id), propertyAt = recordOffset + FixedSize;
        // byte +20 is a mask of extra data pointers (usable items), those records aren't a plain list
        if (rom.ReadByte(recordOffset + 20) != 0) return null;
        var list = new List<(int, int, int)>();
        for (int n = 0; n < 16; n++, propertyAt += 3)
        {
            if (propertyAt >= rom.Length) return null;
            int propertyId = rom.ReadByte(propertyAt);
            if (propertyId == 0) return list;
            list.Add((propertyId, rom.ReadByte(propertyAt + 1), rom.ReadByte(propertyAt + 2)));
        }
        return null;
    }

    // the bytes a record has (up to the next record, or its own length)
    public static int Length(RomBuffer rom, int id) => Room(rom, id);

    // where the next record starts (the room this one has), or the record's own end for moved records
    private static int Room(RomBuffer rom, int id)
    {
        int recordOffset = RecordOffset(rom, id), next = int.MaxValue;
        if (InItemBank(rom, id)) return OwnLength(rom, id);
        for (int i = 0; i < Count; i++) { if (InItemBank(rom, i)) continue; int otherOffset = RecordOffset(rom, i); if (otherOffset > recordOffset && otherOffset < next) next = otherOffset; }
        if (recordOffset >= FreeStart || next == int.MaxValue) return FixedSize + ((Properties(rom, id)?.Count ?? 0) * 3) + 1;
        return next - recordOffset;
    }

    // writes a record's property list: in place when it fits, otherwise the record moves to the end of bank 0A
    public static void SetProperties(RomBuffer rom, int id, IReadOnlyList<(int Id, int A, int B)> props)
    {
        if (Properties(rom, id) == null) throw new InvalidOperationException("This item has no property list.");
        if (props.Any(p => p.Id <= 0 || p.Id >= 0x18)) throw new InvalidOperationException("Property ids are 01-17 (higher ones would write over the game's memory).");
        var tail = new List<byte>();
        foreach (var (propertyId, a, b) in props) { tail.Add((byte)propertyId); tail.Add((byte)a); tail.Add((byte)b); }
        tail.Add(0);
        int recordOffset = RecordOffset(rom, id);
        int room = Room(rom, id);
        if (FixedSize + tail.Count <= room)
        {
            // (zeros up to the next record so the editor still sees a plain property list)
            while (FixedSize + tail.Count < room && recordOffset < FreeStart && !InItemBank(rom, id)) tail.Add(0);
            rom.WriteBytes(recordOffset + FixedSize, tail.ToArray());
            Sync(rom, id);
            rom.FixChecksum();
            return;
        }
        var record = rom.ReadBytes(recordOffset, FixedSize).Concat(tail).ToArray();
        // the item bank gives us 22 KB instead of bank 0A's last 693 bytes
        if (!IsMoved(rom) && IsOriginal(rom)) MoveToItemBank(rom);
        if (IsMoved(rom)) { Place(rom, id, record); return; }
        int newOffset = FindFree(rom, record.Length, id);
        if (newOffset < 0) throw new InvalidOperationException("No room left at the end of bank 0A for a longer item record.");
        rom.WriteBytes(newOffset, record);
        rom.WriteUInt16Le(Table + id * 2, (ushort)(newOffset - Table));
        rom.FixChecksum();
    }

    private static int FindFree(RomBuffer rom, int length, int skip)
    {
        var used = new List<(int S, int E)>();
        for (int i = 0; i < Count; i++)
        {
            if (i == skip) continue;
            int recordOffset = RecordOffset(rom, i);
            if (recordOffset >= FreeStart) used.Add((recordOffset, recordOffset + Room(rom, i)));
        }
        used.AddRange(RomIdentity.ForeignRanges(rom, FreeStart, FreeEnd));   // whatever another hack put there
        for (int start = FreeStart; start + length <= FreeEnd; start++)
        {
            int q = start;
            var blockedUntil = used.Where(u => u.S < q + length && u.E > q).Select(u => u.E).DefaultIfEmpty(-1).Max();
            if (blockedUntil < 0) return start;
            start = blockedUntil - 1;
        }
        return -1;
    }

    // the item bank

    // length of a record that lives in the item bank (kept in a table there)
    private static int OwnLength(RomBuffer rom, int id) => rom.ReadByte(ItemBank(rom) + Lengths + id);

    // moves the game's item reading to an item bank (see the notes at the top). records and offsets don't change
    public static void MoveToItemBank(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's item reading code isn't the original, so items can't be moved safely.");
        if (rom.Length < Maps.MapWriter.ExpandedSize) rom.Expand(Maps.MapWriter.ExpandedSize);
        var freeSpace = Maps.ExpansionSpace.Load(rom);
        int bankOffset = freeSpace.AllocateInOneBank(0x8000, Maps.ExpansionSpace.Kind.Patch, 20);
        freeSpace.Save();
        var bank = new byte[0x8000];
        rom.ReadBytes(0x55800, 0x2800).CopyTo(bank, 0x5800);   // $D800-$FFFF at the same addresses
        int bankByte = SnesAddress(bankOffset) >> 16;
        var code = new List<byte>();
        var labels = new Dictionary<string, int>();
        var fixups = new List<(int At, string Label)>();
        void Op(params int[] bytes) { foreach (int v in bytes) code.Add((byte)v); }
        void L(string label) => labels[label] = code.Count;
        void Br(int op, string label) { Op(op, 0); fixups.Add((code.Count - 1, label)); }
        void LdaX(int addr) => Op(0xBF, addr & 0xFF, addr >> 8, bankByte);                 // LDA item bank:addr,X
        void Lookup() => Op(0xEB, 0xA9, 0x00, 0xEB, 0xC2, 0x20, 0x0A, 0xAA,             // (same as $08:FAD0, DB = 0A)
                            0xBD, 0x00, 0xD8, 0x18, 0x69, 0x00, 0xD8, 0xAA, 0xE2, 0x20);
        // unpack (replaces $08:F999)
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
        // type (replaces $08:FA70): A = item, returns its +0C
        L("type");
        Op(0x08, 0x8B, 0xE2, 0x20, 0xC2, 0x10, 0xDA, 0x48, 0xA9, 0x0A, 0x48, 0xAB, 0x68); Lookup();
        LdaX(0x000C); Op(0xFA, 0xAB, 0x28, 0x6B);
        foreach (var (fixupAt, label) in fixups)
        {
            int distance = labels[label] - (fixupAt + 1);
            if (distance < -128 || distance > 127) throw new InvalidOperationException("Item bank code: branch too far.");
            code[fixupAt] = (byte)distance;
        }
        if (code.Count > MagicAt) throw new InvalidOperationException("Item bank code too long.");
        code.CopyTo(bank, CodeAt);
        Magic.CopyTo(bank, MagicAt);
        rom.WriteBytes(bankOffset, bank);
        int unpackAddress = SnesAddress(bankOffset + labels["unpack"]), typeAddress = SnesAddress(bankOffset + labels["type"]);
        rom.WriteBytes(UnpackSite, new byte[] { 0x5C, (byte)unpackAddress, (byte)(unpackAddress >> 8), (byte)(unpackAddress >> 16) });
        rom.WriteBytes(TypeSite, new byte[] { 0x5C, (byte)typeAddress, (byte)(typeAddress >> 8), (byte)(typeAddress >> 16) });
        rom.FixChecksum();
    }

    // writes record into the item bank's free space as item id
    private static void Place(RomBuffer rom, int id, byte[] record)
    {
        int bankOffset = ItemBank(rom);
        if (record.Length > 255) throw new InvalidOperationException("An item record is at most 255 bytes.");
        var used = new List<(int S, int E)>();
        for (int i = 0; i < Count; i++)
            if (i != id && InItemBank(rom, i)) { int offsetInBank = RecordOffset(rom, i) - bankOffset; used.Add((offsetInBank, offsetInBank + OwnLength(rom, i))); }
        int placeAt = -1;
        for (int start = BankFree; start + record.Length <= BankFreeEnd; start++)
        {
            int q = start;
            var blockedUntil = used.Where(u => u.S < q + record.Length && u.E > q).Select(u => u.E).DefaultIfEmpty(-1).Max();
            if (blockedUntil < 0) { placeAt = start; break; }
            start = blockedUntil - 1;
        }
        if (placeAt < 0) throw new InvalidOperationException("The item bank is full.");
        rom.WriteBytes(bankOffset + placeAt, record);
        rom.WriteByte(bankOffset + Lengths + id, (byte)record.Length);
        rom.WriteUInt16Le(Table + id * 2, (ushort)((0x8000 + placeAt - 0xD800) & 0xFFFF));
        rom.FixChecksum();
    }

    // bytes still free in the item bank (or in bank 0A's tail without it)
    public static int Free(RomBuffer rom)
    {
        int bankOffset = ItemBank(rom);
        if (bankOffset == 0)
        {
            int usedTail = 0;
            for (int i = 0; i < Count; i++) if (RecordOffset(rom, i) >= FreeStart) usedTail += Room(rom, i);
            return FreeEnd - FreeStart - usedTail;
        }
        int used = 0;
        for (int i = 0; i < Count; i++) if (InItemBank(rom, i)) used += OwnLength(rom, i);
        return BankFreeEnd - BankFree - used;
    }

    // item numbers the game leaves unused ("Dummy", "Not Use", FF stays the empty marker) that nothing gives
    // out (shops, chests, events), so they can become new items
    public static List<int> FreeIds(RomBuffer rom, ISet<int> referenced)
    {
        var list = new List<int>();
        for (int i = 1; i < 0xFF; i++)
        {
            string name = new string(rom.ReadBytes(RecordOffset(rom, i), 12).Select(c => c == '@' ? ' ' : (char)c).ToArray()).Trim();
            if ((name.Equals("Dummy", StringComparison.OrdinalIgnoreCase) || name.Equals("Not Use", StringComparison.OrdinalIgnoreCase)) && !referenced.Contains(i))
                list.Add(i);
        }
        return list;
    }

    // makes item id (a free number) a copy of template, in the item bank. a usable
    // item's data pointers get pointed back at the template's data in bank 0A
    public static void NewItem(RomBuffer rom, int id, int template)
    {
        MoveToItemBank(rom);
        int templateOffset = RecordOffset(rom, template), length = Room(rom, template);
        var record = rom.ReadBytes(templateOffset, length);
        int bankOffset = ItemBank(rom);
        // where the copy goes decides its data pointers, so place it first and then fix them
        Place(rom, id, record);
        int recordOffset = RecordOffset(rom, id);
        int oldAddr = Address(rom, template), newAddr = Address(rom, id);
        int mask = record[20], pointerAt = FixedSize;
        for (int bit = 0; bit < 6; bit++)
        {
            if ((mask >> bit & 1) == 0) continue;
            int relative = record[pointerAt] | record[pointerAt + 1] << 8;
            int absolute = (oldAddr + relative) & 0xFFFF;
            rom.WriteUInt16Le(recordOffset + pointerAt, (ushort)((absolute - newAddr) & 0xFFFF));
            pointerAt += 2;
        }
        rom.FixChecksum();
    }
}
