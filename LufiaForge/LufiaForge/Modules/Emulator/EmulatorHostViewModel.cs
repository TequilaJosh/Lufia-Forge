using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace LufiaForge.Modules.Emulator;

public sealed partial class EmulatorHostViewModel : ObservableObject, IDisposable
{
    // -------------------------------------------------------------------------
    // Infrastructure
    // -------------------------------------------------------------------------
    private readonly MemoryReader    _reader   = new();
    private readonly MemoryPoller    _poller   = new();
    private readonly MemoryScanner   _scanner  = new();
    private readonly AppSettings     _settings;

    // Written by the ThreadPool poller, read by the UI timer — volatile so the
    // UI thread always sees the latest reference without a memory fence.
    private volatile MemorySnapshot  _lastSnap = MemorySnapshot.Empty;
    private          Process?        _snes9xProcess;

    // Fires at 5 Hz on the UI thread to render the latest snapshot.
    // Decouples the 30 Hz poll rate from the UI so the dispatcher queue
    // stays empty between ticks and WPF can process input events freely.
    private DispatcherTimer? _uiRefreshTimer;

    /// <summary>Set by EmulatorHostView after the HwndHost is created.</summary>
    public EmulatorHost? Host { get; set; }

    // -------------------------------------------------------------------------
    // Bound properties — Emulator panel
    // -------------------------------------------------------------------------
    [ObservableProperty] private string _snes9xPath = "";
    [ObservableProperty] private string _romPath    = "";
    [ObservableProperty] private string _statusText = "Configure Snes9x path, then click Launch.";
    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private bool   _isAttached;
    [ObservableProperty] private int    _pollRateMs = 33;

    // -------------------------------------------------------------------------
    // Memory viewer
    // -------------------------------------------------------------------------
    [ObservableProperty] private string _ramHexView    = BuildIdleHex();
    [ObservableProperty] private int    _ramViewOffset;
    [ObservableProperty] private string _jumpAddress   = "";

    // -------------------------------------------------------------------------
    // Watchlist
    // -------------------------------------------------------------------------
    public ObservableCollection<WatchEntry> Watchlist { get; } = new();
    [ObservableProperty] private string     _newWatchAddress = "";
    [ObservableProperty] private string     _newWatchLabel   = "";
    [ObservableProperty] private WatchType  _newWatchType    = WatchType.U8;
    [ObservableProperty] private WatchEntry? _selectedWatch;

    // -------------------------------------------------------------------------
    // Memory search
    // -------------------------------------------------------------------------
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    [ObservableProperty] private string           _searchValue      = "";
    [ObservableProperty] private WatchType        _searchType       = WatchType.U8;
    [ObservableProperty] private SearchComparison _searchComparison = SearchComparison.Exact;
    [ObservableProperty] private string           _searchStatus     = "Enter a value and click First Scan.";
    [ObservableProperty] private SearchResult?    _selectedSearchResult;

    // -------------------------------------------------------------------------
    // Key bindings
    // -------------------------------------------------------------------------
    public ObservableCollection<KeyBindingRow> KeyBindings { get; } = new();
    [ObservableProperty] private bool          _showKeyBindings;

    /// <summary>The row currently waiting for a key-capture press, or null.</summary>
    private KeyBindingRow? _capturingRow;

    // -------------------------------------------------------------------------
    // Ctor
    // -------------------------------------------------------------------------
    public EmulatorHostViewModel()
    {
        _settings  = AppSettings.Load();
        Snes9xPath = _settings.Snes9xPath;
        RomPath    = _settings.LastRomPath;
        PollRateMs = _settings.MemoryPollRateMs;

        // Build key-binding rows from saved (or default) mappings
        foreach (var btn in ControllerMapping.AllButtons)
        {
            var key = _settings.Controller.Buttons.TryGetValue(btn, out Key k) ? k : Key.None;
            KeyBindings.Add(new KeyBindingRow(btn, key));
        }

        _poller.SnapshotReady    += OnSnapshotReady;
        _poller.EmulatorDetached += OnEmulatorDetached;
    }

    // -------------------------------------------------------------------------
    // ROM path propagation
    // -------------------------------------------------------------------------
    public void SetRomPath(string path)
    {
        RomPath = path;
        _settings.LastRomPath = path;
        _settings.Save();
        if (Host != null) Host.RomPath = path;
    }

    // -------------------------------------------------------------------------
    // Key forwarding (called from view's Window PreviewKeyDown/Up)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by EmulatorHostView for every key event on the parent Window
    /// (only when no TextBox/ComboBox has keyboard focus).
    /// If we are capturing a binding, assigns the key. Otherwise forwards
    /// any mapped key to Snes9x and marks the event handled.
    /// Returns true if the event should be marked handled (swallowed from WPF).
    /// </summary>
    public bool HandleWindowKey(Key key, bool isDown)
    {
        // ---- Key-capture mode: next key pressed assigns the binding ----
        if (isDown && _capturingRow != null && key != Key.Escape && key != Key.None)
        {
            _capturingRow.BoundKey    = key;
            _capturingRow.IsCapturing = false;
            _capturingRow = null;
            SaveKeyBindings();
            return true;
        }
        if (isDown && _capturingRow != null && key == Key.Escape)
        {
            _capturingRow.IsCapturing = false;
            _capturingRow = null;
            return true;
        }

        // ---- Normal play: forward mapped keys to Snes9x ----
        if (!IsRunning || Host == null) return false;

        foreach (var row in KeyBindings)
        {
            if (row.BoundKey == key)
            {
                Host.ForwardKey(key, isDown);
                return true;
            }
        }
        return false;
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
        if (Host == null) { StatusText = "Emulator panel not ready."; return; }

        Host.Snes9xPath = Snes9xPath;
        Host.RomPath    = RomPath;

        bool ok = await Host.LaunchAsync();
        if (!ok) return;

        _snes9xProcess = FindSnes9xProcess();
        if (_snes9xProcess == null) { StatusText = "Could not find Snes9x process."; return; }

        IsRunning = true;
        RamHexView = "Scanning for WRAM...";

        if (_reader.Attach(_snes9xProcess))
        {
            await Task.Delay(800);
            long ramBase = Snes9xAddressMap.FindRamBase(_reader, _snes9xProcess);

            _poller.PollRateMs = PollRateMs;
            _poller.Attach(_snes9xProcess, ramBase);
            _poller.Start();
            IsAttached = ramBase > 0;

            StatusText = ramBase > 0
                ? $"Attached — RAM @ 0x{ramBase:X8}"
                : "Running (WRAM not located — values show once found)";

            if (ramBase == 0)
                _ = Task.Run(() => RetryFindRamBase());
        }

        // Start the 5 Hz UI refresh timer — the only thing that ever updates
        // the hex view TextBlock and watchlist cells from this point onward.
        StartUiRefreshTimer();

        Host.EmulatorExited += (_, _) => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            StopUiRefreshTimer();
            IsRunning  = false;
            IsAttached = false;
            StatusText = "Snes9x exited.";
            RamHexView = BuildIdleHex();
            _poller.Stop();
        });
    }

    private async Task RetryFindRamBase()
    {
        for (int attempt = 0; attempt < 20 && IsRunning; attempt++)
        {
            await Task.Delay(1500);
            if (_snes9xProcess == null || _snes9xProcess.HasExited) return;
            long ramBase = Snes9xAddressMap.FindRamBase(_reader, _snes9xProcess);
            if (ramBase > 0)
            {
                _poller.UpdateRamBase(ramBase);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsAttached = true;
                    StatusText = $"Attached — RAM @ 0x{ramBase:X8}";
                });
                return;
            }
        }
    }

    [RelayCommand]
    private void ShutdownEmulator()
    {
        StopUiRefreshTimer();
        _poller.Stop();
        _reader.Detach();
        Host?.Shutdown();
        IsRunning  = false;
        IsAttached = false;
        StatusText = "Stopped.";
        RamHexView = BuildIdleHex();
    }

    // Send both KeyDown AND KeyUp so Snes9x sees a complete key press cycle.
    [RelayCommand]
    private void ForwardF5() { Host?.ForwardKey(Key.F5, true); Host?.ForwardKey(Key.F5, false); }

    [RelayCommand]
    private void ForwardF6() { Host?.ForwardKey(Key.F6, true); Host?.ForwardKey(Key.F6, false); }

    [RelayCommand]
    private void ForwardF7() { Host?.ForwardKey(Key.F7, true); Host?.ForwardKey(Key.F7, false); }

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
    // Commands — Memory search
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void FirstScan()
    {
        if (!_lastSnap.IsValid)
        {
            SearchStatus = "No RAM data — launch the emulator first.";
            return;
        }
        TryParseSearchValue(out long val);

        var (results, total) = _scanner.FirstScan(_lastSnap.Ram, val, SearchType, SearchComparison);
        ApplySearchResults(results, total);
    }

    [RelayCommand]
    private void NextScan()
    {
        if (!_scanner.HasScan) { SearchStatus = "Run First Scan first."; return; }
        if (!_lastSnap.IsValid) { SearchStatus = "No RAM data."; return; }
        TryParseSearchValue(out long val);

        var (results, total) = _scanner.NextScan(_lastSnap.Ram, val, SearchType, SearchComparison);
        ApplySearchResults(results, total);
    }

    [RelayCommand]
    private void ResetSearch()
    {
        _scanner.Reset();
        SearchResults.Clear();
        SearchStatus = "Search reset. Enter a value and click First Scan.";
    }

    [RelayCommand]
    private void AddSearchResultToWatchlist()
    {
        if (SelectedSearchResult == null) return;
        Watchlist.Add(new WatchEntry
        {
            SnesAddress = 0x7E0000 + SelectedSearchResult.RamOffset,
            Label       = SelectedSearchResult.AddressHex,
            Type        = SearchType,
        });
    }

    private void ApplySearchResults(IReadOnlyList<SearchResult> results, int total)
    {
        SearchResults.Clear();
        foreach (var r in results) SearchResults.Add(r);
        SearchStatus = total switch
        {
            0    => "No matches found.",
            1    => "1 result found.",
            > 500 => $"{total:N0} candidates (showing first 500) — use Next Scan to narrow.",
            _    => $"{total:N0} result{(total == 1 ? "" : "s")} found.",
        };
    }

    private bool TryParseSearchValue(out long value)
    {
        string s = SearchValue.Trim();
        // Hex: $XX or 0xXX
        if (s.StartsWith("$"))
            return long.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out value);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        // Decimal
        return long.TryParse(s, out value);
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
        if (SelectedWatch != null) Watchlist.Remove(SelectedWatch);
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
    // Commands — Key bindings
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleKeyBindings() => ShowKeyBindings = !ShowKeyBindings;

    [RelayCommand]
    private void StartCapture(KeyBindingRow row)
    {
        if (_capturingRow != null) _capturingRow.IsCapturing = false;
        _capturingRow   = row;
        row.IsCapturing = true;
    }

    [RelayCommand]
    private void ResetBindings()
    {
        _settings.Controller.ResetToDefaults();
        _settings.Save();
        foreach (var row in KeyBindings)
        {
            if (_settings.Controller.Buttons.TryGetValue(row.Button, out Key k))
                row.BoundKey = k;
        }
    }

    // -------------------------------------------------------------------------
    // Snapshot handling
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Snapshot handler — ThreadPool thread, UI-free
    // -------------------------------------------------------------------------

    private void OnSnapshotReady(object? sender, MemorySnapshot snap)
    {
        // Just store the latest snapshot. The UI timer reads it at 5 Hz.
        // No Dispatcher calls here — the poll loop must never touch the UI.
        _lastSnap = snap;
    }

    // -------------------------------------------------------------------------
    // UI refresh timer — fires at 5 Hz on the UI thread
    // -------------------------------------------------------------------------

    private void StartUiRefreshTimer()
    {
        _uiRefreshTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _uiRefreshTimer.Tick += OnUiRefreshTick;
        _uiRefreshTimer.Start();
    }

    private void StopUiRefreshTimer()
    {
        if (_uiRefreshTimer == null) return;
        _uiRefreshTimer.Stop();
        _uiRefreshTimer.Tick -= OnUiRefreshTick;
    }

    private void OnUiRefreshTick(object? sender, EventArgs e)
    {
        // Running on the UI thread at 5 Hz.
        // Only update the two live-data regions: watchlist cells and hex view.
        // Nothing else in the ViewModel is touched here, so TextBoxes,
        // ComboBoxes, and all other bound properties are completely unaffected.

        // If a popup is open (Mouse.Captured != null) or a text box is focused,
        // skip this tick — we'll pick up fresh data on the next one.
        if (IsUserInteracting()) return;

        var snap = _lastSnap;
        if (!snap.IsValid) return;

        foreach (var w in Watchlist)
            w.Update(snap);

        RefreshHexView(snap);
    }

    /// <summary>
    /// True while the user has a text box focused or a popup (ComboBox
    /// dropdown, context menu, …) open. Must be called on the UI thread.
    /// </summary>
    private static bool IsUserInteracting()
    {
        // Mouse.Captured is non-null for any open popup
        if (Mouse.Captured != null) return true;
        // TextBoxBase covers TextBox, RichTextBox, PasswordBox
        return Keyboard.FocusedElement is TextBoxBase or PasswordBox;
    }

    private void OnEmulatorDetached(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            StopUiRefreshTimer();
            IsAttached = false;
            StatusText = "Emulator disconnected.";
        });
    }

    // -------------------------------------------------------------------------
    // Hex view renderer
    // -------------------------------------------------------------------------

    private void RefreshHexView(MemorySnapshot snap)
    {
        if (!snap.IsValid || !IsAttached)
        {
            if (IsRunning) RamHexView = "Searching for WRAM — will appear once found...";
            return;
        }

        const int bytesPerRow = 16;
        const int rowCount    = 24;

        int start = Math.Max(0, RamViewOffset);
        int end   = Math.Min(snap.Ram.Length, start + bytesPerRow * rowCount);

        var sb = new System.Text.StringBuilder(rowCount * 80);
        for (int row = start; row < end; row += bytesPerRow)
        {
            sb.Append($"${0x7E0000 + row:X6}  ");
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

    private static string BuildIdleHex()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("  RAM viewer — launch emulator to see live values");
        sb.AppendLine();
        sb.AppendLine("  $7E0000  -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- --");
        sb.AppendLine("  $7E0010  -- -- -- -- -- -- -- -- -- -- -- -- -- -- -- --");
        sb.AppendLine("  ...");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void SaveKeyBindings()
    {
        foreach (var row in KeyBindings)
            _settings.Controller.Buttons[row.Button] = row.BoundKey;
        _settings.Save();
    }

    private static Process? FindSnes9xProcess()
    {
        foreach (string name in new[] { "snes9x", "snes9x-x64" })
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) return procs[0];
        }
        return null;
    }

    public void Dispose()
    {
        StopUiRefreshTimer();
        _poller.Dispose();
        _reader.Dispose();
    }
}
