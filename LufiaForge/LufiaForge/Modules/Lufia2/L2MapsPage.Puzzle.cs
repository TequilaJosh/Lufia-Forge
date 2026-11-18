using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// the Maps page's puzzle tool (U): a tile change that something sets off (a wall a bomb blasts open, a passage cut
// with the sword, a door opened with the action button). three steps on the map:
// - drag over what changes (the wall and the floor in front of it, walls are flags on both)
// - click the top-left of the look it changes to (painted somewhere else on the map)
// - click the spot that sets it off
// L2PuzzleBuild.AddTileChange writes it
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

    // 0 = drag what changes, 1 = click the look it changes to, 2 = click the spot that sets it off
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

    // a press with the puzzle tool
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

    // the step 1 drag ended, so what changes is picked
    private void PuzzleUp()
    {
        if (AreaRect is not { } area) return;
        _puzzleTarget = area;
        _puzzleStep = 1;
        OnPropertyChanged(nameof(ToolHint));
    }

    private void AddPuzzle(int triggerX, int triggerY)
    {
        if (_edit == null || _rom == null || _main == null) return;
        var (kind, _) = PuzzleTriggers[Math.Clamp(PuzzleTrigger, 0, PuzzleTriggers.Length - 1)];
        // write the map's pending edits first (the puzzle goes into the ROM right away, with the map as it is then)
        var why = WritePendingEdits();
        if (why != null) { Status = "Not added: the map's other changes couldn't be written: " + why; return; }
        int mapId = _edit.MapId;
        var target = _puzzleTarget;
        // a builder that fails partway (no dungeon controls on the map, its info block full) has already written
        // records, so the ROM goes back to how it was
        var romBefore = _rom.ToArray();
        try
        {
            string done = L2PuzzleBuild.AddTileChange(_rom, mapId, kind, triggerX, triggerY, target.X1, target.Y1, target.X2 - target.X1, target.Y2 - target.Y1, _puzzleFrom.X, _puzzleFrom.Y);
            _main.NotifyRomModified($"Lufia II: puzzle on map {mapId:X2}");
            _edit = new L2MapEdit(_rom, mapId);
            _readVersion = _rom.Version;
            _history.Clear(); _redo.Clear();
            UpdateUndoState();
            ResetPuzzle();
            Refresh();
            Status = done + " See it in Events (the map's puzzle scripts); test it in the game from there.";
        }
        catch (Exception ex) { if (!_rom.SameAs(romBefore)) _rom.ReplaceAll(romBefore); Status = "Not added: " + ex.Message; ResetPuzzle(); }
    }

    // presets (R): doors, bomb pieces, spikes

    // what the preset tool places
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

    // what opens a placed door
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
    // the clicks done so far (map blocks) and what each next click is for
    private readonly List<(int X, int Y)> _presetClicks = new();
    private (int X1, int Y1, int X2, int Y2)? _spikeRect;
    private ((int Block, byte Flags)[][] Closed, (int Block, byte Flags)[][] Open)? _presetCells;

    // the clicks the picked kind needs, in order (painted spikes start with a drag)
    private List<string> PresetSteps()
    {
        var steps = new List<string>();
        switch (CurrentKind)
        {
            case PresetKindId.Door:
                steps.Add("click where the door goes (its top-left block)");
                steps.Add("click where its open look is kept: anywhere, best where the camera never goes");
                if (DoorOpener == 1) steps.Add("click the floor plate's block (the party stepping on it, or a pushed block, opens the door)");
                if (DoorOpener == 2) steps.Add("click the lever's block (the party faces it and presses A)");
                break;
            case PresetKindId.BombPiece:
                steps.Add("click where the piece goes (its top-left block)");
                steps.Add("click where its broken look is kept: anywhere, best where the camera never goes");
                break;
            default:
                steps.Add("drag over the spikes as painted now (raised)");
                steps.Add("click the top-left of their lowered look, painted somewhere else on the map");
                if (CurrentKind != PresetKindId.SpikesPlate) steps.Add("click where a copy of the raised look is kept (it is painted there for you)");
                if (CurrentKind == PresetKindId.SpikesLever) steps.Add("click the lever's block (the party faces it and presses A)");
                if (CurrentKind == PresetKindId.SpikesPlate) steps.Add("click the floor plate's block");
                break;
        }
        return steps;
    }

    private string PresetHint
    {
        get
        {
            var steps = PresetSteps();
            int stepIndex = _presetClicks.Count + (_spikeRect != null && _presetClicks.Count == 0 ? 1 : 0);
            stepIndex = Math.Min(stepIndex, steps.Count - 1);
            return $"Preset (R), {stepIndex + 1} of {steps.Count}: {steps[stepIndex]}. The puzzle is made after the last click. Right-click starts again (Ctrl+Z takes back what was painted).";
        }
    }

    partial void OnPresetKindChanged(int value) { ResetPreset(); FillPresets(); NotifyPresetOptions(); }
    partial void OnDoorOpenerChanged(int value) { ResetPreset(); NotifyPresetOptions(); }

    private void NotifyPresetOptions()
    {
        foreach (var property in new[] { nameof(PresetListVisible), nameof(DoorOptionsVisible), nameof(KeyOptionsVisible), nameof(FlagOptionsVisible), nameof(StepOptionsVisible), nameof(ToolHint) })
            OnPropertyChanged(property);
    }

    // fills the game's presets of the picked kind (ones with this map's graphics first), plus the item and flag lists
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
        int blockSet = _edit.BlockSetIndex, tileSet = _edit.TileSetIndex, paletteSet = _edit.PaletteSetIndex;
        foreach (var preset in _gamePresets.Where(p => p.Kind == kind)
                     .OrderBy(p => p.BlockSet == blockSet && p.TileSet == tileSet && p.PaletteSet == paletteSet ? 0 : p.TileSet == tileSet ? 1 : 2).ThenBy(p => p.Map))
        {
            string fit = preset.BlockSet == blockSet && preset.TileSet == tileSet && preset.PaletteSet == paletteSet ? " (same graphics)" : preset.TileSet == tileSet ? " (same tiles)" : "";
            var (w, h, pixels) = L2PuzzlePresets.Picture(_rom, preset);
            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), w * 4);
            image.Freeze();
            PresetChoices.Add(new L2PresetChoice { Preset = preset, Label = preset.Name + fit, Image = image });
        }
        SelectedPreset = PresetChoices.FirstOrDefault();
    }

    private void ResetPreset() { _presetClicks.Clear(); _spikeRect = null; _presetCells = null; AreaRect = null; OnPropertyChanged(nameof(ToolHint)); }

    // paints cells at (x, y): both layers' blocks and flags (the walls are the upper layer's)
    private void PaintCells((int Block, byte Flags)[][] cells, int w, int h, int x, int y)
    {
        if (_edit == null) return;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                var cell = cells[j * w + i];
                for (int layer = 0; layer < _edit.Map.Layers.Length && layer < cell.Length; layer++)
                {
                    var mapLayer = _edit.Map.Layers[layer];
                    if (x + i >= mapLayer.Width || y + j >= mapLayer.Height) continue;
                    mapLayer.Blocks[(y + j) * mapLayer.Width + x + i] = cell[layer].Block;
                    mapLayer.Flags[(y + j) * mapLayer.Width + x + i] = cell[layer].Flags;
                }
            }
    }

    private (int Block, byte Flags)[][] CellsAt(int x, int y, int w, int h)
    {
        var cells = new (int, byte)[w * h][];
        for (int j = 0; j < h; j++) for (int i = 0; i < w; i++) cells[j * w + i] = _edit!.CellAt(x + i, y + j);
        return cells;
    }

    // a press with the preset tool: one more click of PresetSteps (painted spikes start with a drag)
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
        if (painted) { var spikes = _spikeRect!.Value; w = spikes.X2 - spikes.X1; h = spikes.Y2 - spikes.Y1; }
        else
        {
            if (SelectedPreset?.Preset is not { } picked) { Status = "Pick a preset below first."; return; }
            w = picked.W; h = picked.H;
        }
        int step = _presetClicks.Count + (painted ? 1 : 0);
        var steps = PresetSteps();
        bool isPiece = step <= 2 && !(painted && step == 0);   // a click that places a w x h look (not a plate or lever)
        if (isPiece && (cx + w > _edit.Width || cy + h > _edit.Height)) { Status = $"It doesn't fit there: it is {w} x {h} blocks."; return; }
        if (!painted && step == 0)
        {
            // the door or bomb piece itself
            var preset = SelectedPreset!.Preset!;
            try { _presetCells = L2PuzzlePresets.Translate(_rom, preset, _edit); }
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
            // a copy of the raised look gets painted here
            var spikes = _spikeRect!.Value;
            Record("keeping a copy of the raised spikes");
            PaintCells(CellsAt(spikes.X1, spikes.Y1, w, h), w, h, cx, cy);
            DropIfSame(); DrawMap(); UpdateUndoState();
        }
        _presetClicks.Add((cx, cy));
        if (_presetClicks.Count + (painted ? 1 : 0) < steps.Count) { OnPropertyChanged(nameof(ToolHint)); return; }
        WritePreset(w, h);
    }

    // the painted spikes drag ended
    private void PresetUp()
    {
        if (AreaRect is not { } area) return;
        _spikeRect = area;
        OnPropertyChanged(nameof(ToolHint));
    }

    // all the clicks are in: write the map, then the puzzle
    private void WritePreset(int w, int h)
    {
        if (_edit == null || _rom == null) return;
        var why = WritePendingEdits();
        if (why != null) { Status = "Not added: the map couldn't be written: " + why; ResetPreset(); return; }
        int mapId = _edit.MapId;
        var clicks = _presetClicks;
        var romBefore = _rom.ToArray();   // (put back whole if the builder fails partway)
        try
        {
            string done;
            switch (CurrentKind)
            {
                case PresetKindId.Door:
                {
                    var preset = SelectedPreset!.Preset!;
                    var (x, y) = clicks[0]; var (openX, openY) = clicks[1];
                    int tx = x + preset.TriggerX, ty = y + preset.TriggerY;
                    done = DoorOpener switch
                    {
                        1 => L2PuzzleBuild.AddPlateChange(_rom, mapId, clicks[2].X, clicks[2].Y, x, y, w, h, openX, openY),
                        2 => L2PuzzleBuild.AddTileChange(_rom, mapId, L2PuzzleBuild.Trigger.ActionButton, clicks[2].X, clicks[2].Y, x, y, w, h, openX, openY, 0x10),
                        // (the step-on area goes on the floor block just below the door, a step event on the door's
                        // own blocks never played in the game)
                        3 => L2PuzzleBuild.AddKeyDoor(_rom, mapId, tx, y + h, x, y, w, h, openX, openY, KeyItem?.Value ?? 0,
                                 (KeyItem?.Label ?? "key").Split(' ', 2).Last(), UseUpKey, L2FlagUses.Free(_rom)),
                        4 => L2PuzzleBuild.AddFlagDoor(_rom, mapId, x, y, w, h, openX, openY, DoorFlag?.Value ?? 1),
                        _ => L2PuzzleBuild.AddTileChange(_rom, mapId, L2PuzzleBuild.Trigger.ActionButton, tx, ty, x, y, w, h, openX, openY),
                    };
                    break;
                }
                case PresetKindId.BombPiece:
                {
                    var preset = SelectedPreset!.Preset!;
                    done = L2PuzzleBuild.AddTileChange(_rom, mapId, L2PuzzleBuild.Trigger.Bomb, clicks[0].X + preset.TriggerX, clicks[0].Y + preset.TriggerY, clicks[0].X, clicks[0].Y, w, h, clicks[1].X, clicks[1].Y);
                    break;
                }
                default:
                {
                    var spikes = _spikeRect!.Value;
                    done = CurrentKind switch
                    {
                        PresetKindId.SpikesLever => L2PuzzleBuild.AddLeverToggle(_rom, mapId, clicks[2].X, clicks[2].Y, spikes.X1, spikes.Y1, w, h, clicks[1].X, clicks[1].Y, clicks[0].X, clicks[0].Y),
                        PresetKindId.SpikesPlate => L2PuzzleBuild.AddPlateChange(_rom, mapId, clicks[1].X, clicks[1].Y, spikes.X1, spikes.Y1, w, h, clicks[0].X, clicks[0].Y),
                        _ => L2PuzzleBuild.AddStepTimer(_rom, mapId, spikes.X1, spikes.Y1, w, h, clicks[1].X, clicks[1].Y, clicks[0].X, clicks[0].Y, SpikeSteps),
                    };
                    break;
                }
            }
            L2FlagUses.Invalidate();
            _main?.NotifyRomModified($"Lufia II: puzzle on map {mapId:X2}");
            _edit = new L2MapEdit(_rom, mapId);
            _readVersion = _rom.Version;
            _history.Clear(); _redo.Clear();
            UpdateUndoState();
            ResetPreset();
            Refresh();
            Status = done;
        }
        catch (Exception ex) { if (!_rom.SameAs(romBefore)) _rom.ReplaceAll(romBefore); Status = "The pieces are painted, but the puzzle wasn't added: " + ex.Message; ResetPreset(); }
    }
}

// a preset in the Maps page's list: its picture (closed, then open) and where it comes from
public sealed class L2PresetChoice
{
    public L2PuzzlePresets.Preset? Preset { get; init; }
    public string Label { get; init; } = "";
    public BitmapSource? Image { get; init; }
}
