namespace LufiaForge.Core.Battle;

/// <summary>
/// Item records: u16 offsets from $0A:D800 (file 0x55800), read only by $08:FAD0 (16-bit ADC #$D800, data bank 0A),
/// so a record can be anywhere in $0A:8000-$FFFF. An equipment record is 21 fixed bytes and a list of (id, a, b)
/// properties ended by 00; records are packed back to back. A record whose list grows moves to the free end of bank
/// 0A (file 0x57D4B-0x57FFF, zeros in the game).
/// </summary>
public static class ItemTable
{
    public const int Table = 0x55800, Count = 256, FixedSize = 21;
    public const int FreeStart = 0x57D4B, FreeEnd = 0x58000;

    public static int RecordOffset(RomBuffer rom, int id) => Table + rom.ReadUInt16Le(Table + id * 2);

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

    /// <summary>Where the next record starts (the room this one has), or the record's own end for moved records.</summary>
    private static int Room(RomBuffer rom, int id)
    {
        int o = RecordOffset(rom, id), next = int.MaxValue;
        for (int i = 0; i < Count; i++) { int r = RecordOffset(rom, i); if (r > o && r < next) next = r; }
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
            while (FixedSize + tail.Count < room && o < FreeStart) tail.Add(0);
            rom.WriteBytes(o + FixedSize, tail.ToArray());
            rom.FixChecksum();
            return;
        }
        var record = rom.ReadBytes(o, FixedSize).Concat(tail).ToArray();
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
}
