namespace LufiaForge.Core.Lufia2;

/// <summary>
/// A map's whole puzzle block (<see cref="L2Puzzles"/>) as a model that can be changed and written back: the entries of
/// the 16 categories (category 0A: object groups, byte lists), and every command the entries reach, in their order in
/// the ROM (a command that doesn't end or jump away goes on into the next one, so the order is kept), each jump pointing
/// at the command it goes to. Written back, the block is laid out again (header of 16 list offsets, the lists, the
/// groups, the commands; every offset counts from the block start) in added space, and the map's entry of the table at
/// $8F:8000 (u16 lo, u8 hi: bank $8F + hi, address $8000 + lo) points at it. Bytes no entry reaches are left out.
/// </summary>
public sealed class L2PuzzleBlock
{
    public sealed class Cmd
    {
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        /// <summary>The commands its jump targets point at, in the order of <see cref="L2Puzzles.Ops"/>' Jumps (or the
        /// one target at the end of an FF list).</summary>
        public List<Cmd> Targets { get; } = new();
        /// <summary>File offset it was read from (-1: new).</summary>
        public int At { get; init; } = -1;
        public int Op => Bytes.Length > 0 ? Bytes[0] : -1;
    }

    public sealed class Entry
    {
        public int Category { get; init; }
        public int Id { get; set; }
        public Cmd? Start { get; set; }
        /// <summary>Category 0A: the group's members.</summary>
        public List<byte>? Members { get; set; }
    }

    public int Map { get; init; }
    public List<Entry> Entries { get; } = new();
    /// <summary>The commands in block order.</summary>
    public List<Cmd> Code { get; } = new();
    /// <summary>Why it can't be written back (a jump out of the block), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>The commands that end a thread or jump away (nothing falls through after them).</summary>
    public static readonly HashSet<int> Ends = new() { 0x00, 0x07, 0x2C, 0x2D, 0x2E, 0x56, 0x62, 0x93, 0x9B, 0xAC, 0xAD, 0x0A, 0x63, 0xAA };

    /// <summary>The block of <paramref name="map"/>, or an empty one (the map has none).</summary>
    public static L2PuzzleBlock Read(RomBuffer rom, int map)
    {
        var pb = new L2PuzzleBlock { Map = map };
        int block = L2Puzzles.Block(rom, map);
        if (block < 0) return pb;
        int end = L2Puzzles.End(rom, block);
        var entries = L2Puzzles.Entries(rom, block);
        var byAt = new SortedDictionary<int, Cmd>();
        foreach (var (cat, id, off) in entries.Where(e => e.Category != 5))
            foreach (var op in L2Puzzles.Walk(rom, block + off, block, end))
                if (!byAt.ContainsKey(op.At)) byAt[op.At] = new Cmd { At = op.At, Bytes = rom.ReadBytes(op.At, op.Length) };
        foreach (var c in byAt.Values)
            foreach (int t in L2Puzzles.Targets(rom, c.At, c.Bytes.Length, block))
            {
                if (byAt.TryGetValue(t, out var to)) c.Targets.Add(to);
                else { pb.Problem ??= $"a command at ROM {c.At:X6} jumps to {t:X6}, outside this map's block"; c.Targets.Add(c); }
            }
        pb.Code.AddRange(byAt.Values);
        foreach (var (cat, id, off) in entries)
        {
            if (cat == 5)
            {
                var m = new List<byte>();
                for (int p = block + off; p < rom.Length && rom.ReadByte(p) != 0xFF && m.Count < 128; p++) m.Add(rom.ReadByte(p));
                pb.Entries.Add(new Entry { Category = cat, Id = id, Members = m });
            }
            else pb.Entries.Add(new Entry { Category = cat, Id = id, Start = byAt.GetValueOrDefault(block + off) });
        }
        return pb;
    }

    /// <summary>Where the jump offsets sit in a command (byte index of each u16), in <see cref="Cmd.Targets"/> order.</summary>
    public static List<int> JumpSlots(byte[] b)
    {
        var l = new List<int>();
        if (b.Length == 0 || !L2Puzzles.Ops.TryGetValue(b[0], out var d)) return l;
        if (d.Args < 0) l.Add(b.Length - 2);
        else foreach (int j in d.Jumps) l.Add(1 + j);
        return l;
    }

    /// <summary>The block's bytes laid out for <paramref name="size"/> computing (base 0); offsets count from its start.</summary>
    public byte[] Layout()
    {
        // header: 16 list offsets; then the lists (id, u16 offset ... FF); an empty category points at a lone FF
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
        foreach (var c in Code) { cmdAt[c] = at; at += c.Bytes.Length; }
        var data = new byte[at];
        void U16(int p, int v) { data[p] = (byte)v; data[p + 1] = (byte)(v >> 8); }
        for (int c = 0; c < 16; c++) U16(c * 2, listAt[c]);
        if (emptyAt >= 0) data[emptyAt] = 0xFF;
        for (int c = 0; c < 16; c++)
        {
            if (lists[c].Count == 0) continue;
            int p = listAt[c];
            foreach (var e in lists[c])
            {
                int off = e.Members != null ? groupAt[e] : e.Start != null && cmdAt.TryGetValue(e.Start, out int o) ? o : throw new InvalidOperationException($"Entry {e.Category:X2}/{e.Id:X2} has no command to start at.");
                data[p] = (byte)e.Id; U16(p + 1, off); p += 3;
            }
            data[p] = 0xFF;
        }
        foreach (var (e, p) in groupAt)
        {
            for (int i = 0; i < e.Members!.Count; i++) data[p + i] = e.Members[i];
            data[p + e.Members.Count] = 0xFF;
        }
        foreach (var c in Code)
        {
            int p = cmdAt[c];
            Array.Copy(c.Bytes, 0, data, p, c.Bytes.Length);
            var slots = JumpSlots(c.Bytes);
            for (int i = 0; i < slots.Count && i < c.Targets.Count; i++)
            {
                if (!cmdAt.TryGetValue(c.Targets[i], out int t)) throw new InvalidOperationException($"A jump at command {Code.IndexOf(c) + 1} goes to a command that is no longer in the block.");
                U16(p + slots[i], t);
            }
        }
        return data;
    }

    /// <summary>Checks the commands are whole and nothing runs off the end; null or what is wrong.</summary>
    public string? Check()
    {
        if (Problem != null) return Problem;
        for (int i = 0; i < Code.Count; i++)
        {
            var c = Code[i];
            if (c.Bytes.Length == 0 || !L2Puzzles.Ops.ContainsKey(c.Op)) return $"Command {i + 1} is not a puzzle command.";
            if (JumpSlots(c.Bytes).Count != c.Targets.Count) return $"Command {i + 1} has {c.Targets.Count} jump targets where it needs {JumpSlots(c.Bytes).Count}.";
            if (i == Code.Count - 1 && !Ends.Contains(c.Op)) return "The last command must end the thread or jump away.";
        }
        return null;
    }

    /// <summary>Writes the block in added space and points the map at it. Returns its file offset.</summary>
    public int Write(RomBuffer rom)
    {
        if (Check() is { } why) throw new InvalidOperationException("The puzzle block can't be written: " + why);
        var data = Layout();
        if (data.Length > 0x8000) throw new InvalidOperationException("The puzzle block would be bigger than a 32 KB bank.");
        int at = L2Rom.Allocate(rom, data.Length);
        if (at < L2Puzzles.Table) throw new InvalidOperationException("The added space is below bank $8F, where puzzle blocks can't be.");
        rom.WriteBytes(at, data);
        int hi = (at - L2Puzzles.Table) / 0x8000, lo = (at - L2Puzzles.Table) % 0x8000;
        if (hi > 0xFF) throw new InvalidOperationException("The added space is past what the puzzle table can point at.");
        rom.WriteBytes(L2Puzzles.Table + Map * 3, new[] { (byte)lo, (byte)(lo >> 8), (byte)hi });
        return at;
    }
}
