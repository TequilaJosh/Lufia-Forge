namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Where a new game starts, for the Game start page: a map, the preset point the party stands on, a cutscene of that
/// map that plays (or none) and the story flags that are on. Written with <see cref="L2Cutscene.SetTest"/> (the start
/// scenes it makes, F0 up) and read back from the new-game code at $83:ACCD.
/// </summary>
public static class L2GameStart
{
    /// <summary>A cutscene of a map a game can start with (<see cref="None"/>: the party just stands there).</summary>
    public sealed record Choice(int Head, int Id, string Title);

    public const int None = -1;

    /// <summary>The map's cutscenes: scenes, stepping-on areas and talks (the start scenes Lufia Forge made are left out).</summary>
    public static List<Choice> Cutscenes(RomBuffer rom, int map, Func<int, string>? who = null)
    {
        var list = new List<Choice> { new(None, 0, "No cutscene: the party stands on the point") };
        int block = L2Events.Block(rom, map);
        if (block < 0) return list;
        var triggers = L2MapInfo.Records(rom, map, L2MapInfo.Triggers, 5).ToDictionary(r => (int)r.Bytes[0], r => r.Bytes);
        foreach (var e in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript && e.Head != L2EventBlocks.GameList).OrderBy(e => e.Head == L2EventBlocks.SceneList ? 0 : e.Head).ThenBy(e => e.Id))
        {
            if (e.Head == L2EventBlocks.SceneList && e.Id >= 0xF0 && (IsLeaderScene(rom, block + e.Offset) || Setter(rom, block + e.Offset) != null)) continue;
            string title = e.Head switch
            {
                L2EventBlocks.SceneList => L2Cutscene.Starter(rom, map, e.Id) != null ? $"Scene {e.Id:X2}: when the party enters the map" : $"Scene {e.Id:X2}",
                L2EventBlocks.TalkList => $"Talking to {(who != null ? who(e.Id) : $"character {e.Id - 0x4F}")} (plays when you talk)",
                L2EventBlocks.TriggerList => triggers.TryGetValue(e.Id, out var t) ? $"Stepping onto ({t[1]},{t[2]})-({t[3] - 1},{t[4] - 1})" : $"Stepping onto area {e.Id}",
                _ => $"Event {e.Id:X2}",
            };
            try
            {
                var first = L2EventScript.Read(rom, block + e.Offset, block).Steps.FirstOrDefault(x => x.IsText)?.Text.Replace("\n", " ") ?? "";
                if (first.Length > 0) title += ": \"" + (first.Length > 40 ? first[..40] + "..." : first) + "\"";
            }
            catch (Exception) { }
            list.Add(new Choice(e.Head, e.Id, title));
        }
        return list;
    }

    /// <summary>The story flags a cutscene checks.</summary>
    public static SortedSet<int> CheckedFlags(RomBuffer rom, int map, Choice c) =>
        Script(rom, map, c) is { } s ? L2Cutscene.Checked(s.Steps).Flags : new SortedSet<int>();

    /// <summary>Which of the flags the cutscene checks should be on for it to play furthest (playback run with each
    /// combination; the fewest flags on among the best). Empty when it checks none or too many to try.</summary>
    public static HashSet<int> BestFlags(RomBuffer rom, int map, Choice c, int x, int y)
    {
        var s = Script(rom, map, c);
        if (s == null) return new HashSet<int>();
        var flags = L2Cutscene.Checked(s.Steps).Flags.ToList();
        if (flags.Count == 0 || flags.Count > 6) return new HashSet<int>();
        HashSet<int> best = new();
        int bestSteps = -1;
        for (int mask = 0; mask < 1 << flags.Count; mask++)
        {
            var on = flags.Where((_, i) => (mask >> i & 1) != 0).ToHashSet();
            var start = L2Cutscene.Initial(rom, map, x, y);
            start.Flags.UnionWith(on);
            int steps;
            try { steps = L2Cutscene.Run(rom, map, start, s.Steps, _ => "").Select(r => r.Step).Distinct().Count(); }
            catch (Exception) { continue; }
            if (steps > bestSteps || steps == bestSteps && on.Count < best.Count) { best = on; bestSteps = steps; }
        }
        return best;
    }

    /// <summary>A preset point inside a stepping-on cutscene's area (its walks start from there), or -1.</summary>
    public static int PointOnArea(RomBuffer rom, int map, Choice c)
    {
        if (c.Head != L2EventBlocks.TriggerList) return -1;
        var t = L2MapInfo.Records(rom, map, L2MapInfo.Triggers, 5).FirstOrDefault(r => r.Bytes[0] == c.Id).Bytes;
        if (t == null) return -1;
        var pts = L2StoryClear.PresetPoints(rom, map).Where(p => p.X >= t[1] && p.X < t[3] && p.Y >= t[2] && p.Y < t[4]).ToList();
        if (pts.Count == 0) return -1;
        int cx = (t[1] + t[3] - 1) / 2;
        return pts.OrderBy(p => Math.Abs(p.X - cx)).First().Id;
    }

    /// <summary>A new game starts on the map at the point, with the flags on, and the cutscene plays.</summary>
    public static string Set(RomBuffer rom, int map, Choice c, int point, IReadOnlyCollection<int> flags)
    {
        if (c.Head == None)
        {
            if (!L2StoryClear.CanStart(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's, so the start can't be moved.");
            L2StartParty.Keep(rom);
            return L2MapEdit.SetTestStart(rom, map, point, flags);
        }
        return L2Cutscene.SetTest(rom, map, c.Head, c.Id, point, flags);
    }

    /// <summary>Where a new game starts now, in words.</summary>
    public static string Describe(RomBuffer rom, Func<int, string> mapName)
    {
        var (map, scene, point) = L2MapEdit.NewGame(rom);
        if (map == 0x02 && scene == 0x01 && point == 0x06) return "A new game starts with the game's own opening.";
        string text = $"A new game starts on map {map:X2} {mapName(map)} at point {point}";
        try
        {
            int block = L2Events.Block(rom, map);
            var lists = L2EventBlocks.ReadLists(rom, block);
            var e = lists.FirstOrDefault(x => x.Head == L2EventBlocks.SceneList && x.Id == scene);
            if (e == null) return text + $" (scene {scene:X2}, which the map doesn't have).";
            var set = Setter(rom, block + e.Offset);
            if (set is { } st)
            {
                if (st.Flags.Count > 0) text += ", with story flag" + (st.Flags.Count > 1 ? "s " : " ") + string.Join(" ", st.Flags.Select(f => f.ToString("X2"))) + " on";
                scene = st.Scene;
                e = lists.FirstOrDefault(x => x.Head == L2EventBlocks.SceneList && x.Id == scene);
                if (e == null) return text + ".";
            }
            if (!IsLeaderScene(rom, block + e.Offset)) return text + $". Scene {scene:X2} plays.";
            var copy = L2EventScript.Read(rom, block + e.Offset, block).Steps.Skip(3).ToList();
            if (copy.Count <= 1) return text + ". No cutscene plays.";
            foreach (var c in Cutscenes(rom, map).Where(c => c.Head == L2EventBlocks.TriggerList))
                if (Script(rom, map, c) is { } s && Same(s.Steps, copy)) return text + $". {c.Title.Split(':')[0]} plays.";
            return text + ". A cutscene plays (one that has changed since: set the start again to play it as it is now).";
        }
        catch (Exception) { return text + "."; }
    }

    private static L2EventScript? Script(RomBuffer rom, int map, Choice c)
    {
        if (c.Head == None) return null;
        int block = L2Events.Block(rom, map);
        var e = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(x => x.Head == c.Head && x.Id == c.Id);
        if (e == null) return null;
        var s = L2EventScript.Read(rom, block + e.Offset, block);
        return s.Problem == null ? s : null;
    }

    /// <summary>The same steps (commands and text; jump operands differ with the place).</summary>
    private static bool Same(List<L2Step> a, List<L2Step> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.IsText == p.Second.IsText && p.First.Op == p.Second.Op && p.First.Text == p.Second.Text &&
            p.First.Bytes.Length == p.Second.Bytes.Length && (p.First.Jumps.Count > 0 || p.First.Bytes.SequenceEqual(p.Second.Bytes)));

    /// <summary>The start scene that shows the leader and fades in (2D 00 59 18 76), maybe with a cutscene after it.</summary>
    private static bool IsLeaderScene(RomBuffer rom, int p) => rom.ReadBytes(p, 5).SequenceEqual(new byte[] { 0x2D, 0x00, 0x59, 0x18, 0x76 });

    /// <summary>A start scene that turns flags on and goes to a scene of the map (1A ff..., 1D cc nn..., 16 map scene point).</summary>
    private static (List<int> Flags, int Scene)? Setter(RomBuffer rom, int p)
    {
        var flags = new List<int>();
        for (int n = 0; n < 300 && p + 4 < rom.Length; n++)
        {
            int op = rom.ReadByte(p);
            if (op == 0x1A) { flags.Add(rom.ReadByte(p + 1)); p += 2; }
            else if (op == 0x1D) p += 3;
            else return op == 0x16 ? (flags, rom.ReadByte(p + 2)) : null;
        }
        return null;
    }
}
