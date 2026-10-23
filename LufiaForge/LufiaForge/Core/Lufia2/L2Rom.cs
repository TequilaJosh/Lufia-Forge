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

    /// <summary>Space Lufia Forge adds after the game's 2.5 MB (the ROM grows to 3 MB the first time a file doesn't fit
    /// where it was). The last 16 bytes hold "LF2X" and the next free offset (u24).</summary>
    public const int OriginalSize = 0x280000, ExpandedSize = 0x300000, SpaceMark = ExpandedSize - 16;

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
        if (rom.Length < ExpandedSize) rom.Expand(ExpandedSize);
        int next = rom.ReadByte(SpaceMark) == 'L' && rom.ReadByte(SpaceMark + 1) == 'F' && rom.ReadByte(SpaceMark + 2) == '2' && rom.ReadByte(SpaceMark + 3) == 'X'
            ? (int)rom.ReadUInt24Le(SpaceMark + 4) : OriginalSize;
        if (crosses(next, packed.Length)) next = (next / 0x8000 + 1) * 0x8000;
        if (next + packed.Length > SpaceMark) throw new InvalidOperationException("The added space is full.");
        rom.WriteBytes(next, packed);
        int rel = next - FileTable;
        rom.WriteBytes(FileTable + id * 3, new[] { (byte)rel, (byte)(rel >> 8), (byte)(rel >> 16) });
        int after = next + packed.Length;
        rom.WriteBytes(SpaceMark, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'X', (byte)after, (byte)(after >> 8), (byte)(after >> 16) });
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
