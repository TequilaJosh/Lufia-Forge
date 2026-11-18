using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LufiaForge.Modules.GameData;

// game-wide tweaks: movement speeds, the menu button, swamp damage, level cap and the ROM's internal title
public partial class GameSettingsViewModel : GameDataEditorBase
{
    // the editor footer shows a ROM offset, this editor doesn't have one of its own
    public string OffsetText => "";

    [ObservableProperty] private bool   _fastWalk;
    [ObservableProperty] private bool   _fastShip;
    [ObservableProperty] private bool   _fastAirship;
    // Lufia Restored's faster walking on the world map and in dungeons (LufiaForge.Core.RestoredWalk)
    [ObservableProperty] private bool   _restoredWalk;
    // the ROM has Restored's walking (the setting only shows then)
    [ObservableProperty] private bool   _hasRestoredWalk;
    [ObservableProperty] private int    _swampDamage;
    [ObservableProperty] private int    _maxLevelDisplayed;
    [ObservableProperty] private int    _maxLevelActual;
    [ObservableProperty] private string _internalTitle = "";
    [ObservableProperty] private string _regionText = "";
    [ObservableProperty] private int    _encounterRate;
    [ObservableProperty] private int    _safeSteps;
    [ObservableProperty] private string _encounterText = "";
    // hold L to avoid random battles (LufiaForge.Core.Battle.EncounterPatch)
    [ObservableProperty] private bool _holdLPatch;
    [ObservableProperty] private bool _canHoldLPatch;
    [ObservableProperty] private int _ratePreset = -1;
    // the button that opens the menu (LufiaForge.Core.MenuButtonPatch), an index into its list
    [ObservableProperty] private int _menuButton;
    [ObservableProperty] private bool _canMenuButton;
    [ObservableProperty] private string _menuButtonText = "";
    public string[] MenuButtonLabels { get; } = LufiaForge.Core.MenuButtonPatch.Buttons.Select(b => b.Name).ToArray();
    // levels up to 255 and HP / MP up to 9999, shown and kept (LufiaForge.Core.LimitsPatch)
    [ObservableProperty] private bool _limitsPatch;
    [ObservableProperty] private bool _canLimitsPatch;
    [ObservableProperty] private string _limitsText = "";
    // hold R to turn without walking (LufiaForge.Core.TurnInPlacePatch)
    [ObservableProperty] private bool _turnPatch;
    [ObservableProperty] private bool _canTurnPatch;
    [ObservableProperty] private string _turnText = "";
    [ObservableProperty] private string _fastWalkText = "";
    // party members retarget when their target group is gone (LufiaForge.Core.Battle.RetargetPatch)
    [ObservableProperty] private bool _retargetFix;
    [ObservableProperty] private bool _canRetargetFix;
    // Dutch letters in the fonts (LufiaForge.Core.DutchLetters)
    [ObservableProperty] private bool _dutchLetters;
    [ObservableProperty] private string _dutchText = "";

    public string DutchLettersLabel { get; } = "Dutch letters (" + string.Join(" ", LufiaForge.Core.DutchLetters.Map.Select(m => m.Letter)) + ")";

    // the items, spells and monsters Lufia 1 shares with Lufia II get their names from the Dutch Lufia II
    [RelayCommand]
    private void DutchNames()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        LufiaForge.Core.DutchLetters.NamesResult? result = null;
        Commit("Dutch names from Lufia II", () => result = LufiaForge.Core.DutchLetters.ApplyLufia2Names(rom), namesChanged: true);
        if (result != null && !Status.StartsWith("Error")) Status = result.ToString();
        DutchLetters = LufiaForge.Core.DutchLetters.IsApplied(rom);
    }

    // encounter presets as (label, rate), the last one is "custom"
    public static readonly (string Label, int Rate)[] Presets =
    {
        ("Off: no random battles", 0), ("Rare (a quarter)", 3), ("Fewer (half)", 5), ("Normal (the game's)", 10), ("More (one and a half)", 15), ("Custom", -1),
    };
    public string[] PresetLabels { get; } = Presets.Select(p => p.Label).ToArray();
    private bool _syncingPreset;

    partial void OnRatePresetChanged(int value)
    {
        if (_syncingPreset || value < 0 || value >= Presets.Length || Presets[value].Rate < 0) return;
        EncounterRate = Presets[value].Rate;
    }

    partial void OnEncounterRateChanged(int value)
    {
        UpdateEncounterText();
        _syncingPreset = true;
        int preset = Array.FindIndex(Presets, p => p.Rate == value);
        RatePreset = preset >= 0 ? preset : Presets.Length - 1;
        _syncingPreset = false;
    }
    partial void OnSafeStepsChanged(int value) => UpdateEncounterText();
    partial void OnMenuButtonChanged(int value) { UpdateMenuText(); UpdateTurnText(); }
    partial void OnTurnPatchChanged(bool value) => UpdateTurnText();
    partial void OnFastWalkChanged(bool value) => UpdateFastWalkText();

    private void UpdateTurnText() => TurnText = !CanTurnPatch
        ? "The field step code ($01:8BFF) was changed by another patch, so this can't be added here."
        : TurnPatch && MenuButton == 6 ? "R also opens the menu (Menu button above): both will happen."
        : "Hold R and press a direction to face that way without taking a step (no random battle), as in Lufia II. Thanks to Colmines92.";

    private void UpdateFastWalkText()
    {
        var rom = Ctx?.Rom;
        bool canFix = rom != null && (LufiaForge.Core.FoulWaterFix.IsOriginal(rom) || LufiaForge.Core.FoulWaterFix.IsApplied(rom));
        FastWalkText = !FastWalk ? "Towns already walk fast; this makes the world map and dungeons as fast."
            : canFix ? "Foul Water's steps still count down (Colmines92's fix goes in with it: the game counted them only at normal speed)."
            : "Warning: this ROM's Foul Water code isn't the game's, so its effect may never end (a battle every step until the game is reset).";
    }
    partial void OnHoldLPatchChanged(bool value) => UpdateMenuText();
    partial void OnLimitsPatchChanged(bool value) => UpdateLimitsText();
    partial void OnMaxLevelActualChanged(int value) => UpdateLimitsText();

    private void UpdateLimitsText() => LimitsText = !CanLimitsPatch
        ? "Another patch changed the menus or battle code these limits change (a 3-digit level patch, Restored...), so they can't be added here."
        : LimitsPatch
            ? $"Levels show 3 digits and HP / MP 4. Thanks to Colmines92 for the 3-digit levels.{(MaxLevelActual <= 99 ? " Raise Max level (actual) above 99 to let heroes go past it." : "")}"
            : MaxLevelActual > 99
                ? "Above level 99 the game shows only the last 2 digits of the level: tick Higher limits to show 3."
                : "The game shows levels with 2 digits and HP / MP with 3.";

    private void UpdateMenuText() => MenuButtonText = !CanMenuButton
        ? "The menu button check ($01:823E) was changed by another patch, so it can't be set here."
        : MenuButton == 5 && HoldLPatch ? "L also avoids random battles while held (the patch under Random battles): both will happen."
        : "Opens the menu while walking. Thanks to Colmines92 for the remap.";

    private void UpdateEncounterText()
    {
        // chance per step = random(128) < 128 x rate / 128 = rate; halved away from the world map
        if (EncounterRate <= 0) { EncounterText = "No random battles."; return; }
        double worldSteps = 128.0 / Math.Min(128, EncounterRate), dungeonSteps = 128.0 / Math.Min(128, EncounterRate / 2.0);
        EncounterText = $"About 1 battle every {worldSteps:0.#} steps on the world map and every {dungeonSteps:0.#} in dungeons, " +
                        $"never in the first {SafeSteps} steps after one. Game default: rate 10, 4 safe steps.";
    }

    protected override void OnRomLoaded() => LoadSelected();

    protected override void LoadRecord()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        FastWalk          = rom.ReadByte(GameDataOffsets.WalkSpeed) != 0x10;
        FastShip          = rom.ReadByte(GameDataOffsets.ShipSpeed) != 0x20;
        FastAirship       = rom.ReadByte(GameDataOffsets.AirshipSpeed) != 0x40;
        HasRestoredWalk   = LufiaForge.Core.RestoredWalk.IsAvailable(rom);
        RestoredWalk      = HasRestoredWalk && LufiaForge.Core.RestoredWalk.IsOn(rom);
        SwampDamage       = rom.ReadUInt16Le(GameDataOffsets.SwampDamage);
        MaxLevelDisplayed = rom.ReadByte(GameDataOffsets.MaxLevelDisplay);
        MaxLevelActual    = rom.ReadByte(GameDataOffsets.MaxLevelActual);
        EncounterRate     = rom.ReadByte(LufiaForge.Core.Battle.Encounters.RateOffset);
        SafeSteps         = rom.ReadByte(LufiaForge.Core.Battle.Encounters.SafeStepsOffset(rom));
        HoldLPatch        = LufiaForge.Core.Battle.EncounterPatch.IsApplied(rom);
        CanHoldLPatch     = HoldLPatch || LufiaForge.Core.Battle.EncounterPatch.IsOriginal(rom);
        int currentButton = LufiaForge.Core.MenuButtonPatch.Current(rom);
        CanMenuButton     = currentButton >= 0;
        MenuButton        = Math.Max(0, currentButton);
        UpdateMenuText();
        DutchLetters      = LufiaForge.Core.DutchLetters.IsApplied(rom);
        LimitsPatch       = LufiaForge.Core.LimitsPatch.IsApplied(rom);
        CanLimitsPatch    = LimitsPatch || LufiaForge.Core.LimitsPatch.IsOriginal(rom);
        UpdateLimitsText();
        TurnPatch         = LufiaForge.Core.TurnInPlacePatch.IsApplied(rom);
        CanTurnPatch      = TurnPatch || LufiaForge.Core.TurnInPlacePatch.IsOriginal(rom);
        UpdateTurnText();
        UpdateFastWalkText();
        RetargetFix       = LufiaForge.Core.Battle.RetargetPatch.IsApplied(rom);
        CanRetargetFix    = RetargetFix || LufiaForge.Core.Battle.RetargetPatch.IsOriginal(rom);
        InternalTitle     = rom.ReadAscii(GameDataOffsets.InternalTitle, GameDataOffsets.InternalTitleLen).TrimEnd();
        RegionText = rom.ReadByte(GameDataOffsets.RegionByte) switch
        {
            0 => "Japan (NTSC) - offsets are for the US ROM, use with care",
            1 => "North America (NTSC)",
            var region => $"Unknown (0x{region:X2})",
        };
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        bool holdLWanted = HoldLPatch, holdLApplied = LufiaForge.Core.Battle.EncounterPatch.IsApplied(rom);
        bool fixWanted = RetargetFix, fixApplied = LufiaForge.Core.Battle.RetargetPatch.IsApplied(rom);
        bool lettersWanted = DutchLetters, lettersApplied = LufiaForge.Core.DutchLetters.IsApplied(rom);
        bool limitsWanted = LimitsPatch && CanLimitsPatch, limitsApplied = LufiaForge.Core.LimitsPatch.IsApplied(rom);
        bool turnWanted = TurnPatch && CanTurnPatch, turnApplied = LufiaForge.Core.TurnInPlacePatch.IsApplied(rom);
        if ((holdLWanted && !holdLApplied || fixWanted && !fixApplied || lettersWanted && !lettersApplied || limitsWanted && !limitsApplied || turnWanted && !turnApplied) && rom.Length < Core.Maps.MapWriter.ExpandedSize && !AutoWriting &&
            System.Windows.MessageBox.Show("The patches you ticked add small routines to the expanded part of the ROM. Expand the ROM to 2 MB?",
                "Expand ROM?", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
        { Refuse("Not applied: the ROM was not expanded."); return; }

        bool written = Commit("Game settings", () =>
        {
            if (holdLWanted && !holdLApplied) LufiaForge.Core.Battle.EncounterPatch.Apply(rom);
            if (!holdLWanted && holdLApplied) LufiaForge.Core.Battle.EncounterPatch.Remove(rom);
            if (fixWanted && !fixApplied) LufiaForge.Core.Battle.RetargetPatch.Apply(rom);
            if (!fixWanted && fixApplied) LufiaForge.Core.Battle.RetargetPatch.Remove(rom);
            if (lettersWanted && !lettersApplied) LufiaForge.Core.DutchLetters.Apply(rom);
            if (!lettersWanted && lettersApplied) LufiaForge.Core.DutchLetters.Remove(rom);
            if (limitsWanted && !limitsApplied) LufiaForge.Core.LimitsPatch.Apply(rom);
            if (!limitsWanted && limitsApplied) LufiaForge.Core.LimitsPatch.Remove(rom);
            if (turnWanted && !turnApplied) LufiaForge.Core.TurnInPlacePatch.Apply(rom);
            if (!turnWanted && turnApplied) LufiaForge.Core.TurnInPlacePatch.Remove(rom);
            if (LufiaForge.Core.MenuButtonPatch.Current(rom) >= 0) LufiaForge.Core.MenuButtonPatch.Set(rom, MenuButton);
            rom.WriteByte(GameDataOffsets.WalkSpeed, (byte)(FastWalk ? 0x20 : 0x10));
            rom.WriteBytes(GameDataOffsets.WalkSpeedPatch,
                FastWalk ? GameDataOffsets.WalkPatchFast : GameDataOffsets.WalkPatchNormal);
            LufiaForge.Core.FoulWaterFix.Set(rom, FastWalk);
            if (LufiaForge.Core.RestoredWalk.IsAvailable(rom) && RestoredWalk != LufiaForge.Core.RestoredWalk.IsOn(rom))
                LufiaForge.Core.RestoredWalk.Set(rom, RestoredWalk);
            rom.WriteByte(GameDataOffsets.ShipSpeed,    (byte)(FastShip    ? 0x40 : 0x20));
            rom.WriteByte(GameDataOffsets.AirshipSpeed, (byte)(FastAirship ? 0x80 : 0x40));
            rom.WriteUInt16Le(GameDataOffsets.SwampDamage, (ushort)Clamp(SwampDamage, 0, 999));
            rom.WriteByte(GameDataOffsets.MaxLevelDisplay, (byte)Clamp(MaxLevelDisplayed, 1, 255));
            rom.WriteByte(GameDataOffsets.MaxLevelActual,  (byte)Clamp(MaxLevelActual,    1, 255));
            // the event "every member gains 2 levels" ($01:D8F3) skips heroes at 99 with its own CMP #$63
            if (rom.ReadByte(GameDataOffsets.MaxLevelEvent - 1) == 0xC9)
                rom.WriteByte(GameDataOffsets.MaxLevelEvent, (byte)Clamp(MaxLevelActual, 1, 255));
            rom.WriteByte(LufiaForge.Core.Battle.Encounters.RateOffset, (byte)Clamp(EncounterRate, 0, 255));
            rom.WriteByte(LufiaForge.Core.Battle.Encounters.SafeStepsOffset(rom), (byte)Clamp(SafeSteps, 0, 255));

            // the title is 21 bytes. the next header byte (0x7FD5) is the map mode, don't touch it
            var title = new byte[GameDataOffsets.InternalTitleLen];
            for (int i = 0; i < title.Length; i++)
                title[i] = i < InternalTitle.Length && InternalTitle[i] is >= ' ' and <= '~' ? (byte)InternalTitle[i] : (byte)' ';
            rom.WriteBytes(GameDataOffsets.InternalTitle, title);
        });

        if (written) LoadSelected();
    }
}
