using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// The Maps page's puzzle tool (U): a tile change that something sets off (a wall blasted open by a bomb, a passage cut
/// with the sword, a door drawn open with the action button). Three steps on the map: drag over what changes (the wall
/// and the floor in front of it: walls are flags on both), click the top-left of the look it changes to (painted
/// elsewhere on the map), click the spot that sets it off. <see cref="L2PuzzleBuild.AddTileChange"/> writes it.
/// </summary>
public partial class L2MapsPage
{
    public static readonly (L2PuzzleBuild.Trigger Kind, string Label)[] PuzzleTriggers =
    {
        (L2PuzzleBuild.Trigger.Bomb, "A bomb (blast a wall or floor)"),
        (L2PuzzleBuild.Trigger.Sword, "A sword hit (cut grass, a vine)"),
        (L2PuzzleBuild.Trigger.Arrow, "An arrow (hit a switch from afar)"),
        (L2PuzzleBuild.Trigger.Hammer, "The hammer (break a rock or pillar)"),
        (L2PuzzleBuild.Trigger.ActionButton, "The action button (a lever, a door)"),
    };
    public List<string> PuzzleTriggerChoices { get; } = PuzzleTriggers.Select(t => t.Label).ToList();
    [ObservableProperty] private int _puzzleTrigger;

    public bool IsAddPuzzleTool { get => Tool == L2Tool.AddPuzzle; set { if (value) Tool = L2Tool.AddPuzzle; } }
    public bool PuzzleOptionsVisible => Tool == L2Tool.AddPuzzle && _world == null && _edit != null;

    /// <summary>0: drag what changes; 1: click the look it changes to; 2: click the spot that sets it off.</summary>
    private int _puzzleStep;
    private (int X1, int Y1, int X2, int Y2) _puzzleTarget;
    private (int X, int Y) _puzzleFrom;

    private string PuzzleHint => _puzzleStep switch
    {
        1 => $"Puzzle (U), 2 of 3: click the top-left block of the look it changes to ({_puzzleTarget.X2 - _puzzleTarget.X1} x {_puzzleTarget.Y2 - _puzzleTarget.Y1} blocks, painted somewhere else on the map with Paint and Collision, walls and all). Right-click starts again.",
        2 => "Puzzle (U), 3 of 3: click the block that sets it off (where the bomb goes off, the sword or hammer hits, the arrow lands, or the party faces and presses the action button). Right-click starts again.",
        _ => "Puzzle (U), 1 of 3: drag over what changes: the wall or door and the floor in front of it (walls are on both). Paint the look it changes to somewhere else on the map first; the trigger is picked below.",
    };

    private void ResetPuzzle() { _puzzleStep = 0; AreaRect = null; OnPropertyChanged(nameof(ToolHint)); }

    /// <summary>A press with the puzzle tool.</summary>
    private void PuzzleDown(int cx, int cy, bool right)
    {
        if (right) { ResetPuzzle(); return; }
        switch (_puzzleStep)
        {
            case 0:
                _areaStart = (cx, cy);
                AreaRect = (cx, cy, cx + 1, cy + 1);
                _dragging = true;
                break;
            case 1:
                _puzzleFrom = (cx, cy);
                int w = _puzzleTarget.X2 - _puzzleTarget.X1, h = _puzzleTarget.Y2 - _puzzleTarget.Y1;
                if (_edit == null || cx + w > _edit.Width || cy + h > _edit.Height) { Status = "The look doesn't fit there: it needs as many blocks as what changes."; return; }
                AreaRect = (cx, cy, cx + w, cy + h);
                _puzzleStep = 2;
                OnPropertyChanged(nameof(ToolHint));
                break;
            case 2:
                AddPuzzle(cx, cy);
                break;
        }
    }

    /// <summary>The drag of step 1 ended: what changes is picked.</summary>
    private void PuzzleUp()
    {
        if (AreaRect is not { } r) return;
        _puzzleTarget = r;
        _puzzleStep = 1;
        OnPropertyChanged(nameof(ToolHint));
    }

    private void AddPuzzle(int sx, int sy)
    {
        if (_edit == null || _rom == null || _main == null) return;
        var (kind, _) = PuzzleTriggers[Math.Clamp(PuzzleTrigger, 0, PuzzleTriggers.Length - 1)];
        // the map's pending edits first (the puzzle is written into the ROM at once, with the map as it is then)
        var why = WritePendingEdits();
        if (why != null) { Status = "Not added: the map's other changes couldn't be written: " + why; return; }
        int map = _edit.MapId;
        var t = _puzzleTarget;
        try
        {
            string done = L2PuzzleBuild.AddTileChange(_rom, map, kind, sx, sy, t.X1, t.Y1, t.X2 - t.X1, t.Y2 - t.Y1, _puzzleFrom.X, _puzzleFrom.Y);
            _main.NotifyRomModified($"Lufia II: puzzle on map {map:X2}");
            _edit = new L2MapEdit(_rom, map);
            _history.Clear(); _redo.Clear();
            UpdateUndoState();
            ResetPuzzle();
            Refresh();
            Status = done + " See it in Events (the map's puzzle scripts); test it in the game from there.";
        }
        catch (Exception ex) { Status = "Not added: " + ex.Message; ResetPuzzle(); }
    }

    // ---------------------------------------------------------------- presets (R): a door or a bomb piece from the game

    public static readonly (L2PuzzlePresets.Kind Kind, string Label)[] PresetKinds =
    {
        (L2PuzzlePresets.Kind.Door, "Door (opens when the party faces it and presses A)"),
        (L2PuzzlePresets.Kind.BombWall, "Bomb piece (a wall, statue or pillar a bomb breaks)"),
    };
    public List<string> PresetKindChoices { get; } = PresetKinds.Select(k => k.Label).ToList();
    [ObservableProperty] private int _presetKind;
    public ObservableCollection<L2PresetChoice> PresetChoices { get; } = new();
    [ObservableProperty] private L2PresetChoice? _selectedPreset;

    public bool IsAddPresetTool { get => Tool == L2Tool.AddPreset; set { if (value) Tool = L2Tool.AddPreset; } }
    public bool PresetOptionsVisible => Tool == L2Tool.AddPreset && _world == null && _edit != null;

    private List<L2PuzzlePresets.Preset>? _gamePresets;
    /// <summary>0: click where the closed piece goes; 1: click where the open one is kept.</summary>
    private int _presetStep;
    private (int X, int Y) _presetAt;
    private ((int Block, byte Flags)[][] Closed, (int Block, byte Flags)[][] Open)? _presetCells;

    private string PresetHint => _presetStep == 1
        ? "Preset (R), 2 of 2: click where the open look is kept: anywhere on the map, best where the camera never goes (the game keeps them off to the side). The puzzle is written then. Right-click starts again (Ctrl+Z takes the piece back)."
        : "Preset (R), 1 of 2: pick a door or a bomb piece below, then click where it goes (its top-left block). Then click where its open look is kept; the puzzle that opens it is made for you.";

    partial void OnPresetKindChanged(int value) => FillPresets();

    /// <summary>The game's presets of the picked kind: this map's graphics first.</summary>
    private void FillPresets()
    {
        PresetChoices.Clear();
        if (_rom == null || _edit == null || _edit.IsWorld) { SelectedPreset = null; return; }
        _gamePresets ??= L2PuzzlePresets.Game(_rom);
        var kind = PresetKinds[Math.Clamp(PresetKind, 0, PresetKinds.Length - 1)].Kind;
        int bs = _edit.BlockSetIndex, ts = _edit.TileSetIndex, ps = _edit.PaletteSetIndex;
        foreach (var p in _gamePresets.Where(p => p.Kind == kind)
                     .OrderBy(p => p.BlockSet == bs && p.TileSet == ts && p.PaletteSet == ps ? 0 : p.TileSet == ts ? 1 : 2).ThenBy(p => p.Map))
        {
            string fit = p.BlockSet == bs && p.TileSet == ts && p.PaletteSet == ps ? " (same graphics)" : p.TileSet == ts ? " (same tiles)" : "";
            var (w, h, px) = L2PuzzlePresets.Picture(_rom, p);
            var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), w * 4);
            img.Freeze();
            PresetChoices.Add(new L2PresetChoice { Preset = p, Label = p.Name + fit, Image = img });
        }
        SelectedPreset = PresetChoices.FirstOrDefault();
    }

    private void ResetPreset() { _presetStep = 0; _presetCells = null; AreaRect = null; OnPropertyChanged(nameof(ToolHint)); }

    /// <summary>Paints preset cells at (x, y): both layers' blocks, and the upper layer's flags (the walls).</summary>
    private void PaintCells((int Block, byte Flags)[][] cells, int w, int h, int x, int y)
    {
        if (_edit == null) return;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                var c = cells[j * w + i];
                for (int l = 0; l < _edit.Map.Layers.Length && l < c.Length; l++)
                {
                    var L = _edit.Map.Layers[l];
                    if (x + i >= L.Width || y + j >= L.Height) continue;
                    L.Blocks[(y + j) * L.Width + x + i] = c[l].Block;
                    L.Flags[(y + j) * L.Width + x + i] = c[l].Flags;
                }
            }
    }

    private void PresetDown(int cx, int cy, bool right)
    {
        if (right) { ResetPreset(); return; }
        if (_edit == null || _rom == null || SelectedPreset?.Preset is not { } p) { Status = "Pick a preset below first."; return; }
        if (cx + p.W > _edit.Width || cy + p.H > _edit.Height) { Status = $"It doesn't fit there: it is {p.W} x {p.H} blocks."; return; }
        if (_presetStep == 0)
        {
            try { _presetCells = L2PuzzlePresets.Translate(_rom, p, _edit); }
            catch (Exception ex) { Status = "This piece can't be used on this map: " + ex.Message; return; }
            Record("placing a preset");
            PaintCells(_presetCells.Value.Closed, p.W, p.H, cx, cy);
            DropIfSame(); DrawMap(); UpdateUndoState();
            _presetAt = (cx, cy);
            AreaRect = (cx, cy, cx + p.W, cy + p.H);
            _presetStep = 1;
            OnPropertyChanged(nameof(ToolHint));
            return;
        }
        if (_presetCells is not { } cells) { ResetPreset(); return; }
        var r = (X1: cx, Y1: cy, X2: cx + p.W, Y2: cy + p.H);
        if (r.X1 < _presetAt.X + p.W && _presetAt.X < r.X2 && r.Y1 < _presetAt.Y + p.H && _presetAt.Y < r.Y2) { Status = "The open look must go somewhere else than the piece itself."; return; }
        Record("placing a preset's open look");
        PaintCells(cells.Open, p.W, p.H, cx, cy);
        DropIfSame(); DrawMap(); UpdateUndoState();
        // write the map, then the puzzle that changes the piece into the open look
        var why = WritePendingEdits();
        if (why != null) { Status = "Not added: the map couldn't be written: " + why; ResetPreset(); return; }
        var trigger = p.Kind == L2PuzzlePresets.Kind.Door ? L2PuzzleBuild.Trigger.ActionButton : L2PuzzleBuild.Trigger.Bomb;
        int map = _edit.MapId;
        try
        {
            string done = L2PuzzleBuild.AddTileChange(_rom, map, trigger, _presetAt.X + p.TriggerX, _presetAt.Y + p.TriggerY,
                _presetAt.X, _presetAt.Y, p.W, p.H, cx, cy);
            _main?.NotifyRomModified($"Lufia II: {(p.Kind == L2PuzzlePresets.Kind.Door ? "door" : "bomb piece")} on map {map:X2}");
            _edit = new L2MapEdit(_rom, map);
            _history.Clear(); _redo.Clear();
            UpdateUndoState();
            ResetPreset();
            Refresh();
            Status = done;
        }
        catch (Exception ex) { Status = "The pieces are painted, but the puzzle wasn't added: " + ex.Message; ResetPreset(); }
    }
}

/// <summary>A preset in the Maps page's list: its picture (closed, then open) and where it comes from.</summary>
public sealed class L2PresetChoice
{
    public L2PuzzlePresets.Preset? Preset { get; init; }
    public string Label { get; init; } = "";
    public BitmapSource? Image { get; init; }
}
