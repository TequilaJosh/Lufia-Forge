using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A box (or label) drawn over the map: pixel place and size, colour, label.</summary>
public sealed class L2Overlay
{
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public Brush Stroke { get; init; } = Brushes.White;
    public double Thickness { get; init; } = 1;
    public string Label { get; init; } = "";
}

/// <summary>A map in the list.</summary>
public sealed class L2MapItem
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

/// <summary>A chest picture + Chest can use (closed and open, as drawn on its map).</summary>
public sealed class L2ChestLookChoice
{
    public L2ChestLooks.Look Look { get; init; } = null!;
    public string Label { get; init; } = "";
    public BitmapSource? Image { get; init; }
}

/// <summary>A block button (recent brushes, blocks that go with the brush).</summary>
public sealed class L2BlockChoice
{
    public int Block { get; init; }
    public BitmapSource? Image { get; init; }
    public string Tip { get; init; } = "";
}

public sealed class L2BlockRow
{
    public string Label { get; init; } = "";
    public List<L2BlockChoice> Blocks { get; init; } = new();
}

/// <summary>
/// The Lufia II Maps tab (as Lufia 1's map editor): tools to select and move what is on the map, paint blocks (one or
/// a rectangle of them) on either layer, pick, copy / cut / paste / fill areas, paint collision, add exits, step-on
/// areas, characters and preset points; an inspector for the picked thing; zoom, camera, overlays per kind; block
/// order, recent brushes and blocks that go with the brush; map settings (graphics, size, name, test start). Edits are
/// kept here (with Undo / Redo) and written by themselves when another map is picked or the project is saved, or by
/// Apply (<see cref="L2MapEdit"/>).
/// </summary>
public partial class L2MapsPage : ObservableObject, IPendingEdits
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private L2MapEdit? _edit;
    private uint[][] _blocks = Array.Empty<uint[]>();
    public const int PickerColumns = 16;

    public ObservableCollection<L2MapItem> Maps { get; } = new();
    [ObservableProperty] private L2MapItem? _selectedMap;
    [ObservableProperty] private string _mapSearch = "";
    /// <summary>The map shown (its id), -1 for none.</summary>
    [ObservableProperty] private int _selectedIndex = -1;
    private List<string> _names = new();

    [ObservableProperty] private BitmapSource? _mapImage;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _hoverText = "";
    [ObservableProperty] private string _mapSummary = "";
    [ObservableProperty] private bool _hasUnappliedChanges;
    [ObservableProperty] private bool _isEditableMap;
    public ObservableCollection<L2Overlay> Overlays { get; } = new();

    // what to show
    [ObservableProperty] private bool _showGround = true, _showUpper = true, _showCollision, _showCharacters = true, _showExits = true,
        _showDoors = true, _showTreasure = true, _showTriggers = true, _showPoints, _showCamera;

    // zoom
    private static readonly double[] Zooms = { 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4 };
    [ObservableProperty] private double _zoom = 1;
    public string ZoomText => $"{Zoom * 100:0}%";
    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));
    [RelayCommand] private void ZoomIn() => Zoom = Zooms.FirstOrDefault(z => z > Zoom + 0.001, Zooms[^1]);
    [RelayCommand] private void ZoomOut() => Zoom = Zooms.LastOrDefault(z => z < Zoom - 0.001, Zooms[0]);
    [RelayCommand] private void ZoomReset() => Zoom = 1;
    /// <summary>The zoom that shows the whole map in a view of this size.</summary>
    public void Fit(double w, double h)
    {
        if (_edit == null && _world == null || w <= 0 || h <= 0) return;
        double z = _world != null ? Math.Min(w, h) / (double)WorldPixels : Math.Min(w / (_edit!.Width * 16.0), h / (_edit.Height * 16.0));
        Zoom = Math.Clamp(Math.Floor(z * 20) / 20, 0.1, 4);
    }
    public void WheelZoom(int delta) { if (delta > 0) ZoomIn(); else ZoomOut(); }

    /// <summary>Asks the Lufia II tab to show an event of a map (map, list +6 / +8 / +4, id).</summary>
    public event Action<int, int, int>? OpenEventRequested;

    // ---------------------------------------------------------------- loading

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        main.Register(this);
        _names = Enumerable.Range(0, L2Maps.Count).Select(id => RomName(rom, id)).ToList();
        _itemNames = null;
        _gameLooks = null;
        _stats.Clear();
        _edit = null; _history.Clear(); _redo.Clear();
        FillList();
        SelectedIndex = -1;
        SelectedIndex = 3;
    }

    private static string RomName(RomBuffer rom, int id)
    {
        string n = L2Maps.Name(rom, id);
        return n.Length > 0 && !n.Contains('~') ? n : id < 2 ? (id == 0 ? "World map" : "Sea floor") : "Map " + id.ToString("X2");
    }

    /// <summary>The name shown: the project's own name for it, else the game's.</summary>
    public string NameOf(int id) =>
        LufiaForge.Core.Maps.MapNames.ProjectNames is { } p && p.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : _names.ElementAtOrDefault(id) ?? $"Map {id:X2}";

    private void FillList()
    {
        string q = MapSearch.Trim();
        var keep = SelectedIndex;
        _filling = true;
        Maps.Clear();
        for (int id = 0; id < L2Maps.Count; id++)
        {
            string label = $"{id:X2}  {NameOf(id)}";
            if (q.Length > 0 && !label.Contains(q, StringComparison.OrdinalIgnoreCase) && !(int.TryParse(q, System.Globalization.NumberStyles.HexNumber, null, out int h) && h == id)) continue;
            Maps.Add(new L2MapItem { Id = id, Label = label });
        }
        SelectedMap = Maps.FirstOrDefault(m => m.Id == keep);
        _filling = false;
    }
    private bool _filling;

    partial void OnMapSearchChanged(string value) => FillList();
    partial void OnSelectedMapChanged(L2MapItem? value) { if (!_filling && value != null && value.Id != SelectedIndex) SelectedIndex = value.Id; }

    partial void OnSelectedIndexChanging(int oldValue, int newValue)
    {
        // what wasn't written yet goes in by itself (as Lufia 1's editor)
        if ((_edit?.HasChanges == true || _world?.Changed == true) && _rom != null)
        {
            var why = WritePendingEdits();
            if (why != null) Status = $"Map {oldValue:X2} couldn't be written: {why}";
        }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        _edit = null; _history.Clear(); _redo.Clear();
        Selection = null; AreaRect = null; StopStamp();
        if (SelectedMap?.Id != value) { _filling = true; SelectedMap = Maps.FirstOrDefault(m => m.Id == value); _filling = false; }
        if (_rom != null && value >= 0)
        {
            try { _edit = new L2MapEdit(_rom, value); }
            catch (Exception ex) { _edit = null; Status = "Couldn't read the map: " + ex.Message; }
        }
        LoadWorld(value);
        FillStory();
        OnPropertyChanged(nameof(ToolHint));
        IsEditableMap = (_edit != null || _world != null) && TableGuard.LockReason(_rom!, "Maps") == null;
        MapNameText = value >= 0 ? (LufiaForge.Core.Maps.MapNames.ProjectNames?.GetValueOrDefault(value) ?? "") : "";
        OnPropertyChanged(nameof(MapNameHint));
        LoadSettings();
        ReloadBlocks();
        FillChestLooks();
        OnPropertyChanged(nameof(ChestLookVisible));
        Refresh();
        UpdateUndoState();
        if (_rom != null && _world != null) Status = value == 0
            ? "The world map: paint, pick and copy its big blocks; + Exit adds an entrance to a town or dungeon (Select moves them); Battle zones paints where which battles happen."
            : "The sea floor: paint, pick and copy its big blocks.";
        else if (_rom != null && TableGuard.LockReason(_rom, "Maps") is { } locked) Status = locked;
        else if (_edit != null) Status = $"{NameOf(value)}. Pick a tool above; Ctrl+Z takes back each change.";
    }

    // ---------------------------------------------------------------- the story so far

    /// <summary>The story points the map's start-up script tells apart (its creates' flag checks): the first choice
    /// shows each character's first look, the others the characters the game makes while those flags are so.</summary>
    public ObservableCollection<L2Option> StoryChoices { get; } = new();
    [ObservableProperty] private L2Option? _storyChoice;
    [ObservableProperty] private bool _hasStory;
    private List<L2MapInfo.Create> _stages = new();
    private readonly List<List<(int Flag, bool On)>> _storyNeeds = new();
    /// <summary>Persistent characters (10-4F) that stand on this map at some point, on a character start of it.</summary>
    private List<L2Persistent.Place> _persistent = new();

    private void FillStory()
    {
        _filling = true;
        try
        {
            StoryChoices.Clear(); _storyNeeds.Clear();
            _stages = _rom != null && _edit != null && _world == null ? L2MapInfo.Stages(_rom, _edit.MapId) : new();
            _persistent = _rom != null && _edit != null && _world == null ? L2Persistent.On(L2Persistent.All(_rom), _edit.MapId) : new();
            StoryChoices.Add(new L2Option(0, "Any point (each character's first look)"));
            foreach (var g in _stages.Where(c => c.Flags.Count > 0).GroupBy(c => c.When))
            {
                _storyNeeds.Add(g.First().Flags.ToList());
                StoryChoices.Add(new L2Option(_storyNeeds.Count, "While " + g.Key));
            }
            HasStory = StoryChoices.Count > 1;
            StoryChoice = StoryChoices[0];
        }
        finally { _filling = false; }
    }

    partial void OnStoryChoiceChanged(L2Option? value) { if (!_filling) { Refresh(); if (Selection != null) UpdateInspector(); } }

    /// <summary>The sprite a character shows at the story point picked (null: the game doesn't make it then). A
    /// character no create of the start-up script makes (one added here) shows as it is.</summary>
    private int? StorySprite(int id)
    {
        if (_edit == null) return null;
        var creates = _stages.Where(c => c.Id == id).ToList();
        bool edited = _edit.SpritesEdited(id);
        if (creates.Count == 0 && !_edit.Sprites.ContainsKey(id) && _persistent.FirstOrDefault(p => p.Spot == id - 0x4F) is { } pc)
            return pc.Sprite;   // a persistent character's start
        if (StoryChoice is not { Value: > 0 } k || creates.Count == 0 || edited)
            return _edit.Sprites.TryGetValue(id, out int s) ? s : null;
        var need = _storyNeeds[k.Value - 1];
        var c = creates.FirstOrDefault(c => !c.Flags.Any(f => need.Any(n => n.Flag == f.Flag && n.On != f.On)));
        return c?.Sprite;
    }

    /// <summary>The character is on the map at the story point picked (always, with the first choice).</summary>
    private bool AtStoryPoint(int id) => StoryChoice is not { Value: > 0 } || !_stages.Any(c => c.Id == id) || StorySprite(id) != null;

    /// <summary>When the game makes a character, for the inspector.</summary>
    private string StoryNote(int id)
    {
        var creates = _stages.Where(c => c.Id == id).ToList();
        var here = _persistent.Where(p => p.Spot == id - 0x4F).ToList();
        if (creates.Count == 0 && here.Count > 0 && _edit?.Sprites.ContainsKey(id) != true)
            return " " + string.Join(" ", here.Select(p => $"Persistent character {p.Id:X2} (sprite {L2Sprites.Name(p.Sprite)}) stands here " +
                (p.From < 0 ? "from the start of a new game" : $"once an event on map {p.From:X2} {NameOf(p.From)} sends it here") + ".")) +
                " Persistent characters (10-4F) aren't made by the map: the game keeps where each one is and puts it on this character start.";
        if (creates.Count == 0) return _edit?.Sprites.ContainsKey(id) == true ? "" : " The map's start-up script doesn't make it: an event does, or the game never shows it.";
        string note = creates.All(c => c.When == "") ? "" : " The game makes it " + string.Join("; ", creates.Select(c => $"with sprite {L2Sprites.Name(c.Sprite)} " + (c.When == "" ? "always" : "while " + c.When))) + ".";
        if (StoryChoice is { Value: > 0 } && StorySprite(id) == null) note += " Not on the map at the story point picked.";
        if (creates.Select(c => c.Sprite).Distinct().Count() > 1) note += " A new sprite is written to each of its creates.";
        return note;
    }

    /// <summary>The block pictures of the map's block set (as drawn now), and the picker.</summary>
    private void ReloadBlocks()
    {
        if (_world != null) { DrawWorldPicker(); return; }
        if (_rom == null || _edit == null) { _blocks = Array.Empty<uint[]>(); PickerImage = null; return; }
        try { _blocks = L2Maps.AllBlocks(_rom, _edit.ForDrawing); } catch { _blocks = Array.Empty<uint[]>(); }
        if (SelectedBlock >= _blocks.Length) SelectedBlock = Math.Min(1, _blocks.Length - 1);
        DrawPicker();
        UpdateNeighbours();
        UpdateRecent();
    }

    // ---------------------------------------------------------------- pending edits

    public bool HasPendingEdits => _edit?.HasChanges == true || _world?.Changed == true;

    public string? WritePendingEdits()
    {
        if (_rom == null || _main == null) return null;
        if (_world != null) return _world.Changed || _edit?.HasChanges == true ? TableGuard.LockReason(_rom, "Maps") ?? WriteWorld() : null;
        if (_edit == null || !_edit.HasChanges) return null;
        if (TableGuard.LockReason(_rom, "Maps") is { } locked) return locked;
        try
        {
            int before = _rom.Length;
            string what = _edit.Apply(_rom);
            _main.NotifyRomModified($"Lufia II: map {_edit.MapId:X2} ({NameOf(_edit.MapId)})");
            int id = _edit.MapId;
            var keep = Selection;
            // read it back as the ROM has it now (the edit's "before" becomes this)
            _edit = new L2MapEdit(_rom, id);
            _history.Clear(); _redo.Clear();
            if (keep != null) Selection = Find(keep.Kind, keep.Index);
            UpdateUndoState();
            Status = what + (_rom.Length > before ? " (the ROM grew to make room)." : "");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    [RelayCommand]
    private void ApplyMap()
    {
        var why = WritePendingEdits();
        if (why != null) Status = "Not written: " + why;
        Refresh();
    }

    [RelayCommand]
    private void RevertMap()
    {
        int id = SelectedIndex;
        if (_world != null) { LoadWorld(id); _edit = _rom != null ? new L2MapEdit(_rom, id) : null; Selection = null; Refresh(); WorldUndoState(); Status = "Back to the map as the project has it."; return; }
        if (_edit == null) return;
        _edit = null;
        _history.Clear(); _redo.Clear();
        _edit = _rom != null ? new L2MapEdit(_rom, id) : null;
        Selection = null; AreaRect = null; StopStamp();
        LoadSettings(); ReloadBlocks(); Refresh(); UpdateUndoState();
        Status = "Back to the map as the project has it.";
    }

    /// <summary>The tab is shown again: treasure the Treasure tab changed meanwhile comes into this map's edit (for the
    /// treasure not changed here, also in the Undo steps), so the inspector shows it and Apply keeps it.</summary>
    public void Shown()
    {
        if (_rom == null || _edit == null || _world != null) return;
        if (_edit.RebaseChests(_rom) is not var (old, now)) return;
        for (int i = 0; i < _history.Count; i++) _history[i] = (_history[i].State with { Chests = L2MapEdit.Merge(old, _history[i].State.Chests, now) }, _history[i].What);
        for (int i = 0; i < _redo.Count; i++) _redo[i] = (_redo[i].State with { Chests = L2MapEdit.Merge(old, _redo[i].State.Chests, now) }, _redo[i].What);
        DrawOverlays(); UpdateInspector(); UpdateUndoState();
    }

    // ---------------------------------------------------------------- undo

    private readonly List<(L2MapEdit.State State, string What)> _history = new(), _redo = new();
    [ObservableProperty] private string _mapUndoText = "Nothing to undo", _mapRedoText = "Nothing to redo";

    /// <summary>Remembers the map as it is, before a change described by <paramref name="what"/>.</summary>
    private void Record(string what)
    {
        if (_world != null) { WorldRecord(what); return; }
        if (_edit == null) return;
        _history.Add((_edit.Save(), what));
        if (_history.Count > 300) _history.RemoveAt(0);
        _redo.Clear();
        UpdateUndoState();
    }

    /// <summary>Drops the last remembered step when nothing changed after all (a click that didn't move anything).</summary>
    private void DropIfSame()
    {
        if (_world != null) { WorldDropIfSame(); return; }
        if (_edit == null || _history.Count == 0) return;
        if (L2MapEdit.Same(_history[^1].State, _edit.Save())) { _history.RemoveAt(_history.Count - 1); UpdateUndoState(); }
    }

    private void UpdateUndoState()
    {
        if (_world != null) { WorldUndoState(); return; }
        MapUndoText = _history.Count > 0 ? "Undo: " + _history[^1].What + " (Ctrl+Z)" : "Nothing to undo";
        MapRedoText = _redo.Count > 0 ? "Redo: " + _redo[^1].What + " (Ctrl+Y)" : "Nothing to redo";
        HasUnappliedChanges = _edit?.HasChanges == true;
        UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => _history.Count > 0 || _world != null && _worldHistory.Count > 0;
    private bool CanRedo() => _redo.Count > 0 || _world != null && _worldRedo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => UndoPaint();
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => RedoPaint();

    /// <summary>Ctrl+Z: takes back the last change to the map (false when there is none, so Edit &gt; Undo gets it).</summary>
    public bool UndoPaint()
    {
        if (_world != null) return WorldUndo(false);
        EndDrag();
        if (_edit == null || _history.Count == 0) return false;
        var (state, what) = _history[^1]; _history.RemoveAt(_history.Count - 1);
        _redo.Add((_edit.Save(), what));
        Restore(state);
        Status = "Undone: " + what + ".";
        return true;
    }

    public bool RedoPaint()
    {
        if (_world != null) return WorldUndo(true);
        if (_edit == null || _redo.Count == 0) return false;
        var (state, what) = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        _history.Add((_edit.Save(), what));
        Restore(state);
        Status = "Redone: " + what + ".";
        return true;
    }

    private void Restore(L2MapEdit.State s)
    {
        bool sets = _edit!.BlockSetIndex != s.BlockSet || _edit.TileSetIndex != s.TileSet || _edit.PaletteSetIndex != s.PaletteSet;
        _edit.Restore(s);
        if (Selection != null) Selection = Find(Selection.Kind, Selection.Index);
        LoadSettings();
        if (sets) ReloadBlocks();
        Refresh(); UpdateUndoState();
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>Draws the map again (after any change) and its overlays.</summary>
    public void Refresh()
    {
        ReadEventOwners();
        DrawMap();
        DrawOverlays();
        UpdateUndoState();
        UpdateInspector();
    }

    /// <summary>The characters that have a talk event and the step-on areas that have an event (marked on the map).</summary>
    private HashSet<int> _talkIds = new(), _triggerIds = new();

    private void ReadEventOwners()
    {
        _talkIds = new(); _triggerIds = new();
        if (_rom == null || _edit == null) return;
        try
        {
            int block = L2Events.Block(_rom, _edit.MapId);
            if (block < 0) return;
            foreach (var e in L2EventBlocks.ReadLists(_rom, block))
            {
                if (e.Head == L2EventBlocks.TalkList) _talkIds.Add(e.Id);
                else if (e.Head == L2EventBlocks.TriggerList) _triggerIds.Add(e.Id);
            }
        }
        catch (Exception) { }
    }

    /// <summary>Kept for callers that only want a redraw.</summary>
    public void Draw() => Refresh();

    private static void Dot(uint[] px, int w, int h, int x, int y, uint c) { if (x >= 0 && y >= 0 && x < w && y < h) px[y * w + x] = c; }

    private void DrawMap()
    {
        if (_world != null) { DrawWorld(); return; }
        ZoneImage = null; ZoneImageSize = 0;
        if (_rom == null || _edit == null) { MapImage = null; MapSummary = ""; return; }
        try
        {
            var map = _edit.ForDrawing;
            var (w, h, px) = L2Maps.Render(_rom, map, ShowGround, ShowUpper);
            if (ShowCollision && map.Layers.Length > 1)
            {
                var L = map.Layers[1];
                for (int cy = 0; cy < L.Height; cy++)
                    for (int cx = 0; cx < L.Width; cx++)
                    {
                        byte f = L.Flags[cy * L.Width + cx];
                        uint tint = (f & 0x80) != 0 ? 0xFFFF3030u : (f & 0x20) != 0 ? 0xFF30C0FFu : (f & 0x0C) != 0 ? 0xFFFFC030u : 0;
                        if (tint == 0) continue;
                        for (int y = 0; y < 16; y++)
                            for (int x = 0; x < 16; x++)
                            {
                                bool edge = (f & 0x80) != 0 || (f & 0x20) != 0 || (f & 0x04) != 0 && y < 2 || (f & 0x08) != 0 && x < 2;
                                if (!edge || (x + y) % 2 == 1 && (f & 0xA0) != 0) continue;
                                int i = (cy * 16 + y) * w + cx * 16 + x;
                                if (i < px.Length) px[i] = tint;
                            }
                    }
            }
            if (ShowCharacters)
                foreach (var (_, b) in _edit.Records(L2MapEdit.Characters))
                {
                    if (StorySprite(0x4F + b[0]) is not int spr || L2Graphics.MapSprite(_rom, spr) is not var (sw, sh, spx)) continue;
                    int x0 = b[1] * 16 + 8 - sw / 2, y0 = b[2] * 16 + 16 - sh;
                    for (int y = 0; y < sh; y++)
                        for (int x = 0; x < sw; x++)
                            if (spx[y * sw + x] != 0) Dot(px, w, h, x0 + x, y0 + y, spx[y * sw + x]);
                }
            MapImage = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), w * 4);
            int chars = _edit.Records(L2MapEdit.Characters).Count, exits = _edit.Records(L2MapEdit.Exits).Count, areas = _edit.Records(L2MapEdit.Triggers).Count;
            MapSummary = $"{_edit.Width} x {_edit.Height} blocks; block set {_edit.BlockSetIndex}, tile set {_edit.TileSetIndex}, palette set {_edit.PaletteSetIndex}; " +
                         $"{chars} characters, {exits} exits, {areas} step-on areas.";
        }
        catch (Exception ex) { MapImage = null; MapSummary = "Couldn't draw it: " + ex.Message; }
    }

    private static readonly Brush ExitBrush = Freeze(Color.FromRgb(0x40, 0xFF, 0x40)), DoorBrush = Freeze(Color.FromRgb(0xFF, 0xE0, 0x40)),
        TreasureBrush = Freeze(Color.FromRgb(0xFF, 0x60, 0xFF)), CharBrush = Freeze(Color.FromRgb(0x40, 0xC0, 0xFF)),
        TriggerBrush = Freeze(Color.FromRgb(0xFF, 0x90, 0x40)), PointBrush = Freeze(Color.FromRgb(0x40, 0xFF, 0xFF)),
        WalkBrush = Freeze(Color.FromArgb(0x90, 0x40, 0xC0, 0xFF)), SelectedBrush = Freeze(Colors.White), HiddenBrush = Freeze(Color.FromRgb(0xFF, 0xB0, 0xFF));
    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private void DrawOverlays()
    {
        Overlays.Clear();
        if (_edit == null) return;
        void Box(int x1, int y1, int x2, int y2, Brush b, string label, bool selected)
        {
            Overlays.Add(new L2Overlay { X = x1 * 16, Y = y1 * 16, W = Math.Max(1, x2 - x1) * 16, H = Math.Max(1, y2 - y1) * 16, Stroke = selected ? SelectedBrush : b, Thickness = selected ? 2.5 : 1.2, Label = label });
        }
        bool Sel(SelKind k, int i) => Selection?.Kind == k && Selection.Index == i;
        if (ShowExits)
        {
            var exits = _edit.Records(L2MapEdit.Exits);
            for (int i = 0; i < exits.Count; i++)
            {
                var b = exits[i].Bytes;
                Box(b[1], b[2], b[3], b[4], ExitBrush, ExitLabel(b), Sel(SelKind.Exit, i));
            }
        }
        if (ShowDoors)
        {
            var doors = _edit.Records(L2MapEdit.Doors);
            for (int i = 0; i < doors.Count; i++) { var b = doors[i].Bytes; Box(b[1], b[2], b[3], b[4], DoorBrush, $"D{b[0]}", Sel(SelKind.Door, i)); }
        }
        if (ShowTriggers)
        {
            var t = _edit.Records(L2MapEdit.Triggers);
            for (int i = 0; i < t.Count; i++) { var b = t[i].Bytes; Box(b[1], b[2], b[3], b[4], TriggerBrush, $"T{b[0]}" + (_triggerIds.Contains(b[0]) ? " ▶" : ""), Sel(SelKind.Trigger, i)); }
        }
        if (ShowTreasure)
        {
            var t = _edit.Records(L2MapEdit.Treasure);
            for (int i = 0; i < t.Count; i++) { var b = t[i].Bytes; Box(b[1], b[2], b[1] + 1, b[2] + 1, _edit.IsHidden(b) ? HiddenBrush : TreasureBrush, TreasureLabel(b), Sel(SelKind.Treasure, i)); }
        }
        if (ShowPoints)
        {
            var p = _edit.Records(L2MapEdit.Points);
            for (int i = 0; i < p.Count; i++) { var b = p[i].Bytes; Box(b[1], b[2], b[1] + 1, b[2] + 1, PointBrush, $"P{b[0]}", Sel(SelKind.Point, i)); }
        }
        if (ShowCharacters)
        {
            var c = _edit.Records(L2MapEdit.Characters);
            for (int i = 0; i < c.Count; i++)
            {
                var b = c[i].Bytes;
                bool s = Sel(SelKind.Character, i);
                if (!s && !AtStoryPoint(0x4F + b[0])) continue;   // not on the map at the story point picked
                if (s) Box(b[3], b[4], b[5], b[6], WalkBrush, "", false);
                // a speech mark: talking to it plays an event (double-click to open it)
                Box(b[1], b[2], b[1] + 1, b[2] + 1, CharBrush, $"C{b[0]}" + (_talkIds.Contains(0x4F + b[0]) ? " 💬" : ""), s);
            }
        }
    }

    /// <summary>An exit's label: the map it leads to (by name on the world map, where there are many).</summary>
    private string ExitLabel(byte[] b)
    {
        if (b[8] == 0) return _world != null ? $"E{b[0]}: pick where" : $"E{b[0]}→World map";
        if (_world != null) { string n = NameOf(b[8]); return n.Length > 16 ? n[..16] : n; }
        return $"E{b[0]}→{b[8]:X2} {Short(NameOf(b[8]))}";
    }

    private static string Short(string n) => n.Length > 12 ? n[..12] : n;

    private string TreasureLabel(byte[] b)
    {
        var c = _edit?.ChestOf(b[0] & 0x1F);
        string item = c == null ? "empty" : ItemName(c.Item);
        return (_edit!.IsHidden(b) ? "?" : "$") + Short(item);
    }

    private List<string>? _itemNames;
    private string ItemName(int item)
    {
        if (L2Gold.IsGold(item)) return $"{L2Gold.AmountOf(item)} gold";
        _itemNames ??= _rom == null ? new() : L2Battle.Items(_rom).OrderBy(i => i.Index).Select(i => i.Name.Trim()).ToList();
        return item >= 0 && item < _itemNames.Count ? _itemNames[item] : $"item {item:X3}";
    }

    // ---------------------------------------------------------------- map list: names

    [ObservableProperty] private string _mapNameText = "";
    public string MapNameHint => LufiaForge.Core.Maps.MapNames.ProjectNames == null
        ? "Open or make a project to name maps (the names are kept in it)."
        : $"The game calls it \"{_names.ElementAtOrDefault(SelectedIndex)}\". Your name is kept in this project; clear it to go back.";

    partial void OnMapNameTextChanged(string value)
    {
        if (SelectedIndex < 0 || LufiaForge.Core.Maps.MapNames.ProjectNames is not { } names) return;
        string had = names.GetValueOrDefault(SelectedIndex) ?? "";
        if (had == value.Trim()) return;
        LufiaForge.Core.Maps.MapNames.SetProjectName(SelectedIndex, value.Trim());
        var item = Maps.FirstOrDefault(m => m.Id == SelectedIndex);
        if (item != null)
        {
            int at = Maps.IndexOf(item);
            _filling = true;
            Maps[at] = new L2MapItem { Id = item.Id, Label = $"{item.Id:X2}  {NameOf(item.Id)}" };
            SelectedMap = Maps[at];
            _filling = false;
        }
    }
}
