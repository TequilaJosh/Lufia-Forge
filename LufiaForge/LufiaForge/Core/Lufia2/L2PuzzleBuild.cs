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

    private static L2PuzzleBlock.Cmd Jump(int op, int arg, L2PuzzleBlock.Cmd to)
    {
        var c = C(op, arg, 0, 0);
        c.Targets.Add(to);
        return c;
    }

    /// <summary>A puzzle being added to a map: its block, and the map-info records and switches it takes.</summary>
    private sealed class Build
    {
        public RomBuffer Rom = null!;
        public int Map;
        public L2PuzzleBlock Pb = null!;
        private HashSet<int> _switches = new();
        public bool Tools;

        public static Build Start(RomBuffer rom, int map)
        {
            if (map < 2) throw new InvalidOperationException("The world maps have no puzzles.");
            var pb = L2Puzzles.Block(rom, map) >= 0 ? L2PuzzleBlock.Read(rom, map) : Empty(map);
            if (pb.Problem != null) throw new InvalidOperationException("This map's puzzle block can't be changed: " + pb.Problem);
            return new Build { Rom = rom, Map = map, Pb = pb, _switches = SwitchesUsed(pb) };
        }

        private void Add(int list, int size, byte[] rec)
        {
            if (!L2MapInfo.AddRecord(Rom, Map, list, size, rec)) throw new InvalidOperationException("The map's info block would be too big.");
        }

        /// <summary>A tile copy of the w x h piece at (x, y), walls included; its id.</summary>
        public int Copy(int x, int y, int w, int h)
        {
            if (w < 1 || h < 1 || w > 32 || h > 32) throw new InvalidOperationException("A piece must be 1 to 32 tiles wide and high.");
            int id = FreeId(L2MapInfo.Records(Rom, Map, TileList, 10).Select(r => (int)r.Bytes[0]));
            Add(TileList, 10, new[] { (byte)id, (byte)Layers, (byte)x, (byte)y, (byte)w, (byte)h, (byte)0xFF, (byte)0, (byte)0, (byte)0xFF });
            return id;
        }

        /// <summary>A spot at (x, y); its place number (id + 20).</summary>
        public int Spot(int x, int y)
        {
            int id = FreeId(L2MapInfo.Records(Rom, Map, SpotList, 3).Select(r => (int)r.Bytes[0]), 0xBF);
            Add(SpotList, 3, new[] { (byte)id, (byte)x, (byte)y });
            return id + 0x20;
        }

        /// <summary>A trigger spot (bomb, sword, arrow, hammer, action button) at (x, y); its id (the script's entry id).</summary>
        public int TriggerSpot(Trigger t, int x, int y)
        {
            var (cat, list) = Of(t);
            int id = FreeId(L2MapInfo.Records(Rom, Map, list, 3).Select(r => (int)r.Bytes[0])
                              .Concat(Pb.Entries.Where(e => e.Category == cat).Select(e => e.Id)), 0x7F);
            Add(list, 3, new[] { (byte)id, (byte)x, (byte)y });
            if (t != Trigger.ActionButton) Tools = true;
            return id;
        }

        /// <summary>A free switch, from the ones a save keeps (00-1F) first; <paramref name="temporary"/>: one only for the
        /// visit (C0 up, not kept).</summary>
        public int Switch(bool temporary = false)
        {
            var range = temporary ? Enumerable.Range(0xC0, 0x20).Concat(Enumerable.Range(0, 0x20)) : Enumerable.Range(0, 0x20).Concat(Enumerable.Range(0xC0, 0x20));
            int sw = range.FirstOrDefault(i => !_switches.Contains(i), -1);
            if (sw < 0) throw new InvalidOperationException("The map's puzzle switches are all in use.");
            _switches.Add(sw);
            return sw;
        }

        public void Entry(int cat, int id, List<L2PuzzleBlock.Cmd> code)
        {
            Pb.Code.AddRange(code);
            Pb.Entries.Add(new L2PuzzleBlock.Entry { Category = cat, Id = id, Start = code[0] });
        }

        /// <summary>Puts commands before what entry (cat, id) did so far: <paramref name="prefix"/> gets the command to go
        /// on with (a goto to the old start, or an end when there was none) and must end with it.</summary>
        public void Prepend(int cat, int id, Func<L2PuzzleBlock.Cmd, List<L2PuzzleBlock.Cmd>> prefix)
        {
            var e = Pb.Entries.FirstOrDefault(x => x.Category == cat && x.Id == id);
            var go = e?.Start != null ? C(0x0A, 0, 0) : C(0x00);
            if (e?.Start != null) go.Targets.Add(e.Start);
            var code = prefix(go);
            Pb.Code.AddRange(code);
            if (e != null) e.Start = code[0];
            else Pb.Entries.Add(new L2PuzzleBlock.Entry { Category = cat, Id = id, Start = code[0] });
        }

        /// <summary>The set-up redraws tile copy <paramref name="copy"/> on <paramref name="place"/> while the switch is on.</summary>
        public void Redraw(int sw, int place, int copy) =>
            Prepend(0, 2, next => new() { Jump(0x0C, sw, next), C(0x06, place, copy), next });

        public string Write()
        {
            string note = EnsureDungeon(Rom, Map, Tools);
            Pb.Write(Rom);
            return note;
        }
    }

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
        var b = Build.Start(rom, map);
        int copy = b.Copy(fromX, fromY, w, h), place = b.Spot(x, y), trig = b.TriggerSpot(trigger, spotX, spotY), sw = b.Switch();
        // the trigger's script: once (switch on: end), change the tiles, turn the switch on
        var end = C(0x00);
        var run = new List<L2PuzzleBlock.Cmd> { Jump(0x01, sw, end) };
        if (sound >= 0) run.Add(C(0x0B, sound));
        run.Add(C(0x06, place, copy));
        run.Add(C(0x08, sw));
        run.Add(end);
        b.Entry(Of(trigger).Category, trig, run);
        b.Redraw(sw, place, copy);
        string note = b.Write();
        return note + $"Added: {Name(trigger)} at ({spotX}, {spotY}) changes the {w}x{h} tiles at ({x}, {y}) to the ones at ({fromX}, {fromY}), walls included " +
               $"(switch {sw:X2}); the map's set-up redraws it when the party comes back.";
    }

    /// <summary>
    /// When the party (or a block pushed there) stands on the plate at (<paramref name="plateX"/>, <paramref name="plateY"/>),
    /// the piece at (<paramref name="x"/>, <paramref name="y"/>) becomes the one at (<paramref name="fromX"/>,
    /// <paramref name="fromY"/>), once: the room check (category 00 id 00, run as the party moves) tests the plate first
    /// (16: anyone or anything on it, the collision map's 01 / 08 bits); a switch remembers it and the set-up redraws it.
    /// </summary>
    public static string AddPlateChange(RomBuffer rom, int map, int plateX, int plateY, int x, int y, int w, int h, int fromX, int fromY, int sound = 0x10)
    {
        var b = Build.Start(rom, map);
        int copy = b.Copy(fromX, fromY, w, h), place = b.Spot(x, y), plate = b.Spot(plateX, plateY), sw = b.Switch();
        b.Prepend(0, 0, next =>
        {
            var code = new List<L2PuzzleBlock.Cmd> { Jump(0x01, sw, next), Jump(0x16, plate, next) };
            if (sound >= 0) code.Add(C(0x0B, sound));
            code.Add(C(0x06, place, copy));
            code.Add(C(0x08, sw));
            code.Add(next);
            return code;
        });
        b.Redraw(sw, place, copy);
        string note = b.Write();
        return note + $"Added: stepping on the plate at ({plateX}, {plateY}) changes the {w}x{h} tiles at ({x}, {y}) to the ones at ({fromX}, {fromY}), once " +
               $"(switch {sw:X2}); the map's set-up redraws it when the party comes back.";
    }

    /// <summary>
    /// A piece with two looks that a lever (the action button at (<paramref name="spotX"/>, <paramref name="spotY"/>))
    /// switches between: the w x h tiles at (<paramref name="x"/>, <paramref name="y"/>) are look A (a copy of it is kept at
    /// (<paramref name="aX"/>, <paramref name="aY"/>)), each press puts look B (at (<paramref name="bX"/>,
    /// <paramref name="bY"/>)) or A back (spikes lowered or raised, a bridge out or in). The switch is on while it shows
    /// B; the set-up redraws B when the party comes back.
    /// </summary>
    public static string AddLeverToggle(RomBuffer rom, int map, int spotX, int spotY, int x, int y, int w, int h, int aX, int aY, int bX, int bY, int sound = 0x10)
    {
        var b = Build.Start(rom, map);
        int copyA = b.Copy(aX, aY, w, h), copyB = b.Copy(bX, bY, w, h), place = b.Spot(x, y), trig = b.TriggerSpot(Trigger.ActionButton, spotX, spotY), sw = b.Switch();
        // switch off: put B, switch on; switch on: put A back, switch off
        var toB = new List<L2PuzzleBlock.Cmd>();
        if (sound >= 0) toB.Add(C(0x0B, sound));
        toB.Add(C(0x06, place, copyB));
        toB.Add(C(0x08, sw));
        toB.Add(C(0x00));
        var run = new List<L2PuzzleBlock.Cmd> { Jump(0x0C, sw, toB[0]) };
        if (sound >= 0) run.Add(C(0x0B, sound));
        run.Add(C(0x06, place, copyA));
        run.Add(C(0x09, sw));
        run.Add(C(0x00));
        run.AddRange(toB);
        b.Entry(Of(Trigger.ActionButton).Category, trig, run);
        b.Redraw(sw, place, copyB);
        string note = b.Write();
        return note + $"Added: the action button facing ({spotX}, {spotY}) switches the {w}x{h} tiles at ({x}, {y}) between the ones at ({bX}, {bY}) and the ones at ({aX}, {aY}) " +
               $"(switch {sw:X2}: on while the second look shows); the map's set-up redraws it when the party comes back.";
    }

    /// <summary>
    /// A piece that changes back and forth as the party walks (spikes going up and down): every <paramref name="steps"/>
    /// steps the w x h tiles at (<paramref name="x"/>, <paramref name="y"/>) switch between look B (at (<paramref name="bX"/>,
    /// <paramref name="bY"/>)) and look A (kept at (<paramref name="aX"/>, <paramref name="aY"/>)). The room check runs
    /// after every step the party takes ($83:BFE8 sets $7FD0A3); a variable counts the steps. (A thread that loops with
    /// waits holds the party still while it runs, so a clock can't be made of one: checked in the game.)
    /// </summary>
    public static string AddStepTimer(RomBuffer rom, int map, int x, int y, int w, int h, int aX, int aY, int bX, int bY, int steps)
    {
        steps = Math.Clamp(steps, 1, 200);
        var b = Build.Start(rom, map);
        int copyA = b.Copy(aX, aY, w, h), copyB = b.Copy(bX, bY, w, h), place = b.Spot(x, y), sw = b.Switch(temporary: true);
        int v = FreeVariable(b.Pb);
        b.Prepend(0, 0, next =>
        {
            var toggle = C(0x34, v, 0);
            var toB = C(0x06, place, copyB);
            var code = new List<L2PuzzleBlock.Cmd> { C(0x32, v) };
            var check = C(0x35, v, steps, 0, 0); check.Targets.Add(toggle);
            var skip = C(0x0A, 0, 0); skip.Targets.Add(next);
            code.AddRange(new[] { check, skip, toggle, Jump(0x0C, sw, toB), C(0x06, place, copyA), C(0x09, sw) });
            var after = C(0x0A, 0, 0); after.Targets.Add(next);
            code.Add(after);
            code.AddRange(new[] { toB, C(0x08, sw), next });
            return code;
        });
        // as the map loads: look A, the count from 0
        b.Prepend(0, 2, next => new() { C(0x09, sw), C(0x34, v, 0), next });
        string note = b.Write();
        return note + $"Added: the {w}x{h} tiles at ({x}, {y}) switch between the ones at ({bX}, {bY}) and the ones at ({aX}, {aY}) every {steps} step{(steps == 1 ? "" : "s")} the party takes " +
               $"(variable {v:X2} counts them).";
    }

    /// <summary>A puzzle variable (A0-BF, bytes at $7F:D114-D133) the block's commands don't use, from BE down (BF is
    /// where 9F / A7 write).</summary>
    private static int FreeVariable(L2PuzzleBlock pb)
    {
        var used = new HashSet<int>();
        foreach (var c in pb.Code)
            foreach (var x in c.Bytes.Skip(1)) if (x >= 0xA0 && x < 0xC0) used.Add(x);
        for (int v = 0xBE; v >= 0xA0; v--) if (!used.Contains(v)) return v;
        throw new InvalidOperationException("The map's puzzle variables are all in use.");
    }

    // ---------------------------------------------------------------- doors that events open (a key, a story flag)

    /// <summary>An event area (map info +14: id, x1, y1, x2, y2, the end not included) for event command 43; its id.</summary>
    private static int AddArea(RomBuffer rom, int map, int x, int y, int w, int h)
    {
        int id = FreeId(L2MapInfo.Records(rom, map, L2MapInfo.Areas, 5).Select(r => (int)r.Bytes[0]));
        if (!L2MapInfo.AddRecord(rom, map, L2MapInfo.Areas, 5, new[] { (byte)id, (byte)x, (byte)y, (byte)(x + w), (byte)(y + h) }))
            throw new InvalidOperationException("The map's info block would be too big.");
        return id;
    }

    private static L2Step Ev(params int[] b) => new() { Bytes = b.Select(x => (byte)x).ToArray() };

    private static L2Step Say(string text) => new() { Bytes = new byte[] { 0x9E, 0x06 }, IsText = true, Text = text, End = 0x01, OriginalEnd = 0xFF, OriginalText = "\u0001" };

    /// <summary>The map's "on entering" event (+2 id 01, where the game draws such changes: 35 of its 43s) redraws the
    /// open look (event command 43, area onto area) while story flag <paramref name="flag"/> is on. (The start-up script
    /// can't: the game never copies tiles there, and a 43 there showed nothing.)</summary>
    private static void RedrawOnFlag(RomBuffer rom, int map, int flag, int areaDoor, int areaOpen)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block.");
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 1);
        var s = entry != null ? L2EventScript.Read(rom, block + entry.Offset, block) : L2EventScript.New(block);
        if (s.Problem != null) throw new InvalidOperationException("The map's \"on entering\" event can't be changed: " + s.Problem);
        var skip = Ev(0x6A, flag, 0, 0);
        skip.Jumps.Add(new L2Jump { Step = s.Steps[0] });
        s.Steps.InsertRange(0, new[] { skip, Ev(0x43, areaDoor, areaOpen), Ev(0x6B) });
        if (entry != null) L2EventBlocks.Save(rom, map, entry, s); else L2EventBlocks.Add(rom, map, L2EventBlocks.GameList, 1, s);
    }

    /// <summary>
    /// A door that opens with a key: walking onto the block in front of it (<paramref name="standX"/>,
    /// <paramref name="standY"/>, a step-on area) plays an event that checks the party has <paramref name="item"/>: then
    /// "The door opened with the KEY." (the key is taken when <paramref name="takeKey"/>), the w x h tiles at
    /// (<paramref name="x"/>, <paramref name="y"/>) become the open look at (<paramref name="openX"/>, <paramref name="openY"/>)
    /// (event command 43: both layers and where the party can walk) and story flag <paramref name="flag"/> turns on;
    /// without it "It's locked." The "on entering" event redraws it open while the flag is on. (A scene started from an
    /// action spot with puzzle command BB never played in the game, so the door is opened from a step-on event.)
    /// </summary>
    public static string AddKeyDoor(RomBuffer rom, int map, int standX, int standY, int x, int y, int w, int h, int openX, int openY,
        int item, string itemName, bool takeKey, int flag)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block, so it can't have the door's event.");
        int areaDoor = AddArea(rom, map, x, y, w, h), areaOpen = AddArea(rom, map, openX, openY, w, h);
        // the event: open already (flag on): nothing; no key: locked; the key: open
        var s = L2EventScript.New(block);
        var end = s.Steps[0];
        var lockedAt = Ev(0xB8);
        var played = Ev(0x15, flag, 0, 0); played.Jumps.Add(new L2Jump { Step = end });
        var has = Ev(0x14, 0xC2, item & 0xFF, item >> 8, 1, 0, 0x30, 0, 0, 0xFF); has.Jumps.Add(new L2Jump { Step = lockedAt });
        var steps = new List<L2Step> { played, has };
        if (takeKey) steps.Add(Ev(0x24 + (item >> 8), item & 0xFF, 1));
        steps.AddRange(new[] { Ev(0xB8), Say($"Used the {itemName}.\nThe door opened."), Ev(0x43, areaDoor, areaOpen), Ev(0x6B), Ev(0x1A, flag), Ev(0x00),
                               lockedAt, Say("It's locked.") });
        s.Steps.InsertRange(0, steps);
        int trig = L2EventBlocks.AddTrigger(rom, map, standX, standY, standX, standY, s);
        RedrawOnFlag(rom, map, flag, areaDoor, areaOpen);
        return $"Added: walking up to the door (step-on area {trig} at ({standX}, {standY})) with the {itemName} opens it: the {w}x{h} tiles at ({x}, {y}) become the ones at ({openX}, {openY}) " +
               $"{(takeKey ? "(the key is used up) " : "")}and story flag {flag:X2} turns on; without it, \"It's locked.\" The map's \"on entering\" event draws it open while the flag is on.";
    }

    /// <summary>
    /// A door that is open once story flag <paramref name="flag"/> is on (set by any event: a talk, a cutscene): the
    /// "on entering" event draws the open look (at (<paramref name="openX"/>, <paramref name="openY"/>)) over the w x h tiles
    /// at (<paramref name="x"/>, <paramref name="y"/>) as the party enters the map. An event on this map that sets the flag can open it
    /// at once with "Copy map tiles from one area to another" (43) between the two areas this adds.
    /// </summary>
    public static string AddFlagDoor(RomBuffer rom, int map, int x, int y, int w, int h, int openX, int openY, int flag)
    {
        int areaDoor = AddArea(rom, map, x, y, w, h), areaOpen = AddArea(rom, map, openX, openY, w, h);
        RedrawOnFlag(rom, map, flag, areaDoor, areaOpen);
        return $"Added: the {w}x{h} tiles at ({x}, {y}) are drawn open (the ones at ({openX}, {openY})) when the party enters the map with story flag {flag:X2} on " +
               $"(areas {areaDoor} and {areaOpen}: an event here can open it at once with \"Copy map tiles from one area to another\", area {areaDoor} from area {areaOpen}).";
    }
}
