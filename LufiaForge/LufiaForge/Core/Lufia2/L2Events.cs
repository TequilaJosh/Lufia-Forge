namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II map events (LEdit's Dialogs.txt opcode list, US addresses). The map header table at $87:8000 gives each
/// map's event block ("PH") as a 24-bit offset added to $87:8000 (SNES arithmetic). The block's header holds u16
/// offsets (from "PH") to lists of (id, u16 script offset): +2 scripts run as the map loads, +6 talking to a
/// character (ids 0x50+), +8 stepping on a trigger. Scripts are commands; 08, 13 xx and 61-67 start text (a
/// character speaks) that runs until 01 (back to the script) or 00 (the end).
/// </summary>
public static class L2Events
{
    public const int HeaderTable = 0x38000;

    /// <summary>Argument bytes of each command (-1: not known; walking stops there), from LEdit's notes. Some
    /// lengths are uncertain, so <see cref="Lines"/> also scans the block for text (<see cref="ScanLines"/>).</summary>
    private static readonly int[] Args = BuildArgs();

    private static int[] BuildArgs()
    {
        var a = Enumerable.Repeat(-1, 256).ToArray();
        void Set(int n, params int[] ops) { foreach (var o in ops) a[o] = n; }
        Set(0, 0x00, 0x01, 0x03, 0x08, 0x39, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x42, 0x4E, 0x50, 0x51, 0x56, 0x57, 0x6B, 0x70, 0x71, 0x75, 0x76,
               0x78, 0x79, 0x82, 0x83, 0x84, 0x88, 0x8A, 0x97, 0xA4, 0xA5, 0xA6, 0xAA, 0xAD, 0xB5, 0xB8, 0xBB, 0xBC, 0xC5, 0xC7, 0xCC);
        for (int o = 0x61; o <= 0x67; o++) a[o] = 0;
        Set(1, 0x04, 0x07, 0x13, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x25, 0x28, 0x2B, 0x2C, 0x2D, 0x2E, 0x36, 0x37, 0x38, 0x3B, 0x47, 0x49, 0x4A,
               0x4B, 0x4C, 0x4D, 0x4F, 0x52, 0x53, 0x54, 0x55, 0x58, 0x59, 0x5E, 0x5F, 0x60, 0x69, 0x6F, 0x74, 0x77, 0x7C, 0x7D, 0x7E, 0x7F,
               0x80, 0x81, 0x89, 0x92, 0x99, 0x9A, 0x9D, 0x9E, 0xA2, 0xA7, 0xA8, 0xAB, 0xAE, 0xB0, 0xB3, 0xB7, 0xB9, 0xBA, 0xBE, 0xCB);
        Set(2, 0x0A, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x26, 0x27, 0x30, 0x31, 0x32, 0x33, 0x34, 0x3A, 0x41, 0x43, 0x48,
               0x5B, 0x68, 0x73, 0x7A, 0x7B, 0x85, 0x86, 0x87, 0x91, 0x98, 0xA1, 0xA3, 0xA9, 0xAC, 0xAF, 0xB1, 0xB2, 0xB6, 0xBD, 0xC1, 0xC4, 0xC6);
        Set(3, 0x15, 0x16, 0x29, 0x2A, 0x2F, 0x35, 0x44, 0x45, 0x46, 0x5A, 0x6A, 0x6C, 0x8B, 0x8C, 0x8E, 0x8F, 0x90, 0x93, 0x94, 0x95, 0x96,
               0x9C, 0x9F, 0xB4, 0xBF, 0xC0, 0xCA);
        Set(5, 0x9B);
        Set(6, 0x14, 0x8D, 0xA0, 0xC2, 0xC3);
        return a;
    }

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

    /// <summary>The scripts a block starts from (load, talk, trigger lists), as offsets from the block.</summary>
    public static List<(string Kind, int Id, int Offset)> Entries(RomBuffer rom, int block)
    {
        var list = new List<(string, int, int)>();
        foreach (var (kind, head) in new[] { ("load", 2), ("talk", 6), ("trigger", 8) })
        {
            int at = block + rom.ReadUInt16Le(block + head);
            for (int i = 0; i < 128; i++)
            {
                byte id = rom.ReadByte(at + i * 3);
                if (id == 0xFF) break;
                list.Add((kind, id, rom.ReadUInt16Le(at + i * 3 + 1)));
            }
        }
        return list;
    }

    /// <summary>All the text a map's scripts show, in order of where it sits.</summary>
    public static List<Line> Lines(RomBuffer rom, int map)
    {
        int block = Block(rom, map);
        var lines = new SortedDictionary<int, Line>();
        if (block < 0) return new();
        int limit = block + 0x8000;
        var seen = new HashSet<int>();
        var todo = new Stack<int>(Entries(rom, block).Select(e => block + e.Offset));
        while (todo.Count > 0)
        {
            int p = todo.Pop();
            for (int guard = 0; guard < 4000 && p > block && p < limit && seen.Add(p); guard++)
            {
                byte op = rom.ReadByte(p);
                string? speaker = op == 0x08 ? "(the character)" : op is >= 0x61 and <= 0x67 ? Speakers[op - 0x61] : op == 0x13 ? $"character {rom.ReadByte(p + 1):X2}" : null;
                if (speaker != null)
                {
                    int t = p + 1 + (op == 0x13 ? 1 : 0);
                    string text = L2Text.Decode(rom, t, out int end);
                    if (!lines.ContainsKey(t)) lines[t] = new Line(map, t, end - t, speaker, text);
                    if (rom.ReadByte(end - 1) == 0) break;   // 00: the script ends with the text
                    p = end;
                    continue;
                }
                if (op == 0x00 || op == 0x42 || op == 0xBB) break;
                int n = Args[op];
                if (op == 0x10) { int k = rom.ReadByte(p + 1); for (int c = 0; c < k; c++) todo.Push(block + rom.ReadUInt16Le(p + 2 + c * 2)); n = 1 + 2 * k; }
                else if (op == 0x12) { int k = rom.ReadByte(p + 2); for (int c = 0; c < k; c++) todo.Push(block + rom.ReadUInt16Le(p + 3 + c * 2)); n = 2 + 2 * k; }
                else if (op == 0x1C) { p = block + rom.ReadUInt16Le(p + 1); continue; }
                else if (op is 0x15 or 0x6A) todo.Push(block + rom.ReadUInt16Le(p + 2));
                if (n < 0) break;
                p += 1 + n;
            }
        }
        foreach (var l in ScanLines(rom, map, block)) lines.TryAdd(l.Offset, l);
        return lines.Values.ToList();
    }

    /// <summary>Text found by looking through the whole block: a speaker command (08, 13 xx, 61-67) followed by text
    /// that decodes cleanly (letters, the dictionary, the known control codes) and ends with 00 or 01. Finds the lines
    /// behind commands whose length isn't known.</summary>
    public static List<Line> ScanLines(RomBuffer rom, int map, int block)
    {
        var found = new List<Line>();
        int size = BlockSize(rom, map, block);
        for (int p = block + 0x0E; p < block + size; p++)
        {
            byte op = rom.ReadByte(p);
            if (!(op == 0x08 || op is >= 0x61 and <= 0x67 || op == 0x13)) continue;
            // (61-67 are also the letters a-g: one that follows a letter or a space is inside a word)
            byte before = rom.ReadByte(p - 1);
            if (before == 0x20 || before is >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A) continue;
            int t = p + 1 + (op == 0x13 ? 1 : 0);
            if (!LooksLikeText(rom, t, block + size, out int end)) continue;
            string speaker = op == 0x08 ? "(the character)" : op == 0x13 ? $"character {rom.ReadByte(p + 1):X2}" : Speakers[op - 0x61];
            found.Add(new Line(map, t, end - t, speaker, L2Text.Decode(rom, t, out _)));
            p = end - 1;
        }
        return found;
    }

    /// <summary>The block's length: up to the next map's block when that follows it, else 0x3000 at most.</summary>
    private static int BlockSize(RomBuffer rom, int map, int block)
    {
        int next = int.MaxValue;
        for (int m = 0; m < 0xF2; m++) { int b = Block(rom, m); if (b > block && b < next) next = b; }
        return Math.Min(0x3000, next == int.MaxValue ? 0x3000 : next - block);
    }

    private static bool LooksLikeText(RomBuffer rom, int at, int limit, out int end)
    {
        end = at;
        int letters = 0, p = at;
        while (p < limit && p - at < 400)
        {
            byte b = rom.ReadByte(p++);
            if (b is 0 or 1) { end = p; return letters >= 3 && p - at >= 4; }
            if (b is 4 or 5 or 6) { p++; letters += b == 4 ? 0 : 3; continue; }
            if (b >= 0x80) { letters += 3; continue; }
            if (b is 3 or 9 or 0x0B or >= 0x0C and <= 0x0F) continue;
            if (b is >= 0x20 and < 0x7F) { if (char.IsLetter((char)b)) letters++; continue; }
            return false;
        }
        return false;
    }

    /// <summary>Replaces a line's text in place: the new text must not be longer once encoded; a shorter one is
    /// filled up with 04 00 (a pause of no time) and a space. Returns false when it doesn't fit.</summary>
    public static bool WriteLine(RomBuffer rom, Line line, string text)
    {
        var bytes = L2Text.Encode(rom, text).ToList();
        if (bytes.Count == 0 || bytes[^1] is not (0 or 1)) return false;
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
