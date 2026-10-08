using System.IO;
using System.Text.Json;

namespace LufiaForge.Core.Maps;

/// <summary>One place a story flag is used.</summary>
/// <param name="RegionStart">Start of the decoded code holding the use: the event start, or code the event jumps to.</param>
public sealed record FlagUse(int Flag, int MapId, int Event, int Line, int Offset, string Action, bool IsSetup, int RegionStart = -1, int JumpBase = -1)
{
    public bool Sets => Action is "sets" or "clears" or "sets as a result";
    /// <summary>Dialogue next to the use (what is said when the flag is set, or what the check leads to).</summary>
    public string Context { get; init; } = "";
    /// <summary>What the event the use is in seems to do (EventHints).</summary>
    public string EventHint { get; init; } = "";
}

/// <summary>
/// Story flags: the game's global bit flags ($1296 + flag/8, bit flag&amp;7). Event commands 04/05 test them,
/// 06/07 set/clear them, and map setup scripts test them (05-0A) to choose what a map looks like.
/// This builds a cross-reference over every map's events and setup script, and keeps the user's names for
/// flags in %AppData%\LufiaForge\flagnames.json.
/// </summary>
public static class StoryFlags
{
    /// <summary>Every use of every flag. Decodes each distinct event script once.</summary>
    public static List<FlagUse> BuildIndex(RomBuffer rom, IEnumerable<int> mapIds)
    {
        var uses = new List<FlagUse>();
        var seenStarts = new HashSet<int>();
        foreach (int map in mapIds)
        {
            foreach (int ev in EventScript.PlausibleEvents(rom, map))
            {
                EventScript? first = null;
                try { first = EventScript.Load(rom, map, ev); } catch { }
                if (first == null || first.Ops.Count <= 1) continue;
                if (!seenStarts.Add(first.Start)) continue;   // same script listed under several events/maps

                // the event, plus code it jumps to outside its own lines (same block, same jump base)
                string? hint = null;
                var queue = new Queue<EventScript>(); queue.Enqueue(first);
                var regions = new HashSet<int> { first.Start };
                while (queue.Count > 0 && regions.Count < 64)
                {
                    var s = queue.Dequeue();
                    for (int i = 0; i < s.Ops.Count; i++)
                    {
                        var o = s.Ops[i];
                        if (o.IsText || o.Bytes.Length == 0) continue;
                        int b0 = o.Bytes[0];
                        (int flag, string? action) = b0 switch
                        {
                            0x04 => (o.Bytes[1], "checks if set"),
                            0x05 => (o.Bytes[1], "checks if not set"),
                            0x06 => (o.Bytes[1], "sets"),
                            0x07 => (o.Bytes[1], "clears"),
                            >= 0xC0 and <= 0xCF => (0xF0 + (b0 & 15), "checks if set"),
                            >= 0xD0 and <= 0xDF => (0xF0 + (b0 & 15), "checks if not set"),
                            >= 0xE0 and <= 0xEF => (0xF0 + (b0 & 15), "sets"),
                            >= 0xF0 => (0xF0 + (b0 & 15), "clears"),
                            // commands that report their result in flag FF
                            0x1C or 0x3E or 0x41 or 0x4A => (0xFF, "sets as a result"),
                            0x50 when o.Bytes.Length > 1 && o.Bytes[1] is 0x01 or 0x02 or 0x03 or 0x0F or 0x10 or 0x11 or 0x17 or 0x1D or 0x1E => (0xFF, "sets as a result"),
                            _ => (-1, null),
                        };
                        if (action != null)
                            uses.Add(new FlagUse(flag, map, ev, i, o.Offset, action, false, s.Start, first.Start)
                            {
                                Context = Context(s, i, action), EventHint = hint ??= Hint(rom, first),
                            });
                        foreach (var t in o.Targets)
                            if (t.Op == null && t.Absolute >= s.BlockBase && t.Absolute < s.BlockBase + 0x10000 && regions.Add(t.Absolute))
                                try { queue.Enqueue(EventScript.LoadAt(rom, map, ev, t.Absolute, first.Start)); } catch { }
                    }
                }
            }

            // map setup script: 05-0A test flags to decide what the map shows
            try
            {
                int p = MapSetupScript.Offset(rom, map) + 2;
                for (int steps = 0; steps < 1000 && p < rom.Length - 4; steps++)
                {
                    int op = rom.ReadByte(p);
                    if (op == 0x00) break;
                    if (op < 0x20)
                    {
                        if (op is >= 0x05 and <= 0x0A)
                            uses.Add(new FlagUse(rom.ReadByte(p + 1), map, -1, -1, p, "checked by the map setup", true));
                        if (op > 0x0D) break;
                        p += op is 0x04 or 0x0B ? 3 : 2;
                    }
                    else if (op < 0x40) p += 4;
                    else if (op < 0x80) p += 1;
                    else if (op < 0xC0) p += 2;
                    else p += 4;
                }
            }
            catch { /* no setup script */ }
        }
        var list = uses.DistinctBy(u => (u.Offset, u.Flag, u.IsSetup)).ToList();
        _autoNames = list.GroupBy(u => u.Flag).ToDictionary(g => g.Key, g => AutoName(g.Key, g.ToList()));
        return list;
    }

    private static string Hint(RomBuffer rom, EventScript s)
    {
        try { return EventHints.Summarize(rom, s); } catch { return ""; }
    }

    private static string Short(string text, int max = 70)
    {
        var t = text.Replace("\n", " ").Replace("  ", " ").Trim();
        return t.Length > max ? t[..max].TrimEnd() + "…" : t;
    }

    /// <summary>Dialogue near a use: for a set/clear the closest box before it (what just happened), else after;
    /// for a check the first box on the path it chooses (the jump target), else the next box.</summary>
    private static string Context(EventScript s, int i, string action)
    {
        var ops = s.Ops;
        string? Text(int k) => k >= 0 && k < ops.Count && ops[k].IsText && ops[k].Text.Trim().Length > 2 ? Short(ops[k].Text) : null;
        if (action.StartsWith("checks"))
        {
            var t = ops[i].Targets.FirstOrDefault()?.Op;
            int j = t != null ? ops.IndexOf(t) : -1;
            for (int k = j; k >= 0 && k < Math.Min(ops.Count, j + 4); k++) if (Text(k) is { } a) return a;
            for (int k = i + 1; k < Math.Min(ops.Count, i + 4); k++) if (Text(k) is { } b) return b;
            return "";
        }
        for (int k = i - 1; k >= Math.Max(0, i - 14); k--) if (Text(k) is { } a) return a;
        for (int k = i + 1; k < Math.Min(ops.Count, i + 8); k++) if (Text(k) is { } b) return b;
        return "";
    }

    // ── descriptions ───────────────────────────────────────────────────────

    private static Dictionary<int, string> _autoNames = new();

    /// <summary>The user's name for a flag, or a short automatic one worked out from where it is set.</summary>
    public static string? DisplayName(int flag) => NameOf(flag) ?? (_autoNames.TryGetValue(flag, out var a) && a.Length > 0 ? a : null);

    private static string AutoName(int flag, List<FlagUse> uses)
    {
        if (flag == 0xFF) return "result of the last shop / item / gold / menu command";
        if (flag >= 0xF0) return "scratch flag (reused inside cutscenes)";
        var set = uses.FirstOrDefault(u => u.Action == "sets" && u.Context.Length > 0) ?? uses.FirstOrDefault(u => u.Action == "sets");
        if (set == null) return "";
        if (set.Context.Length > 0) return $"set after \"{Short(set.Context, 40)}\"";
        return set.EventHint.Length > 0 && !set.EventHint.StartsWith("Script")
            ? $"set in map {set.MapId:X2} event {set.Event} ({Short(BestHint(set.EventHint), 30)})"
            : $"set in map {set.MapId:X2} event {set.Event}";
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    /// <summary>The most telling part of an event hint ("Someone joins" rather than "Cutscene (51 boxes)").</summary>
    private static string BestHint(string hint)
    {
        var parts = hint.Split('·').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts.FirstOrDefault(p => !p.StartsWith("Cutscene") && !p.StartsWith("Talk") && !p.StartsWith("Script") &&
                                         !p.StartsWith("sets story flag") && !p.StartsWith("clears flag") && !p.StartsWith("changes with"))
               ?? parts.FirstOrDefault() ?? "";
    }

    /// <summary>A plain-language description of what a flag is and does, from every place it is used.</summary>
    public static string Describe(int flag, IReadOnlyList<FlagUse> uses, Func<int, string> mapLabel)
    {
        var sb = new System.Text.StringBuilder();
        string Where(FlagUse u) => u.IsSetup ? $"{mapLabel(u.MapId)} (map setup)" : $"{mapLabel(u.MapId)}, event {u.Event}";
        string Said(FlagUse u) => u.Context.Length > 0 ? $" — near \"{u.Context}\"" : "";
        string What(FlagUse u) => u.EventHint.Length > 0 && u.EventHint != "Script" ? $" [{Short(u.EventHint, 60)}]" : "";

        if (flag == 0xFF)
            sb.AppendLine("Engine result flag. Commands clear it and set it to report how they went: shop (something was bought or sold), " +
                          "give item (it did not fit), take gold (not enough money), save menu, church/curse services. " +
                          "Events check it right after such a command. It is not a story milestone.");
        else if (flag >= 0xF0)
            sb.AppendLine($"Flag {flag:X2} has short commands of its own (C{flag & 15:X}/D{flag & 15:X} check it, E{flag & 15:X}/F{flag & 15:X} set/clear it); events use F0-FF as scratch switches inside cutscenes.");

        var sets = uses.Where(u => u.Action is "sets" or "sets as a result").DistinctBy(u => (u.MapId, u.Event, u.IsSetup)).ToList();
        var clears = uses.Where(u => u.Action == "clears").DistinctBy(u => (u.MapId, u.Event, u.IsSetup)).ToList();
        var checks = uses.Where(u => !u.Sets).ToList();

        if (flag != 0xFF)
        {
            if (sets.Count == 0) sb.AppendLine("No event sets it: the game's own code may set it (battles, the world map, a menu), or it's unused.");
            else
            {
                sb.AppendLine($"Turned on by {sets.Count} place{(sets.Count == 1 ? "" : "s")}:");
                foreach (var u in sets.Take(4)) sb.AppendLine($"  • {Where(u)}{What(u)}{Said(u)}");
                if (sets.Count > 4) sb.AppendLine($"  • … and {sets.Count - 4} more");
            }
            if (clears.Count == 0) sb.AppendLine(sets.Count > 0 ? "Never turned off: once set it stays set (a story milestone)." : "");
            else
            {
                sb.AppendLine($"Turned off by {clears.Count} place{(clears.Count == 1 ? "" : "s")} (it switches back and forth):");
                foreach (var u in clears.Take(3)) sb.AppendLine($"  • {Where(u)}{What(u)}{Said(u)}");
            }
        }

        var setupMaps = checks.Where(u => u.IsSetup).Select(u => u.MapId).Distinct().ToList();
        var eventChecks = checks.Where(u => !u.IsSetup).ToList();
        if (setupMaps.Count > 0)
            sb.AppendLine($"Changes how {(setupMaps.Count == 1 ? "this map looks" : $"{setupMaps.Count} maps look")} (who is there, doors, treasure): " +
                          string.Join(", ", setupMaps.Take(6).Select(mapLabel)) + (setupMaps.Count > 6 ? ", …" : "") + ".");
        if (eventChecks.Count > 0)
        {
            var maps = eventChecks.Select(u => u.MapId).Distinct().ToList();
            sb.AppendLine($"Checked by {Plural(eventChecks.Select(u => (u.MapId, u.Event)).Distinct().Count(), "event")} on {Plural(maps.Count, "map")} " +
                          $"({string.Join(", ", maps.Take(5).Select(mapLabel))}{(maps.Count > 5 ? ", …" : "")}), which then go a different way, e.g.:");
            foreach (var u in eventChecks.Where(u => u.Context.Length > 0).DistinctBy(u => (u.MapId, u.Event)).Take(3))
                sb.AppendLine($"  • {Where(u)}, {(u.Action == "checks if set" ? "when set" : "when not set")}: \"{u.Context}\"");
        }
        if (checks.Count == 0 && flag != 0xFF) sb.AppendLine("Nothing checks it in the events or map setups (the game's own code may).");
        return sb.ToString().TrimEnd();
    }

    // ── names ───────────────────────────────────────────────────────────────

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "flagnames.json");

    private static Dictionary<int, string>? _names;

    public static Dictionary<int, string> Names
    {
        get
        {
            if (_names != null) return _names;
            _names = new();
            try
            {
                if (File.Exists(StorePath))
                    foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? new())
                        if (int.TryParse(k, System.Globalization.NumberStyles.HexNumber, null, out int f)) _names[f] = v;
            }
            catch { }
            return _names;
        }
    }

    public static string? NameOf(int flag) => Names.TryGetValue(flag, out var n) ? n : null;

    public static void SetName(int flag, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) Names.Remove(flag);
        else Names[flag] = name.Trim();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Names.ToDictionary(kv => kv.Key.ToString("X2"), kv => kv.Value),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
