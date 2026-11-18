using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;

namespace LufiaForge.Modules.MapEditor;

// undo / redo for the open map's edits before Apply: blocks, size, characters, exits, arrival points and routes.
// after every finished edit (a paint stroke, a drag, a stamp, a typed value, a button) the map gets compared with the
// last snapshot, and if it changed the snapshot becomes an undo step named after what changed. Apply writes the map
// to the ROM, from then on Edit > Undo (the app's history) takes the whole Apply back
public partial class MapEditorViewModel
{
    private const int MapUndoLimit = 300;
    private LufiaMap.State? _current, _baseline;
    private readonly List<(LufiaMap.State State, string What)> _mapUndo = new(), _mapRedo = new();
    private bool _inGesture, _checkpointPending;

    [ObservableProperty] private string _mapUndoText = "Nothing to undo on this map";
    [ObservableProperty] private string _mapRedoText = "Nothing to redo";

    // steps on this map, newest first (the Undo button's list)
    public List<string> MapUndoSteps => _mapUndo.Select(s => s.What).Reverse().ToList();

    // a freshly loaded (or applied) map, the history starts here
    private void StartHistory()
    {
        _current = _baseline = _map?.Capture();
        _mapUndo.Clear(); _mapRedo.Clear();
        UpdateHistoryUi();
    }

    // call after changing the map: marks it unapplied and records a step once the edit is done
    private void MarkEdited()
    {
        HasUnappliedChanges = true;
        if (_inGesture) return;   // mouse is still down, OnMouseUp records the whole stroke / drag
        if (_checkpointPending) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) { Checkpoint(); return; }
        _checkpointPending = true;
        dispatcher.BeginInvoke(() => { _checkpointPending = false; Checkpoint(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // records a step if the map differs from the last snapshot
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
        var selected = _selection;
        if (state.Threshold != _map.CompositeThreshold) CancelStamp();   // its block numbers were for the other threshold
        _map.Restore(state);
        _current = state;
        if (_rom != null && _tileset != null && !_map.IsWorld) { _tileset = MapTileset.ForMap(_rom, _map); BuildPalette(); OnPropertyChanged(nameof(BrushText)); }
        RedrawMap();
        ClearSelection();
        Reselect(selected);
        RebuildOverlays();
        RefreshMapSettings();
        HasUnappliedChanges = _baseline == null || !state.SameAs(_baseline);
        UpdateHistoryUi();
    }

    // selects the same object again after the map was restored or reloaded, if it's still there
    private void Reselect((string kind, int index)? selected)
    {
        if (_map == null || selected is not { } sel) return;
        if (sel.kind == "npc" && sel.index < _map.Npcs.Count && !_map.Npcs[sel.index].IsUnused) SelectNpc(_map.Npcs[sel.index]);
        else if (sel.kind == "exit" && sel.index < _map.Exits.Count) SelectExit(_map.Exits[sel.index], focusLinked: false);
        else if (sel.kind == "arrival" && sel.index < _map.Arrivals.Count) SelectArrival(_map.Arrivals[sel.index], focusLinked: false);
        else if (sel.kind == "trigger" && sel.index < _map.Triggers.Count && !_map.Triggers[sel.index].IsUnused) SelectTrigger(_map.Triggers[sel.index]);
    }

    // writes this map's unapplied edits to the ROM without asking (the project is being saved) and carries on like
    // nothing happened: the selection, the brush, a building being placed and this map's undo steps all stay.
    // false (with the reason) if they couldn't be written, then the edits stay unapplied
    public bool FlushPendingEdits(out string? error)
    {
        error = null;
        if (_rom == null || _map == null || !HasUnappliedChanges) return true;
        if (_inGesture) { error = "a map edit is in progress"; return false; }
        if (ChangedElsewhere()) { error = ChangedElsewhereText; return false; }
        try
        {
            var result = MapWriter.Save(_rom, _map, allowExpand: true);
            int mapId = _map.MapId;
            var selected = _selection;
            _map = LufiaMap.Load(_rom, mapId);
            RememberLoaded();
            _spotContents = MapSetupScript.ReadSpots(_rom, mapId);
            _tileset = MapTileset.ForMap(_rom, _map);
            _baseline = _current = _map.Capture();
            HasUnappliedChanges = false;
            _links = MapLinks.Build(_rom, Maps.Select(m => m.MapId));
            ClearSelection();
            Reselect(selected);
            RebuildOverlays();
            _mainVm?.NotifyRomModified($"Map {mapId:X2}: blocks/characters/exits ({(result.Moved ? $"moved to 0x{result.FileOffset:X6}" : $"in place at 0x{result.FileOffset:X6}")})");
            UpdateHistoryUi();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // the map's unapplied edits, or painted world map battle zones
    public bool HasPendingEdits => _rom != null && (HasUnappliedChanges || ZonesDirty);

    public string? WritePendingEdits()
    {
        if (!FlushPendingEdits(out var error)) return $"Map {_map?.MapId:X2}: {error}";
        if (ZonesDirty && _rom != null)
        {
            try
            {
                // changed somewhere else since we read them (Game Data's encounter tab), writing this copy would put
                // the older zones back over that
                if (!Core.Battle.Encounters.ReadZones(_rom).AsSpan().SequenceEqual(_zonesRead))
                    return "Encounter zones: they were changed on another page since they were read here. Revert the map to read them again, then paint again.";
                Core.Battle.Encounters.WriteZones(_rom, _zones, allowExpand: true);
                _zonesRead = (byte[])_zones.Clone();
                ZonesDirty = false;
                _mainVm?.NotifyRomModified("World map encounter zones");
            }
            catch (Exception ex) { return "Encounter zones: " + ex.Message; }
        }
        return null;
    }

    // before anything that reloads the map (block editor, importing, make room...) its edits get written first.
    // false if they couldn't be (the reason goes in the status bar)
    private bool WritePendingFirst()
    {
        if (FlushPendingEdits(out var error)) return true;
        Status = $"This map's edits couldn't be written first ({error}): Apply or Revert them, then try again.";
        return false;
    }

    // draws the whole map again (after undo / redo, which can change its size)
    private void RedrawMap()
    {
        if (_map == null || _tileset == null) return;
        _pixels = _tileset.RenderMap(_map);
        int widthPx = _map.Width * 16, heightPx = _map.Height * 16;
        var bmp = new WriteableBitmap(widthPx, heightPx, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, widthPx, heightPx), _pixels, widthPx * 4, 0);
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

    // what changed between two snapshots, in a few words
    private static string Describe(LufiaMap.State a, LufiaMap.State b)
    {
        var parts = new List<string>();
        if (a.Tileset != b.Tileset) parts.Add($"tileset {b.Tileset}");
        if (a.Palette != b.Palette) parts.Add($"palette {b.Palette}");
        if (a.Width != b.Width || a.Height != b.Height) parts.Add($"map size {b.Width} x {b.Height}");
        else if (a.Tileset == b.Tileset)
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
