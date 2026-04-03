using System;
using System.IO.MemoryMappedFiles;

namespace LufiaForge.Modules.MemoryMonitor;

/// <summary>
/// Reads live WRAM data from a shared MemoryMappedFile written by the
/// LufiaForge BizHawk External Tool running inside BizHawk.
/// </summary>
public sealed class BizHawkBridge : IDisposable
{
    private const string MmfName    = "LufiaForge_WRAM";
    private const int    HeaderSize = 256;
    public  const int    WramSize   = 0x20000; // 128 KB

    private MemoryMappedFile?         _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private uint _lastFrameCount;

    public bool   IsConnected { get; private set; }
    public uint   FrameCount  { get; private set; }
    public byte[]? Wram        { get; private set; }
    public byte[]? PreviousWram { get; private set; }

    /// <summary>Attempt to open the shared memory region. Returns true on success.</summary>
    public bool TryConnect()
    {
        if (_accessor != null) return true; // already connected
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
    /// Read one frame of WRAM from the shared memory.
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
