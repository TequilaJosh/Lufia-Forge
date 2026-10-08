using System.IO;
using System.Text.Json;

namespace LufiaForge.Core.Maps;

/// <summary>A flag watched in RAM: a story flag ($1296 + id/8) or one the user added (any address and bit).</summary>
public sealed class EventFlag
{
    /// <summary>Story flag number 00-FF, or -1 for a user flag.</summary>
    public int Id { get; set; } = -1;
    public int RamAddress { get; set; }
    public int BitIndex { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public bool CurrentValue { get; set; }
}

/// <summary>
/// The flags the live tracker watches: the game's 256 story flags (WRAM $1296-$12B5) plus flags the user adds,
/// which are kept per ROM in &lt;rom&gt;.lfflags.json. Story flag names stay in the shared flag-name store.
/// </summary>
public sealed class EventFlagStore
{
    public const int StoryFlagBase = 0x1296;
    /// <summary>WRAM table of bit masks the engine uses for flag bits (filled at boot).</summary>
    public const int BitMaskTable = 0x032B;

    private readonly List<EventFlag> _user = new();
    private string? _path;

    public void Open(string? romPath)
    {
        _user.Clear();
        _path = string.IsNullOrEmpty(romPath) ? null : Path.ChangeExtension(romPath, ".lfflags.json");
        try
        {
            if (_path != null && File.Exists(_path))
                _user.AddRange(JsonSerializer.Deserialize<List<EventFlag>>(File.ReadAllText(_path)) ?? new());
        }
        catch { }
    }

    public void Save()
    {
        if (_path == null) return;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_user, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    /// <summary>All 256 story flags (names from the flag-name store) followed by the user's flags.</summary>
    public List<EventFlag> GetAll()
    {
        var list = new List<EventFlag>();
        for (int f = 0; f < 256; f++)
            list.Add(new EventFlag { Id = f, RamAddress = StoryFlagBase + (f >> 3), BitIndex = f & 7, Name = StoryFlags.DisplayName(f) ?? "" });
        list.AddRange(_user);
        return list;
    }

    public IReadOnlyList<EventFlag> UserFlags => _user;

    public EventFlag? GetByRamAddress(int address, int bit) => GetAll().FirstOrDefault(f => f.RamAddress == address && f.BitIndex == bit);

    public EventFlag AddUserFlag(int address, int bit, string name, string description = "")
    {
        var f = new EventFlag { Id = -1, RamAddress = address, BitIndex = bit & 7, Name = name, Description = description };
        _user.RemoveAll(x => x.RamAddress == address && x.BitIndex == f.BitIndex);
        _user.Add(f);
        Save();
        return f;
    }

    public void RemoveUserFlag(EventFlag f) { _user.Remove(f); Save(); }

    /// <summary>
    /// Import names from CSV. Lines "flag,name[,description]" (flag = hex 00-FF) name story flags; lines
    /// "$address,bit,name[,description]" add user flags. Returns how many lines were used.
    /// </summary>
    public int ImportCsv(string path)
    {
        int used = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = SplitCsv(line);
            if (parts.Count >= 3 && parts[0].StartsWith("$") &&
                int.TryParse(parts[0][1..], System.Globalization.NumberStyles.HexNumber, null, out int addr) && int.TryParse(parts[1], out int bit))
            {
                AddUserFlag(addr, bit, parts[2], parts.Count > 3 ? parts[3] : "");
                used++;
            }
            else if (parts.Count >= 2 && int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out int flag) && flag is >= 0 and <= 255)
            {
                StoryFlags.SetName(flag, parts[1]);
                used++;
            }
        }
        return used;
    }

    private static List<string> SplitCsv(string line)
    {
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { parts.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(c);
        }
        parts.Add(cur.ToString().Trim());
        return parts;
    }

    /// <summary>A flag's value in a WRAM image, using the engine's live bit-mask table when it looks valid.</summary>
    public static bool Read(byte[] wram, EventFlag f)
    {
        if (f.RamAddress < 0 || f.RamAddress >= wram.Length) return false;
        int mask = 1 << f.BitIndex;
        if (f.Id >= 0 && BitMaskTable + 7 < wram.Length)
        {
            byte m = wram[BitMaskTable + f.BitIndex];
            if (m != 0 && (m & (m - 1)) == 0) mask = m;   // a single bit: the engine's table is set up
        }
        return (wram[f.RamAddress] & mask) != 0;
    }
}
