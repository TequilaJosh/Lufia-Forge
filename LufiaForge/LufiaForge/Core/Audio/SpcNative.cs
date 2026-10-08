using System.Runtime.InteropServices;

namespace LufiaForge.Core.Audio;

/// <summary>
/// lufia_spc.dll: blargg's SNES SPC-700 emulator (from Game_Music_Emu, LGPL 2.1). Source and build script in
/// native/spc. Samples are 16-bit stereo at 32000 Hz; counts are in samples (2 per stereo pair). The DLL keeps
/// its own clock between port accesses (see lufia_spc.cpp).
/// </summary>
internal static class SpcNative
{
    private const string Dll = "lufia_spc.dll";

    [DllImport(Dll)] public static extern IntPtr lfspc_new();
    [DllImport(Dll)] public static extern void lfspc_delete(IntPtr s);
    [DllImport(Dll)] public static extern IntPtr lfspc_load(IntPtr s, byte[] data, int size);
    /// <summary>Lets the SPC run for this many clocks (1.024 MHz) before the next port access.</summary>
    [DllImport(Dll)] public static extern void lfspc_wait(IntPtr s, int clocks);
    [DllImport(Dll)] public static extern int lfspc_read(IntPtr s, int port);
    [DllImport(Dll)] public static extern void lfspc_write(IntPtr s, int port, int data);
    [DllImport(Dll)] public static extern void lfspc_render(IntPtr s, short[] output, int count);
    /// <summary>Copies the 64 KB of sound RAM.</summary>
    [DllImport(Dll)] public static extern void lfspc_ram(IntPtr s, byte[] output);

    private static bool? _available;

    /// <summary>True when the DLL is present and loads.</summary>
    public static bool Available
    {
        get
        {
            if (_available is bool b) return b;
            try { var h = lfspc_new(); if (h != IntPtr.Zero) lfspc_delete(h); _available = h != IntPtr.Zero; }
            catch (Exception) { _available = false; }
            return _available.Value;
        }
    }
}
