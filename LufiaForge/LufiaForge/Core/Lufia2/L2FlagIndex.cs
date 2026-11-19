namespace LufiaForge.Core.Lufia2;

// where every kind of lufia 2 flag gets used, read from the whole rom once per rom version:
// - story flags 00-FF and the extra flags 100-17F (L2ExtraFlags): every event script that turns one on or off or
//   checks it (1A 1B 15 6A, CD-D0, the checks of 14)
// - event counters 00-1F ($7E:079E): 1D 1E 1F, 12 and the checks of 14
// - treasure chest flags 000-1FF ($7E:093B): the chest of every map that uses each one (L2Chests)
// - puzzle switches ($7F:D100): the puzzle scripts that turn them on or off or check them. 00-1F are kept in the save
//   and shared by every map, 20 and up get cleared whenever a map loads ($83:B581)
// - doors: every map's doors (map info list +02), the events and puzzle scripts that open, close or check them. door
//   states get cleared whenever a map loads, so a door is the map's own
public static class L2FlagIndex
{
    // Character is only for names and notes (L2FlagNotes): story characters 10-4E
    public enum Kind { Story, Counter, Chest, Switch, Door, Character }

    // where a use is: an event (its list Head and Id, head -1 = the start-up script), a puzzle script (head
    // PuzzleHead) or a map's treasure list (ChestHead)
    public const int InitHead = -1, PuzzleHead = -3, ChestHead = -4, DoorHead = -5;

    // one use: Number is the flag (doors: map * 0x100 + door), How "on", "off", "checks", "sets" ..., Where the
    // event's title, At the step's file offset
    public sealed record Use(Kind Kind, int Number, string How, int Map, int Head, int Id, int At, string Where)
    {
        // turns on / sets / changes (not only reads)
        public bool Writes => How is "on" or "off" or "sets" or "adds to" or "takes from" or "steps" or "opens" or "closes" or "keeps a test in" or "uses";
    }

    private static RomBuffer? _rom;
    private static int _version = -1;
    private static List<Use> _all = new();

    public static void Invalidate() => _version = -1;

    // every use of every kind (cached until the rom changes)
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

    // the uses of one flag
    public static IEnumerable<Use> Of(RomBuffer rom, Kind kind, int number) => All(rom).Where(u => u.Kind == kind && u.Number == number);

    public static readonly (int Head, string Title)[] Lists = { (2, "Map event"), (4, "Scene"), (6, "Talk to"), (8, "Step-on area"), (0x0A, "List A"), (0x0C, "List C") };

    private static void Events(RomBuffer rom, int map, List<Use> list)
    {
        void Walk(int head, int id, string where, IEnumerable<int> starts, int jumpBase)
        {
            foreach (var op in L2Script.Walk(rom, starts, jumpBase).Where(o => !o.IsText))
            {
                var bytes = rom.ReadBytes(op.At, op.Length);
                void Add(Kind kind, int number, string how) => list.Add(new Use(kind, number, how, map, head, id, op.At, where));
                var (use, flag) = L2ExtraFlags.Of(bytes);
                if (use != L2ExtraFlags.Use.None)
                {
                    Add(Kind.Story, flag, use switch { L2ExtraFlags.Use.On => "on", L2ExtraFlags.Use.Off => "off", L2ExtraFlags.Use.IfOn => "checks on", _ => "checks off" });
                    continue;
                }
                switch (bytes[0])
                {
                    case 0x1D when bytes.Length >= 3: Add(Kind.Counter, bytes[1], "sets"); break;
                    case 0x1E when bytes.Length >= 3: Add(Kind.Counter, bytes[1], "adds to"); break;
                    case 0x1F when bytes.Length >= 3: Add(Kind.Counter, bytes[1], "takes from"); break;
                    case 0x12 when bytes.Length >= 3: Add(Kind.Counter, bytes[1], "steps"); break;   // goes by it and adds 1
                    case 0x54 when bytes.Length >= 2: Add(Kind.Door, map * 0x100 + bytes[1], "opens"); break;
                    case 0x55 when bytes.Length >= 2: Add(Kind.Door, map * 0x100 + bytes[1], "closes"); break;
                    case 0x92 when bytes.Length >= 2: Add(Kind.Door, map * 0x100 + bytes[1], "checks"); break;
                    case 0x14:
                        foreach (var check in L2Conditions.Parse(bytes))
                        {
                            if (check.Kind == L2Conditions.KFlag && check.At + 1 < bytes.Length)
                                Add(Kind.Story, L2ExtraFlags.CheckFlag(check.Code, bytes[check.At + 1]), (check.Code & 1) == 0 ? "checks on" : "checks off");
                            else if (check.Kind == L2Conditions.KCounter && check.At + 1 < bytes.Length)
                                Add(Kind.Counter, bytes[check.At + 1], "checks");
                        }
                        break;
                }
            }
        }
        int init = L2EventBlocks.InitScript(rom, map);
        Walk(InitHead, -1, "Start-up script", new[] { init }, L2EventBlocks.InitBase(init));
        int block = L2Events.Block(rom, map);
        if (block < 0) return;
        foreach (var entry in L2EventBlocks.ReadLists(rom, block))
        {
            if (entry.Head == L2EventBlocks.GameList && entry.Id == 3) continue;   // not a script: the key item of the map's locked doors
            string title = Lists.FirstOrDefault(l => l.Head == entry.Head).Title ?? $"List {entry.Head:X2}";
            Walk(entry.Head, entry.Id, entry.Head == L2EventBlocks.TalkList ? $"{title} {(entry.Id >= 0x50 ? $"character {entry.Id - 0x4F}" : $"story character {entry.Id:X2}")}" : $"{title} {entry.Id:X2}",
                new[] { block + entry.Offset }, block);
        }
    }

    private static void Puzzles(RomBuffer rom, int map, List<Use> list)
    {
        int block = L2Puzzles.Block(rom, map);
        if (block < 0) return;
        int end = L2Puzzles.End(rom, block);
        var seen = new HashSet<int>();
        foreach (var (category, id, offset) in L2Puzzles.Entries(rom, block).Where(e => e.Category != 5))
            foreach (var op in L2Puzzles.Walk(rom, block + offset, block, end))
            {
                if (!seen.Add(op.At)) continue;
                var bytes = rom.ReadBytes(op.At, op.Length);
                void Add(Kind kind, int number, string how) => list.Add(new Use(kind, number, how, map, PuzzleHead, category * 0x100 + id, op.At, $"Puzzle script {category:X2}:{id:X2}"));
                int arg = bytes.Length > 1 ? bytes[1] : -1;
                bool isSwitch = arg is >= 0 and < 0xFB, isDoor = arg is >= 0xC0 and < 0xFB;
                switch (bytes[0])
                {
                    case 0x08 when isSwitch: Add(Kind.Switch, arg, "on"); break;
                    case 0x09 when isSwitch: Add(Kind.Switch, arg, "off"); break;
                    case 0x01 when isSwitch: Add(Kind.Switch, arg, "checks on"); break;
                    case 0x0C when isSwitch: Add(Kind.Switch, arg, "checks off"); break;
                    case 0x14 when isSwitch: Add(Kind.Switch, arg, "checks on"); break;
                    case 0x02 or 0xA0 when isDoor: Add(Kind.Door, map * 0x100 + arg - 0xC0, "opens"); break;
                    case 0x03 or 0xA1 when isDoor: Add(Kind.Door, map * 0x100 + arg - 0xC0, "closes"); break;
                    case 0x28 when isDoor: Add(Kind.Door, map * 0x100 + arg - 0xC0, "opens or closes"); break;
                }
            }
    }

    private static void Chests(RomBuffer rom, int map, List<Use> list)
    {
        if (map < 2) return;
        foreach (var chest in L2Chests.Read(rom, map))
            list.Add(new Use(Kind.Chest, chest.Flag, "uses", map, ChestHead, chest.Spot, chest.At, $"Treasure spot {chest.Spot}" + (chest.X >= 0 ? $" ({chest.X}, {chest.Y})" : "") + $", item {chest.Item:X3}"));
    }

    private static void Doors(RomBuffer rom, int map, List<Use> list)
    {
        foreach (var (at, door) in L2MapInfo.Records(rom, map, L2MapInfo.Doors, 15))
            list.Add(new Use(Kind.Door, map * 0x100 + door[0], "is", map, DoorHead, door[0], at, $"Door {door[0]} at ({door[1]}, {door[2]})"));
    }

    // free flags

    // story flags the game's own code reads or writes, or null
    public static string? Reserved(int flag) => flag switch
    {
        0xFF => "the answer of checks and \"the party did not win\" after a story battle",
        0xFE => "set after some story battles",
        >= 0xF0 and <= 0xFF => "cleared on every map change",
        >= 0xC8 and <= 0xD0 => "Iris treasures taken out of the Ancient Cave",
        0x95 => "where the ship can be parked",
        0x41 or 0x50 or 0x5F or 0x62 => "changes the world map",
        _ => null,
    };

    // a story flag nothing uses (no event, no game code, no name given in this project), or -1. story flags 01-EF
    // first (from the top), then the extra flags 100-17F if extra
    public static int FreeStory(RomBuffer rom, bool extra, IEnumerable<int>? avoid = null)
    {
        var used = All(rom).Where(u => u.Kind == Kind.Story).Select(u => u.Number).ToHashSet();
        if (avoid != null) used.UnionWith(avoid);
        for (int flag = 0xEF; flag >= 0x01; flag--)
            if (!used.Contains(flag) && Reserved(flag) == null && L2FlagNotes.Name(Kind.Story, flag) == null) return flag;
        if (extra)
        {
            // not in the byte of a counter some event uses (a hack's own counters 10-1F share the extra flags' bytes)
            var counters = All(rom).Where(u => u.Kind == Kind.Counter).Select(u => u.Number).ToHashSet();
            for (int flag = L2ExtraFlags.First; flag <= L2ExtraFlags.Last; flag++)
                if (!used.Contains(flag) && L2FlagNotes.Name(Kind.Story, flag) == null && !counters.Contains(L2ExtraFlags.FirstCounter + (flag - L2ExtraFlags.First) / 8)) return flag;
        }
        return -1;
    }

    // the game's events use counters 01-0E. with the extra flags in, 10-1F hold them
    public static string? CounterReserved(RomBuffer rom, int counter) =>
        L2ExtraFlags.HoldsFlags(counter) && L2ExtraFlags.IsApplied(rom) ? $"holds extra story flags {L2ExtraFlags.First + (counter - 0x10) * 8:X3}-{L2ExtraFlags.First + (counter - 0x10) * 8 + 7:X3}" : null;
}
