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

namespace LufiaForge.Modules.MapEditor;

/// <summary>An editable value of a command: a hex/decimal field, an item, or a jump target line.</summary>
public partial class ParamVm : ObservableObject
{
    private readonly EventLineVm _line;
    public string Label { get; init; } = "";
    /// <summary>Byte position in the command (field / item), or -1 for a jump.</summary>
    public int Position { get; init; } = -1;
    /// <summary>Index into the command's jump targets, or -1.</summary>
    public int JumpIndex { get; init; } = -1;
    public bool Decimal { get; init; }
    public bool IsItem { get; init; }
    /// <summary>This value is a story flag number (shows a "uses" button).</summary>
    public bool IsFlag { get; init; }
    public string FlagNameText => IsFlag && Position < _line.Op.Bytes.Length && EventScript.FlagName?.Invoke(_line.Op.Bytes[Position]) is { } n ? n : "";

    public void Follow() { if (IsJump) _line.Owner.FollowJump(_line.Op, JumpIndex); }
    public void ShowFlagUses() { if (IsFlag && Position < _line.Op.Bytes.Length) _line.Owner.ShowFlag(_line.Op.Bytes[Position]); }

    public bool IsJump => JumpIndex >= 0;
    public bool IsChoice => IsJump || IsItem;
    public bool IsField => !IsChoice;
    public IReadOnlyList<string> Choices { get; set; } = Array.Empty<string>();

    public ParamVm(EventLineVm line) { _line = line; }

    public string Value
    {
        get
        {
            if (Position < 0 || Position >= _line.Op.Bytes.Length) return "";
            byte b = _line.Op.Bytes[Position];
            return Decimal ? b.ToString() : b.ToString("X2");
        }
        set
        {
            var style = Decimal ? NumberStyles.Integer : NumberStyles.HexNumber;
            if (!int.TryParse(value?.Trim(), style, CultureInfo.InvariantCulture, out int v) || v is < 0 or > 255)
            {
                _line.ErrorText = $"{Label}: enter {(Decimal ? "a number 0-255" : "a hex byte 00-FF")}.";
                return;
            }
            _line.SetByte(Position, (byte)v);
        }
    }

    public int ChoiceIndex
    {
        get
        {
            if (IsItem) return Position >= 0 && Position < _line.Op.Bytes.Length ? _line.Op.Bytes[Position] : -1;
            if (!IsJump) return -1;   // plain value field: its (hidden) dropdown has nothing to select
            var t = JumpIndex < _line.Op.Targets.Count ? _line.Op.Targets[JumpIndex] : null;
            if (t?.Op == null) return Choices.Count - 1;          // the "outside this event" entry
            return _line.Owner.Script.Ops.IndexOf(t.Op);
        }
        set
        {
            if (value < 0) return;
            if (IsItem) { _line.SetByte(Position, (byte)value); return; }
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

    // ── dialogue ──
    public static readonly IReadOnlyList<string> Speakers = BuildSpeakers();
    private static IReadOnlyList<string> BuildSpeakers()
    {
        var l = new List<string> { "Narration (0C)", "Text box (0D)" };
        for (int n = 0; n < 40; n++) l.Add($"Actor {n} says ({(n < 8 ? 0x88 + n : 0x90 + n - 8):X2})");
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

    public void SetByte(int pos, byte v)
    {
        var bytes = (byte[])Op.Bytes.Clone();
        if (pos >= bytes.Length) return;
        bytes[pos] = v;
        if (bytes[0] == 0x03 && pos == 1)
        {
            // choice count: resize the target list
            var resized = new byte[2 + 2 * v];
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
        void Field(string label, int pos, bool dec = false) => Params.Add(new ParamVm(this) { Label = label, Position = pos, Decimal = dec, IsFlag = label == "flag" });
        void Jump(string label, int k) => Params.Add(new ParamVm(this) { Label = label, JumpIndex = k, Choices = Owner.JumpChoices(Op, k) });
        switch (b[0])
        {
            case 0x01: Jump("go to", 0); break;
            case 0x02: Field("flag", 1); Jump("first time", 0); Jump("later", 1); break;
            case 0x03: Field("choices", 1, dec: true); for (int k = 0; k < b[1]; k++) Jump($"#{k + 1}", k); break;
            case 0x04: case 0x05: Field("flag", 1); Jump("go to", 0); break;
            case 0x06: case 0x07: Field("flag", 1); break;
            case 0x3E:
                Params.Add(new ParamVm(this) { Label = "item", Position = 1, IsItem = true, Choices = Owner.ItemChoices });
                Field("qty", 2, dec: true);
                break;
            case 0x6C: Field("frames", 1, dec: true); break;
            default:
                for (int i = 1; i < b.Length; i++) Field($"byte {i}", i);
                break;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(LineText));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(SpeakerIndex));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(OpcodeHex));
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
    public IReadOnlyList<string> ItemChoices { get; private set; } = Array.Empty<string>();

    /// <summary>Raised by the Close button (only shown when the panel sits in its own window).</summary>
    public event Action? CloseRequested;
    public bool ShowCloseButton { set => CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public bool IsLoadedScript => Script != null;

    private static readonly (string Name, byte[] Bytes, bool Text)[] Templates =
    {
        ("Dialogue: narration", new byte[] { 0x0C }, true),
        ("Dialogue: actor 0 speaks", new byte[] { 0x88 }, true),
        ("Give item", new byte[] { 0x3E, 0x94, 0x01 }, false),
        ("Set story flag", new byte[] { 0x06, 0x00 }, false),
        ("Clear story flag", new byte[] { 0x07, 0x00 }, false),
        ("If flag is set, go to…", new byte[] { 0x04, 0x00, 0x00, 0x00 }, false),
        ("If flag is not set, go to…", new byte[] { 0x05, 0x00, 0x00, 0x00 }, false),
        ("Go to…", new byte[] { 0x01, 0x00, 0x00 }, false),
        ("Wait (frames)", new byte[] { 0x6C, 0x3C }, false),
        ("Learn spell", new byte[] { 0x3D, 0x00, 0x00 }, false),
        ("End", new byte[] { 0x00 }, false),
        ("Other command (edit its byte)", new byte[] { 0x84 }, false),
    };

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
        if (ItemChoices.Count == 0)
            ItemChoices = Enumerable.Range(0, 256).Select(i => $"{i:X2} {MapSetupScript.ItemName(rom, i)}").ToList();
        HeaderText.Text = title;
        Fill();
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

    /// <summary>Called after any edit: renumber lines and refresh descriptions / jump pickers.</summary>
    public void Changed(bool rebuild = true)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (rebuild)
                for (int i = 0; i < _lines.Count; i++) { _lines[i].Index = i; _lines[i].Refresh(); }
            StatusText.Text = Script.IsModified
                ? (Script.FitsInPlace() ? "Edited (same size: will be written in place)." : "Edited (size changed: the event will be moved when applied).")
                : "";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _refreshing = false; }
    }

    private void ShowInsertMenu(Button anchor, int insertAt)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        foreach (var t in Templates)
        {
            var item = new MenuItem { Header = t.Name };
            item.Click += (_, _) =>
            {
                var op = t.Text ? Script.NewText(t.Bytes[0], "New text", insertAt) : Script.NewCommand((byte[])t.Bytes.Clone(), insertAt);
                _lines.Insert(insertAt, new EventLineVm(this, op, insertAt));
                Changed();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
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
        if (!Script.IsModified) { StatusText.Text = "Nothing changed."; return; }
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
            string result = Script.Save(allowExpand);
            _owner.EventSaved($"Map {_mapId:X2} event {_event}: dialogue/commands");
            Script = Script.IsCodeView
                ? EventScript.LoadAt(_rom, _mapId, _event, Script.Start, Script.JumpBase)
                : EventScript.Load(_rom, _mapId, _event) ?? Script;
            Fill();
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

    /// <summary>Show where a story flag is used (if the host supports it).</summary>
    public void ShowFlag(int flag) => _owner.ShowFlag(flag);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0 || !ConfirmDiscard()) return;
        var (script, title) = _history.Pop();
        var fresh = script.IsCodeView
            ? EventScript.LoadAt(_rom, script.MapId, script.Event, script.Start, script.JumpBase)
            : EventScript.Load(_rom, script.MapId, script.Event) ?? script;
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
        Script = Script.IsCodeView
            ? EventScript.LoadAt(_rom, _mapId, _event, Script.Start, Script.JumpBase)
            : EventScript.Load(_rom, _mapId, _event) ?? Script;
        Fill();
        StatusText.Text = "Reverted to the event stored in the ROM.";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        CloseRequested?.Invoke();
    }
}
