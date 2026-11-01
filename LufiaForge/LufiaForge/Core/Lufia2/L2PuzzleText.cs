namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Puzzle commands (<see cref="L2Puzzles"/>) in plain words, with their places looked up on the map: a place operand
/// 20-5F is spot n-20 of the map's spot list (map info +22: id, x, y), 60-7F area n-60 (+24: id, x1, y1, x2, y2, the
/// end not included: $80:E15A), 80-9F every member of object group n-80 (the puzzle block's category 0A lists; members
/// are places again, +60: $80:E566), A0-BF the place in variable n-A0, E0-FF rectangle register n-E0, FB the spot that
/// set the script off, FC-FE the values passed to it. "Leader on" tests ($80:E132/E15A) compare the leader's tile
/// ($06BA, $06E2); "standing on" tests read the collision map (bit 01 a character, 08 a movable object). A tile copy
/// (06, 0E, 7A, 95, 96) takes record b of the map's tile-copy list (+16, 10 bytes: id, layers, x, y, width, height;
/// $83:8B40) and puts that rectangle of the map at the place: maps keep the changed look of a floor or wall off to the
/// side. An exit (1A) is record b of the exit list (+06). Switches are the puzzle flags at $7F:D100, not story flags.
/// </summary>
public sealed class L2PuzzleText
{
    private readonly Dictionary<int, (int X, int Y)> _spots = new(), _doors = new(), _objects = new();
    private readonly Dictionary<int, (int X1, int Y1, int X2, int Y2)> _areas = new();
    private readonly Dictionary<int, (int Layers, int X, int Y, int W, int H)> _copies = new();
    private readonly Dictionary<int, (int Map, int X, int Y)> _exits = new();
    private readonly Dictionary<int, List<int>> _groups = new();
    private readonly Func<int, string> _mapName;

    public L2PuzzleText(RomBuffer rom, int map)
    {
        foreach (var (_, b) in L2MapInfo.Records(rom, map, 0x22, 3)) _spots[b[0]] = (b[1], b[2]);
        foreach (var (_, b) in L2MapInfo.Records(rom, map, 0x24, 5)) _areas[b[0]] = (b[1], b[2], b[3], b[4]);
        foreach (var (_, b) in L2MapInfo.Records(rom, map, 0x16, 10)) _copies[b[0]] = (b[1], b[2], b[3], b[4], b[5]);
        foreach (var (_, b) in L2MapInfo.Records(rom, map, L2MapInfo.Exits, 9)) _exits[b[0]] = (b[8], b[6], b[7]);
        foreach (var (_, b) in L2MapInfo.Records(rom, map, L2MapInfo.Doors, 15)) _doors[b[0]] = (b[1], b[2]);
        foreach (var (_, b) in L2MapInfo.Records(rom, map, 0x28, 3)) _objects[b[0]] = (b[1], b[2]);
        int block = L2Puzzles.Block(rom, map);
        if (block >= 0)
            foreach (var (cat, id, off) in L2Puzzles.Entries(rom, block).Where(e => e.Category == 5))
            {
                var l = new List<int>();
                for (int p = block + off; p < rom.Length && rom.ReadByte(p) != 0xFF && l.Count < 64; p++) l.Add(rom.ReadByte(p));
                _groups[id] = l;
            }
        _mapName = m => { try { return $"{L2Maps.Name(rom, m)} ({m:X2})"; } catch (Exception) { return $"map {m:X2}"; } };
    }

    /// <summary>How a command reads its place operand: a spot only ($80:EA09: every value 20-DF is spot n-20), the same
    /// after a variable is looked up (A0-BF: the place in the variable), a spot or an area ($80:E92A: 60-7F are areas),
    /// or a test that also takes groups (80-9F, $80:E566).</summary>
    public enum PlaceKind { Spot, SpotVar, Area, Test }

    public static PlaceKind KindOf(int op) => op switch
    {
        0x06 or 0x0E or 0x7A or 0x91 or 0xA8 or 0x22 or 0x25 or 0x2A or 0x5E or 0x0F or 0x6C => PlaceKind.Spot,
        0x85 or 0x9C or 0xBA or 0xBC or 0xA7 or 0x94 or 0x95 or 0x96 or 0x87 or 0x88 or 0x89 or 0x9F => PlaceKind.SpotVar,
        (>= 0x64 and <= 0x67) or (>= 0x7C and <= 0x81) or (>= 0xA3 and <= 0xA6) or (>= 0xAF and <= 0xB3) or 0x69 or 0x55 or 0x78 => PlaceKind.Area,
        _ => PlaceKind.Test,
    };

    private string SpotText(int id) =>
        _spots.TryGetValue(id, out var s) ? $"spot {id} ({s.X}, {s.Y})" : $"spot {id} (not in this map's list)";

    /// <summary>Where a place operand is, in words ("spot 2 (34, 21)", "area 0 (34, 20)").</summary>
    public string Place(int p, PlaceKind kind = PlaceKind.Test)
    {
        switch (p)
        {
            case 0xFB: return "the spot that set this off";
            case 0xFC: return "the place passed in (1st)";
            case 0xFD: return "the place passed in (2nd)";
            case 0xFE: return "the place passed in (3rd)";
            case >= 0xE0: return $"rectangle {p - 0xE0}";
        }
        if (p >= 0xA0 && p < 0xC0 && kind != PlaceKind.Spot) return $"the place in {Var(p)}";
        if (p < 0x20) return $"place {p:X2}";
        if (kind is PlaceKind.Spot or PlaceKind.SpotVar || p < 0x60) return SpotText(p - 0x20);
        if (p < 0x80)
        {
            if (!_areas.TryGetValue(p - 0x60, out var a)) return $"area {p - 0x60} (not in this map's list)";
            return a.X2 - a.X1 <= 1 && a.Y2 - a.Y1 <= 1 ? $"area {p - 0x60} ({a.X1}, {a.Y1})" : $"area {p - 0x60} ({a.X1}, {a.Y1})-({a.X2 - 1}, {a.Y2 - 1})";
        }
        if (p < 0xA0 && kind == PlaceKind.Test)
            return _groups.TryGetValue(p - 0x80, out var g) ? $"any of group {p - 0x80} ({string.Join(", ", g.Select(m => Place(m + 0x60)))})" : $"group {p - 0x80}";
        return $"place {p:X2}";
    }

    /// <summary>The tiles a place covers with a short label, for marking it on the map; empty when it is only known as
    /// the game runs (a variable, a rectangle register, the spot that set it off).</summary>
    public List<(int X, int Y, int W, int H, string Label)> Tiles(int p, PlaceKind kind = PlaceKind.Test)
    {
        var l = new List<(int, int, int, int, string)>();
        if (p < 0x20 || p >= 0xE0 || p >= 0xA0 && p < 0xC0 && kind != PlaceKind.Spot) return l;
        if (kind is PlaceKind.Spot or PlaceKind.SpotVar || p < 0x60)
        {
            if (_spots.TryGetValue(p - 0x20, out var s)) l.Add((s.X, s.Y, 1, 1, $"spot {p - 0x20}"));
        }
        else if (p < 0x80)
        {
            if (_areas.TryGetValue(p - 0x60, out var a)) l.Add((a.X1, a.Y1, Math.Max(1, a.X2 - a.X1), Math.Max(1, a.Y2 - a.Y1), $"area {p - 0x60}"));
        }
        else if (p < 0xA0 && kind == PlaceKind.Test && _groups.TryGetValue(p - 0x80, out var g))
            foreach (int m in g) l.AddRange(Tiles(m + 0x60));
        return l;
    }

    /// <summary>What a command touches on the map, for the stage: its places, a tile copy's source, a door, a movable
    /// object; each with a short label.</summary>
    public List<(int X, int Y, int W, int H, string Label)> Marks(RomBuffer rom, int at)
    {
        var marks = new List<(int, int, int, int, string)>();
        int op = rom.ReadByte(at);
        if (!L2Puzzles.Ops.TryGetValue(op, out var d) || d.Args <= 0) return marks;
        var b = rom.ReadBytes(at, 1 + d.Args);
        var letters = d.Form.Split(' ');
        for (int i = 0; i < letters.Length && i + 1 < b.Length; i++)
            if (letters[i] == "p") marks.AddRange(Tiles(b[i + 1], KindOf(op)));
        if (op is 0x06 or 0x0E or 0x7A or 0x94 or 0x95 or 0x96 && b.Length > 2 && _copies.TryGetValue(b[2], out var c))
            marks.Add((c.X, c.Y, Math.Max(1, c.W), Math.Max(1, c.H), "copied from"));
        if (op is 0x02 or 0x03 or 0x28 or 0xA0 or 0xA1 && _doors.TryGetValue(b[1], out var dr)) marks.Add((dr.X, dr.Y, 1, 1, $"door {b[1]}"));
        if (op is 0x25 && b.Length > 2 && _objects.TryGetValue(b[2], out var o)) marks.Add((o.X, o.Y, 1, 1, $"object {b[2]:X2}"));
        return marks;
    }

    /// <summary>A puzzle switch operand (FB: the one numbered like the place tested last; FC-FE: passed in).</summary>
    public static string Switch(int f) => f switch
    {
        0xFB => "the switch of the place tested last",
        0xFC or 0xFD or 0xFE => $"the switch passed in ({f - 0xFB})",
        _ => $"switch {f:X2}",
    };

    private string Copy(int b) =>
        b >= 0xFC && b <= 0xFE ? $"the tile copy passed in ({b - 0xFB})" :
        _copies.TryGetValue(b, out var c) ? $"the {c.W}x{c.H} tiles from ({c.X}, {c.Y})" : $"tile copy {b:X2} (not in this map's list)";

    private string Exit(int b) =>
        _exits.TryGetValue(b, out var e) ? $"exit {b} to {_mapName(e.Map)} at ({e.X}, {e.Y})" : $"exit {b}";

    /// <summary>A door operand: C0-FA are door n-C0 ($80:E9B0), FB-FE a value passed in.</summary>
    private string Door(int b)
    {
        if (b >= 0xFB) return "the door passed in";
        if (b >= 0xC0) b -= 0xC0;
        return _doors.TryGetValue(b, out var d) ? $"door {b} ({d.X}, {d.Y})" : $"door {b}";
    }

    private static string Var(int v) => $"variable {v:X2}";
    private static string Value(int x) => x >= 0xA0 && x < 0xC0 ? Var(x) : x >= 0xFC && x <= 0xFE ? $"the value passed in ({x - 0xFB})" : x.ToString();
    private static string Rect(int r) => r >= 0xE0 && r < 0xFB ? $"rectangle {r - 0xE0}" : r >= 0xFC && r <= 0xFE ? $"the rectangle passed in ({r - 0xFB})" : $"rectangle {r:X2}";
    /// <summary>Directions of the move commands (64-67 and the like: 0 up, 1 down, 2 left, 3 right, as the actors'; North
    /// Lighthouse 5F's block goes "1" three times from (34, 20) into the hole at (34, 21)-(34, 22)). The one-step
    /// commands 97-9A are read from their code as down, left, up, right.</summary>
    private static readonly string[] Dirs = { "up", "down", "left", "right" };
    private static readonly string[] StepDirs = { "down", "left", "up", "right" };
    private static string Who(int a) => a >= 0xA0 && a < 0xC0 ? $"the character in {Var(a)}" : a >= 0xFC && a <= 0xFE ? $"the character passed in ({a - 0xFB})" : $"character {a:X2}";

    /// <summary>One command in words. <paramref name="step"/> names the step at a file offset ("step 9").</summary>
    public string Describe(RomBuffer rom, int at, int block, Func<int, string> step, int lastTested = -1)
    {
        int op = rom.ReadByte(at);
        var kind = KindOf(op);
        string P(int v) => Place(v, kind);
        int len = L2Puzzles.Length(rom, at);
        var b = rom.ReadBytes(at, len);
        int A(int i) => i < b.Length ? b[i] : 0;
        string W(int i) => step(block + (A(i) | A(i + 1) << 8));
        string Tail() => step(block + (A(len - 2) | A(len - 1) << 8));
        List<int> ListArgs() { var l = new List<int>(); for (int i = 1; i < len - 3 && b[i] != 0xFF; i++) l.Add(b[i]); return l; }
        string Passed() { var l = ListArgs(); return l.Count == 0 ? "" : $" (passing {string.Join(", ", l.Select(x => x < 0x20 || P(x).Contains("not in") ? x.ToString("X2") : P(x)))})"; }
        switch (op)
        {
            case 0x00 or 0x07 or 0x2C or 0x2D or 0x2E or 0x56 or 0x62 or 0x93 or 0x9B or 0xAC or 0xAD: return "End (this thread stops)";
            case 0x01: return $"If {Switch(A(1))} is on: go to {W(2)}";
            case 0x0C: return $"If {Switch(A(1))} is off: go to {W(2)}";
            case 0x08: return $"Turn {Switch(A(1))} on";
            case 0x09: return $"Turn {Switch(A(1))} off";
            case 0x0A: return $"Go to {W(1)}";
            case 0x0B: return $"Sound effect {A(1):X2}";
            case 0x11: return $"Wait {A(1)} frame{(A(1) == 1 ? "" : "s")}";
            case 0x02: return $"Open {Door(A(1))}";
            case 0x03: return $"Close {Door(A(1))}";
            case 0xA0: return $"Open {Door(A(1))} at once (no animation)";
            case 0xA1: return $"Close {Door(A(1))} at once (no animation)";
            case 0x28: return $"Open {Door(A(1))} if the last test was true, else close it";
            case 0x06: return $"Change the map at {P(A(1))}: copy {Copy(A(2))} onto it";
            case 0x0E: return $"Change the map at {P(A(1))}: copy {Copy(A(2))} onto it (variant 0E)";
            case 0x7A: return $"Change the map at {P(A(1))}: copy {Copy(A(2))} onto it (variant 7A)";
            case 0x1F: return $"Change the map at ({A(1)}, {A(2)}): copy {Copy(A(3))} onto it";
            case 0x8F: return $"Apply tile change set {A(1):X2} (the map's tile-change list)";
            case 0x95: return $"If the tiles at {P(A(1))} don't look like {Copy(A(2))}: go to {W(3)}";
            case 0x96: return $"If the tiles at {P(A(1))} look like {Copy(A(2))}: go to {W(3)}";
            case 0x94: return $"Test: do the tiles at {P(A(1))} look like {Copy(A(2))}?";
            case 0x12: return $"If the leader isn't on {P(A(1))}: go to {W(2)}";
            case 0x6D: return $"If the leader is on {P(A(1))}: go to {W(2)}";
            case 0x13: return $"Test: is the leader on {P(A(1))}? (and did that change)";
            case 0x15: return $"Test: does anyone or anything stand on {P(A(1))}? (and did that change)";
            case 0x16: return $"If no one and nothing stands on {P(A(1))}: go to {W(2)}";
            case 0x6E: return $"If someone or something stands on {P(A(1))}: go to {W(2)}";
            case 0x17: return $"Test: is a movable object on {P(A(1))}? (and did that change)";
            case 0x18: return $"If no movable object is on {P(A(1))}: go to {W(2)}";
            case 0x6F: return $"If a movable object is on {P(A(1))}: go to {W(2)}";
            case 0x72: return $"Test: does a character stand on {P(A(1))}? (and did that change)";
            case 0x73: return $"If no character stands on {P(A(1))}: go to {W(2)}";
            case 0x74: return $"If a character stands on {P(A(1))}: go to {W(2)}";
            case 0x14: return $"If {Switch(A(1))} is on, or no one and nothing stands on {P(A(1))}: go to {W(2)}";
            case 0x19: return $"If the last test was false: go to {W(1)}";
            case 0x1E: return $"If the last test didn't change: go to {W(1)}; if it changed to false: go to {W(3)}";
            case 0x2B: return $"Unless the last test just changed to false: go to {W(1)}";
            case 0x29: return lastTested >= 0
                ? $"Keep the last test's answer in switch {lastTested:X2} ({Place(lastTested).Split(" (")[0]}'s; on: true)"
                : "Keep the last test's answer in the switch of the place tested (on: true)";
            case 0x26: return $"If the last test changed: start {Tail()} as its own thread{Passed()}";
            case 0x27: return $"Start {Tail()} as its own thread{Passed()}";
            case 0xA9: return $"Run {Tail()} and come back{Passed()}";
            case 0xAA: return "Return to the step after the \"Run\"";
            case 0x63: return "Start the map's event 00 again (a fresh check of the room) and end this thread";
            case 0x0D: return $"If the leader faces direction {A(1)}: go to {W(2)}";
            case 0x71: return $"If the leader doesn't face direction {A(1)}: go to {W(2)}";
            case 0x1A: return $"Take {Exit(A(1))}";
            case 0x85: return $"Set a warp to {P(A(1))} (kind {A(2):X2})";
            case 0x86: return "Start the warp";
            case 0x22: return $"Take away the movable object on {P(A(1))} (its tiles are put back)";
            case 0x25: return $"Put movable object {A(2):X2} on {P(A(1))}";
            case 0x24: return $"Put movable object {A(3):X2} at ({A(1)}, {A(2)})";
            case 0x2A: return $"Add a movable object on {P(A(1))} drawn with tile {A(2):X2}";
            case 0x5E: return $"{Var(A(1))} = the number of the movable object on {P(A(2))} (FF: none)";
            case 0x5F: return $"Wait until movable object {A(1):X2} stops";
            case >= 0x64 and <= 0x67: return $"Move {Who(A(2))} {Dirs[op - 0x64]} from {P(A(1))} (speed {A(3)}){(A(1) >= 0xE0 ? $"; {Rect(A(1))} follows it" : "")}";
            case >= 0x7C and <= 0x7F: return $"Move {Who(A(2))} {Dirs[op - 0x7C]} from {P(A(1))} (speed {A(3)}, {A(4):X2}){(A(1) >= 0xE0 ? $"; {Rect(A(1))} follows it" : "")}";
            case >= 0xA3 and <= 0xA6: return $"Move object {A(2):X2} to {P(A(1))} ({Dirs[op - 0xA3]}, speed {A(3)}, {A(4):X2})";
            case >= 0xAF and <= 0xB2: return $"Character {A(2):X2} walks {Dirs[op - 0xAF]} to {P(A(1))} (speed {A(3)})";
            case 0x68: return $"Wait until {Who(A(1))} stops";
            case 0x6B: return "Wait until the leader stops";
            case >= 0x97 and <= 0x9A: return $"Character {A(1) + 0x4F:X2} takes one step {StepDirs[op - 0x97]}";
            case 0xBC: return $"Character {A(1) + 0x4F:X2} goes to {P(A(2))}";
            case 0x8B: return $"Character {A(1):X2} stops and hides";
            case 0x78: return $"Effect {A(3):X2} at {A(2)} spots in {P(A(1))} (puffs, sparks)";
            case 0x2F: return $"{Var(A(1))} = {Value(A(2))}";
            case 0x34: return $"{Var(A(1))} = {A(2)}";
            case 0x30: return $"{Var(A(1))} + {A(2)}";
            case 0x31: return $"{Var(A(1))} - {A(2)}";
            case 0x32: return $"{Var(A(1))} + 1";
            case 0x33: return $"{Var(A(1))} - 1";
            case >= 0x35 and <= 0x3A: return $"If {Var(A(1))} {new[] { "=", "isn't", ">", "<", ">=", "<=" }[op - 0x35]} {A(2)}: go to {W(3)}";
            case >= 0x3B and <= 0x40: return $"If {Var(A(1))} {new[] { "=", "isn't", ">", "<", ">=", "<=" }[op - 0x3B]} {Value(A(2))}: go to {W(3)}";
            case 0x9C: return $"{Var(A(1))} = the tile type at {P(A(2))}";
            case 0x69: return $"{Rect(A(1))} = {P(A(2))}";
            case 0x55: return $"{Rect(A(1))} = {P(A(2))} (as stored)";
            case 0x82: return $"{Rect(A(1))} = the tile of movable object {A(2):X2}";
            case 0x83: return $"{Rect(A(1))}: right edge = left + {A(2)}";
            case 0x84: return $"{Rect(A(1))}: bottom edge = top + {A(2)}";
            case >= 0x41 and <= 0x54:
            {
                string field = new[] { "left", "top", "right", "bottom" }[(op - 0x41) / 5];
                return ((op - 0x41) % 5) switch
                {
                    0 => $"{Rect(A(1))}: {field} = {A(2)}",
                    1 => $"{Rect(A(1))}: {field} + {A(2)}",
                    2 => $"{Rect(A(1))}: {field} - {A(2)}",
                    3 => $"{Rect(A(1))}: {field} + 1",
                    _ => $"{Rect(A(1))}: {field} - 1",
                };
            }
            case 0x9D: return $"{Var(A(1))} = the {new[] { "left", "top", "right", "bottom" }[A(3) >> 6 & 3]} of {Rect(A(2))}";
            case 0x9E: return $"The {new[] { "left", "top", "right", "bottom" }[A(3) >> 6 & 3]} of {Rect(A(2))} = {Var(A(1))}";
            case 0x58: return $"Scroll layer {A(1)} by ({(sbyte)A(2)}, {(sbyte)A(3)}) (speeds {A(4)}, {A(5)})";
            case 0x59: return "Finish the scroll";
            case 0xB4: return $"Camera to the leader + ({(sbyte)A(1)}, {(sbyte)A(2)})";
            case 0xB5: return "Camera back on the leader";
            case 0xBE: return $"Move the view by ({(sbyte)A(1)}, {(sbyte)A(2)}) from the leader";
            case 0x1C: return $"Screen effect {A(1):X2} {A(2):X2}";
            case 0x20: return $"If any of characters {string.Join(", ", ListArgs().Select(x => (x + 0x4F).ToString("X2")))} is still moving: go to {Tail()}";
            case 0xBA: return $"Move treasure {A(1)} to {P(A(2))}";
            case 0xBB: return $"Start the map's event {A(1):X2} (a cutscene or text)";
            case 0x6A: return $"Leader action {A(1):X2}";
            case 0x23: return $"Leader action {A(1):X2} (by room)";
            case 0x57: return "Nothing";
        }
        // the rest: the reference name
        return L2Puzzles.Ops.TryGetValue(op, out var d) ? d.Name : "(not a command)";
    }
}
