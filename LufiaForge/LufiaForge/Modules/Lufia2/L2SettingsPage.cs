using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>Game-wide settings: random battles, walking speed, a run button, selling price, level cap.</summary>
public partial class L2SettingsPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private bool _loading;

    public List<L2Option> EncounterOptions { get; } = L2Settings.EncounterPresets.Select(p => new L2Option(p.Value, p.Label)).ToList();
    public List<L2Option> WorldOptions { get; } = new() { new(1, "Slow (1 pixel a frame)"), new(2, "Normal (2 pixels a frame)"), new(4, "Fast (4)"), new(8, "Very fast (8)") };
    public List<L2Option> FieldOptions { get; } = L2Settings.FieldWalkLabels.Select((l, i) => new L2Option(i, l)).ToList();
    public List<L2Option> SellOptions { get; } = L2Settings.SellLabels.Select((l, i) => new L2Option(i, l)).ToList();

    [ObservableProperty] private int _encounter, _worldSpeed, _fieldSpeed, _sell, _levelCap;
    [ObservableProperty] private string _encounterText = "", _status = "";
    [ObservableProperty] private bool _canEncounter, _canWorld, _canField, _canSell, _canLevel;

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        Reload();
    }

    private void Reload()
    {
        var rom = _rom!;
        _loading = true;
        CanEncounter = L2Settings.CanSetEncounters(rom); Encounter = L2Settings.EncounterThreshold(rom);
        WorldSpeed = L2Settings.WorldSpeed(rom); CanWorld = WorldSpeed > 0;
        FieldSpeed = L2Settings.FieldWalk(rom); CanField = FieldSpeed >= 0;
        Sell = L2Settings.Sell(rom); CanSell = Sell >= 0;
        LevelCap = L2Settings.LevelCap(rom); CanLevel = LevelCap > 0;
        _loading = false;
        UpdateText();
        Status = CanEncounter && CanWorld && CanField && CanSell && CanLevel ? "" : "Some settings are greyed out: that part of the ROM has been changed some other way.";
    }

    private void UpdateText()
    {
        double m = L2Settings.MeanSteps((byte)Encounter);
        EncounterText = double.IsInfinity(m) ? "No random battles on the world map." : $"About 1 battle every {m:0.#} steps on the world map (none for the first 6 steps).";
    }

    private void Change(string what, Action<RomBuffer> write)
    {
        if (_loading || _rom == null || _main == null) return;
        write(_rom);
        _main.NotifyRomModified("Lufia II: " + what);
        Reload();
    }

    partial void OnEncounterChanged(int value) => Change("random battles", r => L2Settings.SetEncounters(r, (byte)value));
    partial void OnWorldSpeedChanged(int value) => Change("world map walking", r => L2Settings.SetWorldSpeed(r, value));
    partial void OnFieldSpeedChanged(int value) => Change("walking in towns and dungeons", r => L2Settings.SetFieldWalk(r, value));
    partial void OnSellChanged(int value) => Change("selling price", r => L2Settings.SetSell(r, value));
    partial void OnLevelCapChanged(int value) => Change("level cap", r => L2Settings.SetLevelCap(r, value));
}
