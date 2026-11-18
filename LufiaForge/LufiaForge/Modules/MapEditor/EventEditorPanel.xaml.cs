using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using DragEventArgs = System.Windows.DragEventArgs;
using DataObject = System.Windows.DataObject;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;

namespace LufiaForge.Modules.MapEditor;

// one editable value of a command: a number, a choice (who, direction, item, map...), or a jump target line
public partial class ParamVm : ObservableObject
{
    private readonly EventLineVm _line;
    public string Label { get; init; } = "";
    // the field (null = the answer count of command 03 or a raw byte, see Position)
    public ParamDef? Def { get; init; }
    // byte position for raw fields that have no definition, or -1
    public int Position { get; init; } = -1;
    // index into the command's jump targets, or -1
    public int JumpIndex { get; init; } = -1;
    public bool Decimal { get; init; }
    // the values behind Choices (null for jumps and plain fields)
    public IReadOnlyList<int>? ChoiceValues { get; init; }

    public bool IsFlag => Def?.Kind == ParamKind.Flag;
    // a song or a sound: its card gets a button to hear it (and a song shows its name)
    public bool IsAudio => Def?.Kind is ParamKind.Music or ParamKind.Sound;
    public bool IsSong => Def?.Kind == ParamKind.Music;
    public string SongNameText => IsSong && _line.Owner.Rom is { } rom
        ? Raw == 0xFF ? "(stop)" : GameData.MusicEditorViewModel.SongName(rom, Raw) : "";
    public void Listen(Button b)
    {
        string note = Common.SoundPreview.Toggle(b, _line.Owner.Rom, lufia2: false, song: IsSong, Raw);
        if (note.Length > 0) _line.ErrorText = note;
    }
    public string FlagNameText => IsFlag && EventScript.FlagName?.Invoke(Raw) is { } flagName ? flagName : "";

    public void Follow() { if (IsJump) _line.Owner.FollowJump(_line.Op, JumpIndex); }
    public void ShowFlagUses() { if (IsFlag) _line.Owner.ShowFlag(Raw); }

    public bool IsJump => JumpIndex >= 0;
    public bool IsChoice => IsJump || ChoiceValues != null;
    public bool IsField => !IsChoice;
    public IReadOnlyList<string> Choices { get; set; } = Array.Empty<string>();

    public ParamVm(EventLineVm line) { _line = line; }

    private int Raw
    {
        get
        {
            var opBytes = _line.Op.Bytes;
            if (Def != null) { try { return Def.Get(opBytes); } catch { return 0; } }
            return Position >= 0 && Position < opBytes.Length ? opBytes[Position] : 0;
        }
    }

    private bool Hex => Def != null
        ? Def.Kind is ParamKind.Hex or ParamKind.Flag or ParamKind.Music or ParamKind.Sound or ParamKind.Formation
        : !Decimal;

    private void Store(int value)
    {
        var bytes = (byte[])_line.Op.Bytes.Clone();
        if (Def != null) Def.Set(bytes, value);
        else if (Position >= 0 && Position < bytes.Length) bytes[Position] = (byte)value;
        _line.SetBytes(bytes, resizeChoices: Def == null && Position == 1 && bytes[0] == 0x03);
    }

    public string Value
    {
        get
        {
            if (Def?.Kind == ParamKind.Name)
            {
                var opBytes = _line.Op.Bytes;
                return opBytes.Length >= 7 && opBytes[2] == 0xFF ? "*" : EventCommands.NameText(opBytes);
            }
            return Hex ? Raw.ToString(Raw > 0xFF ? "X4" : "X2") : Raw.ToString();
        }
        set
        {
            if (Def?.Kind == ParamKind.Name)
            {
                string name = (value ?? "").Trim();
                var bytes = (byte[])_line.Op.Bytes.Clone();
                if (bytes.Length < 7) return;
                if (name == "*") { bytes[2] = 0xFF; for (int i = 3; i < 7; i++) bytes[i] = 0; }
                else
                {
                    if (name.Length > 5 || name.Any(c => c < 0x20 || c > 0x7E))
                    { _line.ErrorText = $"{Label}: up to 5 letters (plain letters, digits, punctuation), or * for the normal name."; return; }
                    for (int i = 0; i < 5; i++) bytes[2 + i] = i < name.Length ? (byte)name[i] : (byte)0;
                }
                _line.SetBytes(bytes, resizeChoices: false);
                return;
            }
            int min = Def?.Min ?? 0, max = Def?.Max ?? 255;
            var style = Hex ? NumberStyles.HexNumber : NumberStyles.Integer;
            if (!int.TryParse(value?.Trim(), style, CultureInfo.InvariantCulture, out int number) || number < min || number > max)
            {
                string lo = Hex ? min.ToString("X2") : min.ToString(), hi = Hex ? max.ToString("X2") : max.ToString();
                _line.ErrorText = $"{Label}: enter {(Hex ? "a hex value" : "a number")} from {lo} to {hi}.";
                return;
            }
            Store(number);
        }
    }

    public int ChoiceIndex
    {
        get
        {
            if (ChoiceValues != null) return ChoiceValues.ToList().IndexOf(Raw);
            if (!IsJump) return -1;   // plain value field, its (hidden) dropdown has nothing to select
            var target = JumpIndex < _line.Op.Targets.Count ? _line.Op.Targets[JumpIndex] : null;
            if (target?.Op == null) return Choices.Count - 1;          // the "outside this event" entry
            return _line.Owner.Script.Ops.IndexOf(target.Op);
        }
        set
        {
            if (value < 0) return;
            if (ChoiceValues != null) { if (value < ChoiceValues.Count && ChoiceValues[value] != Raw) Store(ChoiceValues[value]); return; }
            if (!IsJump) return;
            var ops = _line.Owner.Script.Ops;
            if (value < ops.Count && JumpIndex < _line.Op.Targets.Count)
            {
                _line.Op.Targets[JumpIndex].Op = ops[value];
                _line.Owner.Changed();
            }
        }
    }
}

// one line of the event editor: a command or a dialogue box
public partial class EventLineVm : ObservableObject
{
    public EventEditorPanel Owner { get; }
    public ScriptOp Op { get; }
    public int Index { get; set; }

    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private bool _dropAbove;
    [ObservableProperty] private bool _dropBelow;
    public ObservableCollection<ParamVm> Params { get; } = new();

    public EventLineVm(EventEditorPanel owner, ScriptOp op, int index)
    {
        Owner = owner; Op = op; Index = index;
        BuildParams();
    }

    public string LineText => $"{Index + 1}";
    public string OffsetText => Op.IsNew ? "new line" : $"ROM 0x{Op.Offset:X6}";
    public bool IsText => Op.IsText;
    public string Description => Owner.Script.Describe(Op);
    // a "continue with event N of map M" line (51), its target can be opened
    public bool CanOpenChained => !Op.IsText && Op.Bytes.Length == 3 && Op.Bytes[0] == 0x51;
    public string OpenChainedText => CanOpenChained ? $"▶ Open event {EventCommands.ChainedEvent(Op.Bytes)} of map {Op.Bytes[1]:X2}" : "";

    public string CommandName => Op.IsText || Op.Bytes.Length == 0 ? "" : EventCommands.Find(Op.Bytes[0])?.Name ?? "Unknown command";
    public string WaitsText => Op.IsText || Op.Bytes.Length == 0 ? ""
        : EventCommands.Find(Op.Bytes[0])?.Waits is { Length: > 0 } waits ? "⏸ waits " + waits : "";

    // dialogue
    public static readonly IReadOnlyList<string> Speakers = BuildSpeakers();
    private static IReadOnlyList<string> BuildSpeakers()
    {
        var list = new List<string> { "Narration (0C)", "Text box (0D)" };
        for (int actor = 0; actor < 40; actor++)
        {
            string who = EventCommands.Actor(actor);
            list.Add($"{char.ToUpper(who[0]) + who[1..]} says ({(actor < 8 ? 0x88 + actor : 0x90 + actor - 8):X2})");
        }
        return list;
    }
    public IReadOnlyList<string> SpeakerChoices => Speakers;
    public bool SpeakerEditable => Op.IsText && Op.Bytes.Length == 1 && (Op.Bytes[0] is 0x0C or 0x0D || Op.Bytes[0] is >= 0x88 and <= 0xAF);

    public int SpeakerIndex
    {
        get
        {
            if (!SpeakerEditable) return -1;
            byte code = Op.Bytes[0];
            return code switch { 0x0C => 0, 0x0D => 1, >= 0x88 and <= 0x8F => 2 + code - 0x88, _ => 10 + code - 0x90 };
        }
        set
        {
            if (!SpeakerEditable || value < 0) return;
            byte code = value switch { 0 => 0x0C, 1 => 0x0D, < 10 => (byte)(0x88 + value - 2), _ => (byte)(0x90 + value - 10) };
            if (Op.Bytes[0] == code) return;
            Op.Bytes = new[] { code };
            Owner.Changed();
        }
    }

    public string Text
    {
        get => Op.Text;
        set
        {
            if (Op.Text == value) return;
            Op.Text = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeText));
            Owner.Changed(rebuild: false);
        }
    }

    public string SizeText
    {
        get
        {
            if (!Op.IsText) return "";
            if (Core.DutchLetters.Unwritable(Owner.Script.Rom, Op.Text) is string bad) return "Can't be saved: " + bad + ".";
            int newSize = Owner.Script.EncodeText(Op.Text, Op.Terminator).Length;
            if (Op.IsNew) return $"{newSize} bytes (new)";
            int oldSize = Op.RawLength - Op.Bytes.Length;
            return !Op.TextChanged ? $"{oldSize} bytes" : newSize == oldSize ? $"{newSize} bytes (same size)" : $"{newSize} bytes (was {oldSize})";
        }
    }

    // commands
    // show the command byte (hex) on each line (off by default, see the "Show bytes" box)
    public bool ShowBytes => EventEditorPanel.ShowCommandBytes;
    public void RefreshShowBytes() => OnPropertyChanged(nameof(ShowBytes));

    public string OpcodeHex
    {
        get => Op.Bytes.Length > 0 ? Op.Bytes[0].ToString("X2") : "";
        set
        {
            if (!byte.TryParse(value?.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte opcode))
            { ErrorText = "Command: enter a hex byte."; return; }
            if (Op.Bytes.Length > 0 && Op.Bytes[0] == opcode) return;
            int length = EventScript.CommandLength(opcode, opcode == 0x03 ? 1 : 0);
            var bytes = new byte[Math.Max(1, length)];
            bytes[0] = opcode;
            if (opcode == 0x03) bytes[1] = 1;
            Apply(bytes);
        }
    }

    public void SetBytes(byte[] bytes, bool resizeChoices = false)
    {
        if (resizeChoices)
        {
            // answer count changed: resize the target list
            var resized = new byte[2 + 2 * bytes[1]];
            Array.Copy(bytes, resized, Math.Min(bytes.Length, resized.Length));
            bytes = resized;
        }
        Apply(bytes);
    }

    private void Apply(byte[] bytes)
    {
        var error = Owner.Script.SetCommandBytes(Op, bytes);
        ErrorText = error ?? "";
        if (error != null) { OnPropertyChanged(nameof(OpcodeHex)); return; }
        BuildParams();
        Owner.Changed();
    }

    public void BuildParams()
    {
        Params.Clear();
        if (Op.IsText || Op.Bytes.Length == 0) return;
        var opBytes = Op.Bytes;
        if (opBytes[0] == 0x03)
        {
            Params.Add(new ParamVm(this) { Label = "answers", Position = 1, Decimal = true });
            for (int k = 0; k < opBytes[1]; k++) Params.Add(new ParamVm(this) { Label = $"#{k + 1}", JumpIndex = k, Choices = Owner.JumpChoices(Op, k) });
            return;
        }
        var def = EventCommands.Find(opBytes[0]);
        if (def == null || opBytes.Length != EventScript.CommandLength(opBytes[0], opBytes.Length > 1 ? opBytes[1] : 0))
        {
            for (int i = 1; i < opBytes.Length; i++) Params.Add(new ParamVm(this) { Label = $"byte {i}", Position = i });
            return;
        }
        foreach (var paramDef in def.Params)
        {
            if (paramDef.Kind == ParamKind.Jump)
            {
                Params.Add(new ParamVm(this) { Label = paramDef.Label, Def = paramDef, JumpIndex = paramDef.JumpIndex, Choices = Owner.JumpChoices(Op, paramDef.JumpIndex) });
                continue;
            }
            var choices = Owner.ChoicesFor(paramDef.Kind);
            // a field with a smaller range than its list (rename: characters 0-7 only)
            if (choices != null && paramDef.Kind == ParamKind.Character) choices = choices.Where(c => c.Value >= paramDef.Min && c.Value <= paramDef.Max).ToList();
            // short-form flag commands (C0-FF) hold F0-FF as 0-F
            if (paramDef.Kind == ParamKind.Flag && paramDef.Max < 0xFF && choices != null)
                choices = choices.Where(c => c.Value >= 0xF0).Select(c => (c.Value - 0xF0, c.Label)).ToList();
            // a sound number past the end of the list (from 80 up the driver repeats 00-38) stays as it is
            if (paramDef.Kind == ParamKind.Sound && choices != null)
            {
                int sound; try { sound = paramDef.Get(opBytes); } catch { sound = -1; }
                if (sound >= 0 && choices.All(c => c.Value != sound)) choices = choices.Append((sound, Core.Audio.SoundNames.Label(false, sound))).ToList();
            }
            Params.Add(new ParamVm(this)
            {
                Label = paramDef.Label, Def = paramDef,
                ChoiceValues = choices?.Select(c => c.Value).ToList(),
                Choices = choices?.Select(c => c.Label).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            });
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(LineText));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(SpeakerIndex));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(OpcodeHex));
        OnPropertyChanged(nameof(CommandName));
        OnPropertyChanged(nameof(CanOpenChained));
        OnPropertyChanged(nameof(OpenChainedText));
        OnPropertyChanged(nameof(WaitsText));
        foreach (var param in Params.Where(p => p.IsJump)) param.Choices = Owner.JumpChoices(Op, param.JumpIndex);
        var copy = Params.ToList();
        Params.Clear();
        foreach (var param in copy) Params.Add(param);
    }
}

public partial class EventEditorPanel : UserControl, LufiaForge.ViewModels.IPendingEdits
{
    private RomBuffer _rom = null!;
    public RomBuffer? Rom => _rom;
    private int _mapId, _event;
    private IEventHost _owner = null!;
    private readonly ObservableCollection<EventLineVm> _lines = new();
    private bool _refreshing;

    public EventScript Script { get; private set; } = null!;

    // the screen that opened the editor (confirmations, save list)
    public IEventHost Host => _owner;
    private int _selectedLine = -1;

    // fired by the Close button (only shown when the panel sits in its own window)
    public event Action? CloseRequested;
    public bool ShowCloseButton { set => CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public bool IsLoadedScript => Script != null;

    public EventEditorPanel()
    {
        InitializeComponent();
        ShowBytesBox.IsChecked = ShowCommandBytes;
        Lines.ItemsSource = _lines;
        // splitters remember the sizes they're dragged to (double-click one to get the normal sizes back)
        StageSplitter.DragCompleted += (_, e) =>
        {
            if (Common.LayoutMemory.Moved(e)) Common.LayoutMemory.Set(StageSplitKey, Common.LayoutMemory.Format(LinesColumn.Width) + ";" + Common.LayoutMemory.Format(StageColumn.Width));
        };
        StageSplitter.MouseDoubleClick += (_, _) => { Common.LayoutMemory.Set(StageSplitKey, null); if (Stage.Visibility == Visibility.Visible) ShowStage(true); };
        TimelineSplitter.DragCompleted += (_, e) => { if (Common.LayoutMemory.Moved(e)) Common.LayoutMemory.Set("eventeditor.timeline", Common.LayoutMemory.Format(new GridLength(TimelineRow.ActualHeight))); };
        TimelineSplitter.MouseDoubleClick += (_, _) => { Common.LayoutMemory.Set("eventeditor.timeline", null); TimelineRow.Height = new GridLength(240); };
        // while it's on screen its edits get written on their own (saving the project, Apply to ROM in the top bar)
        Loaded += (_, _) => (Application.Current?.MainWindow?.DataContext as LufiaForge.ViewModels.MainViewModel)?.Register(this);
        // shown again: re-read whatever another tab, Undo or a patch changed in the meantime
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Dispatcher.BeginInvoke(RefreshIfStale); };
        // a sound got renamed in Settings: the cards show the new name (also if it was renamed while this was off screen)
        Loaded += (_, _) =>
        {
            Core.Audio.SoundNames.Changed -= SoundNamesChanged; Core.Audio.SoundNames.Changed += SoundNamesChanged;
            if (_soundNames != Core.Audio.SoundNames.Version) SoundNamesChanged();
        };
        Unloaded += (_, _) =>
        {
            Core.Audio.SoundNames.Changed -= SoundNamesChanged;
            Common.SoundPreview.StopWhenGone(this);
            if (HasPendingEdits) WritePendingEdits();
            // a part popped out into its own window keeps working while this tab is away, so its edits stay registered
            if (Common.PopOutHost.AnyPoppedOut(this)) return;
            (Application.Current?.MainWindow?.DataContext as LufiaForge.ViewModels.MainViewModel)?.Unregister(this);
        };
    }

    // the event (or its cutscene's paths) changed and isn't in the ROM yet
    public bool HasPendingEdits => Script != null && _rom != null && (Script.IsModified || Stage.HasPathChanges);

    public string? WritePendingEdits()
    {
        if (!HasPendingEdits) return null;
        return Write(quiet: true) ? null : $"Event {_event} of map {_mapId:X2}: {StatusText.Text}";
    }

    // if the ROM doesn't hold the event the way we read it anymore (another tab, Undo or a patch wrote it), read it
    // again, so the old lines never get written back over the newer ones. if there are edits not written yet it stays
    // as it is and says so (saving refuses then, see EventScript.Save)
    public void RefreshIfStale()
    {
        if (Script == null || _rom == null) return;
        if (Script.MatchesRom())
        {
            if (Stage.Visibility == Visibility.Visible) Stage.Bind(_rom, this);   // (its map may have changed)
            return;
        }
        if (Script.IsModified)
        {
            StatusText.Text = "This event was changed somewhere else since it was opened here (another tab, Undo or a patch), so these edits " +
                              "can't be written over it. Revert to read it again, then make them again.";
            return;
        }
        // code an event jumps to can only be found again through its event, and that may have moved
        var fresh = Script.IsCodeView && LufiaMap.EventScriptOffset(_rom, Script.MapId, Script.Event) != Script.JumpBase ? null : EventScript.Reload(_rom, Script);
        if (fresh == null)
        {
            StatusText.Text = "This event was changed somewhere else and can't be shown as it is now here: open it again from the list.";
            return;
        }
        int line = _selectedLine;
        Show(_rom, fresh, _title, _owner);
        if (line >= 0 && _lines.Count > 0) SelectLine(Math.Min(line, _lines.Count - 1));
        StatusText.Text = "Read again: the event was changed somewhere else meanwhile.";
    }

    private readonly Stack<(EventScript Script, string Title)> _history = new();
    private string _title = "";

    // shows an event for editing
    public void Load(RomBuffer rom, EventScript script, string title, IEventHost host)
    {
        _history.Clear();
        Show(rom, script, title, host);
    }

    private void Show(RomBuffer rom, EventScript script, string title, IEventHost host)
    {
        _title = title;
        BackButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _rom = rom; Script = script; _owner = host;
        _mapId = script.MapId; _event = script.Event;
        if (!ReferenceEquals(rom, _choiceRom)) { _choiceCache.Clear(); _choiceRom = rom; }
        HeaderText.Text = title;
        Fill();
        _selectedLine = -1;
        bool cutscene = script.Ops.Any(CutsceneStage.IsStageCommand);
        if (_cutsceneMode || StageToggle.IsChecked == true || (cutscene && !_stageChosen)) ShowStage(true);
        if (_cutsceneMode && !_timelineChosen) ShowTimeline(true);
        else if (TimelineCard.Visibility == Visibility.Visible) Timeline.Bind(this, Stage);
        else if (Stage.Visibility == Visibility.Visible) Stage.Bind(rom, this);
    }

    private readonly Dictionary<ParamKind, IReadOnlyList<(int Value, string Label)>?> _choiceCache = new();
    private RomBuffer? _choiceRom;

    // dropdown entries for a field kind (null = plain number)
    public IReadOnlyList<(int Value, string Label)>? ChoicesFor(ParamKind kind)
    {
        if (kind == ParamKind.Sound) return EventCommands.Choices(kind, _rom);   // (not cached, the names can change in Settings)
        if (_choiceCache.TryGetValue(kind, out var choices)) return choices;
        choices = EventCommands.Choices(kind, _rom, EventScript.MapLabel);
        _choiceCache[kind] = choices;
        return choices;
    }

    // jump target choices: every line, plus the original outside target if there is one
    public IReadOnlyList<string> JumpChoices(ScriptOp op, int k)
    {
        var list = Script.Ops.Select((o, i) => $"line {i + 1}: {Summary(o)}").ToList();
        var target = k < op.Targets.Count ? op.Targets[k] : null;
        if (target?.Op == null) list.Add($"outside this event (0x{target?.Absolute ?? 0:X6})");
        return list;
    }

    private string Summary(ScriptOp op)
    {
        string summary = op.IsText ? EventScript.SpeakerName(op.Bytes) + ": " + op.Text.Replace("\n", " ") : Script.Describe(op);
        return summary.Length > 48 ? summary[..48] + "…" : summary;
    }

    private void Fill()
    {
        ResetUndo();
        if (BytesCard.Visibility == Visibility.Visible) Dispatcher.BeginInvoke(() => Bytes.Bind(this));
        _lines.Clear();
        for (int i = 0; i < Script.Ops.Count; i++) _lines.Add(new EventLineVm(this, Script.Ops[i], i));
        int texts = Script.Ops.Count(o => o.IsText);
        InfoText.Text = $"Script at ROM 0x{Script.Start:X6} ({Script.End - Script.Start} bytes), {Script.Ops.Count} lines, " +
                        $"{texts} dialogue box{(texts == 1 ? "" : "es")}." +
                        (Script.StopReason != null
                            ? $" Decoding stopped at an {Script.StopReason}; the script continues there unchanged."
                            : "");
        if (Script.IsCodeView)
            InfoText.Text = $"Code at ROM 0x{Script.Start:X6} that event {Script.Event} jumps to ({Script.Ops.Count} lines). " +
                            "Same-size edits can be applied here; open the event itself to add or remove lines. " + InfoText.Text;
        try { InfoText.Text = "What it does: " + EventHints.Summarize(_rom, Script) + "\n" + InfoText.Text; } catch { }
        StatusText.Text = "";
    }

    private bool _stageChosen;
    private bool _timelineChosen;
    private bool _cutsceneMode;

    // cutscene editor layout (the Cutscenes tab): the stage is always shown and gets most of the room,
    // and the lines become the cutscene's timeline
    public bool CutsceneMode
    {
        get => _cutsceneMode;
        set
        {
            _cutsceneMode = value;
            StageToggle.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            LinesColumn.Width = value ? new GridLength(1, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
            HelpText.Text = value
                ? "The lines on the left are the cutscene's timeline; click one to see the stage at that moment, ▶ Play to watch from there. " +
                  "＋ adds a command below a line (Characters, Camera & screen, Music & sound, Timing…). On the stage, click a tile to move the " +
                  "selected \"Place a character\" / \"Move the camera\" line there; walks are edited under the stage. ✔ Apply writes the cutscene, 💾 the walks."
                : DefaultHelp;
        }
    }

    private string StageSplitKey => _cutsceneMode ? "cutscenes.stage" : "eventeditor.stage";

    private const string DefaultHelp =
        "Dialogue: Enter = new line in the box. Tags: [Hero] [Lufia] [Aguro] [Jerin] [Maxim] [Selan] [Guy] [Artea], [ITEM:xx] [SPELL:xx] [TOWN:xx], " +
        "and [xx] / [xx:yy] for other codes. Commands: pick values from the lists, jump targets by line, ＋ inserts below, ✕ deletes. " +
        "Story flags are shared by the whole game, so change them with care.";

    private void StageToggle_Click(object sender, RoutedEventArgs e)
    {
        _stageChosen = true;
        ShowStage(StageToggle.IsChecked == true);
    }

    private void TimelineToggle_Click(object sender, RoutedEventArgs e) { _timelineChosen = true; ShowTimeline(TimelineToggle.IsChecked == true); }

    // shows or hides the timeline (it needs the stage, so it turns that on)
    public void ShowTimeline(bool show)
    {
        TimelineToggle.IsChecked = show;
        TimelineCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TimelineSplitRow.Height = new GridLength(show ? 7 : 0);
        TimelineRow.Height = show ? Common.LayoutMemory.Parse(Common.LayoutMemory.Get("eventeditor.timeline")) ?? new GridLength(240) : new GridLength(0);
        if (!show) return;
        if (Stage.Visibility != Visibility.Visible) ShowStage(true);
        Timeline.Bind(this, Stage);
        if (_selectedLine >= 0) Timeline.SelectLine(_selectedLine);
    }

    private void ShowStage(bool show)
    {
        StageToggle.IsChecked = show;
        Stage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SplitterColumn.Width = new GridLength(show ? 6 : 0);
        StageColumn.Width = show ? new GridLength(_cutsceneMode ? 1.7 : 1.2, GridUnitType.Star) : new GridLength(0);
        // the split the user dragged to last time (kept separately for the Cutscenes tab and the event editor)
        if (show && Common.LayoutMemory.Get(StageSplitKey)?.Split(';') is { Length: 2 } split &&
            Common.LayoutMemory.Parse(split[0]) is { } linesWidth && Common.LayoutMemory.Parse(split[1]) is { } stageWidth)
        {
            LinesColumn.Width = linesWidth; StageColumn.Width = stageWidth;
        }
        else LinesColumn.Width = new GridLength(1, GridUnitType.Star);
        if (show)
        {
            if (Window.GetWindow(this) is Window window && window.Width < 1400 && window.WindowState == WindowState.Normal && window is EventEditorWindow) window.Width = 1400;
            Stage.Bind(_rom, this);
            if (_selectedLine >= 0) Stage.ShowLine(_selectedLine);
        }
    }

    // reordering

    // moves a line to a new index (jumps keep their targets)
    public void MoveLine(int from, int to)
    {
        if (from < 0 || from >= _lines.Count) return;
        to = Math.Clamp(to, 0, _lines.Count - 1);
        if (to == from) return;
        var line = _lines[from];
        Script.Move(line.Op, to);
        _lines.Move(from, to);
        Changed();
        SelectLine(to);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventLineVm line }) { int i = _lines.IndexOf(line); MoveLine(i, i - 1); }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventLineVm line }) { int i = _lines.IndexOf(line); MoveLine(i, i + 1); }
    }

    private System.Windows.Point _gripStart;
    private EventLineVm? _gripLine;

    private void Grip_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _gripStart = e.GetPosition(this);
        _gripLine = (sender as FrameworkElement)?.DataContext as EventLineVm;
    }

    private void Grip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_gripLine == null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var mousePos = e.GetPosition(this);
        if (Math.Abs(mousePos.Y - _gripStart.Y) < 4 && Math.Abs(mousePos.X - _gripStart.X) < 4) return;
        var line = _gripLine;
        _gripLine = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(EventLineVm), line), DragDropEffects.Move);
        foreach (var row in _lines) { row.DropAbove = false; row.DropBelow = false; }
    }

    // upper half of a line = drop above it, lower half = below it
    private static bool DropsAbove(object sender, DragEventArgs e) =>
        sender is FrameworkElement element && e.GetPosition(element).Y < element.ActualHeight / 2;

    private void Line_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(EventLineVm)) || sender is not FrameworkElement { DataContext: EventLineVm target }) return;
        e.Effects = DragDropEffects.Move;
        bool above = DropsAbove(sender, e);
        foreach (var row in _lines) { row.DropAbove = row == target && above; row.DropBelow = row == target && !above; }
        e.Handled = true;
    }

    private void Line_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventLineVm target }) { target.DropAbove = false; target.DropBelow = false; }
    }

    private void Line_Drop(object sender, DragEventArgs e)
    {
        foreach (var row in _lines) { row.DropAbove = false; row.DropBelow = false; }
        if (e.Data.GetData(typeof(EventLineVm)) is not EventLineVm moving || sender is not FrameworkElement { DataContext: EventLineVm target }) return;
        int from = _lines.IndexOf(moving), targetIndex = _lines.IndexOf(target);
        if (from < 0 || targetIndex < 0 || moving == target) return;
        int to = DropsAbove(sender, e) ? targetIndex : targetIndex + 1;   // position in the list before the moved line is taken out
        if (from < to) to--;
        MoveLine(from, to);
        e.Handled = true;
    }

    private void Line_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventLineVm line }) SelectLine(_lines.IndexOf(line));
    }

    // selects a line: outlines it and shows the stage at that point
    public void SelectLine(int index) => SelectLine(index, fromBytes: false);

    public void SelectLine(int index, bool fromBytes)
    {
        if (index < 0) return;
        _selectedLine = index;
        foreach (var row in _lines) row.IsHighlighted = false;
        if (index < _lines.Count) _lines[index].IsHighlighted = true;
        if (Stage.Visibility == Visibility.Visible) Stage.ShowLine(index);
        if (!fromBytes && BytesCard.Visibility == Visibility.Visible) Bytes.Select(index);
        if (!fromBytes && FlowCard.Visibility == Visibility.Visible) Flow.SelectLine(index);
        if (TimelineCard.Visibility == Visibility.Visible) Timeline.SelectLine(index);
    }

    // lines got added/removed/changed outside the Lines view (raw bytes): rebuild the rows and select one
    public void LinesChangedExternally(int select)
    {
        _lines.Clear();
        for (int i = 0; i < Script.Ops.Count; i++) _lines.Add(new EventLineVm(this, Script.Ops[i], i));
        Changed();
        if (_lines.Count > 0) SelectLine(Math.Clamp(select, 0, _lines.Count - 1));
    }

    private bool _flowHooked;

    private static readonly string PrefsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "eventeditor.txt");

    // show command bytes in the Lines view (remembered between sessions)
    public static bool ShowCommandBytes { get; private set; } = LoadShowBytes();

    private static bool LoadShowBytes()
    {
        try { return System.IO.File.Exists(PrefsPath) && System.IO.File.ReadAllText(PrefsPath).Contains("showbytes=1"); } catch { return false; }
    }

    private void ShowBytes_Click(object sender, RoutedEventArgs e)
    {
        ShowCommandBytes = ShowBytesBox.IsChecked == true;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PrefsPath)!);
            System.IO.File.WriteAllText(PrefsPath, ShowCommandBytes ? "showbytes=1" : "showbytes=0");
        }
        catch { }
        foreach (var row in _lines) row.RefreshShowBytes();
    }

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        bool bytes = ViewBytes.IsChecked == true, flow = ViewFlow.IsChecked == true;
        LinesCard.Visibility = bytes || flow ? Visibility.Collapsed : Visibility.Visible;
        BytesCard.Visibility = bytes ? Visibility.Visible : Visibility.Collapsed;
        FlowCard.Visibility = flow ? Visibility.Visible : Visibility.Collapsed;
        if (bytes) { Bytes.Bind(this); if (_selectedLine >= 0) Bytes.Select(_selectedLine); }
        else if (flow)
        {
            if (!_flowHooked) { Flow.BlockSelected += ShowFlowBlock; _flowHooked = true; }
            Flow.Bind(this);
            if (_selectedLine >= 0) Flow.SelectLine(_selectedLine);
        }
        else if (_selectedLine >= 0) HighlightLine(_selectedLine);
    }

    private (int Start, int End) _flowRange = (-1, -1);

    // shows the picked card's lines in the editor under the graph
    private void ShowFlowBlock(FlowBlock? block)
    {
        if (block == null) { FlowLines.ItemsSource = null; _flowRange = (-1, -1); FlowLinesHeader.Text = "Pick a card to edit its lines."; return; }
        if (_flowRange == (block.Start, block.End) && FlowLines.ItemsSource != null) return;   // keep focus while editing
        _flowRange = (block.Start, block.End);
        FlowLines.ItemsSource = _lines.Skip(block.Start).Take(block.End - block.Start).ToList();
        FlowLinesHeader.Text = $"Lines {block.Start + 1}{(block.End - block.Start > 1 ? $"-{block.End}" : "")}";
    }

    private void FlowAutoLayout_Click(object sender, RoutedEventArgs e) => Flow.AutoLayout();

    // opens the add-line menu at a position (the flow graph uses this)
    public void ShowInsertMenuAt(FrameworkElement anchor, int insertAt) => ShowInsertMenu(anchor, Math.Clamp(insertAt, 0, _lines.Count));

    // copies lines [start, end) and inserts the copies right after them
    public void DuplicateLines(int start, int end)
    {
        int insertAt = end;
        for (int i = start; i < end; i++)
        {
            var source = Script.Ops[i];
            ScriptOp copy;
            if (source.IsText) { copy = Script.NewText(source.Bytes[0], source.Text, insertAt); copy.Bytes = (byte[])source.Bytes.Clone(); }
            else
            {
                copy = Script.NewCommand((byte[])source.Bytes.Clone(), insertAt);
                copy.Targets.Clear();
                foreach (var target in source.Targets) copy.Targets.Add(new JumpRef { Op = target.Op, Absolute = target.Absolute });
            }
            insertAt++;
        }
        LinesChangedExternally(end);
    }

    // deletes lines [start, end) after asking
    public void DeleteLines(int start, int end)
    {
        if (MessageBox.Show($"Delete lines {start + 1}-{end}? Jumps to them move to the line after.", "Delete lines",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        for (int i = end - 1; i >= start; i--)
        {
            var error = Script.Remove(Script.Ops[i]);
            if (error != null) { StatusText.Text = error; break; }
        }
        LinesChangedExternally(Math.Max(0, start - 1));
    }

    // inserts a command after line after (-1 = at the start) and selects it
    public int InsertCommand(int after, byte[] bytes)
    {
        int insertAt = Math.Clamp(after + 1, 0, _lines.Count);
        InsertOp(Script.NewCommand(bytes, insertAt), insertAt);
        SelectLine(insertAt);
        return insertAt;
    }

    // replaces a command line's bytes (the stage uses this for clicked positions and the chosen path)
    public void SetLineBytes(int index, byte[] bytes)
    {
        if (index < 0 || index >= _lines.Count) return;
        _lines[index].SetBytes(bytes);
    }

    // undo / redo
    private const int UndoLimit = 50;
    private readonly LinkedList<ScriptSnapshot> _undo = new();
    private readonly Stack<ScriptSnapshot> _redo = new();
    private ScriptSnapshot? _baseline;
    private DateTime _lastTextEdit;
    private bool _restoring;

    private void ResetUndo()
    {
        _undo.Clear(); _redo.Clear();
        _baseline = Script?.TakeSnapshot();
        UpdateUndoButtons();
    }

    // remember the state from before this edit (typing in one box within 2 s counts as one step)
    private void RecordUndo(bool textEdit)
    {
        if (_restoring || Script == null) return;
        var snapshot = Script.TakeSnapshot();
        if (_baseline != null && !snapshot.SameAs(_baseline))
        {
            bool merge = textEdit && (DateTime.Now - _lastTextEdit).TotalSeconds < 2 && _undo.Count > 0;
            if (!merge)
            {
                _undo.AddLast(_baseline);
                if (_undo.Count > UndoLimit) _undo.RemoveFirst();
            }
            _redo.Clear();
        }
        _lastTextEdit = textEdit ? DateTime.Now : DateTime.MinValue;
        _baseline = snapshot;
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        UndoButton.ToolTip = $"Undo ({_undo.Count} step{(_undo.Count == 1 ? "" : "s")}, Ctrl+Z)";
        RedoButton.ToolTip = $"Redo ({_redo.Count} step{(_redo.Count == 1 ? "" : "s")}, Ctrl+Y)";
    }

    public void Undo()
    {
        if (_undo.Count == 0 || Script == null) return;
        _redo.Push(Script.TakeSnapshot());
        var snapshot = _undo.Last!.Value; _undo.RemoveLast();
        RestoreSnapshot(snapshot);
    }

    public void Redo()
    {
        if (_redo.Count == 0 || Script == null) return;
        _undo.AddLast(Script.TakeSnapshot());
        RestoreSnapshot(_redo.Pop());
    }

    private void RestoreSnapshot(ScriptSnapshot snapshot)
    {
        _restoring = true;
        try
        {
            int keep = _selectedLine;
            Script.Restore(snapshot);
            _lines.Clear();
            for (int i = 0; i < Script.Ops.Count; i++) _lines.Add(new EventLineVm(this, Script.Ops[i], i));
            Changed();
            _baseline = Script.TakeSnapshot();
            if (keep >= 0 && _lines.Count > 0) SelectLine(Math.Min(keep, _lines.Count - 1));
        }
        finally { _restoring = false; }
        UpdateUndoButtons();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // inside a text box the box's own undo applies
        if (System.Windows.Input.Keyboard.FocusedElement is not System.Windows.Controls.TextBox && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            if (e.Key == System.Windows.Input.Key.Z && !shift) { Undo(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Y || (e.Key == System.Windows.Input.Key.Z && shift)) { Redo(); e.Handled = true; }
        }
        base.OnPreviewKeyDown(e);
    }

    // called after every edit: renumbers the lines and refreshes the descriptions / jump pickers
    public void Changed(bool rebuild = true)
    {
        if (_refreshing) return;
        RecordUndo(textEdit: !rebuild);
        _refreshing = true;
        try
        {
            if (rebuild)
                for (int i = 0; i < _lines.Count; i++) { _lines[i].Index = i; _lines[i].Refresh(); }
            StatusText.Text = Script.IsModified
                ? (Script.FitsInPlace() ? "Edited (same size: will be written in place)." : "Edited (size changed: the event will be moved when applied).")
                : "";
            if (Stage.Visibility == Visibility.Visible) { Stage.Resimulate(); if (_selectedLine >= 0) Stage.ShowLine(_selectedLine, scroll: false); }
            if (rebuild && BytesCard.Visibility == Visibility.Visible) Bytes.Refresh();
            if (rebuild && FlowCard.Visibility == Visibility.Visible)
            {
                _flowRange = (-1, -1);
                Flow.Rebuild();
                ShowFlowBlock(Flow.Selected);
            }
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _refreshing = false; }
    }

    private void ShowInsertMenu(FrameworkElement anchor, int insertAt)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        void AddText(string name, byte opener)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => InsertOp(Script.NewText(opener, "New text", insertAt), insertAt);
            menu.Items.Add(item);
        }
        AddText("Dialogue: narration", 0x0C);
        AddText("Dialogue: text box", 0x0D);
        AddText("Dialogue: the leader speaks", 0x89);
        AddText("Dialogue: character 1 speaks", 0x90);
        menu.Items.Add(new Separator());
        foreach (var group in EventCommands.Definitions.Where(d => d.Template != null && (!d.CutsceneOnly || CutsceneMode)).GroupBy(d => d.Category))
        {
            var sub = new MenuItem { Header = group.Key };
            foreach (var command in group)
            {
                var item = new MenuItem { Header = command.Name, ToolTip = command.Waits.Length > 0 ? "The event waits here " + command.Waits + "." : null };
                item.Click += (_, _) => InsertOp(Script.NewCommand((byte[])command.Template!.Clone(), insertAt), insertAt);
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }
        var rawItem = new MenuItem { Header = "Other command (type its bytes)" };
        rawItem.Click += (_, _) => InsertOp(Script.NewCommand(new byte[] { 0x84 }, insertAt), insertAt);
        menu.Items.Add(rawItem);
        menu.IsOpen = true;
    }

    private void InsertOp(ScriptOp op, int insertAt)
    {
        _lines.Insert(insertAt, new EventLineVm(this, op, insertAt));
        Changed();
        HighlightLine(insertAt);
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EventLineVm line } button) ShowInsertMenu(button, _lines.IndexOf(line) + 1);
    }

    private void InsertAtStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) ShowInsertMenu(button, 0);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EventLineVm line }) return;
        var error = Script.Remove(line.Op);
        if (error != null) { line.ErrorText = error; return; }
        _lines.Remove(line);
        Changed();
    }


    // writes the event (and its paths) to the ROM. quiet = no questions or confirmations
    private bool Write(bool quiet)
    {
        if (!Script.IsModified && !Stage.HasPathChanges) { StatusText.Text = "Nothing changed."; return true; }
        try
        {
            bool fits = Script.FitsInPlace();
            bool allowExpand = _rom.Length >= MapWriter.ExpandedSize || quiet;
            if (!fits && !allowExpand)
            {
                if (!_owner.ConfirmExpand("The edited event is a different size than the original, so it has to be moved."))
                {
                    StatusText.Text = "Not applied. Keep every line the same size to save without expanding.";
                    return false;
                }
                allowExpand = true;
            }
            string? walks = Stage.Visibility == Visibility.Visible ? Stage.SavePathsWithEvent() : null;
            string result = Script.Save(allowExpand) + (walks != null ? "\n\n" + walks : "");
            _owner.EventSaved($"Map {_mapId:X2} event {_event}: dialogue/commands");
            Script = EventScript.Reload(_rom, Script) ?? Script;
            var undo = _undo.ToList(); var redo = _redo.Reverse().ToList();
            int line = _selectedLine;
            double scroll = LinesScroll.VerticalOffset;
            Fill();
            // writing isn't a step back, so the undo history, the selected line and the scroll position carry on
            foreach (var undoStep in undo) _undo.AddLast(undoStep);
            foreach (var redoStep in redo) _redo.Push(redoStep);
            UpdateUndoButtons();
            if (Stage.Visibility == Visibility.Visible) Stage.Bind(_rom, this);
            if (line >= 0 && _lines.Count > 0) SelectLine(Math.Min(line, _lines.Count - 1));
            Dispatcher.BeginInvoke(() => LinesScroll.ScrollToVerticalOffset(scroll), System.Windows.Threading.DispatcherPriority.Loaded);
            StatusText.Text = "✔ Saved in the project.";
            if (!quiet) _owner.ConfirmWritten($"Event {_event} of map {_mapId:X2} saved in the project", result);
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Not applied: " + ex.Message;
            return false;
        }
    }

    // scrolls to a line and outlines it
    public void HighlightLine(int index)
    {
        foreach (var row in _lines) row.IsHighlighted = false;
        if (index < 0 || index >= _lines.Count) return;
        _lines[index].IsHighlighted = true;
        Dispatcher.BeginInvoke(() =>
        {
            Lines.UpdateLayout();
            if (Lines.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container) container.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // goes where a jump leads: a line of this event, the start of another event, or code outside it
    public void FollowJump(ScriptOp op, int k)
    {
        if (k >= op.Targets.Count) return;
        var jump = op.Targets[k];
        if (jump.Op != null) { HighlightLine(Script.Ops.IndexOf(jump.Op)); return; }
        if (!ConfirmDiscard()) return;
        int ev = Script.EventStartingAt(jump.Absolute);
        EventScript target;
        string title;
        if (ev >= 0 && !Script.IsCodeView && ev != Script.Event)
        {
            target = EventScript.Load(_rom, _mapId, ev) ?? Script;
            title = $"Map {_mapId:X2} - event {ev} (jumped to from event {Script.Event})";
        }
        else
        {
            target = EventScript.LoadAt(_rom, _mapId, Script.Event, jump.Absolute, Script.JumpBase);
            title = $"Map {_mapId:X2} - code at 0x{jump.Absolute:X6}, reached from event {Script.Event}";
        }
        _history.Push((Script, _title));
        Show(_rom, target, title, _owner);
        HighlightLine(0);
    }

    // opens the event a "run an event of another map" line continues with (Back comes back here)
    public void OpenChained(ScriptOp op)
    {
        if (op.IsText || op.Bytes.Length != 3 || op.Bytes[0] != 0x51) return;
        int map = op.Bytes[1], ev = EventCommands.ChainedEvent(op.Bytes);
        var target = EventScript.LoadWhole(_rom, map, ev);
        if (target == null || target.Ops.Count == 0) { StatusText.Text = $"Event {ev} of map {map:X2} has no script."; return; }
        if (!ConfirmDiscard()) return;
        _history.Push((Script, _title));
        string name = EventScript.MapLabel?.Invoke(map) ?? $"map {map:X2}";
        Show(_rom, target, $"{name} - event {ev} (continued from event {Script.Event} of map {Script.MapId:X2})", _owner);
        SelectLine(0);
    }

    private void OpenChained_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventLineVm line }) OpenChained(line.Op);
    }

    // shows where a story flag is used (if the host can)
    public void ShowFlag(int flag) => _owner.ShowFlag(flag);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0 || !ConfirmDiscard()) return;
        var (script, title) = _history.Pop();
        var fresh = EventScript.Reload(_rom, script) ?? script;
        Show(_rom, fresh, title, _owner);
    }

    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ParamVm param }) param.Follow();
    }

    // a sound got renamed in Settings: the lines with a sound show the new name
    private int _soundNames = Core.Audio.SoundNames.Version;
    private void SoundNamesChanged() => Dispatcher.BeginInvoke(() =>
    {
        _soundNames = Core.Audio.SoundNames.Version;
        foreach (var row in _lines.Where(l => l.Params.Any(p => p.IsAudio && !p.IsSong)).ToList()) { row.BuildParams(); row.Refresh(); }
    });

    private void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ParamVm param } button) param.Listen(button);
    }

    private void FlagUses_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ParamVm param }) param.ShowFlagUses();
    }

    // asks before throwing away edits that weren't applied. true = OK to go on
    public bool ConfirmDiscard() =>
        Script == null || !Script.IsModified || Write(quiet: true) ||
        MessageBox.Show($"The event's changes couldn't be written ({StatusText.Text}). Discard them?", "Event changes",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        Script = EventScript.Reload(_rom, Script) ?? Script;
        Fill();
        if (Stage.Visibility == Visibility.Visible) Stage.Bind(_rom, this);
        StatusText.Text = "Put back as the project has it.";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        CloseRequested?.Invoke();
    }
}
