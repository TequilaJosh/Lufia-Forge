using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

// where a new game starts: a map, one of its cutscenes (or none), the point the party stands on and the story
// flags that are on. the flags the cutscene checks get ticked so it plays (worked out by playing it back)
public partial class L2GameStartPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private bool _loading;
    private string[] _mapNames = Array.Empty<string>();

    public ObservableCollection<L2Option> MapChoices { get; } = new();
    public ObservableCollection<L2GameStartChoice> CutsceneChoices { get; } = new();
    public ObservableCollection<L2Option> PointChoices { get; } = new();
    // the story flags the cutscene checks, ticked when they should be on for it to play
    public ObservableCollection<L2Flag> Flags { get; } = new();

    [ObservableProperty] private L2Option? _selectedMap;
    [ObservableProperty] private L2GameStartChoice? _selectedCutscene;
    [ObservableProperty] private L2Option? _selectedPoint;
    // more story flags on at the start (hex, spaces between), the story so far
    [ObservableProperty] private string _otherFlags = "";
    [ObservableProperty] private string _current = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _flagsNote = "";
    [ObservableProperty] private string _pointNote = "";
    [ObservableProperty] private bool _hasFlags;

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        ReadMapChoices();
        Status = "";
        var (map, _, _) = L2MapEdit.NewGame(rom);
        SelectedMap = MapChoices.FirstOrDefault(o => o.Value == map) ?? MapChoices.FirstOrDefault();
        ShowCurrent();
    }

    // when the page is shown, read the start again (the Cutscenes page's Test in the game changes it) and the
    // map's cutscenes, keeping the picks
    public void Refresh()
    {
        if (_rom == null) return;
        var keepCutscene = SelectedCutscene?.Choice;
        var keepPoint = SelectedPoint?.Value;
        // maps that got their first preset point (or were made) since then can be picked too
        int map = SelectedMap?.Value ?? -1;
        ReadMapChoices();
        if (SelectedMap == null || SelectedMap.Value != map) SelectedMap = MapChoices.FirstOrDefault(o => o.Value == map) ?? MapChoices.FirstOrDefault();
        OnSelectedMapChanged(SelectedMap);
        if (keepCutscene != null) SelectedCutscene = CutsceneChoices.FirstOrDefault(c => c.Choice.Head == keepCutscene.Head && c.Choice.Id == keepCutscene.Id) ?? SelectedCutscene;
        if (keepPoint != null) SelectedPoint = PointChoices.FirstOrDefault(o => o.Value == keepPoint) ?? SelectedPoint;
        ShowCurrent();
    }

    // the maps a new game can start on (they need an event block and a preset point)
    private void ReadMapChoices()
    {
        var rom = _rom!;
        _loading = true;
        try
        {
            _mapNames = Enumerable.Range(0, L2Maps.Count(rom)).Select(m => L2Maps.Name(rom, m)).ToArray();
            var list = new List<L2Option>();
            for (int m = 2; m < _mapNames.Length; m++)
                if (L2Events.Block(rom, m) >= 0 && L2StoryClear.PresetPoints(rom, m).Count > 0) list.Add(new L2Option(m, $"{m:X2} {_mapNames[m]}"));
            if (list.Select(o => o.Label).SequenceEqual(MapChoices.Select(o => o.Label))) return;   // same list, so the pick stays as it is
            MapChoices.Clear();
            foreach (var option in list) MapChoices.Add(option);
        }
        finally { _loading = false; }
    }

    private string MapName(int map) => map < _mapNames.Length ? _mapNames[map] : "";

    private void ShowCurrent()
    {
        if (_rom == null) return;
        Current = L2GameStart.Describe(_rom, MapName);
        SetStartCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedMapChanged(L2Option? value)
    {
        CutsceneChoices.Clear(); PointChoices.Clear();
        if (_rom == null || value == null) return;
        foreach (var cutscene in L2GameStart.Cutscenes(_rom, value.Value, L2EventCommands.DefaultWho)) CutsceneChoices.Add(new L2GameStartChoice(cutscene));
        foreach (var preset in L2StoryClear.PresetPoints(_rom, value.Value)) PointChoices.Add(new L2Option(preset.Id, $"point {preset.Id} ({preset.X}, {preset.Y})"));
        SelectedCutscene = CutsceneChoices.FirstOrDefault();
    }

    partial void OnSelectedCutsceneChanged(L2GameStartChoice? value)
    {
        Flags.Clear(); HasFlags = false; FlagsNote = ""; PointNote = "";
        if (_rom == null || SelectedMap == null || value == null) { SetStartCommand.NotifyCanExecuteChanged(); return; }
        int map = SelectedMap.Value;
        var choice = value.Choice;
        // which point: one on a stepping-on area (its walks start there), the one an "on entering" scene uses, or the first
        int point = L2GameStart.PointOnArea(_rom, map, choice);
        if (point < 0 && choice.Head == L2EventBlocks.SceneList && L2Cutscene.Starter(_rom, map, choice.Id) is { } starter) point = starter.Point;
        SelectedPoint = PointChoices.FirstOrDefault(o => o.Value == point) ?? SelectedPoint ?? PointChoices.FirstOrDefault();
        if (choice.Head == L2EventBlocks.TriggerList)
            PointNote = point >= 0 ? "A point on the cutscene's area is picked: its walks start from there." : "No preset point is on the cutscene's area: add one on the Maps page, or its walks start from elsewhere.";
        var startPoint = L2StoryClear.PresetPoints(_rom, map).FirstOrDefault(x => x.Id == (SelectedPoint?.Value ?? -1));
        var checkedFlags = L2GameStart.CheckedFlags(_rom, map, choice);
        var best = L2GameStart.BestFlags(_rom, map, choice, startPoint.X, startPoint.Y);
        foreach (int flag in checkedFlags)
            Flags.Add(new L2Flag { Label = $"Story flag {flag:X2} on", Bit = flag, IsChecked = best.Contains(flag) });
        HasFlags = Flags.Count > 0;
        FlagsNote = Flags.Count == 0 ? "" : best.Count > 0 || checkedFlags.Count <= 6
            ? "The cutscene checks these flags. The ticks are the ones it needs to play to the end (worked out by playing it back): change them only to start it another way."
            : "The cutscene checks these flags: tick the ones that should be on.";
        SetStartCommand.NotifyCanExecuteChanged();
    }

    private bool CanSet() => _rom != null && SelectedMap != null && SelectedCutscene != null && SelectedPoint != null;

    [RelayCommand(CanExecute = nameof(CanSet))]
    private void SetStart()
    {
        if (_rom == null || _main == null || SelectedMap == null || SelectedCutscene == null || SelectedPoint == null) return;
        var flags = Flags.Where(f => f.IsChecked).Select(f => f.Bit).ToHashSet();
        foreach (var word in OtherFlags.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(word.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int flag) || flag < 0 || flag > 0xFF) { Status = $"\"{word}\" isn't a story flag (hex, 00-FF)."; return; }
            flags.Add(flag);
        }
        try
        {
            Status = L2GameStart.Set(_rom, SelectedMap.Value, SelectedCutscene.Choice, SelectedPoint.Value, flags.OrderBy(f => f).ToList()) +
                     " Play in BizHawk to try it.";
            _main.NotifyRomModified($"Lufia II: a new game starts on map {SelectedMap.Value:X2}");
        }
        catch (Exception ex) { Status = "Not set: " + ex.Message; }
        ShowCurrent();
    }

    // opens the project's ROM in BizHawk as it is right now
    [RelayCommand]
    private void Play()
    {
        if (_main == null) return;
        Status = _main.PlayInBizHawk();
    }

    [RelayCommand]
    private void NormalStart()
    {
        if (_rom == null || _main == null) return;
        L2MapEdit.SetNormalStart(_rom);
        _main.NotifyRomModified("Lufia II: normal new-game start");
        Status = "A new game starts with the game's own opening again.";
        ShowCurrent();
    }
}

// a cutscene to start with, as the list shows it
public sealed record L2GameStartChoice(L2GameStart.Choice Choice)
{
    public string Label => Choice.Title;
}
