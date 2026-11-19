namespace LufiaForge.Core.Maps;

// how the game prints cutscene text: one character every 2 frames (spaces too, line breaks are free), the first one
// 2 frames after the text command runs. 1 frame if command 3B made the text silent (no sound to start), and 3B only
// lasts for the next text.
// narration placed with command 6B gets drawn straight on the screen: a block as wide as its longest line (rounded up
// to 16 pixels), centred (column 20+) or at a column, rows are 8 pixels, lines 16 pixels apart.
// measured on the intro's ten narration texts
public static class CutsceneText
{
    // the text font: 8x16 one-bit glyphs, 16 bytes each, indexed by character code (file 0x54251 + 16 * code,
    // 'W' is at 0x547C1). the game draws them on BG3 in palette 7 (colour 3 = the letter, colour 1 = its outline).
    // measured from the intro's text tiles in VRAM
    public const int FontBase = 0x54251;

    // the 16 rows of a character's glyph (bit 7 = leftmost pixel)
    public static byte[] Glyph(RomBuffer rom, char c)
    {
        var glyph = new byte[16];
        int code = c, glyphOffset = FontBase + code * 16;
        if (code is < 0x20 or > 0x7F || glyphOffset + 16 > rom.Length) return glyph;
        for (int i = 0; i < 16; i++) glyph[i] = rom.ReadByte(glyphOffset + i);
        return glyph;
    }

    public const int FramesPerChar = 2;
    public const int FirstCharDelay = 2;
    public const int ScreenWidth = 256;

    // the text as the game prints it, name codes ([Artea]) print the name
    public static string Printed(string text) => text.Replace("[", "").Replace("]", "");

    // characters printed after frames frames of typing
    public static int CharsAfter(double frames, bool silent = false)
    {
        int delay = silent ? FirstCharDelay - 1 : FirstCharDelay;
        return frames < delay ? 0 : (int)((frames - delay) / FramesPerChar) + 1;
    }

    // the part of text that's on screen after frames frames of typing
    public static string Typed(string text, double frames, bool silent = false)
    {
        string printed = Printed(text);
        int charsLeft = CharsAfter(frames, silent), i = 0;
        for (; i < printed.Length && charsLeft > 0; i++) if (printed[i] != '\n') charsLeft--;
        return printed[..i];
    }

    // frames until the whole text is printed
    public static int TypingFrames(string text)
    {
        int chars = Printed(text).Count(c => c != '\n');
        return chars == 0 ? 0 : FirstCharDelay + (chars - 1) * FramesPerChar;
    }

    // screen position (pixels) of each line of a narration window placed by 6B at (column, row)
    public static List<(string Line, int X, int Y)> Layout(string fullText, string shown, int column, int row)
    {
        var allLines = Printed(fullText).Split('\n');
        int width = (allLines.Max(l => l.Length) + 1) / 2 * 16;   // the window is a whole number of 16-pixel tiles wide
        int x = column >= 0x20 ? (ScreenWidth - width) / 2 : column * 8;
        var lines = shown.Split('\n');
        var placed = new List<(string, int, int)>();
        for (int i = 0; i < lines.Length; i++) placed.Add((lines[i], x, (row + 1) * 8 + i * 16));
        return placed;
    }

    // white a lightning flash (60) adds on the frames after it (measured, 7 frames)
    public static readonly double[] FlashCurve = { 0, 0.27, 0.62, 1, 1, 0.62, 0.27, 0.12 };

    // the same flash the way the console does it: added to every 5-bit colour component (measured in CGRAM)
    public static readonly int[] FlashAdd = { 0, 7, 15, 31, 31, 15, 7, 3 };
}
