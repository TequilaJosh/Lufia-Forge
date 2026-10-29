namespace LufiaForge.Core.Lufia2;

/// <summary>
/// A Lufia II map's info block (LEdit's L2MapHeader "presets", checked on the US ROM): a 3-byte SNES pointer per map
/// at $CF:FCBC (file 0x27FCBC) to a block of u16 size + 21 u16 list offsets; each list holds records starting with
/// an id byte, FF ends it. Coordinates are in 16x16 blocks.
/// </summary>
public static class L2MapInfo
{
    public const int Pointers = 0x27FCBC;

    /// <summary>A thing on the map: a kind, an area (x1,y1)-(x2,y2) inclusive, and what it does.</summary>
    public sealed record Entry(string Kind, int Id, int X1, int Y1, int X2, int Y2, string Detail, int DestMap = -1, int DestX = 0, int DestY = 0)
    {
        /// <summary>File offset of the record.</summary>
        public int At { get; init; }
    }

    private static readonly (int Offset, string Kind, int Size)[] Lists =
    {
        (0x02, "Door", 15), (0x06, "Exit", 9), (0x0A, "Secret passage", 5), (0x0C, "Cutscene trigger", 5),
        (0x10, "Character start", 8), (0x1A, "Bomb spot", 3), (0x1C, "Sword spot", 3), (0x1E, "Arrow spot", 3),
        (0x20, "Action spot", 3), (0x26, "Treasure", 4), (0x28, "Movable object", 3), (0x2A, "Hammer spot", 3),
    };

    /// <summary>The sprite each character of the map is made with: the map's start-up script (the map header's second
    /// u24, from $87:8000) creates them with 68 / 7B id sprite (id 0x50 + n = character start n + 1). Only the script's
    /// own steps are read (before, the bytes after its end were scanned too, so the next maps' scripts gave characters
    /// the game never makes a sprite of theirs). The first create per character is kept; <see cref="Stages"/> has the
    /// others and the story flags they depend on. Sprites FB-FD are given as the new sprites they show on this map
    /// (<see cref="L2Sprites.ToEditor"/>).</summary>
    public static Dictionary<int, int> CharacterSprites(RomBuffer rom, int map)
    {
        var sprites = new Dictionary<int, int>();
        foreach (var c in Stages(rom, map)) sprites.TryAdd(c.Id, c.Sprite);
        return sprites;
    }

    /// <summary>A create in a map's start-up script: the character, its sprite (Lufia Forge's number) and the story
    /// checks that lead to it ("if ... else skip" around it), in words, or "" when it is always made.</summary>
    public sealed record Create(int Id, int Sprite, string When, IReadOnlyList<(int Flag, bool On)> Flags);

    /// <summary>Every character create of the map's start-up script in order, with the flag checks it sits under:
    /// a check (15 / 6A / 14 with flags only) that jumps forward over it. A character made in more than one place (a
    /// story stage each, like Tia in Elcid, later a woman in her place) is listed for each.</summary>
    public static List<Create> Stages(RomBuffer rom, int map)
    {
        var list = new List<Create>();
        int count = L2Sprites.Count(rom);
        L2EventScript s;
        try { int init = L2EventBlocks.InitScript(rom, map); s = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init)); }
        catch (Exception) { return list; }
        var at = new Dictionary<L2Step, int>();
        for (int i = 0; i < s.Steps.Count; i++) at[s.Steps[i]] = i;
        // the checks still open at each step: (until step index, flags that must hold)
        var open = new List<(int Until, List<(int Flag, bool On)> Need)>();
        for (int i = 0; i < s.Steps.Count; i++)
        {
            open.RemoveAll(o => o.Until <= i);
            var st = s.Steps[i];
            if (st.IsText) continue;
            var b = st.Bytes;
            int target = st.Jumps.Count == 1 && st.Jumps[0].Step != null && at.TryGetValue(st.Jumps[0].Step!, out int t) ? t : -1;
            if (target > i)
            {
                // the step jumps past [i + 1, target) when the condition holds: the steps there need it not to
                if (st.Op is 0x15 or 0x6A && b.Length >= 2) open.Add((target, new() { (b[1], st.Op == 0x6A) }));
                else if (st.Op == 0x14 && Conditions(b) is { } need) open.Add((target, need));
            }
            if (st.Op is 0x68 or 0x7B && b.Length >= 3 && b[1] is >= 0x50 and < 0x80 && L2Sprites.ToEditor(rom, map, b[2]) is var spr && spr < count)
            {
                var flags = open.SelectMany(o => o.Need).ToList();
                list.Add(new Create(b[1], spr, string.Join(" and ", flags.Select(f => $"story flag {f.Flag:X2} {(f.On ? "on" : "off")}")), flags));
            }
        }
        return list;
    }

    /// <summary>"14" with only flag tests, as what the steps it jumps over need: "if A and B, else jump" skips them
    /// unless A and B hold; "if A or B, then jump" skips them unless neither holds. Other mixes (variables, "and" with
    /// "then", "or" with "else") aren't read: null.</summary>
    private static List<(int Flag, bool On)>? Conditions(byte[] b)
    {
        var tests = new List<(int Flag, bool On)>();
        int joins = 0;   // 0x80 and, 0x40 or
        for (int i = 1; i + 1 < b.Length && b[i] != 0xFF;)
        {
            int c = b[i];
            if ((c & 0xC0) == 0xC0) return null;
            switch (c & 0x30)
            {
                case 0x00:
                    if ((c & 0xC0) != 0) { if (joins != 0 && joins != (c & 0xC0)) return null; joins = c & 0xC0; }
                    tests.Add((b[i + 1], (c & 1) == 0)); i += 2; break;
                case 0x30: return tests.Count > 0 && joins != 0x40 ? tests : null;                               // else jump
                case 0x20: return tests.Count > 0 && joins != 0x80 ? tests.Select(t => (t.Flag, !t.On)).ToList() : null;   // then jump
                default: return null;
            }
        }
        return null;
    }

    /// <summary>The bytes FB, FC and FD (a map's new sprites) that the map's scripts make or change characters with
    /// (34, 68, 7B), with or without its start-up script.</summary>
    public static HashSet<int> NewSpriteBytesUsed(RomBuffer rom, int map, bool startUp)
    {
        var used = new HashSet<int>();
        foreach (var (title, ops) in L2Script.MapScripts(rom, map))
        {
            if (!startUp && title == "Start-up script") continue;
            foreach (var o in ops)
                if (!o.IsText && o.Length >= 3 && o.At + 2 < rom.Length && rom.ReadByte(o.At) is 0x34 or 0x68 or 0x7B &&
                    rom.ReadByte(o.At + 2) is var b && b >= L2Sprites.FirstSlot && b < L2Sprites.FirstSlot + L2Sprites.SlotsPerMap)
                    used.Add(b);
        }
        return used;
    }

    /// <summary>The raw records of one list of a map's info block (list at <paramref name="list"/>, records of
    /// <paramref name="size"/> bytes): file offset and bytes of each.</summary>
    public static List<(int At, byte[] Bytes)> Records(RomBuffer rom, int map, int list, int size)
    {
        var l = new List<(int, byte[])>();
        int b = Block(rom, map);
        if (b < 0 || b >= rom.Length) return l;
        int end = b + rom.ReadUInt16Le(b);
        int p = b + rom.ReadUInt16Le(b + list);
        for (int guard = 0; guard < 128 && p > b && p + size <= end + 1 && rom.ReadByte(p) != 0xFF; guard++, p += size) l.Add((p, rom.ReadBytes(p, size)));
        return l;
    }

    public const int Exits = 0x06, Doors = 0x02, Triggers = 0x0C, Characters = 0x10, Points = 0x0E, Paths = 0x12, Areas = 0x14;

    /// <summary>Adds a record to a list of a map's info block. The block is copied to the added space with the list
    /// rewritten at its end (the game copies the block to $7E:F000 and reads the lists by offset; any bank works, it must
    /// stay inside one bank and under 4 KB). Returns false when it would be too big.</summary>
    public static bool AddRecord(RomBuffer rom, int map, int list, int size, byte[] record)
    {
        int b = Block(rom, map);
        if (b < 0) return false;
        int blockSize = rom.ReadUInt16Le(b);
        var records = Records(rom, map, list, size);
        var data = rom.ReadBytes(b, blockSize).ToList();
        int listAt = data.Count;
        foreach (var (_, bytes) in records) data.AddRange(bytes);
        data.AddRange(record);
        data.Add(0xFF);
        if (data.Count > 0x0FFF) return false;
        data[0] = (byte)data.Count; data[1] = (byte)(data.Count >> 8);
        data[list] = (byte)listAt; data[list + 1] = (byte)(listAt >> 8);
        int at = L2Rom.Allocate(rom, data.Count + 1, upper: true);
        rom.WriteBytes(at, data.Append((byte)0).ToArray());
        int snes = L2Rom.FileToSnes(at);
        rom.WriteBytes(Pointers + map * 3, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        return true;
    }

    public static int Block(RomBuffer rom, int map)
    {
        int a = rom.ReadByte(Pointers + map * 3) | rom.ReadByte(Pointers + map * 3 + 1) << 8 | rom.ReadByte(Pointers + map * 3 + 2) << 16;
        return a == 0 ? -1 : L2Rom.SnesToFile(a);
    }

    /// <summary>Points an exit at another map and place (record bytes 6, 7 = x, y; 8 = map).</summary>
    public static void WriteExit(RomBuffer rom, Entry exit, int map, int x, int y)
    {
        rom.WriteByte(exit.At + 6, (byte)x);
        rom.WriteByte(exit.At + 7, (byte)y);
        rom.WriteByte(exit.At + 8, (byte)map);
    }

    public static List<Entry> Read(RomBuffer rom, int map)
    {
        var list = new List<Entry>();
        int b = Block(rom, map);
        if (b < 0 || b >= rom.Length) return list;
        int size = rom.ReadUInt16Le(b);
        foreach (var (off, kind, sz) in Lists)
        {
            int p = b + rom.ReadUInt16Le(b + off);
            for (int guard = 0; guard < 255 && p > b && p < b + size && rom.ReadByte(p) != 0xFF; guard++, p += sz)
            {
                var r = rom.ReadBytes(p, sz);
                int at = p;
                list.Add((kind switch
                {
                    "Door" => new Entry(kind, r[0], r[1], r[2], r[3], r[4], $"opens onto ({r[5]},{r[6]})-({r[7]},{r[8]})"),
                    "Exit" => new Entry(kind, r[0], r[1], r[2], r[3], r[4], $"to map {r[8]:X2} at ({r[6]},{r[7]})", r[8], r[6], r[7]),
                    "Secret passage" or "Cutscene trigger" => new Entry(kind, r[0], r[1], r[2], r[3], r[4], kind == "Cutscene trigger" ? $"starts trigger {r[0]:X2}'s scene" : ""),
                    "Character start" => new Entry(kind, r[0], r[1], r[2], r[1], r[2], $"character {r[0] + 0x4F:X2}, walks in ({r[3]},{r[4]})-({r[5]},{r[6]})"),
                    "Treasure" => new Entry(kind, r[0], r[1], r[2], r[1], r[2], ""),
                    _ => new Entry(kind, r[0], r[1], r[2], r[1], r[2], ""),
                }) with { At = at });
            }
        }
        return list;
    }
}
