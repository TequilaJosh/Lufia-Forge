namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Room for event scripts that no longer fit where they were. Lufia II runs a script with the data bank set to the
/// script's own bank and reaches RAM ($7E:0000-$1FFF: flags, party, actors) through it, so scripts only work in banks
/// $80-$BF (file offsets below 0x200000), where that RAM is mirrored; the space added after 2.5 MB ($D0+) can't hold
/// them. That half of the ROM is full, so room is made by moving the packed map files of one 32 KB bank ($A8-$BF
/// hold nothing but packed files; banks of map files are taken first) to the added space (the file table points at
/// the moved copies, as when a map or a song is edited) and handing the bank to the scripts. The bank's last 16 bytes hold "LF2V" and the next free offset (u24).
/// </summary>
public static class L2EventSpace
{
    public const int Mark = 0x7FF0;
    private const int LowestBank = 0x28, HighestBank = 0x3F;   // file banks 0x28-0x3F = $A8-$BF

    /// <summary>Reserves <paramref name="length"/> bytes in one bank of the event space, making a new region when none
    /// has room. Returns the file offset.</summary>
    public static int Allocate(RomBuffer rom, int length)
    {
        if (length <= 0 || length > Mark) throw new ArgumentOutOfRangeException(nameof(length));
        foreach (int bank in Regions(rom))
        {
            int next = (int)rom.ReadUInt24Le(bank + Mark + 4);
            if (next + length <= bank + Mark) { WriteMark(rom, bank, next + length); return next; }
        }
        int fresh = MakeRegion(rom) ?? throw new InvalidOperationException("There is no bank left that can be cleared for event scripts.");
        WriteMark(rom, fresh, fresh + length);
        return fresh;
    }

    /// <summary>Grows a piece reserved last, when its bank has the room (false: reserve a new one).</summary>
    public static bool Grow(RomBuffer rom, int at, int length, int newLength)
    {
        int bank = at / 0x8000 * 0x8000;
        if (!IsRegion(rom, bank) || rom.ReadUInt24Le(bank + Mark + 4) != at + length || at + newLength > bank + Mark) return false;
        WriteMark(rom, bank, at + newLength);
        return true;
    }

    /// <summary>Gives back a piece reserved last (anything else stays used).</summary>
    public static void Release(RomBuffer rom, int at, int length)
    {
        int bank = at / 0x8000 * 0x8000;
        if (IsRegion(rom, bank) && rom.ReadUInt24Le(bank + Mark + 4) == at + length) WriteMark(rom, bank, at);
    }

    /// <summary>Whether a file offset lies in the event space.</summary>
    public static bool Contains(RomBuffer rom, int at) => at < 0x200000 && IsRegion(rom, at / 0x8000 * 0x8000);

    /// <summary>Banks already handed to the scripts (file offsets of their starts).</summary>
    public static List<int> Regions(RomBuffer rom)
    {
        var list = new List<int>();
        for (int b = HighestBank; b >= LowestBank; b--)
            if (IsRegion(rom, b * 0x8000)) list.Add(b * 0x8000);
        return list;
    }

    /// <summary>Bytes still free in the regions made so far.</summary>
    public static int Free(RomBuffer rom) => Regions(rom).Sum(b => b + Mark - (int)rom.ReadUInt24Le(b + Mark + 4));

    private static bool IsRegion(RomBuffer rom, int bank) =>
        bank + Mark + 8 <= rom.Length && rom.ReadByte(bank + Mark) == 'L' && rom.ReadByte(bank + Mark + 1) == 'F' && rom.ReadByte(bank + Mark + 2) == '2' &&
        rom.ReadByte(bank + Mark + 3) == 'V' && rom.ReadUInt24Le(bank + Mark + 4) is var n && n >= bank && n <= bank + Mark;

    private static void WriteMark(RomBuffer rom, int bank, int next) =>
        rom.WriteBytes(bank + Mark, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'V', (byte)next, (byte)(next >> 8), (byte)(next >> 16), (byte)0 });

    /// <summary>Clears a bank whose bytes all belong to packed map files: the files move to the added space (as they are,
    /// still packed) and the bank becomes event space. Returns its file offset, or null when no bank qualifies.</summary>
    private static int? MakeRegion(RomBuffer rom)
    {
        var files = new List<(int Id, int At, int Size)>();
        for (int id = 0; id < L2Rom.FileCount; id++)
        {
            int at = L2Rom.FileOffset(rom, id);
            if (at >= 0x200000 || at < 0) continue;
            try { LufiaCompression.Decompress(rom, at, out int size); files.Add((id, at, size)); }
            catch (Exception) { }
        }
        int firstMap = L2Maps.MapFileBase + 2, lastMap = L2Maps.BlockSetFileBase - 1;   // 3F and 40 are the licence screen and a font
        // banks of map files first (the map editor moves those files too), then any bank of packed files
        foreach (bool mapsOnly in new[] { true, false })
        for (int b = HighestBank; b >= LowestBank; b--)
        {
            int bank = b * 0x8000;
            if (IsRegion(rom, bank)) continue;
            var touching = files.Where(f => f.At < bank + 0x8000 && f.At + f.Size > bank).ToList();
            if (touching.Count == 0 || mapsOnly && touching.Any(f => f.Id < firstMap || f.Id > lastMap)) continue;
            // every byte of the bank must belong to one of them (or be empty), so nothing else lives there
            var used = new bool[0x8000];
            foreach (var f in touching)
                for (int p = Math.Max(f.At, bank); p < Math.Min(f.At + f.Size, bank + 0x8000); p++) used[p - bank] = true;
            bool clean = true;
            for (int i = 0; i < 0x8000 && clean; i++) if (!used[i] && rom.ReadByte(bank + i) is not (0x00 or 0xFF)) clean = false;
            if (!clean) continue;
            foreach (var f in touching)
            {
                int to = L2Rom.Allocate(rom, f.Size);
                rom.WriteBytes(to, rom.ReadBytes(f.At, f.Size));
                // the old copy is dead now, also where it runs into the next bank (so that bank can be cleared later too)
                rom.WriteBytes(f.At, new byte[f.Size]);
                int rel = to - L2Rom.FileTable;
                // every table entry pointing at this file (ids can share one)
                for (int id = 0; id < L2Rom.FileCount; id++)
                    if (L2Rom.FileOffset(rom, id) == f.At) rom.WriteBytes(L2Rom.FileTable + id * 3, new[] { (byte)rel, (byte)(rel >> 8), (byte)(rel >> 16) });
            }
            rom.WriteBytes(bank, new byte[0x8000]);
            WriteMark(rom, bank, bank);
            return bank;
        }
        return null;
    }
}
