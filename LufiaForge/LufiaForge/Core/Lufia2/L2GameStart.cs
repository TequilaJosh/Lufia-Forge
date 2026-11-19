namespace LufiaForge.Core.Lufia2;

// where a new game starts, for the Game start page: a map, the preset point the party stands on, a cutscene of that
// map that plays (or none) and the story flags that are on. written with L2Cutscene.SetTest (the start scenes it
// makes, F0 up) and read back from the new-game code at $83:ACCD
public static class L2GameStart
{
    // a cutscene of a map a game can start with (None = the party just stands there)
    public sealed record Choice(int Head, int Id, string Title);

    public const int None = -1;

    // the map's cutscenes: scenes, stepping-on areas and talks (the start scenes we made are left out)
    public static List<Choice> Cutscenes(RomBuffer rom, int map, Func<int, string>? who = null)
    {
        var list = new List<Choice> { new(None, 0, "No cutscene: the party stands on the point") };
        int block = L2Events.Block(rom, map);
        if (block < 0) return list;
        var triggers = L2MapInfo.Records(rom, map, L2MapInfo.Triggers, 5).ToDictionary(r => (int)r.Bytes[0], r => r.Bytes);
        foreach (var entry in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript && e.Head != L2EventBlocks.GameList).OrderBy(e => e.Head == L2EventBlocks.SceneList ? 0 : e.Head).ThenBy(e => e.Id))
        {
            if (entry.Head == L2EventBlocks.SceneList && entry.Id >= 0xF0 && (IsLeaderScene(rom, block + entry.Offset) || Setter(rom, block + entry.Offset) != null)) continue;
            string title = entry.Head switch
            {
                L2EventBlocks.SceneList => L2Cutscene.Starter(rom, map, entry.Id) != null ? $"Scene {entry.Id:X2}: when the party enters the map" : $"Scene {entry.Id:X2}",
                L2EventBlocks.TalkList => $"Talking to {(who != null ? who(entry.Id) : $"character {entry.Id - 0x4F}")} (plays when you talk)",
                L2EventBlocks.TriggerList => triggers.TryGetValue(entry.Id, out var area) ? $"Stepping onto ({area[1]},{area[2]})-({area[3] - 1},{area[4] - 1})" : $"Stepping onto area {entry.Id}",
                _ => $"Event {entry.Id:X2}",
            };
            try
            {
                var first = L2EventScript.Read(rom, block + entry.Offset, block).Steps.FirstOrDefault(x => x.IsText)?.Text.Replace("\n", " ") ?? "";
                if (first.Length > 0) title += ": \"" + (first.Length > 40 ? first[..40] + "..." : first) + "\"";
            }
            catch (Exception) { }
            list.Add(new Choice(entry.Head, entry.Id, title));
        }
        return list;
    }

    // the story flags a cutscene checks
    public static SortedSet<int> CheckedFlags(RomBuffer rom, int map, Choice choice) =>
        Script(rom, map, choice) is { } script ? L2Cutscene.Checked(script.Steps).Flags : new SortedSet<int>();

    // which of the flags the cutscene checks should be on for it to play furthest (playback run with each
    // combination, the fewest flags on among the best). empty if it checks none or too many to try
    public static HashSet<int> BestFlags(RomBuffer rom, int map, Choice choice, int x, int y)
    {
        var script = Script(rom, map, choice);
        if (script == null) return new HashSet<int>();
        var flags = L2Cutscene.Checked(script.Steps).Flags.ToList();
        if (flags.Count == 0 || flags.Count > 6) return new HashSet<int>();
        HashSet<int> best = new();
        int bestSteps = -1;
        for (int mask = 0; mask < 1 << flags.Count; mask++)
        {
            var on = flags.Where((_, i) => (mask >> i & 1) != 0).ToHashSet();
            var start = L2Cutscene.Initial(rom, map, x, y);
            start.Flags.UnionWith(on);
            int steps;
            try { steps = L2Cutscene.Run(rom, map, start, script.Steps, _ => "").Select(r => r.Step).Distinct().Count(); }
            catch (Exception) { continue; }
            if (steps > bestSteps || steps == bestSteps && on.Count < best.Count) { best = on; bestSteps = steps; }
        }
        return best;
    }

    // a preset point inside a stepping-on cutscene's area (its walks start from there), or -1
    public static int PointOnArea(RomBuffer rom, int map, Choice choice)
    {
        if (choice.Head != L2EventBlocks.TriggerList) return -1;
        var area = L2MapInfo.Records(rom, map, L2MapInfo.Triggers, 5).FirstOrDefault(r => r.Bytes[0] == choice.Id).Bytes;
        if (area == null) return -1;
        var points = L2StoryClear.PresetPoints(rom, map).Where(p => p.X >= area[1] && p.X < area[3] && p.Y >= area[2] && p.Y < area[4]).ToList();
        if (points.Count == 0) return -1;
        int centerX = (area[1] + area[3] - 1) / 2;
        return points.OrderBy(p => Math.Abs(p.X - centerX)).First().Id;
    }

    // a new game starts on the map at the point, with the flags on, and the cutscene plays
    public static string Set(RomBuffer rom, int map, Choice choice, int point, IReadOnlyCollection<int> flags)
    {
        if (choice.Head == None)
        {
            if (!L2StoryClear.CanStart(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's, so the start can't be moved.");
            L2StartParty.Keep(rom);
            return L2MapEdit.SetTestStart(rom, map, point, flags);
        }
        return L2Cutscene.SetTest(rom, map, choice.Head, choice.Id, point, flags);
    }

    // where a new game starts now, in words
    public static string Describe(RomBuffer rom, Func<int, string> mapName)
    {
        var (map, scene, point) = L2MapEdit.NewGame(rom);
        if (map == 0x02 && scene == 0x01 && point == 0x06) return "A new game starts with the game's own opening.";
        string text = $"A new game starts on map {map:X2} {mapName(map)} at point {point}";
        try
        {
            int block = L2Events.Block(rom, map);
            var lists = L2EventBlocks.ReadLists(rom, block);
            var entry = lists.FirstOrDefault(x => x.Head == L2EventBlocks.SceneList && x.Id == scene);
            if (entry == null) return text + $" (scene {scene:X2}, which the map doesn't have).";
            var setter = Setter(rom, block + entry.Offset);
            if (setter is { } found)
            {
                if (found.Flags.Count > 0) text += ", with story flag" + (found.Flags.Count > 1 ? "s " : " ") + string.Join(" ", found.Flags.Select(f => f.ToString("X2"))) + " on";
                scene = found.Scene;
                entry = lists.FirstOrDefault(x => x.Head == L2EventBlocks.SceneList && x.Id == scene);
                if (entry == null) return text + ".";
            }
            if (!IsLeaderScene(rom, block + entry.Offset)) return text + $". Scene {scene:X2} plays.";
            // past the start's own steps: 2D 00, 59 18, 76 and (since 2026-11) AA
            var all = L2EventScript.Read(rom, block + entry.Offset, block).Steps;
            int skip = all.Count > 3 && !all[3].IsText && all[3].Bytes.Length == 1 && all[3].Bytes[0] == 0xAA ? 4 : 3;
            var copy = all.Skip(skip).ToList();
            if (copy.Count <= 1) return text + ". No cutscene plays.";
            foreach (var choice in Cutscenes(rom, map).Where(c => c.Head == L2EventBlocks.TriggerList))
                if (Script(rom, map, choice) is { } script && Same(script.Steps, copy)) return text + $". {choice.Title.Split(':')[0]} plays.";
            return text + ". A cutscene plays (one that has changed since: set the start again to play it as it is now).";
        }
        catch (Exception) { return text + "."; }
    }

    private static L2EventScript? Script(RomBuffer rom, int map, Choice choice)
    {
        if (choice.Head == None) return null;
        int block = L2Events.Block(rom, map);
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(x => x.Head == choice.Head && x.Id == choice.Id);
        if (entry == null) return null;
        var script = L2EventScript.Read(rom, block + entry.Offset, block);
        return script.Problem == null ? script : null;
    }

    // the same steps (commands and text, jump operands differ with the place)
    private static bool Same(List<L2Step> a, List<L2Step> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.IsText == p.Second.IsText && p.First.Op == p.Second.Op && p.First.Text == p.Second.Text &&
            p.First.Bytes.Length == p.Second.Bytes.Length && (p.First.Jumps.Count > 0 || p.First.Bytes.SequenceEqual(p.Second.Bytes)));

    // the start scene that shows the leader and fades in (2D 00 59 18 76), maybe with a cutscene after it
    private static bool IsLeaderScene(RomBuffer rom, int scriptAt) => rom.ReadBytes(scriptAt, 5).SequenceEqual(new byte[] { 0x2D, 0x00, 0x59, 0x18, 0x76 });

    // a start scene that turns flags on and goes to a scene of the map (1A ff..., 1D cc nn..., 16 map scene point)
    private static (List<int> Flags, int Scene)? Setter(RomBuffer rom, int position)
    {
        var flags = new List<int>();
        for (int n = 0; n < 300 && position + 4 < rom.Length; n++)
        {
            int op = rom.ReadByte(position);
            if (op == 0x1A) { flags.Add(rom.ReadByte(position + 1)); position += 2; }
            else if (op == L2ExtraFlags.OpOn) { flags.Add(L2ExtraFlags.First + rom.ReadByte(position + 1)); position += 2; }
            // the party's joins and leaves with their flags (L2MapEdit.StartWithFlags writes them, as IsFlagSetter reads)
            else if (op is 0x1B or 0x2B or 0x2C) position += 2;
            else if (op == 0x1D) position += 3;
            else if (op == 0x35) position += 4;
            else return op == 0x16 ? (flags, rom.ReadByte(position + 2)) : null;
        }
        return null;
    }
}
