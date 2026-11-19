using System;
using System.IO.MemoryMappedFiles;

namespace LufiaForge.Modules.MemoryMonitor;

// reads live WRAM and the CPU registers out of shared memory (a MemoryMappedFile) that our BizHawk external tool
// writes from inside BizHawk
//
// header layout (256 bytes):
//   0-3   : magic "LFWM"
//   4-7   : frame counter (uint32 LE)
//   8-15  : timestamp (int64 LE, DateTime.UtcNow.Ticks)
//   16-19 : WRAM size (uint32 LE, 0x20000)
//   20-23 : flags (uint32 LE). bit 0 = connected, bit 1 = a frame is being written, bit 2 = the registers below
//           are this frame's, bit 3 = the tool sets bits 1 and 2 (a tool without bit 3 gets read the old way)
//   24-25 : CPU PC (uint16 LE, low 16 bits of the program counter)
//   26    : CPU PB (uint8, program bank)
//   27    : CPU P  (uint8, status flags)
//   28-29 : CPU D  (uint16 LE, direct page)
//   30    : CPU DB (uint8, data bank)
//   31-255: reserved
// the 128 KB of WRAM comes after the header
public sealed class BizHawkBridge : IDisposable
{
    private const string MmfName    = "LufiaForge_WRAM";
    private const int    HeaderSize = 256;
    public  const int    WramSize   = 0x20000; // 128 KB

    private MemoryMappedFile?         _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private uint _lastFrameCount;
    // time since the frame counter last moved. if BizHawk crashes or gets killed in the task manager it never clears
    // the connected flag, and the shared memory stays open as long as we hold it, so the monitor said "connected"
    // forever. so after a few seconds with no frame we let go of the memory and open it again, which fails once
    // nobody else holds it
    private readonly System.Diagnostics.Stopwatch _sinceFrame = System.Diagnostics.Stopwatch.StartNew();
    private const int StaleMs = 3000;

    private const uint FlagConnected = 1, FlagWriting = 2, FlagRegisters = 4, FlagNewTool = 8;

    public bool    IsConnected  { get; private set; }
    public uint    FrameCount   { get; private set; }
    public byte[]? Wram         { get; private set; }
    public byte[]? PreviousWram { get; private set; }

    // CPU registers (filled in each frame when the BizHawk tool writes them)

    // fires every new frame that has live registers. the Disassembler listens to follow the live PC
    public static event Action<CpuSnapshot>? CpuSnapshotReady;

    // the 65816 registers at the current frame boundary
    public record struct CpuSnapshot(
        int    SnesAddress,   // full 24-bit SNES address (PB << 16 | PC)
        byte   ProcessorFlags,
        ushort DirectPage,
        byte   DataBank)
    {
        public bool FlagM => (ProcessorFlags & 0x20) != 0;
        public bool FlagX => (ProcessorFlags & 0x10) != 0;
    }

    // connecting

    // tries to open the shared memory, true if it worked
    public bool TryConnect()
    {
        if (_accessor != null) return true;
        try
        {
            _mmf      = MemoryMappedFile.OpenExisting(MmfName);
            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _sinceFrame.Restart();
            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    // reads one frame of WRAM (and the registers if they're there) from shared memory. true if there was a new frame
    public bool ReadFrame()
    {
        if (_accessor == null)
        {
            IsConnected = false;
            return false;
        }

        try
        {
            // magic bytes "LFWM"
            if (_accessor.ReadByte(0) != (byte)'L' ||
                _accessor.ReadByte(1) != (byte)'F' ||
                _accessor.ReadByte(2) != (byte)'W' ||
                _accessor.ReadByte(3) != (byte)'M')
            {
                IsConnected = false;
                return false;
            }

            uint flags = _accessor.ReadUInt32(20);
            IsConnected = (flags & FlagConnected) != 0;
            if (!IsConnected) return false;

            uint frame = _accessor.ReadUInt32(4);
            FrameCount = frame;

            // only copy WRAM when the frame counter moved
            if (frame == _lastFrameCount && Wram != null)
            {
                if (_sinceFrame.ElapsedMilliseconds > StaleMs) CheckStillThere();
                return false;
            }

            // the tool writes frames while we read them. reading halfway through a write gave a copy that was half
            // one frame and half the next (a value "changing" that never did). so a frame still being written gets
            // skipped (next tick reads it), and so does one that changed while we copied it
            bool newTool = (flags & FlagNewTool) != 0;
            if (newTool && (flags & FlagWriting) != 0) return false;

            var wramCopy = new byte[WramSize];
            _accessor.ReadArray(HeaderSize, wramCopy, 0, WramSize);

            // CPU registers (header bytes 24-30)
            ushort programCounter = _accessor.ReadUInt16(24);
            byte   programBank    = _accessor.ReadByte(26);
            byte   statusFlags    = _accessor.ReadByte(27);
            ushort directPage     = _accessor.ReadUInt16(28);
            byte   dataBank       = _accessor.ReadByte(30);

            bool registers = true;
            if (newTool)
            {
                uint flagsAfter = _accessor.ReadUInt32(20);
                if ((flagsAfter & FlagWriting) != 0 || _accessor.ReadUInt32(4) != frame) return false;
                // the Snes9x core in BizHawk gives no registers. the bytes were last frame's (or zero) and the
                // Disassembler followed a PC that wasn't there
                registers = (flagsAfter & FlagRegisters) != 0;
            }

            _lastFrameCount = frame;
            _sinceFrame.Restart();
            PreviousWram    = Wram;
            Wram = wramCopy;

            if (registers)
            {
                int snesAddress = ((int)programBank << 16) | programCounter;
                CpuSnapshotReady?.Invoke(new CpuSnapshot(snesAddress, statusFlags, directPage, dataBank));
            }

            return true;
        }
        catch
        {
            IsConnected = false;
            Disconnect();
            return false;
        }
    }

    // no frame for a while, so BizHawk is paused or gone. we let go of the memory and open it again. that works
    // while BizHawk still holds it (paused = still connected) and fails once it's closed
    private void CheckStillThere()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
        try
        {
            _mmf      = MemoryMappedFile.OpenExisting(MmfName);
            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _sinceFrame.Restart();
        }
        catch
        {
            Disconnect();
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
