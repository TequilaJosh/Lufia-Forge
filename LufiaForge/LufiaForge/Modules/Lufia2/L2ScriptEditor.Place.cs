using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// places on the map, picked on the stage instead of from a list: a preset point (the one on the clicked tile, or a new
// one made there), a tile (x and y together), a door or an exit (the one on the clicked tile), an area (dragged). the
// picked step's places are marked on the stage and the view goes there
public partial class L2ScriptEditor
{
    [ObservableProperty] private bool _isPickingPlace;
    [ObservableProperty] private string _placeInfo = "";
    private L2FieldVm? _placeField;
    private (int X, int Y)? _placeAnchor;
    private (int X1, int Y1, int X2, int Y2) _placeRect;

    // a field that names a place of this map (tile copies have their own tool)
    public static bool IsPlaceField(L2Step step, L2ParamDef paramDef) => !step.IsText &&
        (paramDef.Kind is L2Kind.Point or L2Kind.Door or L2Kind.Exit or L2Kind.Who or L2Kind.WhoOrAll || paramDef.Kind == L2Kind.Area && step.Op is not (0x43 or 0x87 or 0xB4) || paramDef.Kind == L2Kind.Number && paramDef.Label == "x");

    public void StartPickPlace(L2FieldVm field)
    {
        if (_rom == null || _script == null || !CanEdit || !IsPlaceField(field.Step, field.Def)) return;
        if (IsDrawingPath) PathCancel();
        if (IsChangingMap) ChangeCancel();
        _placeField = field; _placeAnchor = null;
        IsPickingPlace = true;
        PlaceInfo = field.Def.Kind switch
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
        if (!IsPickingPlace || _placeAnchor is not { } anchor || !pressed) return;
        _placeRect = (Math.Min(anchor.X, x), Math.Min(anchor.Y, y), Math.Max(anchor.X, x) + 1, Math.Max(anchor.Y, y) + 1);
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
        var field = _placeField!;
        var paramDef = field.Def;
        var step = field.Step;
        try
        {
            switch (paramDef.Kind)
            {
                case L2Kind.Point:
                {
                    int id = PointOn(x, y, out bool made);
                    if (id < 0) { PlaceInfo = "The map has no free point number."; return; }
                    Finish(b => paramDef.Set(b, id), $"point {id} ({x}, {y})" + (made ? ", a new point" : ""));
                    break;
                }
                case L2Kind.Who or L2Kind.WhoOrAll:
                {
                    // the character standing there as the stage shows it before this step (the leader if he's there)
                    int index = _script!.Steps.IndexOf(step);
                    var state = (index >= 0 ? Before(index) : null) ?? _start;
                    var who = state?.Actors.Values.Where(a => a.Placed && (a.Visible || a.Id == 0) && a.X == x && (a.Y == y || a.Y == y + 1))
                        .OrderBy(a => a.Y == y ? 0 : 1).ThenBy(a => a.Id == 0 ? 1 : 0).FirstOrDefault();
                    if (who == null) { PlaceInfo = $"Nobody stands at ({x}, {y}) before this step: click a character (the stage shows them as they are at this step)."; return; }
                    Finish(b => paramDef.Set(b, who.Id), L2EventCommands.Cap(Name(who.Id)));
                    break;
                }
                case L2Kind.Door:
                {
                    var door = L2MapInfo.Records(_rom!, _map, L2MapInfo.Doors, 15).Select(r => r.Bytes)
                        .Where(b => Math.Abs(b[1] - x) <= 1 && Math.Abs(b[2] - y) <= 1).OrderBy(b => Math.Abs(b[1] - x) + Math.Abs(b[2] - y)).FirstOrDefault();
                    if (door == null) { PlaceInfo = $"There is no door at ({x}, {y}): click a door (the Maps page adds doors)."; return; }
                    Finish(b => paramDef.Set(b, door[0]), $"door {door[0]} ({door[1]}, {door[2]})");
                    break;
                }
                case L2Kind.Exit:
                {
                    var exit = L2MapInfo.Records(_rom!, _map, L2MapInfo.Exits, 9).Select(r => r.Bytes)
                        .FirstOrDefault(b => x >= b[1] && x < Math.Max(b[3], b[1] + 1) && y >= b[2] && y < Math.Max(b[4], b[2] + 1));
                    if (exit == null) { PlaceInfo = $"There is no exit at ({x}, {y}): click an exit (the Maps page adds exits)."; return; }
                    Finish(b => paramDef.Set(b, exit[0]), $"exit {exit[0]}");
                    break;
                }
                default:
                {
                    // x, so the "y" field next to it gets set too
                    var yField = L2EventCommands.Find(step.Op)?.FieldsFor(step.Bytes).FirstOrDefault(q => q.Label == "y");
                    Finish(b => { paramDef.Set(b, x); yField?.Set(b, y); }, $"({x}, {y})");
                    break;
                }
            }
        }
        catch (Exception ex) { PlaceInfo = "Not written: " + ex.Message; }
    }

    // the map's point on tile (x, y), made if there's none (-1 = no free number)
    private int PointOn(int x, int y, out bool made)
    {
        var edit = new L2MapEdit(_rom!, _map);
        var here = edit.Records(L2MapEdit.Points).FirstOrDefault(r => r.Bytes[1] == x && r.Bytes[2] == y);
        made = here.Bytes == null;
        if (!made) return here.Bytes![0];
        int id = edit.NextPointId();
        if (id > 0xFF) return -1;
        edit.AddRecord(L2MapEdit.Points, new[] { (byte)id, (byte)x, (byte)y });
        edit.Apply(_rom!);
        _main!.NotifyRomModified($"Lufia II: map {_map:X2} point {id}");
        ReadPlaces();
        return id;
    }

    // the game's warp sparkles on a point: the steps of a ring of sparkles that turns on one spot and goes, like the
    // Sinistrals' warp plays it (Shrine of Daos 1F, 10 sparkles 120 pixels out). the ring appears, tilts one way and the
    // other, shrinks and is removed, and the event waits for it
    public static L2Step[] SparkleSteps(int ring, int point) => RingSteps(new byte[] { 0x8D, (byte)ring, (byte)point, 0x0A, 0x78, 0x40, 0x00 });

    // sparkles turning on tile (x, y) (a point there is used or made), added at step at (-1 = after the picked one)
    public void AddSparkles(int x, int y, int at = -1)
    {
        if (_rom == null || _main == null || _script == null || !CanEdit) return;
        try
        {
            int point = PointOn(x, y, out bool made);
            if (point < 0) { StageInfo = "The map has no free point number."; return; }
            // a ring number no other step of the event uses
            Insert(at, SparkleSteps(FreeRing(), point));
            StageInfo = $"Sparkles on ({x}, {y}) ({(made ? "a new point " : "point ")}{point}): as in the game's warps, the ring appears, turns, closes in and goes, and the event waits for it.";
        }
        catch (Exception ex) { StageInfo = "Not added: " + ex.Message; }
    }

    private void Finish(Action<byte[]> edit, string what)
    {
        var step = _placeField!.Step;
        IsPickingPlace = false; _placeField = null;
        Remember();
        var bytes = (byte[])step.Bytes.Clone();
        edit(bytes);
        step.Bytes = bytes;
        Changed(true);
        int index = _script!.Steps.IndexOf(step);
        StageInfo = $"Step {index + 1} now uses {what}.";
        if (index >= 0) { SelectedLine = -1; SelectedLine = index; }
    }

    // a "Go to another map" arrival point picked on that map (the window of the Maps page's exits). the point on
    // the picked block is used or a point gets made there, and a map picked in the window becomes the step's map
    public void PickArrival(L2FieldVm field, int map, int x, int y)
    {
        if (_rom == null || _main == null || field.Def.Kind != L2Kind.ArrivalPoint || map < 2) return;
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
            var step = field.Step;
            var mapField = L2EventCommands.Find(step.Op)?.FieldsFor(step.Bytes).FirstOrDefault(q => q.Kind == L2Kind.Map);
            _placeField = field;
            _choices.Clear();
            // (a map picked in the window keeps a scene that map has)
            Finish(b => { mapField?.Set(b, map); field.Def.Set(b, id); FitArrivalBytes(b); }, $"map {map:X2} point {id} ({x}, {y})" + (made ? ", a new point" : ""));
        }
        catch (Exception ex) { StageInfo = "Not written: " + ex.Message; }
    }

    // reads the map's points and areas again (after one was made), for the fields' lists
    private void ReadPlaces()
    {
        _points = L2MapInfo.Records(_rom!, _map, L2MapInfo.Points, 3).Select(r => ((int)r.Bytes[0], $"point {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})")).ToList();
        _areas = L2MapInfo.Records(_rom!, _map, L2MapInfo.Areas, 5).Select(r => ((int)r.Bytes[0], $"area {r.Bytes[0]} ({r.Bytes[1]},{r.Bytes[2]})-({r.Bytes[3] - 1},{r.Bytes[4] - 1})")).ToList();
        _choices.Clear();
        ReadPointChoices();
    }

    // the places the picked step names, marked on the stage (points, tiles, doors, exits, areas)
    private void MarkPlaces()
    {
        if (_rom == null || _script == null || IsPuzzle || SelectedLine < 0 || SelectedLine >= _script.Steps.Count) return;
        var step = _script.Steps[SelectedLine];
        var def = step.IsText ? null : L2EventCommands.Find(step.Op);
        if (def == null || step.Op is 0x43 or 0x87 or 0xB4) return;
        Dictionary<int, byte[]>? points = null, areas = null;
        foreach (var paramDef in def.FieldsFor(step.Bytes))
        {
            if (!IsPlaceField(step, paramDef)) continue;
            int value;
            try { value = paramDef.Get(step.Bytes); } catch (Exception) { continue; }
            string label = char.ToUpperInvariant(paramDef.Label[0]) + paramDef.Label[1..];
            switch (paramDef.Kind)
            {
                case L2Kind.Point:
                    points ??= L2MapInfo.Records(_rom, _map, L2MapInfo.Points, 3).GroupBy(r => (int)r.Bytes[0]).ToDictionary(g => g.Key, g => g.First().Bytes);
                    if (points.TryGetValue(value, out var pointRecord)) PuzzleMarks.Add(new L2StageMark { Left = pointRecord[1] * 16, Top = pointRecord[2] * 16, Width = 16, Height = 16, Label = $"{label} {value}" });
                    break;
                case L2Kind.Area:
                    areas ??= L2MapInfo.Records(_rom, _map, L2MapInfo.Areas, 5).GroupBy(r => (int)r.Bytes[0]).ToDictionary(g => g.Key, g => g.First().Bytes);
                    if (areas.TryGetValue(value, out var areaRecord)) PuzzleMarks.Add(new L2StageMark { Left = areaRecord[1] * 16, Top = areaRecord[2] * 16, Width = (areaRecord[3] - areaRecord[1]) * 16, Height = (areaRecord[4] - areaRecord[2]) * 16, Label = $"{label} {value}" });
                    break;
                case L2Kind.Who or L2Kind.WhoOrAll:
                    var state = Before(SelectedLine) ?? _start;
                    if (value != 0xFF && state != null && state.Actors.TryGetValue(value, out var actor) && actor.Placed && value != 0)
                        PuzzleMarks.Add(new L2StageMark { Left = actor.X * 16, Top = actor.Y * 16, Width = 16, Height = 16, Label = L2EventCommands.Cap(Name(value)) });
                    break;
                case L2Kind.Door:
                    var door = L2MapInfo.Records(_rom, _map, L2MapInfo.Doors, 15).Select(r => r.Bytes).FirstOrDefault(b => b[0] == value);
                    if (door != null) PuzzleMarks.Add(new L2StageMark { Left = door[1] * 16, Top = door[2] * 16, Width = 16, Height = 16, Label = $"Door {value}" });
                    break;
                case L2Kind.Exit:
                    var exit = L2MapInfo.Records(_rom, _map, L2MapInfo.Exits, 9).Select(r => r.Bytes).FirstOrDefault(b => b[0] == value);
                    if (exit != null) PuzzleMarks.Add(new L2StageMark { Left = exit[1] * 16, Top = exit[2] * 16, Width = Math.Max(1, exit[3] - exit[1]) * 16, Height = Math.Max(1, exit[4] - exit[2]) * 16, Label = $"Exit {value}" });
                    break;
                default:
                    var yField = def.FieldsFor(step.Bytes).FirstOrDefault(q => q.Label == "y");
                    if (yField != null) PuzzleMarks.Add(new L2StageMark { Left = value * 16, Top = yField.Get(step.Bytes) * 16, Width = 16, Height = 16, Label = $"({value}, {yField.Get(step.Bytes)})" });
                    break;
            }
        }
    }
}
