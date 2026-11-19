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

// an event of a map: what starts it and where its script is (the Events page's list, the editor's title)
public sealed class L2EventItem
{
    public string Title { get; init; } = "";
    public string Preview { get; init; } = "";
    public L2EventBlocks.Entry? Entry { get; init; }
    public bool IsInit { get; init; }
    // a puzzle script (its own command set, shown command by command)
    public List<L2Script.Op>? PuzzleOps { get; init; }
    public override string ToString() => Title;
}

// a field of a step: a drop-down (who, item, map, jump target...) or a number, edited on the step's card
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
    public bool IsPath => Def.Kind == L2Kind.Path;
    // a song or a sound, its card gets a button to hear it
    public bool IsAudio => !Step.IsText && Def.Kind is L2Kind.Song or L2Kind.Sound;
    public bool IsSong => Def.Kind == L2Kind.Song;
    // the "onto" area of a tile copy, its card gets a button to pick both areas on the stage
    public bool IsArea => Def.Kind == L2Kind.Area && !Step.IsText && Step.Op is 0x43 or 0x87 or 0xB4 && Def.Label == "onto area";
    // a place of the map (a point, a tile, a door, an exit, an area), its card gets "Pick on the stage"
    public bool IsPlace => L2ScriptEditor.IsPlaceField(Step, Def);
    // a point of another map (where "Go to another map" arrives), picked on that map in a window
    public bool IsArrival => Def.Kind == L2Kind.ArrivalPoint && !Step.IsText;
    // where a "Send a story character" step sends to, picked on that map in a window
    public bool IsStoryPlace => !Step.IsText && Step.Op == 0x35 && Def.Label == "start";
    public int StepMap => L2EventCommands.Find(Step.Op)?.FieldsFor(Step.Bytes).FirstOrDefault(q => q.Kind == L2Kind.Map) is { } mapField ? mapField.Get(Step.Bytes) : -1;
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

    // the value as the step has it (for a jump, the step index)
    public int Raw => IsChoice ? Selected?.Value ?? -1 : int.TryParse(Value.Trim(), _hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : -1;

    partial void OnSelectedChanged(L2Option? value)
    {
        if (_loading || value == null) return;
        _owner.SetField(this, value.Value);
    }

    partial void OnValueChanged(string value)
    {
        if (_loading || IsChoice) return;
        var style = _hex ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!int.TryParse(value.Trim(), style, CultureInfo.InvariantCulture, out int number) || number < Def.Min || number > Def.Max)
        {
            Error = _hex ? $"Enter a hex value from {Def.Min:X2} to {Def.Max:X2}." : $"Enter a number from {Def.Min} to {Def.Max}.";
            return;
        }
        Error = "";
        _owner.SetField(this, number);
    }

    // go where the jump leads (highlights its step)
    public void Follow() { if (IsJump && Selected != null) _owner.HighlightLine(Selected.Value); }
    public void ShowFlagUses() { if (IsFlag) _owner.ShowFlag(Raw); }
    public void EditPath() { if (IsPath) _owner.EditPath(this); }
    public void EditChange() { if (IsArea) _owner.EditChange(this); }
    public void PickPlace() { if (IsPlace) _owner.StartPickPlace(this); }
}

// one step of the script as a card: text with its speaker, or a command with its fields in place
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
    // the text is wider or longer than the box (a soft warning, it still gets written as typed)
    public bool SizeWarns => SizeText.Contains("Warning:");
    partial void OnSizeTextChanged(string value) => OnPropertyChanged(nameof(SizeWarns));

    // command
    [ObservableProperty] private string _commandName = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _waitsText = "";
    // plain words for what the step does, when the step's name alone doesn't say enough (go to another map)
    [ObservableProperty] private string _explain = "";
    [ObservableProperty] private string _sureText = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _flow = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private bool _reached = true;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _bytesText = "";
    [ObservableProperty] private bool _isHighlighted;
    // a hero the step uses who isn't in the party there (-1 = none), the card offers a map character instead
    [ObservableProperty] private int _heroFix = -1;
    [ObservableProperty] private string _heroFixText = "";
    // in the range marked for Copy (Shift+click)
    [ObservableProperty] private bool _isMarked;
    [ObservableProperty] private bool _dropAbove, _dropBelow;
    public ObservableCollection<L2FieldVm> Fields { get; } = new();

    public bool HasWaits => WaitsText.Length > 0;
    public bool HasExplain => Explain.Length > 0;
    public bool HasNote => Note.Length > 0;
    public bool HasFlow => Flow.Length > 0;
    public bool HasWarning => Warning.Length > 0;
    public bool HasFields => Fields.Count > 0;
    public bool ShowBytes => Owner.ShowBytes;
    public double Dim => Reached ? 1 : 0.5;
    // a "go to another map" step, its scene can be opened
    public bool CanOpenScene => Step is { IsText: false, Op: 0x16 } && Step.Bytes.Length >= 4;
    public string OpenSceneText => CanOpenScene ? $"▶ Open scene {Step!.Bytes[2]:X2} of map {Step.Bytes[1]:X2}" : "";

    partial void OnWaitsTextChanged(string value) => OnPropertyChanged(nameof(HasWaits));
    partial void OnExplainChanged(string value) => OnPropertyChanged(nameof(HasExplain));
    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
    partial void OnFlowChanged(string value) => OnPropertyChanged(nameof(HasFlow));
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    // -1 no button, 0 Maxim look-alike, 1-6 a hero look-alike, -2 make a ring like the game's,
    // -3 make the scene a map change arrives at, -4 add a fade-in to it (L2ScriptEditor.Arrival.cs)
    public bool HasHeroFix => HeroFix >= 0 || HeroFix is -2 or L2ScriptEditor.FixMakeScene or L2ScriptEditor.FixAddFadeIn;
    partial void OnHeroFixChanged(int value) => OnPropertyChanged(nameof(HasHeroFix));
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

// a story flag or counter ticked for playback (what the cutscene checks)
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

// a place a puzzle step touches, marked on the stage (map pixels)
public sealed class L2StageMark
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public string Label { get; init; } = "";
    // the source of a tile copy (drawn in another colour). on a path it's a step a wall stops
    public bool IsSource { get; init; }
    // a path's start or one of its places (a numbered box, the other path marks are its tiles)
    public bool IsCorner { get; init; }
}

// a character drawn on the stage
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
    // a small arrow for sprites that don't have a picture for each facing
    public string Facing { get; init; } = "";
}

// a step placed in time for the timeline: its track, when it starts and how long it takes (frames)
public sealed record L2TimelineItem(int Line, string Track, string Kind, string Label, int Start, int Duration, bool Reached);

// something that can happen at a tile of the stage (the stage's menu)
public sealed record L2StageAction(string Group, string Header, Action Act);

// where every story flag is set, cleared and checked (from L2FlagIndex), as lines of text
public static class L2FlagUses
{
    public static void Invalidate() => L2FlagIndex.Invalidate();

    public static Dictionary<int, List<string>> Get(RomBuffer rom)
    {
        var uses = new Dictionary<int, List<string>>();
        foreach (var use in L2FlagIndex.All(rom).Where(u => u.Kind == L2FlagIndex.Kind.Story))
        {
            if (!uses.TryGetValue(use.Number, out var lines)) uses[use.Number] = lines = new();
            string how = use.How switch { "on" => "set", "off" => "clear", _ => "test" };
            lines.Add($"{how,-8} {use.Map:X2} {L2Maps.Name(rom, use.Map)}, {use.Where} ({use.At:X6})");
        }
        return uses;
    }

    // a story flag 01-EF that nothing uses and no one named (for "plays once"), or -1
    public static int Free(RomBuffer rom, IEnumerable<int>? avoid = null) => L2FlagIndex.FreeStory(rom, false, avoid);
}

// story flags the game's own code reads or writes (so they're never handed out as free)
public static class L2Flags
{
    public static string? Reserved(int flag) => L2FlagIndex.Reserved(flag);
}

// one Lufia II event or cutscene open for editing, the way Lufia 1's event editor works. the steps are cards edited
// in place (text with its speaker, commands with their fields). the map is a stage showing where everyone stands after
// the picked step (click a tile and a character walks, appears or turns there, or the camera jumps there). a timeline
// lays the steps out in time, playback runs the script like the game would, and the edits get written to the ROM by
// themselves when you leave the editor (Save Project, Apply, closing the window). used by the Events page, the
// Cutscenes page and the pop-up editor opened from the Maps page
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
    // hero (1-6) -> the character that stands in for it on this map (same sprite, not in the party)
    private Dictionary<int, int> _heroStandIns = new();
    // the one naming of characters (L2Speakers) for this map
    private L2Speakers? _speakers;
    // who speaks each text step of the script (index -> label), the way the game picks it
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
        var list = new List<L2Option> { new(0x08, "The person talked to") };
        foreach (int hero in new[] { 0, 1, 2, 3, 4, 5, 6 }) list.Add(new L2Option(0x61 + Array.IndexOf(L2EventCommands.SpeakerOf61, hero), L2EventCommands.HeroNames[hero]));
        list.Add(new L2Option(0x13, "A character of the map..."));
        list.Add(new L2Option(0x9E, "Narration (no speaker)"));
        return list;
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

    // the step list takes clicks for editable scripts and puzzle scripts (their steps get picked to mark their
    // places and write their bytes, though they aren't edited as cards). if it was disabled for puzzles no step could be picked
    public bool LinesEnabled => CanEdit || IsPuzzle;
    partial void OnCanEditChanged(bool value) => OnPropertyChanged(nameof(LinesEnabled));
    partial void OnIsPuzzleChanged(bool value) => OnPropertyChanged(nameof(LinesEnabled));
    [ObservableProperty] private bool _canUndo, _canRedo;
    [ObservableProperty] private string _undoTip = "Undo (Ctrl+Z)", _redoTip = "Redo (Ctrl+Y)";
    [ObservableProperty] private string _bytesDump = "";
    // puzzle scripts: the picked command's bytes, written back the same size
    [ObservableProperty] private string _puzzleBytes = "";
    // the cutscene layout: the stage gets most of the room and is always on
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
            foreach (var line in Lines) line.RefreshShowBytes();
        }
    }

    // the script was written to the ROM (the pages read their lists again), with what was written
    public event Action<string>? Saved;
    // asks the view to scroll the stage so a point of the map (pixels) is in sight
    public event Action<double, double>? ScrollRequested;
    // asks the view to bring a line into view
    public event Action<int>? LineHighlighted;
    // asks the host to show where a story flag is used
    public event Action<int>? FlagRequested;
    // asks the host to open another event (a scene of another map that a "go to" step starts)
    public event Action<int, int, int>? OpenRequested;
    // the stage was simulated again (the timeline draws again)
    public event Action? Resimulated;
    // playback is at a line, this many frames into it
    public event Action<int, int>? PlaybackProgress;

    public L2ScriptEditor()
    {
        _timer.Tick += (_, _) => PlayTick();
        _playAnswer = AnswerChoices[0];
    }

    // loading

    // opens an event of a map for editing
    public void Load(RomBuffer rom, MainViewModel main, int map, L2EventItem item, string title)
    {
        Stop();
        IsDrawingPath = false; PathMarks.Clear();
        if (NamesStale(rom)) ReadRomNames(rom);
        _rom = rom; _main = main;
        _map = map;
        _event = item;
        Title = title;
        ReadMap();   // (again for the same map too, the Maps page may have added characters or points meanwhile)
        _undo.Clear(); _redo.Clear(); UpdateUndo();
        _textLine = null;
        _flagsOn.Clear(); _countersAtStart.Clear();
        _startOverride = null;
        _picked = -1;
        ReadScript(-1);
        if (TickFlagsToPlay()) { FillStartFlags(); Simulate(); Annotate(); ShowStage(); }
        _partyByHand = false;
        FillTestParty();
        TestNote = NewGameNote();
        MarkRead();
    }

    // the party in the test

    // heroes 1-6 for the test start, ticked when they're in the party (Maxim always leads)
    public ObservableCollection<L2StartFlagVm> TestParty { get; } = new();

    // the places the picked puzzle step touches, drawn over the stage
    public ObservableCollection<L2StageMark> PuzzleMarks { get; } = new();
    private L2PuzzleText? _puzzleText;
    [ObservableProperty] private string _testPartyWhy = "";
    private bool _partyByHand, _fillingParty;
    // heroes in the test party only because the cutscene uses them (the story so far doesn't bring them)
    private HashSet<int> _testOnly = new();

    // the story's party at this event (L2StoryParty), unless picked by hand
    private void FillTestParty()
    {
        if (_rom == null || _map < 0 || _event == null || _event.PuzzleOps != null) { TestParty.Clear(); TestPartyWhy = ""; return; }
        if (_partyByHand) return;
        L2StoryParty.Result storyParty;
        try { storyParty = L2StoryParty.Of(_rom, _map, _event.Entry?.Head ?? -1, _event.Entry?.Id ?? -1, _flagsOn.ToList()); }
        catch (Exception) { storyParty = new L2StoryParty.Result(new List<int> { 0 }, "Maxim"); }
        _testOnly = storyParty.ForTest.ToHashSet();
        _fillingParty = true;
        TestParty.Clear();
        for (int h = 1; h < 7; h++)
            TestParty.Add(new L2StartFlagVm { Number = h, Label = L2EventCommands.HeroNames[h], IsOn = storyParty.Heroes.Contains(h), Changed = PartyChanged });
        _fillingParty = false;
        TestPartyWhy = "Maxim leads" + (storyParty.Heroes.Count > 1 ? "; the story's party here: " + storyParty.Why[(storyParty.Why.IndexOf(", ") + 2)..] : " alone: the story's party here has no one else (as far as the story flags show).");
        if (_script != null) Annotate();
    }

    private void PartyChanged()
    {
        if (_fillingParty) return;
        _partyByHand = true;
        int count = TestParty.Count(h => h.IsOn);
        TestPartyWhy = "Picked by hand" + (count > L2StoryParty.MaxParty - 1 ? $": only {L2StoryParty.MaxParty - 1} can join Maxim, the first ones ticked are taken." : ".");
        if (_script != null) Annotate();
    }

    // the party at step stepIndex: the test party with the joins (2B) and leaves (2C) before it,
    // plus the heroes this event has made join by then
    private (HashSet<int> Party, HashSet<int> Joined) PartyAt(int stepIndex)
    {
        var party = new HashSet<int>(PartyForTest());
        var joined = new HashSet<int>();
        if (_script == null) return (party, joined);
        for (int k = 0; k < stepIndex && k < _script.Steps.Count; k++)
        {
            var step = _script.Steps[k];
            if (step.IsText || step.Bytes.Length < 2) continue;
            if (step.Op == 0x2B) { party.Add(step.Bytes[1]); joined.Add(step.Bytes[1]); }
            else if (step.Op == 0x2C) { party.Remove(step.Bytes[1]); joined.Remove(step.Bytes[1]); }
        }
        return (party, joined);
    }

    // true if hero (1-6) isn't in the story's party at step stepIndex: not in the test party, or only in it
    // because the cutscene uses the hero, and not made to join earlier in this event
    private bool HeroAbsent(int stepIndex, int hero)
    {
        var (party, joined) = PartyAt(stepIndex);
        return !joined.Contains(hero) && !LeavesLater(stepIndex, hero) && (!party.Contains(hero) || _testOnly.Contains(hero));
    }

    // true if the event makes hero leave the party after step stepIndex (2C, or the hero's story flag turned off)
    // without making the hero join first. then the hero is in the party at that step whatever the story flags say
    // (a hack's own story: Karlloon North Shrine B1's scene ends with Dekar leaving)
    private bool LeavesLater(int stepIndex, int hero)
    {
        if (_script == null) return false;
        for (int k = stepIndex + 1; k < _script.Steps.Count; k++)
        {
            var step = _script.Steps[k];
            if (step.IsText || step.Bytes.Length < 2) continue;
            if (step.Op == 0x2B && step.Bytes[1] == hero || step.Op == 0x1A && step.Bytes[1] == L2StoryParty.HeroFlag[hero]) return false;
            if (step.Op == 0x2C && step.Bytes[1] == hero || step.Op == 0x1B && step.Bytes[1] == L2StoryParty.HeroFlag[hero]) return true;
        }
        return false;
    }

    // breaks texts wider than the box (or longer than a box shows) at spaces to fit, and cuts them into more text
    // steps of the same speaker if they take more than one box (a question only gets its lines broken). only the
    // texts changed here, unless all. returns how many were changed
    private int FitTexts(bool all)
    {
        if (_script == null) return 0;
        int fitted = 0;
        for (int i = _script.Steps.Count - 1; i >= 0; i--)
        {
            var step = _script.Steps[i];
            if (!step.IsText || !all && !step.TextChanged || L2Events.WidthWarning(step.Text).Length == 0) continue;
            var boxes = L2Events.FitToBoxes(step.Text);
            bool question = step.Op == 0x10 || step.End == 0x0B;
            if (question)
            {
                if (boxes.Count > 1) continue;   // (its choices can't move to another box)
                step.Text = boxes[0]; fitted++; continue;
            }
            byte end = step.End;
            step.Text = boxes[0];
            if (boxes.Count > 1) step.End = 0x01;
            for (int k = 1; k < boxes.Count; k++)
                _script.Steps.Insert(i + k, new L2Step
                {
                    Bytes = (byte[])step.Bytes.Clone(), IsText = true, Text = boxes[k], End = k == boxes.Count - 1 ? end : (byte)0x01,
                    OriginalEnd = 0xFF, OriginalText = "\u0001",
                });
            fitted++;
        }
        return fitted;
    }

    private List<int> PartyForTest() => new[] { 0 }.Concat(TestParty.Where(h => h.IsOn).Select(h => h.Number)).Take(L2StoryParty.MaxParty).ToList();

    // an event that only checks and ends with no story flag on (the game's own cutscenes check the story so far,
    // North Lighthouse 6F's step-on area 1 plays only with flag 37 on and 39 off) opens with the flags it needs ticked,
    // one at a time while playback reaches more steps, so playback and Test in the game play it. its played flag
    // stays off. false = nothing ticked
    private bool TickFlagsToPlay()
    {
        if (_rom == null || _script == null || _event?.Entry == null || _flagsOn.Count > 0) return false;
        var (x, y) = LeaderStart();
        int talkedTo = _event.Entry.Head == L2EventBlocks.TalkList ? _event.Entry.Id : -1;
        var on = L2Cutscene.FlagsToPlay(_rom, _map, _script, flags =>
        {
            var state = L2Cutscene.Initial(_rom, _map, x, y, _persistent, flags);
            state.Talker = state.TalkSave = talkedTo >= 0 ? talkedTo : 0xFF;
            return state;
        }, Name);
        if (on.Count == 0) return false;
        _flagsOn.UnionWith(on);
        return true;
    }

    // opens one event of a map by its list and id (from the Maps page), list -1 is the start-up script
    public bool Load(RomBuffer rom, MainViewModel main, int map, int head, int id)
    {
        var item = head < 0 ? ItemsOf(rom, map).FirstOrDefault(e => e.IsInit) : ItemsOf(rom, map).FirstOrDefault(e => e.Entry?.Head == head && e.Entry?.Id == id);
        if (item == null) return false;
        if (NamesStale(rom)) ReadRomNames(rom);
        Load(rom, main, map, item, TitleOf(rom, map, item));
        return true;
    }

    // the ROM's version when the event shown was read (or written here), and when the names were read
    private int _readVersion = -1, _namesVersion = -1;
    // the event's steps as they were read (place and bytes), to tell whether another page changed them since
    private List<(int At, byte[] Bytes)> _readSteps = new();
    private int _readBlock = -1;

    // true if the item, spell, song and map names are from another ROM or an older version of this one (renamed on
    // another page, or Undo)
    private bool NamesStale(RomBuffer rom) => !ReferenceEquals(rom, _rom) || _maps.Length == 0 || _namesVersion != rom.Version;

    // remembers the ROM as the event was read, ChangedElsewhere compares with it
    private void MarkRead()
    {
        if (_rom == null) return;
        _readVersion = _rom.Version;
        _readBlock = _map >= 0 ? L2Events.Block(_rom, _map) : -1;
        _readSteps = _script == null ? new() : _script.Steps.Where(s => !s.IsNew).Select(s => (s.At, _rom.ReadBytes(s.At, s.OriginalLength))).ToList();
    }

    // true if the event was written by another page (or moved with its map's events) since it was read here
    private bool ChangedElsewhere()
    {
        if (_rom == null || _rom.Version == _readVersion) return false;
        if (_map >= 0 && L2Events.Block(_rom, _map) != _readBlock) return true;
        return _readSteps.Any(s => s.At + s.Bytes.Length > _rom.Length || !_rom.ReadBytes(s.At, s.Bytes.Length).AsSpan().SequenceEqual(s.Bytes));
    }

    // the page showing the editor is shown again. if the ROM changed since the event was read and nothing here is
    // unwritten, the event (and the map's names and stage) gets read again, keeping the step picked
    public void Shown()
    {
        if (_rom == null || _main == null || _event == null || _rom.Version == _readVersion || HasPendingEdits) return;
        int keep = SelectedLine;
        if (FindAgain() is not { } item) { Unload(); Status = "The event shown isn't on its map any more (changed on another page)."; return; }
        Load(_rom, _main, _map, item, TitleOf(_rom, _map, item));
        if (keep >= 0 && keep < Lines.Count) SelectedLine = keep;
    }

    // the event shown, as the map's lists have it now (its place may have moved), or null
    private L2EventItem? FindAgain()
    {
        if (_rom == null || _event is not { } shown || _map < 0) return null;
        return ItemsOf(_rom, _map).FirstOrDefault(x => x.IsInit == shown.IsInit && (x.PuzzleOps == null) == (shown.PuzzleOps == null) && (x.Entry == null) == (shown.Entry == null)
            && (shown.Entry == null ? x.Title == shown.Title : x.Entry!.Head == shown.Entry.Head && x.Entry.Id == shown.Entry.Id));
    }

    // no event open, but the map's characters, points and names get read (the New event window lists the
    // characters of the map the Events page shows, not of the last event opened)
    public void UseMap(RomBuffer rom, MainViewModel main, int map)
    {
        if (map < 0) return;
        if (NamesStale(rom)) ReadRomNames(rom);
        _rom = rom; _main = main; _map = map;
        try { ReadMap(); } catch (Exception) { _who.Clear(); }
    }

    // nothing open
    public void Unload()
    {
        Stop();
        IsDrawingPath = false; PathMarks.Clear();
        _script = null; _event = null; _saved = null;
        Lines.Clear(); _undo.Clear(); _redo.Clear(); UpdateUndo();
        Cast.Clear(); CastHeader = "";
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
        _namesVersion = rom.Version;
        _items =L2Battle.Items(rom).Select(i => i.Name.Trim()).ToArray();
        _spells = L2Battle.Spells(rom).Select(s => s.Name.Trim()).ToArray();
        _songs = Enumerable.Range(0, L2Script.SongCount).Select(i => L2Script.SongName(rom, i)).ToArray();
        _maps = Enumerable.Range(0, L2Maps.Count(rom)).Select(m => MapNameOf(rom, m)).ToArray();
        _spritePics.Clear(); _facingPics.Clear();
        _choices.Clear();
        _who.Clear();
    }

    public static string MapNameOf(RomBuffer rom, int map)
    {
        if (LufiaForge.Core.Maps.MapNames.ProjectNames is { } projectNames && projectNames.TryGetValue(map, out var projectName) && !string.IsNullOrWhiteSpace(projectName)) return projectName;
        string name = L2Maps.Name(rom, map);
        return name.Length > 0 && !name.Contains('~') ? name : map switch { 0 => "World map", 1 => "Sea floor", _ => "Map " + map.ToString("X2") };
    }

    // the events of a map as the Events page lists them (the start-up script first, puzzles last)
    public static List<L2EventItem> ItemsOf(RomBuffer rom, int map)
    {
        var list = new List<L2EventItem> { new() { Title = "Start-up script (sets up the map)", IsInit = true, Preview = "Adds the map's characters, music and settings as it loads." } };
        int block = L2Events.Block(rom, map);
        if (block >= 0)
        {
            var speakers = new L2Speakers(rom, map);
            var starts = L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).ToDictionary(r => 0x4F + r.Bytes[0], r => r.Bytes);
            var triggers = L2MapInfo.Records(rom, map, L2MapInfo.Triggers, 5).ToDictionary(r => (int)r.Bytes[0], r => r.Bytes);
            var goTos = L2Cutscene.GoTos(rom, map);
            string From(int scene)
            {
                if (!goTos.TryGetValue(scene, out var starters)) return "started by \"Go to another map\"";
                string One((int Map, int Head, int Id, int Point) goTo) => (goTo.Map == map ? "this map's " : $"{MapNameOf(rom, goTo.Map)} ({goTo.Map:X2}) ") + goTo.Head switch
                {
                    L2EventBlocks.SceneList => $"scene {goTo.Id:X2}",
                    L2EventBlocks.TalkList => goTo.Id >= 0x50 ? $"talk to character {goTo.Id - 0x4F}" : $"talk to story character {goTo.Id:X2}",
                    L2EventBlocks.TriggerList => $"step-on area {goTo.Id}",
                    _ => $"event {goTo.Id:X2}",
                };
                return "started from " + One(starters[0]) + (starters.Count > 1 ? $" and {starters.Count - 1} more" : "");
            }
            foreach (var entry in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript).OrderBy(e => e.Head == 2 ? 9 : e.Head).ThenBy(e => e.Id))
            {
                string preview = FirstText(rom, block + entry.Offset, block);
                string title = entry.Head switch
                {
                    L2EventBlocks.TalkList => "Talk to " + WhoTitle(entry.Id, speakers, starts),
                    L2EventBlocks.TriggerList => triggers.TryGetValue(entry.Id, out var area)
                        ? $"Step on area {entry.Id} ({area[1]},{area[2]})-({area[3] - 1},{area[4] - 1})" : $"Step on area {entry.Id} (no area on the map)",
                    L2EventBlocks.SceneList => L2Cutscene.Starter(rom, map, entry.Id) is { } starter
                        ? $"Scene {entry.Id:X2}: when the party enters the map" + (starter.Flag >= 0 ? $" (story flag {starter.Flag:X2} {(starter.MustBeOn ? "on" : "off")})" : "")
                        : $"Scene {entry.Id:X2} ({From(entry.Id)})",
                    L2EventBlocks.GameList => entry.Id switch
                    {
                        0 => "When the map is shown",
                        1 => "On entering the map or one of its rooms",
                        2 => "Before the map loads",
                        _ => $"Game event {entry.Id:X2}",
                    },
                    _ => $"Unused list {entry.Head:X} event {entry.Id:X2}",
                };
                list.Add(new L2EventItem { Title = title, Entry = entry, Preview = preview });
            }
        }
        foreach (var (title, ops, preview) in L2Puzzles.MapScripts(rom, map)) list.Add(new L2EventItem { Title = title, PuzzleOps = ops, Preview = preview });
        return list;
    }

    private static string WhoTitle(int id, L2Speakers speakers, Dictionary<int, byte[]> starts)
    {
        string at = id >= 0x50 && starts.TryGetValue(id, out var start) ? $" at ({start[1]},{start[2]})" : "";
        return speakers.Label(id) + at;
    }

    // the editor's title for an event: the map and what starts the event
    public static string TitleOf(RomBuffer rom, int map, L2EventItem item)
    {
        string prefix = $"Map {map:X2} {MapNameOf(rom, map)}";
        if (item.Entry is not { } entry) return $"{prefix} - {item.Title}";
        return entry.Head switch
        {
            L2EventBlocks.TalkList => $"{prefix} - {item.Title} (actor {entry.Id:X2})",
            L2EventBlocks.TriggerList => $"{prefix} - {item.Title}",
            L2EventBlocks.SceneList => $"{prefix} - scene {entry.Id:X2}",
            _ => $"{prefix} - {item.Title}",
        };
    }

    public static string FirstText(RomBuffer rom, int start, int jumpBase)
    {
        try
        {
            var script = L2EventScript.Read(rom, start, jumpBase);
            var firstText = script.Steps.FirstOrDefault(x => x.IsText);
            if (firstText == null) return "";
            string line = firstText.Text.Replace("\n", " ");
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
        foreach (var (_, charRecord) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
        {
            int id = 0x4F + charRecord[0];
            if (id >= 0x80) continue;   // a start made for story characters only (L2Persistent.StartAt)
            _who[id] = ($"{_speakers.Label(id)} at ({charRecord[1]},{charRecord[2]}){Made(id)}", _speakers.SpriteOf(id));
        }
        foreach (var kv in sprites.Where(kv => !_who.ContainsKey(kv.Key))) _who[kv.Key] = (_speakers.Label(kv.Key) + Made(kv.Key), kv.Value);
        _persistent = L2Persistent.On(L2Persistent.All(rom), map);
        foreach (var place in _persistent.Where(p => !_who.ContainsKey(p.Id))) _who[place.Id] = (_speakers.Label(place.Id), place.Sprite);
        // a hero standing on the map as a character of its own (Selan at home in Parcelyte is story character 19 with
        // her sprite 01). text for the hero then has to come from that character, the party's hero isn't there
        _heroStandIns = new();
        foreach (var kv in _who.Where(kv => kv.Key >= 0x10 && kv.Value.Sprite is >= 1 and < 7).OrderBy(kv => kv.Key))
            _heroStandIns.TryAdd(kv.Value.Sprite, kv.Key);
        foreach (var (hero, id) in _heroStandIns) _who[hero] = ($"{L2EventCommands.HeroNames[hero]} (party; on this map also {id:X2})", hero);
        _pointSpots = L2StoryClear.PresetPoints(rom, map);
        _goToPoints = L2Cutscene.GoToPoints(rom, map);
        _points = L2MapInfo.Records(rom, map, L2MapInfo.Points, 3).Select(r => ((int)r.Bytes[0], $"point {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})")).ToList();
        ReadPaths();
        _areas = L2MapInfo.Records(rom, map, L2MapInfo.Areas, 5).Select(r => ((int)r.Bytes[0], $"area {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})-({r.Bytes[3] - 1},{r.Bytes[4] - 1})")).ToList();
        _exits = L2MapInfo.Records(rom, map, L2MapInfo.Exits, 9).Select(r => ((int)r.Bytes[0], $"exit {r.Bytes[0]} to {(r.Bytes[8] < _maps.Length ? _maps[r.Bytes[8]] : "map " + r.Bytes[8].ToString("X2"))}")).ToList();
        _doors = L2MapInfo.Records(rom, map, L2MapInfo.Doors, 15).Select(r => ((int)r.Bytes[0], $"door {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})" +
            (L2FlagNotes.Name(L2FlagIndex.Kind.Door, map * 0x100 + r.Bytes[0]) is { } doorName ? $" {doorName}" : ""))).ToList();
        ReadPointChoices();
        try
        {
            var (w, h, pixels) = L2Maps.Render(rom, map);
            _mapPixels = pixels.Select(c => (int)(c == 0 ? 0xFF000000 : c)).ToArray(); _mapW = w; _mapH = h;
            _copiesShown = "";
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, _mapPixels, w * 4);
            bitmap.Freeze();
            MapImage = _baseImage = bitmap;
        }
        catch (Exception ex) { MapImage = _baseImage = null; _mapPixels = null; Status = "The map can't be drawn: " + ex.Message; }
    }

    // where an exit of another map puts the party on this one (for a map without preset points), or null
    private (int X, int Y)? _arrival;

    // the test's start choices: the map's preset points, or (for a map the party only reaches through exits, like
    // the towers' upper floors) a new one, where an exit into the map lands or where the party stands on the stage
    private void ReadPointChoices()
    {
        var rom = _rom!;
        PointChoices.Clear();
        int game = GamePoint();
        foreach (var point in _pointSpots) PointChoices.Add(new L2Option(point.Id, $"point {point.Id} ({point.X}, {point.Y})" + (point.Id == game ? ", where the game starts it" : "")));
        _arrival = null;
        if (_pointSpots.Count == 0)
        {
            string from = "";
            for (int mapNumber = 0; mapNumber < L2Maps.Count(rom) && _arrival == null; mapNumber++)
            {
                if (mapNumber == _map) continue;
                foreach (var (_, exitRecord) in L2MapInfo.Records(rom, mapNumber, L2MapInfo.Exits, 9))
                    if (exitRecord[8] == _map) { _arrival = (exitRecord[6], exitRecord[7]); from = _maps.ElementAtOrDefault(mapNumber) ?? $"map {mapNumber:X2}"; break; }
            }
            PointChoices.Add(new L2Option(NewPoint, _arrival is { } arrival
                ? $"a new preset point at ({arrival.X}, {arrival.Y}), where the exit from {from} arrives (or Party start...)"
                : "a new preset point where the party stands on the stage (Party start... moves it)"));
        }
        TestPoint = PointChoices.FirstOrDefault(p => p.Value == game) ?? PointChoices.FirstOrDefault();
    }

    // the scenes of this map started by "Go to another map" -> the point the game puts the party on
    private Dictionary<int, int> _goToPoints = new();

    // the preset point the game starts the event with, or -1. for a scene it's its "on entering" point, else the point
    // of the "Go to another map" that starts it, else the new game's (if it starts with this scene). for a step-on event
    // it's a point on its area. on another point the camera is elsewhere, and a box over a character off to the side
    // runs off the screen and wraps round (Elcid's scene 01 on point 0 instead of 4)
    private int GamePoint()
    {
        var rom = _rom!;
        var entry = _event?.Entry;
        if (entry?.Head == L2EventBlocks.SceneList)
        {
            if (L2Cutscene.Starter(rom, _map, entry.Id)?.Point is int starterPoint && starterPoint >= 0) return starterPoint;
            if (_goToPoints.TryGetValue(entry.Id, out int goToPoint)) return goToPoint;
            var (startMap, startScene, startPoint) = L2MapEdit.NewGame(rom);
            if (startMap == _map && startScene == entry.Id) return startPoint;
        }
        if (entry?.Head == L2EventBlocks.TriggerList && L2MapInfo.Records(rom, _map, L2MapInfo.Triggers, 5).FirstOrDefault(r => r.Bytes[0] == entry.Id) is { Bytes: var area } && area != null)
            foreach (var point in _pointSpots)
                if (point.X >= area[1] && point.X < area[3] && point.Y >= area[2] && point.Y < area[4]) return point.Id;
        return -1;
    }

    // the test choice that adds a preset point first
    private const int NewPoint = -1;

    // a character's name for the stage and descriptions (short, no sprite)
    public string Name(int id)
    {
        if (id < 0) return "nobody";
        if (id < 7) return L2EventCommands.HeroNames[id];
        if (id == 0xFF) return "everyone";
        return _speakers?.Label(id, sprite: false) ?? L2EventCommands.DefaultWho(id);
    }

    private string LongName(int id) => _who.TryGetValue(id, out var who) ? who.Name : L2EventCommands.DefaultWho(id);

    private void ReadScript(int keepLine)
    {
        Stop();
        ClearMark();   // a range marked in another event (or before a re-read) doesn't belong to this one
        Lines.Clear();
        _script = null; _owners = null; IsDirty = false; Status = ""; _textLine = null;
        _picked = -1;
        var eventItem = _event;
        var rom = _rom;
        if (eventItem == null || rom == null) { HasScript = false; CanEdit = false; IsPuzzle = false; Info = ""; FillCast(); Simulate(); ShowStage(); return; }
        HasScript = true;
        IsPuzzle = eventItem.PuzzleOps != null;
        _puzzleText = null;
        PuzzleMarks.Clear();
        if (eventItem.PuzzleOps != null)
        {
            CanEdit = false;
            for (int i = 0; i < eventItem.PuzzleOps.Count; i++) Lines.Add(new L2LineVm(this, eventItem.PuzzleOps[i], i) { BytesText = Convert.ToHexString(rom.ReadBytes(eventItem.PuzzleOps[i].At, eventItem.PuzzleOps[i].Length)) });
            Info = "A puzzle script (switches, doors, blocks to push, bomb and sword spots), in words. Spots and areas are the map's puzzle places; " +
                   "switches are the puzzle's own on/off flags, not story flags; a tile change copies a piece of the map kept off to the side (the changed floor or wall) onto the place. " +
                   "A step that runs as its own thread says which step starts it.";
            FillCast();
            Simulate(); ShowStage();
            SelectedLine = keepLine >= 0 && keepLine < Lines.Count ? keepLine : -1;
            return;
        }
        int block = L2Events.Block(rom, _map);
        string where;
        if (eventItem.IsInit)
        {
            int init = L2EventBlocks.InitScript(rom, _map);
            _script = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
            where = $"Runs as the map loads, all at once (nothing that waits works here): it adds the map's characters (\"Add a map character\") and sets the music and map settings. Script at ROM {L2Rom.FileToSnes(init):X6}";
        }
        else
        {
            _script = L2EventScript.Read(rom, block + eventItem.Entry!.Offset, block);
            _owners = L2EventBlocks.StepOwners(rom, block);
            string space = L2EventSpace.Contains(rom, block) ? "in Lufia Forge's event space" : "in the game's own place";
            where = $"Script at ROM {L2Rom.FileToSnes(block + eventItem.Entry.Offset):X6}, the map's events {space} ({L2Rom.FileToSnes(block):X6})";
        }
        CanEdit = _script.Problem == null;
        int bytes = _script.Steps.Sum(s => s.OriginalLength);
        int texts = _script.Steps.Count(s => s.IsText);
        Info = "What it does: " + Summary() + $"\n{where} ({bytes} bytes), {_script.Steps.Count} steps, {texts} dialogue box{(texts == 1 ? "" : "es")}.";
        if (_script.Problem != null) Info += " It can't be changed here: " + _script.Problem;
        _saved = _script.TakeSnapshot();
        for (int i = 0; i < _script.Steps.Count; i++) Lines.Add(new L2LineVm(this, _script.Steps[i], i));
        foreach (var line in Lines) FillLine(line, fields: true);
        TestNeeds = eventItem.Entry != null ? L2Cutscene.DescribeNeeds(rom, _map, eventItem.Entry.Head, eventItem.Entry.Id) : "";
        FillStartFlags();
        FillCast();
        Simulate();
        Annotate();
        UpdateBytesDump();
        SelectedLine = -1;
        SelectedLine = keepLine >= 0 && keepLine < Lines.Count ? keepLine : Lines.Count > 0 ? 0 : -1;
    }

    // what the event does in a few words: how it starts, its lines, flags and where it goes
    private string Summary()
    {
        var eventItem = _event; var script = _script;
        if (eventItem == null || script == null || _rom == null) return "";
        var parts = new List<string>();
        int once = L2Cutscene.OnceFlag(script);
        parts.Add(eventItem.IsInit ? "Start-up script" : eventItem.Entry!.Head switch
        {
            L2EventBlocks.TalkList => "Talk",
            L2EventBlocks.TriggerList => "Stepping onto the area",
            L2EventBlocks.SceneList => L2Cutscene.Starter(_rom, _map, eventItem.Entry.Id) is { } starter
                ? $"Plays when the party enters the map{(starter.Flag >= 0 ? $" while story flag {starter.Flag:X2} is {(starter.MustBeOn ? "on" : "off")}" : "")}, once (flag {starter.Played:X2}); the party stands on point {starter.Point}"
                : "Scene: plays when a \"Go to another map\" step sends the party here, or when a new game starts with it",
            L2EventBlocks.GameList => "Run by the game itself",
            _ => "Event",
        });
        int texts = script.Steps.Count(x => x.IsText);
        if (texts > 0) parts[0] += $" ({texts} box{(texts == 1 ? "" : "es")})";
        if (once >= 0) parts.Add($"plays once (story flag {once:X2})");
        var sets = script.Steps.Where(x => !x.IsText && L2ExtraFlags.Of(x.Bytes).Use == L2ExtraFlags.Use.On).Select(x => L2ExtraFlags.FlagOf(x.Bytes)).Distinct().ToList();
        var clears = script.Steps.Where(x => !x.IsText && L2ExtraFlags.Of(x.Bytes).Use == L2ExtraFlags.Use.Off).Select(x => L2ExtraFlags.FlagOf(x.Bytes)).Distinct().ToList();
        if (sets.Count > 0) parts.Add("sets story flag " + string.Join(", ", sets.Select(L2ExtraFlags.Number)));
        if (clears.Count > 0) parts.Add("clears flag " + string.Join(", ", clears.Select(L2ExtraFlags.Number)));
        int moves = script.Steps.Count(x => !x.IsText && x.Op is 0x2F or 0x30 or 0xA3 or 0x7A or 0x2D or 0x2E or 0x98 or 0x99);
        if (moves > 0) parts.Add($"{moves} move{(moves == 1 ? "" : "s")}");
        var gotos = script.Steps.Where(x => !x.IsText && x.Op == 0x16 && x.Bytes.Length >= 4).Select(x => x.Bytes[1]).Distinct().ToList();
        if (gotos.Count > 0) parts.Add("goes to " + string.Join(", ", gotos.Select(m => m < _maps.Length ? _maps[m] : $"map {m:X2}")));
        if (script.Steps.Any(x => !x.IsText && x.Op is 0x14 or 0x15 or 0x6A or L2ExtraFlags.OpIfOn or L2ExtraFlags.OpIfOff)) parts.Add("changes with story flags");
        return string.Join(" · ", parts);
    }

    // the lines

    private L2EventCommands.Words WordsFor(L2Step step) => new()
    {
        Who = id => _who.TryGetValue(id, out var w) ? w.Name : L2EventCommands.DefaultWho(id),
        Item = i => i < _items.Length ? _items[i] : $"item {i:X3}",
        Spell = i => i < _spells.Length ? _spells[i] : $"spell {i:X2}",
        Song = i => i < _songs.Length && _songs[i].Length > 0 ? $"{_songs[i]} ({i:X2})" : $"song {i:X2}",
        Map = m => m < _maps.Length ? $"{_maps[m]} ({m:X2})" : $"map {m:X2}",
        Flag = f => $"story flag {L2ExtraFlags.Number(f)}" + (L2FlagNotes.Name(L2FlagIndex.Kind.Story, f) is { } fn ? $" ({fn})" : ""),
        Jump = k => k < step.Jumps.Count && step.Jumps[k].Step is { } t && _script != null ? $"step {_script.Steps.IndexOf(t) + 1}" : "(pick a step)",
        Point = p => _points.FirstOrDefault(x => x.Id == p).Name ?? $"point {p}",
        Path = p => _paths.FirstOrDefault(x => x.Id == p).Name ?? $"path {p}",
        Area = a => _areas.FirstOrDefault(x => x.Id == a).Name ?? $"area {a}",
        Exit = x => _exits.FirstOrDefault(y => y.Id == x).Name ?? $"exit {x}",
        Door = d => _doors.FirstOrDefault(x => x.Id == d).Name ?? $"door {d}",
        Shop = s => $"shop {s:X2}",
        Capsule = c => c < L2EventCommands.CapsuleNames.Length ? L2EventCommands.CapsuleNames[c] : $"capsule monster {c}",
    };

    public string Sentence(L2Step step) => L2EventCommands.Find(step.Op)?.Describe(step.Bytes, WordsFor(step)) ?? $"Unknown command {step.Op:X2}";

    private static string Short(string text) => text.Length > 60 ? text[..60] + "..." : text;

    // who says a text step, the way the game picks the speaker (same naming as the Text page and playback)
    private string SpokenBy(L2Step step)
    {
        if (_script == null || _speakers == null) return "";
        int index = _script.Steps.IndexOf(step);
        int talkedTo = _event?.Entry is { Head: L2EventBlocks.TalkList } talk ? talk.Id : -1;
        try { _spoken = _speakers.ForScript(_script, talkedTo); } catch (Exception) { }
        if (!_spoken.TryGetValue(index, out var who)) return step.Op == 0x9E ? "Narration." : "Not reached from the start of the event.";
        return who == "Narration" ? "Narration." : step.Op == 0x10 ? who + "." : "Spoken by " + who + ".";
    }

    // fills a card from its step: the sentence, the text, and the fields (if fields is true)
    private void FillLine(L2LineVm vm, bool fields)
    {
        var step = vm.Step;
        if (step == null) return;
        vm.Filling = true;
        try
        {
            var def = L2EventCommands.Find(step.Op);
            vm.CommandName = step.IsText ? (step.Op == 0x10 ? "Ask a question" : "") : def?.Name ?? $"Unknown command {step.Op:X2}";
            vm.Description = step.IsText ? SpokenBy(step) + (step.Op == 0x10 ? " Write the question, then one line per answer at the end." : "") : Sentence(step);
            vm.WaitsText = !step.IsText && def?.Waits.Length > 0 ? "The event waits here " + def.Waits + "." : "";
            vm.Explain = ArrivalExplain(step);
            vm.SureText = def?.SureText ?? "";
            vm.BytesText = Convert.ToHexString(step.Bytes) + (step.IsText ? " + text" : "");
            if (step.IsText)
            {
                vm.SpeakerEditable = step.Op != 0x10;
                vm.Speaker = Speakers.FirstOrDefault(x => x.Value == step.Op);
                if (vm.Text.Replace("\r", "") != step.Text) vm.Text = step.Text;
                vm.CanChangeEnding = step.Op != 0x10;
                vm.Ending = Endings.FirstOrDefault(x => x.Value == step.End);
                vm.SizeText = SizeNote(step);
            }
            if (fields)
            {
                vm.Fields.Clear();
                if (def != null)
                    foreach (var paramDef in def.FieldsFor(step.Bytes))
                    {
                        if (step.IsText && paramDef.Kind == L2Kind.Choice && paramDef.Options?.Any(o => o.Item1 is >= 0x61 and <= 0x67) == true) continue;   // the hero speaking is the speaker list
                        vm.Fields.Add(MakeField(step, paramDef));
                    }
                vm.Notify(nameof(L2LineVm.HasFields));
            }
            var notes = new List<string>();
            var from = _script!.Steps.Select((x, i) => (x, i)).Where(t => t.x.Jumps.Any(j => j.Step == step)).Select(t => t.i + 1).ToList();
            if (from.Count > 0) notes.Add("from step " + string.Join(", ", from));
            if (_owners != null && step.At >= 0 && _owners.TryGetValue(step.At, out var owners) && owners.Count > 1 && _event?.Entry is { } thisEntry)
                notes.Add("shared with " + string.Join(", ", owners.Where(x => x != thisEntry).Take(3).Select(EntryName)) + (owners.Count > 4 ? "..." : ""));
            if (step.EndsFlow && !step.IsText && step.Op is not (0x00 or 0x42)) notes.Add("the event doesn't go on below");
            if (step.IsText && L2Cutscene.SpeakerActor(step.Bytes) is var hero && _heroStandIns.TryGetValue(hero, out int standIn))
                notes.Add($"{L2EventCommands.HeroNames[hero]} also stands on this map as character {standIn:X2}: when {L2EventCommands.HeroNames[hero]} isn't in the party here, the box points at someone else, so pick {standIn:X2} as the speaker (A character of the map...)");
            vm.Note = string.Join("; ", notes);
            vm.Notify(nameof(L2LineVm.CanOpenScene));
            vm.Notify(nameof(L2LineVm.OpenSceneText));
        }
        finally { vm.Filling = false; }
    }

    private string SizeNote(L2Step step)
    {
        if (_rom == null) return "";
        string? problem = L2EventText.Check(step.Text, _rom);
        int size = L2EventScript.SizeOf(_rom, step, step.At >= 0);
        int longest = L2Events.LongestLine(step.Text);
        string room = step.At >= 0 && size > step.OriginalLength ? $" (it had {step.OriginalLength}: longer text moves the map's events to the event space when saved)" : "";
        string warn = L2Events.WidthWarning(step.Text);
        return problem ?? $"{size} bytes{room}. Longest line: {longest} letters, {L2Events.LineCount(step.Text)} lines." + (warn.Length > 0 ? " Warning: " + warn : "");
    }

    private string EntryName(L2EventBlocks.Entry entry) => entry.Head switch
    {
        L2EventBlocks.TalkList => $"talking to {Name(entry.Id)}",
        L2EventBlocks.TriggerList => $"area {entry.Id}",
        L2EventBlocks.SceneList => $"scene {entry.Id}",
        _ => $"game event {entry.Id}",
    };

    private List<L2Option> ChoicesOf(L2Kind kind)
    {
        if (kind == L2Kind.Sound)   // (not kept: names can change in Settings)
            return Enumerable.Range(0, Core.Audio.SoundNames.Count(true)).Select(i => new L2Option(i, Core.Audio.SoundNames.Label(true, i))).ToList();
        if (_choices.TryGetValue(kind, out var cached)) return cached;
        var rom = _rom!;
        IEnumerable<L2Option> options = kind switch
        {
            L2Kind.Hero => L2EventCommands.HeroNames.Select((n, i) => new L2Option(i, n)),
            L2Kind.Item => _items.Select((n, i) => new L2Option(i, $"{i:X3} {n}")),
            L2Kind.Spell => _spells.Select((n, i) => new L2Option(i, $"{i:X2} {n}")),
            L2Kind.Song => _songs.Select((n, i) => new L2Option(i, $"{i:X2} {(n.Length > 0 ? n : "(empty)")}")),
            L2Kind.Map => _maps.Select((n, i) => new L2Option(i, $"{i:X2} {n}")),
            L2Kind.Capsule => L2EventCommands.CapsuleNames.Select((n, i) => new L2Option(i, n)),
            L2Kind.Flag => Enumerable.Range(0, L2ExtraFlags.IsApplied(rom) ? L2ExtraFlags.Last + 1 : 0x100).Select(f => new L2Option(f, FlagChoice(rom, f))),
            L2Kind.Point => _points.Select(x => new L2Option(x.Id, x.Name)),
            L2Kind.Path => _paths.Select(x => new L2Option(x.Id, x.Name)),
            L2Kind.Area => _areas.Select(x => new L2Option(x.Id, x.Name)),
            L2Kind.Exit => _exits.Select(x => new L2Option(x.Id, x.Name)),
            L2Kind.Door => _doors.Select(x => new L2Option(x.Id, x.Name)),
            L2Kind.Who => WhoChoices(false),
            L2Kind.WhoOrAll => WhoChoices(true),
            _ => Enumerable.Empty<L2Option>(),
        };
        return _choices[kind] = options.ToList();
    }

    private L2FieldVm MakeField(L2Step step, L2ParamDef paramDef)
    {
        int value;
        try { value = paramDef.Get(step.Bytes); } catch (Exception) { value = 0; }
        BitmapSource? picture = null;
        IEnumerable<L2Option>? choices = null;
        bool hex = false;
        switch (paramDef.Kind)
        {
            case L2Kind.Choice: choices = paramDef.Options!.Select(o => new L2Option(o.Item1, o.Item2)); break;
            case L2Kind.Jump:
                value = paramDef.JumpIndex < step.Jumps.Count && step.Jumps[paramDef.JumpIndex].Step is { } target ? _script!.Steps.IndexOf(target) : -1;
                choices = _script!.Steps.Select((x, i) => new L2Option(i, $"step {i + 1}: {Short(x.IsText ? x.Text.Replace("\n", " ") : Sentence(x))}"));
                break;
            case L2Kind.Who or L2Kind.WhoOrAll:
                choices = ChoicesOf(paramDef.Kind);
                if (_who.TryGetValue(value, out var who) && who.Sprite >= 0) picture = SpritePicture(who.Sprite);
                break;
            case L2Kind.Hero or L2Kind.Item or L2Kind.Spell or L2Kind.Song or L2Kind.Sound or L2Kind.Map or L2Kind.Capsule or L2Kind.Flag
                or L2Kind.Point or L2Kind.Path or L2Kind.Area or L2Kind.Exit or L2Kind.Door:
                choices = ChoicesOf(paramDef.Kind); break;
            case L2Kind.Scene: choices = SceneChoices(step.Bytes.Length > 1 ? step.Bytes[1] : 0, value); break;
            case L2Kind.ArrivalPoint:
                int arrivalMap = step.Bytes.Length > 1 ? step.Bytes[1] : 0;
                choices = L2MapInfo.Records(_rom!, arrivalMap, L2MapInfo.Points, 3).Select(r => new L2Option(r.Bytes[0], $"point {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})"));
                break;
            case L2Kind.Sprite:
                hex = true;
                picture = SpritePicture(value);
                break;
            case L2Kind.Hex or L2Kind.Variable or L2Kind.Battle or L2Kind.Shop: hex = true; break;
        }
        if (StoryFieldChoices(step, paramDef, value) is { } story)
        {
            choices = story;
            if (paramDef.Kind == L2Kind.Who && _rom != null && L2Persistent.Exists(_rom, value)) picture = SpritePicture(L2Persistent.Record(_rom, value)[1]);
        }
        return new L2FieldVm(this, step, paramDef, choices, value, hex, picture);
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

    private static int SafeGet(L2ParamDef paramDef, byte[] bytes) { try { return paramDef.Get(bytes); } catch (Exception) { return -1; } }

    private static string FlagChoice(RomBuffer rom, int flag)
    {
        var uses = L2FlagUses.Get(rom).GetValueOrDefault(flag);
        string use = flag is >= 0xF0 and < 0x100 ? "cleared on every map change" : uses == null ? (flag >= 0x100 ? "extra flag, free" : "not used by the game") : $"used {uses.Count}x";
        return L2StoryFlags.Label(flag) is { } label ? $"{L2ExtraFlags.Number(flag)}  {label}  ({use})" : $"{L2ExtraFlags.Number(flag)}  {use}";
    }

    private void UpdateBytesDump()
    {
        if (_script == null || _rom == null) { BytesDump = ""; return; }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _script.Steps.Count; i++)
        {
            var s = _script.Steps[i];
            string offset = s.At >= 0 ? $"{s.At:X6}" : "   new";
            string bytes = Convert.ToHexString(L2EventScript.Encode(_rom, s, 0, s.At >= 0));
            if (bytes.Length > 64) bytes = bytes[..64] + "...";
            sb.AppendLine($"{i + 1,4}. {offset}  {bytes,-66}  {(s.IsText ? "\"" + s.Text.Replace("\n", " / ") + "\"" : Sentence(s))}");
        }
        BytesDump = sb.ToString();
    }

    // editing

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

    // a sound was renamed in Settings, so the cards with a sound show the new name
    public void RefreshSoundNames()
    {
        if (_script == null) return;
        foreach (var line in Lines)
            if (line.Step is { IsText: false } step && L2EventCommands.Find(step.Op)?.FieldsFor(step.Bytes).Any(f => f.Kind == L2Kind.Sound) == true)
                FillLine(line, fields: true);
    }

    // after an edit: the cards show the new sentences (every card's fields again if the structure changed) and
    // the stage is simulated again
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
                // the cards follow the steps: kept where the step stays, made for new steps, dropped for removed ones
                var byStep = Lines.Where(l => l.Step != null).ToDictionary(l => l.Step!, l => l);
                Lines.Clear();
                for (int i = 0; i < _script.Steps.Count; i++)
                {
                    var step = _script.Steps[i];
                    if (!byStep.TryGetValue(step, out var vm)) vm = new L2LineVm(this, step, i);
                    vm.Index = i; vm.RefreshNumber();
                    Lines.Add(vm);
                }
                foreach (var line in Lines) FillLine(line, fields: true);
            }
            else foreach (var line in Lines) FillLine(line, fields: line == fieldsOf);
            FillStartFlags();
            FillCast();
            Simulate();
            Annotate();
            UpdateBytesDump();
            Info = Info.Contains('\n') ? "What it does: " + Summary() + Info[Info.IndexOf('\n')..] : Info;
        }
        finally { _refreshing = false; }
        if (!IsPlaying) ShowStage();
    }

    // a field changed (from L2FieldVm)
    public void SetField(L2FieldVm field, int value)
    {
        if (_refreshing || _script == null || !CanEdit) return;
        var step = field.Step;
        var paramDef = field.Def;
        var line = Lines.FirstOrDefault(l => l.Step == step);
        _textLine = null;
        Remember();
        bool rebuild = false;
        if (paramDef.Kind == L2Kind.Jump)
        {
            while (step.Jumps.Count <= paramDef.JumpIndex) step.Jumps.Add(new L2Jump());
            step.Jumps[paramDef.JumpIndex] = new L2Jump { Step = value >= 0 && value < _script.Steps.Count ? _script.Steps[value] : null };
        }
        else if (paramDef.Rebuild != null)
        {
            var (bytes, map) = paramDef.Rebuild(step.Bytes, value);
            var old = step.Jumps.ToList();
            int count = L2EventScript.JumpPositions(bytes).Count;
            var next = Next(step);
            step.Jumps.Clear();
            for (int k = 0; k < count; k++)
            {
                int from = map == null ? (k < old.Count ? k : -1) : k < map.Length ? map[k] : -1;
                step.Jumps.Add(from >= 0 && from < old.Count ? old[from] : new L2Jump { Step = next });
            }
            step.Bytes = bytes;
            rebuild = true;
        }
        else
        {
            var bytes = (byte[])step.Bytes.Clone();
            paramDef.Set(bytes, value);
            step.Bytes = bytes;
            // a go-to step sent to another map keeps a scene and a point that map has
            if (paramDef.Kind == L2Kind.Map && FitArrival(step) is { } fitted) step.Bytes = fitted;
            rebuild = L2EventCommands.Find(step.Op)?.ParamsFor != null || paramDef.Kind is L2Kind.Map or L2Kind.Who or L2Kind.Sprite;
        }
        if (rebuild) Dispatch(() => Changed(false, line));
        else Changed(false);
        if (paramDef.Kind is L2Kind.Who or L2Kind.WhoOrAll && value == 0 && step.Op is 0x99 or 0x9D)
            Dispatch(() => LeaderVanishToStandIn());
        // a hero who may not be in the story's party here gets a warning on the step's card with "Use a map character
        // that looks like ..." (never done by itself. the story's party is a guess from the story flags, and it's wrong in
        // a hack whose hero joined in an event the flags don't lead back to. when it was done anyway, picking Dekar for one
        // step made a stand-in of all 49 of the event's Dekar steps)
    }

    private L2Step? Next(L2Step step)
    {
        int index = _script!.Steps.IndexOf(step);
        return index + 1 < _script.Steps.Count ? _script.Steps[index + 1] : step;
    }

    public void SetSpeaker(L2LineVm line, int opener)
    {
        if (_refreshing || line.Step is not { IsText: true } step || step.Op == opener || !CanEdit) return;
        _textLine = null;
        Remember();
        step.Bytes = opener switch
        {
            0x13 => new byte[] { 0x13, (byte)(step.Op == 0x13 ? step.Bytes[1] : _who.Keys.FirstOrDefault(k => k >= 0x50, 0x50)) },
            0x9E => new byte[] { 0x9E, 0x05 },
            _ => new byte[] { (byte)opener },
        };
        Dispatch(() => Changed(false, line));
    }

    public void SetText(L2LineVm line, string value)
    {
        if (_refreshing || line.Step is not { IsText: true } step || !CanEdit) return;
        string text = value.Replace("\r", "");
        if (text == step.Text) return;
        if (_textLine != line) { Remember(); _textLine = line; }
        step.Text = text;
        line.SizeText = SizeNote(step);
        Changed(false);
    }

    public void SetEnding(L2LineVm line, int end)
    {
        if (_refreshing || line.Step is not { IsText: true } step || step.End == end || !CanEdit) return;
        _textLine = null;
        Remember();
        step.End = (byte)end;
        Changed(false);
    }

    // where a new step goes: below the picked one, else at the end (above a closing End)
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

    // adds a command from the menu at at (-1 = InsertIndex)
    public void AddCommand(L2CommandDef def, int at = -1)
    {
        if (def.Template == null) return;
        if (def.Ops[0] == 0x10) { AddText(new byte[] { 0x10, 0x02, 0, 0, 0, 0 }, "Is that so?\nYes\nNo", 0x0B, at); return; }
        if (L2EventScript.TextOpeners.Contains(def.Template[0])) { AddText(def.Template, "...", 0x01, at); return; }
        // a ring comes with what the game always does with one: it turns, closes in and goes, and the event waits for it
        if (IsRing(def.Template[0])) { AddRing(def.Template, at); return; }
        Insert(at, new L2Step { Bytes = (byte[])def.Template.Clone() });
    }

    // a ring (on a ring number no other ring of the event uses) plus the game's steps that go with it
    public void AddRing(byte[] start, int at = -1)
    {
        var ring = (byte[])start.Clone();
        ring[1] = (byte)FreeRing();
        Insert(at, RingSteps(ring));
        Status = "A ring and the steps the game always gives one (it turns, closes in and goes; the event waits for it). " + Status;
    }

    // adds text spoken by the opener's speaker
    public void AddText(byte[] opener, string text, byte end, int at = -1) =>
        Insert(at, new L2Step { Bytes = (byte[])opener.Clone(), IsText = true, Text = text, End = end, OriginalEnd = 0xFF, OriginalText = "\u0001" });

    // puts steps into the script at at (-1 = below the picked step) and picks the first one
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
        var gone = _script.Steps[index];
        _textLine = null;
        Remember();
        _script.Steps.RemoveAt(index);
        // the last step taken out while a jump leads to it, so an End takes its place (going back to the step before
        // would run that step again)
        bool jumped = _script.Steps.Any(st => st.Jumps.Any(j => j.Step == gone));
        if (index == _script.Steps.Count && jumped) _script.Steps.Add(new L2Step { Bytes = new byte[] { 0x00 } });
        var instead = index < _script.Steps.Count ? _script.Steps[index] : _script.Steps[index - 1];
        foreach (var st in _script.Steps) foreach (var j in st.Jumps) if (j.Step == gone) j.Step = instead;
        Changed(true);
        SelectedLine = Math.Min(index, Lines.Count - 1);
    }

    // moves a step to a new index (jumps keep their targets)
    public void Move(int from, int to)
    {
        if (_script == null || !CanEdit || from < 0 || from >= _script.Steps.Count) return;
        to = Math.Clamp(to, 0, _script.Steps.Count - 1);
        if (to == from) return;
        _textLine = null;
        Remember();
        var step = _script.Steps[from];
        _script.Steps.RemoveAt(from);
        _script.Steps.Insert(to, step);
        Changed(true);
        SelectedLine = to;
        HighlightLine(to);
    }

    public void Duplicate(int index)
    {
        if (_script == null || !CanEdit || index < 0 || index >= _script.Steps.Count) return;
        var step = _script.Steps[index];
        var copy = new L2Step { Bytes = (byte[])step.Bytes.Clone(), IsText = step.IsText, Text = step.Text, End = step.End, OriginalEnd = 0xFF, OriginalText = "\u0001" };
        Insert(index + 1, copy);
        for (int k = 0; k < step.Jumps.Count && k < copy.Jumps.Count; k++) copy.Jumps[k] = new L2Jump { Step = step.Jumps[k].Step };
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
        // (as the ROM has it now, another page may have moved the map's events)
        if (_rom != null && _map >= 0) { ReadMap(); if (FindAgain() is { } again) _event = again; }
        ReadScript(keep);
        MarkRead();
        Status = "Put back as the project has it.";
    }

    // puzzle scripts: writes the picked command's bytes (same size)
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
        MarkRead();
        Status = "Written.";
        Saved?.Invoke("puzzle command");
    }

    partial void OnSelectedLineChanged(int value)
    {
        PuzzleMarks.Clear();
        if (IsPuzzle && value >= 0 && value < Lines.Count && Lines[value].PuzzleOp is { } op && _rom != null)
        {
            PuzzleBytes = string.Join(" ", _rom.ReadBytes(op.At, op.Length).Select(b => b.ToString("X2")));
            // what the step touches, marked on the stage
            try
            {
                _puzzleText ??= new L2PuzzleText(_rom, _map);
                foreach (var (x, y, w, h, label) in _puzzleText.Marks(_rom, op.At))
                    PuzzleMarks.Add(new L2StageMark { Left = x * 16, Top = y * 16, Width = w * 16, Height = h * 16, Label = label, IsSource = label == "copied from" });
            }
            catch (Exception) { }
        }
        else if (!IsPuzzle && !IsChangingMap && _script != null && value >= 0 && value < _script.Steps.Count && _script.Steps[value] is { IsText: false } copyStep &&
                 copyStep.Op is 0x43 or 0x87 or 0xB4 && copyStep.Bytes.Length >= 3 && _rom != null)
        {
            // a tile copy: where the map changes (blue) and where the new look comes from (orange)
            var areas = L2MapInfo.Records(_rom, _map, L2MapInfo.Areas, 5).GroupBy(r => (int)r.Bytes[0]).ToDictionary(g => g.Key, g => g.First().Bytes);
            if (areas.TryGetValue(copyStep.Bytes[1], out var onto) && areas.TryGetValue(copyStep.Bytes[2], out var from))
            {
                int w = from[3] - from[1], h = from[4] - from[2];
                PuzzleMarks.Add(new L2StageMark { Left = onto[1] * 16, Top = onto[2] * 16, Width = w * 16, Height = h * 16, Label = $"changes (area {copyStep.Bytes[1]})" });
                PuzzleMarks.Add(new L2StageMark { Left = from[1] * 16, Top = from[2] * 16, Width = w * 16, Height = h * 16, Label = $"new look (area {copyStep.Bytes[2]})", IsSource = true });
            }
        }
        else if (!IsPuzzle && !IsChangingMap && !IsPickingPlace) MarkPlaces();
        _textLine = null;
        if (!IsPlaying) ShowStage();
        // (after the stage, which brings the view back to the camera)
        if (PuzzleMarks.Count > 0) ScrollRequested?.Invoke(PuzzleMarks[0].Left + PuzzleMarks[0].Width / 2, PuzzleMarks[0].Top + PuzzleMarks[0].Height / 2);
    }

    // the counter 10-1F a step uses while the extra story flags are in (they're kept there), or null
    private int? CounterHoldsFlags(byte[] bytes)
    {
        if (bytes.Length < 2 || !L2ExtraFlags.IsApplied(_rom!)) return null;
        if (bytes[0] is 0x1D or 0x1E or 0x1F or 0x12 && L2ExtraFlags.HoldsFlags(bytes[1])) return bytes[1];
        if (bytes[0] == 0x14)
            foreach (var condition in L2Conditions.Parse(bytes))
                if (condition.Kind == L2Conditions.KCounter && condition.At + 1 < bytes.Length && L2ExtraFlags.HoldsFlags(bytes[condition.At + 1])) return bytes[condition.At + 1];
        return null;
    }

    // picks and shows the step read from file offset at (a use on the Flags tab)
    public void GoToStepAt(int at)
    {
        if (_script == null) return;
        int index = _script.Steps.FindIndex(s => s.At == at);
        if (index < 0) return;
        SelectedLine = index;
        HighlightLine(index);
    }

    // flag names (or the extra flags) changed, so the flag lists and the steps' words get made again
    public void NamesChanged()
    {
        _choices.Remove(L2Kind.Flag);
        _choices.Remove(L2Kind.Door);
        _choices.Remove(L2Kind.Who);
        _choices.Remove(L2Kind.WhoOrAll);
        if (_rom != null && _map >= 0) ReadMap();
        // the cards again with the new words and lists (the steps stay as they are, nothing gets read from the ROM)
        if (_script == null || _rom == null) return;
        _refreshing = true;
        try { foreach (var line in Lines) if (line.Step != null) FillLine(line, fields: true); }
        finally { _refreshing = false; }
        FillStartFlags();
    }

    public void HighlightLine(int index)
    {
        foreach (var line in Lines) line.IsHighlighted = false;
        if (index < 0 || index >= Lines.Count) return;
        Lines[index].IsHighlighted = true;
        LineHighlighted?.Invoke(index);
    }

    public void ShowFlag(int flag) { if (flag >= 0) FlagRequested?.Invoke(flag); }

    // opens the scene a "go to another map" step starts
    public void OpenScene(L2LineVm line)
    {
        if (!line.CanOpenScene) return;
        OpenRequested?.Invoke(line.Step!.Bytes[1], L2EventBlocks.SceneList, line.Step.Bytes[2]);
    }

    private static void Dispatch(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

    // saving

    public bool HasPendingEdits => IsDirty && CanEdit && _script != null && _rom != null;

    public string? WritePendingEdits()
    {
        if (!HasPendingEdits) return null;
        return SaveNow(quiet: true) ? null : $"{Title}: {Status}";
    }

    [RelayCommand]
    public void Save() => SaveNow(quiet: false);

    // writes the event to the ROM, false if it couldn't
    public bool SaveNow(bool quiet)
    {
        if (_rom == null || _main == null || _script == null || _event == null || !CanEdit) return false;
        if (!IsDirty) { Status = "Nothing changed."; return true; }
        // the steps here were read before another page wrote this event (or moved its map's events), so writing them
        // would put back what that page changed
        if (ChangedElsewhere()) { Status = "Not saved: this event was changed on another page after it was opened here. Revert reads it again; then make your changes again."; return false; }
        // every text must be text the game can show (texts not changed get written as they are, the game's own can hold
        // codes typed text can't). checked before the fitting below adds steps, so the step number is the card's
        for (int i = 0; i < _script.Steps.Count; i++)
            if (_script.Steps[i].TextChanged && L2EventText.Check(_script.Steps[i].Text, _rom) is { } problem) { Status = $"Not saved: step {i + 1}: {problem}"; return false; }
        // the fitting adds steps (split texts, ends of effects, rings, redraws). if the write then fails the steps go
        // back to how they were, so the cards and the steps stay in step
        var beforeFit = _script.TakeSnapshot();
        int fitted = FitTexts(all: false);
        int rings = FitRings();
        int ended = _event?.Entry != null ? FitEffects() : 0;
        int drawn = _event?.Entry != null ? FitMapDraws() : 0;
        // the last step has to end the event, or the game runs on into whatever follows
        bool added = false;
        if (!_script.Steps[^1].EndsFlow) { _script.Steps.Add(new L2Step { Bytes = new byte[] { 0x00 } }); added = true; }
        try
        {
            string result = _event.IsInit ? L2EventBlocks.SaveInit(_rom, _map, _script) : L2EventBlocks.Save(_rom, _map, _event.Entry!, _script);
            _main.NotifyRomModified($"Lufia II: {MapName} event \"{_event.Title}\"");
            L2FlagUses.Invalidate();
            int keep = SelectedLine;
            IsDirty = false;
            // the entry may have moved, so read the map's lists again and open the same event
            var entry = _event.Entry;
            bool init = _event.IsInit;
            ReadMap();
            var items = ItemsOf(_rom, _map);
            _event = init ? items[0] : items.FirstOrDefault(x => x.Entry?.Head == entry!.Head && x.Entry?.Id == entry.Id) ?? _event;
            ReadScript(keep);
            MarkRead();
            Status = "Saved in the project. " + result + (added ? " An End step was added at the bottom." : "") +
                     (fitted > 0 ? $" {fitted} text{(fitted > 1 ? "s were" : " was")} wider or longer than the box: the lines were broken to fit ({L2Events.BoxWidth} letters, {L2Events.BoxLines} lines a box, more boxes when needed)." : "") +
                     (ended > 0 ? " A colour, shaking or ring was still going at the end of the event: steps that end it were added before the end (the game's events always end them)." : "") +
                     (rings > 0 ? " A wait for a ring that nothing takes away (the game would wait there forever) now has the game's steps before it: the ring turns, closes in and goes." : "") +
                     (drawn > 0 ? " The map was changed while the screen is off: \"Draw the map again\" was added after it (without it the game doesn't show the change)." : "");
            Saved?.Invoke(result);
            return true;
        }
        catch (Exception ex)
        {
            _script.Restore(beforeFit);   // (the End added too)
            if (fitted + rings + ended + drawn > 0) Changed(true);
            Status = "Not saved: " + ex.Message;
            return false;
        }
    }

    // before the editor shows another event or closes, the edits get written by themselves. if that fails it
    // asks whether to drop them. true = go on
    public bool ConfirmLeave()
    {
        if (!HasPendingEdits) return true;
        if (SaveNow(quiet: true)) return true;
        return System.Windows.MessageBox.Show($"The changes to \"{_event?.Title}\" couldn't be written ({Status}). Drop them?", "Lufia II events",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
    }

    // adds steps at the end of the open event (the "continue another event" choice of a new event). every
    // place where it ends goes on with them instead
    public void ContinueWith(IEnumerable<L2Step> added)
    {
        if (_script == null || !CanEdit) return;
        _textLine = null;
        Remember();
        var steps = added.ToList();
        var first = steps[0];
        // an End at the very bottom just goes, the new steps follow it
        if (_script.Steps[^1] is { IsText: false, Op: 0x00 or 0x42 } bottom && _script.Steps.Count > 1)
        {
            _script.Steps.Remove(bottom);
            foreach (var step in _script.Steps) foreach (var j in step.Jumps) if (j.Step == bottom) j.Step = first;
        }
        foreach (var step in _script.Steps.ToList())
        {
            if (step.IsText && step.End == 0x00) { step.End = 0x01; var go = new L2Step { Bytes = new byte[] { 0x1C, 0, 0 } }; go.Jumps.Add(new L2Jump { Step = first }); _script.Steps.Insert(_script.Steps.IndexOf(step) + 1, go); }
            else if (!step.IsText && step.Op is 0x00 or 0x42) { step.Bytes = new byte[] { 0x1C, 0, 0 }; step.Jumps.Clear(); step.Jumps.Add(new L2Jump { Step = first }); }
        }
        _script.Steps.AddRange(steps);
        Changed(true);
        SelectedLine = _script.Steps.IndexOf(first);
        HighlightLine(SelectedLine);
        int stuck = _script.Steps.Count(s => !s.IsText && s.Op is 0x18 or 0x19 or 0x81 or 0xBC or 0x16 or 0x5E or 0x83 or 0xB8);
        Status = "New steps added at the end; every place where the event ended now goes on with them." +
                 (stuck > 0 ? $" {stuck} place{(stuck == 1 ? "" : "s")} where it opens a shop, the church, a capsule monster or goes to another map still end{(stuck == 1 ? "s" : "")} it: the new steps can't follow those." : "");
    }

    // the stage

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

    // where the leader stands as the event starts: the arrival point of a scene, the area of a stepping-on
    // event, in front of the character talked to, or where you put the party
    private (int X, int Y) LeaderStart()
    {
        if (_startOverride is { } start) return start;
        var rom = _rom!;
        var entry = _event?.Entry;
        if (entry?.Head == L2EventBlocks.SceneList)
        {
            int pointId = GamePoint();
            var spot = _pointSpots.FirstOrDefault(x => x.Id == pointId);
            if (spot != default) return (spot.X, spot.Y);
        }
        if (entry?.Head == L2EventBlocks.TriggerList && L2MapInfo.Records(rom, _map, L2MapInfo.Triggers, 5).FirstOrDefault(r => r.Bytes[0] == entry.Id) is { Bytes: var area } && area != null)
            return (area[1], area[2]);
        if (entry?.Head == L2EventBlocks.TalkList && L2MapInfo.Records(rom, _map, L2MapInfo.Characters, 8).FirstOrDefault(r => 0x4F + r.Bytes[0] == entry.Id) is { Bytes: var charRecord } && charRecord != null)
            return (charRecord[1], charRecord[2] + 1);
        if (TestPoint != null && _pointSpots.FirstOrDefault(x => x.Id == TestPoint.Value) is var testSpot && testSpot != default) return (testSpot.X, testSpot.Y);
        return _pointSpots.Count > 0 ? (_pointSpots[0].X, _pointSpots[0].Y) : _arrival ?? (8, 8);
    }

    private void Simulate()
    {
        if (_rom == null || _map < 0) { _states = new(); _run = new(); _firstVisit.Clear(); _start = null; TimelineItems = new(); Resimulated?.Invoke(); return; }
        var (x, y) = LeaderStart();
        // the map as the game sets it up with these story flags (only the characters its start-up script makes with them)
        var start = L2Cutscene.Initial(_rom, _map, x, y, _persistent, _flagsOn, _countersAtStart);
        int talkedTo = _event?.Entry is { Head: L2EventBlocks.TalkList } talk ? talk.Id : -1;
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

    // the stage after step stepIndex: as playback first reaches it, or (for a step playback doesn't
    // reach with these flags) read top to bottom
    private L2StageState? After(int stepIndex) =>
        _firstVisit.TryGetValue(stepIndex, out int runIndex) ? _run[runIndex].After : stepIndex + 1 < _states.Count && stepIndex >= 0 ? _states[stepIndex + 1] : null;

    private L2StageState? Before(int stepIndex) =>
        _firstVisit.TryGetValue(stepIndex, out int runIndex) ? (runIndex > 0 ? _run[runIndex - 1].After : _start) : stepIndex >= 0 && stepIndex < _states.Count ? _states[stepIndex] : null;

    private string BranchText(L2StageState state) =>
        state.BranchWhy + (state.Jumped
            ? state.BranchTo >= 0 ? $": goes to step {state.BranchTo + 1}" : ": goes to a step outside this event (playback stops)"
            : state.BranchTo >= 0 ? $": goes on to step {state.BranchTo + 1}" : ": the event ends");

    // marks each card with what playback does there: a check's branch, steps it doesn't reach, walks the game stops
    private void Annotate()
    {
        if (_script == null) return;
        var screenOff = ScreenOff();
        for (int i = 0; i < Lines.Count && i < _script.Steps.Count; i++)
        {
            var vm = Lines[i];
            bool reached = _firstVisit.ContainsKey(i);
            vm.Reached = reached || _run.Count == 0;
            vm.Warning = After(i)?.Warning ?? "";
            string flow = "";
            var step = _script.Steps[i];
            if ((step.Jumps.Count > 0 || step.Op is 0x14 && !step.IsText) && _firstVisit.TryGetValue(i, out int runIndex) && _run[runIndex].After.BranchWhy != null)
            {
                int times = _run.Count(r => r.Step == i);
                flow = "Playback: " + BranchText(_run[runIndex].After) + (times > 1 ? $" (the first of {times} times)" : "");
            }
            vm.Flow = !reached && _run.Count > 0 ? "Playback doesn't reach this step with these story flags." : flow;
            // a hero who isn't in the party here, the game's character commands then act on the leader
            vm.HeroFix = -1; vm.HeroFixText = "";
            // a map change that would leave the screen black
            ArrivalWarning(vm, step);
            // commands only the start-up script or the "before the map loads" event may hold
            if (!step.IsText && step.Op is 0x68 or 0x7B && _event is { IsInit: false })
                vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") +
                             "This only works in the map's start-up script: in an event the game puts Maxim on that character's place (checked in the game). " +
                             "To bring in a character here, use + Add a character in the Characters section.";
            if (!step.IsText && step.Op == 0x83 && !(_event?.Entry is { Head: L2EventBlocks.GameList, Id: 2 }))
                vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") +
                             "This only works in the \"Before the map loads\" event: anywhere else the event stops here (checked in the game). Use Go to another map or Leave through an exit instead.";
            if (!step.IsText && _rom != null && CounterHoldsFlags(step.Bytes) is { } held)
                vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") +
                             $"Counter {held:X2} holds the extra story flags {L2ExtraFlags.First + (held - 0x10) * 8:X3}-{L2ExtraFlags.First + (held - 0x10) * 8 + 7:X3} in this ROM: " +
                             "changing it turns those flags on and off. Use counter 0F or below.";
            if (RingWaitsForever(i))
                vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") +
                             "Nothing takes this ring away (a ring stands still and stays until \"Grow or shrink then remove it\" or \"Remove a ring\"), so the game waits here forever. " +
                             "Saving adds the game's steps before this one: the ring turns, closes in and goes.";
            if (RingStandsStill(i))
            {
                vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") +
                             "Nothing makes this ring move, so it stands still where it appears (one sprite is a single dot). The game's rings always turn and close in.";
                if (vm.HeroFix < 0) { vm.HeroFix = -2; vm.HeroFixText = "Make it like the game's ring (it turns, closes in and goes)"; }
            }
            if (CopyNotDrawn(i, screenOff))
                vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") +
                             "The screen is off here (before the scene fades in, or after a fade to black): the game changes the map but doesn't draw it, " +
                             "so the change doesn't show (checked in the game). Saving adds \"Draw the map again\" after this step.";
            if (LeaderVanishes(i))
            {
                vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") +
                             "The game never makes Maxim (the leader) flicker or vanish: it frees his place, and when the event ends he turns up on " +
                             "another tile and the map's characters go. A map character that looks like him can flicker away instead (Test in the game does it by itself).";
                vm.HeroFix = 0;
                vm.HeroFixText = "Use a map character that looks like Maxim";
            }
            if (!step.IsText && step.Op == 0x00 && _event?.Entry != null)
            {
                var (colour, shake, rings) = EffectsAt(i);
                var left = new List<string>();
                if (colour != null) left.Add("a colour (it would keep flashing on the map)");
                if (shake) left.Add("the shaking");
                if (rings.Count > 0) left.Add("ring " + string.Join(", ", rings.Select(r => r.ToString("X2"))));
                if (left.Count > 0)
                    vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") +
                                 $"The event ends with {string.Join(" and ", left)} still going: saving adds the steps that end it before this one.";
                if (Before(i)?.Actors.GetValueOrDefault(0) is { Visible: false })
                    vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") +
                                 "The event ends with Maxim hidden: the player walks about unseen. Show him again, or go to the next scene (Go to another map) before the end.";
            }
            if (!step.IsText && L2EventCommands.Find(step.Op) is { } def && step.Op is not (0x2B or 0x2C))
            {
                var (party, _) = PartyAt(i);
                foreach (var paramDef in def.FieldsFor(step.Bytes))
                {
                    if (paramDef.Kind is not (L2Kind.Who or L2Kind.WhoOrAll)) continue;
                    int hero;
                    try { hero = paramDef.Get(step.Bytes); } catch (Exception) { continue; }
                    if (hero is < 1 or > 6 || !HeroAbsent(i, hero)) continue;
                    bool inTest = party.Contains(hero);
                    string heroName = L2EventCommands.HeroNames[hero];
                    string them = hero is 1 or 4 ? "her" : "him";
                    vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") + (inTest
                        ? $"{heroName} may not be in the party here: going by the story flags, no event before this one makes {them} join. If {heroName} is in the party here " +
                          $"in your game, leave it as it is. If not, the game's character commands skip {them} and move Maxim (the leader) instead, with the camera " +
                          $"(Test in the game adds {them}, so the test still plays): have {them} join first (A hero joins the party), or use a map character that looks like {heroName}."
                        : $"{heroName} isn't in the party here: the game's character commands skip {them} and move Maxim (the leader) instead, and the camera follows. " +
                          $"For a cutscene, use a map character that looks like {heroName}.");
                    vm.HeroFix = hero;
                    vm.HeroFixText = $"Use a map character that looks like {heroName}";
                    break;
                }
                // a map character hidden at this step: put, turned or walked, the game moves the leader instead (the
                // game's own events never do it, 0 of 169 puts on a point, 0 of 606 turns)
                if (vm.HeroFix < 0 && step.Op is 0x2F or 0x30 or 0x31 or 0x33 && Before(i) is { } state)
                    foreach (var paramDef in def.FieldsFor(step.Bytes))
                    {
                        if (paramDef.Kind is not (L2Kind.Who or L2Kind.WhoOrAll)) continue;
                        int who;
                        try { who = paramDef.Get(step.Bytes); } catch (Exception) { continue; }
                        if (who < 0x10 || !state.Actors.TryGetValue(who, out var actor) || actor.Visible || !actor.Placed) continue;
                        vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") +
                                     $"{L2EventCommands.Cap(Name(who))} is hidden at this step: the game skips a hidden character and moves another one instead (the last one the event moved, or Maxim). " +
                                     "Show it first (Show a character, or A character appears on a point).";
                        break;
                    }
            }
        }
        FlowNote = _run.Count == 0 ? "" :
            $"Playback runs {Lines.Count(l => l.Reached)} of the {Lines.Count} steps" + (_run.Count >= 3000 ? " (it goes round a loop: it stops after 3000 commands)." : ".");
    }

    private void BuildTimeline()
    {
        var items = new List<L2TimelineItem>();
        var steps = _script?.Steps;
        if (steps == null) { TimelineItems = items; return; }
        int startFrame = 0;
        for (int i = 0; i < steps.Count && i + 1 < _states.Count; i++)
        {
            var step = steps[i]; var state = _states[i + 1];
            int frames = Math.Max(1, step.IsText ? 60 + step.Text.Length * 2 : state.Frames);
            string track, kind;
            if (step.IsText) { track = "Dialogue"; kind = "dialogue"; }
            else switch (step.Op)
            {
                case 0x2D or 0x2E or 0x2F or 0x30 or 0x31 or 0x7A or 0x98 or 0x99 or 0x9D or 0xA3 or 0x89 or 0x33 when step.Bytes.Length >= 2:
                    int who = step.Op == 0x33 && step.Bytes.Length >= 3 ? step.Bytes[2] : step.Bytes[1];
                    track = L2EventCommands.Cap(Name(who)); kind = step.Op is 0xA3 or 0x33 ? "walk" : "character"; break;
                case 0x3A or 0x3B or 0x41 or 0x75 or 0xC4: track = "Camera"; kind = "camera"; break;
                case 0x58 or 0x59 or 0x3E or 0x3F or 0x5A or 0x8A or 0x56 or 0xA4 or 0x76: track = "Screen"; kind = "screen"; break;
                case 0x6F or 0x4B or 0x72 or 0x4C: track = "Music and sound"; kind = "sound"; break;
                case 0x1A or 0x1B or 0x1D or 0x1E or 0x1F or L2ExtraFlags.OpOn or L2ExtraFlags.OpOff: track = "Story flags"; kind = "flag"; break;
                case 0x37 or 0x38: track = "Timing"; kind = "wait"; break;
                case 0x14 or 0x15 or 0x6A or 0x1C or 0x12 or 0x10 or L2ExtraFlags.OpIfOn or L2ExtraFlags.OpIfOff: track = "Checks and jumps"; kind = "flow"; break;
                default: track = "Other"; kind = "other"; break;
            }
            string label = step.IsText ? (state.Speaker?.Length > 0 ? state.Speaker + ": " : "") + step.Text.Replace("\n", " ") : state.Caption.Length > 0 ? state.Caption : Sentence(step);
            bool reached = _run.Count == 0 || _firstVisit.ContainsKey(i);
            items.Add(new L2TimelineItem(i, track, kind, label, startFrame, frames, reached));
            if (step.Op != 0x89 || step.IsText) startFrame += frames;
        }
        TimelineItems = items;
    }

    // the stage after the picked step (before the first one if nothing is picked), and the stage before it
    private (L2StageState? After, L2StageState? Before) Picked()
    {
        int index = SelectedLine;
        if (_script == null || index < 0 || index >= _script.Steps.Count) return (_start, null);
        return (After(index), Before(index));
    }

    public void ShowStage()
    {
        var (after, before) = Picked();
        ShowState(after, before);
        ShowWarning(after);
    }

    private void ShowWarning(L2StageState? state)
    {
        HasWarning = state?.Warning != null;
        WarningText = state?.Warning ?? "";
        if (state?.Warning != null) { WarnLeft = state.WarnX * 16; WarnTop = state.WarnY * 16; }
    }

    private int[]? _mapPixels;
    private int _mapW, _mapH;
    private BitmapSource? _baseImage;
    private string _copiesShown = "";

    // the stage's map with the tile copies done by the shown step (43, 87, B4), each from the map as it is then
    private void ShowTileCopies(L2StageState? state)
    {
        if (_mapPixels == null) return;
        var copies = state?.TileCopies ?? new();
        string key = string.Join(";", copies);
        if (key == _copiesShown) return;
        _copiesShown = key;
        if (copies.Count == 0) { MapImage = _baseImage; return; }
        var pixels = (int[])_mapPixels.Clone();
        foreach (var (dx, dy, sx, sy, w, h) in copies)
        {
            var part = new int[w * 16 * h * 16];
            for (int y = 0; y < h * 16; y++)
                for (int x = 0; x < w * 16; x++)
                {
                    int fromX = sx * 16 + x, fromY = sy * 16 + y;
                    part[y * w * 16 + x] = fromX < _mapW && fromY < _mapH ? pixels[fromY * _mapW + fromX] : unchecked((int)0xFF000000);
                }
            for (int y = 0; y < h * 16; y++)
                for (int x = 0; x < w * 16; x++)
                {
                    int toX = dx * 16 + x, toY = dy * 16 + y;
                    if (toX < _mapW && toY < _mapH) pixels[toY * _mapW + toX] = part[y * w * 16 + x];
                }
        }
        var bitmap = BitmapSource.Create(_mapW, _mapH, 96, 96, PixelFormats.Bgra32, null, pixels, _mapW * 4);
        bitmap.Freeze();
        MapImage = bitmap;
    }

    // draws a stage. progress below 1 draws the characters that moved since before part of the way there,
    // in a walking frame (phase)
    private void ShowState(L2StageState? state, L2StageState? before, double progress = 1, int phase = 0)
    {
        ShowTileCopies(state);
        Actors.Clear();
        if (state == null || _rom == null) { HasSpeech = false; Caption = ""; FlagsNow = ""; return; }
        foreach (var actor in state.Actors.Values.Where(a => a.Placed).OrderBy(a => a.Y))
        {
            if (!actor.Visible && actor.Id != 0 && !_cast.Contains(actor.Id)) continue;
            int sprite = actor.Sprite >= 0 ? actor.Sprite : actor.Id < 7 ? actor.Id : 0x0C;
            var prev = before != null && before.Actors.TryGetValue(actor.Id, out var found) ? found : null;
            double x = actor.X, y = actor.Y;
            int dir = actor.Dir;
            bool walking;
            if (progress < 1 && state.MovedId == actor.Id && state.MovedRoute is { Count: > 1 } route && prev != null && prev.Visible && actor.Visible)
            {
                // a path walk: along its tiles, facing the way of each step
                walking = true;
                double along = progress * (route.Count - 1);
                int leg = Math.Min(route.Count - 2, (int)along);
                var (ax, ay) = route[leg]; var (bx, by) = route[leg + 1];
                x = ax + (bx - ax) * (along - leg); y = ay + (by - ay) * (along - leg);
                dir = bx < ax ? 2 : bx > ax ? 3 : by < ay ? 0 : 1;
            }
            else
            {
                walking = progress < 1 && prev != null && prev.Placed && prev.Visible && actor.Visible && (prev.X != actor.X || prev.Y != actor.Y) && Math.Abs(prev.X - actor.X) + Math.Abs(prev.Y - actor.Y) <= 6;
                if (walking) { x = prev!.X + (actor.X - prev.X) * progress; y = prev.Y + (actor.Y - prev.Y) * progress; }
            }
            var picture = SpritePicture(sprite, dir, walking ? phase : 0);
            double w = picture?.PixelWidth ?? 16, h = picture?.PixelHeight ?? 32;
            bool moved = prev != null && (prev.X != actor.X || prev.Y != actor.Y || prev.Visible != actor.Visible || prev.Dir != actor.Dir ||
                                       prev.ShiftX != actor.ShiftX || prev.ShiftY != actor.ShiftY || prev.NudgeX != actor.NudgeX || prev.NudgeY != actor.NudgeY);
            string name = L2EventCommands.Cap(Name(actor.Id));
            Actors.Add(new L2StageActor
            {
                Id = actor.Id, X = actor.X, Y = actor.Y, Name = name,
                // off its tile by the nudges (8B / C0) and drawn off that by a shift (6C), like the game draws it
                Left = x * 16 + 8 - w / 2 + actor.NudgeX + actor.ShiftX, Top = y * 16 + 16 - h + actor.NudgeY + actor.ShiftY, Picture = picture,
                Label = actor.Id == 0 || _cast.Contains(actor.Id) || moved || actor.Id == _picked ? name.Replace("Cutscene character", "cutscene").Replace("Character", "char") : "",
                Opacity = actor.Visible ? 1 : 0.3, Moved = moved && progress >= 1, Picked = actor.Id == _picked,
                // sprites with a picture for each facing show it, others get a small arrow
                Facing = L2Cutscene.HasFacings(_rom, sprite) ? "" : new[] { "^", "v", "<", ">" }[actor.Dir & 3],
            });
        }
        CameraLeft = state.CameraX * 16 + 8 - 128; CameraTop = state.CameraY * 16 + 8 - 112;
        HasSpeech = state.Text != null;
        Speaker = state.Speaker ?? "";
        Speech = state.Text ?? "";
        Darkness = state.Dark ? 0.6 : 0;
        Caption = state.Caption;
        FlagsNow = (state.Flags.Count > 0 ? "Story flags on: " + string.Join(" ", state.Flags.OrderBy(f => f).Select(f => f.ToString("X2"))) : "No story flag on")
                   + (state.Counters.Count > 0 ? "; " + string.Join(", ", state.Counters.OrderBy(c => c.Key).Select(c => $"counter {c.Key:X2} = {c.Value}")) : "")
                   + " (at this step, in playback)";
        ScrollRequested?.Invoke(state.CameraX * 16 + 8, state.CameraY * 16 + 8);
    }

    private static BitmapSource? Picture((int Width, int Height, uint[] Pixels)? sprite)
    {
        if (sprite is not var (w, h, pixels)) return null;
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public BitmapSource? SpritePicture(int sprite)
    {
        if (_rom == null || sprite < 0) return null;
        if (_spritePics.TryGetValue(sprite, out var cached)) return cached;
        BitmapSource? bitmap = null;
        try { bitmap = Picture(L2Graphics.MapSprite(_rom, sprite)); } catch (Exception) { }
        return _spritePics[sprite] = bitmap;
    }

    // a sprite facing a way (0 up, 1 down, 2 left, 3 right), in a walking frame (phase 1) or standing
    public BitmapSource? SpritePicture(int sprite, int dir, int phase)
    {
        if (_rom == null || sprite < 0) return null;
        var (frame, flip) = L2Cutscene.FacingFrame(_rom, sprite, dir, phase);
        if (frame == 0 && !flip) return SpritePicture(sprite);
        if (_facingPics.TryGetValue((sprite, frame, flip ? 1 : 0), out var cached)) return cached;
        BitmapSource? bitmap = null;
        try
        {
            if (L2Graphics.MapSprite(_rom, sprite, frame) is var (w, h, pixels))
            {
                if (flip) for (int y = 0; y < h; y++) Array.Reverse(pixels, y * w, w);
                bitmap = Picture((w, h, pixels));
            }
        }
        catch (Exception) { }
        return _facingPics[(sprite, frame, flip ? 1 : 0)] = bitmap;
    }

    // the story flags and counters the script checks, ticked as they're set for playback
    private void FillStartFlags()
    {
        StartFlags.Clear();
        if (_script == null) { HasStartFlags = false; HasQuestion = false; return; }
        var (flags, counters) = L2Cutscene.Checked(_script.Steps);
        int once = L2Cutscene.OnceFlag(_script);
        foreach (int flag in flags)
            StartFlags.Add(new L2StartFlagVm { Number = flag, IsOn = _flagsOn.Contains(flag), Label = $"Story flag {flag:X2} is on" + (flag == once ? " (the event has played)" : L2StoryFlags.Label(flag) is { } flagLabel ? $": {flagLabel}" : ""), Changed = StartChanged });
        foreach (int counter in counters)
            StartFlags.Add(new L2StartFlagVm { Number = counter, IsCounter = true, Value = _countersAtStart.TryGetValue(counter, out int value) ? value : 0, Label = $"Counter {counter:X2}{(L2StoryFlags.CounterLabel(counter) is { } counterLabel ? $" ({counterLabel})" : "")} is", Changed = StartChanged });
        HasStartFlags = StartFlags.Count > 0;
        HasQuestion = _script.Steps.Any(s => s.IsText && s.Op == 0x10);
    }

    private void StartChanged()
    {
        if (_refreshing) return;
        foreach (var startFlag in StartFlags)
        {
            if (startFlag.IsCounter) { if (startFlag.Value != 0) _countersAtStart[startFlag.Number] = Math.Clamp(startFlag.Value, 0, 255); else _countersAtStart.Remove(startFlag.Number); }
            else if (startFlag.IsOn) _flagsOn.Add(startFlag.Number); else _flagsOn.Remove(startFlag.Number);
        }
        Stop();
        Simulate(); Annotate(); ShowStage();
        FillTestParty();
    }

    // the stage: clicks

    // the characters on the stage before the step a click would add (after the picked step)
    private L2StageState? StateForInsert()
    {
        int index = SelectedLine;
        if (_script == null || index < 0 || index >= _script.Steps.Count) return _start;
        return After(index) ?? _start;
    }

    // the character picked on the stage (-1 = none)
    public int PickedActor => _picked;

    // a click on a tile of the stage (map pixels). returns true if it was taken (a character picked or let
    // go, the picked step moved), false and the view shows the menu of what can happen there
    public bool StageClick(double pixelX, double pixelY)
    {
        int x = (int)(pixelX / 16), y = (int)(pixelY / 16);
        if (_script == null || !CanEdit || IsPlaying) { StageInfo = $"Tile ({x}, {y})."; return true; }
        var state = StateForInsert();
        // a character under the cursor, pick it
        var hit = state?.Actors.Values.Where(a => a.Placed && (a.Visible || _cast.Contains(a.Id)) && a.X == x && (a.Y == y || a.Y == y + 1))
                     .OrderBy(a => a.Y == y ? 0 : 1).ThenBy(a => a.Id).FirstOrDefault();
        if (hit != null && hit.Id != _picked)
        {
            _picked = hit.Id;
            ShowStage();
            StageInfo = $"{L2EventCommands.Cap(Name(hit.Id))} picked. Click a tile: walk there, appear there, face that way or point the camera there. Click it again (or Esc) to let go.";
            return true;
        }
        if (hit != null && hit.Id == _picked) { _picked = -1; ShowStage(); StageInfo = ""; return true; }
        // the picked step has a tile, move it here (unless a character is picked)
        if (_picked < 0 && SelectedLine >= 0 && SelectedLine < Lines.Count && Lines[SelectedLine] is { Step: { IsText: false } step } line)
        {
            var fieldX = line.Fields.FirstOrDefault(f => f.Label == "X"); var fieldY = line.Fields.FirstOrDefault(f => f.Label == "Y");
            if (fieldX != null && fieldY != null)
            {
                var bytes = (byte[])step.Bytes.Clone();
                fieldX.Def.Set(bytes, x); fieldY.Def.Set(bytes, y);
                if (!bytes.AsSpan().SequenceEqual(step.Bytes))
                {
                    _textLine = null;
                    Remember();
                    step.Bytes = bytes;
                    Changed(false, line);
                    StageInfo = $"Step {SelectedLine + 1} now points at ({x}, {y}).";
                }
                return true;
            }
        }
        return false;
    }

    public void LetGo() { if (_picked >= 0) { _picked = -1; ShowStage(); StageInfo = ""; } }

    // what can happen at a tile: the picked character (or any character) walks / appears / turns there, the
    // camera jumps there, the party starts there. group "" = a top-level entry, else the character's sub-menu
    public List<L2StageAction> ActionsAt(int x, int y)
    {
        var list = new List<L2StageAction>();
        var state = StateForInsert();
        if (state == null || _script == null || !CanEdit) return list;
        IEnumerable<L2StageState.Actor> who = _picked >= 0 && state.Actors.TryGetValue(_picked, out var pickedActor) ? new[] { pickedActor }
            : state.Actors.Values.Where(a => a.Id == 0 || a.Id >= 0x10 && (a.Placed || _cast.Contains(a.Id))).OrderBy(a => a.Id == 0 ? -1 : a.Id);
        foreach (var candidate in who)
        {
            var actor = candidate;
            string name = L2EventCommands.Cap(Name(actor.Id));
            string groupName = _picked >= 0 ? "" : name + (actor.Id >= 0x50 ? "" : actor.Id == 0 ? " (the leader)" : "");
            string prefix = _picked >= 0 ? name + " " : "";
            list.Add(new L2StageAction(groupName, $"🚶 {prefix}walk{(_picked >= 0 ? "s" : "")} here", () => AddWalk(actor.Id, x, y)));
            list.Add(new L2StageAction(groupName, $"🛤 {prefix}walk{(_picked >= 0 ? "s" : "")} a path, first to here (then click more places)", () => StartPath(actor.Id, x, y)));
            list.Add(new L2StageAction(groupName, $"✨ {prefix}appear{(_picked >= 0 ? "s" : "")} here", () => AddPlace(actor.Id, x, y)));
            list.Add(new L2StageAction(groupName, $"👀 {prefix}turn{(_picked >= 0 ? "s" : "")} to face this tile", () => AddFace(actor.Id, x, y)));
            if (actor.Visible) list.Add(new L2StageAction(groupName, $"💨 {prefix}leave{(_picked >= 0 ? "s" : "")}", () => Insert(-1, new L2Step { Bytes = new byte[] { 0x2E, (byte)actor.Id } })));
        }
        list.Add(new L2StageAction("", "🎥 Camera jumps here", () => Insert(-1, new L2Step { Bytes = new byte[] { 0x3A, (byte)x, (byte)y } })));
        list.Add(new L2StageAction("", "🎥 Camera scrolls here", () => Insert(-1, new L2Step { Bytes = new byte[] { 0xC4, (byte)x, (byte)y } }, new L2Step { Bytes = new byte[] { 0x41, 0x00, 0xFF } })));
        list.Add(new L2StageAction("", "📍 The party starts the event here", () => SetPartyStart(x, y)));
        return list;
    }

    private static readonly int[] FaceFrame = L2Beat.FaceFrame;

    // walks a character to a tile: sideways first, then up or down, one step and one wait per tile
    public void AddWalk(int id, int x, int y)
    {
        var state = StateForInsert();
        if (state == null || !state.Actors.TryGetValue(id, out var actor) || !actor.Placed) { StageInfo = $"{L2EventCommands.Cap(Name(id))} isn't on the map yet: make it appear first."; return; }
        if (actor.X == x && actor.Y == y) { StageInfo = $"{L2EventCommands.Cap(Name(id))} is already there."; return; }
        var steps = new List<L2Step>();
        void Leg(int dir, int tiles) { for (int i = 0; i < tiles; i++) { steps.Add(new L2Step { Bytes = new byte[] { 0xA3, (byte)id, (byte)dir } }); steps.Add(new L2Step { Bytes = new byte[] { 0x89, (byte)id } }); } }
        if (x != actor.X) Leg(x > actor.X ? 3 : 2, Math.Abs(x - actor.X));
        if (y != actor.Y) Leg(y > actor.Y ? 1 : 0, Math.Abs(y - actor.Y));
        Insert(-1, steps.ToArray());
        StageInfo = $"{L2EventCommands.Cap(Name(id))} walks to ({x}, {y}) (sideways first, then up or down), one step and one wait a tile. Walls and other characters stop a walk in the game: a red box shows where.";
    }

    public void AddPlace(int id, int x, int y)
    {
        var state = StateForInsert();
        bool hidden = state == null || !state.Actors.TryGetValue(id, out var actor) || !actor.Visible;
        var steps = new List<L2Step>();
        if (hidden) steps.Add(new L2Step { Bytes = new byte[] { 0x2D, (byte)id } });
        steps.Add(new L2Step { Bytes = new byte[] { 0x2F, (byte)id, (byte)x, (byte)y } });
        Insert(-1, steps.ToArray());
        StageInfo = $"{L2EventCommands.Cap(Name(id))} appears on ({x}, {y}).";
    }

    public void AddFace(int id, int x, int y)
    {
        var state = StateForInsert();
        if (state == null || !state.Actors.TryGetValue(id, out var actor) || !actor.Placed) { StageInfo = $"{L2EventCommands.Cap(Name(id))} isn't on the map yet."; return; }
        int dx = x - actor.X, dy = y - actor.Y;
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

    // a character only cutscenes use: made with the map (hidden), shown by "appears". returns its id or -1
    public int AddCastMember(int sprite, int x, int y)
    {
        if (_rom == null || _main == null || _map < 0) return -1;
        // the map's events may move when the character is hidden in its "when shown" event, so unsaved steps get saved first
        if (HasPendingEdits && !SaveNow(quiet: true)) return -1;
        int guard = _script != null && _event?.Entry != null && _event.Entry.Head != L2EventBlocks.SceneList ? L2Cutscene.OnceFlag(_script) : -1;
        try
        {
            int id = L2Cutscene.AddCast(_rom, _map, sprite, guard, x, y);
            _main.NotifyRomModified($"Lufia II: cutscene character {id - 0x4F} on map {_map:X2}");
            int keep = SelectedLine;
            ReadMap();
            var items = ItemsOf(_rom, _map);
            if (_event != null) _event = _event.IsInit ? items[0] : items.FirstOrDefault(item => item.Entry?.Head == _event.Entry?.Head && item.Entry?.Id == _event.Entry?.Id) ?? _event;
            ReadScript(keep);
            MarkRead();
            StageInfo = $"Cutscene character {id - 0x4F} ({L2Sprites.Name(sprite)}) is made with the map, hidden until a step makes it appear" +
                        (guard >= 0 ? $", and no longer once story flag {guard:X2} is on (the event has played)." : ".") + " Click a tile and pick \"appears here\" to show it.";
            Saved?.Invoke("cutscene character");
            return id;
        }
        catch (Exception ex) { StageInfo = "Not added: " + ex.Message; return -1; }
    }

    // playback

    [RelayCommand]
    public void Play()
    {
        if (_script == null || _run.Count == 0 || _start == null) { Status = "Nothing to play."; return; }
        if (IsPlaying) { Stop(); return; }
        IsPlaying = true;
        // from the picked step on (the start if none or the first step is picked)
        _playAt = SelectedLine > 0 && _firstVisit.TryGetValue(SelectedLine, out int runIndex) ? runIndex - 1 : -1;
        _anim = null;
        ShowState(_playAt >= 0 ? _run[_playAt].After : _start, null);
        ShowWarning(null);
        _timer.Interval = TimeSpan.FromMilliseconds(300);
        _timer.Start();
        Status = "Playing (roughly as the game does, following checks and jumps with the story flags ticked above). Click Stop to stop.";
    }

    // a round of walking being drawn: from, to, how many pictures, which one
    private (L2StageState From, L2StageState To, int Count, int At)? _anim;

    private bool IsStepCommand(int runIndex, bool waits)
    {
        if (_script == null || runIndex < 0 || runIndex >= _run.Count) return false;
        var step = _script.Steps[_run[runIndex].Step];
        if (step.IsText) return false;
        return waits ? step.Op == 0x89 : step.Op == 0xA3 && step.Bytes.Length >= 3 && _run[runIndex].After.MovedId >= 0;
    }

    private void PlayTick()
    {
        _timer.Stop();
        if (!IsPlaying || _script == null || _start == null) return;
        if (_anim is { } anim)
        {
            int nextPicture = anim.At + 1;
            if (nextPicture < anim.Count)
            {
                _anim = (anim.From, anim.To, anim.Count, nextPicture);
                ShowState(anim.To, anim.From, (double)nextPicture / anim.Count, nextPicture / 2 % 2 == 0 ? 1 : 0);
                _timer.Interval = TimeSpan.FromMilliseconds(4000.0 / 60);
                _timer.Start();
                return;
            }
            _anim = null;
            ShowState(anim.To, anim.From);
            _timer.Interval = TimeSpan.FromMilliseconds(30);
            _timer.Start();
            return;
        }
        _playAt++;
        if (_playAt >= _run.Count) { Stop(); Status = "The end." + (_run.Count >= 3000 ? " (Playback went round a loop and stopped.)" : ""); return; }
        var before = _playAt > 0 ? _run[_playAt - 1].After : _start;
        // the steps that start walking (A3) and the waits for them (89) are one round, everyone walks at once
        int end = _playAt;
        if (IsStepCommand(_playAt, false))
        {
            while (IsStepCommand(end + 1, false)) end++;
            while (IsStepCommand(end + 1, true)) end++;
        }
        var state = _run[end].After;
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
            _anim = (before, state, Math.Max(2, frames / 4), 0);
            ShowState(state, before, 0, 1);
            _timer.Interval = TimeSpan.FromMilliseconds(4000.0 / 60);
            _timer.Start();
            return;
        }
        ShowState(state, before);
        if (state.BranchWhy != null) Status = "Playback: " + BranchText(state);
        double ms = state.Text != null ? 1600 + state.Text.Length * 40 : Math.Max(30, state.Frames * 1000.0 / 60);
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

    // test in the game

    private string NewGameNote()
    {
        if (_rom == null) return "";
        var (startMap, startScene, startPoint) = L2MapEdit.NewGame(_rom);
        return startMap == 0x02 && startScene == 0x01 && startPoint == 0x06 ? "A new game starts as in the game (the opening)." : $"A new game starts on map {startMap:X2} {_maps.ElementAtOrDefault(startMap)} at point {startPoint} (scene {startScene:X2}).";
    }

    // before a test in the game: texts too wide or long for the box get broken to fit, and the like. heroes who
    // may not be in the story's party are left as they are (their cards warn, the test party has them).
    // returns what was done ("" = nothing)
    public string PrepareForTest()
    {
        string done = "";
        if (_script == null || !CanEdit || _event?.Entry == null) return done;
        if (LeaderVanishToStandIn() > 0) done += StageInfo + " ";
        int fitted = FitTexts(all: true);
        if (fitted > 0) { Changed(true); done += $"{fitted} text{(fitted > 1 ? "s" : "")} broken to fit the box. "; }
        if (FitRings() > 0) { Changed(true); done += "A ring the event waited for forever now turns, closes in and goes as in the game. "; }
        if (FitEffects() > 0) { Changed(true); done += "A colour, shaking or ring left going at the end is now ended before it. "; }
        if (FitMapDraws() > 0) { Changed(true); done += "The map changed while the screen is off is now drawn again after the change. "; }
        // the party: heroes now played by map characters don't join for the test anymore
        if (done.Length > 0 && SaveNow(quiet: true) && !_partyByHand) FillTestParty();
        return done;
    }

    [RelayCommand]
    public void TestInGame()
    {
        if (_rom == null || _main == null || _map < 0) return;
        string fixedFirst = PrepareForTest();
        if (HasPendingEdits && !SaveNow(quiet: true)) return;
        if (TestPoint == null) { Status = "The map has no preset point for the party to start on: add one on the Maps page."; return; }
        // the preset point is added before the start is set, so if that fails the ROM goes back to how it was
        var before = _rom.ToArray();
        try
        {
            int point = TestPoint.Value;
            string added = "";
            if (point == NewPoint)
            {
                // a map the party only reaches through exits, so add a preset point where the party stands
                var (x, y) = LeaderStart();
                point = 0;
                if (!L2MapInfo.AddRecord(_rom, _map, L2MapInfo.Points, 3, new[] { (byte)point, (byte)x, (byte)y }))
                    throw new InvalidOperationException("the map's info block would be too big for a preset point.");
                _pointSpots = L2StoryClear.PresetPoints(_rom, _map);
                ReadPointChoices();
                added = $"Preset point {point} added at ({x}, {y}). ";
            }
            var flags = StartFlags.Where(f => !f.IsCounter && f.IsOn).Select(f => f.Number).ToList();
            var counters = StartFlags.Where(f => f.IsCounter && f.Value != 0).ToDictionary(f => f.Number, f => Math.Clamp(f.Value, 0, 255));
            // the start-up script (no event of the lists): the game just starts on the map, which runs it as it loads
            var party = PartyForTest();
            string set = added + (_event?.Entry is { } entry
                ? L2Cutscene.SetTest(_rom, _map, entry.Head, entry.Id, point, flags, counters, party)
                : L2Cutscene.SetMapTest(_rom, _map, point, flags, counters, party));
            _main.NotifyRomModified($"Lufia II: new game starts with an event on map {_map:X2}");
            // an event that ends by going to another scene carries on there in the game (Elcid's scene 01 into the
            // rest of the ending)
            var next = _script?.Steps.Where(s => s.Op == 0x16 && s.Bytes.Length >= 4).Select(s => (Map: (int)s.Bytes[1], Scene: (int)s.Bytes[2])).Distinct().ToList() ?? new();
            if (_event?.Entry != null && next.Count > 0)
                set += $" At its end the event goes to {string.Join(" or ", next.Select(n => $"{_maps.ElementAtOrDefault(n.Map)} ({n.Map:X2}) scene {n.Scene:X2}"))}, so the game carries on there.";
            Status = fixedFirst + set + " " + _main.PlayInBizHawk();
        }
        catch (Exception ex)
        {
            if (!_rom.SameAs(before)) { _rom.ReplaceAll(before); _pointSpots = L2StoryClear.PresetPoints(_rom, _map); ReadPointChoices(); }
            Status = "Not set: " + ex.Message;
        }
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
