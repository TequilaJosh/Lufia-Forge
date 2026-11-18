using System.Text.Json;

namespace LufiaForge.Core.Lufia2;

// who speaks a lufia 2 line, named the same way on the Text page, the event editor's cards and lists and in playback.
// how the game picks the speaker ($80:A06E -> $80:BC33, read from its code and checked on all 5,452 lines of the US game):
// - 13 xx and 61-67 store an actor in $09AB (so 13 01 is exactly 62, the party's Selan)
// - 08 and 10 leave it alone, so they speak as the current speaker
// - a talk starts with $09AB = $09AC = the one talked to ($83:BB19), other events with FF ($83:B740)
// - a text ending 01 puts $09AC back in $09AB if it isn't FF ($80:9DB3)
// the box goes over the first shown actor with that id ($80:C33F -> $80:BF6F). with none, over the last one the game
// found, so a line given to a hero who isn't in the party lands on someone else (Selan at home in Parcelyte is story
// character 19, not the party's Selan). 34 actor sprite shows an actor with another sprite (the opening's "Erim:"
// lines are Maxim's actor drawn as Erim).
// names are in Data/Lufia2_Speakers.json (from the game's text: self-introductions, "Name:" lines, people addressed
// by name, and the game's generic sprites by role)
public sealed class L2Speakers
{
    private sealed record NameData(Dictionary<string, string> sprites, Dictionary<string, string> story, Dictionary<string, List<string>> heroPoses);

    private static readonly Lazy<NameData> Names = new(() =>
    {
        using var stream = typeof(L2Speakers).Assembly.GetManifestResourceStream("Data.Lufia2_Speakers.json")
            ?? throw new InvalidOperationException("The Lufia II speaker names are missing from this build.");
        return JsonSerializer.Deserialize<NameData>(stream) ?? throw new InvalidOperationException("The Lufia II speaker names can't be read.");
    });

    private static int Hex(string text) => Convert.ToInt32(text, 16);

    // who a character drawn with that sprite is (a role for the generic sprites), or null
    public static string? SpriteName(int sprite) => Names.Value.sprites.TryGetValue(sprite.ToString("X2"), out var name) ? name : null;

    // the name of story character id (10-4F): the one given on the Story characters tab, otherwise the game's own, or null
    public static string? StoryName(int id) => L2FlagNotes.Name(L2FlagIndex.Kind.Character, id) ?? (Names.Value.story.TryGetValue(id.ToString("X2"), out var name) ? name : null);

    // true if the sprite still shows that hero (its own or one of its poses)
    public static bool ShowsHero(int hero, int sprite) =>
        sprite == hero || Names.Value.heroPoses.TryGetValue(hero.ToString("X2"), out var poses) && poses.Any(s => Hex(s) == sprite);

    private readonly Dictionary<int, int> _mapSprites;
    private readonly Dictionary<int, int> _storySprites;

    public RomBuffer Rom { get; }
    public int Map { get; }
    // the map has dungeon controls (its start-up script's 69 with bit 01, $09A7): only the leader is made until 3D
    // lines up the party, so a hero's line before that goes over the leader ($84:8204, checked in BizHawk)
    public bool Dungeon { get; }

    // text after a 48: the box sits where 48 puts it ($125D), over nobody (checked in BizHawk: Shrine of Daos E7 scene
    // 62's "[Voice 1]Yeah, I'm fine." and the others)
    public const string FixedBox = "Off-screen voice (the box sits where the step before puts it)";

    // a hero's line in a dungeon before the party lines up
    public static string OverLeader(string label) => label + ": only the leader is shown here until the party lines up (3D), so the box goes over the leader";

    public L2Speakers(RomBuffer rom, int map)
    {
        Rom = rom; Map = map;
        try
        {
            int initScript = L2EventBlocks.InitScript(rom, map);
            Dungeon = L2EventScript.Read(rom, initScript, L2EventBlocks.InitBase(initScript)).Steps.Any(s => s.Op == 0x69 && s.Bytes.Length >= 2 && (s.Bytes[1] & 1) != 0);
        }
        catch (Exception) { }
        try { _mapSprites = L2MapInfo.CharacterSprites(rom, map); } catch (Exception) { _mapSprites = new(); }
        try { _storySprites = L2Persistent.Start(rom).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Sprite); } catch (Exception) { _storySprites = new(); }
    }

    // the sprite actor id is made with on this map, or -1
    public int SpriteOf(int id) => id switch
    {
        >= 0 and < 7 => id,
        >= 0x10 and < 0x50 => _storySprites.TryGetValue(id, out int storySprite) ? storySprite : -1,
        >= 0x50 => _mapSprites.TryGetValue(id, out int mapSprite) ? mapSprite : -1,
        _ => -1,
    };

    // the one name for actor id: "Selan (party)", "Selan (story character 19, sprite 01)", "Lemmy (character 14,
    // sprite 3D)", "character 5 (sprite 10)". drawnAs = the sprite a 34 shows it with at that point (-1 = its own).
    // sprite false leaves the sprite out (captions)
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
        int spriteId = SpriteOf(id);
        string spritePart = sprite && spriteId >= 0 ? $", sprite {L2Sprites.Name(spriteId)}" : "";
        string kind = id < 0x50 ? $"story character {id:X2}" : $"character {id - 0x4F}";
        string? name = id < 0x50 ? StoryName(id) ?? (spriteId >= 0 ? SpriteName(spriteId) : null) : spriteId >= 0 ? SpriteName(spriteId) : null;
        if (name == null) return kind + (spritePart.Length > 0 ? $" ({spritePart[2..]})" : "");
        // "Man (villager, blond)" + "character 1": just one pair of brackets
        return name.EndsWith(')') ? $"{name[..^1]}; {kind}{spritePart})" : $"{name} ({kind}{spritePart})";
    }

    // who speaks each text step of script (index: label), the game's current speaker followed down every jump.
    // talkedTo = the character a talk event belongs to (-1 for other events). a step reached with different speakers
    // lists them all
    public Dictionary<int, string> ForScript(L2EventScript script, int talkedTo)
    {
        var steps = script.Steps;
        var index = new Dictionary<L2Step, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < steps.Count; i++) index[steps[i]] = i;
        int start = talkedTo >= 0 ? talkedTo : 0xFF;
        var seen = new HashSet<(int, int, int, int, int)>();
        var said = new Dictionary<int, List<string>>();
        // fixed = a 48 placed the next box, linedUp = 3D lined the party up (a dungeon shows only the leader before it)
        var queue = new Queue<(int Step, int Spk, int Save, int Pose, int PoseOf, bool Fixed, bool LinedUp)>();
        queue.Enqueue((0, start, start, -1, -1, false, !Dungeon));
        while (queue.Count > 0 && seen.Count < 20000)
        {
            var (i, speaker, save, pose, poseOf, fixedBox, linedUp) = queue.Dequeue();
            if (i < 0 || i >= steps.Count || !seen.Add((i, speaker, save, pose << 8 | poseOf & 0xFF, (fixedBox ? 1 : 0) | (linedUp ? 2 : 0)))) continue;
            var step = steps[i];
            var bytes = step.Bytes;
            if (step.IsText)
            {
                if (step.Op == 0x13 || step.Op is >= 0x61 and <= 0x67) speaker = L2Cutscene.SpeakerActor(bytes);
                string who = speaker == 0xFF ? "" : Label(speaker, speaker == poseOf ? pose : -1);
                if (speaker is >= 1 and < 7 && !linedUp && step.Op != 0x9E) who = OverLeader(who);
                string label = step.Op switch
                {
                    0x9E => "Narration",
                    0x08 when fixedBox => FixedBox,
                    0x08 when speaker == 0xFF => "No speaker set (the box goes over the last character the game found)",
                    0x08 => who + (speaker == talkedTo ? ", the one talked to" : ", still speaking"),
                    0x10 when speaker == 0xFF => "A question (no speaker set)",
                    0x10 => "A question from " + who,
                    _ when fixedBox => who + " (off-screen: the box sits where the step before puts it)",
                    _ => who,
                };
                if (!said.TryGetValue(i, out var labels)) said[i] = labels = new();
                if (!labels.Contains(label)) labels.Add(label);
                if (step.End == 0x01 && save != 0xFF) speaker = save;
                fixedBox = false;
            }
            else if (step.Op == 0x34 && bytes.Length >= 3) { pose = L2Sprites.ToEditor(Rom, Map, bytes[2]); poseOf = bytes[1]; }
            else if (step.Op is 0x68 or 0x7B && bytes.Length >= 3 && bytes[1] == poseOf) { pose = -1; poseOf = -1; }
            else if (step.Op == 0x48) fixedBox = true;
            else if (step.Op == 0x3D) linedUp = true;
            foreach (var jump in step.Jumps)
                if (jump.Step != null && index.TryGetValue(jump.Step, out int to)) queue.Enqueue((to, speaker, save, pose, poseOf, fixedBox, linedUp));
            if (!step.EndsFlow) queue.Enqueue((i + 1, speaker, save, pose, poseOf, fixedBox, linedUp));
        }
        return said.ToDictionary(kv => kv.Key, kv => string.Join(" or ", kv.Value));
    }

    // who speaks every text of a map's events, by the file offset of the text (after its opener)
    public static Dictionary<int, string> ForMap(RomBuffer rom, int map)
    {
        var result = new Dictionary<int, List<string>>();
        int block = L2Events.Block(rom, map);
        if (block < 0) return new();
        var speakers = new L2Speakers(rom, map);
        foreach (var e in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript))
        {
            L2EventScript script;
            try { script = L2EventScript.Read(rom, block + e.Offset, block); } catch (Exception) { continue; }
            if (script.Problem != null) continue;
            foreach (var (i, label) in speakers.ForScript(script, e.Head == L2EventBlocks.TalkList ? e.Id : -1))
            {
                var step = script.Steps[i];
                if (step.At < 0) continue;
                int at = step.At + step.Bytes.Length;
                if (!result.TryGetValue(at, out var labels)) result[at] = labels = new();
                if (!labels.Contains(label)) labels.Add(label);
            }
        }
        return result.ToDictionary(kv => kv.Key, kv => string.Join(" or ", kv.Value));
    }
}
