using System.Diagnostics;

namespace LufiaForge.Modules.Emulator;

/// <summary>
/// Background thread that polls Snes9x memory at a configurable rate and fires
/// events on each new snapshot.
///
/// Threading contract:
///   - SnapshotReady fires on a ThreadPool thread — consumers must marshal
///     to the UI thread themselves (e.g., Dispatcher.InvokeAsync).
///   - Start/Stop are safe to call from any thread.
/// </summary>
public sealed class MemoryPoller : IDisposable
{
    private readonly MemoryReader _reader = new();
    private CancellationTokenSource? _cts;
    private long  _frame      = 0;
    private long  _ramBase    = 0;
    private long  _vramBase   = 0;
    private long  _cgramBase  = 0;

    public int PollRateMs { get; set; } = 33;

    public event EventHandler<MemorySnapshot>? SnapshotReady;
    public event EventHandler?                  EmulatorDetached;

    // -------------------------------------------------------------------------
    // Attach / Start / Stop
    // -------------------------------------------------------------------------

    public bool Attach(Process process, long ramBase, long vramBase = 0, long cgramBase = 0)
    {
        Stop();
        bool ok = _reader.Attach(process);
        if (!ok) return false;
        _ramBase   = ramBase;
        _vramBase  = vramBase;
        _cgramBase = cgramBase;
        return true;
    }

    public void Start()
    {
        if (_cts != null) return; // already running
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() => PollLoop(token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Detach()
    {
        Stop();
        _reader.Detach();
        _ramBase = _vramBase = _cgramBase = 0;
    }

    public void Dispose()
    {
        Detach();
        _reader.Dispose();
    }

    // -------------------------------------------------------------------------
    // Poll loop
    // -------------------------------------------------------------------------

    private async Task PollLoop(CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollRateMs));
        while (await timer.WaitForNextTickAsync(cancel))
        {
            if (!_reader.IsAttached)
            {
                EmulatorDetached?.Invoke(this, EventArgs.Empty);
                return;
            }

            try
            {
                var snap = ReadSnapshot();
                SnapshotReady?.Invoke(this, snap);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Read failed — emulator may have exited
                EmulatorDetached?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
    }

    private MemorySnapshot ReadSnapshot()
    {
        byte[] ram   = _ramBase  > 0 ? _reader.ReadBytes(_ramBase,  Snes9xAddressMap.RamSize)   : new byte[Snes9xAddressMap.RamSize];
        byte[] vram  = _vramBase > 0 ? _reader.ReadBytes(_vramBase, Snes9xAddressMap.VramSize)  : new byte[Snes9xAddressMap.VramSize];
        byte[] cgram = _cgramBase> 0 ? _reader.ReadBytes(_cgramBase,Snes9xAddressMap.CgramSize) : new byte[Snes9xAddressMap.CgramSize];

        return new MemorySnapshot(ram, vram, cgram, Interlocked.Increment(ref _frame));
    }
}
