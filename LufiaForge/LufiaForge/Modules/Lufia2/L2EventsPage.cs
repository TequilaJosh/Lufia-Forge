using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>An event of the map in the list: what starts it and where its script is.</summary>
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

/// <summary>A step in the list: its number, what it does in words, and notes (jumps to it, shared code).</summary>
public partial class L2StepVm : ObservableObject
{
    public L2Step? Step { get; init; }
    public L2Script.Op? PuzzleOp { get; init; }
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _sentence = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _note = "";
    public bool HasDetail => Detail.Length > 0;
    public bool HasNote => Note.Length > 0;
    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
}

/// <summary>A field of the picked step: a drop-down (who, item, map, jump target...) or a number.</summary>
public partial class L2FieldVm : ObservableObject
{
    private readonly L2EventsPage _page;
    private bool _loading;
    public L2ParamDef Def { get; }
    public string Label { get; }
    public string Hint { get; }
    public bool IsChoice { get; }
    public bool IsNumber => !IsChoice;
    public bool HasHint => Hint.Length > 0;
    public List<L2Option> Choices { get; } = new();
    [ObservableProperty] private L2Option? _selected;
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private BitmapSource? _picture;
    [ObservableProperty] private string _error = "";
    private readonly bool _hex;

    public L2FieldVm(L2EventsPage page, L2ParamDef def, IEnumerable<L2Option>? choices, int value, bool hex, BitmapSource? picture)
    {
        _page = page; Def = def; _hex = hex;
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

    partial void OnSelectedChanged(L2Option? value)
    {
        if (_loading || value == null) return;
        _page.SetField(this, value.Value);
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
        _page.SetField(this, v);
    }
}

/// <summary>
/// The Events tab: a map's events (talking to characters, stepping on spots, scenes, the start-up script) as steps in
/// plain words; the picked step's fields are edited on the right, steps are added from a menu by category, and Save
/// writes the event back (in place when it fits, else in the event space, see <see cref="L2EventBlocks"/>). Also the
/// story flags with every event that uses them.
/// </summary>
public partial class L2EventsPage : L2Page
{
    private Dictionary<int, List<string>>? _flagUses;
    private L2EventScript? _script;
    private L2EventItem? _event;
    private readonly List<L2EventScript.Snapshot> _undo = new(), _redo = new();
    private L2EventScript.Snapshot? _saved;
    private bool _textEditing;
    private Dictionary<int, List<L2EventBlocks.Entry>>? _owners;
    private int _map = -1;

    // names for the drop-downs and descriptions
    private string[] _items = Array.Empty<string>(), _spells = Array.Empty<string>(), _songs = Array.Empty<string>(), _maps = Array.Empty<string>();
    private Dictionary<int, (string Name, int Sprite)> _who = new();
    private List<(int Id, string Name)> _points = new(), _paths = new(), _areas = new(), _exits = new(), _doors = new();

    public ObservableCollection<L2EventItem> Events { get; } = new();
    [ObservableProperty] private L2EventItem? _selectedEvent;
    public ObservableCollection<L2StepVm> Steps { get; } = new();
    [ObservableProperty] private L2StepVm? _selectedStep;
    public ObservableCollection<L2FieldVm> Fields { get; } = new();
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _eventInfo = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _canEditScript;
    [ObservableProperty] private bool _canUndo, _canRedo;

    // the picked step
    [ObservableProperty] private string _stepTitle = "";
    [ObservableProperty] private string _stepHelp = "";
    [ObservableProperty] private string _stepSure = "";
    [ObservableProperty] private bool _hasStep;
    [ObservableProperty] private bool _isTextStep;
    [ObservableProperty] private bool _isSpeakerStep;
    [ObservableProperty] private string _stepText = "";
    [ObservableProperty] private string _textNote = "";
    public List<L2Option> Speakers { get; } = new();
    [ObservableProperty] private L2Option? _speaker;
    public List<L2Option> Endings { get; } = new() { new(0x01, "then the event goes on"), new(0x00, "then the event ends") };
    [ObservableProperty] private L2Option? _ending;
    [ObservableProperty] private bool _canChangeEnding;
    // puzzle steps: their bytes
    [ObservableProperty] private bool _isPuzzleStep;
    [ObservableProperty] private string _bytesText = "";
    [ObservableProperty] private bool _showBytes;
    [ObservableProperty] private string _stepBytes = "";

    public ObservableCollection<string> Flags { get; } = new();
    [ObservableProperty] private int _selectedFlag = -1;
    [ObservableProperty] private string _flagUsesText = "";

    public RomBuffer? CurrentRom => Rom;
    public int CurrentMap => _map;
    public string CurrentMapName => _map >= 0 && _map < _maps.Length ? _maps[_map] : "";
    /// <summary>The map's characters (actor id: name, sprite or -1).</summary>
    public IReadOnlyDictionary<int, (string Name, int Sprite)> Characters => _who;
    public string Who(int id) => WhoName(id);

    protected override IEnumerable<string> ReadNames()
    {
        // a new ROM (another project, or Edit > Undo / Redo): unsaved steps belong to the old one; dropped without
        // asking (saving them now would write them into this ROM)
        IsDirty = false;
        ClearScript();
        _flagUses = null;
        Flags.Clear();
        _items = L2Battle.Items(Rom!).Select(i => i.Name.Trim()).ToArray();
        _spells = L2Battle.Spells(Rom!).Select(s => s.Name.Trim()).ToArray();
        _songs = Enumerable.Range(0, L2Script.SongCount).Select(i => L2Script.SongName(Rom!, i)).ToArray();
        _maps = Enumerable.Range(0, L2Maps.Count).Select(m => MapName(Rom!, m)).ToArray();
        return Enumerable.Range(0, L2Maps.Count).Select(m => $"{m:X2}  {_maps[m]}");
    }

    private static string MapName(RomBuffer rom, int m)
    {
        string n = L2Maps.Name(rom, m);
        return n.Length > 0 && !n.Contains('~') ? n : m switch { 0 => "World map", 1 => "Sea floor", _ => "Map " + m.ToString("X2") };
    }

    // ------------------------------------------------------------------ the map's events

    protected override void Load(int index)
    {
        ConfirmLeave(canCancel: false);
        _map = index;
        if (Flags.Count == 0) LoadFlags();
        ReadMapNames(index);
        FillEvents(null);
    }

    private void FillEvents(L2EventBlocks.Entry? select, bool init = false)
    {
        Events.Clear();
        var rom = Rom!;
        int block = L2Events.Block(rom, _map);
        Events.Add(new L2EventItem { Title = "Start-up script (sets up the map)", IsInit = true, Preview = "Adds the map's characters, music and settings as it loads." });
        int count = 0;
        if (block >= 0)
        {
            var triggers = L2MapInfo.Records(rom, _map, L2MapInfo.Triggers, 5).ToDictionary(r => (int)r.Bytes[0], r => r.Bytes);
            foreach (var e in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript).OrderBy(e => e.Head == 2 ? 9 : e.Head).ThenBy(e => e.Id))
            {
                count++;
                string preview = FirstText(block + e.Offset, block);
                string title = e.Head switch
                {
                    L2EventBlocks.TalkList => $"Talk to {WhoName(e.Id)}",
                    L2EventBlocks.TriggerList => triggers.TryGetValue(e.Id, out var t)
                        ? $"Step on area {e.Id} ({t[1]},{t[2]})-({t[3] - 1},{t[4] - 1})" : $"Step on area {e.Id} (no area on the map)",
                    L2EventBlocks.SceneList => $"Scene {e.Id} (started by \"Go to another map\")",
                    L2EventBlocks.GameList => e.Id switch
                    {
                        0 => "When the map is shown",
                        1 => "On entering the map or one of its rooms",
                        2 => "Before the map loads",
                        _ => $"Game event {e.Id:X2}",
                    },
                    _ => $"Unused list {e.Head:X} event {e.Id:X2}",
                };
                Events.Add(new L2EventItem { Title = title, Entry = e, Preview = preview });
            }
        }
        foreach (var (title, ops) in L2Puzzles.MapScripts(rom, _map)) Events.Add(new L2EventItem { Title = title, PuzzleOps = ops, Preview = "A puzzle script (switches, doors, spots): its own commands." });
        Summary = $"{count} events{(block < 0 ? " (this map has no event block)" : "")}. Pick one to see its steps; \"New event\" adds one.";
        // (callers have asked about unsaved steps already)
        _skipConfirm = true;
        SelectedEvent = init ? Events[0] : select != null ? Events.FirstOrDefault(x => x.Entry == select) ?? Events.FirstOrDefault(x => x.Entry?.Head == select.Head && x.Entry?.Id == select.Id) : null;
        _skipConfirm = false;
        if (SelectedEvent == null) ClearScript();
    }

    private string FirstText(int start, int jumpBase)
    {
        try
        {
            var s = L2EventScript.Read(Rom!, start, jumpBase);
            var t = s.Steps.FirstOrDefault(x => x.IsText);
            if (t == null) return "";
            string line = t.Text.Replace("\n", " ");
            return line.Length > 70 ? line[..70] + "..." : line;
        }
        catch (Exception) { return ""; }
    }

    private void ReadMapNames(int map)
    {
        var rom = Rom!;
        _who = new();
        for (int h = 0; h < 7; h++) _who[h] = (L2EventCommands.HeroNames[h], h);
        var sprites = L2MapInfo.CharacterSprites(rom, map);
        foreach (var (_, b) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
        {
            int id = 0x4F + b[0];
            int spr = sprites.TryGetValue(id, out int s) ? s : -1;
            _who[id] = ($"character {b[0]} at ({b[1]},{b[2]}){(spr >= 0 ? $", sprite {L2Sprites.Name(spr)}" : "")}", spr);
        }
        foreach (var kv in sprites.Where(kv => !_who.ContainsKey(kv.Key))) _who[kv.Key] = ($"character {kv.Key - 0x4F}, sprite {L2Sprites.Name(kv.Value)}", kv.Value);
        _points = L2MapInfo.Records(rom, map, L2MapInfo.Points, 3).Select(r => ((int)r.Bytes[0], $"point {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})")).ToList();
        _paths = L2MapInfo.Records(rom, map, L2MapInfo.Paths, 6).Select(r => ((int)r.Bytes[0], $"path {r.Bytes[0]} (from {r.Bytes[1]},{r.Bytes[2]})")).ToList();
        _areas = L2MapInfo.Records(rom, map, L2MapInfo.Areas, 5).Select(r => ((int)r.Bytes[0], $"area {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})-({r.Bytes[3] - 1},{r.Bytes[4] - 1})")).ToList();
        _exits = L2MapInfo.Records(rom, map, L2MapInfo.Exits, 9).Select(r => ((int)r.Bytes[0], $"exit {r.Bytes[0]} to {(r.Bytes[8] < _maps.Length ? _maps[r.Bytes[8]] : "map " + r.Bytes[8].ToString("X2"))}")).ToList();
        _doors = L2MapInfo.Records(rom, map, L2MapInfo.Doors, 15).Select(r => ((int)r.Bytes[0], $"door {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})")).ToList();
    }

    private string WhoName(int id) => _who.TryGetValue(id, out var w) ? w.Name : L2EventCommands.DefaultWho(id);

    partial void OnSelectedEventChanged(L2EventItem? value)
    {
        if (value == _event) return;
        if (!_skipConfirm && IsDirty && _script != null && _event != null)
        {
            var target = value;
            if (!ConfirmLeave()) { Dispatch(() => { _skipConfirm = true; SelectedEvent = _event; _skipConfirm = false; }); return; }
            // saving read the map's lists again (an event may have moved): pick the same event in them
            if (target != null && !Events.Contains(target))
            {
                Dispatch(() => SelectedEvent = Events.FirstOrDefault(e => e.IsInit == target.IsInit && e.Title == target.Title && (e.Entry == null) == (target.Entry == null)
                                                                    && (e.Entry == null || e.Entry.Head == target.Entry!.Head && e.Entry.Id == target.Entry.Id)));
                return;
            }
        }
        _event = value;
        LoadScript();
    }

    private bool _skipConfirm;

    /// <summary>Asks before unsaved changes are dropped; false = stay. Leaving the map can't be cancelled (the map list
    /// has moved on already): the changes are saved or dropped.</summary>
    private bool ConfirmLeave(bool canCancel = true)
    {
        if (_skipConfirm || !IsDirty || _script == null || _event == null) return true;
        var r = System.Windows.MessageBox.Show($"Save the changes to \"{_event.Title}\"?", "Lufia II events",
            canCancel ? System.Windows.MessageBoxButton.YesNoCancel : System.Windows.MessageBoxButton.YesNo);
        if (r == System.Windows.MessageBoxResult.Cancel) return false;
        if (r == System.Windows.MessageBoxResult.Yes && SaveNow()) return true;
        if (r == System.Windows.MessageBoxResult.Yes && canCancel) return false;
        if (r == System.Windows.MessageBoxResult.Yes)
            System.Windows.MessageBox.Show($"\"{_event.Title}\" couldn't be saved, so its changes are dropped. {Status}", "Lufia II events");
        IsDirty = false;
        return true;
    }

    /// <summary>Before something that opens another event (the New event window): saves or drops unsaved steps; false =
    /// stay.</summary>
    public bool AskBeforeLeaving() => ConfirmLeave();

    private static void Dispatch(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    private void ClearScript()
    {
        _script = null; _event = null;
        Steps.Clear(); Fields.Clear();
        HasStep = false; CanEditScript = false; IsDirty = false;
        EventInfo = ""; Status = "";
    }

    private void LoadScript(int keepStep = -1)
    {
        Steps.Clear(); Fields.Clear(); _undo.Clear(); _redo.Clear(); UpdateUndo();
        _script = null; IsDirty = false; Status = "";
        var e = _event;
        if (e == null || Rom == null) { ClearScript(); return; }
        if (e.PuzzleOps != null)
        {
            CanEditScript = false;
            int n = 0;
            foreach (var o in e.PuzzleOps) Steps.Add(new L2StepVm { PuzzleOp = o, Number = $"{++n}.", Sentence = o.Text });
            EventInfo = "Puzzle scripts run switches, doors and spots with their own commands. Pick a command to change its bytes (it must keep its size).";
            return;
        }
        int block = L2Events.Block(Rom, _map);
        if (e.IsInit)
        {
            int init = L2EventBlocks.InitScript(Rom, _map);
            _script = L2EventScript.Read(Rom, init, L2EventBlocks.InitBase(init));
            _owners = null;
            EventInfo = "Runs as the map loads, all at once (nothing that waits works here): it adds the map's characters (\"Add a map character\") and sets the music and map settings. " +
                        $"At {L2Rom.FileToSnes(init):X6}.";
        }
        else
        {
            _script = L2EventScript.Read(Rom, block + e.Entry!.Offset, block);
            _owners = L2EventBlocks.StepOwners(Rom, block);
            string where = L2EventSpace.Contains(Rom, block) ? "in Lufia Forge's event space" : "in the game's own place";
            EventInfo = (e.Entry.Head switch
            {
                L2EventBlocks.TalkList => "Runs when the player talks to this character.",
                L2EventBlocks.TriggerList => "Runs each time the leader steps onto a tile of this area (use a story flag to run it once).",
                L2EventBlocks.SceneList => "Runs on arriving here through \"Go to another map\" with this scene number.",
                L2EventBlocks.GameList => "Run by the game itself, all at once (nothing that waits works here).",
                _ => "The game never runs this list.",
            }) + $" The map's events are {where} ({L2Rom.FileToSnes(block):X6}).";
        }
        CanEditScript = _script.Problem == null;
        if (_script.Problem != null) EventInfo += " This script can't be changed: " + _script.Problem;
        _saved = _script.TakeSnapshot();
        RefreshSteps();
        SelectedStep = keepStep >= 0 && keepStep < Steps.Count ? Steps[keepStep] : Steps.FirstOrDefault();
    }

    // ------------------------------------------------------------------ the steps

    private L2EventCommands.Words WordsFor(L2Step step) => new()
    {
        Who = L2EventCommands.DefaultWho,
        Item = i => i < _items.Length ? _items[i] : $"item {i:X3}",
        Spell = i => i < _spells.Length ? _spells[i] : $"spell {i:X2}",
        Song = i => i < _songs.Length && _songs[i].Length > 0 ? $"{_songs[i]} ({i:X2})" : $"song {i:X2}",
        Map = m => m < _maps.Length ? $"{_maps[m]} ({m:X2})" : $"map {m:X2}",
        Flag = FlagName,
        Jump = k => k < step.Jumps.Count && step.Jumps[k].Step is { } t && _script != null ? $"step {_script.Steps.IndexOf(t) + 1}" : "(pick a step)",
        Point = p => _points.FirstOrDefault(x => x.Id == p).Name ?? $"point {p}",
        Path = p => _paths.FirstOrDefault(x => x.Id == p).Name ?? $"path {p}",
        Area = a => _areas.FirstOrDefault(x => x.Id == a).Name ?? $"area {a}",
        Exit = x => _exits.FirstOrDefault(y => y.Id == x).Name ?? $"exit {x}",
        Door = d => _doors.FirstOrDefault(x => x.Id == d).Name ?? $"door {d}",
        Shop = s => $"shop {s:X2}",
        Capsule = c => c < L2EventCommands.CapsuleNames.Length ? L2EventCommands.CapsuleNames[c] : $"capsule monster {c}",
    };

    private string FlagName(int f) => $"story flag {f:X2}";

    public string Sentence(L2Step s)
    {
        var def = L2EventCommands.Find(s.Op);
        string d = def?.Describe(s.Bytes, WordsFor(s)) ?? $"Unknown command {s.Op:X2}";
        return d;
    }

    private void RefreshSteps()
    {
        var keep = SelectedStep?.Step;
        Steps.Clear();
        if (_script == null) return;
        var from = new Dictionary<L2Step, List<int>>();
        for (int i = 0; i < _script.Steps.Count; i++)
            foreach (var j in _script.Steps[i].Jumps)
                if (j.Step != null) { if (!from.TryGetValue(j.Step, out var l)) from[j.Step] = l = new(); if (!l.Contains(i + 1)) l.Add(i + 1); }
        for (int i = 0; i < _script.Steps.Count; i++)
        {
            var s = _script.Steps[i];
            var vm = new L2StepVm { Step = s, Number = $"{i + 1}." };
            FillStepVm(vm, from.GetValueOrDefault(s));
            Steps.Add(vm);
        }
        if (keep != null) SelectedStep = Steps.FirstOrDefault(v => v.Step == keep);
    }

    private void FillStepVm(L2StepVm vm, List<int>? from)
    {
        var s = vm.Step!;
        vm.Sentence = Sentence(s);
        vm.Detail = s.IsText ? "\"" + s.Text.Replace("\n", " / ") + "\"" + (s.End == 0 ? "  (then the event ends)" : "") : "";
        var notes = new List<string>();
        if (from != null) notes.Add("from step " + string.Join(", ", from));
        if (_owners != null && s.At >= 0 && _owners.TryGetValue(s.At, out var o) && o.Count > 1 && _event?.Entry is { } me)
            notes.Add("shared with " + string.Join(", ", o.Where(x => x != me).Take(3).Select(EntryName)) + (o.Count > 4 ? "..." : ""));
        if (s.EndsFlow && !s.IsText && s.Op is not (0x00 or 0x42)) notes.Add("the event doesn't go on below");
        vm.Note = string.Join("; ", notes);
    }

    private string EntryName(L2EventBlocks.Entry e) => e.Head switch
    {
        L2EventBlocks.TalkList => $"talking to {WhoName(e.Id)}",
        L2EventBlocks.TriggerList => $"area {e.Id}",
        L2EventBlocks.SceneList => $"scene {e.Id}",
        _ => $"game event {e.Id}",
    };

    partial void OnSelectedStepChanged(L2StepVm? value)
    {
        _textEditing = false;
        ShowStep();
    }

    private bool _showing;

    private void ShowStep()
    {
        _showing = true;
        try
        {
            Fields.Clear();
            var vm = SelectedStep;
            HasStep = vm != null;
            IsPuzzleStep = vm?.PuzzleOp != null;
            IsTextStep = false; IsSpeakerStep = false;
            if (vm == null) { StepTitle = ""; StepHelp = ""; StepSure = ""; return; }
            if (vm.PuzzleOp is { } op)
            {
                StepTitle = "Puzzle command";
                StepHelp = op.Text;
                StepSure = "";
                BytesText = string.Join(" ", Rom!.ReadBytes(op.At, op.Length).Select(b => b.ToString("X2")));
                return;
            }
            var s = vm.Step!;
            var def = L2EventCommands.Find(s.Op);
            StepTitle = s.IsText ? (s.Op == 0x10 ? "Ask a question" : "Text") : def?.Name ?? $"Command {s.Op:X2}";
            StepHelp = s.IsText && s.Op != 0x10
                ? "What is said. Enter starts a new line; the game shows 3 lines at a time. Codes: [Name] = the hero's name, [Pause 20] = wait 20 frames."
                : (def?.Help ?? "") + (def?.Waits.Length > 0 ? $" The event waits here {def.Waits}." : "");
            if (s.Op == 0x10) StepHelp += " Write the question, then one line per answer at the end.";
            StepSure = def?.SureText ?? "";
            StepBytes = Convert.ToHexString(s.Bytes);
            if (s.IsText)
            {
                IsTextStep = true;
                IsSpeakerStep = s.Op != 0x10;
                Speakers.Clear();
                Speakers.Add(new L2Option(0x08, "The person talked to"));
                foreach (int h in new[] { 0, 1, 2, 3, 4, 5, 6 }) Speakers.Add(new L2Option(0x61 + Array.IndexOf(L2EventCommands.SpeakerOf61, h), L2EventCommands.HeroNames[h]));
                Speakers.Add(new L2Option(0x13, "A character of the map..."));
                Speakers.Add(new L2Option(0x9E, "Narration (no speaker)"));
                OnPropertyChanged(nameof(Speakers));
                Speaker = Speakers.FirstOrDefault(x => x.Value == s.Op);
                StepText = s.Text;
                CanChangeEnding = s.Op != 0x10;
                Ending = s.End == 0x0B ? new L2Option(0x0B, "the answers follow") : Endings.FirstOrDefault(x => x.Value == s.End);
                if (s.End == 0x0B) { OnPropertyChanged(nameof(Endings)); }
                UpdateTextNote();
            }
            if (def == null) return;
            foreach (var p in def.FieldsFor(s.Bytes))
            {
                if (s.IsText && p.Kind == L2Kind.Choice && p.Options?.Any(o => o.Item1 is >= 0x61 and <= 0x67) == true) continue;   // the hero speaking is the speaker list
                Fields.Add(MakeField(s, p));
            }
        }
        finally { _showing = false; }
    }

    private void UpdateTextNote()
    {
        var s = SelectedStep?.Step;
        if (s == null || !s.IsText) { TextNote = ""; return; }
        string? problem = L2EventText.Check(s.Text);
        int size = L2EventScript.SizeOf(Rom!, s, s.At >= 0);
        int longest = s.Text.Split('\n').Max(l => l.Length);
        string room = s.At >= 0 && size > s.OriginalLength ? $" (it had {s.OriginalLength}: longer text moves the map's events to the event space when saved)" : "";
        TextNote = problem ?? $"{size} bytes{room}. Longest line: {longest} letters" + (longest > 28 ? " (the screen fits about 28; break the line with Enter)." : ".");
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
                choices = _script!.Steps.Select((x, i) => new L2Option(i, $"step {i + 1}: {Short(Sentence(x))}"));
                break;
            case L2Kind.Who or L2Kind.WhoOrAll:
                choices = WhoChoices(p.Kind == L2Kind.WhoOrAll, v);
                if (_who.TryGetValue(v, out var w) && w.Sprite >= 0) pic = Picture(L2Graphics.MapSprite(Rom!, w.Sprite));
                break;
            case L2Kind.Hero: choices = L2EventCommands.HeroNames.Select((n, i) => new L2Option(i, n)); break;
            case L2Kind.Item: choices = _items.Select((n, i) => new L2Option(i, $"{i:X3} {n}")); break;
            case L2Kind.Spell: choices = _spells.Select((n, i) => new L2Option(i, $"{i:X2} {n}")); break;
            case L2Kind.Song: choices = _songs.Select((n, i) => new L2Option(i, $"{i:X2} {(n.Length > 0 ? n : "(empty)")}")); break;
            case L2Kind.Map: choices = _maps.Select((n, i) => new L2Option(i, $"{i:X2} {n}")); break;
            case L2Kind.Capsule: choices = L2EventCommands.CapsuleNames.Select((n, i) => new L2Option(i, n)); break;
            case L2Kind.Flag: choices = Enumerable.Range(0, 256).Select(f => new L2Option(f, FlagChoice(f))); break;
            case L2Kind.Point: choices = _points.Select(x => new L2Option(x.Id, x.Name)); break;
            case L2Kind.Path: choices = _paths.Select(x => new L2Option(x.Id, x.Name)); break;
            case L2Kind.Area: choices = _areas.Select(x => new L2Option(x.Id, x.Name)); break;
            case L2Kind.Exit: choices = _exits.Select(x => new L2Option(x.Id, x.Name)); break;
            case L2Kind.Door: choices = _doors.Select(x => new L2Option(x.Id, x.Name)); break;
            case L2Kind.Scene: choices = SceneChoices(s.Bytes.Length > 1 ? s.Bytes[1] : 0); break;
            case L2Kind.ArrivalPoint:
                int m = s.Bytes.Length > 1 ? s.Bytes[1] : 0;
                choices = L2MapInfo.Records(Rom!, m, L2MapInfo.Points, 3).Select(r => new L2Option(r.Bytes[0], $"point {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})"));
                break;
            case L2Kind.Sprite:
                hex = true;
                pic = Picture(L2Graphics.MapSprite(Rom!, v));
                break;
            case L2Kind.Hex or L2Kind.Variable or L2Kind.Sound or L2Kind.Battle or L2Kind.Shop: hex = true; break;
        }
        return new L2FieldVm(this, p, choices, v, hex, pic);
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "..." : s;

    private IEnumerable<L2Option> WhoChoices(bool all, int current)
    {
        var list = _who.OrderBy(k => k.Key).Select(k => new L2Option(k.Key, $"{k.Key:X2} {L2EventCommands.Cap(k.Value.Name)}")).ToList();
        // story characters (10-4F) this map's events use
        if (_script != null)
            foreach (var id in _script.Steps.SelectMany(st => L2EventCommands.Find(st.Op)?.FieldsFor(st.Bytes).Where(f => f.Kind is L2Kind.Who).Select(f => SafeGet(f, st.Bytes)) ?? Enumerable.Empty<int>()).Distinct())
                if (id is >= 0x10 and < 0x50 && list.All(o => o.Value != id)) list.Add(new L2Option(id, $"{id:X2} Story character {id:X2}"));
        if (all) list.Add(new L2Option(0xFF, "FF Everyone"));
        if (list.All(o => o.Value != current)) list.Add(new L2Option(current, $"{current:X2} {L2EventCommands.Cap(L2EventCommands.DefaultWho(current))}"));
        return list;
    }

    private static int SafeGet(L2ParamDef f, byte[] b) { try { return f.Get(b); } catch (Exception) { return -1; } }

    private IEnumerable<L2Option> SceneChoices(int map)
    {
        int block = L2Events.Block(Rom!, map);
        if (block < 0) return Enumerable.Empty<L2Option>();
        return L2EventBlocks.ReadLists(Rom!, block).Where(e => e.Head == L2EventBlocks.SceneList)
            .Select(e => new L2Option(e.Id, $"scene {e.Id}: {Short(FirstText(block + e.Offset, block))}"));
    }

    private string FlagChoice(int f)
    {
        var uses = _flagUses?.GetValueOrDefault(f);
        string use = f >= 0xF0 ? "cleared on every map change" : uses == null ? "not used by the game" : $"used {uses.Count}x";
        return $"{f:X2}  {use}";
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

    private void UpdateUndo() { CanUndo = _undo.Count > 0; CanRedo = _redo.Count > 0; }

    private void Changed(bool structure)
    {
        IsDirty = _saved == null || _script == null || !L2EventScript.Same(_saved, _script.TakeSnapshot());
        Status = IsDirty ? "Not saved yet." : "";
        if (structure) { RefreshSteps(); return; }
        var from = new Dictionary<L2Step, List<int>>();
        for (int i = 0; i < _script!.Steps.Count; i++)
            foreach (var j in _script.Steps[i].Jumps)
                if (j.Step != null) { if (!from.TryGetValue(j.Step, out var l)) from[j.Step] = l = new(); if (!l.Contains(i + 1)) l.Add(i + 1); }
        foreach (var vm in Steps) if (vm.Step != null) FillStepVm(vm, from.GetValueOrDefault(vm.Step));
    }

    /// <summary>A field changed (from <see cref="L2FieldVm"/>).</summary>
    public void SetField(L2FieldVm field, int value)
    {
        if (_showing || _script == null || SelectedStep?.Step is not { } s || !CanEditScript) return;
        var p = field.Def;
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
        Changed(false);
        if (rebuild) Dispatch(ShowStep);
    }

    private L2Step? Next(L2Step s)
    {
        int i = _script!.Steps.IndexOf(s);
        return i + 1 < _script.Steps.Count ? _script.Steps[i + 1] : s;
    }

    partial void OnSpeakerChanged(L2Option? value)
    {
        if (_showing || value == null || SelectedStep?.Step is not { IsText: true } s || s.Op == value.Value || !CanEditScript) return;
        Remember();
        s.Bytes = value.Value switch
        {
            0x13 => new byte[] { 0x13, (byte)(s.Op == 0x13 ? s.Bytes[1] : _who.Keys.FirstOrDefault(k => k >= 0x50, 0x50)) },
            0x9E => new byte[] { 0x9E, 0x05 },
            _ => new byte[] { (byte)value.Value },
        };
        Changed(false);
        Dispatch(ShowStep);
    }

    partial void OnStepTextChanged(string value)
    {
        if (_showing || SelectedStep?.Step is not { IsText: true } s || !CanEditScript) return;
        string text = value.Replace("\r", "");
        if (text == s.Text) return;
        if (!_textEditing) { Remember(); _textEditing = true; }
        s.Text = text;
        UpdateTextNote();
        Changed(false);
    }

    partial void OnEndingChanged(L2Option? value)
    {
        if (_showing || value == null || SelectedStep?.Step is not { IsText: true } s || s.End == value.Value || !CanEditScript) return;
        Remember();
        s.End = (byte)value.Value;
        Changed(false);
    }

    /// <summary>Adds a command after the picked step (or at the end).</summary>
    public void AddCommand(L2CommandDef def)
    {
        if (def.Template == null) return;
        if (def.Ops[0] == 0x10) { AddText(new byte[] { 0x10, 0x02, 0, 0, 0, 0 }, "Is that so?\nYes\nNo", 0x0B); return; }
        if (L2EventScript.TextOpeners.Contains(def.Template[0])) { AddText(def.Template, "...", 0x01); return; }
        Insert(new L2Step { Bytes = (byte[])def.Template.Clone() });
    }

    /// <summary>Adds text spoken by the opener's speaker.</summary>
    public void AddText(byte[] opener, string text, byte end) =>
        Insert(new L2Step { Bytes = (byte[])opener.Clone(), IsText = true, Text = text, End = end, OriginalEnd = 0xFF, OriginalText = "\u0001" });

    private void Insert(L2Step step)
    {
        if (_script == null || !CanEditScript) return;
        Remember();
        int at = SelectedStep?.Step is { } cur ? _script.Steps.IndexOf(cur) + 1 : _script.Steps.Count;
        // a step added after the last step that ends the event goes before that end
        if (at == _script.Steps.Count && at > 0 && _script.Steps[at - 1].Op is 0x00 && !_script.Steps[at - 1].IsText) at--;
        _script.Steps.Insert(at, step);
        var next = at + 1 < _script.Steps.Count ? _script.Steps[at + 1] : step;
        foreach (int _ in L2EventScript.JumpPositions(step.Bytes)) step.Jumps.Add(new L2Jump { Step = next });
        Changed(true);
        SelectedStep = Steps[at];
        Status = "Added. " + Status;
    }

    [RelayCommand]
    private void DeleteStep()
    {
        if (_script == null || !CanEditScript || SelectedStep?.Step is not { } s) return;
        if (_script.Steps.Count == 1) { Status = "An event needs at least one step."; return; }
        Remember();
        int i = _script.Steps.IndexOf(s);
        _script.Steps.RemoveAt(i);
        // the last step taken out while a jump leads to it: an End takes its place (back to the step before would run
        // that step again)
        bool jumped = _script.Steps.Any(st => st.Jumps.Any(j => j.Step == s));
        if (i == _script.Steps.Count && jumped) _script.Steps.Add(new L2Step { Bytes = new byte[] { 0x00 } });
        var instead = i < _script.Steps.Count ? _script.Steps[i] : _script.Steps[i - 1];
        foreach (var st in _script.Steps) foreach (var j in st.Jumps) if (j.Step == s) j.Step = instead;
        Changed(true);
        SelectedStep = Steps[Math.Min(i, Steps.Count - 1)];
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int d)
    {
        if (_script == null || !CanEditScript || SelectedStep?.Step is not { } s) return;
        int i = _script.Steps.IndexOf(s), j = i + d;
        if (j < 0 || j >= _script.Steps.Count) return;
        Remember();
        (_script.Steps[i], _script.Steps[j]) = (_script.Steps[j], _script.Steps[i]);
        Changed(true);
        SelectedStep = Steps[j];
    }

    [RelayCommand]
    private void CopyStep()
    {
        if (SelectedStep?.Step is not { } s) return;
        var c = new L2Step { Bytes = (byte[])s.Bytes.Clone(), IsText = s.IsText, Text = s.Text, End = s.End, OriginalEnd = 0xFF, OriginalText = "\u0001" };
        Insert(c);
        for (int k = 0; k < s.Jumps.Count && k < c.Jumps.Count; k++) c.Jumps[k] = new L2Jump { Step = s.Jumps[k].Step };
        Changed(false);
    }

    [RelayCommand]
    private void Undo()
    {
        if (_script == null || _undo.Count == 0) return;
        _redo.Add(_script.TakeSnapshot());
        _script.Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        UpdateUndo();
        _textEditing = false;
        Changed(true);
        ShowStep();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_script == null || _redo.Count == 0) return;
        _undo.Add(_script.TakeSnapshot());
        _script.Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        UpdateUndo();
        _textEditing = false;
        Changed(true);
        ShowStep();
    }

    [RelayCommand]
    private void Revert()
    {
        if (_script == null) return;
        int keep = SelectedStep != null ? Steps.IndexOf(SelectedStep) : -1;
        LoadScript(keep);
        Status = "Back to what the ROM has.";
    }

    [RelayCommand]
    private void Save() => SaveNow();

    /// <summary>Writes the event to the ROM; false when it couldn't be.</summary>
    private bool SaveNow()
    {
        if (Rom == null || Main == null || _script == null || _event == null || !CanEditScript) return false;
        if (!IsDirty) { Status = "Nothing changed."; return true; }
        // every text must be text the game can show
        // (texts not changed are written as they are: the game's own can hold codes typed text can't)
        for (int i = 0; i < _script.Steps.Count; i++)
            if (_script.Steps[i].TextChanged && L2EventText.Check(_script.Steps[i].Text) is { } p) { Status = $"Not saved: step {i + 1}: {p}"; return false; }
        // the last step must end the event, or the game runs on into whatever follows
        var last = _script.Steps[^1];
        if (!last.EndsFlow) { _script.Steps.Add(new L2Step { Bytes = new byte[] { 0x00 } }); }
        try
        {
            string result = _event.IsInit ? L2EventBlocks.SaveInit(Rom, _map, _script) : L2EventBlocks.Save(Rom, _map, _event.Entry!, _script);
            Main.NotifyRomModified($"Lufia II: {_maps.ElementAtOrDefault(_map)} event \"{_event.Title}\"");
            int keep = SelectedStep != null ? Steps.IndexOf(SelectedStep) : -1;
            var entry = _event.Entry;
            IsDirty = false;
            ReloadAfterWrite(entry, keep);
            Status = "Saved. " + result + (last.EndsFlow ? "" : " An End step was added at the bottom.");
            return true;
        }
        catch (Exception ex)
        {
            if (!last.EndsFlow) _script.Steps.RemoveAt(_script.Steps.Count - 1);
            Status = "Not saved: " + ex.Message;
            return false;
        }
    }

    /// <summary>Reads the map's events again after a write (the entry may have moved) and shows the event.</summary>
    private void ReloadAfterWrite(L2EventBlocks.Entry? entry, int keepStep)
    {
        _flagUses = null; Flags.Clear(); LoadFlags();
        ReadMapNames(_map);
        bool init = _event?.IsInit == true;
        _skipConfirm = true;
        _event = null;
        FillEvents(entry, init);
        _skipConfirm = false;
        if (SelectedEvent != null && keepStep >= 0 && keepStep < Steps.Count) SelectedStep = Steps[keepStep];
    }

    // ------------------------------------------------------------------ new and removed events

    /// <summary>Shows one event of a map (from the Maps tab: a character's talk event, a step-on area's event).</summary>
    public void ShowEvent(int map, int head, int id)
    {
        if (Rom == null) return;
        if (!ConfirmLeave()) return;
        if (SelectedIndex != map) SelectedIndex = map; else { ReadMapNames(map); FillEvents(null); }
        var item = Events.FirstOrDefault(e => e.Entry?.Head == head && e.Entry?.Id == id);
        if (item != null) SelectedEvent = item;
    }

    /// <summary>Opens the event list on a new event made by <see cref="L2NewEventWindow"/>.</summary>
    public void EventCreated(L2EventBlocks.Entry? entry, string what)
    {
        Main?.NotifyRomModified("Lufia II: " + what);
        _flagUses = null; Flags.Clear(); LoadFlags();
        ReadMapNames(_map);
        _event = null;
        FillEvents(entry);
        Status = what;
    }

    /// <summary>Takes the picked event out of its list.</summary>
    [RelayCommand]
    private void DeleteEvent()
    {
        if (Rom == null || Main == null || _event?.Entry is not { } e) { Status = "Pick an event of the map first (the start-up script and puzzles can't be removed)."; return; }
        var r = System.Windows.MessageBox.Show($"Remove \"{_event.Title}\" from the map? Its steps stay in the ROM, unused.", "Lufia II events", System.Windows.MessageBoxButton.OKCancel);
        if (r != System.Windows.MessageBoxResult.OK) return;
        L2EventBlocks.Remove(Rom, _map, e);
        Main.NotifyRomModified($"Lufia II: removed \"{_event.Title}\" from {_maps.ElementAtOrDefault(_map)}");
        IsDirty = false;
        _event = null;
        FillEvents(null);
        Status = "Removed.";
    }

    /// <summary>Adds steps at the end of the picked event (the "continue another event" choice of a new event): every
    /// place where it ends goes on with them instead.</summary>
    public void ContinueEvent(L2EventItem item, IEnumerable<L2Step> added)
    {
        if (item != _event && !ConfirmLeave()) return;
        _skipConfirm = true;
        SelectedEvent = item;
        _skipConfirm = false;
        if (_script == null || !CanEditScript) return;
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
        SelectedStep = Steps.FirstOrDefault(v => v.Step == first);
        // ends nothing can follow: a shop, the church, a capsule monster, BC, or leaving the map
        int stuck = _script.Steps.Count(s => !s.IsText && s.Op is 0x18 or 0x19 or 0x81 or 0xBC or 0x16 or 0x5E or 0x83 or 0xB8);
        Status = "New steps added at the end; every place where the event ended now goes on with them. Save to keep them." +
                 (stuck > 0 ? $" {stuck} place{(stuck == 1 ? "" : "s")} where it opens a shop, the church, a capsule monster or goes to another map still end{(stuck == 1 ? "s" : "")} it: the new steps can't follow those." : "");
    }

    // ------------------------------------------------------------------ puzzle bytes

    [RelayCommand]
    private void ApplyBytes()
    {
        if (Rom == null || Main == null || SelectedStep?.PuzzleOp is not { } op) return;
        byte[] bytes;
        try { bytes = BytesText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => Convert.ToByte(s, 16)).ToArray(); }
        catch (Exception) { Status = "Not written: write the bytes as hex pairs separated by spaces."; return; }
        if (bytes.Length != op.Length) { Status = $"Not written: it must stay {op.Length} bytes (it has {bytes.Length})."; return; }
        var old = Rom.ReadBytes(op.At, op.Length);
        Rom.WriteBytes(op.At, bytes);
        if (L2Puzzles.Length(Rom, op.At) != op.Length) { Rom.WriteBytes(op.At, old); Status = $"Not written: as a command that would be {L2Puzzles.Length(Rom, op.At)} bytes, not {op.Length}."; return; }
        Main.NotifyRomModified($"Lufia II: puzzle command at {op.At:X6}");
        var title = _event?.Title;
        FillEvents(null);
        SelectedEvent = Events.FirstOrDefault(e => e.Title == title);
        Status = "Written.";
    }

    // ------------------------------------------------------------------ story flags

    private void LoadFlags()
    {
        _flagUses = new Dictionary<int, List<string>>();
        for (int m = 0; m < L2Maps.Count; m++)
        {
            string map = $"{m:X2} {L2Maps.Name(Rom!, m)}";
            foreach (var (title, ops) in L2Script.MapScripts(Rom!, m))
                foreach (var o in ops.Where(o => !o.IsText))
                {
                    int c = Rom!.ReadByte(o.At);
                    void Use(int flag, string how)
                    {
                        if (!_flagUses.TryGetValue(flag, out var l)) _flagUses[flag] = l = new();
                        l.Add($"{how,-8} {map}, {title} ({o.At:X6})");
                    }
                    switch (c)
                    {
                        case 0x1A: Use(Rom.ReadByte(o.At + 1), "set"); break;
                        case 0x1B: Use(Rom.ReadByte(o.At + 1), "clear"); break;
                        case 0x15 or 0x6A: Use(Rom.ReadByte(o.At + 1), "test"); break;
                        case 0x14:
                            for (int q = o.At + 1, g = 0; g < 64 && q < o.At + o.Length; g++)
                            {
                                int k = Rom.ReadByte(q++);
                                if (k == 0xFF) break;
                                if ((k & 0xC0) == 0xC0) { q += (k & 0xF8) switch { 0xC0 => 4, 0xC8 or 0xD0 or 0xD8 => 3, _ => 2 }; continue; }
                                if ((k & 0x30) == 0) { Use(Rom.ReadByte(q), "test"); q += 1; }
                                else q += 2;
                            }
                            break;
                    }
                }
        }
        foreach (var f in Enumerable.Range(0, 256))
        {
            var uses = _flagUses.GetValueOrDefault(f);
            int sets = uses?.Count(u => u.StartsWith("set")) ?? 0, tests = uses?.Count(u => u.StartsWith("test")) ?? 0;
            string engine = L2Flags.Reserved(f) is { } why ? $" (game: {why})" : "";
            Flags.Add($"{f:X2}  {(uses == null ? "unused" : $"set {sets}x, tested {tests}x")}{engine}");
        }
    }

    /// <summary>A story flag no event script and no game code uses (for "plays once"), or -1.</summary>
    public int FreeFlag(IEnumerable<int>? avoid = null)
    {
        if (_flagUses == null) LoadFlags();
        var skip = avoid?.ToHashSet() ?? new HashSet<int>();
        for (int f = 0xEF; f >= 0x01; f--)
            if (!_flagUses!.ContainsKey(f) && L2Flags.Reserved(f) == null && !skip.Contains(f)) return f;
        return -1;
    }

    partial void OnSelectedFlagChanged(int value)
    {
        if (_flagUses == null || value < 0) { FlagUsesText = ""; return; }
        string engine = L2Flags.Reserved(value) is { } why ? $"Used by the game's own code: {why}.\n\n" : "";
        FlagUsesText = engine + (_flagUses.TryGetValue(value, out var l) ? string.Join("\n", l) : "No event script uses this flag.");
    }

    protected override bool IsField(string p) => false;
    protected override string? Write() => null;
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
