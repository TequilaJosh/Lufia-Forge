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
