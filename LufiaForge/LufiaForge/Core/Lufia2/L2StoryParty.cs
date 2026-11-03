namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Who is in the party at an event, as the story has it, for test starts. Story flags 02-07 are "in the party":
/// Selan 02, Guy 03, Artea 04, Dekar 05, Tia 06, Lexis 07 (every join, 2B, sets its hero's flag and every leave, 2C,
/// clears it; Parcelyte 2D scene 02 clears Guy's, Dekar's and Tia's as Maxim and Selan set off). The flags an event needs
/// to play (<see cref="L2Cutscene.FlagsToPlay(RomBuffer, int, int, int)"/>) say which events came before it: the one
/// that sets each such flag, and before that the ones that set the flags it needs. The joins and leaves among those,
/// in story order, give the party; the heroes the cutscene itself uses (walks, speaks as, "13 hero") are always in.
/// North Lighthouse 6F's step-on area 1 needs flag 37, set by 2D scene 02: Maxim and Selan (with Maxim alone, her walk
/// and lines went to someone else).
/// </summary>
public static class L2StoryParty
{
    /// <summary>The "in the party" story flag of each hero (Maxim has none: he is always in).</summary>
    public static readonly int[] HeroFlag = { -1, 0x02, 0x03, 0x04, 0x06, 0x05, 0x07 };

    public const int MaxParty = 4;

    public sealed record Result(List<int> Heroes, string Why);

    private sealed record Ev(int Map, int Head, int Id, L2EventScript Script, HashSet<int> Sets);

    /// <summary>Every script event of the game (test starts, scenes F0 up, left out) with the story flags it turns on.</summary>
    private static List<Ev> All(RomBuffer rom)
    {
        var list = new List<Ev>();
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            int block = L2Events.Block(rom, m);
            if (block < 0) continue;
            List<L2EventBlocks.Entry> lists;
            try { lists = L2EventBlocks.ReadLists(rom, block); } catch (Exception) { continue; }
            foreach (var e in lists.Where(e => e.IsScript && !(e.Head == L2EventBlocks.SceneList && e.Id >= 0xF0)))
            {
                L2EventScript s;
                try { s = L2EventScript.Read(rom, block + e.Offset, block); } catch (Exception) { continue; }
                if (s.Problem != null) continue;
                list.Add(new Ev(m, e.Head, e.Id, s, s.Steps.Where(x => x.Op == 0x1A && x.Bytes.Length >= 2).Select(x => (int)x.Bytes[1]).ToHashSet()));
            }
        }
        return list;
    }

    private static bool IsHeroFlag(int f) => f >= 0x01 && f <= 0x07;

    /// <summary>The heroes a cutscene uses as party characters: walks, turns and the like (actor 0-6 is hero 0-6),
    /// lines spoken as a hero (61-67) and "13 hero".</summary>
    public static SortedSet<int> Used(L2EventScript script, int head, int id)
    {
        var used = new SortedSet<int>(L2Cutscene.ActorsUsed(script, head, id).Where(a => a >= 0 && a < 7));
        foreach (var st in script.Steps)
            if (st.Op >= 0x61 && st.Op <= 0x67) used.Add(L2EventCommands.SpeakerOf61[st.Op - 0x61]);
        return used;
    }

    /// <summary>The party for a test of event (<paramref name="head"/>, <paramref name="id"/>) of
    /// <paramref name="map"/> (head -1: the map's start-up script) with the story flags <paramref name="flagsOn"/> on.</summary>
    public static Result Of(RomBuffer rom, int map, int head, int id, IReadOnlyCollection<int> flagsOn)
    {
        var all = All(rom);
        // the events that send the party to each scene (16 map scene point)
        var startedBy = new Dictionary<(int Map, int Scene), List<Ev>>();
        foreach (var e in all)
            foreach (var st in e.Script.Steps.Where(st => st.Op == 0x16 && st.Bytes.Length >= 4))
            {
                if (!startedBy.TryGetValue((st.Bytes[1], st.Bytes[2]), out var l)) startedBy[(st.Bytes[1], st.Bytes[2])] = l = new();
                if (!l.Contains(e)) l.Add(e);
            }
        // what an event needs: the flags it plays with, the ones the map needs to make its characters and, for a scene
        // that only one event starts, what that event needs
        var needs = new Dictionary<Ev, List<int>>();
        List<int> Needs(Ev e)
        {
            if (needs.TryGetValue(e, out var n)) return n;
            needs[e] = n = new List<int>();   // (a loop of scenes ends here)
            n.AddRange(L2Cutscene.FlagsToPlay(rom, e.Map, e.Head, e.Id));
            try { n.AddRange(L2Cutscene.Needs(rom, e.Map, e.Head, e.Id).Flags.Except(n)); } catch (Exception) { }
            if (e.Head == L2EventBlocks.SceneList && startedBy.TryGetValue((e.Map, e.Id), out var by) && by.Count == 1 && by[0] != e)
                n.AddRange(Needs(by[0]).Except(n));
            return n;
        }
        var setters = new Dictionary<int, List<Ev>>();
        foreach (var e in all) foreach (int f in e.Sets) { if (!setters.TryGetValue(f, out var l)) setters[f] = l = new(); l.Add(e); }

        // the story flags that are on by then: the ones given and, back through the one event that sets each, the flags
        // that event needs (several setters: only what all of them need)
        HashSet<int> Closure(IEnumerable<int> start)
        {
            var on = new HashSet<int>();
            var todo = new Queue<int>(start.Where(f => !IsHeroFlag(f)));
            while (todo.Count > 0)
            {
                int f = todo.Dequeue();
                if (!on.Add(f) || !setters.TryGetValue(f, out var by) || by.Count > 6) continue;
                IEnumerable<int> need = Needs(by[0]);
                foreach (var e in by.Skip(1)) need = need.Intersect(Needs(e));
                foreach (int g in need) if (!IsHeroFlag(g) && !on.Contains(g)) todo.Enqueue(g);
            }
            return on;
        }
        var before = Closure(flagsOn);

        // the joins and leaves that came before: an event that turns on one of those flags and changes the party
        var happened = all.Where(e => e.Sets.Any(f => !IsHeroFlag(f) && before.Contains(f)) && Changes(e.Script).Count > 0 &&
                                      !(e.Map == map && e.Head == head && e.Id == id)).ToList();
        // in story order: one before another when a flag it turns on is one the other needs (back through the story)
        var after = happened.ToDictionary(e => e, e => Closure(Needs(e)));
        var order = new List<Ev>();
        var left = happened.OrderBy(e => e.Sets.Where(f => !IsHeroFlag(f)).DefaultIfEmpty(0xFF).Min()).ToList();
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(e => !left.Any(o => o != e && o.Sets.Any(f => !IsHeroFlag(f) && after[e].Contains(f)))) ?? left[0];
            order.Add(next); left.Remove(next);
        }
        var party = new List<int> { 0 };
        var why = new Dictionary<int, string>();
        foreach (var e in order)
            foreach (var (hero, joins) in Changes(e.Script))
            {
                string where = $"{L2Maps.Name(rom, e.Map)} ({e.Map:X2}) {What(e.Head, e.Id)}";
                if (joins) { if (!party.Contains(hero)) party.Add(hero); why[hero] = $"joined in {where}"; }
                else { party.Remove(hero); why.Remove(hero); }
            }
        foreach (int f in flagsOn)
        {
            int hero = Array.IndexOf(HeroFlag, f);
            if (hero > 0 && !party.Contains(hero)) { party.Add(hero); why[hero] = $"story flag {f:X2} is ticked"; }
        }
        // the heroes the cutscene uses are in, the story's others make up the rest of the four
        SortedSet<int> used = new();
        try
        {
            int block = L2Events.Block(rom, map);
            if (head < 0) used = new();
            else if (block >= 0 && L2EventBlocks.ReadLists(rom, block).FirstOrDefault(x => x.Head == head && x.Id == id && x.IsScript) is { } en)
                used = Used(L2EventScript.Read(rom, block + en.Offset, block), head, id);
        }
        catch (Exception) { }
        foreach (int h in used.Where(h => h > 0 && !party.Contains(h))) { party.Add(h); why[h] = "the cutscene uses " + (h == 1 || h == 4 ? "her" : "him"); }
        var keep = new List<int> { 0 };
        keep.AddRange(party.Where(h => h > 0 && used.Contains(h)));
        keep.AddRange(party.Where(h => h > 0 && !used.Contains(h)));
        keep = keep.Distinct().Take(MaxParty).ToList();
        string text = string.Join(", ", keep.Select(h => h == 0 ? "Maxim" : $"{L2EventCommands.HeroNames[h]} ({why.GetValueOrDefault(h, "")})"));
        return new Result(keep, text);
    }

    /// <summary>The party changes an event makes, in step order: joins (2B, story flag 02-07 on) and leaves (2C, flag off).</summary>
    public static List<(int Hero, bool Joins)> Changes(L2EventScript script)
    {
        var l = new List<(int, bool)>();
        foreach (var st in script.Steps)
        {
            var b = st.Bytes;
            if (b.Length < 2) continue;
            if (st.Op is 0x2B or 0x2C && b[1] > 0 && b[1] < 7) l.Add((b[1], st.Op == 0x2B));
            else if (st.Op is 0x1A or 0x1B && Array.IndexOf(HeroFlag, (int)b[1]) is var h && h > 0) l.Add((h, st.Op == 0x1A));
        }
        return l;
    }

    private static string What(int head, int id) => head switch
    {
        L2EventBlocks.SceneList => $"scene {id:X2}",
        L2EventBlocks.TalkList => $"talk {id:X2}",
        L2EventBlocks.TriggerList => $"step-on area {id}",
        _ => $"event {id:X2}",
    };
}
