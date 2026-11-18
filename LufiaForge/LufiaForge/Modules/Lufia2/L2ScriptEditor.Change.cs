using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// "Change the map here": an event step that copies map tiles (43, both layers and the walls) from one place of the
// map onto another. on the stage you drag over the place that changes, then click where its new look is (a part of the
// map the player doesn't see), or pick a preset and click where to keep it. the areas (map info list +14) get made
// as needed and the step is added, or the picked step's areas are changed. the game puts the "from" area, at its
// size, with its top-left on the "onto" area's first tile ($80:B76A)
public partial class L2ScriptEditor
{
    [ObservableProperty] private bool _isChangingMap;
    [ObservableProperty] private string _changeInfo = "";

    private int _changeStep;                     // 1 drag where it changes, 2 click the new look, 3 click where to keep the preset
    private (int X, int Y)? _dragAnchor;
    private (int X1, int Y1, int X2, int Y2) _changeArea;
    private L2Presets.Preset? _changePreset;
    private L2Step? _changeEditStep;
    private int _changeInsertAt;

    // from the stage's menu or button: a new "change the map" step below the picked one
    [RelayCommand]
    public void StartChangeMap()
    {
        if (_rom == null || _script == null || !CanEdit || _event?.PuzzleOps != null) return;
        BeginChange(null);
    }

    // from a tile copy card (43, 87, B4): pick its areas again on the stage
    public void EditChange(L2FieldVm field)
    {
        if (_rom == null || _script == null || !CanEdit) return;
        if (field.Step.IsText || field.Step.Op is not (0x43 or 0x87 or 0xB4)) return;
        BeginChange(field.Step);
    }

    private void BeginChange(L2Step? edit)
    {
        if (IsDrawingPath) PathCancel();
        _picked = -1;
        _changeEditStep = edit; _changeInsertAt = InsertIndex; _dragAnchor = null; _changePreset = null;
        _changeStep = 1;
        IsChangingMap = true;
        PuzzleMarks.Clear();
        ChangeInfo = "Drag over the part of the map that changes (Esc or right-click: stop).";
        ShowStage();
    }

    [RelayCommand]
    public void ChangeCancel()
    {
        if (!IsChangingMap) return;
        IsChangingMap = false; _changeStep = 0; PuzzleMarks.Clear();
        StageInfo = "The map change wasn't added.";
    }

    // instead of a place on the map, a preset (of this map's graphics) kept on a part of the map the player
    // doesn't see
    public string? ChangeWithPreset(L2Presets.Preset preset)
    {
        if (!IsChangingMap || _changeStep != 2 || _rom == null) return null;
        int blockSet = _rom.ReadByte(L2Maps.BlockSetAt(_rom, _map)), tileSet = _rom.ReadByte(L2Maps.TileSetAt(_rom, _map));
        if (preset.BlockSet != blockSet || preset.TileSet != tileSet)
            return $"\"{preset.Name}\" was made with other graphics: place it once on the Maps page (it brings its blocks into this map's), then pick that place here.";
        _changePreset = preset;
        _changeArea = (_changeArea.X1, _changeArea.Y1, _changeArea.X1 + preset.W, _changeArea.Y1 + preset.H);
        _changeStep = 3;
        ChangeInfo = $"Click where to keep \"{preset.Name}\" ({preset.W} x {preset.H}) on the map: a part the player never sees (the step copies it from there).";
        DrawChange(null);
        return null;
    }

    public void ChangeDown(int x, int y)
    {
        if (!IsChangingMap) return;
        switch (_changeStep)
        {
            case 1: _dragAnchor = (x, y); _changeArea = (x, y, x + 1, y + 1); DrawChange(null); break;
            case 2: case 3: FinishChange(x, y); break;
        }
    }

    public void ChangeMove(int x, int y, bool pressed)
    {
        if (!IsChangingMap) return;
        if (_changeStep == 1 && _dragAnchor is { } anchor && pressed)
        {
            _changeArea = (Math.Min(anchor.X, x), Math.Min(anchor.Y, y), Math.Max(anchor.X, x) + 1, Math.Max(anchor.Y, y) + 1);
            DrawChange(null);
        }
        else if (_changeStep is 2 or 3) DrawChange((x, y));
    }

    public void ChangeUp(int x, int y)
    {
        if (!IsChangingMap || _changeStep != 1 || _dragAnchor == null) return;
        _dragAnchor = null;
        int w = _changeArea.X2 - _changeArea.X1, h = _changeArea.Y2 - _changeArea.Y1;
        _changeStep = 2;
        ChangeInfo = $"Now click the top-left of the new look ({w} x {h} blocks, painted on a part of the map the player doesn't see), " +
                     "or use a preset.";
        DrawChange(null);
    }

    // the place that changes (blue) and, under the mouse, where its new look would come from (orange)
    private void DrawChange((int X, int Y)? lookAt)
    {
        PuzzleMarks.Clear();
        var (x1, y1, x2, y2) = _changeArea;
        PuzzleMarks.Add(new L2StageMark { Left = x1 * 16, Top = y1 * 16, Width = (x2 - x1) * 16, Height = (y2 - y1) * 16, Label = $"changes ({x2 - x1} x {y2 - y1})" });
        if (lookAt is { } look)
            PuzzleMarks.Add(new L2StageMark
            {
                Left = look.X * 16, Top = look.Y * 16, Width = (x2 - x1) * 16, Height = (y2 - y1) * 16, IsSource = true,
                Label = _changeStep == 3 ? "keep the preset here" : "new look",
            });
    }

    private void FinishChange(int lookX, int lookY)
    {
        if (_rom == null || _main == null || _script == null) return;
        int w = _changeArea.X2 - _changeArea.X1, h = _changeArea.Y2 - _changeArea.Y1;
        try
        {
            var edit = new L2MapEdit(_rom, _map);
            if (lookX + w > edit.Width || lookY + h > edit.Height) { ChangeInfo = $"That doesn't fit on the map: the new look is {w} x {h} blocks."; return; }
            if (lookX < _changeArea.X2 && lookX + w > _changeArea.X1 && lookY < _changeArea.Y2 && lookY + h > _changeArea.Y1)
            { ChangeInfo = "The new look can't overlap the place that changes: pick another part of the map."; return; }
            if (_changePreset is { } preset)
            {
                // the preset's blocks, walls with them, go on the place picked to keep it
                var ground = edit.Map.Layers[0]; var upper = edit.Map.Layers.Length > 1 ? edit.Map.Layers[1] : null;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int cell = (lookY + y) * ground.Width + lookX + x, presetCell = y * preset.W + x;
                        ground.Blocks[cell] = preset.Ground[presetCell];
                        if (upper != null) { upper.Blocks[cell] = preset.Upper[presetCell]; upper.Flags[cell] = preset.Flags[presetCell]; }
                    }
            }
            int ontoArea = AreaFor(edit, _changeArea.X1, _changeArea.Y1, _changeArea.X2, _changeArea.Y2), lookArea = AreaFor(edit, lookX, lookY, lookX + w, lookY + h);
            edit.Apply(_rom);
            _main.NotifyRomModified($"Lufia II: map {_map:X2} areas {ontoArea} and {lookArea}" + (_changePreset != null ? " and a preset" : ""));
            if (_changePreset != null) ReadMap();
            _areas = L2MapInfo.Records(_rom, _map, L2MapInfo.Areas, 5).Select(r => ((int)r.Bytes[0], $"area {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})-({r.Bytes[3] - 1},{r.Bytes[4] - 1})")).ToList();
            _choices.Clear();
            IsChangingMap = false; _changeStep = 0; PuzzleMarks.Clear();
            if (_changeEditStep != null && _script.Steps.Contains(_changeEditStep))
            {
                Remember();
                var bytes = (byte[])_changeEditStep.Bytes.Clone(); bytes[1] = (byte)ontoArea; bytes[2] = (byte)lookArea; _changeEditStep.Bytes = bytes;
                Changed(true);
            }
            else Insert(_changeInsertAt, new L2Step { Bytes = new byte[] { 0x43, (byte)ontoArea, (byte)lookArea } });
            StageInfo = $"From this step the map shows the look of ({lookX}, {lookY}) at ({_changeArea.X1}, {_changeArea.Y1}), walls too (areas {lookArea} and {ontoArea}). " +
                        "The change stays until the map loads again; for good, set a story flag and do it again in the map's \"when shown\" event.";
            Status = $"Areas {ontoArea} and {lookArea} written to map {_map:X2}.";
        }
        catch (Exception ex) { ChangeInfo = "Not written: " + ex.Message; }
    }

    // the area with exactly this rectangle, or a new one
    private static int AreaFor(L2MapEdit edit, int x1, int y1, int x2, int y2)
    {
        var records = edit.Records(L2MapInfo.Areas);
        var same = records.FirstOrDefault(r => r.Bytes[1] == x1 && r.Bytes[2] == y1 && r.Bytes[3] == x2 && r.Bytes[4] == y2);
        if (same.Bytes != null) return same.Bytes[0];
        int id = NextAreaId(edit);
        if (id > 0xFF) throw new InvalidOperationException("the map has no free area number.");
        edit.AddRecord(L2MapInfo.Areas, new[] { (byte)id, (byte)x1, (byte)y1, (byte)x2, (byte)y2 });
        return id;
    }
}
