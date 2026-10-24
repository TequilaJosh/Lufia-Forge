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

    public static int SnesToFile(int snes) => ((snes >> 16) & 0x7F) * 0x8000 + (snes & 0x7FFF);
    public static int FileToSnes(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

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

    /// <summary>Reserves <paramref name="length"/> bytes of added space, never across a 32 KB bank (so the game reads
    /// them as one piece), growing the ROM when needed. Returns the file offset.</summary>
    public static int Allocate(RomBuffer rom, int length)
    {
        if (length <= 0 || length > 0x8000) throw new ArgumentOutOfRangeException(nameof(length));
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
            var more = NewSpace(rom, mark + 16) ?? throw new InvalidOperationException("The ROM is full (4 MB, the most a Lufia II cartridge can address).");
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
        for (int p = OriginalSize; p + 16 <= rom.Length; p += 16)
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
        int bestStart = -1, bestLen = 0;
        int p = from;
        while (p < rom.Length)
        {
            byte v = rom.ReadByte(p);
            if (v is not (0x00 or 0xFF)) { p++; continue; }
            int q = p;
            while (q < rom.Length && rom.ReadByte(q) == v) q++;
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
        if (FindMark(rom) is not var (mark, next)) return rom.Length >= ExpandedSize ? 0 : (MaxSize - OriginalSize);
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
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
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
            bytes[i] = c is >= ' ' and < (char)0x7F ? (byte)c : (byte)' ';
        }
        rom.WriteBytes(offset, bytes);
    }

    /// <summary>The seven heroes as the equip / learn bits name them.</summary>
    public static readonly string[] Heroes = { "Maxim", "Selan", "Guy", "Artea", "Tia", "Dekar", "Lexis" };
}
