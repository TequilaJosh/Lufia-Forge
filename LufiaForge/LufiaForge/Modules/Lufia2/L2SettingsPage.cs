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
        CheckTidy();
    }

    // ---------------------------------------------------------------- tidy the hack's data

    [ObservableProperty] private string _tidyText = "";
    private L2Tidy.Result? _tidy;

    /// <summary>Works out (on a copy) what tidying would do, for the card.</summary>
    private void CheckTidy()
    {
        if (_rom == null) return;
        _tidy = L2Tidy.Prepare(_rom);
        TidyText = _tidy.Refused != null ? _tidy.Refused
            : _tidy.Tidied == null ? "Nothing to tidy: every battle script sits on its own, outside names and other records."
            : string.Join(" ", new[]
            {
                _tidy.MonsterScripts > 0 ? $"Monster scripts in name fields or inside records: {_tidy.MonsterScripts} ({_tidy.MonsterBytes} bytes)." : "",
                _tidy.SpellScripts > 0 ? $"Spell scripts running into the spell table's spare entries: {_tidy.SpellScripts}." : "",
                _tidy.NamesFreed > 0 ? $"Tidying lets {_tidy.NamesFreed} monster names have 13 letters again." : "",
            }.Where(s => s.Length > 0));
        TidyCommand.NotifyCanExecuteChanged();
    }

    private bool CanTidy() => _tidy?.Tidied != null;

    [RelayCommand(CanExecute = nameof(CanTidy))]
    private void Tidy()
    {
        if (_rom == null || _main == null) return;
        var mouse = System.Windows.Input.Mouse.OverrideCursor;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        L2Tidy.Result r;
        try { r = L2Tidy.Prepare(_rom); }
        finally { System.Windows.Input.Mouse.OverrideCursor = mouse; }
        if (r.Tidied == null)
        {
            System.Windows.MessageBox.Show(r.Preview(), "Lufia II: tidy the hack's data", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            CheckTidy();
            return;
        }
        var ok = System.Windows.MessageBox.Show(r.Preview() + "\n\nTidy it now? Edit, Undo takes it back.", "Lufia II: tidy the hack's data",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.OK) return;
        ApplyTidy(r);
    }

    /// <summary>Puts a prepared (and checked) tidy in the ROM as one Undo step.</summary>
    public void ApplyTidy(L2Tidy.Result r)
    {
        if (_rom == null || _main == null || r.Tidied == null) return;
        L2Tidy.Apply(_rom, r);
        _main.NotifyRomModified("Lufia II: hack's data tidied");
        _main.ReloadModules();   // every page reads the moved scripts and spells again (this one too)
        Status = "Tidied: " + string.Join(" ", r.Gains.Take(1).DefaultIfEmpty("the scripts sit on their own now."));
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
