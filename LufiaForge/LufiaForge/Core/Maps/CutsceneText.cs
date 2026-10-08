namespace LufiaForge.Core.Maps;

/// <summary>
/// How the game prints cutscene text: one character every 2 frames (spaces too, line breaks are free), the first
/// one 2 frames after the text command runs (1 frame when command 3B made the text silent: no sound to start; 3B
/// only lasts for the next text). Narration placed with command 6B is drawn straight on the screen:
/// a block as wide as its longest line (rounded up to 16 pixels), centred (column 20+) or at a column, rows 8 pixels, lines 16 pixels apart.
/// Measured on the intro's ten narration texts.
/// </summary>
public static class CutsceneText
{
    /// <summary>
    /// The text font: 8x16 one-bit glyphs, 16 bytes each, indexed by character code (file 0x54251 + 16 * code;
    /// 'W' is at 0x547C1). The game draws them on BG3 in palette 7 (colour 3 = the letter, colour 1 = its
    /// outline). Measured from the intro's text tiles in VRAM.
    /// </summary>
    public const int FontBase = 0x54251;

    /// <summary>The 16 rows of a character's glyph (bit 7 = leftmost pixel).</summary>
    public static byte[] Glyph(RomBuffer rom, char c)
    {
        var g = new byte[16];
        int code = c, at = FontBase + code * 16;
        if (code is < 0x20 or > 0x7F || at + 16 > rom.Length) return g;
        for (int i = 0; i < 16; i++) g[i] = rom.ReadByte(at + i);
        return g;
    }

    public const int FramesPerChar = 2;
    public const int FirstCharDelay = 2;
    public const int ScreenWidth = 256;

    /// <summary>The text as the game prints it: name codes ([Artea]) print the name.</summary>
    public static string Printed(string text) => text.Replace("[", "").Replace("]", "");

    /// <summary>Characters printed after <paramref name="frames"/> frames of typing.</summary>
    public static int CharsAfter(double frames, bool silent = false)
    {
        int delay = silent ? FirstCharDelay - 1 : FirstCharDelay;
        return frames < delay ? 0 : (int)((frames - delay) / FramesPerChar) + 1;
    }

    /// <summary>The part of <paramref name="text"/> on screen after <paramref name="frames"/> frames of typing.</summary>
    public static string Typed(string text, double frames, bool silent = false)
    {
        string p = Printed(text);
        int n = CharsAfter(frames, silent), i = 0;
        for (; i < p.Length && n > 0; i++) if (p[i] != '\n') n--;
        return p[..i];
    }

    /// <summary>Frames until the whole text is printed.</summary>
    public static int TypingFrames(string text)
    {
        int chars = Printed(text).Count(c => c != '\n');
        return chars == 0 ? 0 : FirstCharDelay + (chars - 1) * FramesPerChar;
    }

    /// <summary>Screen position (pixels) of each line of a narration window placed by 6B at (column, row).</summary>
    public static List<(string Line, int X, int Y)> Layout(string fullText, string shown, int column, int row)
    {
        var all = Printed(fullText).Split('\n');
        int width = (all.Max(l => l.Length) + 1) / 2 * 16;   // the window is a whole number of 16-pixel tiles wide
        int x = column >= 0x20 ? (ScreenWidth - width) / 2 : column * 8;
        var lines = shown.Split('\n');
        var res = new List<(string, int, int)>();
        for (int i = 0; i < lines.Length; i++) res.Add((lines[i], x, (row + 1) * 8 + i * 16));
        return res;
    }

    /// <summary>White added by a lightning flash (60) on the frames after it (measured: 7 frames).</summary>
    public static readonly double[] FlashCurve = { 0, 0.27, 0.62, 1, 1, 0.62, 0.27, 0.12 };

    /// <summary>The same flash as the console does it: added to every 5-bit colour component (measured in CGRAM).</summary>
    public static readonly int[] FlashAdd = { 0, 7, 15, 31, 31, 15, 7, 3 };
}
