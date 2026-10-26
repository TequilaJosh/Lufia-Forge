using System.Text;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II dialogue as the event editor shows it: plain text with a few tokens. [Name] = the hero's name (Maxim's,
/// 09), [Pause 20] = a pause of 20 frames (04 82), [Voice 3] = the letters' sound (04 03), [Glyph 41] = a letter of the
/// third font page (07 41), [0C] etc. for other codes. Dictionary words (05/06/80-FF) and repeats of earlier text (0A)
/// are written out; when the text is changed it is packed again with the dictionary (<see cref="L2Text.Encode"/>).
/// The code that ends the text (00 end, 01 wait and go on, 0B question cursor) is kept apart.
/// </summary>
public static class L2EventText
{
    /// <summary>Reads text from <paramref name="at"/> to its end code; <paramref name="next"/> is the byte after the end
    /// code (or where reading stopped).</summary>
    public static string Decode(RomBuffer rom, int at, out int next, out byte end, out bool backReference)
    {
        var words = L2Text.Words(rom);
        var sb = new StringBuilder();
        int p = at;
        end = 0xFF;
        backReference = false;
        for (int guard = 0; guard < 4000 && p < rom.Length; guard++)
        {
            byte b = rom.ReadByte(p++);
            if (b is 0x00 or 0x01 or 0x0B) { end = b; break; }
            switch (b)
            {
                case 0x03: sb.Append('\n'); break;
                case 0x04:
                {
                    byte v = rom.ReadByte(p++);
                    if (v != 0x80) sb.Append(v > 0x80 ? $"[Pause {(v & 0x7F) * 10}]" : $"[Voice {v}]");   // 04 80 (no pause) fills up shortened text
                    break;
                }
                case 0x05: sb.Append(words[rom.ReadByte(p++)]); break;
                case 0x06: sb.Append(words[0x100 + rom.ReadByte(p++)]); break;
                case 0x07: sb.Append($"[Glyph {rom.ReadByte(p++):X2}]"); break;
                case 0x09: sb.Append("[Name]"); break;
                case 0x0A:
                {
                    byte lo = rom.ReadByte(p), hi = rom.ReadByte(p + 1);
                    int d = lo | (hi & 0x0F) << 8, n = (hi >> 4) + 2;
                    string earlier = L2Text.Decode(rom, p - d, out _, 400).Replace("[END]", "").Replace("[WAIT]", "").Replace("[CHOICE]", "");
                    earlier = earlier.Replace("[NAME]", "\u0001");
                    string part = earlier.Length > n ? earlier[..n] : earlier;
                    sb.Append(part.Replace("\u0001", "[Name]"));
                    backReference = true;
                    p += 2;
                    break;
                }
                case >= 0x0C and <= 0x0F: sb.Append("?!,."[b - 0x0C]).Append('\n'); break;
                case >= 0x80: sb.Append(words[0x200 + (b & 0x7F)]); break;
                case >= 0x20 and < 0x7F: sb.Append((char)b); break;
                default: sb.Append($"[{b:X2}]"); break;
            }
        }
        next = p;
        return sb.ToString();
    }

    /// <summary>Checks typed text: returns what's wrong, or null.</summary>
    public static string? Check(string text)
    {
        foreach (var t in Tokens(text))
        {
            if (t is "Name") continue;
            if (Pause(t) is int f) { if (f is < 0 or > 250) return $"[{t}]: pauses are 0 to 250 frames."; continue; }
            if (t.StartsWith("Voice ") && int.TryParse(t[6..], out int v)) { if (v is < 0 or > 0x7F) return $"[{t}]: voices are 0 to 127."; continue; }
            if (t.StartsWith("Glyph ") && t.Length == 8 && IsHex(t[6..])) continue;
            if (t.Length == 2 && IsHex(t) && Convert.ToInt32(t, 16) is 0x03 or >= 0x0C and <= 0x0F) continue;
            return $"[{t}] isn't a known code. Use [Name], [Pause 20], [Voice 3] or [Glyph 41].";
        }
        foreach (char c in text)
            if (c != '\n' && (c < ' ' || c > '~')) return $"The game has no letter for '{c}'.";
        return null;
    }

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);
    private static int? Pause(string t) => t.StartsWith("Pause ") && int.TryParse(t[6..], out int f) ? f : null;

    private static IEnumerable<string> Tokens(string text)
    {
        for (int i = text.IndexOf('['); i >= 0; i = text.IndexOf('[', i + 1))
        {
            int close = text.IndexOf(']', i);
            if (close < 0) yield break;
            yield return text.Substring(i + 1, close - i - 1);
        }
    }

    /// <summary>The text packed for the game (without the end code).</summary>
    public static byte[] Encode(RomBuffer rom, string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[' && text.IndexOf(']', i) is int close && close > i)
            {
                string t = text.Substring(i + 1, close - i - 1);
                if (t == "Name") sb.Append("[NAME]");
                else if (Pause(t) is int f) sb.Append($"[PAUSE {0x80 | Math.Clamp((f + 5) / 10, 0, 25):X2}]");
                else if (t.StartsWith("Voice ") && int.TryParse(t[6..], out int v)) sb.Append($"[PAUSE {v & 0x7F:X2}]");
                else if (t.StartsWith("Glyph ")) sb.Append($"[GLYPH {t[6..]}]");
                else sb.Append('[').Append(t).Append(']');
                i = close + 1;
                continue;
            }
            sb.Append(text[i] == '\r' ? "" : text[i].ToString());
            i++;
        }
        return L2Text.Encode(rom, sb.ToString());
    }
}
