namespace LufiaForge.Core;

/// <summary>
/// 32 KB of cartridge RAM instead of 8 KB: 24 KB more RAM at $70:2000-$70:7FFF for patches.
/// The game asks for 8 KB in its header (byte $FFD8 = 03, 1 KB shifted left by 3); emulators and flash carts give a
/// LoROM game as much as the header asks for, at $70:0000-$70:7FFF. The game only uses $70:0000-$70:1FFF (three
/// 0x800-byte save slots, a signature at $70:1810-$182F; its setup at $00:9333 checks and clears just that), so with
/// 05 (32 KB) the rest is free, and kept when the power is off (so a patch must set up what it keeps there itself).
/// Old 8 KB save files still load: the emulator fills the first 8 KB from them.
/// On a real cartridge it needs a 32 KB RAM chip.
/// </summary>
public static class SaveRamPatch
{
    /// <summary>File offset of the header's cartridge RAM size byte (LoROM header at 0x7FC0).</summary>
    public const int SizeByte = 0x7FD8;
    public const byte Original = 0x03, Expanded = 0x05;
    /// <summary>The added RAM: SNES $70:2000 up to (not including) $70:8000.</summary>
    public const int FreeStart = 0x702000, FreeEnd = 0x708000;

    public static bool IsApplied(RomBuffer rom) => rom.ReadByte(SizeByte) >= Expanded;
    public static bool IsOriginal(RomBuffer rom) => rom.ReadByte(SizeByte) == Original;

    public static void Apply(RomBuffer rom)
    {
        rom.WriteByte(SizeByte, Expanded);
        rom.FixChecksum();
    }

    public static void Remove(RomBuffer rom)
    {
        rom.WriteByte(SizeByte, Original);
        rom.FixChecksum();
    }
}
