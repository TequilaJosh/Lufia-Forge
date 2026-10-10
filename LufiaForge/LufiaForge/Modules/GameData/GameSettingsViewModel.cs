using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LufiaForge.Modules.GameData;

/// <summary>Global tweaks: movement speeds, swamp damage, level cap and the ROM's internal title.</summary>
public partial class GameSettingsViewModel : GameDataEditorBase
{
    [ObservableProperty] private bool   _fastWalk;
    [ObservableProperty] private bool   _fastShip;
    [ObservableProperty] private bool   _fastAirship;
    [ObservableProperty] private int    _swampDamage;
    [ObservableProperty] private int    _maxLevelDisplayed;
    [ObservableProperty] private int    _maxLevelActual;
    [ObservableProperty] private string _internalTitle = "";
    [ObservableProperty] private string _regionText = "";
    [ObservableProperty] private int    _encounterRate;
    [ObservableProperty] private int    _safeSteps;
    [ObservableProperty] private string _encounterText = "";
    /// <summary>Hold L to avoid random battles (<see cref="LufiaForge.Core.Battle.EncounterPatch"/>).</summary>
    [ObservableProperty] private bool _holdLPatch;
    [ObservableProperty] private bool _canHoldLPatch;
    [ObservableProperty] private int _ratePreset = -1;

    /// <summary>Encounter presets: (label, rate); the last is "custom".</summary>
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
        int i = Array.FindIndex(Presets, p => p.Rate == value);
        RatePreset = i >= 0 ? i : Presets.Length - 1;
        _syncingPreset = false;
    }
    partial void OnSafeStepsChanged(int value) => UpdateEncounterText();

    private void UpdateEncounterText()
    {
        // chance per step = random(128) < 128 x rate / 128 = rate; halved away from the world map
        if (EncounterRate <= 0) { EncounterText = "No random battles."; return; }
        double world = 128.0 / Math.Min(128, EncounterRate), other = 128.0 / Math.Min(128, EncounterRate / 2.0);
        EncounterText = $"About 1 battle every {world:0.#} steps on the world map and every {other:0.#} in dungeons, " +
                        $"never in the first {SafeSteps} steps after one. Game default: rate 10, 4 safe steps.";
    }

    protected override void OnRomLoaded() => LoadSelected();

    protected override void LoadSelected()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        FastWalk          = rom.ReadByte(GameDataOffsets.WalkSpeed) != 0x10;
        FastShip          = rom.ReadByte(GameDataOffsets.ShipSpeed) != 0x20;
        FastAirship       = rom.ReadByte(GameDataOffsets.AirshipSpeed) != 0x40;
        SwampDamage       = rom.ReadUInt16Le(GameDataOffsets.SwampDamage);
        MaxLevelDisplayed = rom.ReadByte(GameDataOffsets.MaxLevelDisplay);
        MaxLevelActual    = rom.ReadByte(GameDataOffsets.MaxLevelActual);
        EncounterRate     = rom.ReadByte(LufiaForge.Core.Battle.Encounters.RateOffset);
        SafeSteps         = rom.ReadByte(LufiaForge.Core.Battle.Encounters.SafeStepsOffset(rom));
        HoldLPatch        = LufiaForge.Core.Battle.EncounterPatch.IsApplied(rom);
        CanHoldLPatch     = HoldLPatch || LufiaForge.Core.Battle.EncounterPatch.IsOriginal(rom);
        InternalTitle     = rom.ReadAscii(GameDataOffsets.InternalTitle, GameDataOffsets.InternalTitleLen).TrimEnd();
        RegionText = rom.ReadByte(GameDataOffsets.RegionByte) switch
        {
            0 => "Japan (NTSC) - offsets are for the US ROM, use with care",
            1 => "North America (NTSC)",
            var b => $"Unknown (0x{b:X2})",
        };
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        bool patchWanted = HoldLPatch, patched = LufiaForge.Core.Battle.EncounterPatch.IsApplied(rom);
        if (patchWanted && !patched && rom.Length < Core.Maps.MapWriter.ExpandedSize &&
            System.Windows.MessageBox.Show("'Hold L to avoid random battles' adds a small routine to the expanded part of the ROM. Expand the ROM to 2 MB?",
                "Expand ROM?", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
        { Status = "Not applied: the ROM was not expanded."; return; }

        Commit("Game settings", () =>
        {
            if (patchWanted && !patched) LufiaForge.Core.Battle.EncounterPatch.Apply(rom);
            if (!patchWanted && patched) LufiaForge.Core.Battle.EncounterPatch.Remove(rom);
            rom.WriteByte(GameDataOffsets.WalkSpeed, (byte)(FastWalk ? 0x20 : 0x10));
            rom.WriteBytes(GameDataOffsets.WalkSpeedPatch,
                FastWalk ? GameDataOffsets.WalkPatchFast : GameDataOffsets.WalkPatchNormal);
            rom.WriteByte(GameDataOffsets.ShipSpeed,    (byte)(FastShip    ? 0x40 : 0x20));
            rom.WriteByte(GameDataOffsets.AirshipSpeed, (byte)(FastAirship ? 0x80 : 0x40));
            rom.WriteUInt16Le(GameDataOffsets.SwampDamage, (ushort)Clamp(SwampDamage, 0, 999));
            rom.WriteByte(GameDataOffsets.MaxLevelDisplay, (byte)Clamp(MaxLevelDisplayed, 1, 255));
            rom.WriteByte(GameDataOffsets.MaxLevelActual,  (byte)Clamp(MaxLevelActual,    1, 255));
            rom.WriteByte(LufiaForge.Core.Battle.Encounters.RateOffset, (byte)Clamp(EncounterRate, 0, 255));
            rom.WriteByte(LufiaForge.Core.Battle.Encounters.SafeStepsOffset(rom), (byte)Clamp(SafeSteps, 0, 255));

            // The title is 21 bytes; the next header byte (0x7FD5) is the map mode and must not be touched.
            var title = new byte[GameDataOffsets.InternalTitleLen];
            for (int i = 0; i < title.Length; i++)
                title[i] = i < InternalTitle.Length && InternalTitle[i] is >= ' ' and <= '~' ? (byte)InternalTitle[i] : (byte)' ';
            rom.WriteBytes(GameDataOffsets.InternalTitle, title);
        });

        LoadSelected();
    }
}
