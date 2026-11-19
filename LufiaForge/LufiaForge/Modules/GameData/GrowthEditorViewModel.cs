using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

// one stat's growth: what it gains over each band of 16 level-ups
public partial class GrowthRow : ObservableObject
{
    private readonly Action _changed;
    public GrowthRow(string stat, int[] bands, Action changed) { Stat = stat; _bands = bands; _changed = changed; }
    public string Stat { get; }
    private readonly int[] _bands;
    public int[] Bands => _bands;
    private int Get(int band) => _bands[band];
    private void Set(int band, int value) { _bands[band] = Math.Clamp(value, 0, 255); OnPropertyChanged($"B{band + 1}"); _changed(); }
    public int B1 { get => Get(0); set => Set(0, value); }
    public int B2 { get => Get(1); set => Set(1, value); }
    public int B3 { get => Get(2); set => Set(2, value); }
    public int B4 { get => Get(3); set => Set(3, value); }
    public int B5 { get => Get(4); set => Set(4, value); }
    public int B6 { get => Get(5); set => Set(5, value); }
    public int B7 { get => Get(6); set => Set(6, value); }
}

// one level in the preview
public sealed record GrowthPreviewRow(int Level, string Exp, int Hp, int Mp, int Str, int Int, int Agl, int Mgr);

// EXP curve and stat growth of the four main characters (see LevelGrowth), with a level by level
// preview worked out the same way the game's level-up code does it
public partial class GrowthEditorViewModel : GameDataEditorBase
{
    // the editor footer shows a ROM offset, this editor doesn't have one of its own
    public string OffsetText => "";

    public ObservableCollection<string> Characters { get; } = new();
    public ObservableCollection<GrowthRow> Growth { get; } = new();
    public ObservableCollection<GrowthPreviewRow> Preview { get; } = new();

    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private int _counter;
    [ObservableProperty] private int _firstLevelExp;
    [ObservableProperty] private int _increment;
    [ObservableProperty] private int _t1;
    [ObservableProperty] private int _t2;
    [ObservableProperty] private int _t3;
    [ObservableProperty] private int _t4;
    [ObservableProperty] private int _t5;
    [ObservableProperty] private string _summary = "";

    private LevelGrowth.Setup? _setup;
    private bool _loading;

    protected override void OnRomLoaded()
    {
        RefreshList();
        SelectedIndex = -1;
        SelectedIndex = 0;
    }

    protected override void OnNamesChanged() { RefreshList(); LoadSelected(); }

    private void RefreshList()
    {
        if (Ctx == null) return;
        int keepIndex = SelectedIndex;
        Characters.Clear();
        for (int i = 0; i < LevelGrowth.Characters; i++) Characters.Add($"{i}  {Ctx.CharacterName(i)}");
        SelectedIndex = keepIndex;
    }

    partial void OnSelectedIndexChanged(int value) => LoadSelected();

    protected override void LoadRecord()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        _loading = true;
        _setup = LevelGrowth.Read(Ctx.Rom, SelectedIndex);
        Counter = _setup.Counter; FirstLevelExp = (int)_setup.FirstLevelExp; Increment = (int)_setup.Increment;
        T1 = _setup.Thresholds[0]; T2 = _setup.Thresholds[1]; T3 = _setup.Thresholds[2]; T4 = _setup.Thresholds[3]; T5 = _setup.Thresholds[4];
        Growth.Clear();
        for (int stat = 0; stat < 6; stat++)
        {
            var bands = Enumerable.Range(0, LevelGrowth.Bands).Select(b => _setup.Growth[stat, b]).ToArray();
            Growth.Add(new GrowthRow(LevelGrowth.StatNames[stat], bands, UpdatePreview));
        }
        _loading = false;
        UpdatePreview();
    }

    partial void OnCounterChanged(int value) => UpdatePreview();
    partial void OnFirstLevelExpChanged(int value) => UpdatePreview();
    partial void OnIncrementChanged(int value) => UpdatePreview();
    partial void OnT1Changed(int value) => UpdatePreview();
    partial void OnT2Changed(int value) => UpdatePreview();
    partial void OnT3Changed(int value) => UpdatePreview();
    partial void OnT4Changed(int value) => UpdatePreview();
    partial void OnT5Changed(int value) => UpdatePreview();

    // the edited values (not written yet)
    private LevelGrowth.Setup? Edited()
    {
        if (_setup == null) return null;
        var setup = new LevelGrowth.Setup
        {
            StartLevel = _setup.StartLevel, StartExp = _setup.StartExp, StartStats = _setup.StartStats,
            Counter = Clamp(Counter, 0, 255), FirstLevelExp = Clamp(FirstLevelExp, 0, 0xFFFFFF), Increment = Clamp(Increment, 0, 0xFFFFFF),
            Thresholds = new[] { T1, T2, T3, T4, T5 }.Select(t => Clamp(t, 0, 255)).ToArray(),
        };
        for (int stat = 0; stat < 6 && stat < Growth.Count; stat++)
            for (int band = 0; band < LevelGrowth.Bands; band++) setup.Growth[stat, band] = Growth[stat].Bands[band];
        return setup;
    }

    private void UpdatePreview()
    {
        if (_loading || Edited() is not { } setup) return;
        Preview.Clear();
        var levels = LevelGrowth.Simulate(setup);
        foreach (var level in levels)
            Preview.Add(new GrowthPreviewRow(level.Number, level.ExpToReach.ToString("N0"), level.Stats[0], level.Stats[1], level.Stats[2], level.Stats[3], level.Stats[4], level.Stats[5]));
        var last = levels[^1];
        Summary = $"Level 99 needs {last.ExpToReach:N0} EXP. Stats then: HP {last.Stats[0]}, MP {last.Stats[1]}, STR {last.Stats[2]}, " +
                  $"INT {last.Stats[3]}, AGL {last.Stats[4]}, MGR {last.Stats[5]} (each level-up also adds a random -2..+2).";
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0 || Edited() is not { } setup) return;
        Commit($"{Ctx.CharacterName(SelectedIndex)}'s growth", () => LevelGrowth.Write(Ctx.Rom, SelectedIndex, setup));
    }
}
