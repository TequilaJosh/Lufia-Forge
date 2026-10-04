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

        Commit("Game settings", () =>
        {
            rom.WriteByte(GameDataOffsets.WalkSpeed, (byte)(FastWalk ? 0x20 : 0x10));
            rom.WriteBytes(GameDataOffsets.WalkSpeedPatch,
                FastWalk ? GameDataOffsets.WalkPatchFast : GameDataOffsets.WalkPatchNormal);
            rom.WriteByte(GameDataOffsets.ShipSpeed,    (byte)(FastShip    ? 0x40 : 0x20));
            rom.WriteByte(GameDataOffsets.AirshipSpeed, (byte)(FastAirship ? 0x80 : 0x40));
            rom.WriteUInt16Le(GameDataOffsets.SwampDamage, (ushort)Clamp(SwampDamage, 0, 999));
            rom.WriteByte(GameDataOffsets.MaxLevelDisplay, (byte)Clamp(MaxLevelDisplayed, 1, 255));
            rom.WriteByte(GameDataOffsets.MaxLevelActual,  (byte)Clamp(MaxLevelActual,    1, 255));

            // The title is 21 bytes; the next header byte (0x7FD5) is the map mode and must not be touched.
            var title = new byte[GameDataOffsets.InternalTitleLen];
            for (int i = 0; i < title.Length; i++)
                title[i] = i < InternalTitle.Length && InternalTitle[i] is >= ' ' and <= '~' ? (byte)InternalTitle[i] : (byte)' ';
            rom.WriteBytes(GameDataOffsets.InternalTitle, title);
        });

        LoadSelected();
    }
}
