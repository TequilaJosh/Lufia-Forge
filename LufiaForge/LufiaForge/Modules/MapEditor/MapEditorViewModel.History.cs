using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;

namespace LufiaForge.Modules.MapEditor;

/// <summary>
/// Undo / redo for the open map's edits before Apply: blocks, size, characters, exits, arrival points and routes.
/// After every finished edit (a paint stroke, a drag, a stamp, a typed value, a button) the map is compared with the
/// last snapshot and, when it changed, the snapshot becomes an undo step named after what changed. Apply writes the map
/// to the ROM: from then on Edit > Undo (the app's history) takes the whole Apply back.
/// </summary>
public partial class MapEditorViewModel
{
    private const int MapUndoLimit = 300;
    private LufiaMap.State? _current, _baseline;
    private readonly List<(LufiaMap.State State, string What)> _mapUndo = new(), _mapRedo = new();
    private bool _inGesture, _checkpointPending;

    [ObservableProperty] private string _mapUndoText = "Nothing to undo on this map";
    [ObservableProperty] private string _mapRedoText = "Nothing to redo";

    /// <summary>Steps on this map, newest first (the Undo button's list).</summary>
    public List<string> MapUndoSteps => _mapUndo.Select(s => s.What).Reverse().ToList();

    /// <summary>A freshly loaded (or applied) map: the history starts here.</summary>
    private void StartHistory()
    {
        _current = _baseline = _map?.Capture();
        _mapUndo.Clear(); _mapRedo.Clear();
        UpdateHistoryUi();
    }

    /// <summary>Call after changing the map: marks it unapplied and records a step once the edit is finished.</summary>
    private void MarkEdited()
    {
        HasUnappliedChanges = true;
        if (_inGesture) return;   // the mouse is still down: OnMouseUp records the whole stroke / drag
        if (_checkpointPending) return;
        var d = Application.Current?.Dispatcher;
        if (d == null) { Checkpoint(); return; }
        _checkpointPending = true;
        d.BeginInvoke(() => { _checkpointPending = false; Checkpoint(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Records a step when the map differs from the last snapshot.</summary>
    public void Checkpoint()
    {
        if (_map == null || _current == null) return;
        var now = _map.Capture();
        if (now.SameAs(_current)) return;
        _mapUndo.Add((_current, Describe(_current, now)));
        if (_mapUndo.Count > MapUndoLimit) _mapUndo.RemoveAt(0);
        _mapRedo.Clear();
        _current = now;
        UpdateHistoryUi();
    }

    private bool CanUndo() => _mapUndo.Count > 0 || _map != null && _current != null && HasUnappliedChanges && !_map.Capture().SameAs(_current);
    private bool CanRedo() => _mapRedo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        Checkpoint();
        if (_mapUndo.Count == 0 || _current == null) return;
        var (state, what) = _mapUndo[^1];
        _mapUndo.RemoveAt(_mapUndo.Count - 1);
        _mapRedo.Add((_current, what));
        GoTo(state);
        Status = $"Undone: {what}. (Redo: Ctrl+Y)";
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        Checkpoint();
        if (_mapRedo.Count == 0 || _current == null) return;
        var (state, what) = _mapRedo[^1];
        _mapRedo.RemoveAt(_mapRedo.Count - 1);
        _mapUndo.Add((_current, what));
        GoTo(state);
        Status = $"Redone: {what}.";
    }

    private void GoTo(LufiaMap.State state)
    {
        if (_map == null) return;
        var sel = _selection;
        _map.Restore(state);
        _current = state;
        RedrawMap();
        ClearSelection();
        // keep the selected object when it still exists
        if (sel is { } s)
        {
            if (s.kind == "npc" && s.index < _map.Npcs.Count && !_map.Npcs[s.index].IsUnused) SelectNpc(_map.Npcs[s.index]);
            else if (s.kind == "exit" && s.index < _map.Exits.Count) SelectExit(_map.Exits[s.index], focusLinked: false);
            else if (s.kind == "arrival" && s.index < _map.Arrivals.Count) SelectArrival(_map.Arrivals[s.index], focusLinked: false);
            else if (s.kind == "trigger" && s.index < _map.Triggers.Count && !_map.Triggers[s.index].IsUnused) SelectTrigger(_map.Triggers[s.index]);
        }
        RebuildOverlays();
        RefreshMapSettings();
        HasUnappliedChanges = _baseline == null || !state.SameAs(_baseline);
        UpdateHistoryUi();
    }

    /// <summary>Draws the whole map again (after undo / redo, which can change its size).</summary>
    private void RedrawMap()
    {
        if (_map == null || _tileset == null) return;
        _pixels = _tileset.RenderMap(_map);
        int w = _map.Width * 16, h = _map.Height * 16;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), _pixels, w * 4, 0);
        MapImage = bmp;
    }

    private void UpdateHistoryUi()
    {
        MapUndoText = _mapUndo.Count > 0 ? $"Undo: {_mapUndo[^1].What}  (Ctrl+Z, {_mapUndo.Count} step{(_mapUndo.Count == 1 ? "" : "s")})" : "Nothing to undo on this map";
        MapRedoText = _mapRedo.Count > 0 ? $"Redo: {_mapRedo[^1].What}  (Ctrl+Y)" : "Nothing to redo";
        OnPropertyChanged(nameof(MapUndoSteps));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What changed between two snapshots, in a few words.</summary>
    private static string Describe(LufiaMap.State a, LufiaMap.State b)
    {
        var parts = new List<string>();
        if (a.Width != b.Width || a.Height != b.Height) parts.Add($"map size {b.Width} x {b.Height}");
        else
        {
            int blocks = 0;
            for (int i = 0; i < a.Layer.Length; i++) if (a.Layer[i] != b.Layer[i]) blocks++;
            if (blocks > 0) parts.Add(blocks == 1 ? "1 block" : $"{blocks} blocks");
        }
        Compare(parts, a.Npcs, b.Npcs, LufiaMap.State.Key, "character ", n => n.Index + 1,
            (x, y) => x.Sprite == 0xFF && y.Sprite != 0xFF ? "shown" : x.Sprite != 0xFF && y.Sprite == 0xFF ? "hidden" :
                      x.X != y.X || x.Y != y.Y ? "moved" : x.Sprite != y.Sprite ? "sprite" : "changed");
        Compare(parts, a.Exits, b.Exits, LufiaMap.State.Key, "exit E", e => e.Index,
            (x, y) => x.DestMap != y.DestMap || x.Arrival != y.Arrival ? "destination" :
                      x.X2 - x.X1 != y.X2 - y.X1 || x.Y2 - x.Y1 != y.Y2 - y.Y1 ? "resized" : "moved");
        Compare(parts, a.Arrivals, b.Arrivals, LufiaMap.State.Key, "arrival point A", p => p.Index, (_, _) => "moved");
        Compare(parts, a.Triggers, b.Triggers, LufiaMap.State.Key, "area T", t => t.Index,
            (x, y) => y.IsUnused && !x.IsUnused ? "deleted" : x.X2 - x.X1 != y.X2 - y.X1 || x.Y2 - x.Y1 != y.Y2 - y.Y1 ? "resized" : "moved");
        if (a.Paths.Count != b.Paths.Count || a.Paths.Zip(b.Paths).Any(p => LufiaMap.State.Key(p.First) != LufiaMap.State.Key(p.Second)))
            parts.Add("routes");
        return parts.Count == 0 ? "edit" : string.Join(", ", parts.Take(3)) + (parts.Count > 3 ? "…" : "");
    }

    private static void Compare<T>(List<string> parts, List<T> a, List<T> b, Func<T, string> key, string noun, Func<T, int> number, Func<T, T, string> how)
    {
        for (int i = b.Count; i < a.Count; i++) parts.Add($"deleted {noun}{number(a[i])}");
        for (int i = a.Count; i < b.Count; i++) parts.Add($"added {noun}{number(b[i])}");
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            if (key(a[i]) != key(b[i])) parts.Add($"{noun}{number(b[i])} {how(a[i], b[i])}");
    }
}
