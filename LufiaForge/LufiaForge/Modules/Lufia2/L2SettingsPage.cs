using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

// game-wide settings: random battles, walking speed, a run button, selling price, level cap, the ROM's size
public partial class L2SettingsPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private bool _loading;

    public List<L2Option> EncounterOptions { get; } = L2Settings.EncounterPresets.Select(p => new L2Option(p.Value, p.Label)).ToList();
    public List<L2Option> WorldOptions { get; } = new() { new(1, "Slow (1 pixel a frame)"), new(2, "Normal (2 pixels a frame)"), new(4, "Fast (4)"), new(8, "Very fast (8)") };
    public List<L2Option> FieldOptions { get; } = L2Settings.FieldWalkLabels.Select((l, i) => new L2Option(i, l)).ToList();
    public List<L2Option> SellOptions { get; } = L2Settings.SellLabels.Select((l, i) => new L2Option(i, l)).ToList();
    public List<L2Option> NameWhenOptions { get; } = L2MapNames.WhenLabels.Select((l, i) => new L2Option(i, l)).ToList();
    public List<L2Option> NameWhereOptions { get; } = L2MapNames.WhereLabels.Select((l, i) => new L2Option(i, l)).ToList();

    [ObservableProperty] private int _encounter, _worldSpeed, _fieldSpeed, _sell, _levelCap, _nameWhen, _nameWhere;
    [ObservableProperty] private string _encounterText = "", _status = "", _sizeText = "";
    [ObservableProperty] private bool _canEncounter, _canWorld, _canField, _canSell, _canLevel, _canNameBox;
    // levels to 255 and HP / MP to 9999 (L2Limits)
    [ObservableProperty] private bool _limits, _canLimits;
    [ObservableProperty] private string _limitsText = "", _levelCapLabel = "Highest level (2 to 99)";

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        Reload();
        CheckTidy();
        CheckRoom();
    }

    // the page is shown, so read the settings as the ROM has them now (a patch or Undo may have changed them)
    public void Shown()
    {
        if (_rom == null || _rom.Version == _readVersion) return;
        string status = Status;
        Reload();
        CheckRoom();
        if (Status.Length == 0) Status = status;
    }

    private int _readVersion = -1;

    // tidy the hack's data

    [ObservableProperty] private string _tidyText = "";
    private L2Tidy.Result? _tidy;

    // works out (on a copy) what tidying would do, for the card
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
        // worked out on a copy of the whole ROM and put in over it, then every page reads again. so the pages' edits
        // not in the ROM yet go in first
        _main.WritePendingEdits();
        var oldCursor = System.Windows.Input.Mouse.OverrideCursor;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        L2Tidy.Result result;
        try { result = L2Tidy.Prepare(_rom); }
        finally { System.Windows.Input.Mouse.OverrideCursor = oldCursor; }
        if (result.Tidied == null)
        {
            System.Windows.MessageBox.Show(result.Preview(), "Lufia II: tidy the hack's data", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            CheckTidy();
            return;
        }
        var answer = System.Windows.MessageBox.Show(result.Preview() + "\n\nTidy it now? Edit, Undo takes it back.", "Lufia II: tidy the hack's data",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        ApplyTidy(result);
    }

    // puts a prepared (and checked) tidy in the ROM as one Undo step
    public void ApplyTidy(L2Tidy.Result result)
    {
        if (_rom == null || _main == null || result.Tidied == null) return;
        L2Tidy.Apply(_rom, result);
        _main.NotifyRomModified("Lufia II: hack's data tidied");
        _main.ReloadModules();   // every page reads the moved scripts and spells again (this one too)
        Status = "Tidied: " + string.Join(" ", result.Gains.Take(1).DefaultIfEmpty("the scripts sit on their own now."));
    }

    // room for new monsters (a hack's full bank $D0)

    [ObservableProperty] private string _roomText = "";

    // what moving a hack's monsters to a bank of their own would give, for the card (cheap, nothing gets built)
    private void CheckRoom()
    {
        if (_rom == null) return;
        bool can = L2NewMonsters.CanMakeRoom(_rom, out var why);
        RoomText = !can ? why
            : $"The hack keeps its monsters in bank ${L2NewMonsters.Bank(_rom):X2}, {(L2NewMonsters.FreeRun(_rom).Length < 0x40 ? "which is full" : $"with {L2NewMonsters.FreeRun(_rom).Length} bytes free for new ones")}. " +
              "Moving them to a bank of their own gives several KB for new monsters.";
        MakeRoomCommand.NotifyCanExecuteChanged();
    }

    private bool CanRoom() => _rom != null && L2NewMonsters.CanMakeRoom(_rom, out _);

    [RelayCommand(CanExecute = nameof(CanRoom))]
    private void MakeRoom()
    {
        if (_rom == null || _main == null) return;
        // worked out on a copy of the whole ROM and put in over it, then every page reads again. so the pages' edits
        // not in the ROM yet go in first
        _main.WritePendingEdits();
        var oldCursor = System.Windows.Input.Mouse.OverrideCursor;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        L2NewMonsters.RoomResult room;
        try { room = L2NewMonsters.PrepareRoom(_rom); }
        finally { System.Windows.Input.Mouse.OverrideCursor = oldCursor; }
        if (room.Bytes == null)
        {
            System.Windows.MessageBox.Show(room.Preview(), "Lufia II: room for new monsters", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            CheckRoom();
            return;
        }
        if (!L2MonstersPage.ConfirmRoom(room, "Make room now? Edit, Undo takes it back.")) return;
        ApplyRoom(room);
    }

    // puts a prepared (and checked) move of the monsters in the ROM as one Undo step
    public void ApplyRoom(L2NewMonsters.RoomResult room)
    {
        if (_rom == null || _main == null || room.Bytes == null) return;
        L2NewMonsters.ApplyRoom(_rom, room);
        _main.NotifyRomModified($"Lufia II: monsters moved to bank ${room.ToBank:X2} (room for new monsters)" + (room.Tidied ? ", hack's data tidied" : ""));
        _main.ReloadModules();
        Status = $"The monsters have a bank of their own now (${room.ToBank:X2}): {room.RoomAfter} bytes for new monsters (Monsters page, \"Add a new monster\").";
    }

    private void Reload()
    {
        var rom = _rom!;
        _readVersion = rom.Version;
        _loading = true;
        CanEncounter = L2Settings.CanSetEncounters(rom); Encounter = L2Settings.EncounterThreshold(rom);
        WorldSpeed = L2Settings.WorldSpeed(rom); CanWorld = WorldSpeed > 0;
        FieldSpeed = L2Settings.FieldWalk(rom); CanField = FieldSpeed >= 0;
        Sell = L2Settings.Sell(rom); CanSell = Sell >= 0;
        LevelCap = L2Settings.LevelCap(rom); CanLevel = LevelCap > 0;
        Limits = L2Limits.IsApplied(rom); CanLimits = Limits || L2Limits.IsOriginal(rom);
        LevelCapLabel = $"Highest level (2 to {L2Settings.MaxLevelCap(rom)})";
        LimitsText = !CanLimits
            ? "Another patch or a fan hack changed the code these limits change (a 3-digit level patch, Frue Lufia...), so they can't be added here."
            : Limits
                ? "Levels show 3 digits and HP / MP 4. Thanks to Colmines92 for the 3-digit levels." + (LevelCap <= 99 ? " Raise the highest level above 99 to let heroes go past it." : "")
                : "The game stops at level 99 (its EXP and growth tables end there; saves keep 7 bits of the level) and shows HP / MP up to 999.";
        CanNameBox = L2MapNames.CanSetBox(rom); NameWhen = (int)L2MapNames.BoxWhen(rom); NameWhere = (int)L2MapNames.BoxWhere(rom);
        _loading = false;
        UpdateText();
        SizeText = SizeOf(rom);
        MakeEightCommand.NotifyCanExecuteChanged();
        Status = CanEncounter && CanWorld && CanField && CanSell && CanLevel && CanNameBox ? "" : "Some settings are greyed out: that part of the ROM has been changed some other way.";
    }

    private void UpdateText()
    {
        double meanSteps = L2Settings.MeanSteps((byte)Encounter);
        EncounterText = double.IsInfinity(meanSteps) ? "No random battles on the world map." : $"About 1 battle every {meanSteps:0.#} steps on the world map (none for the first 6 steps).";
    }

    private static string SizeOf(RomBuffer rom)
    {
        string mb(double bytes) => (bytes / 1048576).ToString("0.##") + " MB";
        if (L2Rom.IsExLoRom(rom))
            return $"8 MB. Free: {mb(L2Rom.FreeSpace(rom))} in the first 4 MB, and {L2EventSpace.UpperBanksLeft(rom)} empty banks of 32 KB " +
                   $"in the upper 4 MB for events and data (made so far: event space {mb(L2EventSpace.Free(rom))} free, data {mb(L2Rom.UpperFree(rom))} free). " +
                   (L2Rom.Snes9x151Mode
                       ? "The top 2 MB stay empty: Snes9x 1.51 mode is on (Edit > Settings)."
                       : $"Snes9x 1.51 mode is off (Edit > Settings), so the top 2 MB are used for data first: {mb(L2Rom.UpperMark - L2Rom.Spaces(rom).Where(s => s.Mark == L2Rom.UpperMark).Select(s => s.Next).DefaultIfEmpty(L2Rom.UpperDataStart).First())} free there.");
        return $"{mb(rom.Length)}. Free for what Lufia Forge adds: {mb(L2Rom.FreeSpace(rom))} (the ROM grows to 3 and then 4 MB by itself as that is used).";
    }

    private bool CanMakeEight() => _rom != null && L2Rom.CantExpandTo8(_rom) == null;

    [RelayCommand(CanExecute = nameof(CanMakeEight))]
    private void MakeEight()
    {
        if (_rom == null || _main == null) return;
        var answer = System.Windows.MessageBox.Show(
            "Make the ROM 8 MB?\n\nIt keeps the game as it is and adds 4 MB for events, map sprites and map data. " +
            "Snes9x, bsnes and BizHawk's BSNESv115+ core run it; BizHawk's older BSNES core (Config > Preferred Cores > SNES) can't, and some flash carts may not either. Edit, Undo takes it back.",
            "Lufia II: ROM size", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.OK) return;
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
    partial void OnLimitsChanged(bool value)
    {
        if (_loading || _rom == null || value == L2Limits.IsApplied(_rom)) return;
        try { Change(value ? "higher limits" : "higher limits removed", r => { if (value) L2Limits.Apply(r); else L2Limits.Remove(r); }); }
        catch (Exception ex) { Reload(); Status = "Not changed: " + ex.Message; }
    }
    partial void OnNameWhenChanged(int value) => Change("map name box", r => L2MapNames.SetBox(r, (L2MapNames.When)value, (L2MapNames.Where)NameWhere));
    partial void OnNameWhereChanged(int value) => Change("map name box place", r => L2MapNames.SetBox(r, (L2MapNames.When)NameWhen, (L2MapNames.Where)value));
}
