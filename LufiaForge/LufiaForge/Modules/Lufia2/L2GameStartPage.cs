using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>Where a new game starts: a map, a cutscene of it (or none), the point the party stands on and the story
/// flags that are on. The flags the cutscene checks are ticked for it to play (worked out by playing it back).</summary>
public partial class L2GameStartPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private bool _loading;
    private string[] _mapNames = Array.Empty<string>();

    public ObservableCollection<L2Option> MapChoices { get; } = new();
    public ObservableCollection<L2GameStartChoice> CutsceneChoices { get; } = new();
    public ObservableCollection<L2Option> PointChoices { get; } = new();
    /// <summary>The story flags the cutscene checks, ticked when they should be on for it to play.</summary>
    public ObservableCollection<L2Flag> Flags { get; } = new();

    [ObservableProperty] private L2Option? _selectedMap;
    [ObservableProperty] private L2GameStartChoice? _selectedCutscene;
    [ObservableProperty] private L2Option? _selectedPoint;
    /// <summary>More story flags on at the start (hex, spaces between): the story so far.</summary>
    [ObservableProperty] private string _otherFlags = "";
    [ObservableProperty] private string _current = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _flagsNote = "";
    [ObservableProperty] private string _pointNote = "";
    [ObservableProperty] private bool _hasFlags;

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        _loading = true;
        try
        {
            _mapNames = Enumerable.Range(0, L2Maps.Count).Select(m => L2Maps.Name(rom, m)).ToArray();
            MapChoices.Clear();
            for (int m = 2; m < _mapNames.Length; m++)
                if (L2Events.Block(rom, m) >= 0 && L2StoryClear.PresetPoints(rom, m).Count > 0) MapChoices.Add(new L2Option(m, $"{m:X2} {_mapNames[m]}"));
        }
        finally { _loading = false; }
        Status = "";
        var (map, _, _) = L2MapEdit.NewGame(rom);
        SelectedMap = MapChoices.FirstOrDefault(o => o.Value == map) ?? MapChoices.FirstOrDefault();
        ShowCurrent();
    }

    /// <summary>When the page is shown: the start (the Cutscenes page's Test in the game changes it) and the map's
    /// cutscenes read again, keeping the picks.</summary>
    public void Refresh()
    {
        if (_rom == null) return;
        var keep = SelectedCutscene?.Choice;
        var point = SelectedPoint?.Value;
        OnSelectedMapChanged(SelectedMap);
        if (keep != null) SelectedCutscene = CutsceneChoices.FirstOrDefault(c => c.Choice.Head == keep.Head && c.Choice.Id == keep.Id) ?? SelectedCutscene;
        if (point != null) SelectedPoint = PointChoices.FirstOrDefault(o => o.Value == point) ?? SelectedPoint;
        ShowCurrent();
    }

    private string MapName(int m) => m < _mapNames.Length ? _mapNames[m] : "";

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
        foreach (var c in L2GameStart.Cutscenes(_rom, value.Value, L2EventCommands.DefaultWho)) CutsceneChoices.Add(new L2GameStartChoice(c));
        foreach (var p in L2StoryClear.PresetPoints(_rom, value.Value)) PointChoices.Add(new L2Option(p.Id, $"point {p.Id} ({p.X}, {p.Y})"));
        SelectedCutscene = CutsceneChoices.FirstOrDefault();
    }

    partial void OnSelectedCutsceneChanged(L2GameStartChoice? value)
    {
        Flags.Clear(); HasFlags = false; FlagsNote = ""; PointNote = "";
        if (_rom == null || SelectedMap == null || value == null) { SetStartCommand.NotifyCanExecuteChanged(); return; }
        int map = SelectedMap.Value;
        var c = value.Choice;
        // the point: one on a stepping-on area (its walks start there), the one an "on entering" scene uses, or the first
        int point = L2GameStart.PointOnArea(_rom, map, c);
        if (point < 0 && c.Head == L2EventBlocks.SceneList && L2Cutscene.Starter(_rom, map, c.Id) is { } st) point = st.Point;
        SelectedPoint = PointChoices.FirstOrDefault(o => o.Value == point) ?? SelectedPoint ?? PointChoices.FirstOrDefault();
        if (c.Head == L2EventBlocks.TriggerList)
            PointNote = point >= 0 ? "A point on the cutscene's area is picked: its walks start from there." : "No preset point is on the cutscene's area: add one on the Maps page, or its walks start from elsewhere.";
        var p = L2StoryClear.PresetPoints(_rom, map).FirstOrDefault(x => x.Id == (SelectedPoint?.Value ?? -1));
        var checkedFlags = L2GameStart.CheckedFlags(_rom, map, c);
        var best = L2GameStart.BestFlags(_rom, map, c, p.X, p.Y);
        foreach (int f in checkedFlags)
            Flags.Add(new L2Flag { Label = $"Story flag {f:X2} on", Bit = f, IsChecked = best.Contains(f) });
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
        foreach (var w in OtherFlags.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(w.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int f) || f < 0 || f > 0xFF) { Status = $"\"{w}\" isn't a story flag (hex, 00-FF)."; return; }
            flags.Add(f);
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

    /// <summary>Opens the project's ROM in BizHawk, as it is now.</summary>
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

/// <summary>A cutscene to start with, as the list shows it.</summary>
public sealed record L2GameStartChoice(L2GameStart.Choice Choice)
{
    public string Label => Choice.Title;
}
