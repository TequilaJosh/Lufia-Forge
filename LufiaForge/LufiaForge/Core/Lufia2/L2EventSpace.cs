namespace LufiaForge.Core.Lufia2;

// room for event scripts that don't fit where they were any more. lufia 2 runs a script with the data bank set to the
// script's own bank and reaches RAM ($7E:0000-$1FFF: flags, party, actors) through it, so scripts only work in banks
// $80-$BF (file offsets below 0x200000), where that RAM is mirrored. the space added after 2.5 MB ($D0+) can't hold
// them. that half of the rom is full, so we make room by moving the packed map files of one 32 KB bank ($A8-$BF hold
// nothing but packed files, banks of map files get taken first) to the added space (the file table points at the
// moved copies, same as when a map or a song gets edited) and handing the bank to the scripts. the bank's last 16
// bytes hold "LF2V" and the next free offset (u24)
// on an 8 MB rom (L2Rom.ExpandTo8) banks $01-$3F of the upper 4 MB see that RAM too, and the map header's bank ($87 +
// a byte, added in 8 bits at $83:B5BB and $80:BEBA) reaches them, so empty banks there get taken first and nothing
// has to move
public static class L2EventSpace
{
    public const int Mark = 0x7FF0;
    private const int LowestBank = 0x28, HighestBank = 0x3F;   // file banks 0x28-0x3F = $A8-$BF
    private const int UpperFirst = L2Rom.UpperStart + 0x8000, UpperEnd = L2Rom.UpperStart + 0x200000;   // banks $01-$3F

    // reserves length bytes in one bank of the event space, making a new region if none has room. returns the file offset
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

    // grows a piece reserved last, if its bank has the room (false = reserve a new one)
    public static bool Grow(RomBuffer rom, int at, int length, int newLength)
    {
        int bank = at / 0x8000 * 0x8000;
        if (!IsRegion(rom, bank) || rom.ReadUInt24Le(bank + Mark + 4) != at + length || at + newLength > bank + Mark) return false;
        WriteMark(rom, bank, at + newLength);
        return true;
    }

    // gives back a piece reserved last (anything else stays used)
    public static void Release(RomBuffer rom, int at, int length)
    {
        int bank = at / 0x8000 * 0x8000;
        if (IsRegion(rom, bank) && rom.ReadUInt24Le(bank + Mark + 4) == at + length) WriteMark(rom, bank, at);
    }

    // whether a file offset lies in the event space
    public static bool Contains(RomBuffer rom, int at) => (at < 0x200000 || at >= UpperFirst && at < UpperEnd) && IsRegion(rom, at / 0x8000 * 0x8000);

    // banks already handed to the scripts (file offsets of their starts), the ones in the upper 4 MB first
    public static List<int> Regions(RomBuffer rom)
    {
        var list = new List<int>();
        if (L2Rom.IsExLoRom(rom))
            for (int bank = UpperFirst; bank < UpperEnd; bank += 0x8000)
                if (IsRegion(rom, bank)) list.Add(bank);
        for (int bank = HighestBank; bank >= LowestBank; bank--)
            if (IsRegion(rom, bank * 0x8000)) list.Add(bank * 0x8000);
        return list;
    }

    // banks of the upper 4 MB that can still become event space (all one value), 0 on a smaller rom
    public static int UpperBanksLeft(RomBuffer rom) => !L2Rom.IsExLoRom(rom) ? 0 :
        Enumerable.Range(0, (UpperEnd - UpperFirst) / 0x8000).Count(i => !IsRegion(rom, UpperFirst + i * 0x8000) && Empty(rom, UpperFirst + i * 0x8000));

    // whether a 32 KB bank is all one value (00 or FF): nothing lives there (data banks take these too)
    internal static bool IsEmptyBank(RomBuffer rom, int bank) => bank + 0x8000 <= rom.Length && Empty(rom, bank);

    private static bool Empty(RomBuffer rom, int bank)
    {
        byte fill = rom.ReadByte(bank);
        if (fill is not (0x00 or 0xFF)) return false;
        for (int i = 1; i < 0x8000; i++) if (rom.ReadByte(bank + i) != fill) return false;
        return true;
    }

    // bytes still free in the regions made so far
    public static int Free(RomBuffer rom) => Regions(rom).Sum(b => b + Mark - (int)rom.ReadUInt24Le(b + Mark + 4));

    private static bool IsRegion(RomBuffer rom, int bank) =>
        bank + Mark + 8 <= rom.Length && rom.ReadByte(bank + Mark) == 'L' && rom.ReadByte(bank + Mark + 1) == 'F' && rom.ReadByte(bank + Mark + 2) == '2' &&
        rom.ReadByte(bank + Mark + 3) == 'V' && rom.ReadUInt24Le(bank + Mark + 4) is var next && next >= bank && next <= bank + Mark;

    private static void WriteMark(RomBuffer rom, int bank, int next) =>
        rom.WriteBytes(bank + Mark, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'V', (byte)next, (byte)(next >> 8), (byte)(next >> 16), (byte)0 });

    // clears a bank whose bytes all belong to packed map files: the files move to the added space (as they are, still
    // packed) and the bank becomes event space. returns its file offset, or null if no bank qualifies
    private static int? MakeRegion(RomBuffer rom)
    {
        // an 8 MB rom: an empty bank of the upper 4 MB
        if (L2Rom.IsExLoRom(rom))
            for (int bank = UpperFirst; bank < UpperEnd; bank += 0x8000)
                if (!IsRegion(rom, bank) && Empty(rom, bank))
                {
                    rom.WriteBytes(bank, new byte[0x8000]);
                    WriteMark(rom, bank, bank);
                    return bank;
                }
        var files = new List<(int Id, int At, int Size)>();
        foreach (int id in L2Rom.FileIds(rom))
        {
            int fileAt = L2Rom.FileOffset(rom, id);
            if (fileAt >= 0x200000 || fileAt < 0) continue;
            try { LufiaCompression.Decompress(rom, fileAt, out int size); files.Add((id, fileAt, size)); }
            catch (Exception) { }
        }
        int firstMap = L2Maps.MapFileBase + 2, lastMap = L2Maps.BlockSetFileBase - 1;   // 3F and 40 are the licence screen and a font
        bool IsMap(int id) => id >= firstMap && id <= lastMap || id >= L2NewMaps.NewFileBase;
        // banks of map files first (the map editor moves those files too), then any bank of packed files
        foreach (bool mapsOnly in new[] { true, false })
        for (int fileBank = HighestBank; fileBank >= LowestBank; fileBank--)
        {
            int bank = fileBank * 0x8000;
            if (IsRegion(rom, bank)) continue;
            var touching = files.Where(f => f.At < bank + 0x8000 && f.At + f.Size > bank).ToList();
            if (touching.Count == 0 || mapsOnly && touching.Any(f => !IsMap(f.Id))) continue;
            // every byte of the bank has to belong to one of them (or be empty), so nothing else lives there
            var used = new bool[0x8000];
            foreach (var file in touching)
                for (int position = Math.Max(file.At, bank); position < Math.Min(file.At + file.Size, bank + 0x8000); position++) used[position - bank] = true;
            bool clean = true;
            for (int i = 0; i < 0x8000 && clean; i++) if (!used[i] && rom.ReadByte(bank + i) is not (0x00 or 0xFF)) clean = false;
            if (!clean) continue;
            foreach (var file in touching)
            {
                int to = L2Rom.Allocate(rom, file.Size);
                rom.WriteBytes(to, rom.ReadBytes(file.At, file.Size));
                // the old copy is dead now, also where it runs into the next bank (so that bank can be cleared later too)
                rom.WriteBytes(file.At, new byte[file.Size]);
                // every table entry pointing at this file (ids can share one)
                foreach (int id in L2Rom.FileIds(rom))
                    if (L2Rom.FileOffset(rom, id) == file.At) L2Rom.SetFileOffset(rom, id, to);
            }
            rom.WriteBytes(bank, new byte[0x8000]);
            WriteMark(rom, bank, bank);
            return bank;
        }
        return null;
    }
}
