using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

public partial class L2MapsPage
{
    // graphics, size, test start

    // the graphics a map can have: each mix of block set, tile set, tile animation, palette set and colour cycling that
    // one of the game's maps uses. they go together, mixed by hand they draw scrambled or in the wrong colours
    public ObservableCollection<string> GraphicsChoices { get; } = new();
    private readonly List<(int Bs, int Ts, int Anim, int Ps, int Ca)> _graphics = new();
    [ObservableProperty] private int _graphicsChoice = -1;
    [ObservableProperty] private int _mapWidth, _mapHeight;
    [ObservableProperty] private int _testStartPoint;
    [ObservableProperty] private string _testStartNote = "";
    private bool _loadingSettings;

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            LoadGraphicsChoices();
            MapWidth = _edit?.Width ?? 0; MapHeight = _edit?.Height ?? 0;
            OnPropertyChanged(nameof(GroundParallax));
            UpdateTestStartNote();
            UpdateNewMapNote();
        }
        finally { _loadingSettings = false; }
    }

    private (int, int, int, int, int) GraphicsOf(L2MapEdit edit) => (edit.BlockSetIndex, edit.TileSetIndex, edit.AnimIndex, edit.PaletteSetIndex, edit.ColourAnimIndex);

    // rebuilds the graphics list (from the ROM: what each map uses now, this map as edited) and picks this map's one
    private void LoadGraphicsChoices()
    {
        var keepSource = BlockSource > 0 && BlockSource <= _sources.Count ? _sources[BlockSource - 1].Key : ((int, int, int, int, int)?)null;
        GraphicsChoices.Clear(); _graphics.Clear(); BlockSourceChoices.Clear(); _sources.Clear();
        OnPropertyChanged(nameof(HasBlockSources));
        if (_rom == null || _edit == null || _edit.IsWorld) { GraphicsChoice = -1; BlockSource = -1; return; }
        var groups = Enumerable.Range(2, L2Maps.Count(_rom) - 2)
            .Where(m => m != _edit.MapId && !L2NewMaps.IsFree(_rom, m))
            .GroupBy(m => (Bs: (int)_rom.ReadByte(L2Maps.BlockSetAt(_rom, m)), Ts: (int)_rom.ReadByte(L2Maps.TileSetAt(_rom, m)),
                           Anim: (int)_rom.ReadByte(L2Maps.AnimIndexAt(_rom, m)), Ps: (int)_rom.ReadByte(L2Maps.PaletteSetAt(_rom, m)),
                           Ca: (int)_rom.ReadByte(L2Maps.ColourAnimAt(_rom, m))))
            .ToDictionary(g => g.Key, g => g.ToList());
        var mine = GraphicsOf(_edit);
        if (!groups.ContainsKey(mine)) groups[mine] = new();
        foreach (var (mix, maps) in groups.OrderBy(g => g.Value.Count == 0 ? -1 : g.Value[0]))
        {
            string who = maps.Count == 0 ? "Only this map"
                : NameOf(maps[0]) + (maps.Count > 1 ? $" and {maps.Count - 1} other map{(maps.Count == 2 ? "" : "s")}" : "") + (mix == mine ? " (and this map)" : "");
            _graphics.Add(mix);
            GraphicsChoices.Add($"{who}   (blocks {mix.Bs}, tiles {mix.Ts}, colours {mix.Ps})");
            if (mix != mine && maps.Count > 0) _sources.Add((mix, maps[0]));
        }
        GraphicsChoice = _graphics.IndexOf(mine);
        BlockSourceChoices.Add("This map's graphics");
        foreach (var source in _sources) BlockSourceChoices.Add(GraphicsChoices[_graphics.IndexOf(source.Key)]);
        BlockSource = keepSource is { } keptMix && _sources.FindIndex(s => s.Key == keptMix) is var sourceIndex and >= 0 ? sourceIndex + 1 : 0;
        OnPropertyChanged(nameof(HasBlockSources));
    }

    partial void OnGraphicsChoiceChanged(int value)
    {
        if (_loadingSettings || _edit == null || value < 0 || value >= _graphics.Count) return;
        var mix = _graphics[value];
        var before = _edit.Save();
        _edit.BlockSetIndex = mix.Bs; _edit.TileSetIndex = mix.Ts; _edit.AnimIndex = mix.Anim; _edit.PaletteSetIndex = mix.Ps; _edit.ColourAnimIndex = mix.Ca;
        if (L2MapEdit.Same(before, _edit.Save())) return;
        _history.Add((before, "changing the graphics")); _redo.Clear();
        _stats.Clear();
        ReloadBlocks(); Refresh();
        Status = $"The map is drawn with the graphics of {GraphicsChoices[value].Split("   ")[0]} now: their blocks, tiles, colours and animations (written with the map; Undo takes it back). " +
                 "The map keeps its block numbers, so on other blocks it looks scrambled until you paint it again. " +
                 "To keep this map's look and paint with other graphics' blocks too, pick them under Blocks, From.";
    }

    // on = the ground layer scrolls as a parallax background far below (the game's mountains, towers and Shrine of Daos
    // floors, where the party looks down past ledges). off = it scrolls with the map like in towns. a map copied from
    // one of those floors but drawn as one flat place shows out of line with its walls in the game while it's on
    public bool GroundParallax
    {
        get => _edit != null && !_edit.IsWorld && (_edit.GroundMode & 0xFF) != 0;
        set
        {
            if (_edit == null || _edit.IsWorld || _loadingSettings || value == GroundParallax) return;
            var before = _edit.Save();
            if (!value) _parallaxWas = _edit.GroundMode;
            _edit.GroundMode = value ? (_parallaxWas & 0xFF) != 0 ? _parallaxWas : L2Maps.ParallaxMode : 0;
            _history.Add((before, value ? "making the ground a far-off background" : "making the ground scroll with the map")); _redo.Clear();
            OnPropertyChanged();
            UpdateUndoState();
            Status = value
                ? "The ground layer scrolls as a far-off background in the game (as on mountains and towers): it moves slower than the walls and the upper layer. Written with the map; Undo takes it back."
                : "The ground layer scrolls with the map in the game, in line with its walls, as in towns. Written with the map; Undo takes it back.";
        }
    }
    private int _parallaxWas;

    [RelayCommand]
    private void ResizeMap()
    {
        if (_edit == null) return;
        if (MapWidth == _edit.Width && MapHeight == _edit.Height) { Status = "Type the new width and height first."; return; }
        var before = _edit.Save();
        int fill = FillBlock();
        try { _edit.Resize(MapWidth, MapHeight, fill); }
        catch (Exception ex) { Status = ex.Message; MapWidth = _edit.Width; MapHeight = _edit.Height; return; }
        _history.Add((before, "resizing the map")); _redo.Clear();
        Refresh();
        Status = $"The map is {_edit.Width} x {_edit.Height} now (new blocks: block {fill:X3} on the ground{(fill != SelectedBlock ? ", as the brush is block 000, which the game draws nothing for" : ", the brush block")}). Exits, characters and areas outside it stay where they were: move them in.";
    }

    // the block new ground gets: the brush, but not block 0 (the game draws nothing for it, so the screen shows what was
    // there before). in that case the block the map's edges use most, else block 1
    private int FillBlock()
    {
        if (SelectedBlock != 0 || _edit == null) return SelectedBlock;
        var ground = _edit.Map.Layers[0];
        var edge = Enumerable.Range(0, ground.Width).SelectMany(x => new[] { ground.Blocks[x], ground.Blocks[(ground.Height - 1) * ground.Width + x] })
            .Concat(Enumerable.Range(0, ground.Height).SelectMany(y => new[] { ground.Blocks[y * ground.Width], ground.Blocks[y * ground.Width + ground.Width - 1] }));
        return edge.Where(b => b != 0).GroupBy(b => b).OrderByDescending(g => g.Count()).Select(g => g.Key).DefaultIfEmpty(1).First();
    }

    private void UpdateTestStartNote()
    {
        if (_rom == null) { TestStartNote = ""; return; }
        var (startMap, scene, point) = L2MapEdit.NewGame(_rom);
        TestStartNote = startMap == 0x02 && scene == 0x01 && point == 0x06 ? "A new game starts as in the game (the opening)."
                      : $"A new game starts on map {startMap:X2} {NameOf(startMap)} at point {point} (scene {scene:X2}).";
        if (_edit != null && TestStartPoint == 0) TestStartPoint = _edit.Records(L2MapEdit.Points).Select(r => (int)r.Bytes[0]).DefaultIfEmpty(0).First();
    }

    [RelayCommand]
    private void SetTestStart()
    {
        if (_rom == null || _main == null || _edit == null) return;
        if (!_edit.Records(L2MapEdit.Points).Any(r => r.Bytes[0] == TestStartPoint)) { Status = $"The map has no preset point {TestStartPoint}: add one with the Add point tool (A), or pick another number."; return; }
        if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
        try
        {
            Status = L2MapEdit.SetTestStart(_rom, _edit.MapId, TestStartPoint) + " Set the normal start again before you share the game.";
            _main.NotifyRomModified($"Lufia II: test start on map {_edit.MapId:X2}");
        }
        catch (Exception ex) { Status = "Not set: " + ex.Message; }
        UpdateTestStartNote();
    }

    [RelayCommand]
    private void SetNormalStart()
    {
        if (_rom == null || _main == null) return;
        L2MapEdit.SetNormalStart(_rom);
        _main.NotifyRomModified("Lufia II: normal new-game start");
        UpdateTestStartNote();
        Status = "A new game starts as in the game again.";
    }

    // new maps

    [ObservableProperty] private int _newMapWidth = 32, _newMapHeight = 28;
    [ObservableProperty] private string _newMapNote = "";
    public bool CanDeleteMap => _rom != null && SelectedIndex >= L2NewMaps.GameCount && SelectedIndex < L2Maps.Count(_rom) && !L2NewMaps.IsFree(_rom, SelectedIndex);

    private void UpdateNewMapNote()
    {
        OnPropertyChanged(nameof(CanDeleteMap));
        if (_rom == null) { NewMapNote = ""; return; }
        int made = L2Maps.Count(_rom) - L2NewMaps.GameCount - Enumerable.Range(L2NewMaps.GameCount, L2Maps.Count(_rom) - L2NewMaps.GameCount).Count(m => L2NewMaps.IsFree(_rom, m));
        NewMapNote = (L2NewMaps.CantInstall(_rom) is { } why ? why + " " : "") +
            $"New maps take the numbers F2-FF ({made} of {L2NewMaps.Slots} used). A copy has this map's blocks, graphics, exits, characters, " +
            "events, treasure (with new opened flags) and puzzles; a blank map has this map's graphics, music and settings, the size typed, " +
            "the brush block on the ground, and preset point 0 in the middle. Then point an exit at it (Pick on map...). In the game it has this map's name until you give it one (Rename or delete this map, under the list).";
    }

    [RelayCommand] private void NewMapCopy() => MakeNewMap(blank: false);
    [RelayCommand] private void NewMapBlank() => MakeNewMap(blank: true);

    private void MakeNewMap(bool blank)
    {
        if (_rom == null || _main == null || _edit == null) { Status = "Pick a town or dungeon map first (the world maps can't be copied)."; return; }
        if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
        int template = _edit.MapId;
        string from = NameOf(template);
        int newMap;
        try { newMap = L2NewMaps.Create(_rom, template, blank, NewMapWidth, NewMapHeight, FillBlock()); }
        catch (Exception ex) { Status = "No new map: " + ex.Message; return; }
        if (LufiaForge.Core.Maps.MapNames.ProjectNames != null)
            LufiaForge.Core.Maps.MapNames.SetProjectName(newMap, blank ? $"New map (graphics of {from})" : $"{from} (copy)");
        _main.NotifyRomModified(blank ? $"Lufia II: new blank map {newMap:X2} ({NewMapWidth} x {NewMapHeight}, graphics of {template:X2})" : $"Lufia II: new map {newMap:X2}, a copy of {template:X2} {from}");
        ReloadNames();
        SelectedIndex = newMap;
        Status = $"Map {newMap:X2} made ({(blank ? "blank, with the graphics of " : "a copy of ")}{template:X2} {from}). Nothing leads to it yet: point an exit at it (Select an exit, Pick on map...) or start a test game on it.";
    }

    [RelayCommand]
    private void DeleteMap()
    {
        if (_rom == null || _main == null || !CanDeleteMap) { Status = "Only maps made with New map (F2 up) can be deleted."; return; }
        int id = SelectedIndex;
        string label = $"{id:X2} {NameOf(id)}";
        Status = "Looking for what leads to this map...";
        var refs = L2NewMaps.References(_rom, id, NameOf);
        bool isLast = id == L2Maps.Count(_rom) - 1;
        string text = $"Delete {label}?\n\n" +
            (isLast ?"Its number becomes free for the next new map." : "Its number stays taken by a small empty map until the next New map uses it (so the maps after it keep their numbers).") +
            (refs.Count == 0 ? "\n\nNothing else leads to it." :
                "\n\nThese still lead to it and would take the party to an empty map until you change them:\n  " + string.Join("\n  ", refs.Take(15)) +
                (refs.Count > 15 ? $"\n  ...and {refs.Count - 15} more" : "")) +
            "\n\nEdit > Undo takes it back.";
        if (System.Windows.MessageBox.Show(text, "Delete map", System.Windows.MessageBoxButton.OKCancel,
                refs.Count == 0 ? System.Windows.MessageBoxImage.Question : System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.Cancel) != System.Windows.MessageBoxResult.OK) { Status = "Not deleted."; return; }
        _edit = null; _history.Clear(); _redo.Clear();
        try { L2NewMaps.Delete(_rom, id); }
        catch (Exception ex) { Status = "Not deleted: " + ex.Message; return; }
        if (LufiaForge.Core.Maps.MapNames.ProjectNames != null) LufiaForge.Core.Maps.MapNames.SetProjectName(id, "");
        _main.NotifyRomModified($"Lufia II: deleted map {id:X2}");
        ReloadNames();
        SelectedIndex = -1;
        SelectedIndex = 3;
        Status = $"Map {id:X2} deleted" + (refs.Count > 0 ? $"; {refs.Count} exit(s) / event(s) still lead to it: point them elsewhere." : ".");
    }

    // rebuilds the map list (after a map was made or deleted)
    private void ReloadNames()
    {
        _names = Enumerable.Range(0, L2Maps.Count(_rom!)).Select(id => RomName(_rom!, id)).ToList();
        DestinationChoices.Clear();
        FillList();
    }

    [RelayCommand]
    private void AddCharacterNearView() { Tool = L2Tool.AddCharacter; Status = "Click the block where the new character stands."; }
}
