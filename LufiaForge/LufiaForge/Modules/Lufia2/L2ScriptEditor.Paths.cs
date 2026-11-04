using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// Drawing a path on the stage: a character is picked, each click adds a place it heads for (sideways first, then up
/// or down, as the game walks a path), Done writes the path to the map (<see cref="L2Paths"/>, the map info's +12
/// list and a walk program in the info block) and adds "A character walks a path" (33 path who), or points the step
/// being edited at it.
/// </summary>
public partial class L2ScriptEditor
{
    private int _pathWho = -1, _pathId = -1, _pathInsertAt = -1;
    private L2Step? _pathStep;
    private (int X, int Y) _pathFrom;
    private readonly List<(int X, int Y)> _pathCorners = new();

    [ObservableProperty] private bool _isDrawingPath;
    [ObservableProperty] private string _pathInfo = "";
    [ObservableProperty] private L2Option? _pathSpeed;
    [ObservableProperty] private L2Option? _pathFace;
    [ObservableProperty] private bool _pathCameraFollows;
    [ObservableProperty] private bool _pathEventGoesOn;
    [ObservableProperty] private string _pathWait = "0";

    /// <summary>The route being drawn: a mark for each tile it goes over, the places numbered.</summary>
    public ObservableCollection<L2StageMark> PathMarks { get; } = new();

    public IReadOnlyList<L2Option> PathSpeeds => Speeds;
    private static readonly L2Option[] Speeds = new[]
    {
        new L2Option(2, "Very slow"), new L2Option(4, "Slow"), new L2Option(8, "Normal"), new L2Option(12, "Quick"), new L2Option(16, "Running"),
    };

    public IReadOnlyList<L2Option> PathFaces => Faces;
    private static readonly L2Option[] Faces = new[]
    {
        new L2Option(-1, "the way it walked"), new L2Option(0, "up"), new L2Option(1, "down"), new L2Option(2, "left"), new L2Option(3, "right"),
    };

    private void ReadPaths() =>
        _paths = L2Paths.Read(_rom!, _map).Select(p => (p.Id, $"path {p.Id} (to {p.X},{p.Y})")).ToList();

    /// <summary>From the stage's menu: <paramref name="who"/> walks a new path whose first place is (x, y).</summary>
    public void StartPath(int who, int x, int y)
    {
        var st = StateForInsert();
        if (st == null || !st.Actors.TryGetValue(who, out var a) || !a.Placed) { StageInfo = $"{L2EventCommands.Cap(Name(who))} isn't on the map yet: make it appear first."; return; }
        BeginPath(who, (a.X, a.Y), -1, null, new L2Paths.Plan());
        _pathInsertAt = InsertIndex;
        if (x != a.X || y != a.Y) _pathCorners.Add((x, y));
        DrawPath();
    }

    /// <summary>From a "walks a path" card: its path drawn on the stage, to change.</summary>
    public void EditPath(L2FieldVm field)
    {
        if (_rom == null || _script == null || !CanEdit || IsPlaying) return;
        var step = field.Step;
        if (step.IsText || step.Op != 0x33 || step.Bytes.Length < 3) return;
        int index = _script.Steps.IndexOf(step);
        int who = step.Bytes[2], id = step.Bytes[1];
        var before = Before(index) ?? _start;
        if (before == null || !before.Actors.TryGetValue(who, out var a) || !a.Placed)
        {
            StageInfo = $"{L2EventCommands.Cap(Name(who))} isn't on the map before step {index + 1}, so there's nowhere to walk from: make it appear first.";
            return;
        }
        var path = L2Paths.Read(_rom, _map).FirstOrDefault(p => p.Id == id);
        var plan = path != null ? L2Paths.ToPlan(path, a.X, a.Y) : null;
        // a path other steps of the map walk too gets a new number, so they keep theirs
        bool shared = path != null && L2Script.MapScripts(_rom, _map).SelectMany(s => s.Ops)
            .Any(o => !o.IsText && o.At != step.At && o.At + 2 < _rom.Length && _rom.ReadByte(o.At) == 0x33 && _rom.ReadByte(o.At + 1) == id);
        BeginPath(who, (a.X, a.Y), path != null && plan != null && !shared ? id : -1, step, plan ?? new L2Paths.Plan());
        DrawPath();
        if (path == null) PathInfo = $"The map has no path {id}: click the places to walk to. " + PathInfo;
        else if (plan == null) PathInfo = $"Path {id} does more than walk to places (it checks the leader, walks at random or jumps), so it can't be drawn here: drawing makes a new path for this step. " + PathInfo;
        else if (shared) PathInfo = $"Other steps of this map walk path {id} too: the changes go into a new path for this step only. " + PathInfo;
    }

    private void BeginPath(int who, (int X, int Y) from, int id, L2Step? step, L2Paths.Plan plan)
    {
        _picked = -1;
        _pathWho = who; _pathFrom = from; _pathId = id; _pathStep = step;
        _pathCorners.Clear();
        _pathCorners.AddRange(plan.Corners);
        PathSpeed = Speeds.FirstOrDefault(o => o.Value == plan.Speed) ?? AddSpeed(plan.Speed);
        PathFace = Faces.FirstOrDefault(o => o.Value == plan.FaceAtEnd) ?? Faces[0];
        PathCameraFollows = plan.CameraFollows;
        PathEventGoesOn = plan.EventGoesOn;
        PathWait = plan.WaitFirst.ToString();
        IsDrawingPath = true;
        ShowStage();
    }

    private static L2Option AddSpeed(int speed) => new(speed, $"speed {speed}");

    /// <summary>A click on the stage while drawing: a place to head for (the last one again: taken back).</summary>
    public void PathClick(int x, int y)
    {
        if (!IsDrawingPath) return;
        var last = _pathCorners.Count > 0 ? _pathCorners[^1] : _pathFrom;
        if ((x, y) == last) { if (_pathCorners.Count > 0) _pathCorners.RemoveAt(_pathCorners.Count - 1); }
        else if (_pathCorners.Count >= 80) { PathInfo = "That's as many places as one path takes here."; return; }
        else _pathCorners.Add((x, y));
        DrawPath();
    }

    [RelayCommand]
    public void PathUndoCorner()
    {
        if (!IsDrawingPath || _pathCorners.Count == 0) return;
        _pathCorners.RemoveAt(_pathCorners.Count - 1);
        DrawPath();
    }

    [RelayCommand]
    public void PathCancel()
    {
        if (!IsDrawingPath) return;
        IsDrawingPath = false;
        PathMarks.Clear();
        StageInfo = "The path wasn't saved.";
        ShowStage();
    }

    [RelayCommand]
    public void PathDone()
    {
        if (!IsDrawingPath || _rom == null || _main == null || _script == null) return;
        if (_pathCorners.Count == 0) { PathInfo = "Click at least one place to walk to first."; return; }
        if (!int.TryParse(PathWait.Trim(), out int wait) || wait is < 0 or > 255) { PathInfo = "The wait before it sets off is 0 to 255 frames (60 a second)."; return; }
        var plan = new L2Paths.Plan
        {
            Speed = PathSpeed?.Value ?? L2Paths.NormalSpeed, FaceAtEnd = PathFace?.Value ?? -1, WaitFirst = wait,
            CameraFollows = PathCameraFollows, EventGoesOn = PathEventGoesOn,
        };
        plan.Corners.AddRange(_pathCorners);
        int id;
        try
        {
            var edit = new L2MapEdit(_rom, _map);
            id = _pathId >= 0 ? _pathId : edit.NextPathId();
            edit.SetPath(id, _pathCorners[0].X, _pathCorners[0].Y, plan.Flags, L2Paths.Build(plan));
            edit.Apply(_rom);
        }
        catch (Exception ex) { PathInfo = "Not saved: " + ex.Message; return; }
        _main.NotifyRomModified($"Lufia II: map {_map:X2} path");
        ReadPaths();
        IsDrawingPath = false;
        PathMarks.Clear();
        string who = L2EventCommands.Cap(Name(_pathWho));
        if (_pathStep != null && _script.Steps.Contains(_pathStep))
        {
            if (_pathStep.Bytes[1] != id)
            {
                Remember();
                var b = (byte[])_pathStep.Bytes.Clone(); b[1] = (byte)id; _pathStep.Bytes = b;
            }
            Changed(true);
            StageInfo = $"Path {id} saved to the map; step {_script.Steps.IndexOf(_pathStep) + 1} walks it.";
        }
        else
        {
            Insert(_pathInsertAt, new L2Step { Bytes = new byte[] { 0x33, (byte)id, (byte)_pathWho } });
            StageInfo = $"Path {id} saved to the map, and a step added: {who} walks it.";
        }
        StageInfo += (plan.EventGoesOn ? " The event goes on while it walks (\"Wait for a character to stop walking\" waits for it)." : " The event waits until the walk is done.") +
                     " A path doesn't go round walls or people: a step that's blocked waits until the way is free, so check the red marks in playback.";
        Status = $"Path {id} written to map {_map:X2}.";
    }

    partial void OnPathSpeedChanged(L2Option? value) => DrawPath();
    partial void OnPathFaceChanged(L2Option? value) => DrawPath();

    /// <summary>The route on the stage: each tile it walks over, the places numbered, a red mark where a wall stops it.</summary>
    private void DrawPath()
    {
        PathMarks.Clear();
        if (!IsDrawingPath) return;
        var route = L2Paths.Route(_pathFrom.X, _pathFrom.Y, _pathCorners);
        L2StageMap? map = null;
        try { map = L2StageMap.Read(_rom!, _map); } catch (Exception) { }
        string? blocked = null;
        var (px, py) = _pathFrom;
        foreach (var (x, y, d) in route)
        {
            string? hit = map?.Wall(px, py, d);
            if (hit != null && blocked == null) blocked = $"a wall or {hit} stops it at ({x}, {y}): in the game it would wait there for good";
            PathMarks.Add(new L2StageMark { Left = x * 16 + 4, Top = y * 16 + 4, Width = 8, Height = 8, IsSource = hit != null });
            (px, py) = (x, y);
        }
        PathMarks.Add(new L2StageMark { Left = _pathFrom.X * 16, Top = _pathFrom.Y * 16, Width = 16, Height = 16, Label = "start", IsCorner = true });
        for (int k = 0; k < _pathCorners.Count; k++)
            PathMarks.Add(new L2StageMark { Left = _pathCorners[k].X * 16, Top = _pathCorners[k].Y * 16, Width = 16, Height = 16, Label = $"{k + 1}", IsCorner = true });
        int frames = route.Count * 64 / Math.Max(1, PathSpeed?.Value ?? L2Paths.NormalSpeed);
        string who = L2EventCommands.Cap(Name(_pathWho));
        PathInfo = $"{who}'s path{(_pathId >= 0 ? $" {_pathId}" : " (new)")}: {_pathCorners.Count} place{(_pathCorners.Count == 1 ? "" : "s")}, {route.Count} steps, about {frames / 60.0:0.#} s. " +
                   "Click tiles to add the places it walks to in turn (sideways first, then up or down); click the last one again or right-click to take it back." +
                   (blocked != null ? $" Careful: {blocked}." : "");
    }
}
