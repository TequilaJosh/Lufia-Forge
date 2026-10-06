using System.IO;
using System.Text.Json;

namespace LufiaForge.Core.Maps;

/// <summary>One place a story flag is used.</summary>
/// <param name="RegionStart">Start of the decoded code holding the use: the event start, or code the event jumps to.</param>
public sealed record FlagUse(int Flag, int MapId, int Event, int Line, int Offset, string Action, bool IsSetup, int RegionStart = -1, int JumpBase = -1)
{
    public bool Sets => Action is "sets" or "clears";
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
                var queue = new Queue<EventScript>(); queue.Enqueue(first);
                var regions = new HashSet<int> { first.Start };
                while (queue.Count > 0 && regions.Count < 64)
                {
                    var s = queue.Dequeue();
                    for (int i = 0; i < s.Ops.Count; i++)
                    {
                        var o = s.Ops[i];
                        if (o.IsText || o.Bytes.Length < 2) continue;
                        string? action = o.Bytes[0] switch
                        {
                            0x04 => "checks if set",
                            0x05 => "checks if not set",
                            0x06 => "sets",
                            0x07 => "clears",
                            _ => null,
                        };
                        if (action != null) uses.Add(new FlagUse(o.Bytes[1], map, ev, i, o.Offset, action, false, s.Start, first.Start));
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
        return uses.DistinctBy(u => (u.Offset, u.Flag, u.IsSetup)).ToList();
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
