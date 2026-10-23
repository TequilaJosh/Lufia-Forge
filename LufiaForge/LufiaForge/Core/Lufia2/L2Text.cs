using System.Text;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II text (US ROM, found 2026-10-22): plain ASCII plus a dictionary of 0x280 words at $8E:EA00 (file 0x76A00:
/// u16 offsets from the table to zero-terminated words, longest first). In text: 05 xx = word xx, 06 xx = word
/// 0x100 + xx, 80-FF = word 0x200 + (byte &amp; 7F); 00 ends the text and the script, 01 waits for a button and goes
/// back to the script, 03 new line, 04 xx pause, 0C-0F = "?" "!" "," "." and a new line, 09 the hero's name.
/// </summary>
public static class L2Text
{
    public const int Dictionary = 0x76A00, WordCount = 0x280;

    private static string[]? _words;
    private static RomBuffer? _wordsRom;

    public static string[] Words(RomBuffer rom)
    {
        if (_words != null && ReferenceEquals(_wordsRom, rom)) return _words;
        var w = new string[WordCount];
        for (int k = 0; k < WordCount; k++)
        {
            int at = Dictionary + rom.ReadUInt16Le(Dictionary + k * 2);
            w[k] = Encoding.Latin1.GetString(rom.ReadUntil(at, 0, 40));
        }
        _wordsRom = rom; _words = w;
        return w;
    }

    /// <summary>Decodes text from <paramref name="at"/> until 00 or 01 (included in <paramref name="end"/>). Control
    /// codes show as tags: [WAIT] [END] [PAUSE xx] [NAME] [xx].</summary>
    public static string Decode(RomBuffer rom, int at, out int end, int max = 2000)
    {
        var words = Words(rom);
        var sb = new StringBuilder();
        int p = at;
        while (p < at + max && p < rom.Length)
        {
            byte b = rom.ReadByte(p++);
            if (b == 0) { sb.Append("[END]"); break; }
            if (b == 1) { sb.Append("[WAIT]"); break; }
            if (b == 3) sb.Append('\n');
            else if (b == 4) sb.Append($"[PAUSE {rom.ReadByte(p++):X2}]");
            else if (b == 5) sb.Append(words[rom.ReadByte(p++)]);
            else if (b == 6) sb.Append(words[0x100 + rom.ReadByte(p++)]);
            else if (b >= 0x80) sb.Append(words[0x200 + (b & 0x7F)]);
            else if (b is >= 0x0C and <= 0x0F) sb.Append("?!,."[b - 0x0C]).Append('\n');
            else if (b == 9) sb.Append("[NAME]");
            else if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
            else sb.Append($"[{b:X2}]");
        }
        end = p;
        return sb.ToString();
    }

    /// <summary>Encodes text written as <see cref="Decode"/> shows it (the words in the dictionary become their
    /// codes, longest first).</summary>
    public static byte[] Encode(RomBuffer rom, string text)
    {
        var words = Words(rom);
        var byLength = Enumerable.Range(0, WordCount).Where(k => words[k].Length >= 2).OrderByDescending(k => words[k].Length).ThenBy(k => k).ToList();
        var outp = new List<byte>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '[')
            {
                int close = text.IndexOf(']', i);
                if (close > i)
                {
                    string tag = text.Substring(i + 1, close - i - 1);
                    if (tag == "END") outp.Add(0);
                    else if (tag == "WAIT") outp.Add(1);
                    else if (tag == "NAME") outp.Add(9);
                    else if (tag.StartsWith("PAUSE ")) { outp.Add(4); outp.Add(Convert.ToByte(tag[6..], 16)); }
                    else if (tag.Length == 2) outp.Add(Convert.ToByte(tag, 16));
                    i = close + 1;
                    continue;
                }
            }
            if (c == '\n') { outp.Add(3); i++; continue; }
            // "?!,." followed by a new line are one byte
            if ("?!,.".IndexOf(c) is int pi && pi >= 0 && i + 1 < text.Length && text[i + 1] == '\n') { outp.Add((byte)(0x0C + pi)); i += 2; continue; }
            int k = byLength.FirstOrDefault(k2 => string.CompareOrdinal(text, i, words[k2], 0, words[k2].Length) == 0, -1);
            if (k >= 0 && words[k].Length >= 2)
            {
                if (k < 0x100) { outp.Add(5); outp.Add((byte)k); }
                else if (k < 0x200) { outp.Add(6); outp.Add((byte)(k - 0x100)); }
                else outp.Add((byte)(0x80 | (k - 0x200)));
                i += words[k].Length;
                continue;
            }
            outp.Add(c is >= ' ' and < (char)0x7F ? (byte)c : (byte)'?');
            i++;
        }
        return outp.ToArray();
    }
}
