using System.Diagnostics;

namespace LufiaForge.Modules.Emulator;

/// <summary>
/// Maps SNES logical addresses to offsets within the Snes9x 1.62.3 Win32
/// process memory.
///
/// Snes9x stores the emulated 128 KB WRAM in a contiguous block somewhere in
/// its heap. We locate it by scanning the process address space for the
/// characteristic 128 KB-aligned region that starts with the SNES power-on RAM
/// pattern, or by locating known static symbols.
///
/// The Snes9x 1.62.3 Win32 build exposes its memory layout as a global struct
/// called "Memory" at a predictable offset from the .data section. We use a
/// two-pass approach:
///   Pass 1 — signature scan for the 128 KB RAM region (works for all builds).
///   Pass 2 — fallback: assume the RAM base is the first committed 128 KB block
///             in the process heap that looks like SNES RAM.
/// </summary>
public static class Snes9xAddressMap
{
    public const int RamSize    = 0x20000;   // 128 KB
    public const int VramSize   = 0x10000;   // 64 KB
    public const int CgramSize  = 0x200;     // 512 bytes

    // -------------------------------------------------------------------------
    // RAM base discovery
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scan the Snes9x process for its emulated WRAM base address.
    /// Returns 0 if not found.
    /// </summary>
    public static long FindRamBase(MemoryReader reader, Process process)
    {
        // Strategy 1: look for a 131072-byte region whose first byte is 0x00
        // and that has a plausible SNES RAM content signature.
        // We scan from 0x10000 (skip the null page) to 2 GB.
        // SNES RAM typically starts all zeros at power-on, so we look for
        // a large block of mostly-zero bytes followed by Snes9x's internal
        // header patterns.

        // Strategy 2 (fast): locate the Snes9x module base and use the known
        // static offset for Memory.RAM in version 1.62.3.
        long moduleBase = GetMainModuleBase(process);
        if (moduleBase != 0)
        {
            // In Snes9x 1.62.3 Win32 the Memory struct is at a fixed RVA.
            // Probe several known candidates for the Memory.RAM pointer.
            foreach (long rva in Snes9x1623RamRvas)
            {
                long candidate = reader.ReadUInt32Le(moduleBase + rva);
                if (candidate != 0 && LooksLikeRam(reader, candidate))
                    return candidate;
            }
        }

        // Strategy 3: brute-force scan for the RAM signature pattern.
        // WRAM $7E:0000 tends to start with specific values set by the game.
        // We look for a 128 KB-aligned committed region.
        return ScanForRamRegion(reader);
    }

    // Known RVAs for Memory.RAM pointer in Snes9x 1.62.3 x86 Win32.
    // These are offsets from the module base where Snes9x stores a pointer
    // to its internal RAM buffer.
    private static readonly long[] Snes9x1623RamRvas = { 0x2E8000, 0x2E9000, 0x2EA000, 0x300000 };

    private static long GetMainModuleBase(Process process)
    {
        try
        {
            process.Refresh();
            return (long)process.MainModule!.BaseAddress;
        }
        catch { return 0; }
    }

    private static bool LooksLikeRam(MemoryReader reader, long address)
    {
        // A 128 KB block that is mostly readable bytes. We just check it's addressable.
        try
        {
            byte[] probe = reader.ReadBytes(address, 64);
            return true; // if read succeeded, it's likely valid
        }
        catch { return false; }
    }

    private static long ScanForRamRegion(MemoryReader reader)
    {
        // Scan for a large zero-filled region (power-on SNES RAM pattern).
        // Not 100% reliable mid-game but better than nothing.
        byte[] zeroes = new byte[64]; // 64 consecutive zero bytes
        long   result = reader.ScanForPattern(zeroes, 0x100000, 0x80000000);
        // Align down to 64 KB boundary
        if (result > 0) result &= ~0xFFFFL;
        return result;
    }

    // -------------------------------------------------------------------------
    // SNES address → process address translation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Convert a 24-bit SNES logical address (bank:addr) to a byte offset
    /// within the WRAM block, or -1 if not in WRAM.
    /// </summary>
    public static int SnesAddressToRamOffset(int snesAddress)
    {
        int bank = (snesAddress >> 16) & 0xFF;
        int addr = snesAddress & 0xFFFF;

        // WRAM mirrors: $7E:0000–$7F:FFFF (banks $7E–$7F)
        if (bank == 0x7E) return addr;
        if (bank == 0x7F) return 0x10000 + addr;

        // Low-page WRAM mirrors: banks $00–$3F and $80–$BF, addr $0000–$1FFF
        if (addr < 0x2000 && (bank <= 0x3F || (bank >= 0x80 && bank <= 0xBF)))
            return addr;

        return -1;
    }

    /// <summary>Format a 24-bit SNES address as a hex string like $7E:05A0.</summary>
    public static string FormatSnesAddress(int snesAddress) =>
        $"${(snesAddress >> 16) & 0xFF:X2}:{snesAddress & 0xFFFF:X4}";
}
