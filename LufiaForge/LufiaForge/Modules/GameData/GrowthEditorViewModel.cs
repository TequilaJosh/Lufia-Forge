using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

/// <summary>One stat's growth: what it gains over each band of 16 level-ups.</summary>
public partial class GrowthRow : ObservableObject
{
    private readonly Action _changed;
    public GrowthRow(string stat, int[] bands, Action changed) { Stat = stat; _bands = bands; _changed = changed; }
    public string Stat { get; }
    private readonly int[] _bands;
    public int[] Bands => _bands;
    private int Get(int b) => _bands[b];
    private void Set(int b, int v) { _bands[b] = Math.Clamp(v, 0, 255); OnPropertyChanged($"B{b + 1}"); _changed(); }
    public int B1 { get => Get(0); set => Set(0, value); }
    public int B2 { get => Get(1); set => Set(1, value); }
    public int B3 { get => Get(2); set => Set(2, value); }
    public int B4 { get => Get(3); set => Set(3, value); }
    public int B5 { get => Get(4); set => Set(4, value); }
    public int B6 { get => Get(5); set => Set(5, value); }
    public int B7 { get => Get(6); set => Set(6, value); }
}

/// <summary>A level in the preview.</summary>
public sealed record GrowthPreviewRow(int Level, string Exp, int Hp, int Mp, int Str, int Int, int Agl, int Mgr);

/// <summary>
/// EXP curve and stat growth of the four main characters (see <see cref="LevelGrowth"/>), with a level-by-level
/// preview computed the way the game's level-up code does it.
/// </summary>
public partial class GrowthEditorViewModel : GameDataEditorBase
{
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
        int keep = SelectedIndex;
        Characters.Clear();
        for (int i = 0; i < LevelGrowth.Characters; i++) Characters.Add($"{i}  {Ctx.CharacterName(i)}");
        SelectedIndex = keep;
    }

    partial void OnSelectedIndexChanged(int value) => LoadSelected();

    protected override void LoadSelected()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        _loading = true;
        _setup = LevelGrowth.Read(Ctx.Rom, SelectedIndex);
        Counter = _setup.Counter; FirstLevelExp = (int)_setup.FirstLevelExp; Increment = (int)_setup.Increment;
        T1 = _setup.Thresholds[0]; T2 = _setup.Thresholds[1]; T3 = _setup.Thresholds[2]; T4 = _setup.Thresholds[3]; T5 = _setup.Thresholds[4];
        Growth.Clear();
        for (int s = 0; s < 6; s++)
        {
            var bands = Enumerable.Range(0, LevelGrowth.Bands).Select(b => _setup.Growth[s, b]).ToArray();
            Growth.Add(new GrowthRow(LevelGrowth.StatNames[s], bands, UpdatePreview));
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

    /// <summary>The edited values (not yet written).</summary>
    private LevelGrowth.Setup? Edited()
    {
        if (_setup == null) return null;
        var p = new LevelGrowth.Setup
        {
            StartLevel = _setup.StartLevel, StartExp = _setup.StartExp, StartStats = _setup.StartStats,
            Counter = Clamp(Counter, 0, 255), FirstLevelExp = Clamp(FirstLevelExp, 0, 0xFFFFFF), Increment = Clamp(Increment, 0, 0xFFFFFF),
            Thresholds = new[] { T1, T2, T3, T4, T5 }.Select(t => Clamp(t, 0, 255)).ToArray(),
        };
        for (int s = 0; s < 6 && s < Growth.Count; s++)
            for (int b = 0; b < LevelGrowth.Bands; b++) p.Growth[s, b] = Growth[s].Bands[b];
        return p;
    }

    private void UpdatePreview()
    {
        if (_loading || Edited() is not { } p) return;
        Preview.Clear();
        var levels = LevelGrowth.Simulate(p);
        foreach (var l in levels)
            Preview.Add(new GrowthPreviewRow(l.Number, l.ExpToReach.ToString("N0"), l.Stats[0], l.Stats[1], l.Stats[2], l.Stats[3], l.Stats[4], l.Stats[5]));
        var last = levels[^1];
        Summary = $"Level 99 needs {last.ExpToReach:N0} EXP. Stats then: HP {last.Stats[0]}, MP {last.Stats[1]}, STR {last.Stats[2]}, " +
                  $"INT {last.Stats[3]}, AGL {last.Stats[4]}, MGR {last.Stats[5]} (each level-up also adds a random -2..+2).";
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0 || Edited() is not { } p) return;
        Commit($"{Ctx.CharacterName(SelectedIndex)}'s growth", () => LevelGrowth.Write(Ctx.Rom, SelectedIndex, p));
    }
}
