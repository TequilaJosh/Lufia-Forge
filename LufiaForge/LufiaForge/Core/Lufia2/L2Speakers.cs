using System.Text.Json;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Who speaks a Lufia II line, named the same way on the Text page, the event editor's cards and lists and in
/// playback. How the game picks the speaker ($80:A06E -> $80:BC33, read from its code and checked on all 5,452 lines
/// of the US game): 13 xx and 61-67 store an actor in $09AB (so 13 01 is exactly 62, the party's Selan); 08 and 10
/// leave it, so they speak as the current speaker; a talk starts with $09AB = $09AC = the one talked to ($83:BB19),
/// other events with FF ($83:B740); a text ending 01 puts $09AC back in $09AB when it isn't FF ($80:9DB3). The box goes
/// over the first shown actor with that id ($80:C33F -> $80:BF6F); with none, over the last one the game found, so a
/// line given to a hero who isn't in the party lands on someone else (Selan at home in Parcelyte is story character 19,
/// not the party's Selan). 34 actor sprite shows an actor with another sprite (the opening's "Erim:" lines are Maxim's
/// actor drawn as Erim). Names: Data/Lufia2_Speakers.json (from the game's text: self-introductions, "Name:" lines,
/// people addressed by name; the game's generic sprites by role).
/// </summary>
public sealed class L2Speakers
{
    private sealed record NameData(Dictionary<string, string> sprites, Dictionary<string, string> story, Dictionary<string, List<string>> heroPoses);

    private static readonly Lazy<NameData> Names = new(() =>
    {
        using var st = typeof(L2Speakers).Assembly.GetManifestResourceStream("Data.Lufia2_Speakers.json")
            ?? throw new InvalidOperationException("The Lufia II speaker names are missing from this build.");
        return JsonSerializer.Deserialize<NameData>(st) ?? throw new InvalidOperationException("The Lufia II speaker names can't be read.");
    });

    private static int Hex(string s) => Convert.ToInt32(s, 16);

    /// <summary>Who a character drawn with <paramref name="sprite"/> is (a role for the generic sprites), or null.</summary>
    public static string? SpriteName(int sprite) => Names.Value.sprites.TryGetValue(sprite.ToString("X2"), out var n) ? n : null;

    /// <summary>The name of story character <paramref name="id"/> (10-4F), or null.</summary>
    public static string? StoryName(int id) => Names.Value.story.TryGetValue(id.ToString("X2"), out var n) ? n : null;

    /// <summary>Whether <paramref name="sprite"/> still shows hero <paramref name="hero"/> (its own or one of its poses).</summary>
    public static bool ShowsHero(int hero, int sprite) =>
        sprite == hero || Names.Value.heroPoses.TryGetValue(hero.ToString("X2"), out var l) && l.Any(s => Hex(s) == sprite);

    private readonly Dictionary<int, int> _mapSprites;
    private readonly Dictionary<int, int> _storySprites;

    public RomBuffer Rom { get; }
    public int Map { get; }
    /// <summary>The map has dungeon controls (its start-up script's 69 with bit 01, $09A7): only the leader is made
    /// until 3D lines up the party, so a hero's line before that goes over the leader ($84:8204; checked in BizHawk).</summary>
    public bool Dungeon { get; }

    /// <summary>Text after a 48: the box sits where 48 puts it ($125D), over nobody (checked in BizHawk: Shrine of Daos
    /// E7 scene 62's "[Voice 1]Yeah, I'm fine." and the others).</summary>
    public const string FixedBox = "Off-screen voice (the box sits where the step before puts it)";

    /// <summary>A hero's line in a dungeon before the party lines up.</summary>
    public static string OverLeader(string label) => label + ": only the leader is shown here until the party lines up (3D), so the box goes over the leader";

    public L2Speakers(RomBuffer rom, int map)
    {
        Rom = rom; Map = map;
        try
        {
            int init = L2EventBlocks.InitScript(rom, map);
            Dungeon = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init)).Steps.Any(s => s.Op == 0x69 && s.Bytes.Length >= 2 && (s.Bytes[1] & 1) != 0);
        }
        catch (Exception) { }
        try { _mapSprites = L2MapInfo.CharacterSprites(rom, map); } catch (Exception) { _mapSprites = new(); }
        try { _storySprites = L2Persistent.Start(rom).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Sprite); } catch (Exception) { _storySprites = new(); }
    }

    /// <summary>The sprite actor <paramref name="id"/> is made with on this map, or -1.</summary>
    public int SpriteOf(int id) => id switch
    {
        >= 0 and < 7 => id,
        >= 0x10 and < 0x50 => _storySprites.TryGetValue(id, out int s) ? s : -1,
        >= 0x50 => _mapSprites.TryGetValue(id, out int m) ? m : -1,
        _ => -1,
    };

    /// <summary>
    /// The one name for actor <paramref name="id"/>: "Selan (party)", "Selan (story character 19, sprite 01)", "Lemmy
    /// (character 14, sprite 3D)", "character 5 (sprite 10)". <paramref name="drawnAs"/>: the sprite a 34 shows it
    /// with at that point (-1: its own). <paramref name="sprite"/> false leaves the sprite out (captions).
    /// </summary>
    public string Label(int id, int drawnAs = -1, bool sprite = true)
    {
        if (id < 0 || id == 0xFF) return "nobody";
        if (id < 7)
        {
            string hero = L2EventCommands.HeroNames[id];
            if (drawnAs >= 0 && !ShowsHero(id, drawnAs))
                return $"{SpriteName(drawnAs) ?? "someone"} ({hero}'s actor drawn as sprite {L2Sprites.Name(drawnAs)})";
            return $"{hero} (party)";
        }
        int spr = SpriteOf(id);
        string sp = sprite && spr >= 0 ? $", sprite {L2Sprites.Name(spr)}" : "";
        string kind = id < 0x50 ? $"story character {id:X2}" : $"character {id - 0x4F}";
        string? n = id < 0x50 ? StoryName(id) ?? (spr >= 0 ? SpriteName(spr) : null) : spr >= 0 ? SpriteName(spr) : null;
        if (n == null) return kind + (sp.Length > 0 ? $" ({sp[2..]})" : "");
        // "Man (villager, blond)" + "character 1": one pair of brackets
        return n.EndsWith(')') ? $"{n[..^1]}; {kind}{sp})" : $"{n} ({kind}{sp})";
    }

    /// <summary>
    /// Who speaks each text step of <paramref name="script"/> (index: label): the game's current speaker followed down
    /// every jump. <paramref name="talkedTo"/>: the character a talk event belongs to (-1 for other events). A step
    /// reached with different speakers lists them all.
    /// </summary>
    public Dictionary<int, string> ForScript(L2EventScript script, int talkedTo)
    {
        var steps = script.Steps;
        var index = new Dictionary<L2Step, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < steps.Count; i++) index[steps[i]] = i;
        int start = talkedTo >= 0 ? talkedTo : 0xFF;
        var seen = new HashSet<(int, int, int, int, int)>();
        var said = new Dictionary<int, List<string>>();
        // fixed: a 48 placed the next box; linedUp: 3D lined the party up (a dungeon shows only the leader before it)
        var queue = new Queue<(int Step, int Spk, int Save, int Pose, int PoseOf, bool Fixed, bool LinedUp)>();
        queue.Enqueue((0, start, start, -1, -1, false, !Dungeon));
        while (queue.Count > 0 && seen.Count < 20000)
        {
            var (i, spk, save, pose, poseOf, fixedBox, linedUp) = queue.Dequeue();
            if (i < 0 || i >= steps.Count || !seen.Add((i, spk, save, pose << 8 | poseOf & 0xFF, (fixedBox ? 1 : 0) | (linedUp ? 2 : 0)))) continue;
            var st = steps[i];
            var b = st.Bytes;
            if (st.IsText)
            {
                if (st.Op == 0x13 || st.Op is >= 0x61 and <= 0x67) spk = L2Cutscene.SpeakerActor(b);
                string who = spk == 0xFF ? "" : Label(spk, spk == poseOf ? pose : -1);
                if (spk is >= 1 and < 7 && !linedUp && st.Op != 0x9E) who = OverLeader(who);
                string label = st.Op switch
                {
                    0x9E => "Narration",
                    0x08 when fixedBox => FixedBox,
                    0x08 when spk == 0xFF => "No speaker set (the box goes over the last character the game found)",
                    0x08 => who + (spk == talkedTo ? ", the one talked to" : ", still speaking"),
                    0x10 when spk == 0xFF => "A question (no speaker set)",
                    0x10 => "A question from " + who,
                    _ when fixedBox => who + " (off-screen: the box sits where the step before puts it)",
                    _ => who,
                };
                if (!said.TryGetValue(i, out var l)) said[i] = l = new();
                if (!l.Contains(label)) l.Add(label);
                if (st.End == 0x01 && save != 0xFF) spk = save;
                fixedBox = false;
            }
            else if (st.Op == 0x34 && b.Length >= 3) { pose = L2Sprites.ToEditor(Rom, Map, b[2]); poseOf = b[1]; }
            else if (st.Op is 0x68 or 0x7B && b.Length >= 3 && b[1] == poseOf) { pose = -1; poseOf = -1; }
            else if (st.Op == 0x48) fixedBox = true;
            else if (st.Op == 0x3D) linedUp = true;
            foreach (var j in st.Jumps)
                if (j.Step != null && index.TryGetValue(j.Step, out int to)) queue.Enqueue((to, spk, save, pose, poseOf, fixedBox, linedUp));
            if (!st.EndsFlow) queue.Enqueue((i + 1, spk, save, pose, poseOf, fixedBox, linedUp));
        }
        return said.ToDictionary(kv => kv.Key, kv => string.Join(" or ", kv.Value));
    }

    /// <summary>Who speaks every text of a map's events, by the file offset of the text (after its opener).</summary>
    public static Dictionary<int, string> ForMap(RomBuffer rom, int map)
    {
        var result = new Dictionary<int, List<string>>();
        int block = L2Events.Block(rom, map);
        if (block < 0) return new();
        var speakers = new L2Speakers(rom, map);
        foreach (var e in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript))
        {
            L2EventScript s;
            try { s = L2EventScript.Read(rom, block + e.Offset, block); } catch (Exception) { continue; }
            if (s.Problem != null) continue;
            foreach (var (i, label) in speakers.ForScript(s, e.Head == L2EventBlocks.TalkList ? e.Id : -1))
            {
                var st = s.Steps[i];
                if (st.At < 0) continue;
                int at = st.At + st.Bytes.Length;
                if (!result.TryGetValue(at, out var l)) result[at] = l = new();
                if (!l.Contains(label)) l.Add(label);
            }
        }
        return result.ToDictionary(kv => kv.Key, kv => string.Join(" or ", kv.Value));
    }
}
