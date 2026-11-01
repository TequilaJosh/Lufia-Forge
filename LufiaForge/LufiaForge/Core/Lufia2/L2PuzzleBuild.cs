namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Ready-made puzzles written into a map's puzzle block (<see cref="L2PuzzleBlock"/>) and its map-info lists: a tile
/// change (a wall blasted open, a door drawn open, a bridge put down) when something happens at a spot. The changed
/// look is a piece of the map kept elsewhere (off to the side, as the game's own maps do), copied over the place with
/// its walls (layers 33: both layers' blocks and their 04 / 08 wall flags, $83:8B6A). A puzzle switch remembers it, and
/// the room set-up (category 00 id 02, run as the map loads) puts it back when the party comes again.
/// </summary>
public static class L2PuzzleBuild
{
    public enum Trigger { Bomb, Sword, Arrow, Hammer, ActionButton }

    /// <summary>The block category and map-info list of each trigger's spots.</summary>
    public static (int Category, int List) Of(Trigger t) => t switch
    {
        Trigger.ActionButton => (1, 0x20),
        Trigger.Sword => (2, 0x1C),
        Trigger.Arrow => (3, 0x1E),
        Trigger.Bomb => (4, 0x1A),
        _ => (7, 0x2A),
    };

    public static string Name(Trigger t) => t switch
    {
        Trigger.ActionButton => "pressing the action button facing it",
        Trigger.Sword => "a sword hit",
        Trigger.Arrow => "an arrow",
        Trigger.Bomb => "a bomb",
        _ => "a hammer",
    };

    public const int TileList = 0x16, SpotList = 0x22, Layers = 0x33;

    private static int FreeId(IEnumerable<int> used, int max = 0xFE)
    {
        var u = used.ToHashSet();
        for (int i = 0; i <= max; i++) if (!u.Contains(i)) return i;
        throw new InvalidOperationException("The map's list has no free number.");
    }

    /// <summary>The puzzle switches a block uses (tests, sets, clears; and the place numbers "keep the answer" writes).</summary>
    public static HashSet<int> SwitchesUsed(L2PuzzleBlock pb)
    {
        var s = new HashSet<int>();
        foreach (var c in pb.Code)
        {
            var b = c.Bytes;
            if (c.Op is 0x01 or 0x0C or 0x08 or 0x09 or 0x14 && b.Length > 1) s.Add(b[1]);
            if (c.Op is 0x13 or 0x15 or 0x17 or 0x72 or 0x05 && b.Length > 1) s.Add(b[1]);   // 29 stores into the tested place's number
        }
        return s;
    }

    /// <summary>A block for a map that has none: the two entries every block has (00 room check, 02 set-up), each ending at once.</summary>
    public static L2PuzzleBlock Empty(int map)
    {
        var pb = new L2PuzzleBlock { Map = map };
        var end0 = new L2PuzzleBlock.Cmd { Bytes = new byte[] { 0x00 } };
        var end2 = new L2PuzzleBlock.Cmd { Bytes = new byte[] { 0x00 } };
        pb.Code.Add(end0); pb.Code.Add(end2);
        pb.Entries.Add(new L2PuzzleBlock.Entry { Category = 0, Id = 0, Start = end0 });
        pb.Entries.Add(new L2PuzzleBlock.Entry { Category = 0, Id = 2, Start = end2 });
        return pb;
    }

    /// <summary>
    /// The game runs a map's puzzles only with dungeon controls on (Map settings, 69, bit 01, in its start-up script:
    /// all 146 puzzle maps have it; in Clamento, a town with 69 08, an action spot never fired), and the field tools
    /// (bomb, sword, arrow, hammer) work only without "no field tools" (bit 40). Turns those bits the right way in the
    /// start-up script's Map settings steps (in place). Returns a note of what changed, "" when nothing did.
    /// </summary>
    public static string EnsureDungeon(RomBuffer rom, int map, bool tools)
    {
        int init = L2EventBlocks.InitScript(rom, map);
        var s = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
        var steps = s.Steps.Where(x => x.Op == 0x69 && x.Bytes.Length >= 2 && x.At >= 0).ToList();
        if (steps.Count == 0)
            throw new InvalidOperationException("The map's start-up script has no Map settings step (69), so the game won't run puzzles here: add one with dungeon controls first.");
        bool changed = false;
        foreach (var st in steps)
        {
            int v = st.Bytes[1], n = v | 0x01;
            if (tools) n &= ~0x40;
            if (n != v) { rom.WriteByte(st.At + 1, (byte)n); changed = true; }
        }
        return changed ? "Map settings: dungeon controls turned on" + (tools ? " and field tools allowed" : "") + " (the game runs puzzles only then). " : "";
    }

    private static L2PuzzleBlock.Cmd C(params int[] b) => new() { Bytes = b.Select(x => (byte)x).ToArray() };

    /// <summary>
    /// When <paramref name="trigger"/> happens at (<paramref name="spotX"/>, <paramref name="spotY"/>), the
    /// <paramref name="w"/> x <paramref name="h"/> tiles at (<paramref name="x"/>, <paramref name="y"/>) become the ones at
    /// (<paramref name="fromX"/>, <paramref name="fromY"/>), walls included, once (a switch remembers it, and the room
    /// set-up redraws it when the map loads again). <paramref name="sound"/>: a sound effect as it happens, or -1.
    /// Returns what was added.
    /// </summary>
    public static string AddTileChange(RomBuffer rom, int map, Trigger trigger, int spotX, int spotY, int x, int y, int w, int h,
        int fromX, int fromY, int sound = -1)
    {
        if (map < 2) throw new InvalidOperationException("The world maps have no puzzles.");
        if (w < 1 || h < 1 || w > 32 || h > 32) throw new InvalidOperationException("The changed piece must be 1 to 32 tiles wide and high.");
        var pb = L2Puzzles.Block(rom, map) >= 0 ? L2PuzzleBlock.Read(rom, map) : Empty(map);
        if (pb.Problem != null) throw new InvalidOperationException("This map's puzzle block can't be changed: " + pb.Problem);
        var (cat, list) = Of(trigger);

        // the numbers: a tile copy, a spot for where it goes, the trigger spot, a switch
        int copy = FreeId(L2MapInfo.Records(rom, map, TileList, 10).Select(r => (int)r.Bytes[0]));
        int spot = FreeId(L2MapInfo.Records(rom, map, SpotList, 3).Select(r => (int)r.Bytes[0]), 0xBF);
        int trig = FreeId(L2MapInfo.Records(rom, map, list, 3).Select(r => (int)r.Bytes[0])
                          .Concat(pb.Entries.Where(e => e.Category == cat).Select(e => e.Id)), 0x7F);
        var used = SwitchesUsed(pb);
        int sw = Enumerable.Range(0, 0x20).Concat(Enumerable.Range(0xC0, 0x20)).FirstOrDefault(i => !used.Contains(i), -1);
        if (sw < 0) throw new InvalidOperationException("The map's puzzle switches are all in use.");
        int place = spot + 0x20;

        // the map-info records
        if (!L2MapInfo.AddRecord(rom, map, TileList, 10, new[] { (byte)copy, (byte)Layers, (byte)fromX, (byte)fromY, (byte)w, (byte)h, (byte)0xFF, (byte)0, (byte)0, (byte)0xFF })
            || !L2MapInfo.AddRecord(rom, map, SpotList, 3, new[] { (byte)spot, (byte)x, (byte)y })
            || !L2MapInfo.AddRecord(rom, map, list, 3, new[] { (byte)trig, (byte)spotX, (byte)spotY }))
            throw new InvalidOperationException("The map's info block would be too big.");

        // the trigger's script: once (switch on: end), change the tiles, turn the switch on
        var end = C(0x00);
        var run = new List<L2PuzzleBlock.Cmd> { C(0x01, sw, 0, 0) };
        run[0].Targets.Add(end);
        if (sound >= 0) run.Add(C(0x0B, sound));
        run.Add(C(0x06, place, copy));
        run.Add(C(0x08, sw));
        run.Add(end);
        pb.Code.AddRange(run);
        pb.Entries.Add(new L2PuzzleBlock.Entry { Category = cat, Id = trig, Start = run[0] });

        // the set-up: switch on, redraw the change; then what the set-up did before
        var setup = pb.Entries.FirstOrDefault(e => e.Category == 0 && e.Id == 2);
        var then = setup?.Start;
        var go = then != null ? C(0x0A, 0, 0) : C(0x00);
        if (then != null) go.Targets.Add(then);
        var skip = C(0x0C, sw, 0, 0);
        skip.Targets.Add(go);
        pb.Code.AddRange(new[] { skip, C(0x06, place, copy), go });
        if (setup != null) setup.Start = skip;
        else pb.Entries.Add(new L2PuzzleBlock.Entry { Category = 0, Id = 2, Start = skip });

        string dungeon = EnsureDungeon(rom, map, trigger != Trigger.ActionButton);
        pb.Write(rom);
        return dungeon + $"Added: {Name(trigger)} at ({spotX}, {spotY}) changes the {w}x{h} tiles at ({x}, {y}) to the ones at ({fromX}, {fromY}), walls included " +
               $"(tile copy {copy:X2}, spot {spot}, {L2Puzzles.Categories[cat].ToLowerInvariant()} {trig}, switch {sw:X2}); the map's set-up redraws it when the party comes back.";
    }
}
