namespace LufiaForge.Core.Maps;

/// <summary>
/// A short, readable guess at what an event does, from the commands it runs (and the code it jumps to):
/// church, inn, shop, item gifts, spells, party changes, story flags, cutscenes, plain talk.
/// </summary>
public static class EventHints
{
    public static string Summarize(RomBuffer rom, EventScript script)
    {
        // the event plus the code it jumps to outside its own lines
        var all = new List<(EventScript s, ScriptOp o)>();
        var queue = new Queue<EventScript>(); queue.Enqueue(script);
        var seen = new HashSet<int> { script.Start };
        while (queue.Count > 0 && seen.Count < 24)
        {
            var s = queue.Dequeue();
            foreach (var o in s.Ops)
            {
                all.Add((s, o));
                foreach (var t in o.Targets)
                    if (t.Op == null && t.Absolute >= s.BlockBase && t.Absolute < s.BlockBase + 0x10000 && seen.Add(t.Absolute))
                        try { queue.Enqueue(EventScript.LoadAt(rom, script.MapId, script.Event, t.Absolute, script.JumpBase)); } catch { }
            }
        }

        var ops = all.Select(x => x.o).ToList();
        var cmds = ops.Where(o => !o.IsText && o.Bytes.Length > 0).ToList();
        var texts = ops.Where(o => o.IsText && o.Text.Trim().Length > 0).ToList();
        if (ops.Count <= 1 && texts.Count == 0) return "Empty";

        var parts = new List<string>();
        string Flag(int f) => StoryFlags.NameOf(f) is { Length: > 0 } n ? $"{f:X2} “{n}”" : f.ToString("X2");

        if (cmds.Any(o => o.Bytes[0] == 0x1E)) parts.Add("Church (revive / cure / save)");
        if (cmds.FirstOrDefault(o => o.Bytes[0] == 0x1D && o.Bytes.Length >= 3) is { } inn)
        {
            int price = inn.Bytes[1] | (inn.Bytes[2] << 8);
            parts.Add(price == 0 ? "Inn (free)" : $"Inn ({price} GP a night)");
        }
        foreach (var shop in cmds.Where(o => o.Bytes[0] == 0x1C && o.Bytes.Length >= 2).Select(o => o.Bytes[1]).Distinct())
            parts.Add($"Shop {shop:X2}");
        var gifts = cmds.Where(o => o.Bytes[0] == 0x3E && o.Bytes.Length >= 3)
                        .Select(o => MapSetupScript.ItemName(rom, o.Bytes[1]) + (o.Bytes[2] > 1 ? $" x{o.Bytes[2]}" : "")).Distinct().ToList();
        if (gifts.Count > 0) parts.Add("Gives " + string.Join(", ", gifts));
        if (cmds.Any(o => o.Bytes[0] == 0x3D)) parts.Add("Teaches a spell");
        if (cmds.Any(o => o.Bytes[0] == 0x1A)) parts.Add("Someone joins / appears");

        var sets = cmds.Where(o => o.Bytes[0] == 0x06 && o.Bytes.Length >= 2).Select(o => Flag(o.Bytes[1])).Distinct().ToList();
        var clears = cmds.Where(o => o.Bytes[0] == 0x07 && o.Bytes.Length >= 2).Select(o => Flag(o.Bytes[1])).Distinct().ToList();
        if (sets.Count > 0) parts.Add("sets story flag " + string.Join(", ", sets.Take(4)) + (sets.Count > 4 ? "…" : ""));
        if (clears.Count > 0) parts.Add("clears flag " + string.Join(", ", clears.Take(3)) + (clears.Count > 3 ? "…" : ""));

        int checks = cmds.Count(o => o.Bytes[0] is 0x04 or 0x05);
        bool firstTime = cmds.Any(o => o.Bytes[0] == 0x02);
        int moves = cmds.Count(o => o.Bytes[0] is (>= 0x10 and <= 0x2F) or (>= 0xB0) && o.Bytes[0] is not (0x1C or 0x1D or 0x1E or 0x1A));
        int speakers = texts.Select(o => o.Bytes[0]).Distinct().Count();
        bool cutscene = moves >= 4 && (speakers >= 2 || texts.Count >= 6);

        string kind = parts.Count > 0 && parts[0] is var p0 && (p0.StartsWith("Church") || p0.StartsWith("Inn") || p0.StartsWith("Shop")) ? ""
                    : cutscene ? "Cutscene"
                    : texts.Count > 0 ? "Talk" : "Script";
        if (kind.Length > 0) parts.Insert(0, kind + (texts.Count > 0 ? $" ({texts.Count} box{(texts.Count == 1 ? "" : "es")})" : ""));
        if (checks > 0) parts.Add("changes with story progress");
        if (firstTime) parts.Add("first time differs");
        return string.Join(" · ", parts);
    }
}
