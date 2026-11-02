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
            _readVersion = _rom.Version;
            _history.Clear(); _redo.Clear();
            UpdateUndoState();
            ResetPuzzle();
            Refresh();
            Status = done + " See it in Events (the map's puzzle scripts); test it in the game from there.";
        }
        catch (Exception ex) { Status = "Not added: " + ex.Message; ResetPuzzle(); }
    }

    // ---------------------------------------------------------------- presets (R): doors, bomb pieces, spikes

    /// <summary>What the preset tool places.</summary>
    public enum PresetKindId { Door, BombPiece, SpikesLever, SpikesPlate, SpikesSteps }

    public static readonly (PresetKindId Kind, string Label)[] PresetKinds =
    {
        (PresetKindId.Door, "Door (one of the game's)"),
        (PresetKindId.BombPiece, "Bomb piece (a wall, statue or pillar a bomb breaks)"),
        (PresetKindId.SpikesLever, "Spikes a lever raises and lowers (paint them first)"),
        (PresetKindId.SpikesPlate, "Spikes a floor plate lowers (paint them first)"),
        (PresetKindId.SpikesSteps, "Spikes that go up and down as the party walks (paint them first)"),
    };
    public List<string> PresetKindChoices { get; } = PresetKinds.Select(k => k.Label).ToList();
    [ObservableProperty] private int _presetKind;

    /// <summary>What opens a placed door.</summary>
    public static readonly string[] DoorOpeners =
    {
        "Facing it and pressing A", "A floor plate (click it after the door)", "A lever or wall switch (click it after the door)",
        "A key item (walking up to it)", "A story flag (open once it is on)",
    };
    public List<string> DoorOpenerChoices { get; } = DoorOpeners.ToList();
    [ObservableProperty] private int _doorOpener;
    public List<L2Option> KeyItemChoices { get; } = new();
    [ObservableProperty] private L2Option? _keyItem;
    [ObservableProperty] private bool _useUpKey = true;
    public List<L2Option> FlagChoices { get; } = new();
    [ObservableProperty] private L2Option? _doorFlag;
    [ObservableProperty] private int _spikeSteps = 3;

    public ObservableCollection<L2PresetChoice> PresetChoices { get; } = new();
    [ObservableProperty] private L2PresetChoice? _selectedPreset;

    public bool IsAddPresetTool { get => Tool == L2Tool.AddPreset; set { if (value) Tool = L2Tool.AddPreset; } }
    public bool PresetOptionsVisible => Tool == L2Tool.AddPreset && _world == null && _edit != null;
    private PresetKindId CurrentKind => PresetKinds[Math.Clamp(PresetKind, 0, PresetKinds.Length - 1)].Kind;
    public bool PresetListVisible => PresetOptionsVisible && CurrentKind is PresetKindId.Door or PresetKindId.BombPiece;
    public bool DoorOptionsVisible => PresetOptionsVisible && CurrentKind == PresetKindId.Door;
    public bool KeyOptionsVisible => DoorOptionsVisible && DoorOpener == 3;
    public bool FlagOptionsVisible => DoorOptionsVisible && DoorOpener == 4;
    public bool StepOptionsVisible => PresetOptionsVisible && CurrentKind == PresetKindId.SpikesSteps;

    private List<L2PuzzlePresets.Preset>? _gamePresets;
    /// <summary>The clicks done so far (map blocks) and what each next click is for.</summary>
    private readonly List<(int X, int Y)> _presetClicks = new();
    private (int X1, int Y1, int X2, int Y2)? _spikeRect;
    private ((int Block, byte Flags)[][] Closed, (int Block, byte Flags)[][] Open)? _presetCells;

    /// <summary>The clicks the picked kind needs, in order (a drag for painted spikes comes first).</summary>
    private List<string> PresetSteps()
    {
        var l = new List<string>();
        switch (CurrentKind)
        {
            case PresetKindId.Door:
                l.Add("click where the door goes (its top-left block)");
                l.Add("click where its open look is kept: anywhere, best where the camera never goes");
                if (DoorOpener == 1) l.Add("click the floor plate's block (the party stepping on it, or a pushed block, opens the door)");
                if (DoorOpener == 2) l.Add("click the lever's block (the party faces it and presses A)");
                break;
            case PresetKindId.BombPiece:
                l.Add("click where the piece goes (its top-left block)");
                l.Add("click where its broken look is kept: anywhere, best where the camera never goes");
                break;
            default:
                l.Add("drag over the spikes as painted now (raised)");
                l.Add("click the top-left of their lowered look, painted somewhere else on the map");
                if (CurrentKind != PresetKindId.SpikesPlate) l.Add("click where a copy of the raised look is kept (it is painted there for you)");
                if (CurrentKind == PresetKindId.SpikesLever) l.Add("click the lever's block (the party faces it and presses A)");
                if (CurrentKind == PresetKindId.SpikesPlate) l.Add("click the floor plate's block");
                break;
        }
        return l;
    }

    private string PresetHint
    {
        get
        {
            var steps = PresetSteps();
            int n = _presetClicks.Count + (_spikeRect != null && _presetClicks.Count == 0 ? 1 : 0);
            n = Math.Min(n, steps.Count - 1);
            return $"Preset (R), {n + 1} of {steps.Count}: {steps[n]}. The puzzle is made after the last click. Right-click starts again (Ctrl+Z takes back what was painted).";
        }
    }

    partial void OnPresetKindChanged(int value) { ResetPreset(); FillPresets(); NotifyPresetOptions(); }
    partial void OnDoorOpenerChanged(int value) { ResetPreset(); NotifyPresetOptions(); }

    private void NotifyPresetOptions()
    {
        foreach (var n in new[] { nameof(PresetListVisible), nameof(DoorOptionsVisible), nameof(KeyOptionsVisible), nameof(FlagOptionsVisible), nameof(StepOptionsVisible), nameof(ToolHint) })
            OnPropertyChanged(n);
    }

    /// <summary>The game's presets of the picked kind (this map's graphics first), and the item and flag lists.</summary>
    private void FillPresets()
    {
        PresetChoices.Clear();
        if (_rom == null || _edit == null || _edit.IsWorld) { SelectedPreset = null; return; }
        if (KeyItemChoices.Count == 0)
        {
            try { KeyItemChoices.AddRange(L2Battle.Items(_rom).Select((it, i) => new L2Option(i, $"{i:X3} {it.Name.Trim()}"))); } catch (Exception) { }
            KeyItem = KeyItemChoices.FirstOrDefault(o => o.Label.Contains("key", StringComparison.OrdinalIgnoreCase)) ?? KeyItemChoices.FirstOrDefault();
        }
        if (FlagChoices.Count == 0)
        {
            FlagChoices.AddRange(Enumerable.Range(1, 0xEF).Select(f => new L2Option(f, $"{f:X2}  {L2StoryFlags.Label(f) ?? "(not used by the game)"}")));
            DoorFlag = FlagChoices.FirstOrDefault();
        }
        var kind = CurrentKind switch { PresetKindId.Door => L2PuzzlePresets.Kind.Door, PresetKindId.BombPiece => L2PuzzlePresets.Kind.BombWall, _ => (L2PuzzlePresets.Kind?)null };
        if (kind == null) { SelectedPreset = null; return; }
        _gamePresets ??= L2PuzzlePresets.Game(_rom);
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

    private void ResetPreset() { _presetClicks.Clear(); _spikeRect = null; _presetCells = null; AreaRect = null; OnPropertyChanged(nameof(ToolHint)); }

    /// <summary>Paints cells at (x, y): both layers' blocks and flags (the walls are the upper layer's).</summary>
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

    private (int Block, byte Flags)[][] CellsAt(int x, int y, int w, int h)
    {
        var cells = new (int, byte)[w * h][];
        for (int j = 0; j < h; j++) for (int i = 0; i < w; i++) cells[j * w + i] = _edit!.CellAt(x + i, y + j);
        return cells;
    }

    /// <summary>A press with the preset tool: one more click of <see cref="PresetSteps"/> (painted spikes start with a drag).</summary>
    private void PresetDown(int cx, int cy, bool right)
    {
        if (right) { ResetPreset(); return; }
        if (_edit == null || _rom == null) return;
        bool painted = CurrentKind is PresetKindId.SpikesLever or PresetKindId.SpikesPlate or PresetKindId.SpikesSteps;
        if (painted && _spikeRect == null)
        {
            _areaStart = (cx, cy);
            AreaRect = (cx, cy, cx + 1, cy + 1);
            _dragging = true;
            return;
        }
        int w, h;
        if (painted) { var r = _spikeRect!.Value; w = r.X2 - r.X1; h = r.Y2 - r.Y1; }
        else
        {
            if (SelectedPreset?.Preset is not { } p) { Status = "Pick a preset below first."; return; }
            w = p.W; h = p.H;
        }
        int step = _presetClicks.Count + (painted ? 1 : 0);
        var steps = PresetSteps();
        bool isPiece = step <= 2 && !(painted && step == 0);   // a click that places a w x h look (not a plate or lever)
        if (isPiece && (cx + w > _edit.Width || cy + h > _edit.Height)) { Status = $"It doesn't fit there: it is {w} x {h} blocks."; return; }
        if (!painted && step == 0)
        {
            // the door or bomb piece itself
            var p = SelectedPreset!.Preset!;
            try { _presetCells = L2PuzzlePresets.Translate(_rom, p, _edit); }
            catch (Exception ex) { Status = "This piece can't be used on this map: " + ex.Message.Replace("the chest's", "the piece's").Replace("chest", "piece") + " Pick one marked (same graphics) or (same tiles)."; return; }
            Record("placing a preset");
            PaintCells(_presetCells.Value.Closed, w, h, cx, cy);
            DropIfSame(); DrawMap(); UpdateUndoState();
            AreaRect = (cx, cy, cx + w, cy + h);
        }
        else if (!painted && step == 1)
        {
            Record("placing a preset's open look");
            PaintCells(_presetCells!.Value.Open, w, h, cx, cy);
            DropIfSame(); DrawMap(); UpdateUndoState();
        }
        else if (painted && step == 2 && CurrentKind != PresetKindId.SpikesPlate)
        {
            // a copy of the raised look, painted here
            var r = _spikeRect!.Value;
            Record("keeping a copy of the raised spikes");
            PaintCells(CellsAt(r.X1, r.Y1, w, h), w, h, cx, cy);
            DropIfSame(); DrawMap(); UpdateUndoState();
        }
        _presetClicks.Add((cx, cy));
        if (_presetClicks.Count + (painted ? 1 : 0) < steps.Count) { OnPropertyChanged(nameof(ToolHint)); return; }
        WritePreset(w, h);
    }

    /// <summary>The drag of painted spikes ended.</summary>
    private void PresetUp()
    {
        if (AreaRect is not { } r) return;
        _spikeRect = r;
        OnPropertyChanged(nameof(ToolHint));
    }

    /// <summary>All the clicks are in: the map is written, then the puzzle.</summary>
    private void WritePreset(int w, int h)
    {
        if (_edit == null || _rom == null) return;
        var why = WritePendingEdits();
        if (why != null) { Status = "Not added: the map couldn't be written: " + why; ResetPreset(); return; }
        int map = _edit.MapId;
        var k = _presetClicks;
        try
        {
            string done;
            switch (CurrentKind)
            {
                case PresetKindId.Door:
                {
                    var p = SelectedPreset!.Preset!;
                    var (x, y) = k[0]; var (ox, oy) = k[1];
                    int tx = x + p.TriggerX, ty = y + p.TriggerY;
                    done = DoorOpener switch
                    {
                        1 => L2PuzzleBuild.AddPlateChange(_rom, map, k[2].X, k[2].Y, x, y, w, h, ox, oy),
                        2 => L2PuzzleBuild.AddTileChange(_rom, map, L2PuzzleBuild.Trigger.ActionButton, k[2].X, k[2].Y, x, y, w, h, ox, oy, 0x10),
                        // (the step-on area on the floor block just below the door: a step event on the door's own
                        // blocks never played in the game)
                        3 => L2PuzzleBuild.AddKeyDoor(_rom, map, tx, y + h, x, y, w, h, ox, oy, KeyItem?.Value ?? 0,
                                 (KeyItem?.Label ?? "key").Split(' ', 2).Last(), UseUpKey, L2FlagUses.Free(_rom)),
                        4 => L2PuzzleBuild.AddFlagDoor(_rom, map, x, y, w, h, ox, oy, DoorFlag?.Value ?? 1),
                        _ => L2PuzzleBuild.AddTileChange(_rom, map, L2PuzzleBuild.Trigger.ActionButton, tx, ty, x, y, w, h, ox, oy),
                    };
                    break;
                }
                case PresetKindId.BombPiece:
                {
                    var p = SelectedPreset!.Preset!;
                    done = L2PuzzleBuild.AddTileChange(_rom, map, L2PuzzleBuild.Trigger.Bomb, k[0].X + p.TriggerX, k[0].Y + p.TriggerY, k[0].X, k[0].Y, w, h, k[1].X, k[1].Y);
                    break;
                }
                default:
                {
                    var r = _spikeRect!.Value;
                    done = CurrentKind switch
                    {
                        PresetKindId.SpikesLever => L2PuzzleBuild.AddLeverToggle(_rom, map, k[2].X, k[2].Y, r.X1, r.Y1, w, h, k[1].X, k[1].Y, k[0].X, k[0].Y),
                        PresetKindId.SpikesPlate => L2PuzzleBuild.AddPlateChange(_rom, map, k[1].X, k[1].Y, r.X1, r.Y1, w, h, k[0].X, k[0].Y),
                        _ => L2PuzzleBuild.AddStepTimer(_rom, map, r.X1, r.Y1, w, h, k[1].X, k[1].Y, k[0].X, k[0].Y, SpikeSteps),
                    };
                    break;
                }
            }
            L2FlagUses.Invalidate();
            _main?.NotifyRomModified($"Lufia II: puzzle on map {map:X2}");
            _edit = new L2MapEdit(_rom, map);
            _readVersion = _rom.Version;
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
