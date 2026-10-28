using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A cutscene (event) of the map in the list.</summary>
public sealed class L2CutsceneItem
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public L2EventBlocks.Entry Entry { get; init; } = null!;
    public override string ToString() => Title;
}

/// <summary>A step of the cutscene's timeline.</summary>
public partial class L2BeatVm : ObservableObject
{
    public L2Beat Beat { get; init; } = null!;
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _sentence = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _icon = "";
    public bool HasDetail => Detail.Length > 0;
    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));
}

/// <summary>A character drawn on the stage.</summary>
public sealed class L2StageActor
{
    public double Left { get; init; }
    public double Top { get; init; }
    public ImageSource? Picture { get; init; }
    public string Label { get; init; } = "";
    public double Opacity { get; init; } = 1;
    public bool Moved { get; init; }
    /// <summary>A small arrow showing where it faces.</summary>
    public string Facing { get; init; } = "";
}

/// <summary>
/// The Lufia II "Cutscenes" page: pick a map, see it with its characters, pick or make a cutscene, build it from
/// steps in plain words (characters appear, walk, face, act, speak; the camera, fades, music, waits, story flags, going
/// to another map), see where everyone stands after each step, play it back, and set a new game to start with it.
/// </summary>
public partial class L2CutscenesPage : L2Page
{
    private int _map = -1;
    private L2EventScript? _script;
    private L2CutsceneItem? _loaded;
    private List<L2StageState> _states = new();
    private readonly List<L2EventScript.Snapshot> _undo = new();
    private readonly Dictionary<int, BitmapSource?> _spritePics = new();
    private string[] _maps = Array.Empty<string>();
    private string[] _songs = Array.Empty<string>();
    private Dictionary<int, string> _names = new();
    private HashSet<int> _cast = new();
    private List<(int Id, int X, int Y)> _points = new();
    private readonly DispatcherTimer _timer = new();
    private int _playAt = -1;

    public ObservableCollection<L2CutsceneItem> Cutscenes { get; } = new();
    [ObservableProperty] private L2CutsceneItem? _selectedCutscene;
    public ObservableCollection<L2BeatVm> Beats { get; } = new();
    [ObservableProperty] private L2BeatVm? _selectedBeat;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _cutsceneInfo = "Pick a map, then a cutscene, or make a new one.";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private bool _canUndo;

    // ------------------------------------------------------------------ the stage
    [ObservableProperty] private BitmapSource? _mapImage;
    public ObservableCollection<L2StageActor> Actors { get; } = new();
    [ObservableProperty] private double _cameraLeft, _cameraTop;
    [ObservableProperty] private bool _showCamera;
    [ObservableProperty] private string _speaker = "";
    [ObservableProperty] private string _speech = "";
    [ObservableProperty] private bool _hasSpeech;
    [ObservableProperty] private double _darkness;
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private double _zoom = 2;
    [ObservableProperty] private int _pickedX = -1, _pickedY = -1;
    [ObservableProperty] private bool _isPlaying;
    /// <summary>Raised when the stage should scroll to show the camera.</summary>
    public event Action<double, double>? ScrollRequested;

    // ------------------------------------------------------------------ the picked step's fields
    [ObservableProperty] private string _beatTitle = "";
    [ObservableProperty] private string _beatHelp = "";
    [ObservableProperty] private bool _hasBeat;
    [ObservableProperty] private bool _showWho, _showWho2, _showDir, _showCount, _showXY, _showText, _showSpeaker, _showAction, _showSong,
        _showNumber, _showFlash, _showAtOnce, _showGoTo, _isOther;
    [ObservableProperty] private string _numberLabel = "";
    public ObservableCollection<L2Option> WhoChoices { get; } = new();
    public ObservableCollection<L2Option> Who2Choices { get; } = new();
    public ObservableCollection<L2Option> SpeakerChoices { get; } = new();
    public List<L2Option> DirChoices { get; } = L2Beat.DirNames.Select((n, i) => new L2Option(i, n)).ToList();
    public List<L2Option> ActionChoices { get; } = new[]
    {
        0x64, 0x87, 0x41, 0x7A, 0x79, 0x7B, 0x7C, 0x81, 0x82, 0x83, 0x84, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x04, 0x05, 0x06, 0x07,
        0x20, 0x21, 0x22, 0x23, 0x30, 0x31, 0x32, 0x33, 0x0C, 0x0D, 0x0E, 0x0F, 0x9C, 0x85, 0x86, 0x9A,
    }.Select(a => new L2Option(a, L2EventCommands.Actions.First(x => x.Item1 == a).Item2)).ToList();
    public ObservableCollection<L2Option> SongChoices { get; } = new();
    public ObservableCollection<L2Option> MapChoices { get; } = new();
    [ObservableProperty] private L2Option? _beatWho, _beatWho2, _beatDir, _beatAction, _beatSong, _beatSpeaker, _beatMap;
    [ObservableProperty] private int _beatCount, _beatX, _beatY, _beatScene, _beatPoint;
    [ObservableProperty] private string _beatNumber = "";
    [ObservableProperty] private string _beatText = "";
    [ObservableProperty] private string _textNote = "";
    [ObservableProperty] private bool _beatFlash, _beatAtOnce;
    private bool _showing;

    // ------------------------------------------------------------------ new cutscene, cast, test
    public List<L2Option> StartChoices { get; } = new()
    {
        new(0, "When the party enters the map (a story flag can decide)"),
        new(1, "When the party steps onto an area"),
        new(2, "When the party talks to a character"),
        new(3, "When another cutscene sends the party here (\"Go to another map\")"),
    };
    [ObservableProperty] private L2Option? _newStart;
    [ObservableProperty] private bool _newOnce = true;
    [ObservableProperty] private string _newFlag = "";
    [ObservableProperty] private bool _newFlagOn = true;
    public ObservableCollection<L2Option> PointChoices { get; } = new();
    [ObservableProperty] private L2Option? _newPoint;
    [ObservableProperty] private int _newX1, _newY1, _newX2, _newY2;
    public ObservableCollection<L2Option> TalkChoices { get; } = new();
    [ObservableProperty] private L2Option? _newCharacter;
    [ObservableProperty] private bool _newIsArea, _newIsTalk, _newIsLoad, _newIsPoint;
    public ObservableCollection<L2SpriteChoice> SpriteChoices { get; } = new();
    [ObservableProperty] private L2SpriteChoice? _castSprite;
    [ObservableProperty] private L2Option? _testPoint;
    [ObservableProperty] private string _testNote = "";

    public L2CutscenesPage()
    {
        _timer.Tick += (_, _) => PlayTick();
        NewStart = StartChoices[0];
    }

    protected override string PageName => "Events";
    protected override bool IsField(string p) => false;
    protected override string? Write() => null;

    protected override IEnumerable<string> ReadNames()
    {
        var rom = Rom!;
        _maps = Enumerable.Range(0, L2Maps.Count).Select(m => { string n = L2Maps.Name(rom, m); return n.Length > 0 && !n.Contains('~') ? n : m switch { 0 => "World map", 1 => "Sea floor", _ => "Map " + m.ToString("X2") }; }).ToArray();
        _songs = Enumerable.Range(0, L2Script.SongCount).Select(i => L2Script.SongName(rom, i)).ToArray();
        SongChoices.Clear();
        for (int i = 0; i < _songs.Length; i++) SongChoices.Add(new L2Option(i, $"{i:X2} {(_songs[i].Length > 0 ? _songs[i] : "(no name)")}"));
        MapChoices.Clear();
        for (int m = 0; m < _maps.Length; m++) MapChoices.Add(new L2Option(m, $"{m:X2} {_maps[m]}"));
        _spritePics.Clear();
        SpriteChoices.Clear();
        for (int s = 0; s < L2Sprites.Count(rom); s++) SpriteChoices.Add(new L2SpriteChoice(s, $"Sprite {L2Sprites.Name(s)}", SpritePicture(s)));
        CastSprite = SpriteChoices.FirstOrDefault(c => c.Sprite == 0x0C) ?? SpriteChoices.FirstOrDefault();
        return Enumerable.Range(0, L2Maps.Count).Select(m => $"{m:X2}  {_maps[m]}");
    }

    // ------------------------------------------------------------------ the map

    protected override void Load(int index)
    {
        if (!ConfirmLeave()) return;
        Stop();
        _map = index;
        var rom = Rom!;
        try
        {
            var (w, h, px) = L2Maps.Render(rom, index);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)(c == 0 ? 0xFF000000 : c)).ToArray(), w * 4);
            bmp.Freeze();
            MapImage = bmp;
        }
        catch (Exception ex) { MapImage = null; Status = "The map can't be drawn: " + ex.Message; }
        ReadMap();
        FillCutscenes(null);
        TestNote = NewGameNote();
    }

    private void ReadMap()
    {
        var rom = Rom!;
        _cast = L2Cutscene.CastIds(rom, _map);
        _names = new Dictionary<int, string>();
        for (int h = 0; h < 7; h++) _names[h] = L2EventCommands.HeroNames[h] + (h == 0 ? " (the leader)" : " (when in the party)");
        foreach (var (id, spr) in L2Cutscene.Creates(rom, _map))
            _names[id] = (_cast.Contains(id) ? "cutscene character " : "character ") + $"{id - 0x4F} (sprite {L2Sprites.Name(spr)})";
        _points = L2StoryClear.PresetPoints(rom, _map);
        PointChoices.Clear();
        foreach (var p in _points) PointChoices.Add(new L2Option(p.Id, $"point {p.Id} ({p.X}, {p.Y})"));
        NewPoint = PointChoices.FirstOrDefault();
        TestPoint = PointChoices.FirstOrDefault();
        WhoChoices.Clear(); Who2Choices.Clear(); SpeakerChoices.Clear(); TalkChoices.Clear();
        Who2Choices.Add(new L2Option(-1, "(nobody)"));
        foreach (var (id, n) in _names.OrderBy(k => k.Key == 0 ? -1 : k.Key >= 0x50 ? (_cast.Contains(k.Key) ? 0 : 1) * 1000 + k.Key : 5000 + k.Key))
        {
            WhoChoices.Add(new L2Option(id, n));
            Who2Choices.Add(new L2Option(id, n));
            SpeakerChoices.Add(new L2Option(id, Short(n)));
            if (id >= 0x50 && !_cast.Contains(id)) TalkChoices.Add(new L2Option(id, n));
        }
        SpeakerChoices.Add(new L2Option(-1, "Narration (no speaker)"));
        SpeakerChoices.Add(new L2Option(-2, "The person talked to"));
        NewCharacter = TalkChoices.FirstOrDefault();
    }

    private static string Short(string n) => n.Replace(" (the leader)", "").Replace(" (when in the party)", "");

    private string Name(int id) => _names.TryGetValue(id, out var n) ? Short(n) : L2EventCommands.DefaultWho(id);

    private void FillCutscenes(L2EventBlocks.Entry? select)
    {
        Cutscenes.Clear();
        var rom = Rom!;
        int block = L2Events.Block(rom, _map);
        if (block < 0) { CutsceneInfo = "This map has no events."; ClearScript(); return; }
        var triggers = L2MapInfo.Records(rom, _map, L2MapInfo.Triggers, 5).ToDictionary(r => (int)r.Bytes[0], r => r.Bytes);
        foreach (var e in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript && e.Head != L2EventBlocks.GameList).OrderBy(e => e.Head == L2EventBlocks.SceneList ? 0 : e.Head).ThenBy(e => e.Id))
        {
            string title = e.Head switch
            {
                L2EventBlocks.SceneList => L2Cutscene.Starter(rom, _map, e.Id) is { } st
                    ? $"Scene {e.Id:X2}: when the party enters the map" + (st.Flag >= 0 ? $" (story flag {st.Flag:X2} {(st.MustBeOn ? "on" : "off")})" : "")
                    : $"Scene {e.Id:X2}: on arrival from \"Go to another map\"",
                L2EventBlocks.TalkList => $"Talking to {Name(e.Id)}",
                L2EventBlocks.TriggerList => triggers.TryGetValue(e.Id, out var t) ? $"Stepping onto ({t[1]},{t[2]})-({t[3] - 1},{t[4] - 1})" : $"Stepping onto area {e.Id} (not on the map)",
                _ => $"Event {e.Id:X2}",
            };
            string detail = "";
            try
            {
                var s = L2EventScript.Read(rom, block + e.Offset, block);
                var beats = L2Cutscene.Beats(s);
                int moves = beats.Count(b => b.Kind is L2BeatKind.Appear or L2BeatKind.Walk or L2BeatKind.Face or L2BeatKind.Act or L2BeatKind.Leave or L2BeatKind.Camera);
                int says = beats.Count(b => b.Kind == L2BeatKind.Say);
                var first = s.Steps.FirstOrDefault(x => x.IsText)?.Text.Replace("\n", " ") ?? "";
                detail = $"{moves} move{(moves == 1 ? "" : "s")}, {says} line{(says == 1 ? "" : "s")}" + (first.Length > 0 ? ": \"" + (first.Length > 40 ? first[..40] + "..." : first) + "\"" : "");
            }
            catch (Exception) { }
            Cutscenes.Add(new L2CutsceneItem { Title = title, Detail = detail, Entry = e });
        }
        var pick = select == null ? null : Cutscenes.FirstOrDefault(c => c.Entry.Head == select.Head && c.Entry.Id == select.Id);
        _loaded = null;
        SelectedCutscene = pick;
        if (pick == null) { ClearScript(); ShowStage(); }
        CutsceneInfo = $"{Cutscenes.Count} events on this map. Pick one to see it on the stage, or make a new cutscene.";
    }

    // ------------------------------------------------------------------ the cutscene

    private bool _skipConfirm;

    partial void OnSelectedCutsceneChanged(L2CutsceneItem? value)
    {
        if (value == _loaded) return;
        if (!_skipConfirm && !ConfirmLeave())
        {
            var back = _loaded;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _skipConfirm = true; SelectedCutscene = back; _skipConfirm = false; });
            return;
        }
        LoadScript(value);
    }

    /// <summary>Asks before unsaved changes are dropped (false: stay).</summary>
    private bool ConfirmLeave()
    {
        if (!IsDirty || _script == null || _loaded == null || System.Windows.Application.Current?.MainWindow?.IsVisible != true) return true;
        var r = System.Windows.MessageBox.Show($"Save the changes to \"{_loaded.Title}\"?", "Lufia II cutscenes", System.Windows.MessageBoxButton.YesNoCancel);
        if (r == System.Windows.MessageBoxResult.Cancel) return false;
        if (r == System.Windows.MessageBoxResult.Yes) return SaveNow();
        IsDirty = false;
        return true;
    }

    private void ClearScript()
    {
        Stop();
        _script = null; _loaded = null;
        Beats.Clear(); _undo.Clear(); CanUndo = false;
        IsDirty = false; CanEdit = false;
        SelectedBeat = null;
        _states = new();
    }

    private void LoadScript(L2CutsceneItem? item, int keepBeat = -1)
    {
        ClearScript();
        _loaded = item;
        if (item == null || Rom == null) { ShowStage(); return; }
        int block = L2Events.Block(Rom, _map);
        _script = L2EventScript.Read(Rom, block + item.Entry.Offset, block);
        CanEdit = _script.Problem == null;
        int once = L2Cutscene.OnceFlag(_script);
        CutsceneInfo = item.Entry.Head switch
        {
            L2EventBlocks.SceneList when L2Cutscene.Starter(Rom, _map, item.Entry.Id) is { } st =>
                $"Plays when the party enters the map{(st.Flag >= 0 ? $" while story flag {st.Flag:X2} is {(st.MustBeOn ? "on" : "off")}" : "")}, once (story flag {st.Played:X2} remembers it); the party stands on point {st.Point}.",
            L2EventBlocks.SceneList => $"Scene {item.Entry.Id:X2}: plays when a \"Go to another map\" step (in another cutscene or event) sends the party here with this scene number, or when a new game starts with it (Test in the game).",
            L2EventBlocks.TalkList => "Plays when the party talks to this character" + (once >= 0 ? $", once (story flag {once:X2})." : "."),
            _ => "Plays each time the leader steps onto the area" + (once >= 0 ? $", once (story flag {once:X2})." : " (no story flag: every time)."),
        };
        if (_script.Problem != null) CutsceneInfo += " It can't be changed here: " + _script.Problem;
        RefreshBeats();
        SelectedBeat = keepBeat >= 0 && keepBeat < Beats.Count ? Beats[keepBeat] : Beats.FirstOrDefault();
    }

    /// <summary>Where the leader stands as the cutscene starts.</summary>
    private (int X, int Y) LeaderStart()
    {
        var rom = Rom!;
        var e = _loaded?.Entry;
        if (e?.Head == L2EventBlocks.SceneList)
        {
            int p = L2Cutscene.Starter(rom, _map, e.Id)?.Point ?? -1;
            var (m, s, np) = L2MapEdit.NewGame(rom);
            if (p < 0 && m == _map && s == e.Id) p = np;
            var pt = _points.FirstOrDefault(x => x.Id == p);
            if (pt != default) return (pt.X, pt.Y);
        }
        if (e?.Head == L2EventBlocks.TriggerList && L2MapInfo.Records(rom, _map, L2MapInfo.Triggers, 5).FirstOrDefault(r => r.Bytes[0] == e.Id) is { Bytes: var t } && t != null)
            return (t[1], t[2]);
        if (e?.Head == L2EventBlocks.TalkList && L2MapInfo.Records(rom, _map, L2MapInfo.Characters, 8).FirstOrDefault(r => 0x4F + r.Bytes[0] == e.Id) is { Bytes: var c } && c != null)
            return (c[1], c[2] + 1);
        if (TestPoint != null && _points.FirstOrDefault(x => x.Id == TestPoint.Value) is var tp && tp != default) return (tp.X, tp.Y);
        return _points.Count > 0 ? (_points[0].X, _points[0].Y) : (8, 8);
    }

    private void Simulate()
    {
        if (Rom == null) return;
        var (x, y) = LeaderStart();
        var start = L2Cutscene.Initial(Rom, _map, x, y);
        _states = L2Cutscene.Simulate(Rom, _map, start, _script?.Steps ?? new List<L2Step>(), Name);
    }

    private void RefreshBeats()
    {
        var keep = SelectedBeat?.Beat;
        Beats.Clear();
        if (_script == null) return;
        var beats = L2Cutscene.Beats(_script);
        for (int i = 0; i < beats.Count; i++) Beats.Add(new L2BeatVm { Beat = beats[i], Number = $"{i + 1}." });
        foreach (var vm in Beats) Describe(vm);
        Simulate();
        if (keep != null)
        {
            var same = Beats.FirstOrDefault(b => b.Beat.Steps.Count > 0 && keep.Steps.Count > 0 && b.Beat.Steps[0] == keep.Steps[0]);
            _showing = true; SelectedBeat = same; _showing = false;
            ShowBeat();
        }
        ShowStage();
    }

    private void Describe(L2BeatVm vm)
    {
        var b = vm.Beat;
        string who = Name(b.Who);
        string dir = L2Beat.DirNames[b.Dir & 3];
        (vm.Icon, vm.Sentence) = b.Kind switch
        {
            L2BeatKind.Appear => ("+", $"{Cap(who)} appears{(b.X >= 0 ? $" on ({b.X}, {b.Y})" : "")}{(b.Flash ? " in a flash" : "")}"),
            L2BeatKind.Leave => ("-", $"{Cap(who)} leaves{(b.Flash ? " in a flash" : "")}"),
            L2BeatKind.Walk => (">", $"{Cap(who)}{(b.Who2 >= 0 ? " and " + Name(b.Who2) : "")} walk{(b.Who2 >= 0 ? "" : "s")} {b.Count} tile{(b.Count == 1 ? "" : "s")} {dir}"),
            L2BeatKind.Face => ("^", $"{Cap(who)} faces {dir}"),
            L2BeatKind.Act => ("!", $"{Cap(who)}: {L2EventCommands.Actions.FirstOrDefault(a => a.Item1 == b.Value).Item2 ?? $"action {b.Value:X2}"}"),
            L2BeatKind.Say => ("\"", b.Opener[0] switch { 0x9E => "Narration:", 0x08 => "The person talked to says:", 0x10 => "A question:", _ => $"{Cap(Name(L2Cutscene.SpeakerActor(b.Opener)))} says:" }),
            L2BeatKind.Camera => ("[]", $"The camera {(b.AtOnce ? "jumps" : "scrolls")} to ({b.X}, {b.Y})"),
            L2BeatKind.CameraBack => ("[]", "The camera goes back to the leader"),
            L2BeatKind.FadeOut => ("#", "The screen fades to black"),
            L2BeatKind.FadeIn => ("#", "The screen fades in"),
            L2BeatKind.Music => ("~", $"Music: {(b.Value < _songs.Length && _songs[b.Value].Length > 0 ? _songs[b.Value] : $"song {b.Value:X2}")}"),
            L2BeatKind.Sound => ("~", $"Sound {b.Value:X2}"),
            L2BeatKind.Wait => ("..", $"Wait {b.Value} frames ({b.Value / 60.0:0.#} s)"),
            L2BeatKind.Shake => ("#", $"The screen shakes (strength {b.Value})"),
            L2BeatKind.StopShake => ("#", "The screen stops shaking"),
            L2BeatKind.FlagOn => ("*", $"Turn story flag {b.Value:X2} on"),
            L2BeatKind.FlagOff => ("*", $"Turn story flag {b.Value:X2} off"),
            L2BeatKind.GoTo => (">>", $"Go to {(b.Value < _maps.Length ? _maps[b.Value] : $"map {b.Value:X2}")} ({b.Value:X2}), scene {b.Value2:X2}, at point {b.Value3}"),
            L2BeatKind.End => ("x", "End of the cutscene"),
            _ => ("?", string.Join("; ", b.Steps.Select(OtherSentence))),
        };
        vm.Detail = b.Kind == L2BeatKind.Say ? "\"" + b.Text.Replace("\n", " / ") + "\"" : b.Kind == L2BeatKind.Other ? "(change it on the Events page)" : "";
    }

    private string OtherSentence(L2Step s)
    {
        var def = L2EventCommands.Find(s.Op);
        if (def == null) return $"command {s.Op:X2}";
        var jumpTo = s.Jumps.Select(j => j.Step == null ? -1 : Beats.ToList().FindIndex(b => b.Beat.Steps.Contains(j.Step))).ToList();
        return def.Describe(s.Bytes, new L2EventCommands.Words
        {
            Who = Name, Map = m => m < _maps.Length ? _maps[m] : $"map {m:X2}",
            Jump = k => k < jumpTo.Count && jumpTo[k] >= 0 ? $"step {jumpTo[k] + 1}" : "another step",
        });
    }

    private static string Cap(string s) => L2EventCommands.Cap(s);

    // ------------------------------------------------------------------ the stage

    partial void OnSelectedBeatChanged(L2BeatVm? value)
    {
        if (_showing) return;
        ShowBeat();
        if (!IsPlaying) ShowStage();
    }

    /// <summary>The stage after the picked step (before the first one when nothing is picked).</summary>
    private int StateIndex()
    {
        if (_script == null || _states.Count == 0) return 0;
        var b = SelectedBeat?.Beat;
        if (b == null || b.Steps.Count == 0) return 0;
        int i = _script.Steps.IndexOf(b.Steps[^1]);
        return Math.Clamp(i + 1, 0, _states.Count - 1);
    }

    private void ShowStage() => ShowState(_states.Count > 0 ? _states[StateIndex()] : Rom != null && _map >= 0 ? StartOnly() : null, StateIndex() > 0 ? _states[StateIndex() - 1] : null);

    private L2StageState StartOnly() { var (x, y) = LeaderStart(); return L2Cutscene.Initial(Rom!, _map, x, y); }

    private void ShowState(L2StageState? s, L2StageState? before)
    {
        Actors.Clear();
        if (s == null || Rom == null) { HasSpeech = false; ShowCamera = false; Caption = ""; return; }
        foreach (var a in s.Actors.Values.Where(a => a.Placed).OrderBy(a => a.Y))
        {
            if (!a.Visible && a.Id != 0 && !_cast.Contains(a.Id)) continue;
            var pic = SpritePicture(a.Sprite >= 0 ? a.Sprite : a.Id < 7 ? a.Id : 0x0C);
            double w = pic?.PixelWidth ?? 16, h = pic?.PixelHeight ?? 32;
            bool moved = before != null && before.Actors.TryGetValue(a.Id, out var o) && (o.X != a.X || o.Y != a.Y || o.Visible != a.Visible || o.Dir != a.Dir);
            Actors.Add(new L2StageActor
            {
                Left = a.X * 16 + 8 - w / 2, Top = a.Y * 16 + 16 - h, Picture = pic, Label = a.Id == 0 || _cast.Contains(a.Id) || moved ? Short(Name(a.Id)).Replace("cutscene character", "cutscene") : "",
                Opacity = a.Visible ? 1 : 0.3, Moved = moved, Facing = new[] { "^", "v", "<", ">" }[a.Dir & 3],
            });
        }
        CameraLeft = s.CameraX * 16 + 8 - 128; CameraTop = s.CameraY * 16 + 8 - 112;
        ShowCamera = true;
        HasSpeech = s.Text != null;
        Speaker = s.Speaker ?? "";
        Speech = s.Text ?? "";
        Darkness = s.Dark ? 0.6 : 0;
        Caption = s.Caption.Length > 0 ? s.Caption : "";
        ScrollRequested?.Invoke(s.CameraX * 16 + 8, s.CameraY * 16 + 8);
    }

    internal BitmapSource? SpritePicture(int sprite)
    {
        if (Rom == null || sprite < 0) return null;
        if (_spritePics.TryGetValue(sprite, out var p)) return p;
        BitmapSource? bmp = null;
        try { bmp = Picture(L2Graphics.MapSprite(Rom, sprite)); } catch (Exception) { }
        return _spritePics[sprite] = bmp;
    }

    /// <summary>A click on the stage (in map pixels): sets the picked step's place, or the tile for a new area.</summary>
    public void StageClick(double px, double py)
    {
        int x = (int)(px / 16), y = (int)(py / 16);
        PickedX = x; PickedY = y;
        NewX1 = NewX2 = x; NewY1 = NewY2 = y;
        var b = SelectedBeat?.Beat;
        if (b == null || !CanEdit || IsPlaying) { Status = $"Tile ({x}, {y})."; return; }
        var before = b.Steps.Count > 0 ? _states.ElementAtOrDefault(_script!.Steps.IndexOf(b.Steps[0])) : _states.FirstOrDefault();
        switch (b.Kind)
        {
            case L2BeatKind.Appear or L2BeatKind.Camera:
                BeatX = x; BeatY = y; break;
            case L2BeatKind.Walk when before != null && before.Actors.TryGetValue(b.Who, out var a) && a.Placed:
            {
                int dx = x - a.X, dy = y - a.Y;
                if (dx == 0 && dy == 0) { Status = "That is where it stands already."; return; }
                if (dx != 0 && dy != 0) { Status = "Walks go in a straight line: click a tile in the same row or column (add another walk for the turn)."; return; }
                // both at once (one change, one Undo step)
                Edit(w => { w.Dir = dx < 0 ? 2 : dx > 0 ? 3 : dy < 0 ? 0 : 1; w.Count = Math.Abs(dx + dy); });
                ShowBeat();
                break;
            }
            case L2BeatKind.Face when before != null && before.Actors.TryGetValue(b.Who, out var f) && f.Placed:
            {
                int dx = x - f.X, dy = y - f.Y;
                BeatDir = DirChoices[Math.Abs(dx) > Math.Abs(dy) ? (dx < 0 ? 2 : 3) : (dy < 0 ? 0 : 1)];
                break;
            }
            default: Status = $"Tile ({x}, {y})."; break;
        }
    }

    partial void OnZoomChanged(double value) { if (Zoom < 0.5) Zoom = 0.5; if (Zoom > 4) Zoom = 4; }

    // ------------------------------------------------------------------ the picked step

    private void ShowBeat()
    {
        _showing = true;
        try
        {
            var b = SelectedBeat?.Beat;
            HasBeat = b != null;
            ShowWho = ShowWho2 = ShowDir = ShowCount = ShowXY = ShowText = ShowSpeaker = ShowAction = ShowSong = ShowNumber = ShowFlash = ShowAtOnce = ShowGoTo = IsOther = false;
            if (b == null) { BeatTitle = ""; BeatHelp = "Pick a step of the timeline, or add one."; return; }
            BeatWho = WhoChoices.FirstOrDefault(o => o.Value == b.Who) ?? (b.Who >= 0 ? AddWho(b.Who) : null);
            BeatWho2 = Who2Choices.FirstOrDefault(o => o.Value == b.Who2) ?? Who2Choices.FirstOrDefault();
            BeatDir = DirChoices[b.Dir & 3];
            BeatCount = b.Count; BeatX = b.X; BeatY = b.Y; BeatFlash = b.Flash; BeatAtOnce = b.AtOnce;
            BeatText = b.Text;
            BeatAction = ActionChoices.FirstOrDefault(o => o.Value == b.Value);
            BeatSong = SongChoices.FirstOrDefault(o => o.Value == b.Value);
            BeatMap = MapChoices.FirstOrDefault(o => o.Value == b.Value);
            BeatScene = b.Value2; BeatPoint = b.Value3;
            int sp = b.Kind == L2BeatKind.Say ? b.Opener[0] switch { 0x9E => -1, 0x08 => -2, _ => L2Cutscene.SpeakerActor(b.Opener) } : 0;
            BeatSpeaker = SpeakerChoices.FirstOrDefault(o => o.Value == sp);
            switch (b.Kind)
            {
                case L2BeatKind.Appear: BeatTitle = "A character appears"; ShowWho = ShowXY = ShowFlash = true; BeatHelp = "Shows the character on a tile (click the map to pick it). Characters made for cutscenes stay hidden until they appear."; break;
                case L2BeatKind.Leave: BeatTitle = "A character leaves"; ShowWho = ShowFlash = true; BeatHelp = "Hides the character (\"in a flash\": it vanishes in a flash of light)."; break;
                case L2BeatKind.Walk: BeatTitle = "Walk"; ShowWho = ShowWho2 = ShowDir = ShowCount = true; BeatHelp = "Walks tile by tile in one direction; the cutscene waits until it gets there. Click a tile in the same row or column to walk there. A second character can walk along at the same time. Walls and other characters stop a walk: keep the way clear."; break;
                case L2BeatKind.Face: BeatTitle = "Face"; ShowWho = ShowDir = true; BeatHelp = "Turns the character (click a tile to face toward it)."; break;
                case L2BeatKind.Act: BeatTitle = "Act"; ShowWho = ShowAction = true; BeatHelp = "The character does something; the cutscene waits until it is done."; break;
                case L2BeatKind.Say:
                    BeatTitle = "Speak"; ShowText = true; ShowSpeaker = b.Opener[0] != 0x10;
                    BeatHelp = "What is said. Enter starts a new line; the box shows 3 lines, about 28 letters across. [Name] is the hero's name, [Pause 20] waits 20 frames. The player presses a button to go on.";
                    UpdateTextNote(); break;
                case L2BeatKind.Camera: BeatTitle = "Camera"; ShowXY = ShowAtOnce = true; BeatHelp = "The camera moves to centre on a tile (click the map). It stays there until \"camera back to the leader\"."; break;
                case L2BeatKind.CameraBack: BeatTitle = "Camera back to the leader"; ShowNumber = true; NumberLabel = "Speed (pixels a frame, 0 = at once)"; BeatNumber = b.Value.ToString(); BeatHelp = "The camera scrolls back to the leader; the cutscene waits for it."; break;
                case L2BeatKind.FadeOut: BeatTitle = "Fade to black"; ShowNumber = true; NumberLabel = "Speed (hex: step and frames, 13 is smooth)"; BeatNumber = b.Value.ToString("X2"); BeatHelp = "The screen fades to black; the cutscene waits for it. Fade in again before the end."; break;
                case L2BeatKind.FadeIn: BeatTitle = "Fade in"; ShowNumber = true; NumberLabel = "Speed (hex: step and frames, 13 is smooth)"; BeatNumber = b.Value.ToString("X2"); BeatHelp = "The screen fades in from black; the cutscene waits for it."; break;
                case L2BeatKind.Music: BeatTitle = "Music"; ShowSong = true; BeatHelp = "Plays a song now (the map's own music comes back on the next map). Music page: listen to them."; break;
                case L2BeatKind.Sound: BeatTitle = "Sound"; ShowNumber = true; NumberLabel = "Sound (hex, 00-8E)"; BeatNumber = b.Value.ToString("X2"); BeatHelp = "Plays a sound. Some sounds go on until another sound plays (24 loops forever)."; break;
                case L2BeatKind.Wait: BeatTitle = "Wait"; ShowNumber = true; NumberLabel = "Frames (60 = 1 second, up to 255)"; BeatNumber = b.Value.ToString(); BeatHelp = "Nothing happens for a while."; break;
                case L2BeatKind.Shake: BeatTitle = "Shake the screen"; ShowNumber = true; NumberLabel = "Strength (1-7)"; BeatNumber = b.Value.ToString(); BeatHelp = "The screen shakes until \"stop shaking\"."; break;
                case L2BeatKind.StopShake: BeatTitle = "Stop shaking"; BeatHelp = "The screen stops shaking."; break;
                case L2BeatKind.FlagOn or L2BeatKind.FlagOff:
                    BeatTitle = b.Kind == L2BeatKind.FlagOn ? "Turn a story flag on" : "Turn a story flag off"; ShowNumber = true; NumberLabel = "Story flag (hex, 00-EF)"; BeatNumber = b.Value.ToString("X2");
                    BeatHelp = "Story flags remember what happened: other events check them (the Events page lists who uses each)."; break;
                case L2BeatKind.GoTo: BeatTitle = "Go to another map"; ShowGoTo = true; BeatHelp = "The party goes to a map, arrives on a preset point, and that map's scene with this number plays (it must exist: make it there as \"When another cutscene sends the party here\"). Ends this cutscene."; break;
                case L2BeatKind.End: BeatTitle = "End"; BeatHelp = "The cutscene ends; the camera comes back to the leader and the player can move."; break;
                default: BeatTitle = "Other step"; IsOther = true; BeatHelp = "A step this page doesn't edit (a check, a jump, a command of its own). It stays as it is; change it on the Events page."; break;
            }
        }
        finally { _showing = false; }
    }

    private L2Option AddWho(int id)
    {
        var o = new L2Option(id, Name(id));
        WhoChoices.Add(o);
        return o;
    }

    private void UpdateTextNote()
    {
        string? p = L2EventText.Check(BeatText.Replace("\r", ""));
        int longest = BeatText.Replace("\r", "").Split('\n').Max(l => l.Length);
        TextNote = p ?? $"Longest line: {longest} letters" + (longest > 28 ? " (the screen fits about 28: break the line with Enter)." : ".");
    }

    partial void OnBeatWhoChanged(L2Option? value) => Edit(b => { if (value != null) b.Who = value.Value; });
    partial void OnBeatWho2Changed(L2Option? value) => Edit(b => b.Who2 = value?.Value ?? -1);
    partial void OnBeatDirChanged(L2Option? value) => Edit(b => b.Dir = value?.Value ?? 1);
    partial void OnBeatCountChanged(int value) => Edit(b => b.Count = Math.Clamp(value, 1, 30));
    partial void OnBeatXChanged(int value) => Edit(b => b.X = value);
    partial void OnBeatYChanged(int value) => Edit(b => b.Y = value);
    partial void OnBeatFlashChanged(bool value) => Edit(b => b.Flash = value);
    partial void OnBeatAtOnceChanged(bool value) => Edit(b => b.AtOnce = value);
    partial void OnBeatActionChanged(L2Option? value) => Edit(b => { if (value != null) b.Value = value.Value; });
    partial void OnBeatSongChanged(L2Option? value) => Edit(b => { if (value != null && b.Kind == L2BeatKind.Music) b.Value = value.Value; });
    partial void OnBeatMapChanged(L2Option? value) => Edit(b => { if (value != null && b.Kind == L2BeatKind.GoTo) b.Value = value.Value; });
    partial void OnBeatSceneChanged(int value) => Edit(b => b.Value2 = value & 0xFF);
    partial void OnBeatPointChanged(int value) => Edit(b => b.Value3 = value & 0xFF);
    partial void OnBeatTextChanged(string value) { if (!_showing) UpdateTextNote(); Edit(b => b.Text = value.Replace("\r", "")); }
    partial void OnBeatSpeakerChanged(L2Option? value) => Edit(b =>
    {
        if (value == null || b.Kind != L2BeatKind.Say || b.Opener[0] == 0x10) return;
        b.Opener = value.Value switch { -1 => new byte[] { 0x9E, 0x05 }, -2 => new byte[] { 0x08 }, _ => L2Cutscene.OpenerFor(value.Value) };
    });
    partial void OnBeatNumberChanged(string value) => Edit(b =>
    {
        bool hex = b.Kind is L2BeatKind.FadeIn or L2BeatKind.FadeOut or L2BeatKind.Sound or L2BeatKind.FlagOn or L2BeatKind.FlagOff;
        if (!int.TryParse(value.Trim(), hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out int v)) return;
        b.Value = b.Kind switch
        {
            L2BeatKind.Wait => Math.Clamp(v, 1, 255), L2BeatKind.Shake => Math.Clamp(v, 1, 7), L2BeatKind.CameraBack => Math.Clamp(v, 0, 15),
            L2BeatKind.Sound => Math.Clamp(v, 0, 0x8E), _ => Math.Clamp(v, 0, 255),
        };
    });

    /// <summary>A field of the picked step changed: the step's commands are made again.</summary>
    private void Edit(Action<L2Beat> change)
    {
        if (_showing || _script == null || SelectedBeat?.Beat is not { } b || b.Kind == L2BeatKind.Other || !CanEdit) return;
        Remember();
        change(b);
        L2Cutscene.Replace(_script, b);
        Changed();
    }

    private void Changed()
    {
        IsDirty = true;
        Stop();
        RefreshBeats();
    }

    private void Remember()
    {
        if (_script == null) return;
        _undo.Add(_script.TakeSnapshot());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        CanUndo = true;
    }

    [RelayCommand]
    private void Undo()
    {
        if (_script == null || _undo.Count == 0) return;
        int keep = SelectedBeat != null ? Beats.IndexOf(SelectedBeat) : -1;
        _script.Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        CanUndo = _undo.Count > 0;
        IsDirty = true;
        RefreshBeats();
        if (keep >= 0 && keep < Beats.Count) SelectedBeat = Beats[keep];
    }

    // ------------------------------------------------------------------ adding, moving, deleting steps

    /// <summary>Adds a step of a kind below the picked one (above the End when the End is picked).</summary>
    public void AddBeat(L2BeatKind kind)
    {
        if (_script == null || !CanEdit) { Status = "Pick a cutscene first (or make a new one)."; return; }
        var sel = SelectedBeat?.Beat;
        int at;
        if (sel == null || sel.Steps.Count == 0) at = Math.Max(0, _script.Steps.Count - 1);
        else if (sel.Kind == L2BeatKind.End || sel.Steps[^1].EndsFlow) at = _script.Steps.IndexOf(sel.Steps[0]);
        else at = _script.Steps.IndexOf(sel.Steps[^1]) + 1;
        // who and where from what the stage shows at that point
        var state = _states.ElementAtOrDefault(at) ?? _states.LastOrDefault();
        int who = sel != null && sel.Kind is L2BeatKind.Appear or L2BeatKind.Walk or L2BeatKind.Face or L2BeatKind.Act or L2BeatKind.Leave ? sel.Who
            : _cast.Count > 0 ? _cast.Min() : 0;
        var beat = L2Beat.New(kind, who);
        if (kind == L2BeatKind.Say) beat.Opener = _loaded?.Entry.Head == L2EventBlocks.TalkList && sel?.Kind != L2BeatKind.Say ? new byte[] { 0x08 } : L2Cutscene.OpenerFor(who);
        if (kind is L2BeatKind.Appear or L2BeatKind.Camera)
        {
            if (PickedX >= 0) { beat.X = PickedX; beat.Y = PickedY; }
            else if (state != null) { beat.X = state.CameraX; beat.Y = state.CameraY; }
        }
        if (kind is L2BeatKind.FlagOn or L2BeatKind.FlagOff) beat.Value = _script != null && L2Cutscene.OnceFlag(_script) is var f && f >= 0 ? f : 0xEF;
        if (kind == L2BeatKind.GoTo) { beat.Value = _map; beat.Value3 = _points.FirstOrDefault().Id; }
        Remember();
        L2Cutscene.Insert(_script, at, beat);
        Changed();
        SelectedBeat = Beats.FirstOrDefault(b => b.Beat.Steps.Count > 0 && b.Beat.Steps[0] == beat.Steps[0]);
        Status = kind is L2BeatKind.Appear or L2BeatKind.Camera or L2BeatKind.Walk ? "Added. Click the map to say where." : "Added.";
    }

    [RelayCommand] private void AddAppear() => AddBeat(L2BeatKind.Appear);
    [RelayCommand] private void AddLeave() => AddBeat(L2BeatKind.Leave);
    [RelayCommand] private void AddWalk() => AddBeat(L2BeatKind.Walk);
    [RelayCommand] private void AddFace() => AddBeat(L2BeatKind.Face);
    [RelayCommand] private void AddAct() => AddBeat(L2BeatKind.Act);
    [RelayCommand] private void AddSay() => AddBeat(L2BeatKind.Say);
    [RelayCommand] private void AddCamera() => AddBeat(L2BeatKind.Camera);
    [RelayCommand] private void AddCameraBack() => AddBeat(L2BeatKind.CameraBack);
    [RelayCommand] private void AddFadeOut() => AddBeat(L2BeatKind.FadeOut);
    [RelayCommand] private void AddFadeIn() => AddBeat(L2BeatKind.FadeIn);
    [RelayCommand] private void AddMusic() => AddBeat(L2BeatKind.Music);
    [RelayCommand] private void AddSound() => AddBeat(L2BeatKind.Sound);
    [RelayCommand] private void AddWait() => AddBeat(L2BeatKind.Wait);
    [RelayCommand] private void AddShake() => AddBeat(L2BeatKind.Shake);
    [RelayCommand] private void AddStopShake() => AddBeat(L2BeatKind.StopShake);
    [RelayCommand] private void AddFlagOn() => AddBeat(L2BeatKind.FlagOn);
    [RelayCommand] private void AddFlagOff() => AddBeat(L2BeatKind.FlagOff);
    [RelayCommand] private void AddGoTo() => AddBeat(L2BeatKind.GoTo);

    [RelayCommand]
    private void DeleteBeat()
    {
        if (_script == null || SelectedBeat?.Beat is not { } b || !CanEdit) return;
        if (Beats.Count <= 1) { Status = "A cutscene needs at least one step."; return; }
        int i = Beats.IndexOf(SelectedBeat);
        Remember();
        L2Cutscene.Remove(_script, b);
        SelectedBeat = null;
        Changed();
        SelectedBeat = Beats.ElementAtOrDefault(Math.Min(i, Beats.Count - 1));
    }

    [RelayCommand] private void MoveUp() => Move(-1);
    [RelayCommand] private void MoveDown() => Move(1);

    private void Move(int d)
    {
        if (_script == null || SelectedBeat?.Beat is not { } b || !CanEdit) return;
        int i = Beats.IndexOf(SelectedBeat), j = i + d;
        if (j < 0 || j >= Beats.Count) return;
        var other = Beats[j].Beat;
        Remember();
        // the two runs of steps change places
        var (first, second) = d < 0 ? (other, b) : (b, other);
        int at = _script.Steps.IndexOf(first.Steps[0]);
        foreach (var s in first.Steps.Concat(second.Steps)) _script.Steps.Remove(s);
        _script.Steps.InsertRange(at, second.Steps.Concat(first.Steps));
        Changed();
        SelectedBeat = Beats.FirstOrDefault(x => x.Beat.Steps.Count > 0 && x.Beat.Steps[0] == b.Steps[0]);
    }

    [RelayCommand]
    private void PrevBeat() { int i = SelectedBeat != null ? Beats.IndexOf(SelectedBeat) : 0; if (i > 0) SelectedBeat = Beats[i - 1]; }
    [RelayCommand]
    private void NextBeat() { int i = SelectedBeat != null ? Beats.IndexOf(SelectedBeat) : -1; if (i + 1 < Beats.Count) SelectedBeat = Beats[i + 1]; }

    // ------------------------------------------------------------------ saving

    [RelayCommand] private void Save() => SaveNow();

    private bool SaveNow()
    {
        if (Rom == null || Main == null || _script == null || _loaded == null || !CanEdit) return false;
        if (!IsDirty) { Status = "Nothing changed."; return true; }
        for (int i = 0; i < _script.Steps.Count; i++)
            if (_script.Steps[i].IsText && L2EventText.Check(_script.Steps[i].Text) is { } p) { Status = $"Not saved: a line of text: {p}"; return false; }
        bool added = false;
        if (!_script.Steps[^1].EndsFlow) { _script.Steps.Add(new L2Step { Bytes = new byte[] { 0x00 } }); added = true; }
        try
        {
            string result = L2EventBlocks.Save(Rom, _map, _loaded.Entry, _script);
            Main.NotifyRomModified($"Lufia II: cutscene \"{_loaded.Title}\" on {_maps.ElementAtOrDefault(_map)}");
            int keep = SelectedBeat != null ? Beats.IndexOf(SelectedBeat) : -1;
            IsDirty = false;
            var entry = _loaded.Entry;
            ReadMap();
            FillCutscenes(entry);
            if (keep >= 0 && keep < Beats.Count) SelectedBeat = Beats[keep];
            Status = "Saved. " + result + (added ? " An End step was added at the bottom." : "");
            return true;
        }
        catch (Exception ex)
        {
            if (added) _script.Steps.RemoveAt(_script.Steps.Count - 1);
            Status = "Not saved: " + ex.Message;
            return false;
        }
    }

    [RelayCommand]
    private void Revert()
    {
        IsDirty = false;
        LoadScript(_loaded);
        Status = "Back to what is in the ROM.";
    }

    // ------------------------------------------------------------------ playback

    [RelayCommand]
    private void Play()
    {
        if (_script == null || _states.Count < 2) { Status = "Pick a cutscene with steps first."; return; }
        if (IsPlaying) { Stop(); return; }
        IsPlaying = true;
        _playAt = 0;
        ShowState(_states[0], null);
        _timer.Interval = TimeSpan.FromMilliseconds(300);
        _timer.Start();
        Status = "Playing (roughly as the game does). Click Play again to stop.";
    }

    private void PlayTick()
    {
        _timer.Stop();
        if (!IsPlaying || _script == null) return;
        _playAt++;
        if (_playAt >= _states.Count) { Stop(); Status = "The end."; return; }
        var s = _states[_playAt];
        ShowState(s, _states[_playAt - 1]);
        var step = _script.Steps.ElementAtOrDefault(_playAt - 1);
        if (step != null)
        {
            var bvm = Beats.FirstOrDefault(b => b.Beat.Steps.Contains(step));
            if (bvm != null && bvm != SelectedBeat) { _showing = true; SelectedBeat = bvm; _showing = false; ShowBeat(); }
        }
        // a frame is 1/60 s; text stays a little longer to be read
        double ms = s.Text != null ? 1600 + s.Text.Length * 40 : Math.Max(30, s.Frames * 1000.0 / 60);
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Min(ms, 5000));
        _timer.Start();
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsPlaying) return;
        _timer.Stop();
        IsPlaying = false;
        ShowStage();
    }

    // ------------------------------------------------------------------ new cutscene

    partial void OnNewStartChanged(L2Option? value)
    {
        int v = value?.Value ?? 0;
        NewIsLoad = v == 0; NewIsArea = v == 1; NewIsTalk = v == 2; NewIsPoint = v is 0;
    }

    [RelayCommand]
    private void MakeCutscene()
    {
        if (Rom == null || Main == null || _map < 0) return;
        if (!ConfirmLeave()) return;
        int kind = NewStart?.Value ?? 0;
        int flag = -1;
        if (kind == 0 && NewFlag.Trim().Length > 0 && !int.TryParse(NewFlag.Trim(), System.Globalization.NumberStyles.HexNumber, null, out flag)) { Status = "Write the story flag in hex (00-EF), or leave it empty."; return; }
        try
        {
            var made = L2Cutscene.Create(Rom, _map, new L2Cutscene.NewOptions
            {
                Start = kind switch { 0 => L2Cutscene.StartKind.MapLoads, 1 => L2Cutscene.StartKind.StepOn, 2 => L2Cutscene.StartKind.Talk, _ => L2Cutscene.StartKind.Arrive },
                Once = NewOnce, Flag = flag, FlagMustBeOn = NewFlagOn, Point = NewPoint?.Value ?? -1,
                X1 = NewX1, Y1 = NewY1, X2 = NewX2, Y2 = NewY2, Character = NewCharacter?.Value ?? -1,
            });
            Main.NotifyRomModified("Lufia II: " + made.What);
            ReadMap();
            IsDirty = false;
            FillCutscenes(new L2EventBlocks.Entry(made.Head, made.Id, 0));
            if (made.Head == L2EventBlocks.SceneList && NewPoint != null) TestPoint = PointChoices.FirstOrDefault(p => p.Value == NewPoint.Value) ?? TestPoint;
            Status = made.What + " Add its steps on the right (the first line is a placeholder), then Save.";
        }
        catch (Exception ex) { Status = "Not made: " + ex.Message; }
    }

    /// <summary>A character only cutscenes use: made with the map (hidden), shown by "appears".</summary>
    [RelayCommand]
    private void AddCastMember()
    {
        if (Rom == null || Main == null || _map < 0 || CastSprite == null) return;
        // the map's events may move when the character is hidden in its "when shown" event: unsaved steps are saved first
        if (IsDirty && !SaveNow()) return;
        // a cutscene that plays once: once it has played, the character isn't made any more (the story flag is on)
        int guard = -1;
        if (_script != null && _loaded != null && _loaded.Entry.Head != L2EventBlocks.SceneList) guard = L2Cutscene.OnceFlag(_script);
        try
        {
            int x = PickedX >= 0 ? PickedX : 0, y = PickedY >= 0 ? PickedY : 0;
            int id = L2Cutscene.AddCast(Rom, _map, CastSprite.Sprite, guard, x, y);
            Main.NotifyRomModified($"Lufia II: cutscene character {id - 0x4F} on map {_map:X2}");
            var keep = _loaded?.Entry; int beat = SelectedBeat != null ? Beats.IndexOf(SelectedBeat) : -1;
            ReadMap();
            FillCutscenes(keep);
            if (beat >= 0 && beat < Beats.Count) SelectedBeat = Beats[beat];
            Status = $"Cutscene character {id - 0x4F} ({CastSprite.Label}) is made with the map, hidden until a step makes it appear" +
                     (guard >= 0 ? $", and no longer once story flag {guard:X2} is on (the cutscene has played)." : ".") + " Add \"Appears\" to show it.";
        }
        catch (Exception ex) { Status = "Not added: " + ex.Message; }
    }

    // ------------------------------------------------------------------ test

    private string NewGameNote()
    {
        if (Rom == null) return "";
        var (m, s, p) = L2MapEdit.NewGame(Rom);
        return m == 0x02 && s == 0x01 && p == 0x06 ? "A new game starts as in the game (the opening)." : $"A new game starts on map {m:X2} {_maps.ElementAtOrDefault(m)} at point {p} (scene {s:X2}).";
    }

    [RelayCommand]
    private void TestInGame()
    {
        if (Rom == null || Main == null || _loaded == null) { Status = "Pick a cutscene first."; return; }
        if (IsDirty && !SaveNow()) return;
        if (TestPoint == null) { Status = "The map has no preset point for the party to start on: add one on the Maps page."; return; }
        try
        {
            Status = L2Cutscene.SetTest(Rom, _map, _loaded.Entry.Head, _loaded.Entry.Id, TestPoint.Value) + " Export the ROM (File > Export ROM) and start a new game. Set the normal start again before you share the game.";
            Main.NotifyRomModified($"Lufia II: new game starts with a cutscene on map {_map:X2}");
        }
        catch (Exception ex) { Status = "Not set: " + ex.Message; }
        TestNote = NewGameNote();
    }

    [RelayCommand]
    private void NormalStart()
    {
        if (Rom == null || Main == null) return;
        L2MapEdit.SetNormalStart(Rom);
        Main.NotifyRomModified("Lufia II: normal new-game start");
        TestNote = NewGameNote();
        Status = "A new game starts as in the game again.";
    }
}

/// <summary>A sprite to pick, with its picture.</summary>
public sealed record L2SpriteChoice(int Sprite, string Label, ImageSource? Picture);
