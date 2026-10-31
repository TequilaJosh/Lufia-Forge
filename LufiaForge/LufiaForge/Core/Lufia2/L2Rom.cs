namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II: Rise of the Sinistrals (USA), 2.5 MB LoROM. Addresses from LEdit (pdinklag, researched on the German
/// ROM) checked on the US ROM (CRC of the clean dump: checksum $BA68).
/// <para>
/// Files: graphics, maps and music sit in 0x2A9 packed "files" reached through a table at $A7:8000 (file 0x138000):
/// 3 bytes each, the file at 0x138000 + that value. They're packed the same way as Lufia 1's resources
/// (<see cref="LufiaCompression"/>), so Lufia Forge reads and writes them with the same code.
/// </para>
/// </summary>
public static class L2Rom
{
    public const int FileTable = 0x138000, FileCount = 0x2A8;

    /// <summary>A SNES address as a file offset. Banks $80-$FF read the first 4 MB; banks $01-$7D (upper halves) read
    /// the upper 4 MB of an 8 MB ROM (<see cref="ExpandTo8"/>); bank $00 is the copy of the first bank.</summary>
    public static int SnesToFile(int snes)
    {
        int bank = snes >> 16 & 0xFF;
        return bank is >= 1 and < 0x7E && (snes & 0x8000) != 0 ? UpperStart + bank * 0x8000 + (snes & 0x7FFF) : (bank & 0x7F) * 0x8000 + (snes & 0x7FFF);
    }

    public static int FileToSnes(int file) =>
        file >= UpperStart ? ((file - UpperStart) / 0x8000) << 16 | (file % 0x8000 | 0x8000) : (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    /// <summary>File offset of the byte <paramref name="offset"/> on from file offset <paramref name="file"/> the way the
    /// game adds a record's u16 offsets: a 16-bit sum in the same bank ($0A42 + Y, INC $BB, the shop pointer...), so an
    /// offset past the bank's end comes round to its start (the fan hacks' monster records in bank $D0 use offsets like
    /// $FF79, which is 0x87 bytes back). Banks $C0-$FF show the ROM at $0000-$7FFF as well; in banks $80-$BF that is
    /// RAM, so -1.</summary>
    public static int InBank(int file, int offset)
    {
        int snes = FileToSnes(file), bank = snes >> 16, addr = (snes + offset) & 0xFFFF;
        if (addr < 0x8000 && bank < 0xC0) return -1;
        return SnesToFile(bank << 16 | addr);
    }

    /// <summary>The u16 the game adds to <paramref name="from"/> to reach <paramref name="to"/> (same bank).</summary>
    public static int OffsetInBank(int from, int to) => (FileToSnes(to) - FileToSnes(from)) & 0xFFFF;

    /// <summary>File offset of packed file <paramref name="id"/>.</summary>
    public static int FileOffset(RomBuffer rom, int id) =>
        FileTable + rom.ReadByte(FileTable + id * 3) + (rom.ReadByte(FileTable + id * 3 + 1) << 8) + (rom.ReadByte(FileTable + id * 3 + 2) << 16);

    /// <summary>Packed file <paramref name="id"/>, unpacked.</summary>
    public static byte[] ReadFile(RomBuffer rom, int id) => LufiaCompression.Decompress(rom, FileOffset(rom, id), out _);

    /// <summary>Space Lufia Forge adds after the game's 2.5 MB: the ROM grows to 3 MB the first time something doesn't
    /// fit where it was, and to 4 MB (the most a LoROM cartridge addresses; the header already says 4 MB) when that is
    /// full. The space used is the largest empty run (all 00 or all FF) past 2.5 MB, so the data of other hacks in
    /// that part of the ROM (Frue, Spekkio and Kureji Lufia use 0x280000-0x30DAC2 and 0x3FFC00-) is left alone. The
    /// last 16 bytes of the run hold "LF2X" and the next free offset (u24); on a ROM Lufia Forge grew itself that is
    /// at 0x2FFFF0 (<see cref="SpaceMark"/>).</summary>
    public const int OriginalSize = 0x280000, ExpandedSize = 0x300000, MaxSize = 0x400000, SpaceMark = ExpandedSize - 16;

    /// <summary>
    /// 8 MB: past 4 MB a LoROM cartridge can be wired as "ExLoROM": banks $80-$FF read the first 4 MB as before and
    /// banks $00-$7D (addresses $8000-$FFFF) the upper 4 MB. The game runs in $80-$FF, so it is unchanged; only the
    /// console's start and its interrupts go through bank $00, which holds a copy of the first bank
    /// (<see cref="RomBuffer.IsExLoRomCopy"/>). Measured in BizHawk (2026-10-27): the game reads nothing else from
    /// banks $00-$7D. Banks $01-$3F also see the RAM at $0000-$1FFF, like $80-$BF, so event scripts can live there
    /// (<see cref="L2EventSpace"/>); banks $40-$7D hold other data (<see cref="Allocate"/> with upper: true).
    /// Works in Snes9x, bsnes / higan / BizHawk's "BSNESv115+" core and others that know ExLoROM; BizHawk's older
    /// "BSNES" core can't run it.
    /// </summary>
    public const int UpperStart = 0x400000, ExSize = RomBuffer.ExLoRomSize, UpperDataStart = 0x600000, UpperDataEnd = 0x7F0000,
        UpperMark = UpperDataEnd - 16;

    /// <summary>Whether the ROM is 8 MB (ExLoROM).</summary>
    public static bool IsExLoRom(RomBuffer rom) => rom.IsExLoRomCopy();

    /// <summary>Why the ROM can't be made 8 MB, or null.</summary>
    public static string? CantExpandTo8(RomBuffer rom)
    {
        if (IsExLoRom(rom)) return "The ROM is already 8 MB.";
        if (rom.Length > MaxSize) return "The ROM is bigger than 4 MB already (another hack's layout), so Lufia Forge leaves its size alone.";
        if (rom.ReadByte(0x7FD5) is var m && (m & 0x0F) != 0) return $"The ROM's header says map mode {m:X2}, not a LoROM game.";
        return null;
    }

    /// <summary>Makes the ROM 8 MB (ExLoROM): 4 MB first if it is smaller, then the upper 4 MB, empty but for the copy
    /// of the first bank; the header says 8 MB.</summary>
    public static void ExpandTo8(RomBuffer rom)
    {
        if (CantExpandTo8(rom) is { } why) throw new InvalidOperationException(why);
        if (rom.Length < MaxSize) rom.Expand(MaxSize);
        rom.Expand(ExSize, 0x00);
        rom.WriteBytes(UpperStart, rom.ReadBytes(0, 0x8000));
        rom.FixChecksum();
    }

    /// <summary>Bytes still free in the upper 4 MB (data banks $40-$7D), 0 on a smaller ROM.</summary>
    public static int UpperFree(RomBuffer rom) =>
        !IsExLoRom(rom) ? 0 : IsMark(rom, UpperMark) ? UpperMark - (int)rom.ReadUInt24Le(UpperMark + 4) : UpperMark - UpperDataStart;

    /// <summary>Reserves <paramref name="length"/> bytes of added space, never across a 32 KB bank (so the game reads
    /// them as one piece), growing the ROM when needed. Returns the file offset. <paramref name="upper"/>: the data is
    /// read through a full 24-bit address, so on an 8 MB ROM it goes in the upper 4 MB (banks $40-$7D) first.</summary>
    public static int Allocate(RomBuffer rom, int length, bool upper = false)
    {
        if (length <= 0 || length > 0x8000) throw new ArgumentOutOfRangeException(nameof(length));
        if (upper && IsExLoRom(rom))
        {
            int up = IsMark(rom, UpperMark) ? (int)rom.ReadUInt24Le(UpperMark + 4) : UpperDataStart;
            if (up / 0x8000 != (up + length - 1) / 0x8000) up = (up / 0x8000 + 1) * 0x8000;
            if (up + length <= UpperMark)
            {
                WriteMark(rom, UpperMark, up + length);
                return up;
            }
        }
        if (rom.Length < ExpandedSize) rom.Expand(ExpandedSize);
        var (mark, next) = FindMark(rom) ?? NewSpace(rom, OriginalSize) ?? throw new InvalidOperationException("The ROM has no empty space left.");
        while (true)
        {
            int at = next;
            if (at / 0x8000 != (at + length - 1) / 0x8000) at = (at / 0x8000 + 1) * 0x8000;
            if (at + length <= mark)
            {
                WriteMark(rom, mark, at + length);
                return at;
            }
            // this run is full: grow the ROM if it can, and carry on in the next empty run
            if (rom.Length < MaxSize) rom.Expand(MaxSize);
            var more = NewSpace(rom, mark + 16) ?? throw new InvalidOperationException(IsExLoRom(rom)
                ? "The first 4 MB of the ROM are full (what goes there can't use the upper 4 MB)."
                : "The ROM is full at 4 MB. Lufia II > Settings > Make the ROM 8 MB gives it 4 MB more.");
            rom.WriteBytes(mark, Enumerable.Repeat((byte)0xFF, 16).ToArray());
            (mark, next) = more;
        }
    }

    private static void WriteMark(RomBuffer rom, int mark, int next) =>
        rom.WriteBytes(mark, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'X', (byte)next, (byte)(next >> 8), (byte)(next >> 16) });

    /// <summary>The "LF2X" mark (16-byte aligned, past 2.5 MB) and the next free offset it holds, or null.</summary>
    private static (int Mark, int Next)? FindMark(RomBuffer rom)
    {
        if (rom.Length >= ExpandedSize && IsMark(rom, SpaceMark)) return (SpaceMark, (int)rom.ReadUInt24Le(SpaceMark + 4));
        for (int p = OriginalSize; p + 16 <= Math.Min(rom.Length, MaxSize); p += 16)
            if (IsMark(rom, p)) return (p, (int)rom.ReadUInt24Le(p + 4));
        return null;
    }

    private static bool IsMark(RomBuffer rom, int p) =>
        rom.ReadByte(p) == 'L' && rom.ReadByte(p + 1) == 'F' && rom.ReadByte(p + 2) == '2' && rom.ReadByte(p + 3) == 'X' &&
        rom.ReadUInt24Le(p + 4) is var n && n >= OriginalSize && n <= p;

    /// <summary>The largest empty run (one value, 00 or FF, at least 4 KB) from <paramref name="from"/> on: a mark is put
    /// in its last 16 bytes; the space starts 16 bytes into it (data may end in a 00 or FF).</summary>
    private static (int Mark, int Next)? NewSpace(RomBuffer rom, int from)
    {
        int bestStart = -1, bestLen = 0, end = Math.Min(rom.Length, MaxSize);
        int p = from;
        while (p < end)
        {
            byte v = rom.ReadByte(p);
            if (v is not (0x00 or 0xFF)) { p++; continue; }
            int q = p;
            while (q < end && rom.ReadByte(q) == v) q++;
            if (q - p > bestLen) { bestStart = p; bestLen = q - p; }
            p = q;
        }
        if (bestLen < 0x1000) return null;
        int start = (bestStart == from ? bestStart : bestStart + 16 + 15) & ~15, mark = (bestStart + bestLen - 16) & ~15;
        WriteMark(rom, mark, start);
        return (mark, start);
    }

    /// <summary>Bytes of added space still free (counting the growth to 4 MB).</summary>
    public static int FreeSpace(RomBuffer rom)
    {
        if (FindMark(rom) is not var (mark, next))
        {
            if (rom.Length < ExpandedSize) return MaxSize - OriginalSize;
            // grown (Expand, or a fan hack) but nothing placed by Lufia Forge yet: the largest empty run is what it would use
            int best = 0;
            for (int p = OriginalSize; p < Math.Min(rom.Length, MaxSize);)
            {
                byte v = rom.ReadByte(p);
                if (v is not (0x00 or 0xFF)) { p++; continue; }
                int q = p;
                while (q < Math.Min(rom.Length, MaxSize) && rom.ReadByte(q) == v) q++;
                best = Math.Max(best, q - p);
                p = q;
            }
            return Math.Max(0, best - 48) + Math.Max(0, MaxSize - rom.Length);
        }
        return Math.Max(0, mark - next) + Math.Max(0, MaxSize - Math.Max(rom.Length, ExpandedSize));
    }

    /// <summary>Packs <paramref name="data"/> and puts it back as file <paramref name="id"/>: where it was when it fits
    /// there, else in the added space (never across a 32 KB bank, so the game's unpacker reads it as one piece), the
    /// file table pointing at the new place.</summary>
    public static void WriteFile(RomBuffer rom, int id, byte[] data)
    {
        byte[] packed = LufiaCompression.Compress(data);
        int at = FileOffset(rom, id);
        LufiaCompression.Decompress(rom, at, out int oldSize);
        bool crosses(int o, int n) => o / 0x8000 != (o + n - 1) / 0x8000;
        if (packed.Length <= oldSize && !(at >= OriginalSize && crosses(at, packed.Length)))
        {
            rom.WriteBytes(at, packed);
            return;
        }
        int next = Allocate(rom, packed.Length);
        rom.WriteBytes(next, packed);
        int rel = next - FileTable;
        rom.WriteBytes(FileTable + id * 3, new[] { (byte)rel, (byte)(rel >> 8), (byte)(rel >> 16) });
    }

    /// <summary>Text the game shows for fixed-width names (monsters, items, spells): plain ASCII for the US game;
    /// 00 ends it.</summary>
    public static string Name(RomBuffer rom, int offset, int length)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < length; i++)
        {
            byte b = rom.ReadByte(offset + i);
            if (b == 0) break;
            sb.Append(L2Charset.ToChar(rom, b) ?? '?');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Writes a fixed-width name, space padded (characters the game can't show become spaces).</summary>
    public static void WriteName(RomBuffer rom, int offset, int length, string name)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            char c = i < name.Length ? name[i] : ' ';
            bytes[i] = L2Charset.ToByte(rom, c) ?? (byte)' ';
        }
        rom.WriteBytes(offset, bytes);
    }

    /// <summary>The seven heroes as the equip / learn bits name them.</summary>
    public static readonly string[] Heroes = { "Maxim", "Selan", "Guy", "Artea", "Tia", "Dekar", "Lexis" };
}
