using System.Diagnostics;
using System.Text;

namespace LufiaForge.Modules.Emulator;

/// <summary>
/// Locates the emulated WRAM buffer inside the Snes9x process.
///
/// Strategy 1 (ROM-title anchor, most reliable):
///   Scan for the Lufia 1 ROM title bytes ("ESTPOLIS" or "LUFIA").
///   The title is stored at offset 0x7FC0 inside the ROM buffer.
///   Subtracting that offset gives the ROM buffer base.
///   We then search for a 32-bit pointer to that base — that's CMemory.ROM.
///   CMemory layout (32-bit snes9x):
///     NSRTHeader[32] + HeaderCount[4] + RAM*[4] + SRAM*[4] + VRAM*[4] + ROM*[4]
///   So RAM pointer is 12 bytes before the ROM pointer field.
///
/// Strategy 2 (region-size scan, fallback):
///   Walk all committed memory regions via VirtualQueryEx.
///   The WRAM buffer is always exactly 0x20000 bytes (128 KB).
///   The first readable private committed region of that exact size is WRAM.
/// </summary>
public static class Snes9xAddressMap
{
    public const int RamSize   = 0x20000;  // 128 KB
    public const int VramSize  = 0x10000;  // 64 KB
    public const int CgramSize = 0x200;    // 512 bytes

    // -------------------------------------------------------------------------
    // RAM base discovery
    // -------------------------------------------------------------------------

    public static long FindRamBase(MemoryReader reader, Process process)
    {
        long ram = FindRamViaRomTitle(reader);
        if (ram > 0) return ram;

        return FindRamViaRegionSize(reader);
    }

    // -------------------------------------------------------------------------
    // Strategy 1 — ROM title anchor
    // -------------------------------------------------------------------------

    private static long FindRamViaRomTitle(MemoryReader reader)
    {
        // Lufia 1 (US) internal ROM header title. Try both common spellings.
        foreach (string title in new[] { "ESTPOLIS", "LUFIA" })
        {
            long ram = TryTitle(reader, Encoding.ASCII.GetBytes(title));
            if (ram > 0) return ram;
        }
        return 0;
    }

    private static long TryTitle(MemoryReader reader, byte[] titleBytes)
    {
        // Scan up to 8 GB — covers all 32-bit and typical 64-bit snes9x heaps.
        long titleAddr = reader.ScanForPattern(titleBytes, 0x10000, 0x200000000L);
        if (titleAddr <= 0x7FC0) return 0;

        // Title is at ROM_base + 0x7FC0 (LoROM header offset)
        long romBase = titleAddr - 0x7FC0;

        // Find the CMemory.ROM pointer field (a 32-bit value == romBase)
        byte[] romPtrBytes = BitConverter.GetBytes((uint)romBase);
        long romPtrAddr = reader.ScanForPattern(romPtrBytes, 0x10000, 0x200000000L);
        if (romPtrAddr <= 0) return 0;

        // RAM* is 12 bytes before ROM* in the CMemory struct.
        // Also probe 8 bytes before as some builds differ.
        foreach (int delta in new[] { 12, 8 })
        {
            long ramBase = reader.ReadUInt32Le(romPtrAddr - delta);
            if (ramBase > 0x10000 && ramBase < 0x100000000L && LooksLikeRam(reader, ramBase))
                return ramBase;
        }
        return 0;
    }

    // -------------------------------------------------------------------------
    // Strategy 2 — exact 128 KB region scan
    // -------------------------------------------------------------------------

    private static long FindRamViaRegionSize(MemoryReader reader)
    {
        foreach (var (baseAddr, size) in reader.EnumerateCommittedRegions(0x10000, 0x100000000L))
        {
            if (size == RamSize && LooksLikeRam(reader, baseAddr))
                return baseAddr;
        }
        return 0;
    }

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    private static bool LooksLikeRam(MemoryReader reader, long address)
    {
        try
        {
            byte[] probe = reader.ReadBytes(address, 64);
            // ReadBytes returns all-zero on failure; a valid region should at
            // least be readable (even if all-zero is valid SNES RAM at power-on).
            return probe.Length == 64;
        }
        catch { return false; }
    }

    // -------------------------------------------------------------------------
    // SNES address → WRAM offset translation
    // -------------------------------------------------------------------------

    public static int SnesAddressToRamOffset(int snesAddress)
    {
        int bank = (snesAddress >> 16) & 0xFF;
        int addr = snesAddress & 0xFFFF;

        if (bank == 0x7E) return addr;
        if (bank == 0x7F) return 0x10000 + addr;

        if (addr < 0x2000 && (bank <= 0x3F || (bank >= 0x80 && bank <= 0xBF)))
            return addr;

        return -1;
    }

    public static string FormatSnesAddress(int snesAddress) =>
        $"${(snesAddress >> 16) & 0xFF:X2}:{snesAddress & 0xFFFF:X4}";
}
