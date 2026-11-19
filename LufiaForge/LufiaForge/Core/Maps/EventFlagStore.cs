using System.IO;
using System.Text.Json;

namespace LufiaForge.Core.Maps;

// a flag watched in RAM: a story flag ($1296 + id/8) or one the user added (any address and bit)
public sealed class EventFlag
{
    // story flag number 00-FF, or -1 for a user flag
    public int Id { get; set; } = -1;
    public int RamAddress { get; set; }
    public int BitIndex { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public bool CurrentValue { get; set; }
}

// the flags the live tracker watches: the game's 256 story flags (WRAM $1296-$12B5) plus flags the user adds,
// which are kept per rom in <rom>.lfflags.json. story flag names stay in the shared flag-name store
public sealed class EventFlagStore
{
    public const int StoryFlagBase = Lufia1Constants.RamStoryFlags;
    // WRAM table of bit masks the engine uses for flag bits (filled in at boot)
    public const int BitMaskTable = Lufia1Constants.RamFlagBitMasks;

    private readonly List<EventFlag> _userFlags = new();
    private string? _flagsPath;

    public void Open(string? romPath)
    {
        _userFlags.Clear();
        _flagsPath = string.IsNullOrEmpty(romPath) ? null : Path.ChangeExtension(romPath, ".lfflags.json");
        try
        {
            if (_flagsPath != null && File.Exists(_flagsPath))
                _userFlags.AddRange(JsonSerializer.Deserialize<List<EventFlag>>(File.ReadAllText(_flagsPath)) ?? new());
        }
        catch { }
    }

    public void Save()
    {
        if (_flagsPath == null) return;
        try { File.WriteAllText(_flagsPath, JsonSerializer.Serialize(_userFlags, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    // all 256 story flags (names from the flag-name store), then the user's flags
    public List<EventFlag> GetAll()
    {
        var list = new List<EventFlag>();
        for (int flag = 0; flag < 256; flag++)
            list.Add(new EventFlag { Id = flag, RamAddress = StoryFlagBase + (flag >> 3), BitIndex = flag & 7, Name = StoryFlags.DisplayName(flag) ?? "" });
        list.AddRange(_userFlags);
        return list;
    }

    public IReadOnlyList<EventFlag> UserFlags => _userFlags;

    public EventFlag? GetByRamAddress(int address, int bit) => GetAll().FirstOrDefault(f => f.RamAddress == address && f.BitIndex == bit);

    public EventFlag AddUserFlag(int address, int bit, string name, string description = "")
    {
        var flag = new EventFlag { Id = -1, RamAddress = address, BitIndex = bit & 7, Name = name, Description = description };
        _userFlags.RemoveAll(x => x.RamAddress == address && x.BitIndex == flag.BitIndex);
        _userFlags.Add(flag);
        Save();
        return flag;
    }

    public void RemoveUserFlag(EventFlag flag) { _userFlags.Remove(flag); Save(); }

    // imports names from a CSV. lines "flag,name[,description]" (flag = hex 00-FF) name story flags, lines
    // "$address,bit,name[,description]" add user flags. returns how many lines got used
    public int ImportCsv(string path)
    {
        int used = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = SplitCsv(line);
            if (parts.Count >= 3 && parts[0].StartsWith("$") &&
                int.TryParse(parts[0][1..], System.Globalization.NumberStyles.HexNumber, null, out int address) && int.TryParse(parts[1], out int bit))
            {
                AddUserFlag(address, bit, parts[2], parts.Count > 3 ? parts[3] : "");
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
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { parts.Add(field.ToString().Trim()); field.Clear(); }
            else field.Append(c);
        }
        parts.Add(field.ToString().Trim());
        return parts;
    }

    // a flag's value in a WRAM image, using the engine's live bit-mask table if it looks valid
    public static bool Read(byte[] wram, EventFlag flag)
    {
        if (flag.RamAddress < 0 || flag.RamAddress >= wram.Length) return false;
        int mask = 1 << flag.BitIndex;
        if (flag.Id >= 0 && BitMaskTable + 7 < wram.Length)
        {
            byte engineMask = wram[BitMaskTable + flag.BitIndex];
            if (engineMask != 0 && (engineMask & (engineMask - 1)) == 0) mask = engineMask;   // a single bit = the engine's table is set up
        }
        return (wram[flag.RamAddress] & mask) != 0;
    }
}
