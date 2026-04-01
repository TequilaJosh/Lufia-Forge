namespace LufiaForge.Modules.Emulator;

/// <summary>
/// A point-in-time capture of Snes9x emulated memory regions.
/// Produced by MemoryPoller and consumed by the memory viewer UI.
/// </summary>
public sealed class MemorySnapshot
{
    public byte[] Ram    { get; }   // 128 KB  $7E:0000–$7F:FFFF
    public byte[] Vram   { get; }   // 64 KB   (PPU tile data)
    public byte[] Cgram  { get; }   // 512 B   (palette)

    public DateTime CapturedAt  { get; }
    public long     FrameNumber { get; }
    public bool     IsValid     { get; }   // false if the read failed

    public static readonly MemorySnapshot Empty = new();

    private MemorySnapshot()
    {
        Ram   = new byte[Snes9xAddressMap.RamSize];
        Vram  = new byte[Snes9xAddressMap.VramSize];
        Cgram = new byte[Snes9xAddressMap.CgramSize];
        IsValid = false;
    }

    public MemorySnapshot(byte[] ram, byte[] vram, byte[] cgram, long frame)
    {
        Ram         = ram;
        Vram        = vram;
        Cgram       = cgram;
        FrameNumber = frame;
        CapturedAt  = DateTime.UtcNow;
        IsValid     = true;
    }

    // -------------------------------------------------------------------------
    // Convenience helpers
    // -------------------------------------------------------------------------

    public byte ReadByte(int ramOffset) =>
        (ramOffset >= 0 && ramOffset < Ram.Length) ? Ram[ramOffset] : (byte)0;

    public ushort ReadUInt16Le(int ramOffset)
    {
        if (ramOffset < 0 || ramOffset + 1 >= Ram.Length) return 0;
        return (ushort)(Ram[ramOffset] | (Ram[ramOffset + 1] << 8));
    }

    /// <summary>Indices of bytes that differ from a previous snapshot.</summary>
    public bool[] DiffMask(MemorySnapshot? previous)
    {
        var mask = new bool[Ram.Length];
        if (previous == null || !previous.IsValid) return mask;
        for (int i = 0; i < Ram.Length; i++)
            mask[i] = Ram[i] != previous.Ram[i];
        return mask;
    }
}
