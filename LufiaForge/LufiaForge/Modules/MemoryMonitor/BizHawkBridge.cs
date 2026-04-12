using System;
using System.IO.MemoryMappedFiles;

namespace LufiaForge.Modules.MemoryMonitor;

/// <summary>
/// Reads live WRAM data and CPU register snapshots from a shared MemoryMappedFile
/// written by the LufiaForge BizHawk External Tool running inside BizHawk.
///
/// MMF header layout (256 bytes total):
///   0-3   : Magic "LFWM"
///   4-7   : Frame counter (uint32 LE)
///   8-15  : Timestamp (int64 LE, DateTime.UtcNow.Ticks)
///   16-19 : WRAM size (uint32 LE, 0x20000)
///   20-23 : Flags (uint32 LE, bit 0 = connected)
///   24-25 : CPU PC (uint16 LE, low 16 bits of program counter)
///   26    : CPU PB (uint8, program bank register)
///   27    : CPU P  (uint8, processor status flags)
///   28-29 : CPU D  (uint16 LE, direct page register)
///   30    : CPU DB (uint8, data bank register)
///   31-255: Reserved
/// WRAM data follows the header (128 KB).
/// </summary>
public sealed class BizHawkBridge : IDisposable
{
    private const string MmfName    = "LufiaForge_WRAM";
    private const int    HeaderSize = 256;
    public  const int    WramSize   = 0x20000; // 128 KB

    private MemoryMappedFile?         _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private uint _lastFrameCount;

    public bool    IsConnected  { get; private set; }
    public uint    FrameCount   { get; private set; }
    public byte[]? Wram         { get; private set; }
    public byte[]? PreviousWram { get; private set; }

    // -------------------------------------------------------------------------
    // CPU register snapshot (populated each frame when BizHawk tool writes them)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fired on every new frame when live CPU register data is available.
    /// Subscribe from the Disassembler module to track the live program counter.
    /// </summary>
    public static event Action<CpuSnapshot>? CpuSnapshotReady;

    /// <summary>Snapshot of 65816 CPU registers at the current frame boundary.</summary>
    public record struct CpuSnapshot(
        int    SnesAddress,   // full 24-bit SNES address (PB << 16 | PC)
        byte   ProcessorFlags,
        ushort DirectPage,
        byte   DataBank)
    {
        public bool FlagM => (ProcessorFlags & 0x20) != 0;
        public bool FlagX => (ProcessorFlags & 0x10) != 0;
    }

    // -------------------------------------------------------------------------
    // Connection management
    // -------------------------------------------------------------------------

    /// <summary>Attempt to open the shared memory region. Returns true on success.</summary>
    public bool TryConnect()
    {
        if (_accessor != null) return true;
        try
        {
            _mmf      = MemoryMappedFile.OpenExisting(MmfName);
            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    /// <summary>
    /// Read one frame of WRAM (and CPU registers if present) from shared memory.
    /// Returns true if new data was available.
    /// </summary>
    public bool ReadFrame()
    {
        if (_accessor == null)
        {
            IsConnected = false;
            return false;
        }

        try
        {
            // Validate magic bytes "LFWM"
            if (_accessor.ReadByte(0) != (byte)'L' ||
                _accessor.ReadByte(1) != (byte)'F' ||
                _accessor.ReadByte(2) != (byte)'W' ||
                _accessor.ReadByte(3) != (byte)'M')
            {
                IsConnected = false;
                return false;
            }

            uint flags = _accessor.ReadUInt32(20);
            IsConnected = (flags & 1) != 0;
            if (!IsConnected) return false;

            uint frame = _accessor.ReadUInt32(4);
            FrameCount = frame;

            // Only copy WRAM if the frame counter advanced
            if (frame == _lastFrameCount && Wram != null)
                return false;

            _lastFrameCount = frame;
            PreviousWram    = Wram;

            var buf = new byte[WramSize];
            _accessor.ReadArray(HeaderSize, buf, 0, WramSize);
            Wram = buf;

            // Read CPU registers (bytes 24-30 in header)
            ushort pc   = _accessor.ReadUInt16(24);
            byte   pb   = _accessor.ReadByte(26);
            byte   p    = _accessor.ReadByte(27);
            ushort d    = _accessor.ReadUInt16(28);
            byte   db   = _accessor.ReadByte(30);

            int snesAddr = ((int)pb << 16) | pc;
            CpuSnapshotReady?.Invoke(new CpuSnapshot(snesAddr, p, d, db));

            return true;
        }
        catch
        {
            IsConnected = false;
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        IsConnected = false;
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
    }

    public void Dispose() => Disconnect();
}
