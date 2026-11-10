using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// "Change the map here": an event step that copies map tiles (43: both layers and the walls) from one place of the
/// map onto another. On the stage: drag over the place that changes, then click where its new look is (a part of the
/// map the player doesn't see), or pick a preset and click where to keep it. The areas (map info list +14) are made
/// as needed and the step is added, or the picked step's areas are changed. The game puts the "from" area, at its
/// size, with its top-left on the "onto" area's first tile ($80:B76A).
/// </summary>
public partial class L2ScriptEditor
{
    [ObservableProperty] private bool _isChangingMap;
    [ObservableProperty] private string _changeInfo = "";

    private int _chStep;                     // 1 drag where it changes, 2 click the new look, 3 click where to keep the preset
    private (int X, int Y)? _chAnchor;
    private (int X1, int Y1, int X2, int Y2) _chDst;
    private L2Presets.Preset? _chPreset;
    private L2Step? _chEdit;
    private int _chInsertAt;

    /// <summary>From the stage's menu or button: a new "change the map" step below the picked one.</summary>
    [RelayCommand]
    public void StartChangeMap()
    {
        if (_rom == null || _script == null || !CanEdit || _event?.PuzzleOps != null) return;
        BeginChange(null);
    }

    /// <summary>From a tile copy card (43, 87, B4): its areas picked again on the stage.</summary>
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
        _chEdit = edit; _chInsertAt = InsertIndex; _chAnchor = null; _chPreset = null;
        _chStep = 1;
        IsChangingMap = true;
        PuzzleMarks.Clear();
        ChangeInfo = "Drag over the part of the map that changes (Esc or right-click: stop).";
        ShowStage();
    }

    [RelayCommand]
    public void ChangeCancel()
    {
        if (!IsChangingMap) return;
        IsChangingMap = false; _chStep = 0; PuzzleMarks.Clear();
        StageInfo = "The map change wasn't added.";
    }

    /// <summary>Instead of a place on the map: a preset (of this map's graphics), kept on a part of the map the player
    /// doesn't see.</summary>
    public string? ChangeWithPreset(L2Presets.Preset p)
    {
        if (!IsChangingMap || _chStep != 2 || _rom == null) return null;
        int bs = _rom.ReadByte(L2Maps.BlockSetAt(_rom, _map)), ts = _rom.ReadByte(L2Maps.TileSetAt(_rom, _map));
        if (p.BlockSet != bs || p.TileSet != ts)
            return $"\"{p.Name}\" was made with other graphics: place it once on the Maps page (it brings its blocks into this map's), then pick that place here.";
        _chPreset = p;
        _chDst = (_chDst.X1, _chDst.Y1, _chDst.X1 + p.W, _chDst.Y1 + p.H);
        _chStep = 3;
        ChangeInfo = $"Click where to keep \"{p.Name}\" ({p.W} x {p.H}) on the map: a part the player never sees (the step copies it from there).";
        DrawChange(null);
        return null;
    }

    public void ChangeDown(int x, int y)
    {
        if (!IsChangingMap) return;
        switch (_chStep)
        {
            case 1: _chAnchor = (x, y); _chDst = (x, y, x + 1, y + 1); DrawChange(null); break;
            case 2: case 3: FinishChange(x, y); break;
        }
    }

    public void ChangeMove(int x, int y, bool pressed)
    {
        if (!IsChangingMap) return;
        if (_chStep == 1 && _chAnchor is { } a && pressed)
        {
            _chDst = (Math.Min(a.X, x), Math.Min(a.Y, y), Math.Max(a.X, x) + 1, Math.Max(a.Y, y) + 1);
            DrawChange(null);
        }
        else if (_chStep is 2 or 3) DrawChange((x, y));
    }

    public void ChangeUp(int x, int y)
    {
        if (!IsChangingMap || _chStep != 1 || _chAnchor == null) return;
        _chAnchor = null;
        int w = _chDst.X2 - _chDst.X1, h = _chDst.Y2 - _chDst.Y1;
        _chStep = 2;
        ChangeInfo = $"Now click the top-left of the new look ({w} x {h} blocks, painted on a part of the map the player doesn't see), " +
                     "or use a preset.";
        DrawChange(null);
    }

    /// <summary>The place that changes (blue) and, under the mouse, where its new look would come from (orange).</summary>
    private void DrawChange((int X, int Y)? at)
    {
        PuzzleMarks.Clear();
        var (x1, y1, x2, y2) = _chDst;
        PuzzleMarks.Add(new L2StageMark { Left = x1 * 16, Top = y1 * 16, Width = (x2 - x1) * 16, Height = (y2 - y1) * 16, Label = $"changes ({x2 - x1} x {y2 - y1})" });
        if (at is { } p)
            PuzzleMarks.Add(new L2StageMark
            {
                Left = p.X * 16, Top = p.Y * 16, Width = (x2 - x1) * 16, Height = (y2 - y1) * 16, IsSource = true,
                Label = _chStep == 3 ? "keep the preset here" : "new look",
            });
    }

    private void FinishChange(int sx, int sy)
    {
        if (_rom == null || _main == null || _script == null) return;
        int w = _chDst.X2 - _chDst.X1, h = _chDst.Y2 - _chDst.Y1;
        try
        {
            var edit = new L2MapEdit(_rom, _map);
            if (sx + w > edit.Width || sy + h > edit.Height) { ChangeInfo = $"That doesn't fit on the map: the new look is {w} x {h} blocks."; return; }
            if (sx < _chDst.X2 && sx + w > _chDst.X1 && sy < _chDst.Y2 && sy + h > _chDst.Y1)
            { ChangeInfo = "The new look can't overlap the place that changes: pick another part of the map."; return; }
            if (_chPreset is { } p)
            {
                // the preset's blocks, walls with them, on the place picked to keep it
                var l0 = edit.Map.Layers[0]; var l1 = edit.Map.Layers.Length > 1 ? edit.Map.Layers[1] : null;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = (sy + y) * l0.Width + sx + x, k = y * p.W + x;
                        l0.Blocks[i] = p.Ground[k];
                        if (l1 != null) { l1.Blocks[i] = p.Upper[k]; l1.Flags[i] = p.Flags[k]; }
                    }
            }
            int dst = AreaFor(edit, _chDst.X1, _chDst.Y1, _chDst.X2, _chDst.Y2), src = AreaFor(edit, sx, sy, sx + w, sy + h);
            edit.Apply(_rom);
            _main.NotifyRomModified($"Lufia II: map {_map:X2} areas {dst} and {src}" + (_chPreset != null ? " and a preset" : ""));
            if (_chPreset != null) ReadMap();
            _areas = L2MapInfo.Records(_rom, _map, L2MapInfo.Areas, 5).Select(r => ((int)r.Bytes[0], $"area {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})-({r.Bytes[3] - 1},{r.Bytes[4] - 1})")).ToList();
            _choices.Clear();
            IsChangingMap = false; _chStep = 0; PuzzleMarks.Clear();
            if (_chEdit != null && _script.Steps.Contains(_chEdit))
            {
                Remember();
                var b = (byte[])_chEdit.Bytes.Clone(); b[1] = (byte)dst; b[2] = (byte)src; _chEdit.Bytes = b;
                Changed(true);
            }
            else Insert(_chInsertAt, new L2Step { Bytes = new byte[] { 0x43, (byte)dst, (byte)src } });
            StageInfo = $"From this step the map shows the look of ({sx}, {sy}) at ({_chDst.X1}, {_chDst.Y1}), walls too (areas {src} and {dst}). " +
                        "The change stays until the map loads again; for good, set a story flag and do it again in the map's \"when shown\" event.";
            Status = $"Areas {dst} and {src} written to map {_map:X2}.";
        }
        catch (Exception ex) { ChangeInfo = "Not written: " + ex.Message; }
    }

    /// <summary>The area with exactly this rectangle, or a new one.</summary>
    private static int AreaFor(L2MapEdit edit, int x1, int y1, int x2, int y2)
    {
        var recs = edit.Records(L2MapInfo.Areas);
        var same = recs.FirstOrDefault(r => r.Bytes[1] == x1 && r.Bytes[2] == y1 && r.Bytes[3] == x2 && r.Bytes[4] == y2);
        if (same.Bytes != null) return same.Bytes[0];
        int id = NextAreaId(edit);
        if (id > 0xFF) throw new InvalidOperationException("the map has no free area number.");
        edit.AddRecord(L2MapInfo.Areas, new[] { (byte)id, (byte)x1, (byte)y1, (byte)x2, (byte)y2 });
        return id;
    }
}
