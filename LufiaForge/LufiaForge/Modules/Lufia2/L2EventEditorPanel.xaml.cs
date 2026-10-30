using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LufiaForge.Core.Lufia2;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using DragEventArgs = System.Windows.DragEventArgs;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragDrop = System.Windows.DragDrop;
using Image = System.Windows.Controls.Image;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// The Lufia II event editor, laid out like Lufia 1's: the steps as cards edited in place on the left, the stage (the
/// map with its characters) on the right, the timeline underneath. Shown on the Events page, the Cutscenes page (stage
/// always on) and in the pop-up window the Maps page opens. Bound to an <see cref="L2ScriptEditor"/>.
/// </summary>
public partial class L2EventEditorPanel : UserControl
{
    private L2ScriptEditor? _editor;
    private bool _stageChosen, _timelineChosen, _cutsceneMode;
    private bool _pickStart;
    private (int X, int Y) _lastTile;
    private readonly Stack<(int Map, int Head, int Id)> _history = new();

    public L2EventEditorPanel()
    {
        InitializeComponent();
        HelpText.Text = DefaultHelp;
        StageSplitter.DragCompleted += (_, e) =>
        {
            if (Common.LayoutMemory.Moved(e)) Common.LayoutMemory.Set(StageSplitKey, Common.LayoutMemory.Format(LinesColumn.Width) + ";" + Common.LayoutMemory.Format(StageColumn.Width));
        };
        StageSplitter.MouseDoubleClick += (_, _) => { Common.LayoutMemory.Set(StageSplitKey, null); if (StageRoot.Visibility == Visibility.Visible) ShowStage(true); };
        TimelineSplitter.DragCompleted += (_, e) => { if (Common.LayoutMemory.Moved(e)) Common.LayoutMemory.Set("l2events.timeline", Common.LayoutMemory.Format(new GridLength(TimelineRow.ActualHeight))); };
        TimelineSplitter.MouseDoubleClick += (_, _) => { Common.LayoutMemory.Set("l2events.timeline", null); TimelineRow.Height = new GridLength(220); };
        // while it's on screen its edits are written by themselves (saving the project, Apply to ROM in the top bar)
        Loaded += (_, _) => { if (_editor != null) Main?.Register(_editor); };
        Unloaded += (_, _) =>
        {
            if (_editor == null) return;
            if (_editor.HasPendingEdits) _editor.WritePendingEdits();
            if (Common.PopOutHost.AnyPoppedOut(this)) return;
            Main?.Unregister(_editor);
        };
    }

    private static LufiaForge.ViewModels.MainViewModel? Main => Application.Current?.MainWindow?.DataContext as LufiaForge.ViewModels.MainViewModel;

    public L2ScriptEditor? Editor => _editor;

    public event Action? CloseRequested;
    public bool ShowCloseButton { set => CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    private const string DefaultHelp =
        "Dialogue: Enter = new line in the box (a box shows 4 lines of 28 letters; a wider or longer text gets a warning under it and is still written as typed). [Name] = the hero's name, [Pause 20] = wait 20 frames. " +
        "Commands: pick values from the lists, jump targets by step, ＋ inserts below, ✕ deletes, drag the grip to reorder. On the stage, click a character, then a tile: it walks, " +
        "appears or turns there. Story flags are shared by the whole game, so change them with care. Edits are written when you leave the event (or press Save).";

    /// <summary>Cutscene layout (the Cutscenes page): the stage is always shown and gets most of the room.</summary>
    public bool CutsceneMode
    {
        get => _cutsceneMode;
        set
        {
            _cutsceneMode = value;
            StageToggle.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            HelpText.Text = value
                ? "The steps on the left are the cutscene's timeline; click one to see the stage at that moment, ▶ Play to watch from there. ＋ adds a step below (Characters, Moves, " +
                  "Camera, Screen, Sound, Timing...). On the stage, click a character and then a tile: it walks, appears or turns there; right-click a tile for the camera and the " +
                  "party's start. Edits are written when you leave the cutscene (or press Save)."
                : DefaultHelp;
            if (_editor != null) _editor.CutsceneMode = value;
            if (value) { ShowStage(true); if (!_timelineChosen) ShowTimeline(true); }
        }
    }

    private string StageSplitKey => _cutsceneMode ? "l2cutscenes.stage" : "l2events.stage";

    /// <summary>Shows the editor's script (the panel follows every event it loads from now on).</summary>
    public void Bind(L2ScriptEditor editor)
    {
        if (ReferenceEquals(_editor, editor)) { Refresh(); return; }
        if (_editor != null)
        {
            _editor.ScrollRequested -= ScrollTo; _editor.LineHighlighted -= BringLineIntoView; _editor.OpenRequested -= OpenOther;
            _editor.PropertyChanged -= Editor_PropertyChanged;
            if (IsLoaded) Main?.Unregister(_editor);
        }
        _editor = editor;
        editor.CutsceneMode = _cutsceneMode;
        DataContext = editor;
        editor.ScrollRequested += ScrollTo; editor.LineHighlighted += BringLineIntoView; editor.OpenRequested += OpenOther;
        editor.PropertyChanged += Editor_PropertyChanged;
        Timeline.Bind(editor);
        if (IsLoaded) Main?.Register(editor);
        _history.Clear();
        BackButton.Visibility = Visibility.Collapsed;
        Refresh();
    }

    /// <summary>After the editor loaded another script: the stage comes on for one with moves, the bytes view follows.</summary>
    public void Refresh()
    {
        if (_editor == null) return;
        bool moves = _editor.Script?.Steps.Any(s => !s.IsText && s.Op is 0x2D or 0x2E or 0x2F or 0x30 or 0x7A or 0x98 or 0x99 or 0xA3 or 0x3A or 0xC4 or 0x41) == true;
        if (_cutsceneMode || StageToggle.IsChecked == true || (moves && !_stageChosen)) ShowStage(true);
        if (_cutsceneMode && !_timelineChosen) ShowTimeline(true);
        else if (TimelineCard.Visibility == Visibility.Visible) Timeline.Refresh();
        if (_editor.SelectedLine >= 0) _editor.HighlightLine(_editor.SelectedLine);
    }

    private void Editor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_editor == null) return;
        if (e.PropertyName == nameof(L2ScriptEditor.SelectedLine))
        {
            _editor.HighlightLine(_editor.SelectedLine);
            Timeline.SelectLine(_editor.SelectedLine);
        }
        else if (e.PropertyName == nameof(L2ScriptEditor.Title)) Refresh();
    }

    // ── the steps ──

    private void Line_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editor == null || sender is not FrameworkElement { DataContext: L2LineVm line }) return;
        if (_editor.SelectedLine != line.Index) _editor.SelectedLine = line.Index;
    }

    private void BringLineIntoView(int index)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LinesList.UpdateLayout();
            if (LinesList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement fe) fe.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2LineVm line }) _editor?.Move(line.Index, line.Index - 1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2LineVm line }) _editor?.Move(line.Index, line.Index + 1);
    }

    private Point _gripStart;
    private L2LineVm? _gripLine;

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2LineVm line }) { _gripLine = line; _gripStart = e.GetPosition(this); e.Handled = true; }
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_gripLine == null || e.LeftButton != MouseButtonState.Pressed) { _gripLine = null; return; }
        var p = e.GetPosition(this);
        if (Math.Abs(p.Y - _gripStart.Y) < 4 && Math.Abs(p.X - _gripStart.X) < 4) return;
        var line = _gripLine; _gripLine = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(L2LineVm), line), DragDropEffects.Move);
        foreach (var l in _editor?.Lines ?? Enumerable.Empty<L2LineVm>()) { l.DropAbove = false; l.DropBelow = false; }
    }

    private static bool DropsAbove(object sender, DragEventArgs e) =>
        sender is FrameworkElement fe && e.GetPosition(fe).Y < fe.ActualHeight / 2;

    private void Line_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(L2LineVm)) || sender is not FrameworkElement { DataContext: L2LineVm line }) { e.Effects = DragDropEffects.None; return; }
        bool above = DropsAbove(sender, e);
        line.DropAbove = above; line.DropBelow = !above;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Line_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2LineVm line }) { line.DropAbove = false; line.DropBelow = false; }
    }

    private void Line_Drop(object sender, DragEventArgs e)
    {
        if (_editor == null || sender is not FrameworkElement { DataContext: L2LineVm target } || e.Data.GetData(typeof(L2LineVm)) is not L2LineVm moved) return;
        target.DropAbove = false; target.DropBelow = false;
        int from = moved.Index, to = target.Index + (DropsAbove(sender, e) ? 0 : 1);
        if (to > from) to--;
        _editor.Move(from, to);
        e.Handled = true;
    }

    /// <summary>The "insert a step" menu: text first, then the commands by category (rarely used ones in their own sub-menu).</summary>
    private void ShowInsertMenu(FrameworkElement anchor, int at)
    {
        if (_editor == null || !_editor.CanEdit) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void AddText(string name, byte[] opener)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => _editor.AddText(opener, "...", 0x01, at);
            menu.Items.Add(item);
        }
        bool talk = _editor.Event?.Entry?.Head == L2EventBlocks.TalkList;
        if (talk) AddText("Dialogue: the person talked to says", new byte[] { 0x08 });
        AddText("Dialogue: Maxim says", new byte[] { 0x61 });
        AddText("Dialogue: a character of the map says", new byte[] { 0x13, (byte)(_editor.Characters.Keys.FirstOrDefault(k => k >= 0x50, 0x50)) });
        AddText("Dialogue: narration", new byte[] { 0x9E, 0x05 });
        var q = new MenuItem { Header = "Dialogue: a question (yes / no)" };
        q.Click += (_, _) => _editor.AddText(new byte[] { 0x10, 0x02, 0, 0, 0, 0 }, "Is that so?\nYes\nNo", 0x0B, at);
        menu.Items.Add(q);
        menu.Items.Add(new Separator());
        var offered = L2EventCommands.Definitions.Where(d => d.Template != null && !L2EventScript.TextOpeners.Contains(d.Template[0])).ToList();
        foreach (string cat in L2EventCommands.Categories)
        {
            var defs = cat == L2EventCommands.Special ? offered.Where(d => d.Rare || d.Category == cat).ToList() : offered.Where(d => d.Category == cat && !d.Rare).ToList();
            if (defs.Count == 0) continue;
            var sub = new MenuItem { Header = cat };
            foreach (var d in defs.OrderBy(d => cat == L2EventCommands.Special ? d.Name : ""))
            {
                var item = new MenuItem { Header = d.Name, ToolTip = (d.Help.Length > 0 ? d.Help : null) + (d.Waits.Length > 0 ? " The event waits here " + d.Waits + "." : "") };
                var def = d;
                item.Click += (_, _) => _editor.AddCommand(def, at);
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }
        menu.IsOpen = true;
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: L2LineVm line } b) ShowInsertMenu(b, line.Index + 1);
    }

    private void InsertAtStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) ShowInsertMenu(b, 0);
    }

    /// <summary>The "Add a step" button of a host page: below the picked step.</summary>
    public void ShowInsertMenuAt(FrameworkElement anchor) { if (_editor != null) ShowInsertMenu(anchor, _editor.InsertIndex); }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: L2LineVm line }) _editor?.Delete(line.Index);
    }

    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2FieldVm f }) f.Follow();
    }

    private void FlagUses_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2FieldVm f }) f.ShowFlagUses();
    }

    private void OpenScene_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2LineVm line }) _editor?.OpenScene(line);
    }

    /// <summary>Opens another event in place of this one (◀ Back comes back).</summary>
    private void OpenOther(int map, int head, int id)
    {
        if (_editor?.Rom == null || _editor.Main == null || _editor.Event?.Entry is not { } here) return;
        if (!_editor.ConfirmLeave()) return;
        var back = (_editor.Map, here.Head, here.Id);
        if (!_editor.Load(_editor.Rom, _editor.Main, map, head, id)) { _editor.Status = $"Map {map:X2} has no scene {id:X2}: make it there first (New cutscene, \"when another cutscene sends the party here\")."; return; }
        _history.Push(back);
        BackButton.Visibility = Visibility.Visible;
        Refresh();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Rom == null || _editor.Main == null || _history.Count == 0 || !_editor.ConfirmLeave()) return;
        var (map, head, id) = _history.Pop();
        _editor.Load(_editor.Rom, _editor.Main, map, head, id);
        BackButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    // ── views ──

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        bool bytes = ViewBytes.IsChecked == true;
        LinesCard.Visibility = bytes ? Visibility.Collapsed : Visibility.Visible;
        BytesCard.Visibility = bytes ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StageToggle_Click(object sender, RoutedEventArgs e) { _stageChosen = true; ShowStage(StageToggle.IsChecked == true); }

    private void TimelineToggle_Click(object sender, RoutedEventArgs e) { _timelineChosen = true; ShowTimeline(TimelineToggle.IsChecked == true); }

    public void ShowTimeline(bool show)
    {
        TimelineToggle.IsChecked = show;
        TimelineCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TimelineSplitRow.Height = new GridLength(show ? 7 : 0);
        TimelineRow.Height = show ? Common.LayoutMemory.Parse(Common.LayoutMemory.Get("l2events.timeline")) ?? new GridLength(220) : new GridLength(0);
        if (!show) return;
        if (StageRoot.Visibility != Visibility.Visible) ShowStage(true);
        Timeline.Refresh();
        if (_editor != null && _editor.SelectedLine >= 0) Timeline.SelectLine(_editor.SelectedLine);
    }

    public void ShowStage(bool show)
    {
        StageToggle.IsChecked = show;
        StageRoot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SplitterColumn.Width = new GridLength(show ? 6 : 0);
        StageColumn.Width = show ? new GridLength(_cutsceneMode ? 1.7 : 1.2, GridUnitType.Star) : new GridLength(0);
        if (show && Common.LayoutMemory.Get(StageSplitKey)?.Split(';') is { Length: 2 } split &&
            Common.LayoutMemory.Parse(split[0]) is { } lines && Common.LayoutMemory.Parse(split[1]) is { } stage)
        {
            LinesColumn.Width = lines; StageColumn.Width = stage;
        }
        else LinesColumn.Width = new GridLength(1, GridUnitType.Star);
        if (show)
        {
            if (Window.GetWindow(this) is L2EventEditorWindow w && w.Width < 1400 && w.WindowState == WindowState.Normal) w.Width = 1400;
            _editor?.ShowStage();
        }
    }

    // ── the stage ──

    private (int X, int Y) TileAt(MouseEventArgs e)
    {
        var p = e.GetPosition(Stage);
        return ((int)Math.Max(0, p.X / 16), (int)Math.Max(0, p.Y / 16));
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        var (x, y) = TileAt(e);
        Stage.ToolTip = $"Tile ({x}, {y})";
    }

    private void Stage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editor == null) return;
        Focus();
        var (x, y) = TileAt(e);
        _lastTile = (x, y);
        if (_pickStart) { _pickStart = false; _editor.SetPartyStart(x, y); return; }
        var p = e.GetPosition(Stage);
        if (_editor.StageClick(p.X, p.Y)) return;
        ShowActionMenu(x, y);
    }

    private void Stage_RightDown(object sender, MouseButtonEventArgs e)
    {
        var (x, y) = TileAt(e);
        _lastTile = (x, y);
        ShowActionMenu(x, y);
        e.Handled = true;
    }

    /// <summary>What can happen at a tile: the picked character (or any) walks / appears / turns; the camera moves; the party starts.</summary>
    private void ShowActionMenu(int x, int y)
    {
        if (_editor == null) return;
        var actions = _editor.ActionsAt(x, y);
        if (actions.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = Stage };
        menu.Items.Add(new MenuItem { Header = $"Tile ({x}, {y}), added after step {_editor.InsertIndex}", IsEnabled = false });
        var subs = new Dictionary<string, MenuItem>();
        foreach (var a in actions)
        {
            var mi = new MenuItem { Header = a.Header };
            var act = a.Act;
            mi.Click += (_, _) => act();
            if (a.Group.Length == 0) { if (subs.Count > 0 && !menu.Items.OfType<Separator>().Any()) menu.Items.Add(new Separator()); menu.Items.Add(mi); continue; }
            if (!subs.TryGetValue(a.Group, out var sub)) { sub = new MenuItem { Header = a.Group }; subs[a.Group] = sub; menu.Items.Add(sub); }
            sub.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void Stage_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_editor == null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var before = e.GetPosition(Stage);
        var inView = e.GetPosition(StageScroll);
        _editor.Zoom = Math.Clamp(_editor.Zoom + (e.Delta > 0 ? 0.5 : -0.5), 0.5, 6);
        StageScroll.UpdateLayout();
        StageScroll.ScrollToHorizontalOffset(before.X * _editor.Zoom - inView.X);
        StageScroll.ScrollToVerticalOffset(before.Y * _editor.Zoom - inView.Y);
        e.Handled = true;
    }

    private void PickStart_Click(object sender, RoutedEventArgs e)
    {
        _pickStart = true;
        if (_editor != null) _editor.StageInfo = "Click the tile where the party stands when the event begins.";
    }

    /// <summary>Keeps the camera's view (a point of the map) in sight.</summary>
    private void ScrollTo(double x, double y)
    {
        if (_editor == null || StageRoot.Visibility != Visibility.Visible) return;
        double z = _editor.Zoom;
        double vx = x * z - StageScroll.ViewportWidth / 2, vy = y * z - StageScroll.ViewportHeight / 2;
        double left = StageScroll.HorizontalOffset, top = StageScroll.VerticalOffset;
        if (x * z < left + StageScroll.ViewportWidth * 0.2 || x * z > left + StageScroll.ViewportWidth * 0.8) StageScroll.ScrollToHorizontalOffset(Math.Max(0, vx));
        if (y * z < top + StageScroll.ViewportHeight * 0.2 || y * z > top + StageScroll.ViewportHeight * 0.8) StageScroll.ScrollToVerticalOffset(Math.Max(0, vy));
    }

    private void AddCast_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Rom == null) return;
        int sprite = PickSprite(_editor, Window.GetWindow(this));
        if (sprite < 0) return;
        _editor.AddCastMember(sprite, _lastTile.X, _lastTile.Y);
    }

    /// <summary>A small window to pick a map sprite (with its picture); -1 when cancelled.</summary>
    public static int PickSprite(L2ScriptEditor editor, Window? owner)
    {
        var rom = editor.Rom!;
        var w = L2Ui.Window("Cutscene character", 420, 560);
        w.Owner = owner;
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(L2Ui.Label("Pick the sprite of the new character. It is made with the map and hidden until a step makes it appear on the tile you clicked last (or (0, 0)).", true));
        DockPanel.SetDock(root.Children[0], Dock.Top);
        var list = new ListBox { Margin = new Thickness(0, 8, 0, 8) };
        if (L2Ui.S("GdRecordList") is { } ls) list.Style = ls;
        int count = L2Sprites.Count(rom);
        for (int s = 0; s < count; s++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = s };
            var img = new Image { Source = editor.SpritePicture(s), Width = 24, Height = 32, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            row.Children.Add(img);
            row.Children.Add(new TextBlock { Text = $"{s:X2}  {(s < 7 ? L2Rom.Heroes[s] : L2Sprites.Name(s))}", VerticalAlignment = VerticalAlignment.Center, Foreground = L2Ui.B("TextPrimaryBrush") });
            list.Items.Add(row);
        }
        list.SelectedIndex = Math.Min(0x0C, count - 1);
        int picked = -1;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(L2Ui.Button("＋ Add the character", true, (_, _) => { picked = list.SelectedItem is StackPanel { Tag: int t } ? t : -1; w.DialogResult = true; }));
        var cancel = L2Ui.Button("Cancel", false, (_, _) => w.DialogResult = false); cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(list);
        list.MouseDoubleClick += (_, _) => { picked = list.SelectedItem is StackPanel { Tag: int t } ? t : -1; w.DialogResult = true; };
        w.Content = root;
        return w.ShowDialog() == true ? picked : -1;
    }

    // ── keys, closing ──

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_editor != null)
        {
            if (e.Key == Key.Escape && (_editor.PickedActor >= 0 || _pickStart)) { _pickStart = false; _editor.LetGo(); e.Handled = true; }
            else if (Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                if (e.Key == Key.Z && !shift) { _editor.Undo(); e.Handled = true; }
                else if (e.Key == Key.Y || e.Key == Key.Z && shift) { _editor.Redo(); e.Handled = true; }
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_editor != null && !_editor.ConfirmLeave()) return;
        CloseRequested?.Invoke();
    }
}
