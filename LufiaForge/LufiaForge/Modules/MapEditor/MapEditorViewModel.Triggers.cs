using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.MapEditor;

/// <summary>
/// Step-on areas (walking in runs an event): shown in pink, selected, moved and resized like exits, added with the
/// ＋ Area tool (T) and deleted. New areas are switched on and get an empty event when the map is applied; "Event…"
/// then opens it. Deleting keeps later areas' numbers (their events go by number): only a new last area is removed,
/// others become empty and are switched off on Apply.
/// </summary>
public partial class MapEditorViewModel
{
    [ObservableProperty] private bool _showTriggers = true;
    [ObservableProperty] private bool _isTriggerSelected;
    [ObservableProperty] private string _triggerX = "", _triggerY = "";
    partial void OnShowTriggersChanged(bool value) => RebuildOverlays();

    public bool IsAddTriggerTool { get => Tool == MapTool.AddTrigger; set { if (value) Tool = MapTool.AddTrigger; } }

    private static readonly SolidColorBrush TriggerOn = new(Color.FromRgb(255, 64, 200)), TriggerOff = new(Color.FromArgb(170, 255, 140, 220));

    /// <summary>The area's state: switched on in the map's setup script, new (switched on when applied), or never switched on (dormant).</summary>
    private string TriggerState(MapTrigger t) =>
        _rom == null || _map == null ? "" :
        t.Index >= _map.LoadedTriggerCount ? "new" :
        MapEvents.EnabledAreas(_rom, _map.MapId).Contains(t.Index) ? "on" : "off";

    private void AddTriggerOverlays()
    {
        if (_map == null || _rom == null || !ShowTriggers) return;
        var on = MapEvents.EnabledAreas(_rom, _map.MapId);
        foreach (var t in _map.Triggers.Where(t => !t.IsUnused))
        {
            bool active = on.Contains(t.Index) || t.Index >= _map.LoadedTriggerCount;
            Overlays.Add(new MapOverlay
            {
                Kind = "trigger", Index = t.Index, X = t.X1 * 16, Y = t.Y1 * 16,
                W = Math.Max(1, t.X2 - t.X1) * 16, H = Math.Max(1, t.Y2 - t.Y1) * 16,
                Stroke = active ? TriggerOn : TriggerOff, Thickness = active ? 2.5 : 1.5,
                Label = $"T{t.Index}→ev{MapEvents.AreaEvent(_rom, _map.MapId, t.Index)}" + (active ? "" : " (off)"),
            });
        }
    }

    private IEnumerable<(string Kind, int Index)> TriggersAt(int x, int y) =>
        _map == null || !ShowTriggers ? Enumerable.Empty<(string, int)>() :
        _map.Triggers.Where(t => !t.IsUnused && x >= t.X1 && x < t.X2 && y >= t.Y1 && y < t.Y2).Reverse().Select(t => ("trigger", t.Index));

    private void SelectTrigger(MapTrigger t)
    {
        if (_map == null || _rom == null) return;
        _selection = ("trigger", t.Index);
        IsExitSelected = IsNpcSelected = IsSpotSelected = IsArrivalSelected = false;
        IsTriggerSelected = true;
        SpotEdits.Clear();
        int ev = MapEvents.AreaEvent(_rom, _map.MapId, t.Index);
        string state = TriggerState(t);
        SelectionTitle = $"Step-on area T{t.Index}";
        SelectionDetails =
            $"Blocks ({t.X1},{t.Y1}) to ({t.X2 - 1},{t.Y2 - 1}), {t.X2 - t.X1} x {t.Y2 - t.Y1}\n" +
            $"Walking in runs event {ev}.\n" +
            state switch
            {
                "new" => "New: Writing the map (by itself, or Apply to ROM in the top bar) switches it on and makes its event (then Event… opens it).",
                "on" => "Switched on when the map loads (by its setup script).",
                _ => "Not switched on by this map's setup script, so it does nothing in the game (the game keeps a few spare areas).",
            } + "\nDrag it to move it, its bottom-right block to resize it.";
        DialoguePreview = "";
        try
        {
            var s = MapEvents.HasEvent(_rom, _map.MapId, ev) ? EventScript.Load(_rom, _map.MapId, ev) : null;
            var text = s?.Ops.FirstOrDefault(o => o.IsText && o.Text.Trim().Length > 0)?.Text.Replace("\n", " ");
            if (text != null) DialoguePreview = "\"" + (text.Length > 120 ? text[..120] + "…" : text) + "\"";
        }
        catch { }
        RebuildOverlays();
    }

    /// <summary>Mouse down on a selected area: move it, or resize it from its bottom-right block.</summary>
    private void StartTriggerDrag(int x, int y)
    {
        if (_map == null || _selection is not { kind: "trigger" } sel || sel.index >= _map.Triggers.Count) return;
        var t = _map.Triggers[sel.index];
        bool corner = x == t.X2 - 1 && y == t.Y2 - 1 && (t.X2 - t.X1 > 1 || t.Y2 - t.Y1 > 1);
        _dragMode = corner ? "trigger-resize" : "trigger-move";
        _dragStart = (x, y);
        _dragAnchor = corner ? (t.X1, t.Y1) : (x - t.X1, y - t.Y1);
    }

    private void DragTrigger(int x, int y, bool resize)
    {
        if (_map == null || _selection is not { kind: "trigger" } sel || sel.index >= _map.Triggers.Count) return;
        var t = _map.Triggers[sel.index];
        x = Math.Clamp(x, 0, _map.Width - 1); y = Math.Clamp(y, 0, _map.Height - 1);
        if (resize)
        {
            var (ax, ay) = _dragAnchor;
            t.X1 = Math.Min(ax, x); t.Y1 = Math.Min(ay, y);
            t.X2 = Math.Max(ax, x) + 1; t.Y2 = Math.Max(ay, y) + 1;
        }
        else
        {
            int w = Math.Max(1, t.X2 - t.X1), h = Math.Max(1, t.Y2 - t.Y1);
            int nx = Math.Clamp(x - _dragAnchor.x, 0, _map.Width - w), ny = Math.Clamp(y - _dragAnchor.y, 0, _map.Height - h);
            if (nx == t.X1 && ny == t.Y1) return;
            t.X1 = nx; t.Y1 = ny; t.X2 = nx + w; t.Y2 = ny + h;
        }
        MarkEdited();
        RebuildOverlays();
    }

    /// <summary>The ＋ Area tool: a new area at the clicked block (drag to size it).</summary>
    private void AddTriggerAt(int x, int y)
    {
        if (_map == null || _rom == null) return;
        try
        {
            int index = MapEvents.NextAreaIndex(_rom, _map);
            var t = _map.AddTrigger(x, y, x + 1, y + 1, index);
            MarkEdited();
            SelectTrigger(t);
            _dragStart = (x, y); _dragAnchor = (x, y); _dragMode = "new-trigger"; _dragMoved = false;
        }
        catch (Exception ex) { Status = "New area: " + ex.Message; }
    }

    private void DeleteTrigger(MapTrigger t)
    {
        if (_map == null) return;
        bool removed = t.Index >= _map.LoadedTriggerCount && _map.Triggers[^1] == t;
        _map.RemoveTrigger(t);
        MarkEdited();
        ClearSelection();
        RebuildOverlays();
        Status = removed ? "New area removed." : $"Area T{t.Index} deleted: Apply switches it off (later areas keep their numbers and events).";
    }

    /// <summary>Opens the selected area's event (it exists once the map is applied).</summary>
    [RelayCommand]
    private void OpenTriggerEvent()
    {
        if (_rom == null || _map == null || _selection is not { kind: "trigger" } sel) return;
        // writing the map switches the area on and makes its event
        if (!WritePendingFirst() || _map == null) return;
        if (sel.index >= _map.LoadedTriggerCount) { Status = "The area couldn't be switched on yet: see the status above."; return; }
        int ev = MapEvents.AreaEvent(_rom, _map.MapId, sel.index);
        if (!MapEvents.HasEvent(_rom, _map.MapId, ev))
        {
            if (System.Windows.MessageBox.Show($"Area T{sel.index} runs event {ev}, which has no script yet. Make an empty one and switch the area on?", "Step-on area",
                    System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK) return;
            try
            {
                MapEvents.Create(_rom, _map.MapId, ev);
                MapEvents.SetAreaEnabled(_rom, _map.MapId, sel.index, true);
                _mainVm?.NotifyRomModified($"Map {_map.MapId:X2}: event {ev} for area T{sel.index}");
            }
            catch (Exception ex) { Status = "Area event: " + ex.Message; return; }
        }
        var script = EventScript.Load(_rom, _map.MapId, ev);
        if (script != null) EventEditorRequested?.Invoke(script, $"Map {_map.MapId:X2} - step-on area T{sel.index} (walking in runs event {ev})");
    }
}
