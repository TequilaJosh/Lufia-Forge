using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// Places on the map, picked on the stage instead of from a list: a preset point (the one on the clicked tile, or a new
/// one made there), a tile (x and y together), a door or an exit (the one on the clicked tile), an area (dragged). The
/// picked step's places are marked on the stage and the view goes there.
/// </summary>
public partial class L2ScriptEditor
{
    [ObservableProperty] private bool _isPickingPlace;
    [ObservableProperty] private string _placeInfo = "";
    private L2FieldVm? _placeField;
    private (int X, int Y)? _placeAnchor;
    private (int X1, int Y1, int X2, int Y2) _placeRect;

    /// <summary>A field that names a place of this map (tile copies have their own tool).</summary>
    public static bool IsPlaceField(L2Step s, L2ParamDef p) => !s.IsText &&
        (p.Kind is L2Kind.Point or L2Kind.Door or L2Kind.Exit or L2Kind.Who or L2Kind.WhoOrAll || p.Kind == L2Kind.Area && s.Op is not (0x43 or 0x87 or 0xB4) || p.Kind == L2Kind.Number && p.Label == "x");

    public void StartPickPlace(L2FieldVm f)
    {
        if (_rom == null || _script == null || !CanEdit || !IsPlaceField(f.Step, f.Def)) return;
        if (IsDrawingPath) PathCancel();
        if (IsChangingMap) ChangeCancel();
        _placeField = f; _placeAnchor = null;
        IsPickingPlace = true;
        PlaceInfo = f.Def.Kind switch
        {
            L2Kind.Point => "Click the tile on the stage (the point there is used, or a new point is made on it).",
            L2Kind.Door => "Click the door on the stage.",
            L2Kind.Exit => "Click the exit on the stage.",
            L2Kind.Area => "Drag over the area on the stage.",
            L2Kind.Who or L2Kind.WhoOrAll => "Click the character on the stage (as it stands before this step).",
            _ => "Click the tile on the stage.",
        } + " Right-click or Esc: stop.";
        ShowStage();
    }

    [RelayCommand]
    public void PlaceCancel()
    {
        if (!IsPickingPlace) return;
        IsPickingPlace = false; _placeField = null;
        MarkPlaces();
    }

    public void PlaceDown(int x, int y)
    {
        if (!IsPickingPlace || _placeField == null) return;
        if (_placeField.Def.Kind == L2Kind.Area) { _placeAnchor = (x, y); _placeRect = (x, y, x + 1, y + 1); DrawPlaceRect(); return; }
        PickAt(x, y);
    }

    public void PlaceMove(int x, int y, bool pressed)
    {
        if (!IsPickingPlace || _placeAnchor is not { } a || !pressed) return;
        _placeRect = (Math.Min(a.X, x), Math.Min(a.Y, y), Math.Max(a.X, x) + 1, Math.Max(a.Y, y) + 1);
        DrawPlaceRect();
    }

    public void PlaceUp(int x, int y)
    {
        if (!IsPickingPlace || _placeAnchor == null || _placeField == null || _rom == null || _main == null) return;
        _placeAnchor = null;
        try
        {
            var edit = new L2MapEdit(_rom, _map);
            int id = AreaFor(edit, _placeRect.X1, _placeRect.Y1, _placeRect.X2, _placeRect.Y2);
            edit.Apply(_rom);
            _main.NotifyRomModified($"Lufia II: map {_map:X2} area {id}");
            ReadPlaces();
            Finish(b => _placeField.Def.Set(b, id), $"area {id} ({_placeRect.X1}, {_placeRect.Y1})-({_placeRect.X2 - 1}, {_placeRect.Y2 - 1})");
        }
        catch (Exception ex) { PlaceInfo = "Not written: " + ex.Message; }
    }

    private void DrawPlaceRect()
    {
        PuzzleMarks.Clear();
        var (x1, y1, x2, y2) = _placeRect;
        PuzzleMarks.Add(new L2StageMark { Left = x1 * 16, Top = y1 * 16, Width = (x2 - x1) * 16, Height = (y2 - y1) * 16, Label = $"{x2 - x1} x {y2 - y1}" });
    }

    private void PickAt(int x, int y)
    {
        var f = _placeField!;
        var p = f.Def;
        var s = f.Step;
        try
        {
            switch (p.Kind)
            {
                case L2Kind.Point:
                {
                    var edit = new L2MapEdit(_rom!, _map);
                    var here = edit.Records(L2MapEdit.Points).FirstOrDefault(r => r.Bytes[1] == x && r.Bytes[2] == y);
                    int id;
                    bool made = here.Bytes == null;
                    if (!made) id = here.Bytes![0];
                    else
                    {
                        id = edit.NextPointId();
                        if (id > 0xFF) { PlaceInfo = "The map has no free point number."; return; }
                        edit.AddRecord(L2MapEdit.Points, new[] { (byte)id, (byte)x, (byte)y });
                        edit.Apply(_rom!);
                        _main!.NotifyRomModified($"Lufia II: map {_map:X2} point {id}");
                        ReadPlaces();
                    }
                    Finish(b => p.Set(b, id), $"point {id} ({x}, {y})" + (made ? ", a new point" : ""));
                    break;
                }
                case L2Kind.Who or L2Kind.WhoOrAll:
                {
                    // the character standing there as the stage shows it before this step (the leader if it's there)
                    int i = _script!.Steps.IndexOf(s);
                    var st = (i >= 0 ? Before(i) : null) ?? _start;
                    var who = st?.Actors.Values.Where(a => a.Placed && (a.Visible || a.Id == 0) && a.X == x && (a.Y == y || a.Y == y + 1))
                        .OrderBy(a => a.Y == y ? 0 : 1).ThenBy(a => a.Id == 0 ? 1 : 0).FirstOrDefault();
                    if (who == null) { PlaceInfo = $"Nobody stands at ({x}, {y}) before this step: click a character (the stage shows them as they are at this step)."; return; }
                    Finish(b => p.Set(b, who.Id), L2EventCommands.Cap(Name(who.Id)));
                    break;
                }
                case L2Kind.Door:
                {
                    var door = L2MapInfo.Records(_rom!, _map, L2MapInfo.Doors, 15).Select(r => r.Bytes)
                        .Where(b => Math.Abs(b[1] - x) <= 1 && Math.Abs(b[2] - y) <= 1).OrderBy(b => Math.Abs(b[1] - x) + Math.Abs(b[2] - y)).FirstOrDefault();
                    if (door == null) { PlaceInfo = $"There is no door at ({x}, {y}): click a door (the Maps page adds doors)."; return; }
                    Finish(b => p.Set(b, door[0]), $"door {door[0]} ({door[1]}, {door[2]})");
                    break;
                }
                case L2Kind.Exit:
                {
                    var exit = L2MapInfo.Records(_rom!, _map, L2MapInfo.Exits, 9).Select(r => r.Bytes)
                        .FirstOrDefault(b => x >= b[1] && x < Math.Max(b[3], b[1] + 1) && y >= b[2] && y < Math.Max(b[4], b[2] + 1));
                    if (exit == null) { PlaceInfo = $"There is no exit at ({x}, {y}): click an exit (the Maps page adds exits)."; return; }
                    Finish(b => p.Set(b, exit[0]), $"exit {exit[0]}");
                    break;
                }
                default:
                {
                    // x: the "y" field next to it is set too
                    var yField = L2EventCommands.Find(s.Op)?.FieldsFor(s.Bytes).FirstOrDefault(q => q.Label == "y");
                    Finish(b => { p.Set(b, x); yField?.Set(b, y); }, $"({x}, {y})");
                    break;
                }
            }
        }
        catch (Exception ex) { PlaceInfo = "Not written: " + ex.Message; }
    }

    private void Finish(Action<byte[]> edit, string what)
    {
        var s = _placeField!.Step;
        IsPickingPlace = false; _placeField = null;
        Remember();
        var b = (byte[])s.Bytes.Clone();
        edit(b);
        s.Bytes = b;
        Changed(true);
        int i = _script!.Steps.IndexOf(s);
        StageInfo = $"Step {i + 1} now uses {what}.";
        if (i >= 0) { SelectedLine = -1; SelectedLine = i; }
    }

    /// <summary>A "Go to another map" arrival point picked on that map (the window of the Maps page's exits): the point on
    /// the picked block is used, or a point is made there; a map picked in the window becomes the step's map.</summary>
    public void PickArrival(L2FieldVm f, int map, int x, int y)
    {
        if (_rom == null || _main == null || f.Def.Kind != L2Kind.ArrivalPoint || map < 2) return;
        try
        {
            var edit = new L2MapEdit(_rom, map);
            var here = edit.Records(L2MapEdit.Points).FirstOrDefault(r => r.Bytes[1] == x && r.Bytes[2] == y);
            int id;
            bool made = here.Bytes == null;
            if (!made) id = here.Bytes![0];
            else
            {
                id = edit.NextPointId();
                if (id > 0xFF) { StageInfo = $"Map {map:X2} has no free point number."; return; }
                edit.AddRecord(L2MapEdit.Points, new[] { (byte)id, (byte)x, (byte)y });
                edit.Apply(_rom);
                _main.NotifyRomModified($"Lufia II: map {map:X2} point {id}");
            }
            var s = f.Step;
            var mapField = L2EventCommands.Find(s.Op)?.FieldsFor(s.Bytes).FirstOrDefault(q => q.Kind == L2Kind.Map);
            _placeField = f;
            _choices.Clear();
            Finish(b => { mapField?.Set(b, map); f.Def.Set(b, id); }, $"map {map:X2} point {id} ({x}, {y})" + (made ? ", a new point" : ""));
        }
        catch (Exception ex) { StageInfo = "Not written: " + ex.Message; }
    }

    /// <summary>The map's points and areas read again (after one was made), for the fields' lists.</summary>
    private void ReadPlaces()
    {
        _points = L2MapInfo.Records(_rom!, _map, L2MapInfo.Points, 3).Select(r => ((int)r.Bytes[0], $"point {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})")).ToList();
        _areas = L2MapInfo.Records(_rom!, _map, L2MapInfo.Areas, 5).Select(r => ((int)r.Bytes[0], $"area {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})-({r.Bytes[3] - 1},{r.Bytes[4] - 1})")).ToList();
        _choices.Clear();
        ReadPointChoices();
    }

    /// <summary>The places the picked step names, marked on the stage (points, tiles, doors, exits, areas).</summary>
    private void MarkPlaces()
    {
        if (_rom == null || _script == null || IsPuzzle || SelectedLine < 0 || SelectedLine >= _script.Steps.Count) return;
        var s = _script.Steps[SelectedLine];
        var def = s.IsText ? null : L2EventCommands.Find(s.Op);
        if (def == null || s.Op is 0x43 or 0x87 or 0xB4) return;
        Dictionary<int, byte[]>? points = null, areas = null;
        foreach (var p in def.FieldsFor(s.Bytes))
        {
            if (!IsPlaceField(s, p)) continue;
            int v;
            try { v = p.Get(s.Bytes); } catch (Exception) { continue; }
            string label = char.ToUpperInvariant(p.Label[0]) + p.Label[1..];
            switch (p.Kind)
            {
                case L2Kind.Point:
                    points ??= L2MapInfo.Records(_rom, _map, L2MapInfo.Points, 3).GroupBy(r => (int)r.Bytes[0]).ToDictionary(g => g.Key, g => g.First().Bytes);
                    if (points.TryGetValue(v, out var pb)) PuzzleMarks.Add(new L2StageMark { Left = pb[1] * 16, Top = pb[2] * 16, Width = 16, Height = 16, Label = $"{label} {v}" });
                    break;
                case L2Kind.Area:
                    areas ??= L2MapInfo.Records(_rom, _map, L2MapInfo.Areas, 5).GroupBy(r => (int)r.Bytes[0]).ToDictionary(g => g.Key, g => g.First().Bytes);
                    if (areas.TryGetValue(v, out var ab)) PuzzleMarks.Add(new L2StageMark { Left = ab[1] * 16, Top = ab[2] * 16, Width = (ab[3] - ab[1]) * 16, Height = (ab[4] - ab[2]) * 16, Label = $"{label} {v}" });
                    break;
                case L2Kind.Who or L2Kind.WhoOrAll:
                    var bs = Before(SelectedLine) ?? _start;
                    if (v != 0xFF && bs != null && bs.Actors.TryGetValue(v, out var wa) && wa.Placed && v != 0)
                        PuzzleMarks.Add(new L2StageMark { Left = wa.X * 16, Top = wa.Y * 16, Width = 16, Height = 16, Label = L2EventCommands.Cap(Name(v)) });
                    break;
                case L2Kind.Door:
                    var d = L2MapInfo.Records(_rom, _map, L2MapInfo.Doors, 15).Select(r => r.Bytes).FirstOrDefault(b => b[0] == v);
                    if (d != null) PuzzleMarks.Add(new L2StageMark { Left = d[1] * 16, Top = d[2] * 16, Width = 16, Height = 16, Label = $"Door {v}" });
                    break;
                case L2Kind.Exit:
                    var e = L2MapInfo.Records(_rom, _map, L2MapInfo.Exits, 9).Select(r => r.Bytes).FirstOrDefault(b => b[0] == v);
                    if (e != null) PuzzleMarks.Add(new L2StageMark { Left = e[1] * 16, Top = e[2] * 16, Width = Math.Max(1, e[3] - e[1]) * 16, Height = Math.Max(1, e[4] - e[2]) * 16, Label = $"Exit {v}" });
                    break;
                default:
                    var yf = def.FieldsFor(s.Bytes).FirstOrDefault(q => q.Label == "y");
                    if (yf != null) PuzzleMarks.Add(new L2StageMark { Left = v * 16, Top = yf.Get(s.Bytes) * 16, Width = 16, Height = 16, Label = $"({v}, {yf.Get(s.Bytes)})" });
                    break;
            }
        }
    }
}
