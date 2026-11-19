namespace LufiaForge.Core.Lufia2;

// lufia 2 map events (the commands are in L2Script). the map header table at $87:8000 gives each map's event block
// ("PH") as a 24-bit offset added to $87:8000 (SNES arithmetic). the block's header holds u16 offsets (from "PH") to
// lists of (id, u16 script offset): +2 scripts run as the map loads, +6 talking to a character (ids 0x50+), +8
// stepping on a trigger. scripts are commands. 08, 13 xx and 61-67 start text (a character speaks) that runs until
// 01 (back to the script), 0B (a choice) or 00 (the end)
public static class L2Events
{
    // $87:8000: the map names (a u16 from here) and the persistent characters' table pointer. the 8-byte map records
    // start at +0x10 (HeaderAt, New map moves them)
    public const int HeaderTable = 0x38000;

    // file offset of map map's header record: event block u24, start-up script u24, name u16
    public static int HeaderAt(RomBuffer rom, int map) => L2NewMaps.Entry(rom, L2MapTable.Header, map);

    public static readonly string[] Speakers = { "Maxim", "Selan", "Guy", "Artea", "Tia", "Dekar", "Lexis" };

    // a piece of text in a map's events
    public sealed record Line(int Map, int Offset, int Length, string Speaker, string Text);

    // file offset of map map's event block, or -1
    public static int Block(RomBuffer rom, int map)
    {
        int headerAt = HeaderAt(rom, map);
        int offset = rom.ReadByte(headerAt) | rom.ReadByte(headerAt + 1) << 8 | rom.ReadByte(headerAt + 2) << 16;
        int blockAt = L2Rom.SnesToFile(0x878000 + offset);
        return blockAt + 2 < rom.Length && rom.ReadByte(blockAt) == 'P' && rom.ReadByte(blockAt + 1) == 'H' ? blockAt : -1;
    }

    // the scripts a block starts from, as offsets from the block: the lists at +2 (run as the map loads), +4 (scenes,
    // started by the map's cutscene triggers), +6 (talking to character id), +8 (stepping on trigger id), +A and +C
    // (rarely used)
    public static List<(string Kind, int Id, int Offset)> Entries(RomBuffer rom, int block)
    {
        var list = new List<(string, int, int)>();
        foreach (var (kind, head) in new[] { ("load", 2), ("scene", 4), ("talk", 6), ("trigger", 8), ("list A", 0x0A), ("list C", 0x0C) })
        {
            int listAt = block + rom.ReadUInt16Le(block + head);
            for (int i = 0; i < 128; i++)
            {
                byte id = rom.ReadByte(listAt + i * 3);
                if (id == 0xFF) break;
                int offset = rom.ReadUInt16Le(listAt + i * 3 + 1);
                if (head == 2 && id == 3) continue;   // load entry 03 isn't a script: 4 bytes (a number, the u16 key item for this map's locked doors, 00)
                list.Add((kind, id, offset));
            }
        }
        return list;
    }

    // all the text a map's scripts show, in order of where it sits (every script of the map walked with L2Script, the
    // speaker is the command before the text)
    public static List<Line> Lines(RomBuffer rom, int map)
    {
        var lines = new SortedDictionary<int, Line>();
        Dictionary<int, string> who;
        try { who = L2Speakers.ForMap(rom, map); } catch (Exception) { who = new(); }
        foreach (var (_, ops) in L2Script.MapScripts(rom, map))
        {
            var byEnd = ops.Where(o => !o.IsText).GroupBy(o => o.At + o.Length).ToDictionary(g => g.Key, g => g.First());
            foreach (var textOp in ops.Where(o => o.IsText))
            {
                if (lines.ContainsKey(textOp.At)) continue;
                string speaker = "(text)";
                if (byEnd.TryGetValue(textOp.At, out var before))
                {
                    int op = rom.ReadByte(before.At);
                    speaker = op switch
                    {
                        0x08 => "(the character)",
                        0x13 => $"character {rom.ReadByte(before.At + 1):X2}",
                        >= 0x61 and <= 0x67 => Speakers[new[] { 0, 1, 2, 3, 5, 4, 6 }[op - 0x61]],
                        0x10 => "(choice)",
                        0x9E => "(narration)",
                        _ => "(text)",
                    };
                }
                if (who.TryGetValue(textOp.At, out var named)) speaker = named;
                lines[textOp.At] = new Line(map, textOp.At, textOp.Length, speaker, textOp.Text);
            }
        }
        return lines.Values.ToList();
    }

    // the block's length: up to the next map's block if that follows it, else 0x3000 at most
    public static int BlockSize(RomBuffer rom, int map, int block)
    {
        int next = int.MaxValue;
        for (int m = 0; m < 0xF2; m++) { int other = Block(rom, m); if (other > block && other < next) next = other; }
        return Math.Min(0x3000, next == int.MaxValue ? 0x3000 : next - block);
    }

    // whether the line repeats earlier text with 0A (written back, that part takes its full length)
    public static bool HasBackReference(RomBuffer rom, Line line)
    {
        for (int position = line.Offset; position < line.Offset + line.Length; position++)
        {
            byte b = rom.ReadByte(position);
            if (b == 0x0A) return true;
            if (b is 4 or 5 or 6 or 7) position++;
        }
        return false;
    }

    // letters one line of the text box holds (measured in the game by Artemis, 2026-10-30, checked in BizHawk: a
    // 36-letter line runs into the lines under it and garbles them)
    public const int BoxWidth = 28;

    // lines one text box shows (checked in BizHawk, 2026-10-30: the 5th line of a 6-line text came out as a copy of the
    // first, the 6th as a copy of the second). more lines need another text step
    public const int BoxLines = 4;

    // the widest line of a text as the box shows it, written the way the Text page or the event editor shows it
    // ([NAME] / [Name] counted as "Maxim", pauses, voices and other codes take no room, a glyph takes one letter)
    public static int LongestLine(string text)
    {
        int best = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            string shown = System.Text.RegularExpressions.Regex.Replace(raw, @"\[(NAME|Name)\]", "Maxim");
            shown = System.Text.RegularExpressions.Regex.Replace(shown, @"\[(GLYPH|Glyph) [0-9A-Fa-f]{2}\]", "#");
            shown = System.Text.RegularExpressions.Regex.Replace(shown, @"\[[^\]]*\]", "");
            best = Math.Max(best, shown.Length);
        }
        return best;
    }

    // the width of one line as the box shows it (see LongestLine)
    private static int Width(string line)
    {
        string shown = System.Text.RegularExpressions.Regex.Replace(line, @"\[(NAME|Name)\]", "Maxim");
        shown = System.Text.RegularExpressions.Regex.Replace(shown, @"\[(GLYPH|Glyph) [0-9A-Fa-f]{2}\]", "#");
        return System.Text.RegularExpressions.Regex.Replace(shown, @"\[[^\]]*\]", "").Length;
    }

    // the text with every line wider than the box broken at spaces (a word wider than the box gets cut), then cut into
    // boxes of BoxLines lines. one box = the text as it was if it already fits
    public static List<string> FitToBoxes(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (Width(raw) <= BoxWidth) { lines.Add(raw); continue; }
            string current = "";
            foreach (var word in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string rest = word;
                string tryLine = current.Length == 0 ? rest : current + " " + rest;
                if (Width(tryLine) <= BoxWidth) { current = tryLine; continue; }
                if (current.Length > 0) lines.Add(current);
                // a word wider than the box on its own gets cut (codes kept whole)
                while (Width(rest) > BoxWidth)
                {
                    int k = 0, width = 0;
                    while (k < rest.Length && width < BoxWidth)
                    {
                        if (rest[k] == '[' && rest.IndexOf(']', k) is var close && close > k) { width += Width(rest[k..(close + 1)]); k = close + 1; }
                        else { width++; k++; }
                    }
                    lines.Add(rest[..k]); rest = rest[k..];
                }
                current = rest;
            }
            lines.Add(current);
        }
        bool trailing = lines.Count > 1 && lines[^1].Length == 0;
        if (trailing) lines.RemoveAt(lines.Count - 1);
        var boxes = new List<string>();
        for (int i = 0; i < lines.Count; i += BoxLines) boxes.Add(string.Join("\n", lines.Skip(i).Take(BoxLines)));
        if (boxes.Count == 0) boxes.Add("");
        if (trailing) boxes[^1] += "\n";
        return boxes;
    }

    // lines the text takes in its box (a trailing line break doesn't start one)
    public static int LineCount(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        int count = lines.Length;
        if (count > 1 && lines[^1].Length == 0) count--;
        return count;
    }

    // a soft warning if a line of the text is wider than the box or the text has more lines than the box shows, or "".
    // nothing gets changed, the text is written as typed
    public static string WidthWarning(string text)
    {
        var parts = new List<string>();
        int longest = LongestLine(text), lines = LineCount(text);
        if (longest > BoxWidth) parts.Add($"A line has {longest} letters; the box shows {BoxWidth} and a longer line spills into the lines under it. Break the line with Enter.");
        if (lines > BoxLines) parts.Add($"The text has {lines} lines; a box shows {BoxLines} and the lines after that come out garbled. Put the rest in another text (in the event editor, + under the step, Text).");
        return string.Join(" ", parts);
    }

    // text as the Text page shows it ([NAME], [PAUSE xx], [GLYPH xx], ending [WAIT] / [END] / [CHOICE]) as an event
    // step's text and end code (L2EventText: [Name], [Pause 20], [Voice 3], [Glyph 41])
    public static (string Text, byte End)? ToStepText(string text)
    {
        text = text.Replace("\r", "");
        byte end;
        if (text.EndsWith("[WAIT]")) { end = 0x01; text = text[..^6]; }
        else if (text.EndsWith("[END]")) { end = 0x00; text = text[..^5]; }
        else if (text.EndsWith("[CHOICE]")) { end = 0x0B; text = text[..^8]; }
        else return null;
        text = text.Replace("[NAME]", "[Name]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[GLYPH ([0-9A-Fa-f]{2})\]", m => $"[Glyph {m.Groups[1].Value.ToUpperInvariant()}]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[PAUSE ([0-9A-Fa-f]{2})\]", m =>
        {
            int value = Convert.ToInt32(m.Groups[1].Value, 16);
            return value == 0x80 ? "" : value > 0x80 ? $"[Pause {(value & 0x7F) * 10}]" : $"[Voice {value}]";
        });
        return (text, end);
    }

    // replaces a line's text, however long it needs to be: in place if it fits, else every event of the map that shows
    // the line gets written again with the new text in the map's added event space (the block moves to the event space
    // the first time, like the event editor does). returns what was done, throws with the reason if it can't
    public static string WriteText(RomBuffer rom, Line line, string text)
    {
        if (WriteLine(rom, line, text)) return "Written in place.";
        text = text.Replace("\r", "");
        var step = ToStepText(text) ?? throw new InvalidOperationException("The text has to end with [WAIT], [END] or [CHOICE].");
        if (L2EventText.Check(step.Text, rom) is { } why) throw new InvalidOperationException(why);
        bool Change(L2EventScript script)
        {
            bool hit = false;
            foreach (var textStep in script.Steps.Where(x => x.IsText && x.At >= 0 && x.At + x.Bytes.Length == line.Offset))
            {
                textStep.Text = step.Text; textStep.End = step.End; hit = true;
            }
            return hit;
        }
        // the start-up script
        int init = L2EventBlocks.InitScript(rom, line.Map);
        var initScript = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
        bool inInit = initScript.Problem == null && Change(initScript);
        string done = inInit ? L2EventBlocks.SaveInit(rom, line.Map, initScript) : "";
        // the events of the block that run it (events sharing a script share one copy)
        int block = Block(rom, line.Map);
        var changed = new Dictionary<L2EventBlocks.Entry, L2EventScript>();
        if (block >= 0)
        {
            var byOffset = new Dictionary<int, L2EventScript?>();
            foreach (var entry in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript))
            {
                if (!byOffset.TryGetValue(entry.Offset, out var script))
                {
                    script = L2EventScript.Read(rom, block + entry.Offset, block);
                    byOffset[entry.Offset] = script = script.Steps.Any(x => x.IsText && x.At + x.Bytes.Length == line.Offset) ? script : null;
                    if (script != null)
                    {
                        if (script.Problem != null) throw new InvalidOperationException($"An event showing this line can't be laid out again: {script.Problem}");
                        Change(script);
                    }
                }
                if (script != null) changed[entry] = script;
            }
            if (changed.Count > 0) done = L2EventBlocks.SaveAll(rom, line.Map, changed);
        }
        if (!inInit && changed.Count == 0) throw new InvalidOperationException("No event of the map shows this line, so it can only be written in place: shorten it.");
        return done;
    }

    // replaces a line's text in place: the new text can't be longer once encoded, and a shorter one gets filled up with
    // 04 80 (a pause of no time, like the event editor fills, 04 00 would set the letters' voice) and a space. returns
    // false if it doesn't fit, or if other text repeats bytes of it (CopiedFrom): then the events showing it get laid
    // out again and its old bytes stay for the text that copies them
    public static bool WriteLine(RomBuffer rom, Line line, string text)
    {
        var bytes = L2Text.Encode(rom, text).ToList();
        if (bytes.Count == 0 || bytes[^1] is not (0 or 1 or 0x0B)) return false;
        if (bytes.Count > line.Length) return false;
        byte last = bytes[^1];
        bytes.RemoveAt(bytes.Count - 1);
        int fill = line.Length - 1 - bytes.Count;
        if (fill % 2 == 1) { bytes.Add((byte)' '); fill--; }
        for (; fill > 0; fill -= 2) { bytes.Add(4); bytes.Add(0x80); }
        bytes.Add(last);
        if (CopiedFrom(rom, new[] { (line.Offset, bytes.ToArray()) }) >= 0) return false;
        rom.WriteBytes(line.Offset, bytes.ToArray());
        return true;
    }

    // where some text of the maps' scripts repeats a byte that writes would change: the file offset of the first such
    // 0A (n bytes of the text d bytes back), or -1. the game's texts use 0A for words said before (256 of the US game's
    // 285 repeat another line, mostly a speaker's name), so a line written over its old bytes would change those lines
    // too (map 02: "Erim:  Master." written as "Kirk: Hi" made "Erirk:  Yes, sir."). a 0A that the writes themselves
    // change doesn't count (its text gets written again). we only look at the scripts of the maps whose event block or
    // start-up script lies within reach (offsets go 64 KB on, 0A 4 KB back)
    public static int CopiedFrom(RomBuffer rom, IEnumerable<(int At, byte[] Bytes)> writes)
    {
        var changed = new HashSet<int>();
        foreach (var (at, bytes) in writes)
            for (int i = 0; i < bytes.Length; i++)
                if (at + i < rom.Length && rom.ReadByte(at + i) != bytes[i]) changed.Add(at + i);
        if (changed.Count == 0) return -1;
        int lo = changed.Min(), hi = changed.Max();
        bool Near(int offset) => offset >= 0 && offset <= hi + 0x1000 && offset + 0x10000 > lo;
        var seen = new HashSet<int>();
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            if (!Near(Block(rom, m)) && !Near(L2EventBlocks.InitScript(rom, m))) continue;
            List<(string, List<L2Script.Op>)> scripts;
            try { scripts = L2Script.MapScripts(rom, m); } catch (Exception) { continue; }
            foreach (var (_, ops) in scripts)
                foreach (var textOp in ops.Where(o => o.IsText && seen.Add(o.At)))
                    for (int position = textOp.At; position < textOp.At + textOp.Length && position + 2 < rom.Length; position++)
                    {
                        byte b = rom.ReadByte(position);
                        if (b == 0x0A)
                        {
                            byte low = rom.ReadByte(position + 1), high = rom.ReadByte(position + 2);
                            int source = position + 1 - (low | (high & 0x0F) << 8), count = (high >> 4) + 2;
                            bool rewritten = changed.Contains(position) || changed.Contains(position + 1) || changed.Contains(position + 2);
                            if (!rewritten && Enumerable.Range(source, count).Any(changed.Contains)) return position;
                            position += 2;
                        }
                        else if (b is 4 or 5 or 6 or 7) position++;
                    }
        }
        return -1;
    }
}
