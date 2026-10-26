using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

public partial class L2MapsPage
{
    // ---------------------------------------------------------------- graphics, size, test start

    public ObservableCollection<string> BlockSetChoices { get; } = new();
    public ObservableCollection<string> TileSetChoices { get; } = new();
    public ObservableCollection<string> PaletteSetChoices { get; } = new();
    [ObservableProperty] private int _blockSetChoice = -1, _tileSetChoice = -1, _paletteSetChoice = -1;
    [ObservableProperty] private int _mapWidth, _mapHeight;
    [ObservableProperty] private int _testStartPoint;
    [ObservableProperty] private string _testStartNote = "";
    private bool _loadingSettings;

    // the game's own sets (1 up): 27 block sets, 20 tile sets, 87 palette sets
    private const int BlockSets = 27, TileSets = 20, PaletteSets = 87;

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            if (BlockSetChoices.Count == 0 && _rom != null)
            {
                var uses = Enumerable.Range(2, L2Maps.Count - 2).ToList();
                string Used(int list, int v) { var m = uses.Where(id => _rom.ReadByte(list + id) == v).ToList(); return m.Count == 0 ? "" : $"  ({m.Count} map{(m.Count == 1 ? "" : "s")}, e.g. {NameOf(m[0])})"; }
                for (int i = 1; i <= BlockSets; i++) BlockSetChoices.Add($"{i}{Used(L2Maps.BlockSetList, i)}");
                for (int i = 1; i <= TileSets; i++) TileSetChoices.Add($"{i}{Used(L2Maps.TileSetList, i)}");
                for (int i = 1; i <= PaletteSets; i++) PaletteSetChoices.Add($"{i}{Used(L2Maps.PaletteSetList, i)}");
            }
            BlockSetChoice = _edit?.BlockSetIndex - 1 ?? -1;
            TileSetChoice = _edit?.TileSetIndex - 1 ?? -1;
            PaletteSetChoice = _edit?.PaletteSetIndex - 1 ?? -1;
            MapWidth = _edit?.Width ?? 0; MapHeight = _edit?.Height ?? 0;
            UpdateTestStartNote();
        }
        finally { _loadingSettings = false; }
    }

    partial void OnBlockSetChoiceChanged(int value) => ChangeSets("the block set", e => e.BlockSetIndex = value + 1, value);
    partial void OnTileSetChoiceChanged(int value) => ChangeSets("the tile set", e => e.TileSetIndex = value + 1, value);
    partial void OnPaletteSetChoiceChanged(int value) => ChangeSets("the palette set", e => e.PaletteSetIndex = value + 1, value);

    private void ChangeSets(string what, Action<L2MapEdit> set, int value)
    {
        if (_loadingSettings || _edit == null || value < 0) return;
        var before = _edit.Save();
        set(_edit);
        if (L2MapEdit.Same(before, _edit.Save())) return;
        _history.Add((before, "changing " + what)); _redo.Clear();
        _stats.Clear();
        ReloadBlocks(); Refresh();
        Status = $"The map is drawn with {what} {value + 1} now (written with the map; Undo takes it back). Block sets and tile sets go in pairs: a block set drawn with another tile set looks scrambled.";
    }

    [RelayCommand]
    private void ResizeMap()
    {
        if (_edit == null) return;
        if (MapWidth == _edit.Width && MapHeight == _edit.Height) { Status = "Type the new width and height first."; return; }
        var before = _edit.Save();
        try { _edit.Resize(MapWidth, MapHeight, SelectedBlock); }
        catch (Exception ex) { Status = ex.Message; MapWidth = _edit.Width; MapHeight = _edit.Height; return; }
        _history.Add((before, "resizing the map")); _redo.Clear();
        Refresh();
        Status = $"The map is {_edit.Width} x {_edit.Height} now (new blocks: the brush block on the ground). Exits, characters and areas outside it stay where they were: move them in.";
    }

    private void UpdateTestStartNote()
    {
        if (_rom == null) { TestStartNote = ""; return; }
        var (m, s, p) = L2MapEdit.NewGame(_rom);
        TestStartNote = m == 0x02 && s == 0x01 && p == 0x06 ? "A new game starts as in the game (the opening)."
                      : $"A new game starts on map {m:X2} {NameOf(m)} at point {p} (scene {s:X2}).";
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

    [RelayCommand]
    private void AddCharacterNearView() { Tool = L2Tool.AddCharacter; Status = "Click the block where the new character stands."; }
}
