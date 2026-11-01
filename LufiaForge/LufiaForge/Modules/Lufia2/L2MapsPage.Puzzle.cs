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
}
