namespace LufiaForge.Core.Maps;

/// <summary>One dialogue box of the game and where it is used.</summary>
public sealed class DialogueLine
{
    public int MapId { get; init; }
    public int Event { get; init; }
    /// <summary>Start of the decoded code holding the line (event start, or code the event jumps to).</summary>
    public int RegionStart { get; init; }
    /// <summary>Start of the event (jump offsets are counted from here).</summary>
    public int JumpBase { get; init; }
    /// <summary>Index of the line inside the decoded code.</summary>
    public int Line { get; init; }
    public int Offset { get; init; }
    public string Speaker { get; init; } = "";
    public string Text { get; set; } = "";
    /// <summary>What starts the event: "Character 3", "Step-on area D1", or empty.</summary>
    public string Source { get; init; } = "";
    /// <summary>Sprite number of the character whose event this is, or -1.</summary>
    public int Sprite { get; init; } = -1;
    public bool IsCodeView => RegionStart != JumpBase;
}

/// <summary>Every dialogue box in every map's events (and the code those events jump to).</summary>
public static class DialogueIndex
{
    public static List<DialogueLine> Build(RomBuffer rom, IEnumerable<int> mapIds, Action<int, int>? progress = null)
    {
        var lines = new List<DialogueLine>();
        var seen = new HashSet<int>();      // decoded regions (shared scripts appear once)
        var ids = mapIds.ToList();
        for (int mi = 0; mi < ids.Count; mi++)
        {
            int map = ids[mi];
            progress?.Invoke(mi, ids.Count);
            var npcs = new Dictionary<int, int>();
            try { foreach (var n in LufiaMap.Load(rom, map).Npcs.Where(n => !n.IsUnused)) npcs[n.Index] = n.Sprite; } catch { }
            var triggers = new Dictionary<int, string>();
            try { foreach (var (area, ev, _, _) in MapSetupScript.ReadTriggers(rom, map)) triggers.TryAdd(ev, $"Step-on area D{area}"); } catch { }

            foreach (int ev in EventScript.PlausibleEvents(rom, map))
            {
                EventScript? first = null;
                try { first = EventScript.Load(rom, map, ev); } catch { }
                if (first == null || !seen.Add(first.Start)) continue;
                string source = npcs.ContainsKey(ev) ? $"Character {ev}" : triggers.GetValueOrDefault(ev, "");
                int sprite = npcs.TryGetValue(ev, out int sp) ? sp : -1;

                var queue = new Queue<EventScript>(); queue.Enqueue(first);
                int regions = 0;
                while (queue.Count > 0 && regions++ < 64)
                {
                    var s = queue.Dequeue();
                    for (int i = 0; i < s.Ops.Count; i++)
                    {
                        var o = s.Ops[i];
                        if (o.IsText && o.Text.Trim().Length > 0)
                        {
                            // "Actor N says": actor slots 7+ are the map's characters (slot n+7 = character n)
                            string speaker = EventScript.SpeakerName(o.Bytes);
                            int lineSprite = sprite;
                            int op = o.Bytes[0];
                            int actor = op is >= 0x88 and <= 0x8F ? op - 0x88 : op is >= 0x90 and <= 0xAF ? op - 0x90 + 8 : -1;
                            if (actor >= 7 && npcs.TryGetValue(actor - 7, out int ns)) { speaker = $"Character {actor - 7} says"; lineSprite = ns; }
                            else if (actor is >= 0 and < 7) { speaker = $"Party actor {actor} says"; lineSprite = -1; }
                            lines.Add(new DialogueLine
                            {
                                MapId = map, Event = ev, RegionStart = s.Start, JumpBase = first.Start, Line = i, Offset = o.Offset,
                                Speaker = speaker, Text = o.Text, Source = source, Sprite = lineSprite,
                            });
                        }
                        foreach (var t in o.Targets)
                            if (t.Op == null && t.Absolute >= s.BlockBase && t.Absolute < s.BlockBase + 0x10000 && seen.Add(t.Absolute))
                                try { queue.Enqueue(EventScript.LoadAt(rom, map, ev, t.Absolute, first.Start)); } catch { }
                    }
                }
            }
        }
        return lines;
    }
}
