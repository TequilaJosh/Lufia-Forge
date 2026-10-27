using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>Game-wide settings: random battles, walking speed, a run button, selling price, level cap, the ROM's size.</summary>
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
    [ObservableProperty] private string _encounterText = "", _status = "", _sizeText = "";
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
        SizeText = SizeOf(rom);
        MakeEightCommand.NotifyCanExecuteChanged();
        Status = CanEncounter && CanWorld && CanField && CanSell && CanLevel ? "" : "Some settings are greyed out: that part of the ROM has been changed some other way.";
    }

    private void UpdateText()
    {
        double m = L2Settings.MeanSteps((byte)Encounter);
        EncounterText = double.IsInfinity(m) ? "No random battles on the world map." : $"About 1 battle every {m:0.#} steps on the world map (none for the first 6 steps).";
    }

    private static string SizeOf(RomBuffer rom)
    {
        string mb(double b) => (b / 1048576).ToString("0.##") + " MB";
        if (L2Rom.IsExLoRom(rom))
            return $"8 MB. Free: {mb(L2Rom.FreeSpace(rom))} in the first 4 MB, {mb(L2Rom.UpperFree(rom))} for data and " +
                   $"{L2EventSpace.UpperBanksLeft(rom)} empty banks of 32 KB for events in the upper 4 MB (event space made so far: {mb(L2EventSpace.Free(rom))} free).";
        return $"{mb(rom.Length)}. Free for what Lufia Forge adds: {mb(L2Rom.FreeSpace(rom))} (the ROM grows to 3 and then 4 MB by itself as that is used).";
    }

    private bool CanMakeEight() => _rom != null && L2Rom.CantExpandTo8(_rom) == null;

    [RelayCommand(CanExecute = nameof(CanMakeEight))]
    private void MakeEight()
    {
        if (_rom == null || _main == null) return;
        var r = System.Windows.MessageBox.Show(
            "Make the ROM 8 MB?\n\nIt keeps the game as it is and adds 4 MB for events, map sprites and map data. " +
            "Snes9x, bsnes and BizHawk's BSNESv115+ core run it; BizHawk's older BSNES core (Config > Preferred Cores > SNES) can't, and some flash carts may not either. Edit, Undo takes it back.",
            "Lufia II: ROM size", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (r != System.Windows.MessageBoxResult.OK) return;
        try { L2Rom.ExpandTo8(_rom); }
        catch (Exception ex) { Status = "Not changed: " + ex.Message; return; }
        _main.NotifyRomModified("Lufia II: ROM made 8 MB");
        Reload();
        Status = "The ROM is 8 MB now. New events, map sprites and map character lists go in the upper 4 MB first.";
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
