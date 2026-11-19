using System.Runtime.InteropServices;

namespace LufiaForge.Core.Audio;

// lufia_spc.dll: blargg's SNES SPC-700 emulator (from Game_Music_Emu, LGPL 2.1). source and build script are in
// native/spc. samples are 16-bit stereo at 32000 Hz, counts are in samples (2 per stereo pair). the DLL keeps its own
// clock between port accesses (see lufia_spc.cpp)
internal static class SpcNative
{
    private const string Dll = "lufia_spc.dll";

    [DllImport(Dll)] public static extern IntPtr lfspc_new();
    [DllImport(Dll)] public static extern void lfspc_delete(IntPtr s);
    [DllImport(Dll)] public static extern IntPtr lfspc_load(IntPtr s, byte[] data, int size);
    // lets the SPC run for this many clocks (1.024 MHz) before the next port access
    [DllImport(Dll)] public static extern void lfspc_wait(IntPtr s, int clocks);
    [DllImport(Dll)] public static extern int lfspc_read(IntPtr s, int port);
    [DllImport(Dll)] public static extern void lfspc_write(IntPtr s, int port, int data);
    [DllImport(Dll)] public static extern void lfspc_render(IntPtr s, short[] output, int count);
    // copies the 64 KB of sound RAM
    [DllImport(Dll)] public static extern void lfspc_ram(IntPtr s, byte[] output);

    private static bool? _available;

    // true when the DLL is there and loads
    public static bool Available
    {
        get
        {
            if (_available is bool known) return known;
            try { var handle = lfspc_new(); if (handle != IntPtr.Zero) lfspc_delete(handle); _available = handle != IntPtr.Zero; }
            catch (Exception) { _available = false; }
            return _available.Value;
        }
    }
}
