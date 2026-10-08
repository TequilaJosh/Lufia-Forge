using System.Runtime.InteropServices;

namespace LufiaForge.Core.Audio;

/// <summary>
/// Streams 16-bit stereo samples to the default sound device (Windows waveOut). A background thread keeps a few
/// short buffers queued, filling them from the callback. <see cref="PlayedPairs"/> is what has actually been heard,
/// so the cutscene preview follows it and picture and sound stay together.
/// </summary>
public sealed class AudioOut : IDisposable
{
    private const int Buffers = 4, PairsPerBuffer = 1024;   // 4 x 32 ms at 32 kHz

    private readonly Action<short[], int> _fill;
    private readonly int _rate;
    private IntPtr _wave;
    private readonly IntPtr[] _headers = new IntPtr[Buffers], _data = new IntPtr[Buffers];
    private readonly short[] _pcm = new short[PairsPerBuffer * 2];
    private Thread? _thread;
    private volatile bool _stop;

    public AudioOut(int sampleRate, Action<short[], int> fill)
    {
        _rate = sampleRate; _fill = fill;
    }

    /// <summary>Opens the device and starts playing; false when there's no sound device.</summary>
    public bool Start()
    {
        var fmt = new WaveFormat { wFormatTag = 1, nChannels = 2, nSamplesPerSec = (uint)_rate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = (uint)_rate * 4 };
        if (waveOutOpen(out _wave, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { _wave = IntPtr.Zero; return false; }
        int size = Marshal.SizeOf<WaveHdr>();
        for (int i = 0; i < Buffers; i++)
        {
            _data[i] = Marshal.AllocHGlobal(PairsPerBuffer * 4);
            _headers[i] = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new WaveHdr { lpData = _data[i], dwBufferLength = PairsPerBuffer * 4 }, _headers[i], false);
            waveOutPrepareHeader(_wave, _headers[i], size);
            Queue(i);
        }
        _thread = new Thread(Pump) { IsBackground = true, Name = "Cutscene audio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return true;
    }

    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags));

    /// <summary>Tests: everything runs but the device gets silence (LUFIAFORGE_SILENT_AUDIO=1).</summary>
    private static readonly bool Silent = Environment.GetEnvironmentVariable("LUFIAFORGE_SILENT_AUDIO") == "1";

    private void Queue(int i)
    {
        _fill(_pcm, PairsPerBuffer);
        if (Silent) Array.Clear(_pcm);
        Marshal.Copy(_pcm, 0, _data[i], _pcm.Length);
        Marshal.WriteInt32(_headers[i], FlagsOffset, Marshal.ReadInt32(_headers[i], FlagsOffset) & ~WhdrDone);
        waveOutWrite(_wave, _headers[i], Marshal.SizeOf<WaveHdr>());
    }

    private void Pump()
    {
        while (!_stop)
        {
            for (int i = 0; i < Buffers && !_stop; i++)
                if ((Marshal.ReadInt32(_headers[i], FlagsOffset) & WhdrDone) != 0) Queue(i);
            Thread.Sleep(4);
        }
    }

    /// <summary>Stereo sample pairs the device has played so far.</summary>
    public long PlayedPairs
    {
        get
        {
            if (_wave == IntPtr.Zero) return 0;
            var t = new MmTime { wType = TimeSamples };
            return waveOutGetPosition(_wave, ref t, Marshal.SizeOf<MmTime>()) == 0 && t.wType == TimeSamples ? t.sample : 0;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(500);
        if (_wave != IntPtr.Zero)
        {
            waveOutReset(_wave);
            for (int i = 0; i < Buffers; i++)
                if (_headers[i] != IntPtr.Zero) waveOutUnprepareHeader(_wave, _headers[i], Marshal.SizeOf<WaveHdr>());
            waveOutClose(_wave);
            _wave = IntPtr.Zero;
        }
        for (int i = 0; i < Buffers; i++)
        {
            if (_headers[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_headers[i]); _headers[i] = IntPtr.Zero; }
            if (_data[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_data[i]); _data[i] = IntPtr.Zero; }
        }
    }

    // ── winmm ───────────────────────────────────────────────────────────────

    private const int WaveMapper = -1, WhdrDone = 1;
    private const uint TimeSamples = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser;
        public int dwFlags; public uint dwLoops; public IntPtr lpNext, reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MmTime
    {
        [FieldOffset(0)] public uint wType;
        [FieldOffset(4)] public uint sample;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, int device, ref WaveFormat fmt, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr hwo, ref MmTime t, int size);
}
