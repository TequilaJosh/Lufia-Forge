namespace LufiaForge.Core.Lufia2;

// a lufia 2 map's info block (LEdit's L2MapHeader "presets", checked on the US rom). there's a 3-byte snes pointer
// per map at $CF:FCBC (file 0x27FCBC) to a block of u16 size + 21 u16 list offsets. each list holds records that
// start with an id byte, FF ends it. coordinates are in 16x16 blocks
public static class L2MapInfo
{
    public const int Pointers = 0x27FCBC;

    // file offset of the map's info pointer (u24). the game's table is at Pointers until New map moves it
    public static int PointerAt(RomBuffer rom, int map) => L2NewMaps.Entry(rom, L2MapTable.Info, map);

    // a thing on the map: a kind, an area (x1,y1)-(x2,y2) inclusive, and what it does
    public sealed record Entry(string Kind, int Id, int X1, int Y1, int X2, int Y2, string Detail, int DestMap = -1, int DestX = 0, int DestY = 0)
    {
        // file offset of the record
        public int At { get; init; }
    }

    // record size of the list at offset (+2 to +2A in the block), 0 if unknown
    public static int RecordSize(int offset) => offset switch
    {
        0x02 => 15, 0x04 => 10, 0x06 => 9, 0x0A => 5, 0x0C => 5, 0x0E => 3, 0x10 => 8, 0x14 => 5, 0x16 => 10,
        0x1A or 0x1C or 0x1E or 0x20 or 0x22 or 0x28 or 0x2A => 3, 0x24 => 5, 0x26 => 4, _ => 0,
    };

    // what the list at offset holds, for the merge window
    public static string ListName(int offset) => offset switch
    {
        0x0E => "arrival points", 0x12 => "walk paths", 0x14 => "areas", 0x10 => "character starts", 0x26 => "treasure",
        _ => Lists.FirstOrDefault(l => l.Offset == offset).Kind is { } k ? k.ToLowerInvariant() + "s" : $"list +{offset:X2}",
    };

    private static readonly (int Offset, string Kind, int Size)[] Lists =
    {
        (0x02, "Door", 15), (0x06, "Exit", 9), (0x0A, "Secret passage", 5), (0x0C, "Cutscene trigger", 5),
        (0x10, "Character start", 8), (0x1A, "Bomb spot", 3), (0x1C, "Sword spot", 3), (0x1E, "Arrow spot", 3),
        (0x20, "Action spot", 3), (0x26, "Treasure", 4), (0x28, "Movable object", 3), (0x2A, "Hammer spot", 3),
    };

    // the sprite each character of the map is made with. the map's start-up script (the map header's second u24,
    // from $87:8000) creates them with 68 / 7B id sprite (id 0x50 + n = character start n + 1).
    // we only read the script's own steps. we used to scan the bytes after its end too, and then the next maps'
    // scripts gave characters a sprite the game never makes them with.
    // the first create per character is kept, Stages has the others and the story flags they depend on.
    // sprites FB-FD come back as the new sprites they show on this map (L2Sprites.ToEditor)
    public static Dictionary<int, int> CharacterSprites(RomBuffer rom, int map)
    {
        var sprites = new Dictionary<int, int>();
        foreach (var c in Stages(rom, map)) sprites.TryAdd(c.Id, c.Sprite);
        return sprites;
    }

    // a create in a map's start-up script: the character, its sprite (lufia forge's number) and the story checks that
    // lead to it ("if ... else skip" around it) in words, or "" if it's always made
    public sealed record Create(int Id, int Sprite, string When, IReadOnlyList<(int Flag, bool On)> Flags)
    {
        // the step of the start-up script that makes it (0 is the first)
        public int Step { get; init; } = -1;
    }

    // every character create in the map's start-up script in order, with the flag checks it sits under (a check
    // with flags only, 15 / 6A / 14, that jumps forward over it). a character made in more than one place (one per
    // story stage, like Tia in Elcid and later a woman in her place) is listed for each
    public static List<Create> Stages(RomBuffer rom, int map)
    {
        var list = new List<Create>();
        int count = L2Sprites.Count(rom);
        L2EventScript script;
        try { int initScript = L2EventBlocks.InitScript(rom, map); script = L2EventScript.Read(rom, initScript, L2EventBlocks.InitBase(initScript)); }
        catch (Exception) { return list; }
        var stepIndex = new Dictionary<L2Step, int>();
        for (int i = 0; i < script.Steps.Count; i++) stepIndex[script.Steps[i]] = i;
        // the checks still open at each step: (until step index, flags that must hold)
        var open = new List<(int Until, List<(int Flag, bool On)> Need)>();
        for (int i = 0; i < script.Steps.Count; i++)
        {
            open.RemoveAll(o => o.Until <= i);
            var step = script.Steps[i];
            if (step.IsText) continue;
            var bytes = step.Bytes;
            int target = step.Jumps.Count == 1 && step.Jumps[0].Step != null && stepIndex.TryGetValue(step.Jumps[0].Step!, out int targetIndex) ? targetIndex : -1;
            if (target > i)
            {
                // the step jumps past [i + 1, target) if the condition holds, so the steps in there need it not to
                if (step.Op is 0x15 or 0x6A or L2ExtraFlags.OpIfOn or L2ExtraFlags.OpIfOff && bytes.Length >= 2)
                    open.Add((target, new() { (L2ExtraFlags.FlagOf(bytes), step.Op is 0x6A or L2ExtraFlags.OpIfOff) }));
                else if (step.Op == 0x14 && Conditions(bytes) is { } need) open.Add((target, need));
            }
            if (step.Op is 0x68 or 0x7B && bytes.Length >= 3 && bytes[1] is >= 0x50 and < 0x80 && L2Sprites.ToEditor(rom, map, bytes[2]) is var sprite && sprite < count)
            {
                var flags = open.SelectMany(o => o.Need).ToList();
                list.Add(new Create(bytes[1], sprite, string.Join(" and ", flags.Select(f => $"story flag {L2ExtraFlags.Number(f.Flag)} {(f.On ? "on" : "off")}")), flags) { Step = i });
            }
        }
        return list;
    }

    // a "14" with only flag tests, read as what the steps it jumps over need. "if A and B, else jump" skips them
    // unless A and B hold, "if A or B, then jump" skips them unless neither holds. other mixes (variables, "and"
    // with "then", "or" with "else") aren't read and give null
    private static List<(int Flag, bool On)>? Conditions(byte[] stepBytes)
    {
        var tests = new List<(int Flag, bool On)>();
        int joins = 0;   // 0x80 and, 0x40 or
        for (int i = 1; i + 1 < stepBytes.Length && stepBytes[i] != 0xFF;)
        {
            int test = stepBytes[i];
            if ((test & 0xC0) == 0xC0) return null;
            switch (test & 0x30)
            {
                case 0x00:
                    if ((test & 0xC0) != 0) { if (joins != 0 && joins != (test & 0xC0)) return null; joins = test & 0xC0; }
                    tests.Add((L2ExtraFlags.CheckFlag(test, stepBytes[i + 1]), (test & 1) == 0)); i += 2; break;
                case 0x30: return tests.Count > 0 && joins != 0x40 ? tests : null;                               // else jump
                case 0x20: return tests.Count > 0 && joins != 0x80 ? tests.Select(t => (t.Flag, !t.On)).ToList() : null;   // then jump
                default: return null;
            }
        }
        return null;
    }

    // the bytes FB, FC and FD (a map's new sprites) that the map's scripts make or change characters with
    // (34, 68, 7B), with or without its start-up script
    public static HashSet<int> NewSpriteBytesUsed(RomBuffer rom, int map, bool startUp)
    {
        var used = new HashSet<int>();
        foreach (var (title, ops) in L2Script.MapScripts(rom, map))
        {
            if (!startUp && title == "Start-up script") continue;
            foreach (var op in ops)
                if (!op.IsText && op.Length >= 3 && op.At + 2 < rom.Length && rom.ReadByte(op.At) is 0x34 or 0x68 or 0x7B &&
                    rom.ReadByte(op.At + 2) is var spriteByte && spriteByte >= L2Sprites.FirstSlot && spriteByte < L2Sprites.FirstSlot + L2Sprites.SlotsPerMap)
                    used.Add(spriteByte);
        }
        return used;
    }

    // the raw records of one list of a map's info block (the list at offset list, records of size bytes):
    // file offset and bytes of each
    public static List<(int At, byte[] Bytes)> Records(RomBuffer rom, int map, int list, int size)
    {
        var records = new List<(int, byte[])>();
        int block = Block(rom, map);
        if (block < 0 || block >= rom.Length) return records;
        int end = block + rom.ReadUInt16Le(block);
        int offset = block + rom.ReadUInt16Le(block + list);
        for (int guard = 0; guard < 128 && offset > block && offset + size <= end + 1 && rom.ReadByte(offset) != 0xFF; guard++, offset += size) records.Add((offset, rom.ReadBytes(offset, size)));
        return records;
    }

    public const int Exits = 0x06, Doors = 0x02, Triggers = 0x0C, Characters = 0x10, Points = 0x0E, Paths = 0x12, Areas = 0x14;

    // adds a record to a list of a map's info block. the block gets copied to the added space with the list rewritten
    // at its end. the game copies the block to $7E:F000 and reads the lists by offset, so any bank works, but it has
    // to stay inside one bank and under 4 KB. returns false if it'd be too big
    public static bool AddRecord(RomBuffer rom, int map, int list, int size, byte[] record)
    {
        int block = Block(rom, map);
        if (block < 0) return false;
        int blockSize = rom.ReadUInt16Le(block);
        var records = Records(rom, map, list, size);
        var data = rom.ReadBytes(block, blockSize).ToList();
        int listAt = data.Count;
        foreach (var (_, bytes) in records) data.AddRange(bytes);
        data.AddRange(record);
        data.Add(0xFF);
        if (data.Count > 0x0FFF) return false;
        data[0] = (byte)data.Count; data[1] = (byte)(data.Count >> 8);
        data[list] = (byte)listAt; data[list + 1] = (byte)(listAt >> 8);
        int newAt = L2Rom.Allocate(rom, data.Count + 1, upper: true);
        rom.WriteBytes(newAt, data.Append((byte)0).ToArray());
        int snesAddress = L2Rom.FileToSnes(newAt);
        rom.WriteBytes(PointerAt(rom, map), new[] { (byte)snesAddress, (byte)(snesAddress >> 8), (byte)(snesAddress >> 16) });
        return true;
    }

    public static int Block(RomBuffer rom, int map)
    {
        int pointer = (int)rom.ReadUInt24Le(PointerAt(rom, map));
        return pointer == 0 ? -1 : L2Rom.SnesToFile(pointer);
    }

    // points an exit at another map and place (record bytes 6, 7 = x, y and 8 = map)
    public static void WriteExit(RomBuffer rom, Entry exit, int map, int x, int y)
    {
        rom.WriteByte(exit.At + 6, (byte)x);
        rom.WriteByte(exit.At + 7, (byte)y);
        rom.WriteByte(exit.At + 8, (byte)map);
    }

    public static List<Entry> Read(RomBuffer rom, int map)
    {
        var list = new List<Entry>();
        int block = Block(rom, map);
        if (block < 0 || block >= rom.Length) return list;
        int size = rom.ReadUInt16Le(block);
        foreach (var (listOffset, kind, recordSize) in Lists)
        {
            int offset = block + rom.ReadUInt16Le(block + listOffset);
            for (int guard = 0; guard < 255 && offset > block && offset < block + size && rom.ReadByte(offset) != 0xFF; guard++, offset += recordSize)
            {
                var r = rom.ReadBytes(offset, recordSize);
                int recordAt = offset;
                list.Add((kind switch
                {
                    "Door" => new Entry(kind, r[0], r[1], r[2], r[3], r[4], $"opens onto ({r[5]},{r[6]})-({r[7]},{r[8]})"),
                    "Exit" => new Entry(kind, r[0], r[1], r[2], r[3], r[4], $"to map {r[8]:X2} at ({r[6]},{r[7]})", r[8], r[6], r[7]),
                    "Secret passage" or "Cutscene trigger" => new Entry(kind, r[0], r[1], r[2], r[3], r[4], kind == "Cutscene trigger" ? $"starts trigger {r[0]:X2}'s scene" : ""),
                    "Character start" => new Entry(kind, r[0], r[1], r[2], r[1], r[2], $"character {r[0] + 0x4F:X2}, walks in ({r[3]},{r[4]})-({r[5]},{r[6]})"),
                    "Treasure" => new Entry(kind, r[0], r[1], r[2], r[1], r[2], ""),
                    _ => new Entry(kind, r[0], r[1], r[2], r[1], r[2], ""),
                }) with { At = recordAt });
            }
        }
        return list;
    }
}
