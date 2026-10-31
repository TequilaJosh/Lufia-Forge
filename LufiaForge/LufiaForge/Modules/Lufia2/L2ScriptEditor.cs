using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>An event of a map: what starts it and where its script is (the Events page's list, the editor's title).</summary>
public sealed class L2EventItem
{
    public string Title { get; init; } = "";
    public string Preview { get; init; } = "";
    public L2EventBlocks.Entry? Entry { get; init; }
    public bool IsInit { get; init; }
    /// <summary>A puzzle script (its own command set, shown command by command).</summary>
    public List<L2Script.Op>? PuzzleOps { get; init; }
    public override string ToString() => Title;
}

/// <summary>A field of a step: a drop-down (who, item, map, jump target...) or a number, edited on the step's card.</summary>
public partial class L2FieldVm : ObservableObject
{
    private readonly L2ScriptEditor _owner;
    private bool _loading;
    public L2Step Step { get; }
    public L2ParamDef Def { get; }
    public string Label { get; }
    public string Hint { get; }
    public bool IsChoice { get; }
    public bool IsNumber => !IsChoice;
    public bool IsJump => Def.Kind == L2Kind.Jump;
    public bool IsFlag => Def.Kind == L2Kind.Flag;
    public bool HasHint => Hint.Length > 0;
    public List<L2Option> Choices { get; } = new();
    [ObservableProperty] private L2Option? _selected;
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private BitmapSource? _picture;
    [ObservableProperty] private string _error = "";
    private readonly bool _hex;

    public L2FieldVm(L2ScriptEditor owner, L2Step step, L2ParamDef def, IEnumerable<L2Option>? choices, int value, bool hex, BitmapSource? picture)
    {
        _owner = owner; Step = step; Def = def; _hex = hex;
        Label = def.Label.Length > 0 ? char.ToUpperInvariant(def.Label[0]) + def.Label[1..] : "";
        Hint = def.Hint;
        _loading = true;
        if (choices != null)
        {
            IsChoice = true;
            Choices.AddRange(choices);
            Selected = Choices.FirstOrDefault(c => c.Value == value);
            if (Selected == null && value >= 0) { var extra = new L2Option(value, $"{value:X2} (other)"); Choices.Add(extra); Selected = extra; }
        }
        Value = hex ? value.ToString(value > 0xFF ? "X3" : "X2") : value.ToString();
        Picture = picture;
        _loading = false;
    }

    /// <summary>The value as the step has it (a jump: the step index).</summary>
    public int Raw => IsChoice ? Selected?.Value ?? -1 : int.TryParse(Value.Trim(), _hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;

    partial void OnSelectedChanged(L2Option? value)
    {
        if (_loading || value == null) return;
        _owner.SetField(this, value.Value);
    }

    partial void OnValueChanged(string value)
    {
        if (_loading || IsChoice) return;
        var style = _hex ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!int.TryParse(value.Trim(), style, CultureInfo.InvariantCulture, out int v) || v < Def.Min || v > Def.Max)
        {
            Error = _hex ? $"Enter a hex value from {Def.Min:X2} to {Def.Max:X2}." : $"Enter a number from {Def.Min} to {Def.Max}.";
            return;
        }
        Error = "";
        _owner.SetField(this, v);
    }

    /// <summary>Go where the jump leads (highlights its step).</summary>
    public void Follow() { if (IsJump && Selected != null) _owner.HighlightLine(Selected.Value); }
    public void ShowFlagUses() { if (IsFlag) _owner.ShowFlag(Raw); }
}

/// <summary>One step of the script as a card: text with its speaker, or a command with its fields in place.</summary>
public partial class L2LineVm : ObservableObject
{
    public L2ScriptEditor Owner { get; }
    public L2Step? Step { get; }
    public L2Script.Op? PuzzleOp { get; }
    public int Index { get; set; }

    public L2LineVm(L2ScriptEditor owner, L2Step step, int index) { Owner = owner; Step = step; Index = index; }
    public L2LineVm(L2ScriptEditor owner, L2Script.Op op, int index) { Owner = owner; PuzzleOp = op; Index = index; Description = op.Text; }

    public string LineText => $"{Index + 1}";
    public string OffsetText => Step == null ? $"ROM 0x{PuzzleOp!.At:X6}" : Step.IsNew ? "new step" : $"ROM 0x{Step.At:X6}";
    public bool IsText => Step?.IsText == true;
    public bool IsCommand => Step != null && !Step.IsText;
    public bool IsPuzzle => PuzzleOp != null;

    // text
    public IReadOnlyList<L2Option> SpeakerChoices => L2ScriptEditor.Speakers;
    [ObservableProperty] private L2Option? _speaker;
    [ObservableProperty] private string _text = "";
    public IReadOnlyList<L2Option> EndingChoices => L2ScriptEditor.Endings;
    [ObservableProperty] private L2Option? _ending;
    [ObservableProperty] private bool _canChangeEnding;
    [ObservableProperty] private bool _speakerEditable;
    [ObservableProperty] private string _sizeText = "";
    /// <summary>The text is wider or longer than the box (a soft warning: it is still written as typed).</summary>
    public bool SizeWarns => SizeText.Contains("Warning:");
    partial void OnSizeTextChanged(string value) => OnPropertyChanged(nameof(SizeWarns));

    // command
    [ObservableProperty] private string _commandName = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _waitsText = "";
    [ObservableProperty] private string _sureText = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _flow = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private bool _reached = true;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _bytesText = "";
    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private bool _dropAbove, _dropBelow;
    public ObservableCollection<L2FieldVm> Fields { get; } = new();

    public bool HasWaits => WaitsText.Length > 0;
    public bool HasNote => Note.Length > 0;
    public bool HasFlow => Flow.Length > 0;
    public bool HasWarning => Warning.Length > 0;
    public bool HasFields => Fields.Count > 0;
    public bool ShowBytes => Owner.ShowBytes;
    public double Dim => Reached ? 1 : 0.5;
    /// <summary>A "go to another map" step: its scene can be opened.</summary>
    public bool CanOpenScene => Step is { IsText: false, Op: 0x16 } && Step.Bytes.Length >= 4;
    public string OpenSceneText => CanOpenScene ? $"▶ Open scene {Step!.Bytes[2]:X2} of map {Step.Bytes[1]:X2}" : "";

    partial void OnWaitsTextChanged(string value) => OnPropertyChanged(nameof(HasWaits));
    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
    partial void OnFlowChanged(string value) => OnPropertyChanged(nameof(HasFlow));
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnReachedChanged(bool value) => OnPropertyChanged(nameof(Dim));
    public void RefreshShowBytes() => OnPropertyChanged(nameof(ShowBytes));
    public void Notify(string property) => OnPropertyChanged(property);
    public void RefreshNumber() { OnPropertyChanged(nameof(LineText)); OnPropertyChanged(nameof(OffsetText)); }

    private bool _filling;
    public bool Filling { get => _filling; set => _filling = value; }

    partial void OnSpeakerChanged(L2Option? value) { if (!_filling && value != null) Owner.SetSpeaker(this, value.Value); }
    partial void OnTextChanged(string value) { if (!_filling) Owner.SetText(this, value); }
    partial void OnEndingChanged(L2Option? value) { if (!_filling && value != null) Owner.SetEnding(this, value.Value); }
}

/// <summary>A story flag or counter ticked for playback (what the cutscene checks).</summary>
public partial class L2StartFlagVm : ObservableObject
{
    public int Number { get; init; }
    public bool IsCounter { get; init; }
    public string Label { get; init; } = "";
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private int _value;
    public Action? Changed { get; set; }
    partial void OnIsOnChanged(bool value) => Changed?.Invoke();
    partial void OnValueChanged(int value) => Changed?.Invoke();
}

/// <summary>A character drawn on the stage.</summary>
public sealed class L2StageActor
{
    public int Id { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public string Name { get; init; } = "";
    public double Left { get; init; }
    public double Top { get; init; }
    public ImageSource? Picture { get; init; }
    public string Label { get; init; } = "";
    public double Opacity { get; init; } = 1;
    public bool Moved { get; init; }
    public bool Picked { get; init; }
    /// <summary>A small arrow for sprites without a picture for each facing.</summary>
    public string Facing { get; init; } = "";
}

/// <summary>A step placed in time for the timeline: its track, when it starts and how long it takes (frames).</summary>
public sealed record L2TimelineItem(int Line, string Track, string Kind, string Label, int Start, int Duration, bool Reached);

/// <summary>Something that can happen at a tile of the stage (the stage's menu).</summary>
public sealed record L2StageAction(string Group, string Header, Action Act);

/// <summary>Where every story flag is set, cleared and checked, read from every map's scripts once per ROM.</summary>
public static class L2FlagUses
{
    private static RomBuffer? _rom;
    private static Dictionary<int, List<string>>? _uses;

    public static void Invalidate() { _uses = null; }

    public static Dictionary<int, List<string>> Get(RomBuffer rom)
    {
        if (_uses != null && ReferenceEquals(_rom, rom)) return _uses;
        var uses = new Dictionary<int, List<string>>();
        for (int m = 0; m < L2Maps.Count; m++)
        {
            string map = $"{m:X2} {L2Maps.Name(rom, m)}";
            foreach (var (title, ops) in L2Script.MapScripts(rom, m))
                foreach (var o in ops.Where(o => !o.IsText))
                {
                    int c = rom.ReadByte(o.At);
                    void Use(int flag, string how)
                    {
                        if (!uses.TryGetValue(flag, out var l)) uses[flag] = l = new();
                        l.Add($"{how,-8} {map}, {title} ({o.At:X6})");
                    }
                    switch (c)
                    {
                        case 0x1A: Use(rom.ReadByte(o.At + 1), "set"); break;
                        case 0x1B: Use(rom.ReadByte(o.At + 1), "clear"); break;
                        case 0x15 or 0x6A: Use(rom.ReadByte(o.At + 1), "test"); break;
                        case 0x14:
                            for (int q = o.At + 1, g = 0; g < 64 && q < o.At + o.Length; g++)
                            {
                                int k = rom.ReadByte(q++);
                                if (k == 0xFF) break;
                                if ((k & 0xC0) == 0xC0) { q += (k & 0xF8) switch { 0xC0 => 4, 0xC8 or 0xD0 or 0xD8 => 3, _ => 2 }; continue; }
                                if ((k & 0x30) == 0) { Use(rom.ReadByte(q), "test"); q += 1; }
                                else q += 2;
                            }
                            break;
                    }
                }
        }
        _rom = rom; _uses = uses;
        return uses;
    }

    /// <summary>A story flag no event script and no game code uses (for "plays once"), or -1.</summary>
    public static int Free(RomBuffer rom, IEnumerable<int>? avoid = null)
    {
        var uses = Get(rom);
        var skip = avoid?.ToHashSet() ?? new HashSet<int>();
        for (int f = 0xEF; f >= 0x01; f--)
            if (!uses.ContainsKey(f) && L2Flags.Reserved(f) == null && !skip.Contains(f)) return f;
        return -1;
    }
}

/// <summary>Story flags the game's own code reads or writes (so they are never handed out as free).</summary>
public static class L2Flags
{
    public static string? Reserved(int f) => f switch
    {
        0xFF => "the answer of checks and \"the party did not win\" after a story battle",
        0xFE => "set after some story battles",
        >= 0xF0 => "cleared on every map change",
        >= 0xC8 and <= 0xD0 => "Iris treasures taken out of the Ancient Cave",
        0x95 => "where the ship can be parked",
        0x41 or 0x50 or 0x5F or 0x62 => "changes the world map",
        _ => null,
    };
}

/// <summary>
/// One Lufia II event or cutscene open for editing, the way Lufia 1's event editor works: the steps are cards edited in
/// place (text with its speaker, commands with their fields), the map is a stage showing where everyone stands after
/// the picked step (click a tile: a character walks, appears or turns there, the camera jumps there), a timeline places
/// the steps in time, playback runs the script the way the game would, and the edits are written to the ROM by
/// themselves when the editor is left (Save Project, Apply, closing the window). Hosted by the Events page, the
/// Cutscenes page and the pop-up editor opened from the Maps page.
/// </summary>
public partial class L2ScriptEditor : ObservableObject, IPendingEdits
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private int _map = -1;
    private L2EventItem? _event;
    private L2EventScript? _script;
    private L2EventScript.Snapshot? _saved;
    private readonly List<L2EventScript.Snapshot> _undo = new(), _redo = new();
    private L2LineVm? _textLine;   // the line whose text is being typed (one Undo step)
    private Dictionary<int, List<L2EventBlocks.Entry>>? _owners;
    private bool _refreshing;

    // names for the drop-downs and descriptions
    private string[] _items = Array.Empty<string>(), _spells = Array.Empty<string>(), _songs = Array.Empty<string>(), _maps = Array.Empty<string>();
    private Dictionary<int, (string Name, int Sprite)> _who = new();
    /// <summary>Hero (1-6) to the character that stands for it on this map (same sprite, not in the party).</summary>
    private Dictionary<int, int> _heroStandIns = new();
    /// <summary>The one naming of characters (<see cref="L2Speakers"/>) for this map.</summary>
    private L2Speakers? _speakers;
    /// <summary>Who speaks each text step of the script (index: label), as the game picks it.</summary>
    private Dictionary<int, string> _spoken = new();
    private List<(int Id, string Name)> _points = new(), _paths = new(), _areas = new(), _exits = new(), _doors = new();
    private List<(int Id, int X, int Y)> _pointSpots = new();
    private HashSet<int> _cast = new();
    private List<L2Persistent.Place> _persistent = new();
    private readonly Dictionary<L2Kind, List<L2Option>> _choices = new();
    private readonly Dictionary<int, BitmapSource?> _spritePics = new();
    private readonly Dictionary<(int, int, int), BitmapSource?> _facingPics = new();

    public static readonly IReadOnlyList<L2Option> Speakers = BuildSpeakers();
    private static List<L2Option> BuildSpeakers()
    {
        var l = new List<L2Option> { new(0x08, "The person talked to") };
        foreach (int h in new[] { 0, 1, 2, 3, 4, 5, 6 }) l.Add(new L2Option(0x61 + Array.IndexOf(L2EventCommands.SpeakerOf61, h), L2EventCommands.HeroNames[h]));
        l.Add(new L2Option(0x13, "A character of the map..."));
        l.Add(new L2Option(0x9E, "Narration (no speaker)"));
        return l;
    }
    public static readonly IReadOnlyList<L2Option> Endings = new List<L2Option> { new(0x01, "then the event goes on"), new(0x00, "then the event ends"), new(0x0B, "the answers follow") };

    public RomBuffer? Rom => _rom;
    public MainViewModel? Main => _main;
    public int Map => _map;
    public L2EventItem? Event => _event;
    public L2EventScript? Script => _script;
    public string MapName => _map >= 0 && _map < _maps.Length ? _maps[_map] : "";
    public IReadOnlyDictionary<int, (string Name, int Sprite)> Characters => _who;

    public ObservableCollection<L2LineVm> Lines { get; } = new();
    [ObservableProperty] private int _selectedLine = -1;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private bool _hasScript;
    [ObservableProperty] private bool _isPuzzle;
    [ObservableProperty] private bool _canUndo, _canRedo;
    [ObservableProperty] private string _undoTip = "Undo (Ctrl+Z)", _redoTip = "Redo (Ctrl+Y)";
    [ObservableProperty] private string _bytesDump = "";
    /// <summary>Puzzle scripts: the picked command's bytes, written back the same size.</summary>
    [ObservableProperty] private string _puzzleBytes = "";
    /// <summary>The cutscene layout: the stage gets most of the room and is always on.</summary>
    [ObservableProperty] private bool _cutsceneMode;

    private static bool _showBytes = Common.LayoutMemory.Get("l2events.showbytes") == "1";
    public bool ShowBytes
    {
        get => _showBytes;
        set
        {
            if (_showBytes == value) return;
            _showBytes = value;
            Common.LayoutMemory.Set("l2events.showbytes", value ? "1" : null);
            OnPropertyChanged();
            foreach (var l in Lines) l.RefreshShowBytes();
        }
    }

    /// <summary>The script was written to the ROM (the pages read their lists again): what was written.</summary>
    public event Action<string>? Saved;
    /// <summary>Asks the view to scroll the stage so a point of the map (pixels) is in sight.</summary>
    public event Action<double, double>? ScrollRequested;
    /// <summary>Asks the view to bring a line into view.</summary>
    public event Action<int>? LineHighlighted;
    /// <summary>Asks the host to show where a story flag is used.</summary>
    public event Action<int>? FlagRequested;
    /// <summary>Asks the host to open another event (a scene of another map a "go to" step starts).</summary>
    public event Action<int, int, int>? OpenRequested;
    /// <summary>The stage was simulated again (the timeline draws again).</summary>
    public event Action? Resimulated;
    /// <summary>Playback is at a line, so many frames into it.</summary>
    public event Action<int, int>? PlaybackProgress;

    public L2ScriptEditor()
    {
        _timer.Tick += (_, _) => PlayTick();
        _playAnswer = AnswerChoices[0];
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Opens an event of a map for editing.</summary>
    public void Load(RomBuffer rom, MainViewModel main, int map, L2EventItem item, string title)
    {
        Stop();
        if (!ReferenceEquals(rom, _rom) || _maps.Length == 0) ReadRomNames(rom);
        _rom = rom; _main = main;
        _map = map;
        _event = item;
        Title = title;
        ReadMap();   // (again for the same map too: the Maps page may have added characters or points meanwhile)
        _undo.Clear(); _redo.Clear(); UpdateUndo();
        _textLine = null;
        _flagsOn.Clear(); _countersAtStart.Clear();
        _startOverride = null;
        _picked = -1;
        ReadScript(-1);
        TestNote = NewGameNote();
    }

    /// <summary>Opens one event of a map by its list and id (from the Maps page).</summary>
    public bool Load(RomBuffer rom, MainViewModel main, int map, int head, int id)
    {
        var item = ItemsOf(rom, map).FirstOrDefault(e => e.Entry?.Head == head && e.Entry?.Id == id);
        if (item == null) return false;
        if (!ReferenceEquals(rom, _rom) || _maps.Length == 0) ReadRomNames(rom);
        Load(rom, main, map, item, TitleOf(rom, map, item));
        return true;
    }

    /// <summary>Nothing open.</summary>
    public void Unload()
    {
        Stop();
        _script = null; _event = null; _saved = null;
        Lines.Clear(); _undo.Clear(); _redo.Clear(); UpdateUndo();
        HasScript = false; CanEdit = false; IsDirty = false; IsPuzzle = false;
        Title = ""; Info = ""; Status = ""; BytesDump = "";
        _states = new(); _run = new(); _firstVisit.Clear(); _start = null;
        StartFlags.Clear(); HasStartFlags = false; HasQuestion = false; FlowNote = "";
        TimelineItems = new();
        ShowState(null, null);
        Resimulated?.Invoke();
    }

    private void ReadRomNames(RomBuffer rom)
    {
        _items = L2Battle.Items(rom).Select(i => i.Name.Trim()).ToArray();
        _spells = L2Battle.Spells(rom).Select(s => s.Name.Trim()).ToArray();
        _songs = Enumerable.Range(0, L2Script.SongCount).Select(i => L2Script.SongName(rom, i)).ToArray();
        _maps = Enumerable.Range(0, L2Maps.Count).Select(m => MapNameOf(rom, m)).ToArray();
        _spritePics.Clear(); _facingPics.Clear();
        _choices.Clear();
        _who.Clear();
    }

    public static string MapNameOf(RomBuffer rom, int m)
    {
        if (LufiaForge.Core.Maps.MapNames.ProjectNames is { } p && p.TryGetValue(m, out var pn) && !string.IsNullOrWhiteSpace(pn)) return pn;
        string n = L2Maps.Name(rom, m);
        return n.Length > 0 && !n.Contains('~') ? n : m switch { 0 => "World map", 1 => "Sea floor", _ => "Map " + m.ToString("X2") };
    }

    /// <summary>The events of a map as the Events page lists them (the start-up script first, puzzles last).</summary>
    public static List<L2EventItem> ItemsOf(RomBuffer rom, int map)
    {
        var list = new List<L2EventItem> { new() { Title = "Start-up script (sets up the map)", IsInit = true, Preview = "Adds the map's characters, music and settings as it loads." } };
        int block = L2Events.Block(rom, map);
        if (block >= 0)
        {
            var speakers = new L2Speakers(rom, map);
            var starts = L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).ToDictionary(r => 0x4F + r.Bytes[0], r => r.Bytes);
            var triggers = L2MapInfo.Records(rom, map, L2MapInfo.Triggers, 5).ToDictionary(r => (int)r.Bytes[0], r => r.Bytes);
            foreach (var e in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript).OrderBy(e => e.Head == 2 ? 9 : e.Head).ThenBy(e => e.Id))
            {
                string preview = FirstText(rom, block + e.Offset, block);
                string title = e.Head switch
                {
                    L2EventBlocks.TalkList => "Talk to " + WhoTitle(e.Id, speakers, starts),
                    L2EventBlocks.TriggerList => triggers.TryGetValue(e.Id, out var t)
                        ? $"Step on area {e.Id} ({t[1]},{t[2]})-({t[3] - 1},{t[4] - 1})" : $"Step on area {e.Id} (no area on the map)",
                    L2EventBlocks.SceneList => L2Cutscene.Starter(rom, map, e.Id) is { } st
                        ? $"Scene {e.Id:X2}: when the party enters the map" + (st.Flag >= 0 ? $" (story flag {st.Flag:X2} {(st.MustBeOn ? "on" : "off")})" : "")
                        : $"Scene {e.Id:X2} (started by \"Go to another map\")",
                    L2EventBlocks.GameList => e.Id switch
                    {
                        0 => "When the map is shown",
                        1 => "On entering the map or one of its rooms",
                        2 => "Before the map loads",
                        _ => $"Game event {e.Id:X2}",
                    },
                    _ => $"Unused list {e.Head:X} event {e.Id:X2}",
                };
                list.Add(new L2EventItem { Title = title, Entry = e, Preview = preview });
            }
        }
        foreach (var (title, ops) in L2Puzzles.MapScripts(rom, map)) list.Add(new L2EventItem { Title = title, PuzzleOps = ops, Preview = "A puzzle script (switches, doors, spots): its own commands." });
        return list;
    }

    private static string WhoTitle(int id, L2Speakers speakers, Dictionary<int, byte[]> starts)
    {
        string at = id >= 0x50 && starts.TryGetValue(id, out var b) ? $" at ({b[1]},{b[2]})" : "";
        return speakers.Label(id) + at;
    }

    /// <summary>The editor's title for an event: the map and what starts the event.</summary>
    public static string TitleOf(RomBuffer rom, int map, L2EventItem item)
    {
        string m = $"Map {map:X2} {MapNameOf(rom, map)}";
        if (item.Entry is not { } e) return $"{m} - {item.Title}";
        return e.Head switch
        {
            L2EventBlocks.TalkList => $"{m} - {item.Title} (actor {e.Id:X2})",
            L2EventBlocks.TriggerList => $"{m} - {item.Title}",
            L2EventBlocks.SceneList => $"{m} - scene {e.Id:X2}",
            _ => $"{m} - {item.Title}",
        };
    }

    public static string FirstText(RomBuffer rom, int start, int jumpBase)
    {
        try
        {
            var s = L2EventScript.Read(rom, start, jumpBase);
            var t = s.Steps.FirstOrDefault(x => x.IsText);
            if (t == null) return "";
            string line = t.Text.Replace("\n", " ");
            return line.Length > 70 ? line[..70] + "..." : line;
        }
        catch (Exception) { return ""; }
    }

    private void ReadMap()
    {
        var rom = _rom!;
        int map = _map;
        _who = new();
        _choices.Clear();
        _speakers = new L2Speakers(rom, map);
        for (int h = 0; h < 7; h++) _who[h] = (_speakers.Label(h), h);
        var sprites = L2MapInfo.CharacterSprites(rom, map);
        _cast = L2Cutscene.CastIds(rom, map);
        string Made(int id) => _cast.Contains(id) ? ", made for cutscenes" : "";
        foreach (var (_, b) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
        {
            int id = 0x4F + b[0];
            _who[id] = ($"{_speakers.Label(id)} at ({b[1]},{b[2]}){Made(id)}", _speakers.SpriteOf(id));
        }
        foreach (var kv in sprites.Where(kv => !_who.ContainsKey(kv.Key))) _who[kv.Key] = (_speakers.Label(kv.Key) + Made(kv.Key), kv.Value);
        _persistent = L2Persistent.On(L2Persistent.All(rom), map);
        foreach (var p in _persistent.Where(p => !_who.ContainsKey(p.Id))) _who[p.Id] = (_speakers.Label(p.Id), p.Sprite);
        // a hero standing on the map as a character of its own (Selan at home in Parcelyte is story character 19 with
        // her sprite 01): text for the hero then has to come from that character, the party's hero isn't there
        _heroStandIns = new();
        foreach (var kv in _who.Where(kv => kv.Key >= 0x10 && kv.Value.Sprite is >= 1 and < 7).OrderBy(kv => kv.Key))
            _heroStandIns.TryAdd(kv.Value.Sprite, kv.Key);
        foreach (var (h, id) in _heroStandIns) _who[h] = ($"{L2EventCommands.HeroNames[h]} (party; on this map also {id:X2})", h);
        _pointSpots = L2StoryClear.PresetPoints(rom, map);
        _points = L2MapInfo.Records(rom, map, L2MapInfo.Points, 3).Select(r => ((int)r.Bytes[0], $"point {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})")).ToList();
        _paths = L2MapInfo.Records(rom, map, L2MapInfo.Paths, 6).Select(r => ((int)r.Bytes[0], $"path {r.Bytes[0]} (from {r.Bytes[1]},{r.Bytes[2]})")).ToList();
        _areas = L2MapInfo.Records(rom, map, L2MapInfo.Areas, 5).Select(r => ((int)r.Bytes[0], $"area {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})-({r.Bytes[3] - 1},{r.Bytes[4] - 1})")).ToList();
        _exits = L2MapInfo.Records(rom, map, L2MapInfo.Exits, 9).Select(r => ((int)r.Bytes[0], $"exit {r.Bytes[0]} to {(r.Bytes[8] < _maps.Length ? _maps[r.Bytes[8]] : "map " + r.Bytes[8].ToString("X2"))}")).ToList();
        _doors = L2MapInfo.Records(rom, map, L2MapInfo.Doors, 15).Select(r => ((int)r.Bytes[0], $"door {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})")).ToList();
        PointChoices.Clear();
        foreach (var p in _pointSpots) PointChoices.Add(new L2Option(p.Id, $"point {p.Id} ({p.X}, {p.Y})"));
        TestPoint = PointChoices.FirstOrDefault();
        try
        {
            var (w, h, px) = L2Maps.Render(rom, map);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)(c == 0 ? 0xFF000000 : c)).ToArray(), w * 4);
            bmp.Freeze();
            MapImage = bmp;
        }
        catch (Exception ex) { MapImage = null; Status = "The map can't be drawn: " + ex.Message; }
    }

    /// <summary>A character's name for the stage and descriptions (short: no sprite).</summary>
    public string Name(int id)
    {
        if (id < 0) return "nobody";
        if (id < 7) return L2EventCommands.HeroNames[id];
        if (id == 0xFF) return "everyone";
        return _speakers?.Label(id, sprite: false) ?? L2EventCommands.DefaultWho(id);
    }

    private string LongName(int id) => _who.TryGetValue(id, out var w) ? w.Name : L2EventCommands.DefaultWho(id);

    private void ReadScript(int keepLine)
    {
        Stop();
        Lines.Clear();
        _script = null; _owners = null; IsDirty = false; Status = ""; _textLine = null;
        _picked = -1;
        var e = _event;
        var rom = _rom;
        if (e == null || rom == null) { HasScript = false; CanEdit = false; IsPuzzle = false; Info = ""; Simulate(); ShowStage(); return; }
        HasScript = true;
        IsPuzzle = e.PuzzleOps != null;
        if (e.PuzzleOps != null)
        {
            CanEdit = false;
            for (int i = 0; i < e.PuzzleOps.Count; i++) Lines.Add(new L2LineVm(this, e.PuzzleOps[i], i) { BytesText = Convert.ToHexString(rom.ReadBytes(e.PuzzleOps[i].At, e.PuzzleOps[i].Length)) });
            Info = "A puzzle script: switches, doors and spots run with their own commands. Pick a command and change its bytes under the list (it must keep its size).";
            Simulate(); ShowStage();
            SelectedLine = keepLine >= 0 && keepLine < Lines.Count ? keepLine : -1;
            return;
        }
        int block = L2Events.Block(rom, _map);
        string where;
        if (e.IsInit)
        {
            int init = L2EventBlocks.InitScript(rom, _map);
            _script = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
            where = $"Runs as the map loads, all at once (nothing that waits works here): it adds the map's characters (\"Add a map character\") and sets the music and map settings. Script at ROM {L2Rom.FileToSnes(init):X6}";
        }
        else
        {
            _script = L2EventScript.Read(rom, block + e.Entry!.Offset, block);
            _owners = L2EventBlocks.StepOwners(rom, block);
            string space = L2EventSpace.Contains(rom, block) ? "in Lufia Forge's event space" : "in the game's own place";
            where = $"Script at ROM {L2Rom.FileToSnes(block + e.Entry.Offset):X6}, the map's events {space} ({L2Rom.FileToSnes(block):X6})";
        }
        CanEdit = _script.Problem == null;
        int bytes = _script.Steps.Sum(s => s.OriginalLength);
        int texts = _script.Steps.Count(s => s.IsText);
        Info = "What it does: " + Summary() + $"\n{where} ({bytes} bytes), {_script.Steps.Count} steps, {texts} dialogue box{(texts == 1 ? "" : "es")}.";
        if (_script.Problem != null) Info += " It can't be changed here: " + _script.Problem;
        _saved = _script.TakeSnapshot();
        for (int i = 0; i < _script.Steps.Count; i++) Lines.Add(new L2LineVm(this, _script.Steps[i], i));
        foreach (var l in Lines) FillLine(l, fields: true);
        TestNeeds = e.Entry != null ? L2Cutscene.DescribeNeeds(rom, _map, e.Entry.Head, e.Entry.Id) : "";
        FillStartFlags();
        Simulate();
        Annotate();
        UpdateBytesDump();
        SelectedLine = -1;
        SelectedLine = keepLine >= 0 && keepLine < Lines.Count ? keepLine : Lines.Count > 0 ? 0 : -1;
    }

    /// <summary>What the event does in a few words: how it starts, its lines, flags and where it goes.</summary>
    private string Summary()
    {
        var e = _event; var s = _script;
        if (e == null || s == null || _rom == null) return "";
        var parts = new List<string>();
        int once = L2Cutscene.OnceFlag(s);
        parts.Add(e.IsInit ? "Start-up script" : e.Entry!.Head switch
        {
            L2EventBlocks.TalkList => "Talk",
            L2EventBlocks.TriggerList => "Stepping onto the area",
            L2EventBlocks.SceneList => L2Cutscene.Starter(_rom, _map, e.Entry.Id) is { } st
                ? $"Plays when the party enters the map{(st.Flag >= 0 ? $" while story flag {st.Flag:X2} is {(st.MustBeOn ? "on" : "off")}" : "")}, once (flag {st.Played:X2}); the party stands on point {st.Point}"
                : "Scene: plays when a \"Go to another map\" step sends the party here, or when a new game starts with it",
            L2EventBlocks.GameList => "Run by the game itself",
            _ => "Event",
        });
        int texts = s.Steps.Count(x => x.IsText);
        if (texts > 0) parts[0] += $" ({texts} box{(texts == 1 ? "" : "es")})";
        if (once >= 0) parts.Add($"plays once (story flag {once:X2})");
        var sets = s.Steps.Where(x => !x.IsText && x.Op == 0x1A && x.Bytes.Length > 1).Select(x => x.Bytes[1]).Distinct().ToList();
        var clears = s.Steps.Where(x => !x.IsText && x.Op == 0x1B && x.Bytes.Length > 1).Select(x => x.Bytes[1]).Distinct().ToList();
        if (sets.Count > 0) parts.Add("sets story flag " + string.Join(", ", sets.Select(f => f.ToString("X2"))));
        if (clears.Count > 0) parts.Add("clears flag " + string.Join(", ", clears.Select(f => f.ToString("X2"))));
        int moves = s.Steps.Count(x => !x.IsText && x.Op is 0x2F or 0x30 or 0xA3 or 0x7A or 0x2D or 0x2E or 0x98 or 0x99);
        if (moves > 0) parts.Add($"{moves} move{(moves == 1 ? "" : "s")}");
        var gotos = s.Steps.Where(x => !x.IsText && x.Op == 0x16 && x.Bytes.Length >= 4).Select(x => x.Bytes[1]).Distinct().ToList();
        if (gotos.Count > 0) parts.Add("goes to " + string.Join(", ", gotos.Select(m => m < _maps.Length ? _maps[m] : $"map {m:X2}")));
        if (s.Steps.Any(x => !x.IsText && x.Op is 0x14 or 0x15 or 0x6A)) parts.Add("changes with story flags");
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------ the lines

    private L2EventCommands.Words WordsFor(L2Step step) => new()
    {
        Who = id => _who.TryGetValue(id, out var w) ? w.Name : L2EventCommands.DefaultWho(id),
        Item = i => i < _items.Length ? _items[i] : $"item {i:X3}",
        Spell = i => i < _spells.Length ? _spells[i] : $"spell {i:X2}",
        Song = i => i < _songs.Length && _songs[i].Length > 0 ? $"{_songs[i]} ({i:X2})" : $"song {i:X2}",
        Map = m => m < _maps.Length ? $"{_maps[m]} ({m:X2})" : $"map {m:X2}",
        Flag = f => $"story flag {f:X2}",
        Jump = k => k < step.Jumps.Count && step.Jumps[k].Step is { } t && _script != null ? $"step {_script.Steps.IndexOf(t) + 1}" : "(pick a step)",
        Point = p => _points.FirstOrDefault(x => x.Id == p).Name ?? $"point {p}",
        Path = p => _paths.FirstOrDefault(x => x.Id == p).Name ?? $"path {p}",
        Area = a => _areas.FirstOrDefault(x => x.Id == a).Name ?? $"area {a}",
        Exit = x => _exits.FirstOrDefault(y => y.Id == x).Name ?? $"exit {x}",
        Door = d => _doors.FirstOrDefault(x => x.Id == d).Name ?? $"door {d}",
        Shop = s => $"shop {s:X2}",
        Capsule = c => c < L2EventCommands.CapsuleNames.Length ? L2EventCommands.CapsuleNames[c] : $"capsule monster {c}",
    };

    public string Sentence(L2Step s) => L2EventCommands.Find(s.Op)?.Describe(s.Bytes, WordsFor(s)) ?? $"Unknown command {s.Op:X2}";

    private static string Short(string s) => s.Length > 60 ? s[..60] + "..." : s;

    /// <summary>Who says a text step, as the game picks the speaker (the same naming as the Text page and playback).</summary>
    private string SpokenBy(L2Step s)
    {
        if (_script == null || _speakers == null) return "";
        int i = _script.Steps.IndexOf(s);
        int talkedTo = _event?.Entry is { Head: L2EventBlocks.TalkList } te ? te.Id : -1;
        try { _spoken = _speakers.ForScript(_script, talkedTo); } catch (Exception) { }
        if (!_spoken.TryGetValue(i, out var who)) return s.Op == 0x9E ? "Narration." : "Not reached from the start of the event.";
        return who == "Narration" ? "Narration." : s.Op == 0x10 ? who + "." : "Spoken by " + who + ".";
    }

    /// <summary>Fills a card from its step: the sentence, the text, the fields (when <paramref name="fields"/>).</summary>
    private void FillLine(L2LineVm vm, bool fields)
    {
        var s = vm.Step;
        if (s == null) return;
        vm.Filling = true;
        try
        {
            var def = L2EventCommands.Find(s.Op);
            vm.CommandName = s.IsText ? (s.Op == 0x10 ? "Ask a question" : "") : def?.Name ?? $"Unknown command {s.Op:X2}";
            vm.Description = s.IsText ? SpokenBy(s) + (s.Op == 0x10 ? " Write the question, then one line per answer at the end." : "") : Sentence(s);
            vm.WaitsText = !s.IsText && def?.Waits.Length > 0 ? "The event waits here " + def.Waits + "." : "";
            vm.SureText = def?.SureText ?? "";
            vm.BytesText = Convert.ToHexString(s.Bytes) + (s.IsText ? " + text" : "");
            if (s.IsText)
            {
                vm.SpeakerEditable = s.Op != 0x10;
                vm.Speaker = Speakers.FirstOrDefault(x => x.Value == s.Op);
                if (vm.Text.Replace("\r", "") != s.Text) vm.Text = s.Text;
                vm.CanChangeEnding = s.Op != 0x10;
                vm.Ending = Endings.FirstOrDefault(x => x.Value == s.End);
                vm.SizeText = SizeNote(s);
            }
            if (fields)
            {
                vm.Fields.Clear();
                if (def != null)
                    foreach (var p in def.FieldsFor(s.Bytes))
                    {
                        if (s.IsText && p.Kind == L2Kind.Choice && p.Options?.Any(o => o.Item1 is >= 0x61 and <= 0x67) == true) continue;   // the hero speaking is the speaker list
                        vm.Fields.Add(MakeField(s, p));
                    }
                vm.Notify(nameof(L2LineVm.HasFields));
            }
            var notes = new List<string>();
            var from = _script!.Steps.Select((x, i) => (x, i)).Where(t => t.x.Jumps.Any(j => j.Step == s)).Select(t => t.i + 1).ToList();
            if (from.Count > 0) notes.Add("from step " + string.Join(", ", from));
            if (_owners != null && s.At >= 0 && _owners.TryGetValue(s.At, out var o) && o.Count > 1 && _event?.Entry is { } me)
                notes.Add("shared with " + string.Join(", ", o.Where(x => x != me).Take(3).Select(EntryName)) + (o.Count > 4 ? "..." : ""));
            if (s.EndsFlow && !s.IsText && s.Op is not (0x00 or 0x42)) notes.Add("the event doesn't go on below");
            if (s.IsText && L2Cutscene.SpeakerActor(s.Bytes) is var hero && _heroStandIns.TryGetValue(hero, out int standIn))
                notes.Add($"{L2EventCommands.HeroNames[hero]} also stands on this map as character {standIn:X2}: when {L2EventCommands.HeroNames[hero]} isn't in the party here, the box points at someone else, so pick {standIn:X2} as the speaker (A character of the map...)");
            vm.Note = string.Join("; ", notes);
            vm.Notify(nameof(L2LineVm.CanOpenScene));
            vm.Notify(nameof(L2LineVm.OpenSceneText));
        }
        finally { vm.Filling = false; }
    }

    private string SizeNote(L2Step s)
    {
        if (_rom == null) return "";
        string? problem = L2EventText.Check(s.Text, _rom);
        int size = L2EventScript.SizeOf(_rom, s, s.At >= 0);
        int longest = L2Events.LongestLine(s.Text);
        string room = s.At >= 0 && size > s.OriginalLength ? $" (it had {s.OriginalLength}: longer text moves the map's events to the event space when saved)" : "";
        string warn = L2Events.WidthWarning(s.Text);
        return problem ?? $"{size} bytes{room}. Longest line: {longest} letters, {L2Events.LineCount(s.Text)} lines." + (warn.Length > 0 ? " Warning: " + warn : "");
    }

    private string EntryName(L2EventBlocks.Entry e) => e.Head switch
    {
        L2EventBlocks.TalkList => $"talking to {Name(e.Id)}",
        L2EventBlocks.TriggerList => $"area {e.Id}",
        L2EventBlocks.SceneList => $"scene {e.Id}",
        _ => $"game event {e.Id}",
    };

    private List<L2Option> ChoicesOf(L2Kind kind)
    {
        if (_choices.TryGetValue(kind, out var l)) return l;
        var rom = _rom!;
        IEnumerable<L2Option> c = kind switch
        {
            L2Kind.Hero => L2EventCommands.HeroNames.Select((n, i) => new L2Option(i, n)),
            L2Kind.Item => _items.Select((n, i) => new L2Option(i, $"{i:X3} {n}")),
            L2Kind.Spell => _spells.Select((n, i) => new L2Option(i, $"{i:X2} {n}")),
            L2Kind.Song => _songs.Select((n, i) => new L2Option(i, $"{i:X2} {(n.Length > 0 ? n : "(empty)")}")),
            L2Kind.Map => _maps.Select((n, i) => new L2Option(i, $"{i:X2} {n}")),
            L2Kind.Capsule => L2EventCommands.CapsuleNames.Select((n, i) => new L2Option(i, n)),
            L2Kind.Flag => Enumerable.Range(0, 256).Select(f => new L2Option(f, FlagChoice(rom, f))),
            L2Kind.Point => _points.Select(x => new L2Option(x.Id, x.Name)),
            L2Kind.Path => _paths.Select(x => new L2Option(x.Id, x.Name)),
            L2Kind.Area => _areas.Select(x => new L2Option(x.Id, x.Name)),
            L2Kind.Exit => _exits.Select(x => new L2Option(x.Id, x.Name)),
            L2Kind.Door => _doors.Select(x => new L2Option(x.Id, x.Name)),
            L2Kind.Who => WhoChoices(false),
            L2Kind.WhoOrAll => WhoChoices(true),
            _ => Enumerable.Empty<L2Option>(),
        };
        return _choices[kind] = c.ToList();
    }

    private L2FieldVm MakeField(L2Step s, L2ParamDef p)
    {
        int v;
        try { v = p.Get(s.Bytes); } catch (Exception) { v = 0; }
        BitmapSource? pic = null;
        IEnumerable<L2Option>? choices = null;
        bool hex = false;
        switch (p.Kind)
        {
            case L2Kind.Choice: choices = p.Options!.Select(o => new L2Option(o.Item1, o.Item2)); break;
            case L2Kind.Jump:
                v = p.JumpIndex < s.Jumps.Count && s.Jumps[p.JumpIndex].Step is { } t ? _script!.Steps.IndexOf(t) : -1;
                choices = _script!.Steps.Select((x, i) => new L2Option(i, $"step {i + 1}: {Short(x.IsText ? x.Text.Replace("\n", " ") : Sentence(x))}"));
                break;
            case L2Kind.Who or L2Kind.WhoOrAll:
                choices = ChoicesOf(p.Kind);
                if (_who.TryGetValue(v, out var w) && w.Sprite >= 0) pic = SpritePicture(w.Sprite);
                break;
            case L2Kind.Hero or L2Kind.Item or L2Kind.Spell or L2Kind.Song or L2Kind.Map or L2Kind.Capsule or L2Kind.Flag
                or L2Kind.Point or L2Kind.Path or L2Kind.Area or L2Kind.Exit or L2Kind.Door:
                choices = ChoicesOf(p.Kind); break;
            case L2Kind.Scene: choices = SceneChoices(s.Bytes.Length > 1 ? s.Bytes[1] : 0); break;
            case L2Kind.ArrivalPoint:
                int m = s.Bytes.Length > 1 ? s.Bytes[1] : 0;
                choices = L2MapInfo.Records(_rom!, m, L2MapInfo.Points, 3).Select(r => new L2Option(r.Bytes[0], $"point {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})"));
                break;
            case L2Kind.Sprite:
                hex = true;
                pic = SpritePicture(v);
                break;
            case L2Kind.Hex or L2Kind.Variable or L2Kind.Sound or L2Kind.Battle or L2Kind.Shop: hex = true; break;
        }
        return new L2FieldVm(this, s, p, choices, v, hex, pic);
    }

    private IEnumerable<L2Option> WhoChoices(bool all)
    {
        var list = _who.OrderBy(k => k.Key).Select(k => new L2Option(k.Key, $"{k.Key:X2} {L2EventCommands.Cap(k.Value.Name)}")).ToList();
        // story characters (10-4F) this script names
        if (_script != null)
            foreach (var id in _script.Steps.SelectMany(st => L2EventCommands.Find(st.Op)?.FieldsFor(st.Bytes).Where(f => f.Kind is L2Kind.Who).Select(f => SafeGet(f, st.Bytes)) ?? Enumerable.Empty<int>()).Distinct())
                if (id is >= 0x10 and < 0x50 && list.All(o => o.Value != id)) list.Add(new L2Option(id, $"{id:X2} {L2EventCommands.Cap(_speakers?.Label(id) ?? $"story character {id:X2}")}"));
        if (all) list.Add(new L2Option(0xFF, "FF Everyone"));
        return list;
    }

    private static int SafeGet(L2ParamDef f, byte[] b) { try { return f.Get(b); } catch (Exception) { return -1; } }

    private IEnumerable<L2Option> SceneChoices(int map)
    {
        int block = L2Events.Block(_rom!, map);
        if (block < 0) return Enumerable.Empty<L2Option>();
        return L2EventBlocks.ReadLists(_rom!, block).Where(e => e.Head == L2EventBlocks.SceneList)
            .Select(e => new L2Option(e.Id, $"scene {e.Id}: {Short(FirstText(_rom!, block + e.Offset, block))}"));
    }

    private static string FlagChoice(RomBuffer rom, int f)
    {
        var uses = L2FlagUses.Get(rom).GetValueOrDefault(f);
        string use = f >= 0xF0 ? "cleared on every map change" : uses == null ? "not used by the game" : $"used {uses.Count}x";
        return $"{f:X2}  {use}";
    }

    private void UpdateBytesDump()
    {
        if (_script == null || _rom == null) { BytesDump = ""; return; }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _script.Steps.Count; i++)
        {
            var s = _script.Steps[i];
            string at = s.At >= 0 ? $"{s.At:X6}" : "   new";
            string bytes = Convert.ToHexString(L2EventScript.Encode(_rom, s, 0, s.At >= 0));
            if (bytes.Length > 64) bytes = bytes[..64] + "...";
            sb.AppendLine($"{i + 1,4}. {at}  {bytes,-66}  {(s.IsText ? "\"" + s.Text.Replace("\n", " / ") + "\"" : Sentence(s))}");
        }
        BytesDump = sb.ToString();
    }

    // ------------------------------------------------------------------ editing

    private void Remember()
    {
        if (_script == null) return;
        _undo.Add(_script.TakeSnapshot());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        UpdateUndo();
    }

    private void UpdateUndo()
    {
        CanUndo = _undo.Count > 0; CanRedo = _redo.Count > 0;
        UndoTip = $"Undo ({_undo.Count} step{(_undo.Count == 1 ? "" : "s")}, Ctrl+Z)";
        RedoTip = $"Redo ({_redo.Count} step{(_redo.Count == 1 ? "" : "s")}, Ctrl+Y)";
    }

    /// <summary>After an edit: the cards show the new sentences (every card's fields again when the structure changed),
    /// the stage is simulated again.</summary>
    private void Changed(bool structure, L2LineVm? fieldsOf = null)
    {
        if (_script == null) return;
        _refreshing = true;
        try
        {
            IsDirty = _saved == null || !L2EventScript.Same(_saved, _script.TakeSnapshot());
            Status = IsDirty ? (_event?.IsInit == true || (_script.ReadStarts.Count > 0 && _script.FitsInPlace(_rom!, _script.ReadStarts)) ? "Edited (same size: it will be written in place)." : "Edited (the size changed: the map's events move to the event space when written).") : "";
            if (structure)
            {
                // the cards follow the steps: kept where the step stays, made for new steps, dropped for gone ones
                var byStep = Lines.Where(l => l.Step != null).ToDictionary(l => l.Step!, l => l);
                Lines.Clear();
                for (int i = 0; i < _script.Steps.Count; i++)
                {
                    var s = _script.Steps[i];
                    if (!byStep.TryGetValue(s, out var vm)) vm = new L2LineVm(this, s, i);
                    vm.Index = i; vm.RefreshNumber();
                    Lines.Add(vm);
                }
                foreach (var l in Lines) FillLine(l, fields: true);
            }
            else foreach (var l in Lines) FillLine(l, fields: l == fieldsOf);
            FillStartFlags();
            Simulate();
            Annotate();
            UpdateBytesDump();
            Info = Info.Contains('\n') ? "What it does: " + Summary() + Info[Info.IndexOf('\n')..] : Info;
        }
        finally { _refreshing = false; }
        if (!IsPlaying) ShowStage();
    }

    /// <summary>A field changed (from <see cref="L2FieldVm"/>).</summary>
    public void SetField(L2FieldVm field, int value)
    {
        if (_refreshing || _script == null || !CanEdit) return;
        var s = field.Step;
        var p = field.Def;
        var line = Lines.FirstOrDefault(l => l.Step == s);
        _textLine = null;
        Remember();
        bool rebuild = false;
        if (p.Kind == L2Kind.Jump)
        {
            while (s.Jumps.Count <= p.JumpIndex) s.Jumps.Add(new L2Jump());
            s.Jumps[p.JumpIndex] = new L2Jump { Step = value >= 0 && value < _script.Steps.Count ? _script.Steps[value] : null };
        }
        else if (p.Rebuild != null)
        {
            var (bytes, map) = p.Rebuild(s.Bytes, value);
            var old = s.Jumps.ToList();
            int count = L2EventScript.JumpPositions(bytes).Count;
            var next = Next(s);
            s.Jumps.Clear();
            for (int k = 0; k < count; k++)
            {
                int from = map == null ? (k < old.Count ? k : -1) : k < map.Length ? map[k] : -1;
                s.Jumps.Add(from >= 0 && from < old.Count ? old[from] : new L2Jump { Step = next });
            }
            s.Bytes = bytes;
            rebuild = true;
        }
        else
        {
            var bytes = (byte[])s.Bytes.Clone();
            p.Set(bytes, value);
            s.Bytes = bytes;
            rebuild = L2EventCommands.Find(s.Op)?.ParamsFor != null || p.Kind is L2Kind.Map or L2Kind.Who or L2Kind.Sprite;
        }
        if (rebuild) Dispatch(() => Changed(false, line));
        else Changed(false);
    }

    private L2Step? Next(L2Step s)
    {
        int i = _script!.Steps.IndexOf(s);
        return i + 1 < _script.Steps.Count ? _script.Steps[i + 1] : s;
    }

    public void SetSpeaker(L2LineVm line, int opener)
    {
        if (_refreshing || line.Step is not { IsText: true } s || s.Op == opener || !CanEdit) return;
        _textLine = null;
        Remember();
        s.Bytes = opener switch
        {
            0x13 => new byte[] { 0x13, (byte)(s.Op == 0x13 ? s.Bytes[1] : _who.Keys.FirstOrDefault(k => k >= 0x50, 0x50)) },
            0x9E => new byte[] { 0x9E, 0x05 },
            _ => new byte[] { (byte)opener },
        };
        Dispatch(() => Changed(false, line));
    }

    public void SetText(L2LineVm line, string value)
    {
        if (_refreshing || line.Step is not { IsText: true } s || !CanEdit) return;
        string text = value.Replace("\r", "");
        if (text == s.Text) return;
        if (_textLine != line) { Remember(); _textLine = line; }
        s.Text = text;
        line.SizeText = SizeNote(s);
        Changed(false);
    }

    public void SetEnding(L2LineVm line, int end)
    {
        if (_refreshing || line.Step is not { IsText: true } s || s.End == end || !CanEdit) return;
        _textLine = null;
        Remember();
        s.End = (byte)end;
        Changed(false);
    }

    /// <summary>Where a new step goes: below the picked one, else at the end (above a closing End).</summary>
    public int InsertIndex
    {
        get
        {
            if (_script == null) return 0;
            int at = SelectedLine >= 0 && SelectedLine < _script.Steps.Count ? SelectedLine + 1 : _script.Steps.Count;
            if (at == _script.Steps.Count && at > 0 && _script.Steps[at - 1] is { IsText: false, Op: 0x00 }) at--;
            return at;
        }
    }

    /// <summary>Adds a command from the menu at <paramref name="at"/> (-1: <see cref="InsertIndex"/>).</summary>
    public void AddCommand(L2CommandDef def, int at = -1)
    {
        if (def.Template == null) return;
        if (def.Ops[0] == 0x10) { AddText(new byte[] { 0x10, 0x02, 0, 0, 0, 0 }, "Is that so?\nYes\nNo", 0x0B, at); return; }
        if (L2EventScript.TextOpeners.Contains(def.Template[0])) { AddText(def.Template, "...", 0x01, at); return; }
        Insert(at, new L2Step { Bytes = (byte[])def.Template.Clone() });
    }

    /// <summary>Adds text spoken by the opener's speaker.</summary>
    public void AddText(byte[] opener, string text, byte end, int at = -1) =>
        Insert(at, new L2Step { Bytes = (byte[])opener.Clone(), IsText = true, Text = text, End = end, OriginalEnd = 0xFF, OriginalText = "\u0001" });

    /// <summary>Puts steps into the script at <paramref name="at"/> (-1: below the picked step) and picks the first.</summary>
    public void Insert(int at, params L2Step[] steps)
    {
        if (_script == null || !CanEdit || steps.Length == 0) return;
        if (at < 0) at = InsertIndex;
        at = Math.Clamp(at, 0, _script.Steps.Count);
        _textLine = null;
        Remember();
        _script.Steps.InsertRange(at, steps);
        int after = at + steps.Length;
        var next = after < _script.Steps.Count ? _script.Steps[after] : steps[^1];
        foreach (var step in steps) foreach (int _ in L2EventScript.JumpPositions(step.Bytes)) step.Jumps.Add(new L2Jump { Step = next });
        Changed(true);
        SelectedLine = at;
        HighlightLine(at);
        Status = "Added. " + Status;
    }

    public void Delete(int index)
    {
        if (_script == null || !CanEdit || index < 0 || index >= _script.Steps.Count) return;
        if (_script.Steps.Count == 1) { Status = "An event needs at least one step."; return; }
        var s = _script.Steps[index];
        _textLine = null;
        Remember();
        _script.Steps.RemoveAt(index);
        // the last step taken out while a jump leads to it: an End takes its place (back to the step before would run
        // that step again)
        bool jumped = _script.Steps.Any(st => st.Jumps.Any(j => j.Step == s));
        if (index == _script.Steps.Count && jumped) _script.Steps.Add(new L2Step { Bytes = new byte[] { 0x00 } });
        var instead = index < _script.Steps.Count ? _script.Steps[index] : _script.Steps[index - 1];
        foreach (var st in _script.Steps) foreach (var j in st.Jumps) if (j.Step == s) j.Step = instead;
        Changed(true);
        SelectedLine = Math.Min(index, Lines.Count - 1);
    }

    /// <summary>Moves a step to a new index (jumps keep their targets).</summary>
    public void Move(int from, int to)
    {
        if (_script == null || !CanEdit || from < 0 || from >= _script.Steps.Count) return;
        to = Math.Clamp(to, 0, _script.Steps.Count - 1);
        if (to == from) return;
        _textLine = null;
        Remember();
        var s = _script.Steps[from];
        _script.Steps.RemoveAt(from);
        _script.Steps.Insert(to, s);
        Changed(true);
        SelectedLine = to;
        HighlightLine(to);
    }

    public void Duplicate(int index)
    {
        if (_script == null || !CanEdit || index < 0 || index >= _script.Steps.Count) return;
        var s = _script.Steps[index];
        var c = new L2Step { Bytes = (byte[])s.Bytes.Clone(), IsText = s.IsText, Text = s.Text, End = s.End, OriginalEnd = 0xFF, OriginalText = "\u0001" };
        Insert(index + 1, c);
        for (int k = 0; k < s.Jumps.Count && k < c.Jumps.Count; k++) c.Jumps[k] = new L2Jump { Step = s.Jumps[k].Step };
        Changed(false);
    }

    [RelayCommand]
    public void Undo()
    {
        if (_script == null || _undo.Count == 0) return;
        _redo.Add(_script.TakeSnapshot());
        _script.Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        UpdateUndo();
        _textLine = null;
        Changed(true);
        if (SelectedLine >= Lines.Count) SelectedLine = Lines.Count - 1;
    }

    [RelayCommand]
    public void Redo()
    {
        if (_script == null || _redo.Count == 0) return;
        _undo.Add(_script.TakeSnapshot());
        _script.Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        UpdateUndo();
        _textLine = null;
        Changed(true);
        if (SelectedLine >= Lines.Count) SelectedLine = Lines.Count - 1;
    }

    [RelayCommand]
    public void Revert()
    {
        if (_event == null) return;
        int keep = SelectedLine;
        _undo.Clear(); _redo.Clear(); UpdateUndo();
        ReadScript(keep);
        Status = "Put back as the project has it.";
    }

    /// <summary>Puzzle scripts: writes the picked command's bytes (same size).</summary>
    [RelayCommand]
    private void ApplyPuzzleBytes()
    {
        if (_rom == null || _main == null || SelectedLine < 0 || SelectedLine >= Lines.Count || Lines[SelectedLine].PuzzleOp is not { } op) return;
        byte[] bytes;
        try { bytes = PuzzleBytes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => Convert.ToByte(s, 16)).ToArray(); }
        catch (Exception) { Status = "Not written: write the bytes as hex pairs separated by spaces."; return; }
        if (bytes.Length != op.Length) { Status = $"Not written: it must stay {op.Length} bytes (it has {bytes.Length})."; return; }
        var old = _rom.ReadBytes(op.At, op.Length);
        _rom.WriteBytes(op.At, bytes);
        if (L2Puzzles.Length(_rom, op.At) != op.Length) { _rom.WriteBytes(op.At, old); Status = $"Not written: as a command that would be {L2Puzzles.Length(_rom, op.At)} bytes, not {op.Length}."; return; }
        _main.NotifyRomModified($"Lufia II: puzzle command at {op.At:X6}");
        Status = "Written.";
        Saved?.Invoke("puzzle command");
    }

    partial void OnSelectedLineChanged(int value)
    {
        if (IsPuzzle && value >= 0 && value < Lines.Count && Lines[value].PuzzleOp is { } op && _rom != null)
            PuzzleBytes = string.Join(" ", _rom.ReadBytes(op.At, op.Length).Select(b => b.ToString("X2")));
        _textLine = null;
        if (!IsPlaying) ShowStage();
    }

    public void HighlightLine(int index)
    {
        foreach (var l in Lines) l.IsHighlighted = false;
        if (index < 0 || index >= Lines.Count) return;
        Lines[index].IsHighlighted = true;
        LineHighlighted?.Invoke(index);
    }

    public void ShowFlag(int flag) { if (flag >= 0) FlagRequested?.Invoke(flag); }

    /// <summary>Opens the scene a "go to another map" step starts.</summary>
    public void OpenScene(L2LineVm line)
    {
        if (!line.CanOpenScene) return;
        OpenRequested?.Invoke(line.Step!.Bytes[1], L2EventBlocks.SceneList, line.Step.Bytes[2]);
    }

    private static void Dispatch(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    // ------------------------------------------------------------------ saving

    public bool HasPendingEdits => IsDirty && CanEdit && _script != null && _rom != null;

    public string? WritePendingEdits()
    {
        if (!HasPendingEdits) return null;
        return SaveNow(quiet: true) ? null : $"{Title}: {Status}";
    }

    [RelayCommand]
    public void Save() => SaveNow(quiet: false);

    /// <summary>Writes the event to the ROM; false when it couldn't be.</summary>
    public bool SaveNow(bool quiet)
    {
        if (_rom == null || _main == null || _script == null || _event == null || !CanEdit) return false;
        if (!IsDirty) { Status = "Nothing changed."; return true; }
        // every text must be text the game can show (texts not changed are written as they are: the game's own can hold
        // codes typed text can't)
        for (int i = 0; i < _script.Steps.Count; i++)
            if (_script.Steps[i].TextChanged && L2EventText.Check(_script.Steps[i].Text, _rom) is { } p) { Status = $"Not saved: step {i + 1}: {p}"; return false; }
        // the last step must end the event, or the game runs on into whatever follows
        bool added = false;
        if (!_script.Steps[^1].EndsFlow) { _script.Steps.Add(new L2Step { Bytes = new byte[] { 0x00 } }); added = true; }
        try
        {
            string result = _event.IsInit ? L2EventBlocks.SaveInit(_rom, _map, _script) : L2EventBlocks.Save(_rom, _map, _event.Entry!, _script);
            _main.NotifyRomModified($"Lufia II: {MapName} event \"{_event.Title}\"");
            L2FlagUses.Invalidate();
            int keep = SelectedLine;
            IsDirty = false;
            // the entry may have moved: the map's lists are read again and the same event opened
            var entry = _event.Entry;
            bool init = _event.IsInit;
            ReadMap();
            var items = ItemsOf(_rom, _map);
            _event = init ? items[0] : items.FirstOrDefault(x => x.Entry?.Head == entry!.Head && x.Entry?.Id == entry.Id) ?? _event;
            ReadScript(keep);
            Status = "Saved in the project. " + result + (added ? " An End step was added at the bottom." : "");
            Saved?.Invoke(result);
            return true;
        }
        catch (Exception ex)
        {
            if (added) _script.Steps.RemoveAt(_script.Steps.Count - 1);
            Status = "Not saved: " + ex.Message;
            return false;
        }
    }

    /// <summary>Before the editor shows another event or closes: the edits are written by themselves; when that fails,
    /// asks whether to drop them. True = go on.</summary>
    public bool ConfirmLeave()
    {
        if (!HasPendingEdits) return true;
        if (SaveNow(quiet: true)) return true;
        return System.Windows.MessageBox.Show($"The changes to \"{_event?.Title}\" couldn't be written ({Status}). Drop them?", "Lufia II events",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
    }

    /// <summary>Adds steps at the end of the open event (the "continue another event" choice of a new event): every
    /// place where it ends goes on with them instead.</summary>
    public void ContinueWith(IEnumerable<L2Step> added)
    {
        if (_script == null || !CanEdit) return;
        _textLine = null;
        Remember();
        var steps = added.ToList();
        var first = steps[0];
        // an End at the very bottom just goes: the new steps follow it
        if (_script.Steps[^1] is { IsText: false, Op: 0x00 or 0x42 } bottom && _script.Steps.Count > 1)
        {
            _script.Steps.Remove(bottom);
            foreach (var st in _script.Steps) foreach (var j in st.Jumps) if (j.Step == bottom) j.Step = first;
        }
        foreach (var s in _script.Steps.ToList())
        {
            if (s.IsText && s.End == 0x00) { s.End = 0x01; var go = new L2Step { Bytes = new byte[] { 0x1C, 0, 0 } }; go.Jumps.Add(new L2Jump { Step = first }); _script.Steps.Insert(_script.Steps.IndexOf(s) + 1, go); }
            else if (!s.IsText && s.Op is 0x00 or 0x42) { s.Bytes = new byte[] { 0x1C, 0, 0 }; s.Jumps.Clear(); s.Jumps.Add(new L2Jump { Step = first }); }
        }
        _script.Steps.AddRange(steps);
        Changed(true);
        SelectedLine = _script.Steps.IndexOf(first);
        HighlightLine(SelectedLine);
        int stuck = _script.Steps.Count(s => !s.IsText && s.Op is 0x18 or 0x19 or 0x81 or 0xBC or 0x16 or 0x5E or 0x83 or 0xB8);
        Status = "New steps added at the end; every place where the event ended now goes on with them." +
                 (stuck > 0 ? $" {stuck} place{(stuck == 1 ? "" : "s")} where it opens a shop, the church, a capsule monster or goes to another map still end{(stuck == 1 ? "s" : "")} it: the new steps can't follow those." : "");
    }

    // ------------------------------------------------------------------ the stage

    [ObservableProperty] private BitmapSource? _mapImage;
    public ObservableCollection<L2StageActor> Actors { get; } = new();
    [ObservableProperty] private double _cameraLeft, _cameraTop;
    [ObservableProperty] private bool _showCamera = true;
    [ObservableProperty] private string _speaker = "";
    [ObservableProperty] private string _speech = "";
    [ObservableProperty] private bool _hasSpeech;
    [ObservableProperty] private double _darkness;
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private string _flagsNow = "";
    [ObservableProperty] private string _stageInfo = "";
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private double _warnLeft, _warnTop;
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private double _zoom = 1.5;
    [ObservableProperty] private bool _isPlaying;
    public ObservableCollection<L2StartFlagVm> StartFlags { get; } = new();
    [ObservableProperty] private bool _hasStartFlags;
    public List<L2Option> AnswerChoices { get; } = Enumerable.Range(0, 4).Select(i => new L2Option(i, $"answer {i + 1}")).ToList();
    [ObservableProperty] private L2Option? _playAnswer;
    [ObservableProperty] private bool _hasQuestion;
    [ObservableProperty] private string _flowNote = "";
    public ObservableCollection<L2Option> PointChoices { get; } = new();
    [ObservableProperty] private L2Option? _testPoint;
    [ObservableProperty] private string _testNeeds = "";
    [ObservableProperty] private string _testNote = "";
    public List<L2TimelineItem> TimelineItems { get; private set; } = new();

    private List<L2StageState> _states = new();
    private List<L2Cutscene.RunStep> _run = new();
    private readonly Dictionary<int, int> _firstVisit = new();
    private L2StageState? _start;
    private readonly HashSet<int> _flagsOn = new();
    private readonly Dictionary<int, int> _countersAtStart = new();
    private (int X, int Y)? _startOverride;
    private int _picked = -1;
    private readonly DispatcherTimer _timer = new();
    private int _playAt = -1;

    partial void OnZoomChanged(double value) { if (value < 0.5) Zoom = 0.5; if (value > 6) Zoom = 6; }
    partial void OnShowCameraChanged(bool value) { if (!IsPlaying) ShowStage(); }
    partial void OnPlayAnswerChanged(L2Option? value) { if (_script != null && !_refreshing) { Stop(); Simulate(); Annotate(); ShowStage(); } }

    /// <summary>Where the leader stands as the event starts: the arrival point of a scene, the area of a stepping-on
    /// event, in front of the character talked to, or where the user put the party.</summary>
    private (int X, int Y) LeaderStart()
    {
        if (_startOverride is { } o) return o;
        var rom = _rom!;
        var e = _event?.Entry;
        if (e?.Head == L2EventBlocks.SceneList)
        {
            int p = L2Cutscene.Starter(rom, _map, e.Id)?.Point ?? -1;
            var (m, s, np) = L2MapEdit.NewGame(rom);
            if (p < 0 && m == _map && s == e.Id) p = np;
            var pt = _pointSpots.FirstOrDefault(x => x.Id == p);
            if (pt != default) return (pt.X, pt.Y);
        }
        if (e?.Head == L2EventBlocks.TriggerList && L2MapInfo.Records(rom, _map, L2MapInfo.Triggers, 5).FirstOrDefault(r => r.Bytes[0] == e.Id) is { Bytes: var t } && t != null)
            return (t[1], t[2]);
        if (e?.Head == L2EventBlocks.TalkList && L2MapInfo.Records(rom, _map, L2MapInfo.Characters, 8).FirstOrDefault(r => 0x4F + r.Bytes[0] == e.Id) is { Bytes: var c } && c != null)
            return (c[1], c[2] + 1);
        if (TestPoint != null && _pointSpots.FirstOrDefault(x => x.Id == TestPoint.Value) is var tp && tp != default) return (tp.X, tp.Y);
        return _pointSpots.Count > 0 ? (_pointSpots[0].X, _pointSpots[0].Y) : (8, 8);
    }

    private void Simulate()
    {
        if (_rom == null || _map < 0) { _states = new(); _run = new(); _firstVisit.Clear(); _start = null; TimelineItems = new(); Resimulated?.Invoke(); return; }
        var (x, y) = LeaderStart();
        var start = L2Cutscene.Initial(_rom, _map, x, y, _persistent);
        start.Flags.UnionWith(_flagsOn);
        foreach (var kv in _countersAtStart) start.Counters[kv.Key] = kv.Value;
        int talkedTo = _event?.Entry is { Head: L2EventBlocks.TalkList } te ? te.Id : -1;
        start.Talker = start.TalkSave = talkedTo >= 0 ? talkedTo : 0xFF;
        _start = start;
        var steps = _script?.Steps ?? new List<L2Step>();
        try { _spoken = _script != null && _speakers != null ? _speakers.ForScript(_script, talkedTo) : new(); } catch (Exception) { _spoken = new(); }
        _states = L2Cutscene.Simulate(_rom, _map, start, steps, Name);
        _run = L2Cutscene.Run(_rom, _map, start, steps, Name, PlayAnswer?.Value ?? 0);
        _firstVisit.Clear();
        for (int k = 0; k < _run.Count; k++) _firstVisit.TryAdd(_run[k].Step, k);
        BuildTimeline();
        Resimulated?.Invoke();
    }

    /// <summary>The stage after step <paramref name="i"/>: as playback first reaches it, or (a step playback doesn't
    /// reach with these flags) read top to bottom.</summary>
    private L2StageState? After(int i) =>
        _firstVisit.TryGetValue(i, out int k) ? _run[k].After : i + 1 < _states.Count && i >= 0 ? _states[i + 1] : null;

    private L2StageState? Before(int i) =>
        _firstVisit.TryGetValue(i, out int k) ? (k > 0 ? _run[k - 1].After : _start) : i >= 0 && i < _states.Count ? _states[i] : null;

    private string BranchText(L2StageState s) =>
        s.BranchWhy + (s.Jumped
            ? s.BranchTo >= 0 ? $": goes to step {s.BranchTo + 1}" : ": goes to a step outside this event (playback stops)"
            : s.BranchTo >= 0 ? $": goes on to step {s.BranchTo + 1}" : ": the event ends");

    /// <summary>Marks each card with what playback does there: a check's branch, steps it doesn't reach, walks the game stops.</summary>
    private void Annotate()
    {
        if (_script == null) return;
        for (int i = 0; i < Lines.Count && i < _script.Steps.Count; i++)
        {
            var vm = Lines[i];
            bool reached = _firstVisit.ContainsKey(i);
            vm.Reached = reached || _run.Count == 0;
            vm.Warning = After(i)?.Warning ?? "";
            string flow = "";
            var st = _script.Steps[i];
            if ((st.Jumps.Count > 0 || st.Op is 0x14 && !st.IsText) && _firstVisit.TryGetValue(i, out int k) && _run[k].After.BranchWhy != null)
            {
                int times = _run.Count(r => r.Step == i);
                flow = "Playback: " + BranchText(_run[k].After) + (times > 1 ? $" (the first of {times} times)" : "");
            }
            vm.Flow = !reached && _run.Count > 0 ? "Playback doesn't reach this step with these story flags." : flow;
        }
        FlowNote = _run.Count == 0 ? "" :
            $"Playback runs {Lines.Count(l => l.Reached)} of the {Lines.Count} steps" + (_run.Count >= 3000 ? " (it goes round a loop: it stops after 3000 commands)." : ".");
    }

    private void BuildTimeline()
    {
        var items = new List<L2TimelineItem>();
        var steps = _script?.Steps;
        if (steps == null) { TimelineItems = items; return; }
        int at = 0;
        for (int i = 0; i < steps.Count && i + 1 < _states.Count; i++)
        {
            var s = steps[i]; var st = _states[i + 1];
            int frames = Math.Max(1, s.IsText ? 60 + s.Text.Length * 2 : st.Frames);
            string track, kind;
            if (s.IsText) { track = "Dialogue"; kind = "dialogue"; }
            else switch (s.Op)
            {
                case 0x2D or 0x2E or 0x2F or 0x30 or 0x31 or 0x7A or 0x98 or 0x99 or 0x9D or 0xA3 or 0x89 or 0x33 when s.Bytes.Length >= 2:
                    int who = s.Op == 0x33 && s.Bytes.Length >= 3 ? s.Bytes[2] : s.Bytes[1];
                    track = L2EventCommands.Cap(Name(who)); kind = s.Op is 0xA3 or 0x33 ? "walk" : "character"; break;
                case 0x3A or 0x3B or 0x41 or 0x75 or 0xC4: track = "Camera"; kind = "camera"; break;
                case 0x58 or 0x59 or 0x3E or 0x3F or 0x5A or 0x8A or 0x56 or 0xA4 or 0x76: track = "Screen"; kind = "screen"; break;
                case 0x6F or 0x4B or 0x72 or 0x4C: track = "Music and sound"; kind = "sound"; break;
                case 0x1A or 0x1B or 0x1D or 0x1E or 0x1F: track = "Story flags"; kind = "flag"; break;
                case 0x37 or 0x38: track = "Timing"; kind = "wait"; break;
                case 0x14 or 0x15 or 0x6A or 0x1C or 0x12 or 0x10: track = "Checks and jumps"; kind = "flow"; break;
                default: track = "Other"; kind = "other"; break;
            }
            string label = s.IsText ? (st.Speaker?.Length > 0 ? st.Speaker + ": " : "") + s.Text.Replace("\n", " ") : st.Caption.Length > 0 ? st.Caption : Sentence(s);
            bool reached = _run.Count == 0 || _firstVisit.ContainsKey(i);
            items.Add(new L2TimelineItem(i, track, kind, label, at, frames, reached));
            if (s.Op != 0x89 || s.IsText) at += frames;
        }
        TimelineItems = items;
    }

    /// <summary>The stage after the picked step (before the first one when nothing is picked), and the stage before it.</summary>
    private (L2StageState? After, L2StageState? Before) Picked()
    {
        int i = SelectedLine;
        if (_script == null || i < 0 || i >= _script.Steps.Count) return (_start, null);
        return (After(i), Before(i));
    }

    public void ShowStage()
    {
        var (after, before) = Picked();
        ShowState(after, before);
        ShowWarning(after);
    }

    private void ShowWarning(L2StageState? s)
    {
        HasWarning = s?.Warning != null;
        WarningText = s?.Warning ?? "";
        if (s?.Warning != null) { WarnLeft = s.WarnX * 16; WarnTop = s.WarnY * 16; }
    }

    /// <summary>Draws a stage. <paramref name="t"/> below 1 draws the characters that moved since <paramref name="before"/>
    /// part of the way there, in a walking frame (<paramref name="phase"/>).</summary>
    private void ShowState(L2StageState? s, L2StageState? before, double t = 1, int phase = 0)
    {
        Actors.Clear();
        if (s == null || _rom == null) { HasSpeech = false; Caption = ""; FlagsNow = ""; return; }
        foreach (var a in s.Actors.Values.Where(a => a.Placed).OrderBy(a => a.Y))
        {
            if (!a.Visible && a.Id != 0 && !_cast.Contains(a.Id)) continue;
            int sprite = a.Sprite >= 0 ? a.Sprite : a.Id < 7 ? a.Id : 0x0C;
            var o = before != null && before.Actors.TryGetValue(a.Id, out var oo) ? oo : null;
            double x = a.X, y = a.Y;
            bool walking = t < 1 && o != null && o.Placed && o.Visible && a.Visible && (o.X != a.X || o.Y != a.Y) && Math.Abs(o.X - a.X) + Math.Abs(o.Y - a.Y) <= 6;
            if (walking) { x = o!.X + (a.X - o.X) * t; y = o.Y + (a.Y - o.Y) * t; }
            var pic = SpritePicture(sprite, a.Dir, walking ? phase : 0);
            double w = pic?.PixelWidth ?? 16, h = pic?.PixelHeight ?? 32;
            bool moved = o != null && (o.X != a.X || o.Y != a.Y || o.Visible != a.Visible || o.Dir != a.Dir);
            string name = L2EventCommands.Cap(Name(a.Id));
            Actors.Add(new L2StageActor
            {
                Id = a.Id, X = a.X, Y = a.Y, Name = name,
                Left = x * 16 + 8 - w / 2, Top = y * 16 + 16 - h, Picture = pic,
                Label = a.Id == 0 || _cast.Contains(a.Id) || moved || a.Id == _picked ? name.Replace("Cutscene character", "cutscene").Replace("Character", "char") : "",
                Opacity = a.Visible ? 1 : 0.3, Moved = moved && t >= 1, Picked = a.Id == _picked,
                // sprites with a picture for each facing show it; others get a small arrow
                Facing = L2Cutscene.HasFacings(_rom, sprite) ? "" : new[] { "^", "v", "<", ">" }[a.Dir & 3],
            });
        }
        CameraLeft = s.CameraX * 16 + 8 - 128; CameraTop = s.CameraY * 16 + 8 - 112;
        HasSpeech = s.Text != null;
        Speaker = s.Speaker ?? "";
        Speech = s.Text ?? "";
        Darkness = s.Dark ? 0.6 : 0;
        Caption = s.Caption;
        FlagsNow = (s.Flags.Count > 0 ? "Story flags on: " + string.Join(" ", s.Flags.OrderBy(f => f).Select(f => f.ToString("X2"))) : "No story flag on")
                   + (s.Counters.Count > 0 ? "; " + string.Join(", ", s.Counters.OrderBy(c => c.Key).Select(c => $"counter {c.Key:X2} = {c.Value}")) : "")
                   + " (at this step, in playback)";
        ScrollRequested?.Invoke(s.CameraX * 16 + 8, s.CameraY * 16 + 8);
    }

    private static BitmapSource? Picture((int Width, int Height, uint[] Pixels)? pic)
    {
        if (pic is not var (w, h, px)) return null;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
        bmp.Freeze();
        return bmp;
    }

    public BitmapSource? SpritePicture(int sprite)
    {
        if (_rom == null || sprite < 0) return null;
        if (_spritePics.TryGetValue(sprite, out var p)) return p;
        BitmapSource? bmp = null;
        try { bmp = Picture(L2Graphics.MapSprite(_rom, sprite)); } catch (Exception) { }
        return _spritePics[sprite] = bmp;
    }

    /// <summary>A sprite facing a way (0 up, 1 down, 2 left, 3 right), in a walking frame (phase 1) or standing.</summary>
    public BitmapSource? SpritePicture(int sprite, int dir, int phase)
    {
        if (_rom == null || sprite < 0) return null;
        var (frame, flip) = L2Cutscene.FacingFrame(_rom, sprite, dir, phase);
        if (frame == 0 && !flip) return SpritePicture(sprite);
        if (_facingPics.TryGetValue((sprite, frame, flip ? 1 : 0), out var p)) return p;
        BitmapSource? bmp = null;
        try
        {
            if (L2Graphics.MapSprite(_rom, sprite, frame) is var (w, h, px))
            {
                if (flip) for (int y = 0; y < h; y++) Array.Reverse(px, y * w, w);
                bmp = Picture((w, h, px));
            }
        }
        catch (Exception) { }
        return _facingPics[(sprite, frame, flip ? 1 : 0)] = bmp;
    }

    /// <summary>The story flags and counters the script checks, ticked as they are set for playback.</summary>
    private void FillStartFlags()
    {
        StartFlags.Clear();
        if (_script == null) { HasStartFlags = false; HasQuestion = false; return; }
        var (flags, counters) = L2Cutscene.Checked(_script.Steps);
        int once = L2Cutscene.OnceFlag(_script);
        foreach (int f in flags)
            StartFlags.Add(new L2StartFlagVm { Number = f, IsOn = _flagsOn.Contains(f), Label = $"Story flag {f:X2} is on" + (f == once ? " (the event has played)" : ""), Changed = StartChanged });
        foreach (int c in counters)
            StartFlags.Add(new L2StartFlagVm { Number = c, IsCounter = true, Value = _countersAtStart.TryGetValue(c, out int v) ? v : 0, Label = $"Counter {c:X2} is", Changed = StartChanged });
        HasStartFlags = StartFlags.Count > 0;
        HasQuestion = _script.Steps.Any(s => s.IsText && s.Op == 0x10);
    }

    private void StartChanged()
    {
        if (_refreshing) return;
        foreach (var f in StartFlags)
        {
            if (f.IsCounter) { if (f.Value != 0) _countersAtStart[f.Number] = Math.Clamp(f.Value, 0, 255); else _countersAtStart.Remove(f.Number); }
            else if (f.IsOn) _flagsOn.Add(f.Number); else _flagsOn.Remove(f.Number);
        }
        Stop();
        Simulate(); Annotate(); ShowStage();
    }

    // ------------------------------------------------------------------ the stage: clicks

    /// <summary>The characters on the stage before the step a click would add (after the picked step).</summary>
    private L2StageState? StateForInsert()
    {
        int i = SelectedLine;
        if (_script == null || i < 0 || i >= _script.Steps.Count) return _start;
        return After(i) ?? _start;
    }

    /// <summary>The character picked on the stage (-1: none).</summary>
    public int PickedActor => _picked;

    /// <summary>A click on a tile of the stage (map pixels). Returns true when it was taken (a character picked or let
    /// go, the picked step moved); false: the view shows the menu of what can happen there.</summary>
    public bool StageClick(double px, double py)
    {
        int x = (int)(px / 16), y = (int)(py / 16);
        if (_script == null || !CanEdit || IsPlaying) { StageInfo = $"Tile ({x}, {y})."; return true; }
        var st = StateForInsert();
        // a character under the cursor: pick it
        var hit = st?.Actors.Values.Where(a => a.Placed && (a.Visible || _cast.Contains(a.Id)) && a.X == x && (a.Y == y || a.Y == y + 1))
                     .OrderBy(a => a.Y == y ? 0 : 1).ThenBy(a => a.Id).FirstOrDefault();
        if (hit != null && hit.Id != _picked)
        {
            _picked = hit.Id;
            ShowStage();
            StageInfo = $"{L2EventCommands.Cap(Name(hit.Id))} picked. Click a tile: walk there, appear there, face that way or point the camera there. Click it again (or Esc) to let go.";
            return true;
        }
        if (hit != null && hit.Id == _picked) { _picked = -1; ShowStage(); StageInfo = ""; return true; }
        // the picked step has a tile: move it here (unless a character is picked)
        if (_picked < 0 && SelectedLine >= 0 && SelectedLine < Lines.Count && Lines[SelectedLine] is { Step: { IsText: false } s } line)
        {
            var fx = line.Fields.FirstOrDefault(f => f.Label == "X"); var fy = line.Fields.FirstOrDefault(f => f.Label == "Y");
            if (fx != null && fy != null)
            {
                var bytes = (byte[])s.Bytes.Clone();
                fx.Def.Set(bytes, x); fy.Def.Set(bytes, y);
                if (!bytes.AsSpan().SequenceEqual(s.Bytes))
                {
                    _textLine = null;
                    Remember();
                    s.Bytes = bytes;
                    Changed(false, line);
                    StageInfo = $"Step {SelectedLine + 1} now points at ({x}, {y}).";
                }
                return true;
            }
        }
        return false;
    }

    public void LetGo() { if (_picked >= 0) { _picked = -1; ShowStage(); StageInfo = ""; } }

    /// <summary>What can happen at a tile: the picked character (or any character) walks / appears / turns there, the
    /// camera jumps there, the party starts there. Group "" = a top-level entry; else the character's sub-menu.</summary>
    public List<L2StageAction> ActionsAt(int x, int y)
    {
        var list = new List<L2StageAction>();
        var st = StateForInsert();
        if (st == null || _script == null || !CanEdit) return list;
        IEnumerable<L2StageState.Actor> who = _picked >= 0 && st.Actors.TryGetValue(_picked, out var pa) ? new[] { pa }
            : st.Actors.Values.Where(a => a.Id == 0 || a.Id >= 0x10 && (a.Placed || _cast.Contains(a.Id))).OrderBy(a => a.Id == 0 ? -1 : a.Id);
        foreach (var a in who)
        {
            var actor = a;
            string n = L2EventCommands.Cap(Name(actor.Id));
            string g = _picked >= 0 ? "" : n + (actor.Id >= 0x50 ? "" : actor.Id == 0 ? " (the leader)" : "");
            string pre = _picked >= 0 ? n + " " : "";
            list.Add(new L2StageAction(g, $"🚶 {pre}walk{(_picked >= 0 ? "s" : "")} here", () => AddWalk(actor.Id, x, y)));
            list.Add(new L2StageAction(g, $"✨ {pre}appear{(_picked >= 0 ? "s" : "")} here", () => AddPlace(actor.Id, x, y)));
            list.Add(new L2StageAction(g, $"👀 {pre}turn{(_picked >= 0 ? "s" : "")} to face this tile", () => AddFace(actor.Id, x, y)));
            if (actor.Visible) list.Add(new L2StageAction(g, $"💨 {pre}leave{(_picked >= 0 ? "s" : "")}", () => Insert(-1, new L2Step { Bytes = new byte[] { 0x2E, (byte)actor.Id } })));
        }
        list.Add(new L2StageAction("", "🎥 Camera jumps here", () => Insert(-1, new L2Step { Bytes = new byte[] { 0x3A, (byte)x, (byte)y } })));
        list.Add(new L2StageAction("", "🎥 Camera scrolls here", () => Insert(-1, new L2Step { Bytes = new byte[] { 0xC4, (byte)x, (byte)y } }, new L2Step { Bytes = new byte[] { 0x41, 0x00, 0xFF } })));
        list.Add(new L2StageAction("", "📍 The party starts the event here", () => SetPartyStart(x, y)));
        return list;
    }

    private static readonly int[] FaceFrame = L2Beat.FaceFrame;

    /// <summary>Walks a character to a tile: sideways first, then up or down, one step and one wait per tile.</summary>
    public void AddWalk(int id, int x, int y)
    {
        var st = StateForInsert();
        if (st == null || !st.Actors.TryGetValue(id, out var a) || !a.Placed) { StageInfo = $"{L2EventCommands.Cap(Name(id))} isn't on the map yet: make it appear first."; return; }
        if (a.X == x && a.Y == y) { StageInfo = $"{L2EventCommands.Cap(Name(id))} is already there."; return; }
        var steps = new List<L2Step>();
        void Leg(int dir, int n) { for (int i = 0; i < n; i++) { steps.Add(new L2Step { Bytes = new byte[] { 0xA3, (byte)id, (byte)dir } }); steps.Add(new L2Step { Bytes = new byte[] { 0x89, (byte)id } }); } }
        if (x != a.X) Leg(x > a.X ? 3 : 2, Math.Abs(x - a.X));
        if (y != a.Y) Leg(y > a.Y ? 1 : 0, Math.Abs(y - a.Y));
        Insert(-1, steps.ToArray());
        StageInfo = $"{L2EventCommands.Cap(Name(id))} walks to ({x}, {y}) (sideways first, then up or down), one step and one wait a tile. Walls and other characters stop a walk in the game: a red box shows where.";
    }

    public void AddPlace(int id, int x, int y)
    {
        var st = StateForInsert();
        bool hidden = st == null || !st.Actors.TryGetValue(id, out var a) || !a.Visible;
        var steps = new List<L2Step>();
        if (hidden) steps.Add(new L2Step { Bytes = new byte[] { 0x2D, (byte)id } });
        steps.Add(new L2Step { Bytes = new byte[] { 0x2F, (byte)id, (byte)x, (byte)y } });
        Insert(-1, steps.ToArray());
        StageInfo = $"{L2EventCommands.Cap(Name(id))} appears on ({x}, {y}).";
    }

    public void AddFace(int id, int x, int y)
    {
        var st = StateForInsert();
        if (st == null || !st.Actors.TryGetValue(id, out var a) || !a.Placed) { StageInfo = $"{L2EventCommands.Cap(Name(id))} isn't on the map yet."; return; }
        int dx = x - a.X, dy = y - a.Y;
        int dir = Math.Abs(dx) > Math.Abs(dy) ? (dx < 0 ? 2 : 3) : (dy < 0 ? 0 : 1);
        Insert(-1, new L2Step { Bytes = new byte[] { 0x7A, (byte)id, (byte)FaceFrame[dir] } });
        StageInfo = $"{L2EventCommands.Cap(Name(id))} faces {L2Beat.DirNames[dir]}.";
    }

    public void SetPartyStart(int x, int y)
    {
        _startOverride = (x, y);
        Stop();
        Simulate(); Annotate(); ShowStage();
        StageInfo = $"The party starts the event on ({x}, {y}) (on the stage only; the game puts it where the event starts).";
    }

    /// <summary>A character only cutscenes use: made with the map (hidden), shown by "appears". Returns its id or -1.</summary>
    public int AddCastMember(int sprite, int x, int y)
    {
        if (_rom == null || _main == null || _map < 0) return -1;
        // the map's events may move when the character is hidden in its "when shown" event: unsaved steps are saved first
        if (HasPendingEdits && !SaveNow(quiet: true)) return -1;
        int guard = _script != null && _event?.Entry != null && _event.Entry.Head != L2EventBlocks.SceneList ? L2Cutscene.OnceFlag(_script) : -1;
        try
        {
            int id = L2Cutscene.AddCast(_rom, _map, sprite, guard, x, y);
            _main.NotifyRomModified($"Lufia II: cutscene character {id - 0x4F} on map {_map:X2}");
            int keep = SelectedLine;
            ReadMap();
            var items = ItemsOf(_rom, _map);
            if (_event != null) _event = _event.IsInit ? items[0] : items.FirstOrDefault(x2 => x2.Entry?.Head == _event.Entry?.Head && x2.Entry?.Id == _event.Entry?.Id) ?? _event;
            ReadScript(keep);
            StageInfo = $"Cutscene character {id - 0x4F} ({L2Sprites.Name(sprite)}) is made with the map, hidden until a step makes it appear" +
                        (guard >= 0 ? $", and no longer once story flag {guard:X2} is on (the event has played)." : ".") + " Click a tile and pick \"appears here\" to show it.";
            Saved?.Invoke("cutscene character");
            return id;
        }
        catch (Exception ex) { StageInfo = "Not added: " + ex.Message; return -1; }
    }

    // ------------------------------------------------------------------ playback

    [RelayCommand]
    public void Play()
    {
        if (_script == null || _run.Count == 0 || _start == null) { Status = "Nothing to play."; return; }
        if (IsPlaying) { Stop(); return; }
        IsPlaying = true;
        // from the picked step on (the start when none, or the first step is picked)
        _playAt = SelectedLine > 0 && _firstVisit.TryGetValue(SelectedLine, out int k) ? k - 1 : -1;
        _anim = null;
        ShowState(_playAt >= 0 ? _run[_playAt].After : _start, null);
        ShowWarning(null);
        _timer.Interval = TimeSpan.FromMilliseconds(300);
        _timer.Start();
        Status = "Playing (roughly as the game does, following checks and jumps with the story flags ticked above). Click Stop to stop.";
    }

    /// <summary>A round of walking being drawn: from, to, how many pictures, which one.</summary>
    private (L2StageState From, L2StageState To, int Count, int At)? _anim;

    private bool IsStepCommand(int runIndex, bool waits)
    {
        if (_script == null || runIndex < 0 || runIndex >= _run.Count) return false;
        var st = _script.Steps[_run[runIndex].Step];
        if (st.IsText) return false;
        return waits ? st.Op == 0x89 : st.Op == 0xA3 && st.Bytes.Length >= 3 && _run[runIndex].After.MovedId >= 0;
    }

    private void PlayTick()
    {
        _timer.Stop();
        if (!IsPlaying || _script == null || _start == null) return;
        if (_anim is { } an)
        {
            int k = an.At + 1;
            if (k < an.Count)
            {
                _anim = (an.From, an.To, an.Count, k);
                ShowState(an.To, an.From, (double)k / an.Count, k / 2 % 2 == 0 ? 1 : 0);
                _timer.Interval = TimeSpan.FromMilliseconds(4000.0 / 60);
                _timer.Start();
                return;
            }
            _anim = null;
            ShowState(an.To, an.From);
            _timer.Interval = TimeSpan.FromMilliseconds(30);
            _timer.Start();
            return;
        }
        _playAt++;
        if (_playAt >= _run.Count) { Stop(); Status = "The end." + (_run.Count >= 3000 ? " (Playback went round a loop and stopped.)" : ""); return; }
        var before = _playAt > 0 ? _run[_playAt - 1].After : _start;
        // the steps that start walking (A3) and the waits for them (89) are one round: everyone walks at once
        int end = _playAt;
        if (IsStepCommand(_playAt, false))
        {
            while (IsStepCommand(end + 1, false)) end++;
            while (IsStepCommand(end + 1, true)) end++;
        }
        var s = _run[end].After;
        int line = _run[end].Step;
        if (line != SelectedLine) { _refreshing = true; SelectedLine = line; _refreshing = false; HighlightLine(line); }
        PlaybackProgress?.Invoke(line, 0);
        string? warn = null;
        for (int k = _playAt; k <= end; k++) if (_run[k].After.Warning != null) { warn = _run[k].After.Warning; ShowWarning(_run[k].After); }
        if (warn == null) ShowWarning(null);
        else Status = warn;
        if (end > _playAt || IsStepCommand(_playAt, false))
        {
            int frames = Math.Max(8, Enumerable.Range(_playAt, end - _playAt + 1).Max(k => _run[k].After.Frames));
            _playAt = end;
            _anim = (before, s, Math.Max(2, frames / 4), 0);
            ShowState(s, before, 0, 1);
            _timer.Interval = TimeSpan.FromMilliseconds(4000.0 / 60);
            _timer.Start();
            return;
        }
        ShowState(s, before);
        if (s.BranchWhy != null) Status = "Playback: " + BranchText(s);
        double ms = s.Text != null ? 1600 + s.Text.Length * 40 : Math.Max(30, s.Frames * 1000.0 / 60);
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Min(ms, 5000));
        _timer.Start();
    }

    [RelayCommand]
    public void Stop()
    {
        if (!IsPlaying) return;
        _timer.Stop();
        _anim = null;
        IsPlaying = false;
        ShowStage();
    }

    // ------------------------------------------------------------------ test in the game

    private string NewGameNote()
    {
        if (_rom == null) return "";
        var (m, s, p) = L2MapEdit.NewGame(_rom);
        return m == 0x02 && s == 0x01 && p == 0x06 ? "A new game starts as in the game (the opening)." : $"A new game starts on map {m:X2} {_maps.ElementAtOrDefault(m)} at point {p} (scene {s:X2}).";
    }

    [RelayCommand]
    public void TestInGame()
    {
        if (_rom == null || _main == null || _event?.Entry == null) { Status = "Pick an event of the map first (the start-up script can't be tested this way)."; return; }
        if (HasPendingEdits && !SaveNow(quiet: true)) return;
        if (TestPoint == null) { Status = "The map has no preset point for the party to start on: add one on the Maps page."; return; }
        try
        {
            var flags = StartFlags.Where(f => !f.IsCounter && f.IsOn).Select(f => f.Number).ToList();
            var counters = StartFlags.Where(f => f.IsCounter && f.Value != 0).ToDictionary(f => f.Number, f => Math.Clamp(f.Value, 0, 255));
            string set = L2Cutscene.SetTest(_rom, _map, _event.Entry.Head, _event.Entry.Id, TestPoint.Value, flags, counters);
            _main.NotifyRomModified($"Lufia II: new game starts with an event on map {_map:X2}");
            Status = set + " " + _main.PlayInBizHawk();
        }
        catch (Exception ex) { Status = "Not set: " + ex.Message; }
        TestNote = NewGameNote();
    }

    [RelayCommand]
    public void NormalStart()
    {
        if (_rom == null || _main == null) return;
        L2MapEdit.SetNormalStart(_rom);
        _main.NotifyRomModified("Lufia II: normal new-game start");
        TestNote = NewGameNote();
        Status = "A new game starts as in the game again.";
    }
}
