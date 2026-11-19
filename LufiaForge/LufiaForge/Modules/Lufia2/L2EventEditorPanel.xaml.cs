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

// the Lufia II event editor, laid out like the Lufia 1 one: step cards edited in place on the left, the stage (the
// map with its characters) on the right, the timeline under it. used on the Events page, the Cutscenes page (stage
// always on) and in the pop-up window the Maps page opens. bound to an L2ScriptEditor
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
        // while it's on screen its edits get written along with everything else (saving the project, Apply to ROM in the top bar)
        Loaded += (_, _) =>
        {
            if (_editor != null) Main?.Register(_editor);
            CastExpander.IsExpanded = Common.LayoutMemory.Get("l2events.cast") == "1";   // (might have been opened or shut on another page meanwhile)
        };
        // a sound renamed in Settings: the cards show the new name (also if it got renamed while this was off screen)
        Loaded += (_, _) =>
        {
            Core.Audio.SoundNames.Changed -= SoundNamesChanged; Core.Audio.SoundNames.Changed += SoundNamesChanged;
            if (_soundNamesVersion != Core.Audio.SoundNames.Version) SoundNamesChanged();
        };
        Unloaded += (_, _) =>
        {
            Core.Audio.SoundNames.Changed -= SoundNamesChanged;
            Common.SoundPreview.StopWhenGone(this);
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

    private const string PuzzleHelp =
        "Pick a step to see the places it uses on the stage: blue is the spot or area, orange is the piece of the map a tile change copies from. " +
        "Show bytes gives the raw commands; pick a step and change its bytes under the list (it must keep its size).";

    // cutscene layout (the Cutscenes page): the stage is always shown and gets most of the room
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

    // shows the editor's script (from now on the panel follows every event the editor loads)
    public void Bind(L2ScriptEditor editor)
    {
        if (ReferenceEquals(_editor, editor)) { Refresh(); return; }
        if (_editor != null)
        {
            _editor.ScrollRequested -= ScrollTo; _editor.LineHighlighted -= BringLineIntoView; _editor.OpenRequested -= OpenOther;
            _editor.PropertyChanged -= Editor_PropertyChanged; _editor.PasteMatchRequested -= AskPasteMatch;
            if (IsLoaded) Main?.Unregister(_editor);
        }
        _editor = editor;
        editor.CutsceneMode = _cutsceneMode;
        DataContext = editor;
        editor.ScrollRequested += ScrollTo; editor.LineHighlighted += BringLineIntoView; editor.OpenRequested += OpenOther;
        editor.PropertyChanged += Editor_PropertyChanged; editor.PasteMatchRequested += AskPasteMatch;
        Timeline.Bind(editor);
        if (IsLoaded) Main?.Register(editor);
        _history.Clear();
        BackButton.Visibility = Visibility.Collapsed;
        Refresh();
    }

    // after the editor loads another script: the stage comes on if the script has moves, the bytes view follows
    public void Refresh()
    {
        if (_editor == null) return;
        bool hasMoves = _editor.Script?.Steps.Any(s => !s.IsText && s.Op is 0x2D or 0x2E or 0x2F or 0x30 or 0x7A or 0x98 or 0x99 or 0xA3 or 0x3A or 0xC4 or 0x41) == true;
        // a puzzle script's steps mark their places on the stage
        if (!_cutsceneMode) HelpText.Text = _editor.IsPuzzle ? PuzzleHelp : DefaultHelp;
        if (_cutsceneMode || StageToggle.IsChecked == true || ((hasMoves || _editor.IsPuzzle) && !_stageChosen)) ShowStage(true);
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
        else if (e.PropertyName is nameof(L2ScriptEditor.Title) or nameof(L2ScriptEditor.IsPuzzle)) Refresh();
    }

    // the steps

    private void Line_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editor == null || sender is not FrameworkElement { DataContext: L2LineVm line }) return;
        // shift+click marks the steps from the picked one to this one, for Copy
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _editor.SelectedLine >= 0) { _editor.MarkRange(_editor.SelectedLine, line.Index); return; }
        _editor.ClearMark();
        if (_editor.SelectedLine != line.Index) _editor.SelectedLine = line.Index;
    }

    private void BringLineIntoView(int index)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LinesList.UpdateLayout();
            if (LinesList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement card) card.BringIntoView();
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
        var position = e.GetPosition(this);
        if (Math.Abs(position.Y - _gripStart.Y) < 4 && Math.Abs(position.X - _gripStart.X) < 4) return;
        var line = _gripLine; _gripLine = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(L2LineVm), line), DragDropEffects.Move);
        foreach (var step in _editor?.Lines ?? Enumerable.Empty<L2LineVm>()) { step.DropAbove = false; step.DropBelow = false; }
    }

    private static bool DropsAbove(object sender, DragEventArgs e) =>
        sender is FrameworkElement card && e.GetPosition(card).Y < card.ActualHeight / 2;

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

    // the "insert a step" menu: text first, then the commands by category (the rarely used ones get their own sub-menu)
    private void ShowInsertMenu(FrameworkElement anchor, int insertAt)
    {
        if (_editor == null || !_editor.CanEdit) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void AddText(string name, byte[] opener)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => _editor.AddText(opener, "...", 0x01, insertAt);
            menu.Items.Add(item);
        }
        bool isTalkEvent = _editor.Event?.Entry?.Head == L2EventBlocks.TalkList;
        if (isTalkEvent) AddText("Dialogue: the person talked to says", new byte[] { 0x08 });
        AddText("Dialogue: Maxim says", new byte[] { 0x61 });
        AddText("Dialogue: a character of the map says", new byte[] { 0x13, (byte)(_editor.Characters.Keys.FirstOrDefault(k => k >= 0x50, 0x50)) });
        AddText("Dialogue: narration", new byte[] { 0x9E, 0x05 });
        var question = new MenuItem { Header = "Dialogue: a question (yes / no)" };
        question.Click += (_, _) => _editor.AddText(new byte[] { 0x10, 0x02, 0, 0, 0, 0 }, "Is that so?\nYes\nNo", 0x0B, insertAt);
        menu.Items.Add(question);
        menu.Items.Add(new Separator());
        var ready = new MenuItem { Header = "Ready-made" };
        var sparkles = new MenuItem { Header = "✨ Sparkles on a spot (they turn, then go)", ToolTip = "The game's warp sparkles (Shrine of Daos 1F): 10 sparkles appear 120 pixels out, turn, close in and go, and the event waits for them. Pick the spot on the stage." };
        sparkles.Click += (_, _) =>
        {
            int linesBefore = _editor.Lines.Count;
            _editor.AddRing(new byte[] { 0x8D, 0x00, 0x01, 0x0A, 0x78, 0x40, 0x00 }, insertAt);
            // the ring's point gets picked on the stage
            if (_editor.Lines.Count > linesBefore && insertAt >= 0 && insertAt < _editor.Lines.Count && _editor.Lines[insertAt].Fields.FirstOrDefault(f => f.IsPlace) is { } placeField)
            {
                if (StageRoot.Visibility != Visibility.Visible) ShowStage(true);
                placeField.PickPlace();
            }
        };
        ready.Items.Add(sparkles);
        menu.Items.Add(ready);
        menu.Items.Add(new Separator());
        var offered = L2EventCommands.Definitions.Where(d => d.Template != null && !L2EventScript.TextOpeners.Contains(d.Template[0])).ToList();
        foreach (string category in L2EventCommands.Categories)
        {
            var commands = category == L2EventCommands.Special ? offered.Where(d => d.Rare || d.Category == category).ToList() : offered.Where(d => d.Category == category && !d.Rare).ToList();
            if (commands.Count == 0) continue;
            var sub = new MenuItem { Header = category };
            foreach (var d in commands.OrderBy(d => category == L2EventCommands.Special ? d.Name : ""))
            {
                var item = new MenuItem { Header = d.Name, ToolTip = (d.Help.Length > 0 ? d.Help : null) + (d.Waits.Length > 0 ? " The event waits here " + d.Waits + "." : "") };
                var def = d;
                item.Click += (_, _) => _editor.AddCommand(def, insertAt);
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }
        menu.IsOpen = true;
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: L2LineVm line } button) ShowInsertMenu(button, line.Index + 1);
    }

    private void InsertAtStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) ShowInsertMenu(button, 0);
    }

    // the "Add a step" button of a host page: inserts below the picked step
    public void ShowInsertMenuAt(FrameworkElement anchor) { if (_editor != null) ShowInsertMenu(anchor, _editor.InsertIndex); }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: L2LineVm line }) _editor?.Delete(line.Index);
    }

    private void CastLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2CastLink link }) _editor?.GoTo(link);
    }

    private void CastExpander_Changed(object sender, RoutedEventArgs e) =>
        Common.LayoutMemory.Set("l2events.cast", CastExpander.IsExpanded ? "1" : null);

    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2FieldVm field }) field.Follow();
    }

    private void FlagUses_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2FieldVm field }) field.ShowFlagUses();
    }

    private int _soundNamesVersion = Core.Audio.SoundNames.Version;
    private void SoundNamesChanged() { _soundNamesVersion = Core.Audio.SoundNames.Version; Dispatcher.BeginInvoke(() => _editor?.RefreshSoundNames()); }

    private void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: L2FieldVm field } button || _editor == null || field.Raw < 0) return;
        string note = Common.SoundPreview.Toggle(button, _editor.Rom, lufia2: true, song: field.IsSong, field.Raw);
        if (note.Length > 0) _editor.Status = note;
    }

    private void PickArrival_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: L2FieldVm field } || _editor?.Rom is not { } rom) return;
        int map = Math.Max(2, field.StepMap);
        int point = field.Raw;
        var pointRecord = L2MapInfo.Records(rom, map, L2MapInfo.Points, 3).Select(r => r.Bytes).FirstOrDefault(b => b[0] == point);
        var window = new L2PickPlaceWindow(rom, map, pointRecord?[1] ?? 0, pointRecord?[2] ?? 0, m => L2Maps.Name(rom, m)) { Owner = Window.GetWindow(this), Title = "Where the party arrives - Lufia Forge" };
        if (window.ShowDialog() != true) return;
        if (window.Map < 2) { _editor.StageInfo = "\"Go to another map\" arrives at a point of a town or dungeon, not the world map."; return; }
        _editor.PickArrival(field, window.Map, window.X, window.Y);
    }

    private void HeroFix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: L2LineVm line } || !line.HasHeroFix || _editor == null) return;
        if (line.HeroFix == -2) _editor.RingLikeGame(line.Index);
        else if (line.HeroFix == 0) _editor.LeaderVanishToStandIn();
        else _editor.HeroToCharacter(line.HeroFix);
    }

    private void PickPlace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: L2FieldVm field } || _editor == null) return;
        if (StageRoot.Visibility != Visibility.Visible) ShowStage(true);
        field.PickPlace();
    }

    private void EditChange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: L2FieldVm field } || _editor == null) return;
        if (StageRoot.Visibility != Visibility.Visible) ShowStage(true);
        field.EditChange();
    }

    private void ChangePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Rom == null) return;
        var presets = L2Presets.Load().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (presets.Count == 0) { _editor.ChangeInfo = "You have no presets yet: keep one on the Maps page (drag an area, then Pieces and presets...)."; return; }
        var window = L2Ui.Window("Use a preset", 460, 520);
        window.Owner = Window.GetWindow(this);
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = L2Ui.Label("Pick the preset the place changes into. It is kept on a part of the map you click next (somewhere the player never sees).", true);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var list = new ListBox { Margin = new Thickness(0, 8, 0, 8) };
        if (L2Ui.S("GdRecordList") is { } listStyle) list.Style = listStyle;
        foreach (var preset in presets) list.Items.Add(new ListBoxItem { Content = $"{preset.Name}  ({preset.W} x {preset.H})", Tag = preset });
        list.SelectedIndex = 0;
        L2Presets.Preset? picked = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(L2Ui.Button("Use it", true, (_, _) => { picked = (list.SelectedItem as ListBoxItem)?.Tag as L2Presets.Preset; window.DialogResult = true; }));
        var cancel = L2Ui.Button("Cancel", false, (_, _) => window.DialogResult = false); cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(list);
        list.MouseDoubleClick += (_, _) => { picked = (list.SelectedItem as ListBoxItem)?.Tag as L2Presets.Preset; window.DialogResult = true; };
        window.Content = root;
        if (window.ShowDialog() != true || picked == null) return;
        if (_editor.ChangeWithPreset(picked) is { } why) _editor.ChangeInfo = why;
    }

    private void EditPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: L2FieldVm field }) return;
        if (StageRoot.Visibility != Visibility.Visible) ShowStage(true);
        field.EditPath();
    }

    private void OpenScene_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2LineVm line }) _editor?.OpenScene(line);
    }

    // opens another event in place of this one (Back comes back)
    private void OpenOther(int map, int head, int id)
    {
        // (the start-up script is list -1)
        if (_editor?.Rom == null || _editor.Main == null || _editor.Event is not { PuzzleOps: null } here || here.Entry == null && !here.IsInit) return;
        if (!_editor.ConfirmLeave()) return;
        var back = (_editor.Map, here.Entry?.Head ?? -1, here.Entry?.Id ?? -1);
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

    // views

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
            if (Window.GetWindow(this) is L2EventEditorWindow window && window.Width < 1400 && window.WindowState == WindowState.Normal) window.Width = 1400;
            _editor?.ShowStage();
        }
    }

    // the stage

    private (int X, int Y) TileAt(MouseEventArgs e)
    {
        var position = e.GetPosition(Stage);
        return ((int)Math.Max(0, position.X / 16), (int)Math.Max(0, position.Y / 16));
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        var (x, y) = TileAt(e);
        Stage.ToolTip = $"Tile ({x}, {y})";
        if (_editor?.IsChangingMap == true) _editor.ChangeMove(x, y, e.LeftButton == MouseButtonState.Pressed);
        if (_editor?.IsPickingPlace == true) _editor.PlaceMove(x, y, e.LeftButton == MouseButtonState.Pressed);
    }

    private void Stage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Stage.IsMouseCaptured) Stage.ReleaseMouseCapture();
        var (x, y) = TileAt(e);
        if (_editor?.IsPickingPlace == true) { _editor.PlaceUp(x, y); return; }
        if (_editor?.IsChangingMap != true) return;
        _editor.ChangeUp(x, y);
    }

    private void Stage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editor == null) return;
        Focus();
        var (x, y) = TileAt(e);
        _lastTile = (x, y);
        if (_pickStart) { _pickStart = false; _editor.SetPartyStart(x, y); return; }
        if (_placeSprite >= 0) { int sprite = _placeSprite; _placeSprite = -1; _editor.AddCharacterHere(sprite, x, y); return; }
        if (_placeStory is var (storyId, storySprite)) { _placeStory = null; _editor.SendStoryHere(storyId, storySprite, x, y); return; }
        if (_editor.IsChangingMap) { _editor.ChangeDown(x, y); Stage.CaptureMouse(); return; }
        if (_editor.IsPickingPlace) { _editor.PlaceDown(x, y); Stage.CaptureMouse(); return; }
        if (_editor.IsDrawingPath) { _editor.PathClick(x, y); return; }
        var position = e.GetPosition(Stage);
        if (_editor.StageClick(position.X, position.Y)) return;
        ShowActionMenu(x, y);
    }

    private void Stage_RightDown(object sender, MouseButtonEventArgs e)
    {
        var (x, y) = TileAt(e);
        _lastTile = (x, y);
        e.Handled = true;
        if (_editor?.IsDrawingPath == true) { _editor.PathUndoCorner(); return; }
        if (_editor?.IsChangingMap == true) { _editor.ChangeCancel(); return; }
        if (_editor?.IsPickingPlace == true) { _editor.PlaceCancel(); return; }
        if (_placeSprite >= 0) { _placeSprite = -1; if (_editor != null) _editor.StageInfo = "No character added."; return; }
        if (_placeStory != null) { _placeStory = null; if (_editor != null) _editor.StageInfo = "No story character placed."; return; }
        ShowActionMenu(x, y);
    }

    // what can happen at a tile: the picked character (or any) walks / appears / turns there, the camera moves there, the party starts there
    private void ShowActionMenu(int x, int y)
    {
        if (_editor == null) return;
        var actions = _editor.ActionsAt(x, y);
        if (actions.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = Stage };
        menu.Items.Add(new MenuItem { Header = $"Tile ({x}, {y}), added after step {_editor.InsertIndex}", IsEnabled = false });
        var subMenus = new Dictionary<string, MenuItem>();
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = action.Header };
            var act = action.Act;
            item.Click += (_, _) => act();
            if (action.Group.Length == 0) { if (subMenus.Count > 0 && !menu.Items.OfType<Separator>().Any()) menu.Items.Add(new Separator()); menu.Items.Add(item); continue; }
            if (!subMenus.TryGetValue(action.Group, out var sub)) { sub = new MenuItem { Header = action.Group }; subMenus[action.Group] = sub; menu.Items.Add(sub); }
            sub.Items.Add(item);
        }
        if (_editor.CanEdit)
        {
            var change = new MenuItem { Header = "🧱 Change the map (copy tiles from another place)…", ToolTip = "Drag over the place that changes, then click where its new look is, or use a preset" };
            change.Click += (_, _) => _editor.StartChangeMap();
            menu.Items.Add(change);
            var sparkle = new MenuItem { Header = "✨ Sparkles here (turn on this spot, then go)", ToolTip = "The Sinistrals' warp: a ring of sparkles appears on this tile, turns, shrinks away; the event waits for it" };
            sparkle.Click += (_, _) => _editor.AddSparkles(x, y);
            menu.Items.Add(sparkle);
            var add = new MenuItem { Header = "＋ A new character appears here…", ToolTip = "Pick its sprite: it is made with the map (hidden) and appears on this tile at this step" };
            add.Click += (_, _) => { int sprite = PickSprite(_editor, Window.GetWindow(this), "New character", "＋ Add the character"); if (sprite >= 0) _editor.AddCharacterHere(sprite, x, y); };
            menu.Items.Add(add);
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

    // keeps the camera's view (a point of the map) in sight
    private void ScrollTo(double x, double y)
    {
        if (_editor == null || StageRoot.Visibility != Visibility.Visible) return;
        double zoom = _editor.Zoom;
        double centeredX = x * zoom - StageScroll.ViewportWidth / 2, centeredY = y * zoom - StageScroll.ViewportHeight / 2;
        double left = StageScroll.HorizontalOffset, top = StageScroll.VerticalOffset;
        if (x * zoom < left + StageScroll.ViewportWidth * 0.2 || x * zoom > left + StageScroll.ViewportWidth * 0.8) StageScroll.ScrollToHorizontalOffset(Math.Max(0, centeredX));
        if (y * zoom < top + StageScroll.ViewportHeight * 0.2 || y * zoom > top + StageScroll.ViewportHeight * 0.8) StageScroll.ScrollToVerticalOffset(Math.Max(0, centeredY));
    }

    // a sprite to place with the next click on the stage (-1 = none)
    private int _placeSprite = -1;

    private void AddCast_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Rom == null) return;
        int sprite = PickSprite(_editor, Window.GetWindow(this), "New character", "Next: click the tile on the stage");
        if (sprite < 0) return;
        _placeSprite = sprite;
        if (StageRoot.Visibility != Visibility.Visible) { _stageChosen = true; StageToggle.IsChecked = true; ShowStage(true); }
        _editor.StageInfo = $"Click the tile on the stage where the new character ({L2Sprites.Name(sprite)}) appears (right-click or Esc: no character).";
    }

    private void CastWhen_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Rom == null || sender is not FrameworkElement { DataContext: L2CastRowVm row }) return;
        var window = L2Ui.Window($"{row.Name.Trim()}: when it is on the map", 520, 300);
        window.Owner = Window.GetWindow(this);
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(L2Ui.Label("The start-up script makes the map's characters as the map loads. Pick when it makes this one " +
                                     "(two characters can share a place: one while a flag is off, the other once it is on).", true));
        var always = new System.Windows.Controls.RadioButton { Content = "Always", Margin = new Thickness(0, 10, 0, 4), Foreground = L2Ui.B("TextPrimaryBrush"), IsChecked = true };
        var whileOn = new System.Windows.Controls.RadioButton { Content = "Only while this story flag is on", Margin = new Thickness(0, 4, 0, 4), Foreground = L2Ui.B("TextPrimaryBrush") };
        var whileOff = new System.Windows.Controls.RadioButton { Content = "Only while this story flag is off", Margin = new Thickness(0, 4, 0, 4), Foreground = L2Ui.B("TextPrimaryBrush") };
        var flags = new System.Windows.Controls.ComboBox { ItemsSource = _editor.FlagChoices, DisplayMemberPath = "Label", Margin = new Thickness(20, 4, 0, 4), MinWidth = 380 };
        if (L2Ui.S("GdCombo") is { } comboStyle) flags.Style = comboStyle;
        flags.SelectedIndex = 0;
        root.Children.Add(always); root.Children.Add(whileOn); root.Children.Add(whileOff); root.Children.Add(flags);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        bool ok = false;
        buttons.Children.Add(L2Ui.Button("Set", true, (_, _) => { ok = true; window.DialogResult = true; }));
        var cancel = L2Ui.Button("Cancel", false, (_, _) => window.DialogResult = false); cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        window.Content = root;
        window.ShowDialog();
        if (!ok) return;
        int flag = always.IsChecked == true ? -1 : (flags.SelectedItem as L2Option)?.Value ?? 0;
        _editor.SetWhen(row.Id, flag, whileOn.IsChecked == true);
    }

    // a story character to put on the next tile clicked on the stage: (id, sprite), id -1 = a new one
    private (int Id, int Sprite)? _placeStory;

    private void AddStory_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Rom is not { } rom) return;
        var window = L2Ui.Window("Story character here", 520, 260);
        window.Owner = Window.GetWindow(this);
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(L2Ui.Label("Pick the story character. Next, click the tile on the stage: from the picked step on it stands there " +
                                     "(from the next time this map loads; the game remembers where each story character is).", true));
        var box = new System.Windows.Controls.ComboBox { DisplayMemberPath = "Label", Margin = new Thickness(0, 10, 0, 4), MinWidth = 440 };
        if (L2Ui.S("GdCombo") is { } comboStyle) box.Style = comboStyle;
        var items = new List<L2Option> { new(-1, "＋ A new story character (pick its sprite next)") };
        for (int id = L2Persistent.First; id <= L2Persistent.LastUsable; id++)
            if (L2Persistent.Exists(rom, id)) items.Add(new L2Option(id, $"{id:X2}  {L2EventCommands.Cap(_editor.StoryLabel(id))}"));
        box.ItemsSource = items;
        box.SelectedIndex = 0;
        root.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        bool ok = false;
        buttons.Children.Add(L2Ui.Button("Next", true, (_, _) => { ok = true; window.DialogResult = true; }));
        var cancel = L2Ui.Button("Cancel", false, (_, _) => window.DialogResult = false); cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        window.Content = root;
        window.ShowDialog();
        if (!ok || box.SelectedItem is not L2Option pick) return;
        int sprite = -1;
        if (pick.Value < 0)
        {
            sprite = PickSprite(_editor, Window.GetWindow(this), "New story character", "Use this sprite",
                "Pick the new story character's sprite (one of the game's: a story character goes from map to map).", gameOnly: true);
            if (sprite < 0) return;
        }
        _placeStory = (pick.Value, sprite);
        if (StageRoot.Visibility != Visibility.Visible) { _stageChosen = true; StageToggle.IsChecked = true; ShowStage(true); }
        _editor.StageInfo = "Click the tile on the stage where it stands (right-click or Esc: none).";
    }

    private void CastSendAway_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: L2CastRowVm row }) _editor?.SendAway(row.Id);
    }

    private void CastStory_Click(object sender, RoutedEventArgs e)
    {
        if (_editor == null || sender is not FrameworkElement { DataContext: L2CastRowVm row }) return;
        if (!_editor.RequestStoryChar(row.Id))
            _editor.StageInfo = $"The Events page's Story characters tab shows story character {row.Id:X2}: its name, sprite, new-game place and the events that move it.";
    }

    private void CastMakeStory_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Rom is not { } rom || sender is not FrameworkElement { DataContext: L2CastRowVm row }) return;
        if (L2Persistent.WhyNotStory(rom, _editor.Map, row.Id) is { } why) { _editor.StageInfo = "Can't: " + why; return; }
        if (System.Windows.MessageBox.Show(Window.GetWindow(this),
                $"Make {row.Name.Trim()} a story character?\n\nIt gets a free story character number. A new game puts it here, on its place, and the game " +
                "remembers where it is from then on: events can send it to another map or away, and it stays there. The start-up script no longer makes it, " +
                "and this map's events (its talk event too) use the new number.\n\nEdit > Undo takes it back.",
                "Story character", System.Windows.MessageBoxButton.OKCancel) != System.Windows.MessageBoxResult.OK) return;
        _editor.MakeStoryCharacter(row.Id);
    }

    private void PickStoryPlace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: L2FieldVm field } || _editor?.Rom is not { } rom || field.Step.Bytes.Length < 4) return;
        int map = field.Step.Bytes[2] < L2Maps.Count(rom) && !L2Persistent.IsAway(field.Step.Bytes[2], field.Step.Bytes[3]) ? field.Step.Bytes[2] : Math.Max(2, _editor.Map);
        var characterRecord = L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).Select(r => r.Bytes).FirstOrDefault(b => b[0] == field.Step.Bytes[3]);
        var window = new L2PickPlaceWindow(rom, map, characterRecord?[1] ?? 0, characterRecord?[2] ?? 0, m => L2Maps.Name(rom, m)) { Owner = Window.GetWindow(this), Title = "Where the story character stands - Lufia Forge" };
        if (window.ShowDialog() != true) return;
        _editor.PickStoryPlace(field, window.Map, window.X, window.Y);
    }

    private void CastSprite_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Rom == null || sender is not FrameworkElement { DataContext: L2CastRowVm row }) return;
        int sprite = PickSprite(_editor, Window.GetWindow(this), $"{row.Name.Trim()}: sprite in this event", "Use this sprite",
            "Pick the sprite it has in this event. A \"Change a character's sprite\" step goes in before the first step that uses it (after the one that makes it appear).");
        if (sprite >= 0) _editor.ChangeSpriteInEvent(row.Id, sprite);
    }

    // pasting steps from another map: a window to match up each character, point, path and area they name
    private bool AskPasteMatch(string intro, List<L2PasteRef> refs)
    {
        var window = L2Ui.Window("Paste steps", 640, Math.Min(760, 220 + refs.Count * 44));
        window.Owner = Window.GetWindow(this);
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = L2Ui.Label(intro + " (Points and characters are this map's; paths and areas can be copied over.)", true);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bool ok = false;
        var combos = new List<(L2PasteRef Ref, System.Windows.Controls.ComboBox Box)>();
        buttons.Children.Add(L2Ui.Button("Paste", true, (_, _) =>
        {
            foreach (var (pasteRef, box) in combos) if (box.SelectedItem is L2Option option) pasteRef.To = option.Value;
            ok = true; window.DialogResult = true;
        }));
        var cancel = L2Ui.Button("Cancel", false, (_, _) => window.DialogResult = false); cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var pasteRef in refs)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var label = L2Ui.Label(pasteRef.Label); label.Width = 260; label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            var box = new System.Windows.Controls.ComboBox { ItemsSource = pasteRef.Choices, DisplayMemberPath = "Label", MinWidth = 300 };
            if (L2Ui.S("GdCombo") is { } comboStyle) box.Style = comboStyle;
            box.SelectedItem = pasteRef.Choices.FirstOrDefault(o => o.Value == pasteRef.To) ?? pasteRef.Choices.FirstOrDefault();
            row.Children.Add(box);
            combos.Add((pasteRef, box));
            list.Children.Add(row);
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = root;
        window.ShowDialog();
        return ok;
    }

    // a small window to pick a map sprite (with its picture), -1 when cancelled
    public static int PickSprite(L2ScriptEditor editor, Window? owner, string title = "New character", string button = "＋ Add the character", string? hint = null,
        bool gameOnly = false)
    {
        var rom = editor.Rom!;
        var window = L2Ui.Window(title, 420, 560);
        window.Owner = owner;
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(L2Ui.Label(hint ?? "Pick the sprite of the new character. It is made with the map, hidden until a step makes it appear.", true));
        DockPanel.SetDock(root.Children[0], Dock.Top);
        var list = new ListBox { Margin = new Thickness(0, 8, 0, 8) };
        if (L2Ui.S("GdRecordList") is { } listStyle) list.Style = listStyle;
        int count = gameOnly ? L2Sprites.GameCount : L2Sprites.Count(rom);
        for (int s = 0; s < count; s++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = s };
            var picture = new Image { Source = editor.SpritePicture(s), Width = 24, Height = 32, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
            row.Children.Add(picture);
            row.Children.Add(new TextBlock { Text = $"{s:X2}  {(s < 7 ? L2Rom.Heroes[s] : L2Sprites.Name(s))}", VerticalAlignment = VerticalAlignment.Center, Foreground = L2Ui.B("TextPrimaryBrush") });
            list.Items.Add(row);
        }
        list.SelectedIndex = Math.Min(0x0C, count - 1);
        int picked = -1;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(L2Ui.Button(button, true, (_, _) => { picked = list.SelectedItem is StackPanel { Tag: int sprite } ? sprite : -1; window.DialogResult = true; }));
        var cancel = L2Ui.Button("Cancel", false, (_, _) => window.DialogResult = false); cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(list);
        list.MouseDoubleClick += (_, _) => { picked = list.SelectedItem is StackPanel { Tag: int sprite } ? sprite : -1; window.DialogResult = true; };
        window.Content = root;
        return window.ShowDialog() == true ? picked : -1;
    }

    // keys, closing

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_editor != null)
        {
            if (e.Key == Key.Escape && _editor.IsDrawingPath) { _editor.PathCancel(); e.Handled = true; }
            else if (e.Key == Key.Escape && _editor.IsChangingMap) { _editor.ChangeCancel(); e.Handled = true; }
            else if (e.Key == Key.Escape && _editor.IsPickingPlace) { _editor.PlaceCancel(); e.Handled = true; }
            else if (e.Key == Key.Escape && _placeSprite >= 0) { _placeSprite = -1; _editor.StageInfo = "No character added."; e.Handled = true; }
            else if (e.Key == Key.Escape && _placeStory != null) { _placeStory = null; _editor.StageInfo = "No story character placed."; e.Handled = true; }
            else if (e.Key == Key.Escape && (_editor.PickedActor >= 0 || _pickStart)) { _pickStart = false; _editor.LetGo(); e.Handled = true; }
            else if (Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                if (e.Key == Key.Z && !shift) { _editor.Undo(); e.Handled = true; }
                else if (e.Key == Key.C && !shift) { _editor.CopySteps(); e.Handled = true; }
                else if (e.Key == Key.V && !shift) { _editor.PasteSteps(); e.Handled = true; }
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
