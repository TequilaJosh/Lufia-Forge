using System.Text;

namespace LufiaForge.Core.Lufia2;

// lufia 2 text (US rom, found 2026-10-22): plain ASCII plus a dictionary of 0x280 words at $8E:EA00 (file 0x76A00, u16
// offsets from the table to zero-terminated words, longest first). in text:
// - 05 xx = word xx, 06 xx = word 0x100 + xx, 80-FF = word 0x200 + (byte & 7F)
// - 00 ends the text and the script, 01 waits for a button and goes back to the script, 03 new line, 04 xx pause
// - 0C-0F = "?" "!" "," "." and a new line, 09 the hero's name, 07 xx a letter of the third page, 0B the choice
//   cursor (ends the text)
// - 0A lo hi = (hi >> 4) + 2 letters of the text lo + (hi & 0F) * 256 bytes back (shown written out, written back as
//   plain text)
public static class L2Text
{
    public const int Dictionary = 0x76A00, WordCount = 0x280;

    // the words read last, with the rom and the version they came from, kept as one object so a merge reading other
    // roms on another thread never pairs one rom with another's words. the version makes an Undo (or a Dutch import)
    // read them again
    private sealed record Cache(RomBuffer Rom, int Version, string[] Words);
    private static Cache? _cache;

    public static string[] Words(RomBuffer rom)
    {
        if (_cache is { } cache && ReferenceEquals(cache.Rom, rom) && cache.Version == rom.Version) return cache.Words;
        var words = new string[WordCount];
        for (int k = 0; k < WordCount; k++)
        {
            int at = Dictionary + rom.ReadUInt16Le(Dictionary + k * 2);
            words[k] = new string(rom.ReadUntil(at, 0, 40).Select(b => L2Charset.ToChar(rom, b) ?? '?').ToArray());
        }
        _cache = new Cache(rom, rom.Version, words);
        return words;
    }

    // decodes text from at until 00 or 01 (included in end). control codes show as tags: [WAIT] [END] [PAUSE xx]
    // [NAME] [xx]
    public static string Decode(RomBuffer rom, int at, out int end, int max = 2000) => DecodeAt(rom, at, out end, max, 0);

    private static string DecodeAt(RomBuffer rom, int at, out int end, int max, int depth)
    {
        var words = Words(rom);
        var sb = new StringBuilder();
        int p = at;
        while (p < at + max && p < rom.Length)
        {
            byte textByte = rom.ReadByte(p++);
            if (textByte == 0) { sb.Append("[END]"); break; }
            if (textByte == 1) { sb.Append("[WAIT]"); break; }
            if (textByte == 0x0B) { sb.Append("[CHOICE]"); break; }
            if (textByte == 0x0A && depth < 4)
            {
                // a back-reference: (hi >> 4) + 2 bytes of the text distance bytes back from here (bytes, not letters:
                // a Dutch letter 10-1F or a dictionary word is one byte of them)
                byte lo = rom.ReadByte(p), hi = rom.ReadByte(p + 1);
                int distance = lo | (hi & 0x0F) << 8, length = (hi >> 4) + 2;
                sb.Append(DecodeAt(rom, p - distance, out _, length, depth + 1).Replace("[END]", "").Replace("[WAIT]", "").Replace("[CHOICE]", ""));
                p += 2;
            }
            else if (textByte == 7) sb.Append($"[GLYPH {rom.ReadByte(p++):X2}]");
            else if (textByte == 3) sb.Append('\n');
            else if (textByte == 4)
            {
                byte pause = rom.ReadByte(p++);
                if (pause != 0x80) sb.Append($"[PAUSE {pause:X2}]");   // 04 80, a pause of no time, only pads out shortened text
            }
            else if (textByte == 5) sb.Append(words[rom.ReadByte(p++)]);
            else if (textByte == 6) sb.Append(words[0x100 + rom.ReadByte(p++)]);
            else if (textByte >= 0x80) sb.Append(words[0x200 + (textByte & 0x7F)]);
            else if (textByte is >= 0x0C and <= 0x0F) sb.Append("?!,."[textByte - 0x0C]).Append('\n');
            else if (textByte == 9) sb.Append("[NAME]");
            else if (textByte != 0x5B && L2Charset.ToChar(rom, textByte) is char ch) sb.Append(ch);   // "[" itself shows as [5B], so it reads back
            else sb.Append($"[{textByte:X2}]");
        }
        end = p;
        return sb.ToString();
    }

    // encodes text written the way Decode shows it (the words in the dictionary become their codes, longest first).
    // a tag in brackets that isn't one of Decode's ([END], [WAIT], [CHOICE], [NAME], [GLYPH xx], [PAUSE xx], [xx])
    // throws with what's wrong (it used to get left out without a word, so "[Name]" typed the event editor's way lost
    // the hero's name)
    public static byte[] Encode(RomBuffer rom, string text)
    {
        var words = Words(rom);
        var byLength = Enumerable.Range(0, WordCount).Where(k => words[k].Length >= 2).OrderByDescending(k => words[k].Length).ThenBy(k => k).ToList();
        var output = new List<byte>();
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
                    if (tag == "END") output.Add(0);
                    else if (tag == "WAIT") output.Add(1);
                    else if (tag == "NAME") output.Add(9);
                    else if (tag == "CHOICE") output.Add(0x0B);
                    else if (tag.StartsWith("GLYPH ") && Hex(tag[6..], 1) is byte glyph) { output.Add(7); output.Add(glyph); }
                    else if (tag.StartsWith("PAUSE ") && Hex(tag[6..], 1) is byte pause) { output.Add(4); output.Add(pause); }
                    else if (Hex(tag) is byte code) output.Add(code);
                    else throw new InvalidOperationException($"[{tag}] isn't a code the text knows. Use [NAME], [PAUSE xx], [GLYPH xx], a code like [0C], "
                        + "and end with [WAIT], [END] or [CHOICE] (xx: two hex digits).");
                    i = close + 1;
                    continue;
                }
            }
            if (c == '\n') { output.Add(3); i++; continue; }
            // "?!,." followed by a new line are one byte
            if ("?!,.".IndexOf(c) is int punctuation && punctuation >= 0 && i + 1 < text.Length && text[i + 1] == '\n') { output.Add((byte)(0x0C + punctuation)); i += 2; continue; }
            int word = byLength.FirstOrDefault(k => string.CompareOrdinal(text, i, words[k], 0, words[k].Length) == 0, -1);
            if (word >= 0 && words[word].Length >= 2)
            {
                if (word < 0x100) { output.Add(5); output.Add((byte)word); }
                else if (word < 0x200) { output.Add(6); output.Add((byte)(word - 0x100)); }
                else output.Add((byte)(0x80 | (word - 0x200)));
                i += words[word].Length;
                continue;
            }
            output.Add(L2Charset.ToByte(rom, c) ?? (byte)'?');
            i++;
        }
        return output.ToArray();
    }

    // two hex digits (or as few as least) as a byte, or null
    private static byte? Hex(string digits, int least = 2) => digits.Length >= least && digits.Length <= 2 && digits.All(Uri.IsHexDigit) ? Convert.ToByte(digits, 16) : null;
}
