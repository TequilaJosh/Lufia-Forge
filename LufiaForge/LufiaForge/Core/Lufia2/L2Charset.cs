using System.Runtime.CompilerServices;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// The letters a Lufia II ROM's fonts show for each byte. The US game shows ASCII (20-7E). With the Dutch font (the
/// Dutch ROM itself, or a ROM <see cref="L2Dutch.Import"/> put it in) 10-1F are À Á È É Ë Ï Ò Ó Ö Ù Ú Ü à á è é and a
/// few signs are letters: " ë, # ï, $ ò, &amp; ó, ( ö, ) ù, * ú, ; ü, &lt; ä, = Ä, &gt; ì, \ ñ, ^ Ñ, ` ¿, | ß, ~ ê,
/// 7F ¡, while [ and ] draw ( and ) (the dialogue font and the menu font agree; read from the Dutch ROM's fonts,
/// 2026-10-30). Names and text are shown and typed with the letters themselves.
/// </summary>
public static class L2Charset
{
    private static readonly (byte Code, char Letter)[] DutchMap = BuildDutch();

    private static (byte, char)[] BuildDutch()
    {
        var list = new List<(byte, char)>();
        const string upper10 = "ÀÁÈÉËÏÒÓÖÙÚÜàáèé";
        for (int i = 0; i < 16; i++) list.Add(((byte)(0x10 + i), upper10[i]));
        list.AddRange(new (byte, char)[]
        {
            (0x22, 'ë'), (0x23, 'ï'), (0x24, 'ò'), (0x26, 'ó'), (0x28, 'ö'), (0x29, 'ù'), (0x2A, 'ú'), (0x3B, 'ü'),
            (0x3C, 'ä'), (0x3D, 'Ä'), (0x3E, 'ì'), (0x5C, 'ñ'), (0x5E, 'Ñ'), (0x60, '¿'), (0x7C, 'ß'), (0x7E, 'ê'),
            (0x7F, '¡'), (0x5B, '('), (0x5D, ')'),
        });
        return list.ToArray();
    }

    private static readonly Dictionary<byte, char> DutchChar = DutchMap.ToDictionary(x => x.Code, x => x.Letter);
    private static readonly Dictionary<char, byte> DutchByte = DutchMap.ToDictionary(x => x.Letter, x => x.Code);

    /// <summary>The Dutch letters and their codes (for help texts).</summary>
    public static IReadOnlyList<(byte Code, char Letter)> DutchLetters => DutchMap;

    private static readonly ConditionalWeakTable<RomBuffer, StrongBox<int>> Sites = new();

    /// <summary>Whether the ROM's dialogue font is the Dutch one (its glyph code starts the font at character 10).</summary>
    public static bool IsDutch(RomBuffer rom)
    {
        if (rom.Game != GameKind.Lufia2 && rom.Game != GameKind.Unknown) return false;
        var box = Sites.GetValue(rom, r => new StrongBox<int>(L2Dutch.FontSite(r).Operand));
        int op = box.Value;
        // the operand of LDA long,X; 27 bytes before it the SBC's value: 20 in the US code, 10 in the Dutch one
        return op >= 27 && op < rom.Length && rom.ReadByte(op - 27) == 0x10 && rom.ReadByte(op - 28) == 0xE9;
    }

    /// <summary>The letter byte <paramref name="b"/> shows, or null (a code, not a letter).</summary>
    public static char? ToChar(RomBuffer rom, byte b)
    {
        if (IsDutch(rom) && DutchChar.TryGetValue(b, out char c)) return c;
        return b is >= 0x20 and < 0x7F ? (char)b : null;
    }

    /// <summary>The byte for letter <paramref name="c"/>, or null when the font has no such letter.</summary>
    public static byte? ToByte(RomBuffer rom, char c)
    {
        if (IsDutch(rom))
        {
            if (DutchByte.TryGetValue(c, out byte d)) return d;
            // signs the Dutch font draws as letters have no other place: they can't be typed
            if (DutchChar.ContainsKey((byte)c) && c < 0x80) return null;
        }
        return c is >= ' ' and < (char)0x7F ? (byte)c : null;
    }

    /// <summary>Whether <paramref name="c"/> can be written in this ROM's text.</summary>
    public static bool CanShow(RomBuffer rom, char c) => ToByte(rom, c) != null;
}
