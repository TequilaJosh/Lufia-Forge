using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LufiaForge.Modules.Emulator;

/// <summary>
/// Thin Win32 ReadProcessMemory wrapper.
/// Attach to a Snes9x process, then read arbitrary bytes from its address space.
/// </summary>
public sealed class MemoryReader : IDisposable
{
    // -------------------------------------------------------------------------
    // Win32
    // -------------------------------------------------------------------------
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQueryEx(
        IntPtr hProcess, IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr  BaseAddress;
        public IntPtr  AllocationBase;
        public uint    AllocationProtect;
        public IntPtr  RegionSize;
        public uint    State;
        public uint    Protect;
        public uint    Type;
    }

    private const uint PROCESS_VM_READ    = 0x0010;
    private const uint PROCESS_VM_WRITE   = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint MEM_COMMIT  = 0x1000;
    private const uint PAGE_GUARD  = 0x100;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private IntPtr _handle = IntPtr.Zero;
    private int    _pid    = -1;

    public bool IsAttached => _handle != IntPtr.Zero && _pid >= 0;

    // -------------------------------------------------------------------------
    // Attach / Detach
    // -------------------------------------------------------------------------

    public bool Attach(Process process)
    {
        Detach();
        _handle = OpenProcess(
            PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION,
            false, process.Id);
        if (_handle == IntPtr.Zero) return false;
        _pid = process.Id;
        return true;
    }

    public void Detach()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        _pid = -1;
    }

    public void Dispose() => Detach();

    // -------------------------------------------------------------------------
    // Read helpers
    // -------------------------------------------------------------------------

    public byte[] ReadBytes(long address, int count)
    {
        var buf = new byte[count];
        if (!IsAttached) return buf;
        ReadProcessMemory(_handle, (IntPtr)address, buf, count, out _);
        return buf;
    }

    public byte ReadUInt8(long address)
    {
        var b = ReadBytes(address, 1);
        return b[0];
    }

    public ushort ReadUInt16Le(long address)
    {
        var b = ReadBytes(address, 2);
        return (ushort)(b[0] | (b[1] << 8));
    }

    public uint ReadUInt32Le(long address)
    {
        var b = ReadBytes(address, 4);
        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    }

    public long ReadPtr64(long address)
    {
        var b = ReadBytes(address, 8);
        return BitConverter.ToInt64(b, 0);
    }

    /// <summary>Write bytes into the target process (used for address freeze/cheat).</summary>
    public bool WriteBytes(long address, byte[] data)
    {
        if (!IsAttached) return false;
        return WriteProcessMemory(_handle, (IntPtr)address, data, data.Length, out _);
    }

    // -------------------------------------------------------------------------
    // Scan helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scan the process address space for a byte pattern.
    /// Returns the first match address or -1 if not found.
    /// </summary>
    public long ScanForPattern(byte[] pattern, long startAddress = 0, long endAddress = long.MaxValue)
    {
        if (!IsAttached) return -1;

        long addr = startAddress;
        while (addr < endAddress)
        {
            VirtualQueryEx(_handle, (IntPtr)addr, out var mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            long regionEnd = (long)mbi.BaseAddress + (long)mbi.RegionSize;

            if (mbi.State == MEM_COMMIT && (mbi.Protect & PAGE_GUARD) == 0 && mbi.RegionSize != IntPtr.Zero)
            {
                int  size   = (int)Math.Min((long)mbi.RegionSize, 64 * 1024 * 1024L);
                var  chunk  = ReadBytes((long)mbi.BaseAddress, size);
                int  found  = SearchBytes(chunk, pattern);
                if (found >= 0)
                    return (long)mbi.BaseAddress + found;
            }

            if (regionEnd <= addr) break;
            addr = regionEnd;
        }
        return -1;
    }

    private static int SearchBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>
    /// Enumerate all committed, readable (non-guard) memory regions in the
    /// target process between <paramref name="startAddress"/> and
    /// <paramref name="endAddress"/>. Yields (baseAddress, regionSize) pairs.
    /// </summary>
    public IEnumerable<(long baseAddress, long size)> EnumerateCommittedRegions(
        long startAddress, long endAddress)
    {
        if (!IsAttached) yield break;

        long addr = startAddress;
        uint mbiSize = (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

        while (addr < endAddress)
        {
            var result = VirtualQueryEx(_handle, (IntPtr)addr, out var mbi, mbiSize);
            if (result == IntPtr.Zero) break;

            long regionEnd = (long)mbi.BaseAddress + (long)mbi.RegionSize;

            if (mbi.State == MEM_COMMIT && (mbi.Protect & PAGE_GUARD) == 0 && mbi.RegionSize != IntPtr.Zero)
                yield return ((long)mbi.BaseAddress, (long)mbi.RegionSize);

            if (regionEnd <= addr) break;
            addr = regionEnd;
        }
    }
}
