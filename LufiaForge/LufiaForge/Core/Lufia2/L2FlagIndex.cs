namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Where every kind of Lufia II flag is used, read from the whole ROM once per ROM version:
/// <list type="bullet">
/// <item>story flags 00-FF and the extra flags 100-17F (<see cref="L2ExtraFlags"/>): every event script that turns one
/// on or off or checks it (1A 1B 15 6A, CD-D0, the checks of 14);</item>
/// <item>event counters 00-1F ($7E:079E): 1D 1E 1F, 12 and the checks of 14;</item>
/// <item>treasure chest flags 000-1FF ($7E:093B): the chest of every map that uses each one (<see cref="L2Chests"/>);</item>
/// <item>puzzle switches ($7F:D100): the puzzle scripts that turn them on or off or check them. 00-1F are kept in the
/// save and shared by every map; 20 and up are cleared whenever a map loads ($83:B581);</item>
/// <item>doors: every map's doors (map info list +02), the events and puzzle scripts that open, close or check them.
/// Door states are cleared whenever a map loads, so a door is the map's own.</item>
/// </list>
/// </summary>
public static class L2FlagIndex
{
    /// <summary><see cref="Character"/> is only for names and notes (<see cref="L2FlagNotes"/>): story characters 10-4E.</summary>
    public enum Kind { Story, Counter, Chest, Switch, Door, Character }

    /// <summary>Where a use is: an event (its list <see cref="Head"/> and <see cref="Id"/>; head -1: the start-up
    /// script), a puzzle script (head <see cref="PuzzleHead"/>) or a map's treasure list (<see cref="ChestHead"/>).</summary>
    public const int InitHead = -1, PuzzleHead = -3, ChestHead = -4, DoorHead = -5;

    /// <summary>One use: <see cref="Number"/> is the flag (doors: map * 0x100 + door), <see cref="How"/> "on", "off",
    /// "checks", "sets" ..., <see cref="Where"/> the event's title, <see cref="At"/> the step's file offset.</summary>
    public sealed record Use(Kind Kind, int Number, string How, int Map, int Head, int Id, int At, string Where)
    {
        /// <summary>Turns on / sets / changes (not only reads).</summary>
        public bool Writes => How is "on" or "off" or "sets" or "adds to" or "takes from" or "steps" or "opens" or "closes" or "keeps a test in" or "uses";
    }

    private static RomBuffer? _rom;
    private static int _version = -1;
    private static List<Use> _all = new();

    public static void Invalidate() => _version = -1;

    /// <summary>Every use of every kind (cached until the ROM changes).</summary>
    public static IReadOnlyList<Use> All(RomBuffer rom)
    {
        if (ReferenceEquals(rom, _rom) && _version == rom.Version) return _all;
        var list = new List<Use>();
        int maps = L2Maps.Count(rom);
        for (int m = 0; m < maps; m++)
        {
            try { Events(rom, m, list); } catch (Exception) { }
            try { Puzzles(rom, m, list); } catch (Exception) { }
            try { Chests(rom, m, list); } catch (Exception) { }
            try { Doors(rom, m, list); } catch (Exception) { }
        }
        _rom = rom; _version = rom.Version; _all = list;
        return list;
    }

    /// <summary>The uses of one flag.</summary>
    public static IEnumerable<Use> Of(RomBuffer rom, Kind kind, int number) => All(rom).Where(u => u.Kind == kind && u.Number == number);

    public static readonly (int Head, string Title)[] Lists = { (2, "Map event"), (4, "Scene"), (6, "Talk to"), (8, "Step-on area"), (0x0A, "List A"), (0x0C, "List C") };

    private static void Events(RomBuffer rom, int m, List<Use> list)
    {
        void Walk(int head, int id, string where, IEnumerable<int> starts, int jumpBase)
        {
            foreach (var o in L2Script.Walk(rom, starts, jumpBase).Where(o => !o.IsText))
            {
                var b = rom.ReadBytes(o.At, o.Length);
                void Add(Kind k, int n, string how) => list.Add(new Use(k, n, how, m, head, id, o.At, where));
                var (use, flag) = L2ExtraFlags.Of(b);
                if (use != L2ExtraFlags.Use.None)
                {
                    Add(Kind.Story, flag, use switch { L2ExtraFlags.Use.On => "on", L2ExtraFlags.Use.Off => "off", L2ExtraFlags.Use.IfOn => "checks on", _ => "checks off" });
                    continue;
                }
                switch (b[0])
                {
                    case 0x1D when b.Length >= 3: Add(Kind.Counter, b[1], "sets"); break;
                    case 0x1E when b.Length >= 3: Add(Kind.Counter, b[1], "adds to"); break;
                    case 0x1F when b.Length >= 3: Add(Kind.Counter, b[1], "takes from"); break;
                    case 0x12 when b.Length >= 3: Add(Kind.Counter, b[1], "steps"); break;   // goes by it and adds 1
                    case 0x54 when b.Length >= 2: Add(Kind.Door, m * 0x100 + b[1], "opens"); break;
                    case 0x55 when b.Length >= 2: Add(Kind.Door, m * 0x100 + b[1], "closes"); break;
                    case 0x92 when b.Length >= 2: Add(Kind.Door, m * 0x100 + b[1], "checks"); break;
                    case 0x14:
                        foreach (var c in L2Conditions.Parse(b))
                        {
                            if (c.Kind == L2Conditions.KFlag && c.At + 1 < b.Length)
                                Add(Kind.Story, L2ExtraFlags.CheckFlag(c.Code, b[c.At + 1]), (c.Code & 1) == 0 ? "checks on" : "checks off");
                            else if (c.Kind == L2Conditions.KCounter && c.At + 1 < b.Length)
                                Add(Kind.Counter, b[c.At + 1], "checks");
                        }
                        break;
                }
            }
        }
        int init = L2EventBlocks.InitScript(rom, m);
        Walk(InitHead, -1, "Start-up script", new[] { init }, L2EventBlocks.InitBase(init));
        int block = L2Events.Block(rom, m);
        if (block < 0) return;
        foreach (var e in L2EventBlocks.ReadLists(rom, block))
        {
            if (e.Head == L2EventBlocks.GameList && e.Id == 3) continue;   // not a script: the key item of the map's locked doors
            string title = Lists.FirstOrDefault(l => l.Head == e.Head).Title ?? $"List {e.Head:X2}";
            Walk(e.Head, e.Id, e.Head == L2EventBlocks.TalkList ? $"{title} {(e.Id >= 0x50 ? $"character {e.Id - 0x4F}" : $"story character {e.Id:X2}")}" : $"{title} {e.Id:X2}",
                new[] { block + e.Offset }, block);
        }
    }

    private static void Puzzles(RomBuffer rom, int m, List<Use> list)
    {
        int block = L2Puzzles.Block(rom, m);
        if (block < 0) return;
        int end = L2Puzzles.End(rom, block);
        var seen = new HashSet<int>();
        foreach (var (cat, id, off) in L2Puzzles.Entries(rom, block).Where(e => e.Category != 5))
            foreach (var o in L2Puzzles.Walk(rom, block + off, block, end))
            {
                if (!seen.Add(o.At)) continue;
                var b = rom.ReadBytes(o.At, o.Length);
                void Add(Kind k, int n, string how) => list.Add(new Use(k, n, how, m, PuzzleHead, cat * 0x100 + id, o.At, $"Puzzle script {cat:X2}:{id:X2}"));
                int a = b.Length > 1 ? b[1] : -1;
                bool sw = a is >= 0 and < 0xFB, door = a is >= 0xC0 and < 0xFB;
                switch (b[0])
                {
                    case 0x08 when sw: Add(Kind.Switch, a, "on"); break;
                    case 0x09 when sw: Add(Kind.Switch, a, "off"); break;
                    case 0x01 when sw: Add(Kind.Switch, a, "checks on"); break;
                    case 0x0C when sw: Add(Kind.Switch, a, "checks off"); break;
                    case 0x14 when sw: Add(Kind.Switch, a, "checks on"); break;
                    case 0x02 or 0xA0 when door: Add(Kind.Door, m * 0x100 + a - 0xC0, "opens"); break;
                    case 0x03 or 0xA1 when door: Add(Kind.Door, m * 0x100 + a - 0xC0, "closes"); break;
                    case 0x28 when door: Add(Kind.Door, m * 0x100 + a - 0xC0, "opens or closes"); break;
                }
            }
    }

    private static void Chests(RomBuffer rom, int m, List<Use> list)
    {
        if (m < 2) return;
        foreach (var c in L2Chests.Read(rom, m))
            list.Add(new Use(Kind.Chest, c.Flag, "uses", m, ChestHead, c.Spot, c.At, $"Treasure spot {c.Spot}" + (c.X >= 0 ? $" ({c.X}, {c.Y})" : "") + $", item {c.Item:X3}"));
    }

    private static void Doors(RomBuffer rom, int m, List<Use> list)
    {
        foreach (var (at, b) in L2MapInfo.Records(rom, m, L2MapInfo.Doors, 15))
            list.Add(new Use(Kind.Door, m * 0x100 + b[0], "is", m, DoorHead, b[0], at, $"Door {b[0]} at ({b[1]}, {b[2]})"));
    }

    // ------------------------------------------------------------------ free flags

    /// <summary>Story flags the game's own code reads or writes, or null.</summary>
    public static string? Reserved(int f) => f switch
    {
        0xFF => "the answer of checks and \"the party did not win\" after a story battle",
        0xFE => "set after some story battles",
        >= 0xF0 and <= 0xFF => "cleared on every map change",
        >= 0xC8 and <= 0xD0 => "Iris treasures taken out of the Ancient Cave",
        0x95 => "where the ship can be parked",
        0x41 or 0x50 or 0x5F or 0x62 => "changes the world map",
        _ => null,
    };

    /// <summary>A story flag nothing uses (no event, no game code, no name given in this project), or -1. Story flags
    /// 01-EF first (from the top), then the extra flags 100-17F when <paramref name="extra"/>.</summary>
    public static int FreeStory(RomBuffer rom, bool extra, IEnumerable<int>? avoid = null)
    {
        var used = All(rom).Where(u => u.Kind == Kind.Story).Select(u => u.Number).ToHashSet();
        if (avoid != null) used.UnionWith(avoid);
        for (int f = 0xEF; f >= 0x01; f--)
            if (!used.Contains(f) && Reserved(f) == null && L2FlagNotes.Name(Kind.Story, f) == null) return f;
        if (extra)
        {
            // not in the byte of a counter some event uses (a hack's own counters 10-1F share the extra flags' bytes)
            var counters = All(rom).Where(u => u.Kind == Kind.Counter).Select(u => u.Number).ToHashSet();
            for (int f = L2ExtraFlags.First; f <= L2ExtraFlags.Last; f++)
                if (!used.Contains(f) && L2FlagNotes.Name(Kind.Story, f) == null && !counters.Contains(L2ExtraFlags.FirstCounter + (f - L2ExtraFlags.First) / 8)) return f;
        }
        return -1;
    }

    /// <summary>The game's events use counters 01-0E; with the extra flags in, 10-1F hold them.</summary>
    public static string? CounterReserved(RomBuffer rom, int c) =>
        L2ExtraFlags.HoldsFlags(c) && L2ExtraFlags.IsApplied(rom) ? $"holds extra story flags {L2ExtraFlags.First + (c - 0x10) * 8:X3}-{L2ExtraFlags.First + (c - 0x10) * 8 + 7:X3}" : null;
}
