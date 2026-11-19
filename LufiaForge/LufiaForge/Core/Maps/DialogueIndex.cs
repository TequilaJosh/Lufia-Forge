namespace LufiaForge.Core.Maps;

// one dialogue box of the game and where it's used
public sealed class DialogueLine
{
    public int MapId { get; init; }
    public int Event { get; init; }
    // start of the decoded code holding the line (the event start, or code the event jumps to)
    public int RegionStart { get; init; }
    // start of the event (jump offsets count from here)
    public int JumpBase { get; init; }
    // index of the line inside the decoded code
    public int Line { get; init; }
    public int Offset { get; init; }
    public string Speaker { get; init; } = "";
    public string Text { get; set; } = "";
    // what starts the event: "Character 3", "Step-on area D1", or empty
    public string Source { get; init; } = "";
    // sprite number of the character whose event this is, or -1
    public int Sprite { get; init; } = -1;
    public bool IsCodeView => RegionStart != JumpBase;
}

// every dialogue box in every map's events (and the code those events jump to)
public static class DialogueIndex
{
    public static List<DialogueLine> Build(RomBuffer rom, IEnumerable<int> mapIds, Action<int, int>? progress = null)
    {
        var lines = new List<DialogueLine>();
        var seen = new HashSet<int>();      // decoded regions (shared scripts show up once)
        var ids = mapIds.ToList();
        for (int mapIndex = 0; mapIndex < ids.Count; mapIndex++)
        {
            int map = ids[mapIndex];
            progress?.Invoke(mapIndex, ids.Count);
            var npcSprites = new Dictionary<int, int>();
            try { foreach (var npc in LufiaMap.Load(rom, map).Npcs.Where(n => !n.IsUnused)) npcSprites[MapEvents.TalkEvent(npc.Index)] = npc.Sprite; } catch { }
            var triggers = new Dictionary<int, string>();
            try { foreach (var (area, ev, _, _) in MapSetupScript.ReadTriggers(rom, map)) triggers.TryAdd(ev, $"Step-on area D{area}"); } catch { }

            foreach (int ev in EventScript.PlausibleEvents(rom, map))
            {
                EventScript? first = null;
                try { first = EventScript.Load(rom, map, ev); } catch { }
                if (first == null || !seen.Add(first.Start)) continue;
                string source = npcSprites.ContainsKey(ev) ? $"Character {ev}" : triggers.GetValueOrDefault(ev, "");
                int sprite = npcSprites.TryGetValue(ev, out int eventSprite) ? eventSprite : -1;

                var queue = new Queue<EventScript>(); queue.Enqueue(first);
                int regions = 0;
                while (queue.Count > 0 && regions++ < 64)
                {
                    var script = queue.Dequeue();
                    for (int i = 0; i < script.Ops.Count; i++)
                    {
                        var scriptOp = script.Ops[i];
                        if (scriptOp.IsText && scriptOp.Text.Trim().Length > 0)
                        {
                            // "Actor N says": actor 7 + k is "character k", the map's record k - 1
                            string speaker = EventScript.SpeakerName(scriptOp.Bytes);
                            int lineSprite = sprite;
                            int op = scriptOp.Bytes[0];
                            int actor = op is >= 0x88 and <= 0x8F ? op - 0x88 : op is >= 0x90 and <= 0xAF ? op - 0x90 + 8 : -1;
                            if (actor >= 8 && npcSprites.TryGetValue(actor - 8, out int speakerSprite)) { speaker = $"Character {actor - 7} says"; lineSprite = speakerSprite; }
                            else if (actor >= 7) { speaker = $"Character {actor - 7} says"; }
                            else if (actor is >= 0 and < 7) { speaker = $"Party actor {actor} says"; lineSprite = -1; }
                            lines.Add(new DialogueLine
                            {
                                MapId = map, Event = ev, RegionStart = script.Start, JumpBase = first.Start, Line = i, Offset = scriptOp.Offset,
                                Speaker = speaker, Text = scriptOp.Text, Source = source, Sprite = lineSprite,
                            });
                        }
                        foreach (var target in scriptOp.Targets)
                            if (target.Op == null && target.Absolute >= script.BlockBase && target.Absolute < script.BlockBase + 0x10000 && seen.Add(target.Absolute))
                                try { queue.Enqueue(EventScript.LoadAt(rom, map, ev, target.Absolute, first.Start)); } catch { }
                    }
                }
            }
        }
        return lines;
    }
}
