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

// a box (or label) drawn over the map: where and how big in pixels, colour, label
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

// one map in the list
public sealed class L2MapItem
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

// a chest picture "+ Chest" can use (closed and open, as it's drawn on its map)
public sealed class L2ChestLookChoice
{
    public L2ChestLooks.Look Look { get; init; } = null!;
    public string Label { get; init; } = "";
    public BitmapSource? Image { get; init; }
}

// a block button (recent brushes, blocks that go with the brush)
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

// the Lufia II Maps tab, works like Lufia 1's map editor. tools to select and move stuff on the map, paint blocks
// (one or a rectangle) on either layer, pick, copy / cut / paste / fill areas, paint collision, add exits, step-on
// areas, characters and preset points. plus an inspector for whatever's picked, zoom, camera, overlays per kind, block
// order, recent brushes, blocks that go with the brush, and map settings (graphics, size, name, test start).
// edits stay here (with Undo / Redo) and get written by themselves when another map is picked or the project is saved,
// or on Apply (L2MapEdit)
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
    // id of the map shown, -1 = none
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
    // zooms so the whole map fits in a view this size
    public void Fit(double viewWidth, double viewHeight)
    {
        if (_edit == null && _world == null || viewWidth <= 0 || viewHeight <= 0) return;
        double fitZoom = _world != null ? Math.Min(viewWidth, viewHeight) / (double)WorldPixels : Math.Min(viewWidth / (_edit!.Width * 16.0), viewHeight / (_edit.Height * 16.0));
        Zoom = Math.Clamp(Math.Floor(fitZoom * 20) / 20, 0.1, 4);
    }
    public void WheelZoom(int delta) { if (delta > 0) ZoomIn(); else ZoomOut(); }

    // asks the Lufia II tab to show one of a map's events (map, list +6 / +8 / +4, id)
    public event Action<int, int, int>? OpenEventRequested;

    // loading

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        // same ROM read again (Undo, a patch, a tidy) = the map shown stays. edits still not written by now belong to
        // the old bytes, so they get dropped, along with what the world map and its zones hold (the operations that
        // reload every page write them first anyway)
        bool sameRom = ReferenceEquals(rom, _rom);
        int keepMap = sameRom && SelectedIndex >= 0 ? SelectedIndex : 3;
        bool hadPendingEdits = HasPendingEdits;
        _rom = rom; _main = main;
        main.Register(this);
        _names = Enumerable.Range(0, L2Maps.Count(rom)).Select(id => RomName(rom, id)).ToList();
        _itemNames = null;
        _gameLooks = null;
        _stats.Clear();
        _edit = null; _history.Clear(); _redo.Clear();
        _world = null; _worldHistory.Clear(); _worldRedo.Clear();
        ItemChoices.Clear(); SpriteChoices.Clear(); DestinationChoices.Clear();
        FillList();
        SelectedIndex = -1;
        SelectedIndex = keepMap;
        if (sameRom && hadPendingEdits) Status = "Map edits that couldn't be written before the ROM was read again were dropped. " + Status;
    }

    // the ROM's version when the map shown was read (or written here)
    private int _readVersion = -1;

    private static string RomName(RomBuffer rom, int id)
    {
        if (L2NewMaps.IsFree(rom, id)) return "(free: the next new map takes this number)";
        string name = L2Maps.Name(rom, id);
        return name.Length > 0 && !name.Contains('~') ? name : id < 2 ? (id == 0 ? "World map" : "Sea floor") : "Map " + id.ToString("X2");
    }

    // the name we show: the project's own name if it has one, else the game's
    public string NameOf(int id) =>
        LufiaForge.Core.Maps.MapNames.ProjectNames is { } projectNames && projectNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : _names.ElementAtOrDefault(id) ?? $"Map {id:X2}";

    private void FillList()
    {
        string search = MapSearch.Trim();
        var keepId = SelectedIndex;
        _filling = true;
        Maps.Clear();
        for (int id = 0; id < L2Maps.Count(_rom!); id++)
        {
            string label = $"{id:X2}  {NameOf(id)}";
            if (search.Length > 0 && !label.Contains(search, StringComparison.OrdinalIgnoreCase) && !(int.TryParse(search, System.Globalization.NumberStyles.HexNumber, null, out int hexId) && hexId == id)) continue;
            Maps.Add(new L2MapItem { Id = id, Label = label });
        }
        SelectedMap = Maps.FirstOrDefault(m => m.Id == keepId);
        _filling = false;
    }
    private bool _filling;

    partial void OnMapSearchChanged(string value) => FillList();
    partial void OnSelectedMapChanged(L2MapItem? value) { if (!_filling && value != null && value.Id != SelectedIndex) SelectedIndex = value.Id; }

    partial void OnSelectedIndexChanging(int oldValue, int newValue)
    {
        // whatever isn't written yet goes in by itself (like Lufia 1's editor)
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
        _settingGameName = true;
        GameNameText = value >= 2 && _rom != null ? L2Maps.Name(_rom, value) : "";
        _settingGameName = false;
        GameNameHint = value < 2 ? "The world maps have no name of their own." : L2MapNames.CantWrite(_rom!) ?? "Shown in the save menu and in the box on entering (Settings, Map names). Letters of the game's font, at most 20.";
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
        // a new map made from a mountain, tower or Shrine of Daos floor keeps its ground layer far below, so in the game
        // it shows out of line with the walls. a flat map built here won't want that
        if (_edit != null && value >= L2NewMaps.GameCount && GroundParallax)
            Status = $"{NameOf(value)}: its ground layer scrolls as a far-off background in the game (as on the map it was made from), so the game shows it out of line with the walls. " +
                     "For a town or any flat map, untick \"Ground layer is a far-off background\" in Map settings.";
        _readVersion = _rom?.Version ?? -1;
    }

    // the story so far

    // the story points the map's start-up script tells apart (the flag checks on its creates). the first choice shows
    // each character's first look, the others show the characters the game makes while those flags are set that way
    public ObservableCollection<L2Option> StoryChoices { get; } = new();
    [ObservableProperty] private L2Option? _storyChoice;
    [ObservableProperty] private bool _hasStory;
    private List<L2MapInfo.Create> _stages = new();
    private readonly List<List<(int Flag, bool On)>> _storyNeeds = new();
    // persistent characters (10-4F) that stand on this map at some point, on one of its character starts
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
            foreach (var whenGroup in _stages.Where(c => c.Flags.Count > 0).GroupBy(c => c.When))
            {
                _storyNeeds.Add(whenGroup.First().Flags.ToList());
                StoryChoices.Add(new L2Option(_storyNeeds.Count, "While " + whenGroup.Key));
            }
            HasStory = StoryChoices.Count > 1;
            StoryChoice = StoryChoices[0];
        }
        finally { _filling = false; }
    }

    partial void OnStoryChoiceChanged(L2Option? value) { if (!_filling) { Refresh(); if (Selection != null) UpdateInspector(); } }

    // the sprite a character shows at the story point picked (null = the game doesn't make it then). a character
    // none of the start-up script's creates makes (one added here) shows as it is
    private int? StorySprite(int id)
    {
        if (_edit == null) return null;
        var creates = _stages.Where(c => c.Id == id).ToList();
        bool edited = _edit.SpritesEdited(id);
        if (creates.Count == 0 && !_edit.Sprites.ContainsKey(id) && _persistent.FirstOrDefault(p => p.Spot == id - 0x4F) is { } persistentChar)
            return persistentChar.Sprite;   // a persistent character's start
        if (StoryChoice is not { Value: > 0 } story || creates.Count == 0 || edited)
            return _edit.Sprites.TryGetValue(id, out int sprite) ? sprite : null;
        var need = _storyNeeds[story.Value - 1];
        var create = creates.FirstOrDefault(c => !c.Flags.Any(f => need.Any(n => n.Flag == f.Flag && n.On != f.On)));
        return create?.Sprite;
    }

    // true if the character is on the map at the story point picked (always true with the first choice)
    private bool AtStoryPoint(int id) => StoryChoice is not { Value: > 0 } || !_stages.Any(c => c.Id == id) || StorySprite(id) != null;

    // when the game makes a character, for the inspector
    private string StoryNote(int id)
    {
        var creates = _stages.Where(c => c.Id == id).ToList();
        var persistentHere = _persistent.Where(p => p.Spot == id - 0x4F).ToList();
        if (creates.Count == 0 && persistentHere.Count > 0 && _edit?.Sprites.ContainsKey(id) != true)
            return " " + string.Join(" ", persistentHere.Select(p => $"Persistent character {p.Id:X2} (sprite {L2Sprites.Name(p.Sprite)}) stands here " +
                (p.From < 0 ? "from the start of a new game" : $"once an event on map {p.From:X2} {NameOf(p.From)} sends it here") + ".")) +
                " Persistent characters (10-4F) aren't made by the map: the game keeps where each one is and puts it on this character start.";
        if (creates.Count == 0) return _edit?.Sprites.ContainsKey(id) == true ? "" : " The map's start-up script doesn't make it: an event does, or the game never shows it.";
        string note = creates.All(c => c.When == "") ? "" : " The game makes it " + string.Join("; ", creates.Select(c => $"with sprite {L2Sprites.Name(c.Sprite)} " + (c.When == "" ? "always" : "while " + c.When))) + ".";
        if (StoryChoice is { Value: > 0 } && StorySprite(id) == null) note += " Not on the map at the story point picked.";
        if (creates.Select(c => c.Sprite).Distinct().Count() > 1) note += " A new sprite is written to each of its creates.";
        return note;
    }

    // reads the block pictures of the map's block set (as drawn right now) and redraws the picker
    private void ReloadBlocks()
    {
        if (_world != null) { DrawWorldPicker(); return; }
        if (_rom == null || _edit == null) { _blocks = Array.Empty<uint[]>(); PickerImage = null; return; }
        try { _blocks = L2Maps.AllBlocks(_rom, _edit.ForDrawing); } catch { _blocks = Array.Empty<uint[]>(); }
        if (SelectedBlock >= _blocks.Length) SelectedBlock = Math.Min(1, _blocks.Length - 1);
        if (_brush.Any(b => b >= _blocks.Length)) SetBrush(SelectedBlock);
        LoadSourceBlocks();
        DrawPicker();
        UpdateNeighbours();
        UpdateRecent();
    }

    // pending edits

    public bool HasPendingEdits => _edit?.HasChanges == true || _world?.Changed == true;

    public string? WritePendingEdits()
    {
        if (_rom == null || _main == null) return null;
        if (_world != null) return _world.Changed || _edit?.HasChanges == true ? TableGuard.LockReason(_rom, "Maps") ?? WriteWorld() : null;
        if (_edit == null || !_edit.HasChanges) return null;
        if (TableGuard.LockReason(_rom, "Maps") is { } locked) return locked;
        try
        {
            int lengthBefore = _rom.Length;
            string written = _edit.Apply(_rom);
            _main.NotifyRomModified($"Lufia II: map {_edit.MapId:X2} ({NameOf(_edit.MapId)})");
            int id = _edit.MapId;
            var keepSelection = Selection;
            // read it back the way the ROM has it now (that becomes the edit's "before")
            _edit = new L2MapEdit(_rom, id);
            _readVersion = _rom.Version;
            _history.Clear(); _redo.Clear();
            if (keepSelection != null) Selection = Find(keepSelection.Kind, keepSelection.Index);
            UpdateUndoState();
            Status = written + (_rom.Length > lengthBefore ? " (the ROM grew to make room)." : "");
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

    // the tab is shown again. treasure the Treasure tab changed in the meantime comes into this map's edit (and into
    // the Undo steps, for treasure not changed here), so the inspector shows it and Apply keeps it
    public void Shown()
    {
        if (_rom == null) return;
        // nothing unwritten here and the ROM changed since we read the map (another page, Undo): read it again
        if (_rom.Version != _readVersion && !HasPendingEdits && SelectedIndex >= 0)
        {
            var keepSelection = Selection;
            _itemNames = null; _gameLooks = null; _stats.Clear();
            ItemChoices.Clear(); SpriteChoices.Clear();
            int id = SelectedIndex;
            SelectedIndex = -1;
            SelectedIndex = id;
            if (keepSelection != null) Selection = Find(keepSelection.Kind, keepSelection.Index);
            return;
        }
        if (_edit == null || _world != null) return;
        if (_edit.RebaseChests(_rom) is not var (old, now)) return;
        for (int i = 0; i < _history.Count; i++) _history[i] = (_history[i].State with { Chests = L2MapEdit.Merge(old, _history[i].State.Chests, now) }, _history[i].What);
        for (int i = 0; i < _redo.Count; i++) _redo[i] = (_redo[i].State with { Chests = L2MapEdit.Merge(old, _redo[i].State.Chests, now) }, _redo[i].What);
        DrawOverlays(); UpdateInspector(); UpdateUndoState();
    }

    // undo

    private readonly List<(L2MapEdit.State State, string What)> _history = new(), _redo = new();
    [ObservableProperty] private string _mapUndoText = "Nothing to undo", _mapRedoText = "Nothing to redo";

    // remembers the map as it is right before a change (what = how the Undo menu names it)
    private void Record(string what)
    {
        if (_world != null) { WorldRecord(what); return; }
        if (_edit == null) return;
        _history.Add((_edit.Save(), what));
        if (_history.Count > 300) _history.RemoveAt(0);
        _redo.Clear();
        UpdateUndoState();
    }

    // drops the last remembered step if nothing changed after all (a click that didn't move anything)
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

    // Ctrl+Z: takes back the last change to the map. false if there's none, so Edit > Undo gets it
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

    private void Restore(L2MapEdit.State state)
    {
        bool setsChanged = _edit!.BlockSetIndex != state.BlockSet || _edit.TileSetIndex != state.TileSet || _edit.PaletteSetIndex != state.PaletteSet || _edit.AnimIndex != state.Anim;
        _edit.Restore(state);
        if (Selection != null) Selection = Find(Selection.Kind, Selection.Index);
        LoadSettings();
        if (setsChanged) ReloadBlocks();
        Refresh(); UpdateUndoState();
    }

    // drawing

    // redraws the map and its overlays (after any change)
    public void Refresh()
    {
        ReadEventOwners();
        DrawMap();
        DrawOverlays();
        UpdateUndoState();
        UpdateInspector();
    }

    // characters with a talk event and step-on areas with an event (they get a mark on the map)
    private HashSet<int> _talkIds = new(), _triggerIds = new();

    private void ReadEventOwners()
    {
        _talkIds = new(); _triggerIds = new();
        if (_rom == null || _edit == null) return;
        try
        {
            int block = L2Events.Block(_rom, _edit.MapId);
            if (block < 0) return;
            foreach (var entry in L2EventBlocks.ReadLists(_rom, block))
            {
                if (entry.Head == L2EventBlocks.TalkList) _talkIds.Add(entry.Id);
                else if (entry.Head == L2EventBlocks.TriggerList) _triggerIds.Add(entry.Id);
            }
        }
        catch (Exception) { }
    }

    // kept for callers that only want a redraw
    public void Draw() => Refresh();

    private static void Dot(uint[] pixels, int w, int h, int x, int y, uint colour) { if (x >= 0 && y >= 0 && x < w && y < h) pixels[y * w + x] = colour; }

    private void DrawMap()
    {
        if (_world != null) { DrawWorld(); return; }
        ZoneImage = null; ZoneImageSize = 0;
        if (_rom == null || _edit == null) { MapImage = null; MapSummary = ""; return; }
        try
        {
            var map = _edit.ForDrawing;
            var (w, h, pixels) = L2Maps.Render(_rom, map, ShowGround, ShowUpper);
            // ground block 0 draws nothing in the game (the screen keeps whatever was there, grass from elsewhere). we
            // draw purple stripes there so it doesn't pass for the black the game's maps put around their rooms
            if (ShowGround && map.Layers.Length > 0)
            {
                var ground = map.Layers[0];
                for (int cy = 0; cy < ground.Height; cy++)
                    for (int cx = 0; cx < ground.Width; cx++)
                    {
                        if (ground.Blocks[cy * ground.Width + cx] != 0) continue;
                        for (int y = 0; y < 16; y++)
                            for (int x = 0; x < 16; x++)
                            {
                                int i = (cy * 16 + y) * w + cx * 16 + x;
                                if (i < pixels.Length && pixels[i] == 0) pixels[i] = (x + y) % 8 < 3 ? 0xFF6A2A78u : 0xFF1A0A20u;
                            }
                    }
            }
            if (ShowCollision && map.Layers.Length > 1)
            {
                var upper = map.Layers[1];
                for (int cy = 0; cy < upper.Height; cy++)
                    for (int cx = 0; cx < upper.Width; cx++)
                    {
                        byte flags = upper.Flags[cy * upper.Width + cx];
                        uint tint = (flags & 0x80) != 0 ? 0xFFFF3030u : (flags & 0x20) != 0 ? 0xFF30C0FFu : (flags & 0x0C) != 0 ? 0xFFFFC030u : 0;
                        if (tint == 0) continue;
                        for (int y = 0; y < 16; y++)
                            for (int x = 0; x < 16; x++)
                            {
                                bool edge = (flags & 0x80) != 0 || (flags & 0x20) != 0 || (flags & 0x04) != 0 && y < 2 || (flags & 0x08) != 0 && x < 2;
                                if (!edge || (x + y) % 2 == 1 && (flags & 0xA0) != 0) continue;
                                int i = (cy * 16 + y) * w + cx * 16 + x;
                                if (i < pixels.Length) pixels[i] = tint;
                            }
                    }
            }
            if (ShowCharacters)
                foreach (var (_, charBytes) in _edit.Records(L2MapEdit.Characters))
                {
                    if (StorySprite(0x4F + charBytes[0]) is not int sprite || L2Graphics.MapSprite(_rom, sprite) is not var (spriteW, spriteH, spritePixels)) continue;
                    int x0 = charBytes[1] * 16 + 8 - spriteW / 2, y0 = charBytes[2] * 16 + 16 - spriteH;
                    for (int y = 0; y < spriteH; y++)
                        for (int x = 0; x < spriteW; x++)
                            if (spritePixels[y * spriteW + x] != 0) Dot(pixels, w, h, x0 + x, y0 + y, spritePixels[y * spriteW + x]);
                }
            MapImage = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), w * 4);
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
    private static Brush Freeze(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    private void DrawOverlays()
    {
        Overlays.Clear();
        if (_edit == null) return;
        void Box(int x1, int y1, int x2, int y2, Brush brush, string label, bool selected)
        {
            Overlays.Add(new L2Overlay { X = x1 * 16, Y = y1 * 16, W = Math.Max(1, x2 - x1) * 16, H = Math.Max(1, y2 - y1) * 16, Stroke = selected ? SelectedBrush : brush, Thickness = selected ? 2.5 : 1.2, Label = label });
        }
        bool Sel(SelKind kind, int i) => Selection?.Kind == kind && Selection.Index == i;
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
            var triggers = _edit.Records(L2MapEdit.Triggers);
            for (int i = 0; i < triggers.Count; i++) { var b = triggers[i].Bytes; Box(b[1], b[2], b[3], b[4], TriggerBrush, $"T{b[0]}" + (_triggerIds.Contains(b[0]) ? " ▶" : ""), Sel(SelKind.Trigger, i)); }
        }
        if (ShowTreasure)
        {
            var treasure = _edit.Records(L2MapEdit.Treasure);
            for (int i = 0; i < treasure.Count; i++) { var b = treasure[i].Bytes; Box(b[1], b[2], b[1] + 1, b[2] + 1, _edit.IsHidden(b) ? HiddenBrush : TreasureBrush, TreasureLabel(b), Sel(SelKind.Treasure, i)); }
        }
        if (ShowPoints)
        {
            var points = _edit.Records(L2MapEdit.Points);
            for (int i = 0; i < points.Count; i++) { var b = points[i].Bytes; Box(b[1], b[2], b[1] + 1, b[2] + 1, PointBrush, $"P{b[0]}", Sel(SelKind.Point, i)); }
        }
        if (ShowCharacters)
        {
            var characters = _edit.Records(L2MapEdit.Characters);
            for (int i = 0; i < characters.Count; i++)
            {
                var b = characters[i].Bytes;
                bool selected = Sel(SelKind.Character, i);
                if (!selected && !AtStoryPoint(0x4F + b[0])) continue;   // not on the map at the story point picked
                if (selected) Box(b[3], b[4], b[5], b[6], WalkBrush, "", false);
                // speech mark = talking to it plays an event (double-click to open it)
                Box(b[1], b[2], b[1] + 1, b[2] + 1, CharBrush, $"C{b[0]}" + (_talkIds.Contains(0x4F + b[0]) ? " 💬" : ""), selected);
            }
        }
    }

    // an exit's label: the map it leads to (just the name on the world map, there are lots of them there)
    private string ExitLabel(byte[] b)
    {
        if (b[8] == 0) return _world != null ? $"E{b[0]}: pick where" : $"E{b[0]}→World map";
        if (_world != null) { string name = NameOf(b[8]); return name.Length > 16 ? name[..16] : name; }
        return $"E{b[0]}→{b[8]:X2} {Short(NameOf(b[8]))}";
    }

    private static string Short(string name) => name.Length > 12 ? name[..12] : name;

    private string TreasureLabel(byte[] b)
    {
        var chest = _edit?.ChestOf(b[0] & 0x1F);
        string item = chest == null ? "empty" : ItemName(chest.Item);
        return (_edit!.IsHidden(b) ? "?" : "$") + Short(item);
    }

    private List<string>? _itemNames;
    private string ItemName(int item)
    {
        if (L2Gold.IsGold(item)) return $"{L2Gold.AmountOf(item)} gold";
        _itemNames ??= _rom == null ? new() : L2Battle.Items(_rom).OrderBy(i => i.Index).Select(i => i.Name.Trim()).ToList();
        return item >= 0 && item < _itemNames.Count ? _itemNames[item] : $"item {item:X3}";
    }

    // map list: names

    [ObservableProperty] private string _mapNameText = "";
    public string MapNameHint => LufiaForge.Core.Maps.MapNames.ProjectNames == null
        ? "Open or make a project to name maps (the names are kept in it)."
        : $"The game calls it \"{_names.ElementAtOrDefault(SelectedIndex)}\". Your name is kept in this project; clear it to go back.";

    [ObservableProperty] private string _gameNameText = "", _gameNameHint = "";
    private bool _settingGameName;

    // the name the game shows (written when the box loses focus)
    partial void OnGameNameTextChanged(string value)
    {
        if (_settingGameName || _rom == null || _main == null || SelectedIndex < 2) return;
        int id = SelectedIndex;
        if (value.Trim() == L2Maps.Name(_rom, id)) return;
        try { L2MapNames.Write(_rom, id, value); }
        catch (Exception ex) { GameNameHint = "Not written: " + ex.Message; return; }
        _main.NotifyRomModified($"Lufia II: map {id:X2} named \"{value.Trim()}\" in the game");
        _names[id] = RomName(_rom, id);
        var item = Maps.FirstOrDefault(m => m.Id == id);
        if (item != null)
        {
            int listIndex = Maps.IndexOf(item);
            _filling = true;
            Maps[listIndex] = new L2MapItem { Id = id, Label = $"{id:X2}  {NameOf(id)}" };
            SelectedMap = Maps[listIndex];
            _filling = false;
        }
        GameNameHint = $"Map {id:X2} is called \"{L2Maps.Name(_rom, id)}\" in the game now.";
        OnPropertyChanged(nameof(MapNameHint));
    }

    partial void OnMapNameTextChanged(string value)
    {
        if (SelectedIndex < 0 || LufiaForge.Core.Maps.MapNames.ProjectNames is not { } names) return;
        string oldName = names.GetValueOrDefault(SelectedIndex) ?? "";
        if (oldName == value.Trim()) return;
        LufiaForge.Core.Maps.MapNames.SetProjectName(SelectedIndex, value.Trim());
        var item = Maps.FirstOrDefault(m => m.Id == SelectedIndex);
        if (item != null)
        {
            int listIndex = Maps.IndexOf(item);
            _filling = true;
            Maps[listIndex] = new L2MapItem { Id = item.Id, Label = $"{item.Id:X2}  {NameOf(item.Id)}" };
            SelectedMap = Maps[listIndex];
            _filling = false;
        }
    }
}
