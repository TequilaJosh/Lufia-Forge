namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II map events (the commands are in <see cref="L2Script"/>). The map header table at $87:8000 gives each
/// map's event block ("PH") as a 24-bit offset added to $87:8000 (SNES arithmetic). The block's header holds u16
/// offsets (from "PH") to lists of (id, u16 script offset): +2 scripts run as the map loads, +6 talking to a
/// character (ids 0x50+), +8 stepping on a trigger. Scripts are commands; 08, 13 xx and 61-67 start text (a
/// character speaks) that runs until 01 (back to the script), 0B (a choice) or 00 (the end).
/// </summary>
public static class L2Events
{
    public const int HeaderTable = 0x38000;

    public static readonly string[] Speakers = { "Maxim", "Selan", "Guy", "Artea", "Tia", "Dekar", "Lexis" };

    /// <summary>A piece of text in a map's events.</summary>
    public sealed record Line(int Map, int Offset, int Length, string Speaker, string Text);

    /// <summary>File offset of map <paramref name="map"/>'s event block, or -1.</summary>
    public static int Block(RomBuffer rom, int map)
    {
        int e = HeaderTable + 0x10 + map * 8;
        int off = rom.ReadByte(e) | rom.ReadByte(e + 1) << 8 | rom.ReadByte(e + 2) << 16;
        int at = L2Rom.SnesToFile(0x878000 + off);
        return at + 2 < rom.Length && rom.ReadByte(at) == 'P' && rom.ReadByte(at + 1) == 'H' ? at : -1;
    }

    /// <summary>The scripts a block starts from, as offsets from the block: the lists at +2 (run as the map loads),
    /// +4 (scenes, started by the map's cutscene triggers), +6 (talking to character id), +8 (stepping on trigger id),
    /// +A and +C (rarely used).</summary>
    public static List<(string Kind, int Id, int Offset)> Entries(RomBuffer rom, int block)
    {
        var list = new List<(string, int, int)>();
        foreach (var (kind, head) in new[] { ("load", 2), ("scene", 4), ("talk", 6), ("trigger", 8), ("list A", 0x0A), ("list C", 0x0C) })
        {
            int at = block + rom.ReadUInt16Le(block + head);
            for (int i = 0; i < 128; i++)
            {
                byte id = rom.ReadByte(at + i * 3);
                if (id == 0xFF) break;
                int off = rom.ReadUInt16Le(at + i * 3 + 1);
                if (head == 2 && id == 3) continue;   // load entry 03 isn't a script: 4 bytes (a number, the u16 key item for this map's locked doors, 00)
                list.Add((kind, id, off));
            }
        }
        return list;
    }

    /// <summary>All the text a map's scripts show, in order of where it sits (every script of the map walked with
    /// <see cref="L2Script"/>; the speaker is the command before the text).</summary>
    public static List<Line> Lines(RomBuffer rom, int map)
    {
        var lines = new SortedDictionary<int, Line>();
        foreach (var (_, ops) in L2Script.MapScripts(rom, map))
        {
            var byEnd = ops.Where(o => !o.IsText).GroupBy(o => o.At + o.Length).ToDictionary(g => g.Key, g => g.First());
            foreach (var t in ops.Where(o => o.IsText))
            {
                if (lines.ContainsKey(t.At)) continue;
                string speaker = "(text)";
                if (byEnd.TryGetValue(t.At, out var before))
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
                lines[t.At] = new Line(map, t.At, t.Length, speaker, t.Text);
            }
        }
        return lines.Values.ToList();
    }

    /// <summary>The block's length: up to the next map's block when that follows it, else 0x3000 at most.</summary>
    public static int BlockSize(RomBuffer rom, int map, int block)
    {
        int next = int.MaxValue;
        for (int m = 0; m < 0xF2; m++) { int b = Block(rom, m); if (b > block && b < next) next = b; }
        return Math.Min(0x3000, next == int.MaxValue ? 0x3000 : next - block);
    }

    /// <summary>Whether the line repeats earlier text with 0A (written back, that part takes its full length).</summary>
    public static bool HasBackReference(RomBuffer rom, Line line)
    {
        for (int p = line.Offset; p < line.Offset + line.Length; p++)
        {
            byte b = rom.ReadByte(p);
            if (b == 0x0A) return true;
            if (b is 4 or 5 or 6 or 7) p++;
        }
        return false;
    }

    /// <summary>Letters a line of the text box holds (measured in the game by Artemis, 2026-10-30; checked in BizHawk:
    /// a 36-letter line runs into the lines under it and garbles them).</summary>
    public const int BoxWidth = 28;

    /// <summary>Lines one text box shows (checked in BizHawk, 2026-10-30: the 5th line of a 6-line text came out as a
    /// copy of the first, the 6th as a copy of the second). More lines need another text step.</summary>
    public const int BoxLines = 4;

    /// <summary>The widest line of a text as the box shows it, written as the Text page or the event editor shows it
    /// ([NAME] / [Name] counted as "Maxim"; pauses, voices and other codes take no room, a glyph one letter).</summary>
    public static int LongestLine(string text)
    {
        int best = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            string s = System.Text.RegularExpressions.Regex.Replace(raw, @"\[(NAME|Name)\]", "Maxim");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[(GLYPH|Glyph) [0-9A-Fa-f]{2}\]", "#");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]*\]", "");
            best = Math.Max(best, s.Length);
        }
        return best;
    }

    /// <summary>Lines the text takes in its box (a trailing line break doesn't start one).</summary>
    public static int LineCount(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        int n = lines.Length;
        if (n > 1 && lines[^1].Length == 0) n--;
        return n;
    }

    /// <summary>A soft warning when a line of the text is wider than the box or the text has more lines than the box
    /// shows, or "". Nothing is changed: the text is written as typed.</summary>
    public static string WidthWarning(string text)
    {
        var parts = new List<string>();
        int n = LongestLine(text), lines = LineCount(text);
        if (n > BoxWidth) parts.Add($"A line has {n} letters; the box shows {BoxWidth} and a longer line spills into the lines under it. Break the line with Enter.");
        if (lines > BoxLines) parts.Add($"The text has {lines} lines; a box shows {BoxLines} and the lines after that come out garbled. Put the rest in another text (in the event editor, + under the step, Text).");
        return string.Join(" ", parts);
    }

    /// <summary>Text as the Text page shows it ([NAME], [PAUSE xx], [GLYPH xx], ending [WAIT] / [END] / [CHOICE]) as an
    /// event step's text and end code (<see cref="L2EventText"/>: [Name], [Pause 20], [Voice 3], [Glyph 41]).</summary>
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
            int v = Convert.ToInt32(m.Groups[1].Value, 16);
            return v == 0x80 ? "" : v > 0x80 ? $"[Pause {(v & 0x7F) * 10}]" : $"[Voice {v}]";
        });
        return (text, end);
    }

    /// <summary>Replaces a line's text, as long as it needs: in place when it fits, else every event of the map that
    /// shows the line is written again with the new text in the map's added event space (the block moves to the event
    /// space the first time, as the event editor does). Returns what was done; throws with the reason when it can't.</summary>
    public static string WriteText(RomBuffer rom, Line line, string text)
    {
        if (WriteLine(rom, line, text)) return "Written in place.";
        text = text.Replace("\r", "");
        var step = ToStepText(text) ?? throw new InvalidOperationException("The text has to end with [WAIT], [END] or [CHOICE].");
        if (L2EventText.Check(step.Text) is { } why) throw new InvalidOperationException(why);
        bool Change(L2EventScript s)
        {
            bool hit = false;
            foreach (var st in s.Steps.Where(x => x.IsText && x.At >= 0 && x.At + x.Bytes.Length == line.Offset))
            {
                st.Text = step.Text; st.End = step.End; hit = true;
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
            foreach (var e in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript))
            {
                if (!byOffset.TryGetValue(e.Offset, out var s))
                {
                    s = L2EventScript.Read(rom, block + e.Offset, block);
                    byOffset[e.Offset] = s = s.Steps.Any(x => x.IsText && x.At + x.Bytes.Length == line.Offset) ? s : null;
                    if (s != null)
                    {
                        if (s.Problem != null) throw new InvalidOperationException($"An event showing this line can't be laid out again: {s.Problem}");
                        Change(s);
                    }
                }
                if (s != null) changed[e] = s;
            }
            if (changed.Count > 0) done = L2EventBlocks.SaveAll(rom, line.Map, changed);
        }
        if (!inInit && changed.Count == 0) throw new InvalidOperationException("No event of the map shows this line, so it can only be written in place: shorten it.");
        return done;
    }

    /// <summary>Replaces a line's text in place: the new text must not be longer once encoded; a shorter one is
    /// filled up with 04 00 (a pause of no time) and a space. Returns false when it doesn't fit.</summary>
    public static bool WriteLine(RomBuffer rom, Line line, string text)
    {
        var bytes = L2Text.Encode(rom, text).ToList();
        if (bytes.Count == 0 || bytes[^1] is not (0 or 1 or 0x0B)) return false;
        if (bytes.Count > line.Length) return false;
        byte last = bytes[^1];
        bytes.RemoveAt(bytes.Count - 1);
        int fill = line.Length - 1 - bytes.Count;
        if (fill % 2 == 1) { bytes.Add((byte)' '); fill--; }
        for (; fill > 0; fill -= 2) { bytes.Add(4); bytes.Add(0); }
        bytes.Add(last);
        rom.WriteBytes(line.Offset, bytes.ToArray());
        return true;
    }
}
