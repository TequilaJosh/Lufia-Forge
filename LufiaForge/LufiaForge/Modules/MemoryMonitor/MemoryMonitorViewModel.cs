using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LufiaForge.Modules.MemoryMonitor;

public partial class MemoryMonitorViewModel : ObservableObject, IDisposable
{
    private readonly BizHawkBridge _bridge = new();
    private readonly BizHawkHost   _host   = new();
    private readonly DispatcherTimer _timer;

    private static readonly string DefaultEmuHawkPath = FindBundledBizHawk();

    // bound properties
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isBizHawkRunning;
    [ObservableProperty] private bool   _isBizHawkUndocked;
    [ObservableProperty] private string _connectionStatus = "BizHawk isn't connected: click Launch BizHawk.";
    [ObservableProperty] private string _hexText = "  The game's memory shows here while BizHawk runs the ROM\n  (with the Lufia Forge tool open in BizHawk: Tools > External Tools).";
    [ObservableProperty] private int    _hexOffset;
    [ObservableProperty] private string _frameInfo = "";
    [ObservableProperty] private string _searchValueText = "";
    [ObservableProperty] private int    _searchTypeIndex;
    [ObservableProperty] private string _searchStatus = "";
    [ObservableProperty] private string _watchAddrText = "7E0000";
    [ObservableProperty] private string _watchLabelText = "";
    [ObservableProperty] private int    _watchTypeIndex;
    [ObservableProperty] private string _labelSearchText = "";
    [ObservableProperty] private KnownAddress? _selectedKnownAddress;

    public ObservableCollection<WatchItem>        Watches       { get; } = new();
    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();
    public ObservableCollection<KnownAddress>     FilteredAddresses { get; } = new();

    public string[] SizeOptions => ["U8", "U16", "U32", "S8", "S16"];
    public IReadOnlyList<string> AddressCategories => Lufia1AddressMap.Categories;

    private const int HexRows  = 32;
    private const int HexCols  = 16;
    private const int WramSize = BizHawkBridge.WramSize;

    // search state
    private List<int>? _searchCandidates;

    // for the other modules

    // the Disassembler's "Add to Watchlist" menu item calls this. adds a U16 watch unless the address is on the list already
    public void AddWatch(int snesAddress, string label)
    {
        if (Watches.Any(w => w.SnesAddr == snesAddress)) return;
        Watches.Add(new WatchItem(snesAddress, label, WatchSize.U16));
    }

    // ROM auto-load
    private string? _pendingRomPath;
    private static readonly string LoadRomCommandFile =
        Path.Combine(Path.GetTempPath(), "LufiaForge_LoadROM.txt");

    // the WinForms Panel that holds BizHawk's window. the view puts it in a WindowsFormsHost
    public Panel HostPanel { get; }

    // setup
    public MemoryMonitorViewModel()
    {
        HostPanel = _host.CreateHostPanel();

        // the user's own labels
        Lufia1AddressMap.LoadCustom();

        // default watches for Lufia 1, checked PAR code addresses
        Watches.Add(new WatchItem(0x7E157F, "Hero Current HP",  WatchSize.U16));
        Watches.Add(new WatchItem(0x7E15F1, "Hero Max HP",      WatchSize.U16));
        Watches.Add(new WatchItem(0x7E1587, "Hero Current MP",  WatchSize.U16));
        Watches.Add(new WatchItem(0x7E15F9, "Hero Max MP",      WatchSize.U16));
        Watches.Add(new WatchItem(0x7E14CF, "Gold (full)",       WatchSize.U16));
        Watches.Add(new WatchItem(0x7E141A, "Experience",        WatchSize.U16));
        Watches.Add(new WatchItem(0x7E16F0, "Hero ATP",          WatchSize.U16));
        Watches.Add(new WatchItem(0x7E16F8, "Hero DFP",          WatchSize.U16));
        Watches.Add(new WatchItem(0x7E1710, "Hero STR",          WatchSize.U16));

        _host.Attached += () =>
        {
            IsBizHawkRunning = true;
            ConnectionStatus = _pendingRomPath != null
                ? "BizHawk embedded: loading ROM automatically…"
                : "BizHawk embedded. Open Tools › External Tools › Lufia Forge Monitor.";
        };
        _host.Exited += () =>
        {
            IsBizHawkRunning    = false;
            IsBizHawkUndocked   = false;
            IsConnected         = false;
            ConnectionStatus    = "BizHawk closed. Click Launch to restart.";
        };
        _host.StatusChanged    += message => ConnectionStatus  = message;
        _host.DockStateChanged += undocked => IsBizHawkUndocked = undocked;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    // MainWindow calls this when the user opens a ROM
    public void SetRom(Core.RomBuffer rom)
    {
        // just remember which ROM is open. BizHawk gets the path when the user clicks "Launch BizHawk". we don't
        // start or load anything on our own, so the user decides when the emulator starts
        _pendingRomPath = rom.FilePath;
        Lufia1AddressMap.Game = rom.Game;
        OnPropertyChanged(nameof(AddressCategories));
    }

    // writes the ROM BizHawk should play (the project as it is right now) and returns its path
    public Func<string?>? PrepareRom { get; set; }

    private static void WriteLoadRomCommand(string romPath)
    {
        try { File.WriteAllText(LoadRomCommandFile, romPath); } catch { }
    }

    private async System.Threading.Tasks.Task LaunchBizHawkWithRom(string romPath)
    {
        try
        {
            await _host.LaunchAsync(DefaultEmuHawkPath, romPath);
            IsBizHawkRunning = _host.IsRunning;
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Launch error: {ex.Message}";
        }
    }

    // the Launch BizHawk button
    [RelayCommand]
    private async System.Threading.Tasks.Task LaunchBizHawk()
    {
        if (_host.IsRunning)
        {
            ConnectionStatus = "BizHawk is already embedded.";
            return;
        }

        try
        {
            // hand over the project's ROM (freshly written) so BizHawk loads it right away
            string? romPath = PrepareRom?.Invoke() ?? _pendingRomPath;
            await _host.LaunchAsync(DefaultEmuHawkPath, romPath);
            IsBizHawkRunning = _host.IsRunning;
            // a BizHawk that was already open keeps playing what it had, so the Lufia Forge tool in it loads the ROM
            // we just wrote (within a second)
            if (_host.LastLaunchAttached && romPath != null) { WriteLoadRomCommand(romPath); ConnectionStatus = "BizHawk was open already: it loads the project's ROM now."; }
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Launch error: {ex.Message}";
        }
    }

    // dock / undock
    [RelayCommand]
    private void ToggleUndock()
    {
        if (!_host.IsRunning) return;
        if (_host.IsUndocked)
            _host.Dock();
        else
            _host.Undock();
    }

    // polling
    private void Timer_Tick(object? sender, EventArgs e)
    {
        // is BizHawk still there?
        if (IsBizHawkRunning && !_host.IsRunning)
        {
            IsBizHawkRunning = false;
            IsConnected      = false;
            ConnectionStatus = "BizHawk closed. Click Launch to restart.";
            _bridge.Disconnect();
            return;
        }

        // a BizHawk started outside Lufia Forge stays in its own window (its WRAM still streams in below). it only
        // gets docked when you click Launch

        // check the shared memory for WRAM
        if (!_bridge.IsConnected)
        {
            _bridge.TryConnect();
        }

        if (_bridge.ReadFrame())
        {
            IsConnected = true;
            ConnectionStatus = _host.IsRunning
                ? "Connected - live WRAM streaming"
                : "WRAM streaming from BizHawk in its own window (click Launch to dock it here)";
            FrameInfo = $"Frame: {_bridge.FrameCount:N0}";
            RefreshHexView();
            RefreshWatches();
        }
        else if (!_bridge.IsConnected && IsConnected)
        {
            IsConnected = false;
            ConnectionStatus = IsBizHawkRunning
                ? "BizHawk embedded - open Tools > External Tools > Lufia Forge Monitor"
                : "Waiting for BizHawk...";
            FrameInfo = "";
            _bridge.Disconnect();
        }
        else if (!_bridge.IsConnected)
        {
            _bridge.Disconnect();
        }
    }

    // hex view
    private void RefreshHexView()
    {
        var wram = _bridge.Wram;
        if (wram == null) return;

        int start = HexOffset & ~0xF;
        if (start < 0) start = 0;
        int end = Math.Min(WramSize, start + HexRows * HexCols);

        var sb = new StringBuilder(HexRows * 80);
        for (int row = start; row < end; row += HexCols)
        {
            sb.Append($"$7E{row:X4}  ");
            for (int col = 0; col < HexCols && row + col < end; col++)
            {
                sb.Append($"{wram[row + col]:X2} ");
                if (col == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int col = 0; col < HexCols && row + col < end; col++)
            {
                byte cell = wram[row + col];
                sb.Append(cell >= 0x20 && cell <= 0x7E ? (char)cell : '.');
            }
            sb.AppendLine();
        }
        HexText = sb.ToString();
    }

    [RelayCommand]
    private void HexScrollUp()
    {
        HexOffset = Math.Max(0, HexOffset - 256);
        RefreshHexView();
    }

    [RelayCommand]
    private void HexScrollDown()
    {
        HexOffset = Math.Min(WramSize - 256, HexOffset + 256);
        RefreshHexView();
    }

    partial void OnHexOffsetChanged(int value) => RefreshHexView();

    // label search (looking up known addresses)
    partial void OnLabelSearchTextChanged(string value)
    {
        FilteredAddresses.Clear();
        foreach (var known in Lufia1AddressMap.Search(value).Take(50))
            FilteredAddresses.Add(known);
    }

    [RelayCommand]
    private void AddKnownWatch(KnownAddress? known)
    {
        if (known == null) return;
        // no duplicates
        if (Watches.Any(w => w.SnesAddr == known.Address)) return;
        Watches.Add(new WatchItem(known.Address, known.Label, known.Size));
    }

    [RelayCommand]
    private void AddAllFromCategory(string? category)
    {
        if (string.IsNullOrEmpty(category)) return;
        foreach (var known in Lufia1AddressMap.ByCategory(category))
        {
            if (Watches.Any(w => w.SnesAddr == known.Address)) continue;
            Watches.Add(new WatchItem(known.Address, known.Label, known.Size));
        }
    }

    // saves a watch as a custom label you can reuse
    [RelayCommand]
    private void SaveAsLabel(WatchItem? item)
    {
        if (item == null) return;
        Lufia1AddressMap.AddCustom(item.SnesAddr, item.Label, item.Size);
        // refresh the search results if there's a search going
        OnLabelSearchTextChanged(LabelSearchText);
        OnPropertyChanged(nameof(AddressCategories));
    }

    // watchlist
    private void RefreshWatches()
    {
        var wram = _bridge.Wram;
        var previousWram = _bridge.PreviousWram;
        if (wram == null) return;

        foreach (var watch in Watches)
        {
            int offset = watch.SnesAddr - 0x7E0000;
            if (offset < 0 || offset + SizeBytes(watch.Size) > WramSize) continue;   // (a 2 or 4 byte value has to end inside WRAM)

            long current = ReadTyped(wram, offset, watch.Size);
            long previous = previousWram != null ? ReadTyped(previousWram, offset, watch.Size) : current;

            watch.CurrentValue  = current.ToString();
            watch.PreviousValue = previous.ToString();
            watch.Delta         = (current - previous).ToString();
        }
    }

    [RelayCommand]
    private void AddWatch()
    {
        string addressText = WatchAddrText.TrimStart('$', '0', 'x', 'X').Trim();
        if (!int.TryParse(addressText, NumberStyles.HexNumber, null, out int snesAddress))
            return;

        var size  = (WatchSize)WatchTypeIndex;
        string label = string.IsNullOrWhiteSpace(WatchLabelText) ? $"${snesAddress:X6}" : WatchLabelText;
        Watches.Add(new WatchItem(snesAddress, label, size));
        WatchAddrText  = "";
        WatchLabelText = "";
    }

    [RelayCommand]
    private void RemoveWatch(WatchItem? item)
    {
        if (item != null)
            Watches.Remove(item);
    }

    // search
    [RelayCommand]
    private void FirstScan()
    {
        var wram = _bridge.Wram;
        if (wram == null) { SearchStatus = "Not connected to BizHawk."; return; }
        if (!TryParseSearchValue(out long target)) { SearchStatus = "Invalid value."; return; }

        var size = (WatchSize)SearchTypeIndex;
        _searchCandidates = Enumerable.Range(0, WramSize)
            .Where(i =>
            {
                if (i + SizeBytes(size) > WramSize) return false;
                return ReadTyped(wram, i, size) == target;
            })
            .ToList();

        ShowSearchResults();
    }

    [RelayCommand]
    private void NextScan()
    {
        var wram = _bridge.Wram;
        if (wram == null || _searchCandidates == null) { SearchStatus = "Run First Scan first."; return; }
        if (!TryParseSearchValue(out long target)) { SearchStatus = "Invalid value."; return; }

        var size = (WatchSize)SearchTypeIndex;
        _searchCandidates = _searchCandidates
            .Where(i => i + SizeBytes(size) <= WramSize && ReadTyped(wram, i, size) == target)
            .ToList();

        ShowSearchResults();
    }

    [RelayCommand]
    private void ResetSearch()
    {
        _searchCandidates = null;
        SearchResults.Clear();
        SearchStatus = "Search reset.";
    }

    private void ShowSearchResults()
    {
        SearchResults.Clear();
        int shown = Math.Min(500, _searchCandidates!.Count);
        var size = (WatchSize)SearchTypeIndex;
        var wram = _bridge.Wram!;

        for (int i = 0; i < shown; i++)
        {
            int offset = _searchCandidates[i];
            SearchResults.Add(new SearchResultItem
            {
                Address = $"${0x7E0000 + offset:X6}",
                Value   = ReadTyped(wram, offset, size).ToString(),
                Offset  = offset,
            });
        }

        SearchStatus = _searchCandidates.Count switch
        {
            0     => "No matches.",
            > 500 => $"{_searchCandidates.Count:N0} candidates (showing 500) - use Next Scan.",
            _     => $"{_searchCandidates.Count} result(s) found.",
        };
    }

    [RelayCommand]
    private void AddResultToWatchlist(SearchResultItem? item)
    {
        if (item == null) return;
        int snesAddress = 0x7E0000 + item.Offset;
        Watches.Add(new WatchItem(snesAddress, $"${snesAddress:X6}", (WatchSize)SearchTypeIndex));
    }

    private bool TryParseSearchValue(out long value)
    {
        string text = SearchValueText.Trim();
        if (text.StartsWith('$'))
            return long.TryParse(text[1..], NumberStyles.HexNumber, null, out value);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, null, out value);
        return long.TryParse(text, out value);
    }

    internal static long ReadTyped(byte[] ram, int offset, WatchSize size) => size switch
    {
        WatchSize.U8  => ram[offset],
        WatchSize.U16 => (ushort)(ram[offset] | (ram[offset + 1] << 8)),
        WatchSize.U32 => (uint)(ram[offset] | (ram[offset + 1] << 8) | (ram[offset + 2] << 16) | (ram[offset + 3] << 24)),
        WatchSize.S8  => (sbyte)ram[offset],
        WatchSize.S16 => (short)(ram[offset] | (ram[offset + 1] << 8)),
        _             => ram[offset],
    };

    private static int SizeBytes(WatchSize size) => size switch
    {
        WatchSize.U16 or WatchSize.S16 => 2,
        WatchSize.U32                  => 4,
        _                              => 1,
    };

    // walks up from the app's folder looking for the bundled BizHawk, a "BizHawk" folder next to the repo/solution
    // root. falls back to a hardcoded path if there isn't one
    internal static string FindBundledBizHawk()
    {
        try
        {
            // start at the exe's folder and go up looking for BizHawk\EmuHawk.exe
            string? folder = AppDomain.CurrentDomain.BaseDirectory;
            while (folder != null)
            {
                string candidate = Path.Combine(folder, "BizHawk", "EmuHawk.exe");
                if (File.Exists(candidate))
                    return candidate;

                folder = Path.GetDirectoryName(folder);
            }
        }
        catch { /* not worth stopping for */ }

        // fallback: the usual spots
        string localBizHawk = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BizHawk", "EmuHawk.exe");
        if (File.Exists(localBizHawk)) return localBizHawk;

        return @"D:\BizHawk\EmuHawk.exe";
    }

    public void Dispose()
    {
        _timer.Stop();
        _bridge.Dispose();
        _host.Dispose();
    }
}

public class SearchResultItem
{
    public string Address { get; set; } = "";
    public string Value   { get; set; } = "";
    public int    Offset  { get; set; }
}
