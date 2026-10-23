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
