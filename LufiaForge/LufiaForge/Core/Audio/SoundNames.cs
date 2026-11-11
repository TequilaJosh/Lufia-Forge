using System.IO;
using System.Text.Json;

namespace LufiaForge.Core.Audio;

/// <summary>
/// Names for the games' sound effects, which the games themselves don't name. The defaults
/// (<c>SoundNames.Defaults.cs</c>) come from where the game plays each sound: its code (menus, battles, shops) and its own
/// events (a sound right before a character vanishes, with every warp...); a sound the game is never seen to use is
/// named by how it sounds. Names the user gives (Settings) are kept in %AppData%\LufiaForge\soundnames.json for every
/// project of that game.
/// </summary>
public static partial class SoundNames
{
    /// <summary>The sounds there are: Lufia II's driver takes 00-8E; Lufia 1's has 00-38 (from 80 the numbers repeat 00-38).</summary>
    public static int Count(bool lufia2) => lufia2 ? Lufia2Defaults.Length : Lufia1Defaults.Length;

    /// <summary>A name was changed in Settings (lists of sounds read them again).</summary>
    public static event Action? Changed;

    /// <summary>Goes up with every rename (a list made before knows it is out of date).</summary>
    public static int Version { get; private set; }

    public static string Name(bool lufia2, int id) =>
        Own(lufia2).TryGetValue(id, out var n) ? n : Default(lufia2, id);

    public static string Default(bool lufia2, int id)
    {
        var t = lufia2 ? Lufia2Defaults : Lufia1Defaults;
        if (id >= 0 && id < t.Length) return t[id].Name;
        if (!lufia2 && id >= 0x80 && id - 0x80 < t.Length) return t[id - 0x80].Name;   // the driver reads only 7 bits
        return "(no sound)";
    }

    /// <summary>Where the default name comes from.</summary>
    public static string Why(bool lufia2, int id)
    {
        var t = lufia2 ? Lufia2Defaults : Lufia1Defaults;
        if (id >= 0 && id < t.Length) return t[id].Why;
        if (!lufia2 && id >= 0x80 && id - 0x80 < t.Length) return $"The same sound as {id - 0x80:X2}.";
        return "The game has no sound with this number.";
    }

    public static bool IsRenamed(bool lufia2, int id) => Own(lufia2).ContainsKey(id);

    /// <summary>"XX name", for lists.</summary>
    public static string Label(bool lufia2, int id) => $"{id:X2} {Name(lufia2, id)}";

    /// <summary>Gives a sound a name of the user's own; empty (or the default) goes back to the default name.</summary>
    public static void Rename(bool lufia2, int id, string? name)
    {
        var own = Own(lufia2);
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name == Default(lufia2, id)) own.Remove(id);
        else own[id] = name;
        Save();
        Version++;
        Changed?.Invoke();
    }

    // ── the user's names ──────────────────────────────────────────────────────

    /// <summary>The file (tests point it elsewhere).</summary>
    public static string StorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "soundnames.json");

    private static Dictionary<int, string>? _l1, _l2;

    private static Dictionary<int, string> Own(bool lufia2)
    {
        if (_l1 == null || _l2 == null) Load();
        return lufia2 ? _l2! : _l1!;
    }

    private static void Load()
    {
        _l1 = new(); _l2 = new();
        try
        {
            if (!File.Exists(StorePath)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(StorePath)) ?? new();
            foreach (var (game, names) in all)
            {
                var into = game == "lufia2" ? _l2 : game == "lufia1" ? _l1 : null;
                if (into == null) continue;
                foreach (var (k, v) in names)
                    if (int.TryParse(k, System.Globalization.NumberStyles.HexNumber, null, out int id) && !string.IsNullOrWhiteSpace(v)) into[id] = v;
            }
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            static Dictionary<string, string> Hex(Dictionary<int, string> d) => d.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString("X2"), kv => kv.Value);
            var all = new Dictionary<string, Dictionary<string, string>> { ["lufia1"] = Hex(_l1!), ["lufia2"] = Hex(_l2!) };
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
