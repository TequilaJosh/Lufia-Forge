using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LufiaForge.Modules.Emulator;

public sealed partial class EmulatorHostViewModel : ObservableObject, IDisposable
{
    // -------------------------------------------------------------------------
    // Infrastructure
    // -------------------------------------------------------------------------
    private readonly MemoryReader  _reader  = new();
    private readonly MemoryPoller  _poller  = new();
    private readonly AppSettings   _settings;
    private          MemorySnapshot _lastSnap  = MemorySnapshot.Empty;
    private          Process?       _snes9xProcess;

    // The HwndHost is set by the view after construction
    public EmulatorHost? Host { get; set; }

    // -------------------------------------------------------------------------
    // Bound properties — Emulator panel
    // -------------------------------------------------------------------------
    [ObservableProperty] private string _snes9xPath  = "";
    [ObservableProperty] private string _romPath     = "";
    [ObservableProperty] private string _statusText  = "Not running.";
    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private bool   _isAttached;
    [ObservableProperty] private int    _pollRateMs  = 33;

    // -------------------------------------------------------------------------
    // Memory viewer
    // -------------------------------------------------------------------------
    [ObservableProperty] private string _ramHexView  = "";
    [ObservableProperty] private int    _ramViewOffset = 0;  // start address shown
    [ObservableProperty] private string _jumpAddress  = "";

    // -------------------------------------------------------------------------
    // Watchlist
    // -------------------------------------------------------------------------
    public ObservableCollection<WatchEntry> Watchlist { get; } = new();

    [ObservableProperty] private string    _newWatchAddress = "";
    [ObservableProperty] private string    _newWatchLabel   = "";
    [ObservableProperty] private WatchType _newWatchType    = WatchType.U8;
    [ObservableProperty] private WatchEntry? _selectedWatch;

    // -------------------------------------------------------------------------
    // Ctor
    // -------------------------------------------------------------------------
    public EmulatorHostViewModel()
    {
        _settings    = AppSettings.Load();
        Snes9xPath   = _settings.Snes9xPath;
        RomPath      = _settings.LastRomPath;
        PollRateMs   = _settings.MemoryPollRateMs;

        _poller.SnapshotReady   += OnSnapshotReady;
        _poller.EmulatorDetached += OnEmulatorDetached;
    }

    /// <summary>Called by MainWindow when a ROM is opened in the editor.</summary>
    public void SetRomPath(string path)
    {
        RomPath = path;
        _settings.LastRomPath = path;
        _settings.Save();
        if (Host != null) Host.RomPath = path;
    }

    // -------------------------------------------------------------------------
    // Commands — Emulator
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void BrowseSnes9x()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select Snes9x Executable",
            Filter = "Snes9x|snes9x.exe;snes9x-x64.exe|All Executables|*.exe",
        };
        if (dlg.ShowDialog() != true) return;
        Snes9xPath = dlg.FileName;
        _settings.Snes9xPath = dlg.FileName;
        _settings.Save();
    }

    [RelayCommand]
    private void BrowseRom()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select Lufia 1 ROM",
            Filter = "SNES ROM|*.sfc;*.smc|All Files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        RomPath = dlg.FileName;
        _settings.LastRomPath = dlg.FileName;
        _settings.Save();
    }

    [RelayCommand]
    private async Task Launch()
    {
        if (Host == null)
        {
            StatusText = "Emulator panel not ready.";
            return;
        }

        Host.Snes9xPath = Snes9xPath;
        Host.RomPath    = RomPath;

        bool ok = await Host.LaunchAsync();
        if (!ok) return;

        // Find the Snes9x process
        _snes9xProcess = FindSnes9xProcess();
        if (_snes9xProcess != null)
        {
            IsRunning = true;

            // Attach memory reader
            if (_reader.Attach(_snes9xProcess))
            {
                await Task.Delay(500); // give Snes9x time to init
                long ramBase = Snes9xAddressMap.FindRamBase(_reader, _snes9xProcess);
                if (ramBase > 0)
                {
                    _poller.PollRateMs = PollRateMs;
                    _poller.Attach(_snes9xProcess, ramBase);
                    _poller.Start();
                    IsAttached = true;
                    StatusText = $"Attached — RAM @ 0x{ramBase:X8}";
                }
                else
                {
                    StatusText = "Running (RAM base not found — memory viewer inactive).";
                }
            }
        }

        Host.EmulatorExited += (_, _) => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsRunning  = false;
            IsAttached = false;
            StatusText = "Snes9x exited.";
            _poller.Stop();
        });
    }

    [RelayCommand]
    private void ShutdownEmulator()
    {
        _poller.Stop();
        _reader.Detach();
        Host?.Shutdown();
        IsRunning  = false;
        IsAttached = false;
        StatusText = "Stopped.";
    }

    [RelayCommand]
    private void ForwardF5() => Host?.ForwardKey(System.Windows.Input.Key.F5, true);

    [RelayCommand]
    private void ForwardF6() => Host?.ForwardKey(System.Windows.Input.Key.F6, true);

    [RelayCommand]
    private void ForwardF7() => Host?.ForwardKey(System.Windows.Input.Key.F7, true);

    // -------------------------------------------------------------------------
    // Commands — Memory viewer
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void JumpToAddress()
    {
        string s = JumpAddress.TrimStart('$', '0', 'x', 'X').Trim();
        if (int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int addr))
        {
            RamViewOffset = Math.Clamp(addr & ~0xF, 0, Snes9xAddressMap.RamSize - 256);
            RefreshHexView(_lastSnap);
        }
    }

    [RelayCommand]
    private void ScrollRamUp()
    {
        RamViewOffset = Math.Max(0, RamViewOffset - 256);
        RefreshHexView(_lastSnap);
    }

    [RelayCommand]
    private void ScrollRamDown()
    {
        RamViewOffset = Math.Min(Snes9xAddressMap.RamSize - 256, RamViewOffset + 256);
        RefreshHexView(_lastSnap);
    }

    // -------------------------------------------------------------------------
    // Commands — Watchlist
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void AddWatch()
    {
        string s = NewWatchAddress.TrimStart('$', '0', 'x', 'X').Trim();
        if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int addr)) return;

        Watchlist.Add(new WatchEntry
        {
            SnesAddress = addr,
            Label       = string.IsNullOrWhiteSpace(NewWatchLabel) ? $"${addr:X6}" : NewWatchLabel,
            Type        = NewWatchType,
        });
        NewWatchAddress = "";
        NewWatchLabel   = "";
    }

    [RelayCommand]
    private void RemoveWatch()
    {
        if (SelectedWatch != null)
            Watchlist.Remove(SelectedWatch);
    }

    [RelayCommand]
    private void FreezeWatch()
    {
        if (SelectedWatch == null || !_reader.IsAttached) return;
        // Write current value back every poll — simple cheat freeze
        // (actual freeze happens in the poll loop if we extend it, for now just a note)
        StatusText = $"Freeze not yet implemented for ${SelectedWatch.SnesAddress:X6}";
    }

    [RelayCommand]
    private void ExportWatchlist()
    {
        var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "watchlist.csv" };
        if (dlg.ShowDialog() != true) return;

        var lines = new List<string> { "Address,Label,Type,Current,Previous,Delta" };
        foreach (var w in Watchlist)
            lines.Add($"${w.SnesAddress:X6},{w.Label},{w.Type},{w.CurrentValue},{w.PreviousValue},{w.Delta}");
        File.WriteAllLines(dlg.FileName, lines);
    }

    // -------------------------------------------------------------------------
    // Snapshot handling
    // -------------------------------------------------------------------------

    private void OnSnapshotReady(object? sender, MemorySnapshot snap)
    {
        _lastSnap = snap;

        // Update watchlist (on poll thread — WatchEntry updates are property-based,
        // but we need to marshal the ObservableCollection changes if any were added.
        // Since we're only updating existing entries here, this is safe.)
        foreach (var w in Watchlist)
            w.Update(snap);

        // Refresh hex view on UI thread, but throttle — only update every ~10 frames
        if (snap.FrameNumber % 10 == 0)
        {
            Application.Current?.Dispatcher.InvokeAsync(
                () => RefreshHexView(snap), DispatcherPriority.Background);
        }
    }

    private void OnEmulatorDetached(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsAttached = false;
            StatusText = "Emulator disconnected.";
        });
    }

    // -------------------------------------------------------------------------
    // Hex view renderer
    // -------------------------------------------------------------------------

    private void RefreshHexView(MemorySnapshot snap)
    {
        if (!snap.IsValid) { RamHexView = "(No snapshot yet)"; return; }

        const int bytesPerRow = 16;
        const int rowCount    = 24;   // show 24 rows = 384 bytes

        int start  = Math.Max(0, RamViewOffset);
        int end    = Math.Min(snap.Ram.Length, start + bytesPerRow * rowCount);

        var sb = new System.Text.StringBuilder(rowCount * 80);
        for (int row = start; row < end; row += bytesPerRow)
        {
            int snesAddr = 0x7E0000 + row;
            sb.Append($"${snesAddr:X6}  ");

            for (int col = 0; col < bytesPerRow && row + col < end; col++)
            {
                sb.Append($"{snap.Ram[row + col]:X2} ");
                if (col == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int col = 0; col < bytesPerRow && row + col < end; col++)
            {
                byte b = snap.Ram[row + col];
                sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            }
            sb.AppendLine();
        }

        RamHexView = sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Process? FindSnes9xProcess()
    {
        foreach (string name in new[] { "snes9x", "snes9x-x64" })
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) return procs[0];
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------
    public void Dispose()
    {
        _poller.Dispose();
        _reader.Dispose();
    }
}
