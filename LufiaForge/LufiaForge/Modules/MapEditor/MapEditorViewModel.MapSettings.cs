using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;
using System.Collections.ObjectModel;
using System.Windows;

namespace LufiaForge.Modules.MapEditor;

/// <summary>Map settings (tileset, palette, size), new maps and tilesets, and adding / hiding characters.</summary>
public partial class MapEditorViewModel
{
    public ObservableCollection<string> TilesetChoices { get; } = new();
    public ObservableCollection<string> SpriteChoices { get; } = new();
    public ObservableCollection<string> PaletteChoices { get; } = new();

    /// <summary>Picking a tileset or palette shows the map with it at once (Apply writes it; Undo takes it back).</summary>
    partial void OnTilesetChoiceChanged(int value) => PreviewGraphics();
    partial void OnMapPaletteChanged(int value) => PreviewGraphics();

    private void PreviewGraphics()
    {
        if (_populating || _rom == null || _map == null || _map.IsWorld) return;
        if (TilesetChoice < 0 || TilesetChoice >= Tilesets.Count(_rom) || MapPalette < 0 || MapPalette >= LufiaMap.MapPaletteCount) return;
        if (TilesetChoice == _map.Tileset && MapPalette == _map.PaletteIndex) return;
        try
        {
            _map.SetTilesetAndPalette(TilesetChoice, MapPalette);
            _tileset = MapTileset.ForMap(_rom, _map);
            BuildPalette();
            OnPropertyChanged(nameof(BrushText));
            RedrawMap();
            RebuildOverlays();
            MarkEdited();
            Status = $"Map {_map.MapId:X2} shown with tileset {TilesetChoice}, palette {MapPalette}. Apply to ROM to keep it (Undo takes it back). " +
                     "Block numbers stay the same, so a tileset made for other maps can look scrambled here.";
        }
        catch (Exception ex) { Status = "Tileset: " + ex.Message; }
    }

    [ObservableProperty] private int _tilesetChoice;
    [ObservableProperty] private int _mapPalette;
    [ObservableProperty] private int _mapWidth;
    [ObservableProperty] private int _mapHeight;
    [ObservableProperty] private bool _isTownMap;
    [ObservableProperty] private int _npcSprite = -1;
    [ObservableProperty] private string _newMapNote = "";

    public string[] TestStartModes { get; } = { "Normal start (off)", "Fortress of Doom (prologue)", "Skip the prologue", "This map" };
    [ObservableProperty] private int _testStartMode;
    [ObservableProperty] private int _testStartArrival = 1;
    [ObservableProperty] private string _testStartNote = "";

    private void RefreshTestStart()
    {
        if (_rom == null) return;
        var mode = TestStartPatch.Current(_rom);
        var (m, e) = TestStartPatch.Target(_rom);
        bool old = TestStartPatch.IsOldStyle(_rom);
        _populating = true;
        TestStartMode = (int)mode;
        if (mode == TestStartPatch.Mode.ChosenMap) TestStartArrival = Math.Max(1, e);
        _populating = false;
        TestStartNote = mode switch
        {
            TestStartPatch.Mode.Off => "Power-on plays the opening as usual.",
            TestStartPatch.Mode.Fortress => "Power-on goes straight to the file select (Initial = new game with the Hero's name, Continue = load a save); a new game starts the prologue at the Fortress of Doom.",
            TestStartPatch.Mode.SkipPrologue => "Power-on goes straight to the file select; a new game (after the Hero's name) skips the prologue: the Hero's game starts.",
            _ => $"Power-on goes straight to the file select; a new game puts the prologue party on map {m:X2} at arrival {e} (after the prologue's first scene).",
        } + (old ? " (Made by an older Lufia Forge that skipped the game's start-up: click Apply to update it.)" : "");
    }

    /// <summary>Sets the "test start" patch (see <see cref="TestStartPatch"/>).</summary>
    [RelayCommand]
    private void ApplyTestStart()
    {
        if (_rom == null) return;
        var mode = (TestStartPatch.Mode)Math.Clamp(TestStartMode, 0, 3);
        if (mode == TestStartPatch.Mode.ChosenMap)
        {
            if (_map == null) { Status = "Open the map to start on."; return; }
            if (TestStartArrival < 1 || _map != null && TestStartArrival > _map.Arrivals.Count) { Status = $"This map has {_map?.Arrivals.Count} arrival points."; return; }
        }
        try
        {
            TestStartPatch.Set(_rom, mode, _map?.MapId ?? 0, TestStartArrival);
            _mainVm?.NotifyRomModified("Test start: " + TestStartModes[(int)mode]);
            RefreshTestStart();
            Status = TestStartNote + " Save the ROM and reset the game to use it.";
        }
        catch (Exception ex) { Status = "Test start: " + ex.Message; }
    }

    /// <summary>Fills the map settings from the loaded map (called after a map loads).</summary>
    private void RefreshMapSettings()
    {
        if (_rom == null || _map == null) return;
        IsTownMap = !_map.IsWorld;
        TilesetChoices.Clear();
        for (int t = 0, n = Tilesets.Count(_rom); t < n; t++) TilesetChoices.Add($"Tileset {t}" + TilesetUsers(t));
        if (PaletteChoices.Count != LufiaMap.MapPaletteCount)
        {
            PaletteChoices.Clear();
            for (int p = 0; p < LufiaMap.MapPaletteCount; p++) PaletteChoices.Add($"Palette {p}");
        }
        if (SpriteChoices.Count != NpcSprites.Count(_rom))
        {
            SpriteChoices.Clear();
            for (int s = 0, n = NpcSprites.Count(_rom); s < n; s++) SpriteChoices.Add($"{s:X2}");
        }
        _populating = true;
        TilesetChoice = _map.IsWorld ? -1 : _map.Tileset;
        MapPalette = _map.PaletteIndex;
        MapWidth = _map.Width; MapHeight = _map.Height;
        _populating = false;
        NewMapNote = $"{MapFactory.FreeIds(_rom).Count} free map numbers left.";
        RefreshTestStart();
    }

    /// <summary>" (Alekia, Treck…)": the first maps that use a tileset, to recognise it in the list.</summary>
    private string TilesetUsers(int t)
    {
        var users = Maps.Where(m => m.MapId != LufiaMap.WorldMap && m.Tileset == t).Take(3).Select(m => string.IsNullOrEmpty(m.Name) ? $"map {m.MapId:X2}" : m.Name).ToList();
        int more = Maps.Count(m => m.MapId != LufiaMap.WorldMap && m.Tileset == t) - users.Count;
        return users.Count == 0 ? "  (no map uses it)" : $"  ({string.Join(", ", users)}{(more > 0 ? $" +{more}" : "")})";
    }

    /// <summary>Writes the tileset, palette and size (and any other edits of this map).</summary>
    [RelayCommand]
    private void ApplyMapSettings()
    {
        if (_rom == null || _map == null || _map.IsWorld) return;
        if (MapPalette < 0 || MapPalette >= LufiaMap.MapPaletteCount) { Status = $"Pick a palette 0-{LufiaMap.MapPaletteCount - 1} (the game has {LufiaMap.MapPaletteCount})."; return; }
        try
        {
            if (MapWidth != _map.Width || MapHeight != _map.Height)
            {
                if (MessageBox.Show($"Resize map {_map.MapId:X2} from {_map.Width}x{_map.Height} to {MapWidth}x{MapHeight} blocks? New cells get the brush block ({Brush:X3}); " +
                                    "cut-off cells are lost (characters, exits and arrival points outside stay where they are: move them in).", "Resize map",
                                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                _map.Resize(MapWidth, MapHeight, Brush);
            }
            if (TilesetChoice < 0 || TilesetChoice >= Tilesets.Count(_rom)) { Status = "Pick a tileset."; return; }
            _map.SetTilesetAndPalette(TilesetChoice, MapPalette);
            MarkEdited();
            ApplyToRom();
        }
        catch (Exception ex) { Status = "Map settings: " + ex.Message; }
    }

    /// <summary>A new tileset copied from this map's (graphics and blocks), used by this map from now on.</summary>
    [RelayCommand]
    private void NewTileset()
    {
        if (_rom == null || _map == null || _map.IsWorld) return;
        if (HasUnappliedChanges) { Status = "Apply or revert this map's changes first."; return; }
        if (MessageBox.Show($"Make a new tileset as a copy of tileset {_map.Tileset} and use it for map {_map.MapId:X2}? " +
                            "Then editing its blocks and tiles changes only the maps you give it.", "New tileset",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            int t = Tilesets.Add(_rom, _map.Tileset);
            _map.SetTilesetAndPalette(t, _map.PaletteIndex);
            MarkEdited();
            ApplyToRom();
            _mainVm?.NotifyRomModified($"New tileset {t}");
            Status = $"Tileset {t} made (a copy of the old one) and given to map {_map?.MapId:X2}. Edit it with Edit blocks.";
        }
        catch (Exception ex) { Status = "New tileset: " + ex.Message; }
    }

    /// <summary>Deletes the open map (after listing what still leads to it).</summary>
    [RelayCommand]
    private void DeleteMap()
    {
        if (_rom == null || _map == null) return;
        int id = _map.MapId;
        if (_map.IsWorld) { Status = "The world map can't be deleted."; return; }
        string label = SelectedMap?.Label ?? $"map {id:X2}";
        Status = "Looking for what leads to this map...";
        string Label(int m) => Maps.FirstOrDefault(x => x.MapId == m)?.Label ?? $"map {m:X2}";
        var refs = MapFactory.References(_rom, id, Label);
        bool mine = MapFactory.IsNewMap(_rom, id);
        string text = $"Delete {label}?\n\n" +
            (mine ? "It was made with Lufia Forge; its number becomes free for a new map and its space is given back."
                  : "It is one of the game's own maps: the story may take the party there, and deleting it can break the game.") +
            (refs.Count == 0 ? "\n\nNothing else leads to it." :
                $"\n\nThese still lead to it and would crash the game until you change them:\n  " + string.Join("\n  ", refs.Take(15)) +
                (refs.Count > 15 ? $"\n  ...and {refs.Count - 15} more" : "")) +
            "\n\nEdit > Undo takes it back.";
        if (MessageBox.Show(text, "Delete map", MessageBoxButton.OKCancel, mine && refs.Count == 0 ? MessageBoxImage.Question : MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK) { Status = "Not deleted."; return; }
        try
        {
            HasUnappliedChanges = false;
            MapFactory.Delete(_rom, id);
            var names = MapNames.LoadCustom();
            if (names.Remove(id)) MapNames.SaveCustom(names);
            _mainVm?.NotifyRomModified($"Deleted map {id:X2}");
            Maps.Clear();
            foreach (var m in MapCatalog.ScanNamed(_rom)) Maps.Add(m);
            _generatedNames = MapNames.Generate(_rom, Maps.Select(m => m.MapId).ToList());
            _links = MapLinks.Build(_rom, Maps.Select(m => m.MapId));
            BuildDestinationChoices();
            SelectedMap = Maps.FirstOrDefault(m => m.MapId == 0x04) ?? Maps.FirstOrDefault();
            Status = $"Map {id:X2} deleted" + (refs.Count > 0 ? $"; {refs.Count} exit(s) / event(s) still lead to it: point them elsewhere." : ".");
        }
        catch (Exception ex) { Status = "Delete map: " + ex.Message; }
    }

    [RelayCommand] private void NewMapCopy() => NewMap(blank: false);
    [RelayCommand] private void NewMapBlank() => NewMap(blank: true);

    private void NewMap(bool blank)
    {
        if (_rom == null || _map == null || _map.IsWorld) { Status = "Open a town or dungeon map to start from."; return; }
        if (HasUnappliedChanges) { Status = "Apply or revert this map's changes first."; return; }
        string what = blank
            ? $"a new blank map with map {_map.MapId:X2}'s size and graphics, filled with block {Brush:X3} (no characters, exits or step-on areas; one arrival point in the middle; its own empty events)"
            : $"a new map copied from map {_map.MapId:X2} (layout, characters, exits; its own copy of the events; chests and doors are shared with map {_map.MapId:X2})";
        if (MessageBox.Show($"Make {what}?\n\nConnect it afterwards: point an exit of another map at it.", "New map",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            int id = blank ? MapFactory.Blank(_rom, _map.MapId, Brush) : MapFactory.Copy(_rom, _map.MapId);
            Maps.Clear();
            foreach (var m in MapCatalog.ScanNamed(_rom)) Maps.Add(m);
            _generatedNames = MapNames.Generate(_rom, Maps.Select(m => m.MapId).ToList());
            _links = MapLinks.Build(_rom, Maps.Select(m => m.MapId));
            BuildDestinationChoices();
            _mainVm?.NotifyRomModified($"New map {id:X2}");
            SelectedMap = Maps.FirstOrDefault(m => m.MapId == id);
            Status = $"Map {id:X2} made. Point an exit of another map at it to walk in (select an exit > Leads to map).";
        }
        catch (Exception ex) { Status = "New map: " + ex.Message; }
    }

    /// <summary>Adds a character in the middle of the map (or at the first arrival point).</summary>
    [RelayCommand]
    private void AddNpc()
    {
        if (_rom == null || _map == null || _map.IsWorld) return;
        try
        {
            int x = _map.Width / 2, y = _map.Height / 2;
            if (_map.Arrivals.Count > 0) { x = Math.Min(_map.Width - 1, _map.Arrivals[0].X + 1); y = _map.Arrivals[0].Y; }
            int slots = EventScript.SlotCount(_rom, _map.MapId);
            if (_map.Npcs.Count >= slots)
            {
                // talking to it would run event n, past the map's event table: the game would read code as a pointer
                Status = $"This map's events have {slots} slots, so it can have at most {slots} characters (talking to character n runs event n).";
                return;
            }
            var n = _map.AddNpc(NpcSprite >= 0 ? NpcSprite : 0x0C, x, y);
            Status = $"Character {n.Index + 1} added (talking runs event {n.Index}; an empty one is made when you Apply). Drag it into place, then Apply.";
            MarkEdited();
            RebuildOverlays();
            SelectNpc(n);
        }
        catch (Exception ex) { Status = "Add character: " + ex.Message; }
    }

    /// <summary>Hides the selected character (removes it if it is the last one).</summary>
    [RelayCommand]
    private void HideNpc()
    {
        if (_map == null || _selection is not { kind: "npc" } sel) return;
        var n = _map.Npcs[sel.index];
        bool last = _map.Npcs.Count > 0 && _map.Npcs[^1] == n;
        _map.RemoveNpc(n);
        MarkEdited();
        ClearSelection();
        RebuildOverlays();
        Status = last ? "Character removed (not yet applied)." : "Character hidden (it keeps its number so the others keep their events; not yet applied).";
    }

    partial void OnNpcSpriteChanged(int value)
    {
        if (_populating || _map == null || value < 0 || _selection is not { kind: "npc" } sel) return;
        var n = _map.Npcs[sel.index];
        if (n.Sprite == value) return;
        n.Sprite = value;
        MarkEdited();
        RebuildOverlays();
        Status = $"Character {sel.index + 1} now uses sprite {value:X2} (not yet applied).";
    }

    /// <summary>Creates an empty talk event for the selected character, then opens it.</summary>
    private bool CreateTalkEvent(int ev)
    {
        if (_rom == null || _map == null) return false;
        if (HasUnappliedChanges) { Status = "Apply this map's changes first."; return false; }
        if (MessageBox.Show($"Character {ev + 1} has no event yet. Make one (talking to it runs event {ev})?", "New event",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return false;
        try
        {
            EventScript.CreateEvent(_rom, _map.MapId, ev);
            _mainVm?.NotifyRomModified($"Map {_map.MapId:X2} event {ev}");
            return true;
        }
        catch (Exception ex) { Status = "New event: " + ex.Message; return false; }
    }
}
