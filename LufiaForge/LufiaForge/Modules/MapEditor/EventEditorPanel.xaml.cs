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

/// <summary>An editable value of a command: a number, a choice (who, direction, item, map…), or a jump target line.</summary>
public partial class ParamVm : ObservableObject
{
    private readonly EventLineVm _line;
    public string Label { get; init; } = "";
    /// <summary>The field (null = the answer count of command 03 or a raw byte, see <see cref="Position"/>).</summary>
    public ParamDef? Def { get; init; }
    /// <summary>Byte position for raw fields without a definition, or -1.</summary>
    public int Position { get; init; } = -1;
    /// <summary>Index into the command's jump targets, or -1.</summary>
    public int JumpIndex { get; init; } = -1;
    public bool Decimal { get; init; }
    /// <summary>Values behind <see cref="Choices"/> (null for jumps and plain fields).</summary>
    public IReadOnlyList<int>? ChoiceValues { get; init; }

    public bool IsFlag => Def?.Kind == ParamKind.Flag;
    public string FlagNameText => IsFlag && EventScript.FlagName?.Invoke(Raw) is { } n ? n : "";

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
            var b = _line.Op.Bytes;
            if (Def != null) { try { return Def.Get(b); } catch { return 0; } }
            return Position >= 0 && Position < b.Length ? b[Position] : 0;
        }
    }

    private bool Hex => Def != null
        ? Def.Kind is ParamKind.Hex or ParamKind.Flag or ParamKind.Music or ParamKind.Sound or ParamKind.Formation
        : !Decimal;

    private void Store(int v)
    {
        var bytes = (byte[])_line.Op.Bytes.Clone();
        if (Def != null) Def.Set(bytes, v);
        else if (Position >= 0 && Position < bytes.Length) bytes[Position] = (byte)v;
        _line.SetBytes(bytes, resizeChoices: Def == null && Position == 1 && bytes[0] == 0x03);
    }

    public string Value
    {
        get => Hex ? Raw.ToString(Raw > 0xFF ? "X4" : "X2") : Raw.ToString();
        set
        {
            int min = Def?.Min ?? 0, max = Def?.Max ?? 255;
            var style = Hex ? NumberStyles.HexNumber : NumberStyles.Integer;
            if (!int.TryParse(value?.Trim(), style, CultureInfo.InvariantCulture, out int v) || v < min || v > max)
            {
                string lo = Hex ? min.ToString("X2") : min.ToString(), hi = Hex ? max.ToString("X2") : max.ToString();
                _line.ErrorText = $"{Label}: enter {(Hex ? "a hex value" : "a number")} from {lo} to {hi}.";
                return;
            }
            Store(v);
        }
    }

    public int ChoiceIndex
    {
        get
        {
            if (ChoiceValues != null) return ChoiceValues.ToList().IndexOf(Raw);
            if (!IsJump) return -1;   // plain value field: its (hidden) dropdown has nothing to select
            var t = JumpIndex < _line.Op.Targets.Count ? _line.Op.Targets[JumpIndex] : null;
            if (t?.Op == null) return Choices.Count - 1;          // the "outside this event" entry
            return _line.Owner.Script.Ops.IndexOf(t.Op);
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

/// <summary>One line of the event editor: a command or a dialogue box.</summary>
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
    /// <summary>A "continue with event N of map M" line (51): its target can be opened.</summary>
    public bool CanOpenChained => !Op.IsText && Op.Bytes.Length == 3 && Op.Bytes[0] == 0x51;
    public string OpenChainedText => CanOpenChained ? $"▶ Open event {EventCommands.ChainedEvent(Op.Bytes)} of map {Op.Bytes[1]:X2}" : "";

    public string CommandName => Op.IsText || Op.Bytes.Length == 0 ? "" : EventCommands.Find(Op.Bytes[0])?.Name ?? "Unknown command";
    public string WaitsText => Op.IsText || Op.Bytes.Length == 0 ? ""
        : EventCommands.Find(Op.Bytes[0])?.Waits is { Length: > 0 } w ? "⏸ waits " + w : "";

    // ── dialogue ──
    public static readonly IReadOnlyList<string> Speakers = BuildSpeakers();
    private static IReadOnlyList<string> BuildSpeakers()
    {
        var l = new List<string> { "Narration (0C)", "Text box (0D)" };
        for (int n = 0; n < 40; n++)
        {
            string who = EventCommands.Actor(n);
            l.Add($"{char.ToUpper(who[0]) + who[1..]} says ({(n < 8 ? 0x88 + n : 0x90 + n - 8):X2})");
        }
        return l;
    }
    public IReadOnlyList<string> SpeakerChoices => Speakers;
    public bool SpeakerEditable => Op.IsText && Op.Bytes.Length == 1 && (Op.Bytes[0] is 0x0C or 0x0D || Op.Bytes[0] is >= 0x88 and <= 0xAF);

    public int SpeakerIndex
    {
        get
        {
            if (!SpeakerEditable) return -1;
            byte b = Op.Bytes[0];
            return b switch { 0x0C => 0, 0x0D => 1, >= 0x88 and <= 0x8F => 2 + b - 0x88, _ => 10 + b - 0x90 };
        }
        set
        {
            if (!SpeakerEditable || value < 0) return;
            byte b = value switch { 0 => 0x0C, 1 => 0x0D, < 10 => (byte)(0x88 + value - 2), _ => (byte)(0x90 + value - 10) };
            if (Op.Bytes[0] == b) return;
            Op.Bytes = new[] { b };
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
            int now = Owner.Script.EncodeText(Op.Text, Op.Terminator).Length;
            if (Op.IsNew) return $"{now} bytes (new)";
            int was = Op.RawLength - Op.Bytes.Length;
            return !Op.TextChanged ? $"{was} bytes" : now == was ? $"{now} bytes (same size)" : $"{now} bytes (was {was})";
        }
    }

    // ── commands ──
    public string OpcodeHex
    {
        get => Op.Bytes.Length > 0 ? Op.Bytes[0].ToString("X2") : "";
        set
        {
            if (!byte.TryParse(value?.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte op))
            { ErrorText = "Command: enter a hex byte."; return; }
            if (Op.Bytes.Length > 0 && Op.Bytes[0] == op) return;
            int len = EventScript.CommandLength(op, op == 0x03 ? 1 : 0);
            var bytes = new byte[Math.Max(1, len)];
            bytes[0] = op;
            if (op == 0x03) bytes[1] = 1;
            Apply(bytes);
        }
    }

    public void SetBytes(byte[] bytes, bool resizeChoices = false)
    {
        if (resizeChoices)
        {
            // answer count: resize the target list
            var resized = new byte[2 + 2 * bytes[1]];
            Array.Copy(bytes, resized, Math.Min(bytes.Length, resized.Length));
            bytes = resized;
        }
        Apply(bytes);
    }

    private void Apply(byte[] bytes)
    {
        var err = Owner.Script.SetCommandBytes(Op, bytes);
        ErrorText = err ?? "";
        if (err != null) { OnPropertyChanged(nameof(OpcodeHex)); return; }
        BuildParams();
        Owner.Changed();
    }

    public void BuildParams()
    {
        Params.Clear();
        if (Op.IsText || Op.Bytes.Length == 0) return;
        var b = Op.Bytes;
        if (b[0] == 0x03)
        {
            Params.Add(new ParamVm(this) { Label = "answers", Position = 1, Decimal = true });
            for (int k = 0; k < b[1]; k++) Params.Add(new ParamVm(this) { Label = $"#{k + 1}", JumpIndex = k, Choices = Owner.JumpChoices(Op, k) });
            return;
        }
        var def = EventCommands.Find(b[0]);
        if (def == null || b.Length != EventScript.CommandLength(b[0], b.Length > 1 ? b[1] : 0))
        {
            for (int i = 1; i < b.Length; i++) Params.Add(new ParamVm(this) { Label = $"byte {i}", Position = i });
            return;
        }
        foreach (var d in def.Params)
        {
            if (d.Kind == ParamKind.Jump)
            {
                Params.Add(new ParamVm(this) { Label = d.Label, Def = d, JumpIndex = d.JumpIndex, Choices = Owner.JumpChoices(Op, d.JumpIndex) });
                continue;
            }
            var choices = Owner.ChoicesFor(d.Kind);
            Params.Add(new ParamVm(this)
            {
                Label = d.Label, Def = d,
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
        foreach (var p in Params.Where(p => p.IsJump)) p.Choices = Owner.JumpChoices(Op, p.JumpIndex);
        var copy = Params.ToList();
        Params.Clear();
        foreach (var p in copy) Params.Add(p);
    }
}

public partial class EventEditorPanel : UserControl
{
    private RomBuffer _rom = null!;
    private int _mapId, _event;
    private IEventHost _owner = null!;
    private readonly ObservableCollection<EventLineVm> _lines = new();
    private bool _refreshing;

    public EventScript Script { get; private set; } = null!;

    /// <summary>The screen that opened the editor (confirmations, save list).</summary>
    public IEventHost Host => _owner;
    private int _selectedLine = -1;

    /// <summary>Raised by the Close button (only shown when the panel sits in its own window).</summary>
    public event Action? CloseRequested;
    public bool ShowCloseButton { set => CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public bool IsLoadedScript => Script != null;

    public EventEditorPanel()
    {
        InitializeComponent();
        Lines.ItemsSource = _lines;
    }

    private readonly Stack<(EventScript Script, string Title)> _history = new();
    private string _title = "";

    /// <summary>Show an event for editing.</summary>
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
        else if (Stage.Visibility == Visibility.Visible) Stage.Bind(rom, this);
    }

    private readonly Dictionary<ParamKind, IReadOnlyList<(int Value, string Label)>?> _choiceCache = new();
    private RomBuffer? _choiceRom;

    /// <summary>Dropdown entries for a field kind (null = plain number).</summary>
    public IReadOnlyList<(int Value, string Label)>? ChoicesFor(ParamKind kind)
    {
        if (_choiceCache.TryGetValue(kind, out var c)) return c;
        c = EventCommands.Choices(kind, _rom, EventScript.MapLabel);
        _choiceCache[kind] = c;
        return c;
    }

    /// <summary>Jump target choices: every line, plus the original outside target when there is one.</summary>
    public IReadOnlyList<string> JumpChoices(ScriptOp op, int k)
    {
        var list = Script.Ops.Select((o, i) => $"line {i + 1}: {Summary(o)}").ToList();
        var t = k < op.Targets.Count ? op.Targets[k] : null;
        if (t?.Op == null) list.Add($"outside this event (0x{t?.Absolute ?? 0:X6})");
        return list;
    }

    private string Summary(ScriptOp o)
    {
        string s = o.IsText ? EventScript.SpeakerName(o.Bytes) + ": " + o.Text.Replace("\n", " ") : Script.Describe(o);
        return s.Length > 48 ? s[..48] + "…" : s;
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
    private bool _cutsceneMode;

    /// <summary>
    /// Cutscene editor layout (the Cutscenes tab): the stage is always shown and gets most of the room,
    /// the lines become the cutscene's timeline.
    /// </summary>
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

    private const string DefaultHelp =
        "Dialogue: Enter = new line in the box. Tags: [Hero] [Lufia] [Aguro] [Jerin] [Maxim] [Selan] [Guy] [Artea], [ITEM:xx] [SPELL:xx] [TOWN:xx], " +
        "and [xx] / [xx:yy] for other codes. Commands: pick values from the lists, jump targets by line, ＋ inserts below, ✕ deletes. " +
        "Story flags are shared by the whole game, so change them with care.";

    private void StageToggle_Click(object sender, RoutedEventArgs e)
    {
        _stageChosen = true;
        ShowStage(StageToggle.IsChecked == true);
    }

    private void ShowStage(bool show)
    {
        StageToggle.IsChecked = show;
        Stage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SplitterColumn.Width = new GridLength(show ? 6 : 0);
        StageColumn.Width = show ? new GridLength(_cutsceneMode ? 1.7 : 1.2, GridUnitType.Star) : new GridLength(0);
        if (show)
        {
            if (Window.GetWindow(this) is Window w && w.Width < 1400 && w.WindowState == WindowState.Normal && w is EventEditorWindow) w.Width = 1400;
            Stage.Bind(_rom, this);
            if (_selectedLine >= 0) Stage.ShowLine(_selectedLine);
        }
    }

    // ── reordering ──

    /// <summary>Move a line to a new index (jumps keep their targets).</summary>
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
        if (sender is FrameworkElement { DataContext: EventLineVm l }) { int i = _lines.IndexOf(l); MoveLine(i, i - 1); }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventLineVm l }) { int i = _lines.IndexOf(l); MoveLine(i, i + 1); }
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
        var p = e.GetPosition(this);
        if (Math.Abs(p.Y - _gripStart.Y) < 4 && Math.Abs(p.X - _gripStart.X) < 4) return;
        var line = _gripLine;
        _gripLine = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(EventLineVm), line), DragDropEffects.Move);
        foreach (var l in _lines) { l.DropAbove = false; l.DropBelow = false; }
    }

    /// <summary>Upper half of a line = drop above it, lower half = below it.</summary>
    private static bool DropsAbove(object sender, DragEventArgs e) =>
        sender is FrameworkElement fe && e.GetPosition(fe).Y < fe.ActualHeight / 2;

    private void Line_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(EventLineVm)) || sender is not FrameworkElement { DataContext: EventLineVm target }) return;
        e.Effects = DragDropEffects.Move;
        bool above = DropsAbove(sender, e);
        foreach (var l in _lines) { l.DropAbove = l == target && above; l.DropBelow = l == target && !above; }
        e.Handled = true;
    }

    private void Line_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventLineVm target }) { target.DropAbove = false; target.DropBelow = false; }
    }

    private void Line_Drop(object sender, DragEventArgs e)
    {
        foreach (var l in _lines) { l.DropAbove = false; l.DropBelow = false; }
        if (e.Data.GetData(typeof(EventLineVm)) is not EventLineVm moving || sender is not FrameworkElement { DataContext: EventLineVm target }) return;
        int from = _lines.IndexOf(moving), at = _lines.IndexOf(target);
        if (from < 0 || at < 0 || moving == target) return;
        int to = DropsAbove(sender, e) ? at : at + 1;   // position in the list before removing the moved line
        if (from < to) to--;
        MoveLine(from, to);
        e.Handled = true;
    }

    private void Line_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventLineVm line }) SelectLine(_lines.IndexOf(line));
    }

    /// <summary>Select a line: outline it and show the stage at that point.</summary>
    public void SelectLine(int index) => SelectLine(index, fromBytes: false);

    public void SelectLine(int index, bool fromBytes)
    {
        if (index < 0) return;
        _selectedLine = index;
        foreach (var l in _lines) l.IsHighlighted = false;
        if (index < _lines.Count) _lines[index].IsHighlighted = true;
        if (Stage.Visibility == Visibility.Visible) Stage.ShowLine(index);
        if (!fromBytes && BytesCard.Visibility == Visibility.Visible) Bytes.Select(index);
        if (!fromBytes && FlowCard.Visibility == Visibility.Visible) Flow.SelectLine(index);
    }

    /// <summary>Lines were added/removed/changed outside the Lines view (raw bytes): rebuild the rows and select one.</summary>
    public void LinesChangedExternally(int select)
    {
        _lines.Clear();
        for (int i = 0; i < Script.Ops.Count; i++) _lines.Add(new EventLineVm(this, Script.Ops[i], i));
        Changed();
        if (_lines.Count > 0) SelectLine(Math.Clamp(select, 0, _lines.Count - 1));
    }

    private bool _flowHooked;

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

    /// <summary>Show the lines of the picked card in the editor under the graph.</summary>
    private void ShowFlowBlock(FlowBlock? b)
    {
        if (b == null) { FlowLines.ItemsSource = null; _flowRange = (-1, -1); FlowLinesHeader.Text = "Pick a card to edit its lines."; return; }
        if (_flowRange == (b.Start, b.End) && FlowLines.ItemsSource != null) return;   // keep focus while editing
        _flowRange = (b.Start, b.End);
        FlowLines.ItemsSource = _lines.Skip(b.Start).Take(b.End - b.Start).ToList();
        FlowLinesHeader.Text = $"Lines {b.Start + 1}{(b.End - b.Start > 1 ? $"–{b.End}" : "")}";
    }

    private void FlowAutoLayout_Click(object sender, RoutedEventArgs e) => Flow.AutoLayout();

    /// <summary>Open the add-line menu at a position (used by the flow graph).</summary>
    public void ShowInsertMenuAt(FrameworkElement anchor, int insertAt) => ShowInsertMenu(anchor, Math.Clamp(insertAt, 0, _lines.Count));

    /// <summary>Copy lines [start, end) and insert the copies right after them.</summary>
    public void DuplicateLines(int start, int end)
    {
        int at = end;
        for (int i = start; i < end; i++)
        {
            var src = Script.Ops[i];
            ScriptOp copy;
            if (src.IsText) { copy = Script.NewText(src.Bytes[0], src.Text, at); copy.Bytes = (byte[])src.Bytes.Clone(); }
            else
            {
                copy = Script.NewCommand((byte[])src.Bytes.Clone(), at);
                copy.Targets.Clear();
                foreach (var t in src.Targets) copy.Targets.Add(new JumpRef { Op = t.Op, Absolute = t.Absolute });
            }
            at++;
        }
        LinesChangedExternally(end);
    }

    /// <summary>Delete lines [start, end) after asking.</summary>
    public void DeleteLines(int start, int end)
    {
        if (MessageBox.Show($"Delete lines {start + 1}–{end}? Jumps to them move to the line after.", "Delete lines",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        for (int i = end - 1; i >= start; i--)
        {
            var err = Script.Remove(Script.Ops[i]);
            if (err != null) { StatusText.Text = err; break; }
        }
        LinesChangedExternally(Math.Max(0, start - 1));
    }

    /// <summary>Insert a command after line <paramref name="after"/> (-1 = at the start) and select it.</summary>
    public int InsertCommand(int after, byte[] bytes)
    {
        int at = Math.Clamp(after + 1, 0, _lines.Count);
        InsertOp(Script.NewCommand(bytes, at), at);
        SelectLine(at);
        return at;
    }

    /// <summary>Replace the bytes of a command line (used by the stage: clicked positions, chosen path).</summary>
    public void SetLineBytes(int index, byte[] bytes)
    {
        if (index < 0 || index >= _lines.Count) return;
        _lines[index].SetBytes(bytes);
    }

    // ── undo / redo ──
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

    /// <summary>Remember the state before this edit (typing in one box within 2 s counts as one step).</summary>
    private void RecordUndo(bool textEdit)
    {
        if (_restoring || Script == null) return;
        var now = Script.TakeSnapshot();
        if (_baseline != null && !now.SameAs(_baseline))
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
        _baseline = now;
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        UndoButton.ToolTip = $"Undo ({_undo.Count} step{(_undo.Count == 1 ? "" : "s")}) — Ctrl+Z";
        RedoButton.ToolTip = $"Redo ({_redo.Count}) — Ctrl+Y";
    }

    public void Undo()
    {
        if (_undo.Count == 0 || Script == null) return;
        _redo.Push(Script.TakeSnapshot());
        var s = _undo.Last!.Value; _undo.RemoveLast();
        RestoreSnapshot(s);
    }

    public void Redo()
    {
        if (_redo.Count == 0 || Script == null) return;
        _undo.AddLast(Script.TakeSnapshot());
        RestoreSnapshot(_redo.Pop());
    }

    private void RestoreSnapshot(ScriptSnapshot s)
    {
        _restoring = true;
        try
        {
            int keep = _selectedLine;
            Script.Restore(s);
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
        // inside a text box its own undo applies
        if (System.Windows.Input.Keyboard.FocusedElement is not System.Windows.Controls.TextBox && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            if (e.Key == System.Windows.Input.Key.Z && !shift) { Undo(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Y || (e.Key == System.Windows.Input.Key.Z && shift)) { Redo(); e.Handled = true; }
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Called after any edit: renumber lines and refresh descriptions / jump pickers.</summary>
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
        foreach (var group in EventCommands.Definitions.Where(d => d.Template != null).GroupBy(d => d.Category))
        {
            var sub = new MenuItem { Header = group.Key };
            foreach (var d in group)
            {
                var item = new MenuItem { Header = d.Name, ToolTip = d.Waits.Length > 0 ? "The event waits here " + d.Waits + "." : null };
                item.Click += (_, _) => InsertOp(Script.NewCommand((byte[])d.Template!.Clone(), insertAt), insertAt);
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }
        var raw = new MenuItem { Header = "Other command (type its bytes)" };
        raw.Click += (_, _) => InsertOp(Script.NewCommand(new byte[] { 0x84 }, insertAt), insertAt);
        menu.Items.Add(raw);
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
        if (sender is Button { DataContext: EventLineVm line } b) ShowInsertMenu(b, _lines.IndexOf(line) + 1);
    }

    private void InsertAtStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) ShowInsertMenu(b, 0);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EventLineVm line }) return;
        var err = Script.Remove(line.Op);
        if (err != null) { line.ErrorText = err; return; }
        _lines.Remove(line);
        Changed();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!Script.IsModified && !Stage.HasPathChanges) { StatusText.Text = "Nothing changed."; return; }
        try
        {
            bool fits = Script.FitsInPlace();
            bool allowExpand = _rom.Length >= MapWriter.ExpandedSize;
            if (!fits && !allowExpand)
            {
                if (!_owner.ConfirmExpand("The edited event is a different size than the original, so it has to be moved."))
                {
                    StatusText.Text = "Not applied. Keep every line the same size to save without expanding.";
                    return;
                }
                allowExpand = true;
            }
            string? walks = Stage.Visibility == Visibility.Visible ? Stage.SavePathsWithEvent() : null;
            string result = Script.Save(allowExpand) + (walks != null ? "\n\n" + walks : "");
            _owner.EventSaved($"Map {_mapId:X2} event {_event}: dialogue/commands");
            Script = EventScript.Reload(_rom, Script) ?? Script;
            Fill();
            if (Stage.Visibility == Visibility.Visible) { Stage.Bind(_rom, this); if (_selectedLine >= 0) SelectLine(Math.Min(_selectedLine, _lines.Count - 1)); }
            StatusText.Text = "✔ Written to the ROM. Use Save ROM to write a dated copy.";
            _owner.ConfirmWritten($"Event {_event} of map {_mapId:X2} written to the ROM", result);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Not applied: " + ex.Message;
        }
    }

    /// <summary>Scroll to a line and outline it.</summary>
    public void HighlightLine(int index)
    {
        foreach (var l in _lines) l.IsHighlighted = false;
        if (index < 0 || index >= _lines.Count) return;
        _lines[index].IsHighlighted = true;
        Dispatcher.BeginInvoke(() =>
        {
            Lines.UpdateLayout();
            if (Lines.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement fe) fe.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Go where a jump leads: a line of this event, the start of another event, or code outside it.</summary>
    public void FollowJump(ScriptOp op, int k)
    {
        if (k >= op.Targets.Count) return;
        var t = op.Targets[k];
        if (t.Op != null) { HighlightLine(Script.Ops.IndexOf(t.Op)); return; }
        if (!ConfirmDiscard()) return;
        int ev = Script.EventStartingAt(t.Absolute);
        EventScript target;
        string title;
        if (ev >= 0 && !Script.IsCodeView && ev != Script.Event)
        {
            target = EventScript.Load(_rom, _mapId, ev) ?? Script;
            title = $"Map {_mapId:X2} – event {ev} (jumped to from event {Script.Event})";
        }
        else
        {
            target = EventScript.LoadAt(_rom, _mapId, Script.Event, t.Absolute, Script.JumpBase);
            title = $"Map {_mapId:X2} – code at 0x{t.Absolute:X6}, reached from event {Script.Event}";
        }
        _history.Push((Script, _title));
        Show(_rom, target, title, _owner);
        HighlightLine(0);
    }

    /// <summary>Open the event a "run an event of another map" line continues with (◀ Back returns here).</summary>
    public void OpenChained(ScriptOp op)
    {
        if (op.IsText || op.Bytes.Length != 3 || op.Bytes[0] != 0x51) return;
        int map = op.Bytes[1], ev = EventCommands.ChainedEvent(op.Bytes);
        var target = EventScript.LoadWhole(_rom, map, ev);
        if (target == null || target.Ops.Count == 0) { StatusText.Text = $"Event {ev} of map {map:X2} has no script."; return; }
        if (!ConfirmDiscard()) return;
        _history.Push((Script, _title));
        string name = EventScript.MapLabel?.Invoke(map) ?? $"map {map:X2}";
        Show(_rom, target, $"{name} – event {ev} (continued from event {Script.Event} of map {Script.MapId:X2})", _owner);
        SelectLine(0);
    }

    private void OpenChained_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventLineVm line }) OpenChained(line.Op);
    }

    /// <summary>Show where a story flag is used (if the host supports it).</summary>
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
        if (sender is FrameworkElement { DataContext: ParamVm p }) p.Follow();
    }

    private void FlagUses_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ParamVm p }) p.ShowFlagUses();
    }

    /// <summary>Ask before throwing away unapplied edits. True = OK to continue.</summary>
    public bool ConfirmDiscard() =>
        Script == null || !Script.IsModified ||
        MessageBox.Show("Discard the event changes that weren't applied?", "Unapplied changes",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        Script = EventScript.Reload(_rom, Script) ?? Script;
        Fill();
        StatusText.Text = "Reverted to the event stored in the ROM.";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        CloseRequested?.Invoke();
    }
}
