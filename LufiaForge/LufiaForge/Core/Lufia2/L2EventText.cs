using System.Text;

namespace LufiaForge.Core.Lufia2;

// lufia 2 dialogue the way the event editor shows it: plain text with a few tokens. [Name] = the hero's name
// (Maxim's, 09), [Pause 20] = a pause of 20 frames (04 82), [Voice 3] = the letters' sound (04 03), [Glyph 41] = a
// letter of the third font page (07 41), [0C] etc. for other codes. dictionary words (05/06/80-FF) and repeats of
// earlier text (0A) get written out. when the text changes it gets packed again with the dictionary (L2Text.Encode).
// the code that ends the text (00 end, 01 wait and go on, 0B question cursor) is kept apart
public static class L2EventText
{
    // reads text from at to its end code. next is the byte after the end code (or where reading stopped)
    public static string Decode(RomBuffer rom, int at, out int next, out byte end, out bool backReference) =>
        DecodeCore(rom, at, int.MaxValue, 0, out next, out end, out backReference);

    // decodes up to limit bytes (a back-reference copies that many bytes of earlier text)
    private static string DecodeCore(RomBuffer rom, int at, int limit, int depth, out int next, out byte end, out bool backReference)
    {
        var words = L2Text.Words(rom);
        var sb = new StringBuilder();
        int position = at;
        end = 0xFF;
        backReference = false;
        for (int guard = 0; guard < 4000 && position < rom.Length && position - at < limit; guard++)
        {
            byte b = rom.ReadByte(position++);
            if (b is 0x00 or 0x01 or 0x0B) { end = b; break; }
            switch (b)
            {
                case 0x03: sb.Append('\n'); break;
                case 0x04:
                {
                    byte value = rom.ReadByte(position++);
                    if (value != 0x80) sb.Append(value > 0x80 ? $"[Pause {(value & 0x7F) * 10}]" : $"[Voice {value}]");   // 04 80 (no pause) fills up shortened text
                    break;
                }
                case 0x05: sb.Append(words[rom.ReadByte(position++)]); break;
                case 0x06: sb.Append(words[0x100 + rom.ReadByte(position++)]); break;
                case 0x07: sb.Append($"[Glyph {rom.ReadByte(position++):X2}]"); break;
                case 0x09: sb.Append("[Name]"); break;
                case 0x0A:
                {
                    byte lo = rom.ReadByte(position), hi = rom.ReadByte(position + 1);
                    int distance = lo | (hi & 0x0F) << 8, count = (hi >> 4) + 2;
                    // count bytes of the text distance bytes back (bytes, not letters: a Dutch letter 10-1F or a word is one byte)
                    if (depth < 4) sb.Append(DecodeCore(rom, position - distance, count, depth + 1, out _, out _, out _));
                    backReference = true;
                    position += 2;
                    break;
                }
                case >= 0x0C and <= 0x0F: sb.Append("?!,."[b - 0x0C]).Append('\n'); break;
                case >= 0x80: sb.Append(words[0x200 + (b & 0x7F)]); break;
                case not 0x5B when L2Charset.ToChar(rom, b) is char ch: sb.Append(ch); break;   // "[" itself shows as [5B], so it reads back
                default: sb.Append($"[{b:X2}]"); break;
            }
        }
        next = position;
        return sb.ToString();
    }

    // checks typed text: returns what's wrong, or null
    public static string? Check(string text, RomBuffer? rom = null)
    {
        foreach (var token in Tokens(text))
        {
            if (token is "Name") continue;
            if (Pause(token) is int frames) { if (frames is < 0 or > 250) return $"[{token}]: pauses are 0 to 250 frames."; continue; }
            if (token.StartsWith("Voice ") && int.TryParse(token[6..], out int voice)) { if (voice is < 0 or > 0x7F) return $"[{token}]: voices are 0 to 127."; continue; }
            if (token.StartsWith("Glyph ") && token.Length == 8 && IsHex(token[6..])) continue;
            // codes shown as [XX] that get written back as they are (08 opens the game's questions: inns, shops...).
            // not the end codes 00-02 and 0B. 5B is "[" itself (in the Dutch font "(")
            if (token.Length == 2 && IsHex(token) && Convert.ToInt32(token, 16) is 0x03 or 0x08 or >= 0x0C and <= 0x1F or 0x5B or 0x7F) continue;
            return $"[{token}] isn't a known code. Use [Name], [Pause 20], [Voice 3] or [Glyph 41].";
        }
        foreach (char c in text)
            if (c != '\n' && !(rom != null ? L2Charset.CanShow(rom, c) : c is >= ' ' and <= '~'))
                return $"The game has no letter for '{c}'." + (rom != null && !L2Charset.IsDutch(rom) && c > 0x7F ? " Accented letters come with the Dutch font (Import the Dutch translation)." : "");
        return null;
    }

    private static bool IsHex(string text) => text.All(Uri.IsHexDigit);
    private static int? Pause(string token) => token.StartsWith("Pause ") && int.TryParse(token[6..], out int frames) ? frames : null;

    private static IEnumerable<string> Tokens(string text)
    {
        for (int i = text.IndexOf('['); i >= 0; i = text.IndexOf('[', i + 1))
        {
            int close = text.IndexOf(']', i);
            if (close < 0) yield break;
            yield return text.Substring(i + 1, close - i - 1);
        }
    }

    // the text packed for the game (without the end code)
    public static byte[] Encode(RomBuffer rom, string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[' && text.IndexOf(']', i) is int close && close > i)
            {
                string token = text.Substring(i + 1, close - i - 1);
                if (token == "Name") sb.Append("[NAME]");
                else if (Pause(token) is int frames) sb.Append($"[PAUSE {0x80 | Math.Clamp((frames + 5) / 10, 0, 25):X2}]");
                else if (token.StartsWith("Voice ") && int.TryParse(token[6..], out int voice)) sb.Append($"[PAUSE {voice & 0x7F:X2}]");
                else if (token.StartsWith("Glyph ")) sb.Append($"[GLYPH {token[6..]}]");
                else if (token.Length == 2 && IsHex(token)) sb.Append('[').Append(token).Append(']');
                else throw new InvalidOperationException(Check($"[{token}]") ?? $"[{token}] isn't a known code. Use [Name], [Pause 20], [Voice 3] or [Glyph 41].");
                i = close + 1;
                continue;
            }
            sb.Append(text[i] == '\r' ? "" : text[i].ToString());
            i++;
        }
        return L2Text.Encode(rom, sb.ToString());
    }
}
