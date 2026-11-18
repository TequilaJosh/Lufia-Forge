namespace LufiaForge.Core.Lufia2;

// ready-made puzzles written into a map's puzzle block (L2PuzzleBlock) and its map info lists: a tile change (a wall
// blasted open, a door drawn open, a bridge put down) when something happens at a spot. the changed look is a piece of
// the map kept somewhere else (off to the side, like the game's own maps do it), copied over the place with its walls
// (layers 33 = both layers' blocks and their 04 / 08 wall flags, $83:8B6A). a puzzle switch remembers it, and the room
// set-up (category 00 id 02, run as the map loads) puts it back when the party comes again
public static class L2PuzzleBuild
{
    public enum Trigger { Bomb, Sword, Arrow, Hammer, ActionButton }

    // the block category and map info list of each trigger's spots
    public static (int Category, int List) Of(Trigger trigger) => trigger switch
    {
        Trigger.ActionButton => (1, 0x20),
        Trigger.Sword => (2, 0x1C),
        Trigger.Arrow => (3, 0x1E),
        Trigger.Bomb => (4, 0x1A),
        _ => (7, 0x2A),
    };

    public static string Name(Trigger trigger) => trigger switch
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
        var usedSet = used.ToHashSet();
        for (int i = 0; i <= max; i++) if (!usedSet.Contains(i)) return i;
        throw new InvalidOperationException("The map's list has no free number.");
    }

    // the puzzle switches a block uses (tests, sets, clears, plus the place numbers "keep the answer" writes)
    public static HashSet<int> SwitchesUsed(L2PuzzleBlock puzzleBlock)
    {
        var switches = new HashSet<int>();
        foreach (var cmd in puzzleBlock.Code)
        {
            var bytes = cmd.Bytes;
            if (cmd.Op is 0x01 or 0x0C or 0x08 or 0x09 or 0x14 && bytes.Length > 1) switches.Add(bytes[1]);
            if (cmd.Op is 0x13 or 0x15 or 0x17 or 0x72 or 0x05 && bytes.Length > 1) switches.Add(bytes[1]);   // 29 stores into the tested place's number
        }
        return switches;
    }

    // a block for a map that has none: the two entries every block has (00 room check, 02 set-up), each ending right away
    public static L2PuzzleBlock Empty(int map)
    {
        var puzzleBlock = new L2PuzzleBlock { Map = map };
        var end0 = new L2PuzzleBlock.Cmd { Bytes = new byte[] { 0x00 } };
        var end2 = new L2PuzzleBlock.Cmd { Bytes = new byte[] { 0x00 } };
        puzzleBlock.Code.Add(end0); puzzleBlock.Code.Add(end2);
        puzzleBlock.Entries.Add(new L2PuzzleBlock.Entry { Category = 0, Id = 0, Start = end0 });
        puzzleBlock.Entries.Add(new L2PuzzleBlock.Entry { Category = 0, Id = 2, Start = end2 });
        return puzzleBlock;
    }

    // the game only runs a map's puzzles with dungeon controls on (Map settings, 69, bit 01, in its start-up script).
    // all 146 puzzle maps have it, and in Clamento, a town with 69 08, an action spot never fired. the field tools
    // (bomb, sword, arrow, hammer) only work without "no field tools" (bit 40). this sets those bits the right way in
    // the start-up script's Map settings steps (in place). returns a note of what changed, "" if nothing did
    public static string EnsureDungeon(RomBuffer rom, int map, bool tools)
    {
        int initScript = L2EventBlocks.InitScript(rom, map);
        var script = L2EventScript.Read(rom, initScript, L2EventBlocks.InitBase(initScript));
        var steps = script.Steps.Where(x => x.Op == 0x69 && x.Bytes.Length >= 2 && x.At >= 0).ToList();
        if (steps.Count == 0)
            throw new InvalidOperationException("The map's start-up script has no Map settings step (69), so the game won't run puzzles here: add one with dungeon controls first.");
        bool changed = false;
        foreach (var step in steps)
        {
            int oldValue = step.Bytes[1], newValue = oldValue | 0x01;
            if (tools) newValue &= ~0x40;
            if (newValue != oldValue) { rom.WriteByte(step.At + 1, (byte)newValue); changed = true; }
        }
        return changed ? "Map settings: dungeon controls turned on" + (tools ? " and field tools allowed" : "") + " (the game runs puzzles only then). " : "";
    }

    private static L2PuzzleBlock.Cmd C(params int[] bytes) => new() { Bytes = bytes.Select(x => (byte)x).ToArray() };

    private static L2PuzzleBlock.Cmd Jump(int op, int arg, L2PuzzleBlock.Cmd to)
    {
        var cmd = C(op, arg, 0, 0);
        cmd.Targets.Add(to);
        return cmd;
    }

    // a puzzle being added to a map: its block, and the map info records and switches it takes
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
            var puzzleBlock = L2Puzzles.Block(rom, map) >= 0 ? L2PuzzleBlock.Read(rom, map) : Empty(map);
            if (puzzleBlock.Problem != null) throw new InvalidOperationException("This map's puzzle block can't be changed: " + puzzleBlock.Problem);
            return new Build { Rom = rom, Map = map, Pb = puzzleBlock, _switches = SwitchesUsed(puzzleBlock) };
        }

        private void Add(int list, int size, byte[] record)
        {
            if (!L2MapInfo.AddRecord(Rom, Map, list, size, record)) throw new InvalidOperationException("The map's info block would be too big.");
        }

        // a tile copy of the w x h piece at (x, y), walls included. returns its id
        public int Copy(int x, int y, int w, int h)
        {
            if (w < 1 || h < 1 || w > 32 || h > 32) throw new InvalidOperationException("A piece must be 1 to 32 tiles wide and high.");
            int id = FreeId(L2MapInfo.Records(Rom, Map, TileList, 10).Select(r => (int)r.Bytes[0]));
            Add(TileList, 10, new[] { (byte)id, (byte)Layers, (byte)x, (byte)y, (byte)w, (byte)h, (byte)0xFF, (byte)0, (byte)0, (byte)0xFF });
            return id;
        }

        // a spot at (x, y). returns its place number (id + 20)
        public int Spot(int x, int y)
        {
            int id = FreeId(L2MapInfo.Records(Rom, Map, SpotList, 3).Select(r => (int)r.Bytes[0]), 0xBF);
            Add(SpotList, 3, new[] { (byte)id, (byte)x, (byte)y });
            return id + 0x20;
        }

        // a trigger spot (bomb, sword, arrow, hammer, action button) at (x, y). returns its id (the script's entry id)
        public int TriggerSpot(Trigger trigger, int x, int y)
        {
            var (category, list) = Of(trigger);
            int id = FreeId(L2MapInfo.Records(Rom, Map, list, 3).Select(r => (int)r.Bytes[0])
                              .Concat(Pb.Entries.Where(e => e.Category == category).Select(e => e.Id)), 0x7F);
            Add(list, 3, new[] { (byte)id, (byte)x, (byte)y });
            if (trigger != Trigger.ActionButton) Tools = true;
            return id;
        }

        // a free switch, from the ones a save keeps (00-1F) first. with temporary, one that only lasts the visit
        // (C0 up, not kept)
        public int Switch(bool temporary = false)
        {
            var range = temporary ? Enumerable.Range(0xC0, 0x20).Concat(Enumerable.Range(0, 0x20)) : Enumerable.Range(0, 0x20).Concat(Enumerable.Range(0xC0, 0x20));
            int sw = range.FirstOrDefault(i => !_switches.Contains(i), -1);
            if (sw < 0) throw new InvalidOperationException("The map's puzzle switches are all in use.");
            _switches.Add(sw);
            return sw;
        }

        public void Entry(int category, int id, List<L2PuzzleBlock.Cmd> code)
        {
            Pb.Code.AddRange(code);
            Pb.Entries.Add(new L2PuzzleBlock.Entry { Category = category, Id = id, Start = code[0] });
        }

        // puts commands in front of whatever entry (category, id) did so far. prefix gets the command to carry on
        // with (a goto to the old start, or an end if there wasn't one) and has to end with it
        public void Prepend(int category, int id, Func<L2PuzzleBlock.Cmd, List<L2PuzzleBlock.Cmd>> prefix)
        {
            var entry = Pb.Entries.FirstOrDefault(x => x.Category == category && x.Id == id);
            var go = entry?.Start != null ? C(0x0A, 0, 0) : C(0x00);
            if (entry?.Start != null) go.Targets.Add(entry.Start);
            var code = prefix(go);
            Pb.Code.AddRange(code);
            if (entry != null) entry.Start = code[0];
            else Pb.Entries.Add(new L2PuzzleBlock.Entry { Category = category, Id = id, Start = code[0] });
        }

        // the set-up redraws tile copy copy on place while the switch is on
        public void Redraw(int sw, int place, int copy) =>
            Prepend(0, 2, next => new() { Jump(0x0C, sw, next), C(0x06, place, copy), next });

        public string Write()
        {
            string note = EnsureDungeon(Rom, Map, Tools);
            Pb.Write(Rom);
            return note;
        }
    }

    // when trigger happens at (spotX, spotY), the w x h tiles at (x, y) turn into the ones at (fromX, fromY), walls
    // included, once (a switch remembers it, and the room set-up redraws it when the map loads again).
    // sound = a sound effect as it happens, or -1. returns what was added
    public static string AddTileChange(RomBuffer rom, int map, Trigger trigger, int spotX, int spotY, int x, int y, int w, int h,
        int fromX, int fromY, int sound = -1)
    {
        var build = Build.Start(rom, map);
        int copy = build.Copy(fromX, fromY, w, h), place = build.Spot(x, y), triggerId = build.TriggerSpot(trigger, spotX, spotY), sw = build.Switch();
        // the trigger's script: only once (switch on = end), change the tiles, turn the switch on
        var end = C(0x00);
        var run = new List<L2PuzzleBlock.Cmd> { Jump(0x01, sw, end) };
        if (sound >= 0) run.Add(C(0x0B, sound));
        run.Add(C(0x06, place, copy));
        run.Add(C(0x08, sw));
        run.Add(end);
        build.Entry(Of(trigger).Category, triggerId, run);
        build.Redraw(sw, place, copy);
        string note = build.Write();
        return note + $"Added: {Name(trigger)} at ({spotX}, {spotY}) changes the {w}x{h} tiles at ({x}, {y}) to the ones at ({fromX}, {fromY}), walls included " +
               $"(switch {sw:X2}); the map's set-up redraws it when the party comes back.";
    }

    // when the party (or a block pushed there) stands on the plate at (plateX, plateY), the piece at (x, y) turns into
    // the one at (fromX, fromY), once. the room check (category 00 id 00, run as the party moves) tests the plate first
    // (16 = anyone or anything on it, the collision map's 01 / 08 bits). a switch remembers it and the set-up redraws it
    public static string AddPlateChange(RomBuffer rom, int map, int plateX, int plateY, int x, int y, int w, int h, int fromX, int fromY, int sound = 0x10)
    {
        var build = Build.Start(rom, map);
        int copy = build.Copy(fromX, fromY, w, h), place = build.Spot(x, y), plate = build.Spot(plateX, plateY), sw = build.Switch();
        build.Prepend(0, 0, next =>
        {
            var code = new List<L2PuzzleBlock.Cmd> { Jump(0x01, sw, next), Jump(0x16, plate, next) };
            if (sound >= 0) code.Add(C(0x0B, sound));
            code.Add(C(0x06, place, copy));
            code.Add(C(0x08, sw));
            code.Add(next);
            return code;
        });
        build.Redraw(sw, place, copy);
        string note = build.Write();
        return note + $"Added: stepping on the plate at ({plateX}, {plateY}) changes the {w}x{h} tiles at ({x}, {y}) to the ones at ({fromX}, {fromY}), once " +
               $"(switch {sw:X2}); the map's set-up redraws it when the party comes back.";
    }

    // a piece with two looks that a lever (the action button at (spotX, spotY)) switches between. the w x h tiles at
    // (x, y) are look A (with a copy of it kept at (aX, aY)), and each press puts look B (at (bX, bY)) or A back
    // (spikes lowered or raised, a bridge out or in). the switch is on while it shows B, and the set-up redraws B
    // when the party comes back
    public static string AddLeverToggle(RomBuffer rom, int map, int spotX, int spotY, int x, int y, int w, int h, int aX, int aY, int bX, int bY, int sound = 0x10)
    {
        var build = Build.Start(rom, map);
        int copyA = build.Copy(aX, aY, w, h), copyB = build.Copy(bX, bY, w, h), place = build.Spot(x, y), triggerId = build.TriggerSpot(Trigger.ActionButton, spotX, spotY), sw = build.Switch();
        // switch off: put B, switch on. switch on: put A back, switch off
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
        build.Entry(Of(Trigger.ActionButton).Category, triggerId, run);
        build.Redraw(sw, place, copyB);
        string note = build.Write();
        return note + $"Added: the action button facing ({spotX}, {spotY}) switches the {w}x{h} tiles at ({x}, {y}) between the ones at ({bX}, {bY}) and the ones at ({aX}, {aY}) " +
               $"(switch {sw:X2}: on while the second look shows); the map's set-up redraws it when the party comes back.";
    }

    // a piece that changes back and forth as the party walks (spikes going up and down): every steps steps the w x h
    // tiles at (x, y) switch between look B (at (bX, bY)) and look A (kept at (aX, aY)). the room check runs after
    // every step the party takes ($83:BFE8 sets $7FD0A3) and a variable counts the steps.
    // a thread that loops with waits holds the party still while it runs, so you can't make a clock out of one
    // (checked in the game)
    public static string AddStepTimer(RomBuffer rom, int map, int x, int y, int w, int h, int aX, int aY, int bX, int bY, int steps)
    {
        steps = Math.Clamp(steps, 1, 200);
        var build = Build.Start(rom, map);
        int copyA = build.Copy(aX, aY, w, h), copyB = build.Copy(bX, bY, w, h), place = build.Spot(x, y), sw = build.Switch(temporary: true);
        int variable = FreeVariable(build.Pb);
        build.Prepend(0, 0, next =>
        {
            var toggle = C(0x34, variable, 0);
            var toB = C(0x06, place, copyB);
            var code = new List<L2PuzzleBlock.Cmd> { C(0x32, variable) };
            var check = C(0x35, variable, steps, 0, 0); check.Targets.Add(toggle);
            var skip = C(0x0A, 0, 0); skip.Targets.Add(next);
            code.AddRange(new[] { check, skip, toggle, Jump(0x0C, sw, toB), C(0x06, place, copyA), C(0x09, sw) });
            var after = C(0x0A, 0, 0); after.Targets.Add(next);
            code.Add(after);
            code.AddRange(new[] { toB, C(0x08, sw), next });
            return code;
        });
        // as the map loads: look A, the count from 0
        build.Prepend(0, 2, next => new() { C(0x09, sw), C(0x34, variable, 0), next });
        string note = build.Write();
        return note + $"Added: the {w}x{h} tiles at ({x}, {y}) switch between the ones at ({bX}, {bY}) and the ones at ({aX}, {aY}) every {steps} step{(steps == 1 ? "" : "s")} the party takes " +
               $"(variable {variable:X2} counts them).";
    }

    // a puzzle variable (A0-BF, bytes at $7F:D114-D133) the block's commands don't use, from BE down (BF is where
    // 9F / A7 write)
    private static int FreeVariable(L2PuzzleBlock puzzleBlock)
    {
        var used = new HashSet<int>();
        foreach (var cmd in puzzleBlock.Code)
            foreach (var x in cmd.Bytes.Skip(1)) if (x >= 0xA0 && x < 0xC0) used.Add(x);
        for (int variable = 0xBE; variable >= 0xA0; variable--) if (!used.Contains(variable)) return variable;
        throw new InvalidOperationException("The map's puzzle variables are all in use.");
    }

    // doors that events open (a key, a story flag)

    // an event area (map info +14: id, x1, y1, x2, y2, the end not included) for event command 43. returns its id
    private static int AddArea(RomBuffer rom, int map, int x, int y, int w, int h)
    {
        int id = FreeId(L2MapInfo.Records(rom, map, L2MapInfo.Areas, 5).Select(r => (int)r.Bytes[0]));
        if (!L2MapInfo.AddRecord(rom, map, L2MapInfo.Areas, 5, new[] { (byte)id, (byte)x, (byte)y, (byte)(x + w), (byte)(y + h) }))
            throw new InvalidOperationException("The map's info block would be too big.");
        return id;
    }

    private static L2Step Ev(params int[] bytes) => new() { Bytes = bytes.Select(x => (byte)x).ToArray() };

    private static L2Step Say(string text) => new() { Bytes = new byte[] { 0x9E, 0x06 }, IsText = true, Text = text, End = 0x01, OriginalEnd = 0xFF, OriginalText = "\u0001" };

    // the map's "on entering" event (+2 id 01, where the game draws changes like this, 35 of its 43s) redraws the open
    // look (event command 43, area onto area) while story flag flag is on. the start-up script can't do it: the game
    // never copies tiles there, and a 43 there showed nothing
    private static void RedrawOnFlag(RomBuffer rom, int map, int flag, int areaDoor, int areaOpen)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block.");
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 1);
        var script = entry != null ? L2EventScript.Read(rom, block + entry.Offset, block) : L2EventScript.New(block);
        if (script.Problem != null) throw new InvalidOperationException("The map's \"on entering\" event can't be changed: " + script.Problem);
        var skip = Ev(0x6A, flag, 0, 0);
        skip.Jumps.Add(new L2Jump { Step = script.Steps[0] });
        script.Steps.InsertRange(0, new[] { skip, Ev(0x43, areaDoor, areaOpen), Ev(0x6B) });
        if (entry != null) L2EventBlocks.Save(rom, map, entry, script); else L2EventBlocks.Add(rom, map, L2EventBlocks.GameList, 1, script);
    }

    // a door that opens with a key. walking onto the block in front of it (standX, standY, a step-on area) plays an
    // event that checks the party has item. if it does: "The door opened with the KEY." (the key is taken if takeKey),
    // the w x h tiles at (x, y) turn into the open look at (openX, openY) (event command 43, both layers and where the
    // party can walk) and story flag flag turns on. without it: "It's locked." the "on entering" event redraws it open
    // while the flag is on. a scene started from an action spot with puzzle command BB never played in the game, so
    // the door opens from a step-on event instead
    public static string AddKeyDoor(RomBuffer rom, int map, int standX, int standY, int x, int y, int w, int h, int openX, int openY,
        int item, string itemName, bool takeKey, int flag)
    {
        if (flag < 0 || flag is >= 0xF0 and < 0x100 || flag > L2ExtraFlags.Last)
            throw new InvalidOperationException("No story flag is free for the door (to remember that it is open): free one on the Flags tab of the Events page.");
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block, so it can't have the door's event.");
        int areaDoor = AddArea(rom, map, x, y, w, h), areaOpen = AddArea(rom, map, openX, openY, w, h);
        // the event: already open (flag on) = nothing, no key = locked, the key = open
        var script = L2EventScript.New(block);
        var end = script.Steps[0];
        var lockedAt = Ev(0xB8);
        var played = new L2Step { Bytes = L2ExtraFlags.Step(L2ExtraFlags.Use.IfOn, flag) }; played.Jumps.Add(new L2Jump { Step = end });
        var has = Ev(0x14, 0xC2, item & 0xFF, item >> 8, 1, 0, 0x30, 0, 0, 0xFF); has.Jumps.Add(new L2Jump { Step = lockedAt });
        var steps = new List<L2Step> { played, has };
        if (takeKey) steps.Add(Ev(0x24 + (item >> 8), item & 0xFF, 1));
        steps.AddRange(new[] { Ev(0xB8), Say($"Used the {itemName}.\nThe door opened."), Ev(0x43, areaDoor, areaOpen), Ev(0x6B), new L2Step { Bytes = L2ExtraFlags.Step(L2ExtraFlags.Use.On, flag) }, Ev(0x00),
                               lockedAt, Say("It's locked.") });
        script.Steps.InsertRange(0, steps);
        int triggerId = L2EventBlocks.AddTrigger(rom, map, standX, standY, standX, standY, script);
        RedrawOnFlag(rom, map, flag, areaDoor, areaOpen);
        return $"Added: walking up to the door (step-on area {triggerId} at ({standX}, {standY})) with the {itemName} opens it: the {w}x{h} tiles at ({x}, {y}) become the ones at ({openX}, {openY}) " +
               $"{(takeKey ? "(the key is used up) " : "")}and story flag {flag:X2} turns on; without it, \"It's locked.\" The map's \"on entering\" event draws it open while the flag is on.";
    }

    // a door that's open once story flag flag is on (set by any event, a talk, a cutscene). the "on entering" event
    // draws the open look (at (openX, openY)) over the w x h tiles at (x, y) as the party enters the map. an event on
    // this map that sets the flag can open it right away with "Copy map tiles from one area to another" (43) between
    // the two areas this adds
    public static string AddFlagDoor(RomBuffer rom, int map, int x, int y, int w, int h, int openX, int openY, int flag)
    {
        int areaDoor = AddArea(rom, map, x, y, w, h), areaOpen = AddArea(rom, map, openX, openY, w, h);
        RedrawOnFlag(rom, map, flag, areaDoor, areaOpen);
        return $"Added: the {w}x{h} tiles at ({x}, {y}) are drawn open (the ones at ({openX}, {openY})) when the party enters the map with story flag {flag:X2} on " +
               $"(areas {areaDoor} and {areaOpen}: an event here can open it at once with \"Copy map tiles from one area to another\", area {areaDoor} from area {areaOpen}).";
    }
}
