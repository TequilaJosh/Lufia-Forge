namespace LufiaForge.Core.Lufia2;

// a map's whole puzzle block (L2Puzzles) as a model we can change and write back: the entries of the 16 categories
// (category 0A = object groups, byte lists) and every command the entries reach, in their order in the rom (a command
// that doesn't end or jump away runs on into the next one, so the order has to be kept), each jump pointing at the
// command it goes to. written back, the block is laid out again (header of 16 list offsets, the lists, the groups,
// the commands, every offset counted from the block start) in the event space (SeesRam), and the map's entry in the
// table at $8F:8000 (u16 lo, u8 hi: bank $8F + hi, address $8000 + lo) points at it. bytes no entry reaches get left out
public sealed class L2PuzzleBlock
{
    public sealed class Cmd
    {
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        // the commands its jump targets point at, in the order of L2Puzzles.Ops' Jumps (or the one target at the end
        // of an FF list)
        public List<Cmd> Targets { get; } = new();
        // file offset it was read from (-1 = new)
        public int At { get; init; } = -1;
        public int Op => Bytes.Length > 0 ? Bytes[0] : -1;
    }

    public sealed class Entry
    {
        public int Category { get; init; }
        public int Id { get; set; }
        public Cmd? Start { get; set; }
        // category 0A: the group's members
        public List<byte>? Members { get; set; }
    }

    public int Map { get; init; }
    public List<Entry> Entries { get; } = new();
    // the commands in block order
    public List<Cmd> Code { get; } = new();
    // why it can't be written back (a jump out of the block), or null
    public string? Problem { get; private set; }

    // the commands that end a thread or jump away (nothing falls through after them)
    public static readonly HashSet<int> Ends = new() { 0x00, 0x07, 0x2C, 0x2D, 0x2E, 0x56, 0x62, 0x93, 0x9B, 0xAC, 0xAD, 0x0A, 0x63, 0xAA };

    // the map's block, or an empty one if the map has none
    public static L2PuzzleBlock Read(RomBuffer rom, int map)
    {
        var puzzleBlock = new L2PuzzleBlock { Map = map };
        int block = L2Puzzles.Block(rom, map);
        if (block < 0) return puzzleBlock;
        int end = L2Puzzles.End(rom, block);
        var entries = L2Puzzles.Entries(rom, block);
        var byAt = new SortedDictionary<int, Cmd>();
        foreach (var (category, id, offset) in entries.Where(e => e.Category != 5))
            foreach (var op in L2Puzzles.Walk(rom, block + offset, block, end))
                if (!byAt.ContainsKey(op.At)) byAt[op.At] = new Cmd { At = op.At, Bytes = rom.ReadBytes(op.At, op.Length) };
        foreach (var cmd in byAt.Values)
            foreach (int target in L2Puzzles.Targets(rom, cmd.At, cmd.Bytes.Length, block))
            {
                if (byAt.TryGetValue(target, out var to)) cmd.Targets.Add(to);
                else { puzzleBlock.Problem ??= $"a command at ROM {cmd.At:X6} jumps to {target:X6}, outside this map's block"; cmd.Targets.Add(cmd); }
            }
        puzzleBlock.Code.AddRange(byAt.Values);
        foreach (var (category, id, offset) in entries)
        {
            if (category == 5)
            {
                var members = new List<byte>();
                for (int p = block + offset; p < rom.Length && rom.ReadByte(p) != 0xFF && members.Count < 128; p++) members.Add(rom.ReadByte(p));
                puzzleBlock.Entries.Add(new Entry { Category = category, Id = id, Members = members });
            }
            else puzzleBlock.Entries.Add(new Entry { Category = category, Id = id, Start = byAt.GetValueOrDefault(block + offset) });
        }
        return puzzleBlock;
    }

    // where the jump offsets sit in a command (byte index of each u16), in Cmd.Targets order
    public static List<int> JumpSlots(byte[] cmdBytes)
    {
        var slots = new List<int>();
        if (cmdBytes.Length == 0 || !L2Puzzles.Ops.TryGetValue(cmdBytes[0], out var def)) return slots;
        if (def.Args < 0) slots.Add(cmdBytes.Length - 2);
        else foreach (int j in def.Jumps) slots.Add(1 + j);
        return slots;
    }

    // the block's bytes laid out (base 0), offsets count from its start
    public byte[] Layout()
    {
        // header: 16 list offsets, then the lists (id, u16 offset ... FF). an empty category points at a lone FF
        var lists = Enumerable.Range(0, 16).Select(c => Entries.Where(e => e.Category == c).ToList()).ToList();
        int at = 32;
        var listAt = new int[16];
        int emptyAt = -1;
        for (int c = 0; c < 16; c++)
        {
            if (lists[c].Count == 0) { if (emptyAt < 0) { emptyAt = at; at += 1; } listAt[c] = emptyAt; }
            else { listAt[c] = at; at += lists[c].Count * 3 + 1; }
        }
        var groupAt = new Dictionary<Entry, int>();
        foreach (var e in Entries.Where(e => e.Members != null)) { groupAt[e] = at; at += e.Members!.Count + 1; }
        var cmdAt = new Dictionary<Cmd, int>();
        foreach (var cmd in Code) { cmdAt[cmd] = at; at += cmd.Bytes.Length; }
        var data = new byte[at];
        void U16(int offset, int value) { data[offset] = (byte)value; data[offset + 1] = (byte)(value >> 8); }
        for (int c = 0; c < 16; c++) U16(c * 2, listAt[c]);
        if (emptyAt >= 0) data[emptyAt] = 0xFF;
        for (int c = 0; c < 16; c++)
        {
            if (lists[c].Count == 0) continue;
            int p = listAt[c];
            foreach (var e in lists[c])
            {
                int entryOffset = e.Members != null ? groupAt[e] : e.Start != null && cmdAt.TryGetValue(e.Start, out int startAt) ? startAt : throw new InvalidOperationException($"Entry {e.Category:X2}/{e.Id:X2} has no command to start at.");
                data[p] = (byte)e.Id; U16(p + 1, entryOffset); p += 3;
            }
            data[p] = 0xFF;
        }
        foreach (var (e, p) in groupAt)
        {
            for (int i = 0; i < e.Members!.Count; i++) data[p + i] = e.Members[i];
            data[p + e.Members.Count] = 0xFF;
        }
        foreach (var cmd in Code)
        {
            int p = cmdAt[cmd];
            Array.Copy(cmd.Bytes, 0, data, p, cmd.Bytes.Length);
            var slots = JumpSlots(cmd.Bytes);
            for (int i = 0; i < slots.Count && i < cmd.Targets.Count; i++)
            {
                if (!cmdAt.TryGetValue(cmd.Targets[i], out int targetAt)) throw new InvalidOperationException($"A jump at command {Code.IndexOf(cmd) + 1} goes to a command that is no longer in the block.");
                U16(p + slots[i], targetAt);
            }
        }
        return data;
    }

    // checks the commands are whole and nothing runs off the end. null, or what's wrong
    public string? Check()
    {
        if (Problem != null) return Problem;
        for (int i = 0; i < Code.Count; i++)
        {
            var cmd = Code[i];
            if (cmd.Bytes.Length == 0 || !L2Puzzles.Ops.ContainsKey(cmd.Op)) return $"Command {i + 1} is not a puzzle command.";
            if (JumpSlots(cmd.Bytes).Count != cmd.Targets.Count) return $"Command {i + 1} has {cmd.Targets.Count} jump targets where it needs {JumpSlots(cmd.Bytes).Count}.";
            if (i == Code.Count - 1 && !Ends.Contains(cmd.Op)) return "The last command must end the thread or jump away.";
        }
        return null;
    }

    // true if a block at file offset at works. the puzzle commands run with the data bank set to the block's own bank
    // and reach RAM and registers through it ($0583, $0692, $1273, the multiplier's $4216...), so a block only works in
    // banks $80-$BF (below 2 MB) or, on an 8 MB rom, $01-$3F. banks $C0-$FF and $40-$7D have neither at $0000-$7FFF
    // (measured in BizHawk 2026-11-01: a block moved to $D8 read $D8:1273, $D8:0583, $D8:4216).
    // the table's bank is $8F + a byte added in 8 bits ($80:E891), so it gets to $01-$3F by wrapping round
    public static bool SeesRam(int at) => at < 0x200000 || at >= L2Rom.UpperStart + 0x8000 && at < L2Rom.UpperStart + 0x200000;

    // maps whose block sits where its commands can't reach RAM (SeesRam). lufia forge wrote those into the space added
    // after 2.5 MB before 2026-11-01
    public static List<int> OutOfReach(RomBuffer rom) =>
        Enumerable.Range(0, L2Maps.Count(rom)).Where(m => L2Puzzles.Block(rom, m) is var block && block >= 0 && !SeesRam(block)).ToList();

    // lays the OutOfReach blocks out again in the event space. returns the maps moved and why any couldn't be
    public static (List<int> Moved, string? Problem) MoveIntoReach(RomBuffer rom)
    {
        var moved = new List<int>();
        foreach (int m in OutOfReach(rom))
        {
            var puzzleBlock = Read(rom, m);
            if (puzzleBlock.Problem != null || puzzleBlock.Check() != null) return (moved, $"map {m:X2}'s puzzle block can't be laid out again ({puzzleBlock.Problem ?? puzzleBlock.Check()})");
            try { puzzleBlock.Write(rom); moved.Add(m); }
            catch (InvalidOperationException e) { return (moved, e.Message); }
        }
        return (moved, null);
    }

    // the same entries and commands for another map (a new map copied from this one)
    public L2PuzzleBlock For(int map)
    {
        var copy = new L2PuzzleBlock { Map = map, Problem = Problem };
        copy.Entries.AddRange(Entries);
        copy.Code.AddRange(Code);
        return copy;
    }

    // writes the block in the event space (SeesRam) and points the map at it. returns its file offset
    public int Write(RomBuffer rom)
    {
        if (Check() is { } why) throw new InvalidOperationException("The puzzle block can't be written: " + why);
        var data = Layout();
        if (data.Length > L2EventSpace.Mark) throw new InvalidOperationException("The puzzle block would be bigger than a 32 KB bank.");
        int at = L2EventSpace.Allocate(rom, data.Length);
        if (at < L2Puzzles.Table) throw new InvalidOperationException("The added space is below bank $8F, where puzzle blocks can't be.");
        rom.WriteBytes(at, data);
        int hi = (at - L2Puzzles.Table) / 0x8000, lo = (at - L2Puzzles.Table) % 0x8000;
        if (hi > 0xFF) throw new InvalidOperationException("The added space is past what the puzzle table can point at.");
        rom.WriteBytes(L2NewMaps.Entry(rom, L2MapTable.Puzzle, Map), new[] { (byte)lo, (byte)(lo >> 8), (byte)hi });
        return at;
    }
}
