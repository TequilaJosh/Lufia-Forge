namespace LufiaForge.Core;

// 32 KB of cartridge ram instead of 8 KB, so 24 KB more ram at $70:2000-$70:7FFF for patches.
// the game asks for 8 KB in its header (byte $FFD8 = 03, 1 KB shifted left by 3), and emulators and flash carts give a
// lorom game as much as the header asks for, at $70:0000-$70:7FFF. the game only uses $70:0000-$70:1FFF (three
// 0x800-byte save slots and a signature at $70:1810-$182F, its setup at $00:9333 checks and clears just that), so with
// 05 (32 KB) the rest is free and kept when the power is off (so a patch has to set up what it keeps there itself).
// old 8 KB save files still load, the emulator fills the first 8 KB from them.
// on a real cartridge it needs a 32 KB ram chip
public static class SaveRamPatch
{
    // file offset of the header's cartridge ram size byte (lorom header at 0x7FC0)
    public const int SizeByte = 0x7FD8;
    public const byte Original = 0x03, Expanded = 0x05;
    // the added ram: snes $70:2000 up to (not including) $70:8000
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
