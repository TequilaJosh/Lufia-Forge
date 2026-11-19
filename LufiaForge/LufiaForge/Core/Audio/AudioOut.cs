using System.Runtime.InteropServices;

namespace LufiaForge.Core.Audio;

// streams 16-bit stereo samples to the default sound device (Windows waveOut). a background thread keeps a few short
// buffers queued, filling them from the callback. PlayedPairs is what's actually been heard, so the cutscene preview
// follows it and picture and sound stay together
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

    // what stopped the sound: the callback failed (the sound stops then, the app carries on). null while it's fine
    public Exception? Error { get; private set; }

    // opens the device and starts playing. false when there's no sound device (or the callback failed right away)
    public bool Start()
    {
        var format = new WaveFormat { wFormatTag = 1, nChannels = 2, nSamplesPerSec = (uint)_rate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = (uint)_rate * 4 };
        if (waveOutOpen(out _wave, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0) { _wave = IntPtr.Zero; return false; }
        int size = Marshal.SizeOf<WaveHdr>();
        for (int i = 0; i < Buffers; i++)
        {
            _data[i] = Marshal.AllocHGlobal(PairsPerBuffer * 4);
            _headers[i] = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new WaveHdr { lpData = _data[i], dwBufferLength = PairsPerBuffer * 4 }, _headers[i], false);
            waveOutPrepareHeader(_wave, _headers[i], size);
        }
        try { for (int i = 0; i < Buffers; i++) Queue(i); }
        catch (Exception ex) { Error = ex; Dispose(); return false; }
        _thread = new Thread(Pump) { IsBackground = true, Name = "Cutscene audio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return true;
    }

    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags));

    // for tests: everything runs but the device gets silence (LUFIAFORGE_SILENT_AUDIO=1)
    private static readonly bool Silent = Environment.GetEnvironmentVariable("LUFIAFORGE_SILENT_AUDIO") == "1";

    private void Queue(int buffer)
    {
        _fill(_pcm, PairsPerBuffer);
        if (Silent) Array.Clear(_pcm);
        Marshal.Copy(_pcm, 0, _data[buffer], _pcm.Length);
        Marshal.WriteInt32(_headers[buffer], FlagsOffset, Marshal.ReadInt32(_headers[buffer], FlagsOffset) & ~WhdrDone);
        waveOutWrite(_wave, _headers[buffer], Marshal.SizeOf<WaveHdr>());
    }

    private void Pump()
    {
        try
        {
            while (!_stop)
            {
                for (int i = 0; i < Buffers && !_stop; i++)
                    if ((Marshal.ReadInt32(_headers[i], FlagsOffset) & WhdrDone) != 0) Queue(i);
                Thread.Sleep(4);
            }
        }
        catch (Exception ex)
        {
            // the callback failed (the sound driver stopped answering, data it read was bad...). no more buffers get
            // queued, so the sound stops after the ones playing. an error on this thread would close the whole app
            Error = ex;
            _stop = true;
        }
    }

    // stereo sample pairs the device has played so far
    public long PlayedPairs
    {
        get
        {
            if (_wave == IntPtr.Zero) return 0;
            var time = new MmTime { wType = TimeSamples };
            return waveOutGetPosition(_wave, ref time, Marshal.SizeOf<MmTime>()) == 0 && time.wType == TimeSamples ? time.sample : 0;
        }
    }

    public void Dispose()
    {
        _stop = true;
        // the playback thread still filling a buffer (a slow fill) has to be gone before the device and the buffers it
        // writes to get freed. freeing them under it was an access violation, which ends the app with no recovery copy.
        // if it doesn't stop we keep them (a few KB left behind)
        if (_thread != null && !_thread.Join(500) && !_thread.Join(3000)) return;
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

    // winmm

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
