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

    // -------------------------------------------------------------------------
    // Observable properties
    // -------------------------------------------------------------------------
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isBizHawkRunning;
    [ObservableProperty] private string _connectionStatus = "Waiting for BizHawk...";
    [ObservableProperty] private string _hexText = "  Waiting for BizHawk connection...";
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

    // Search state
    private List<int>? _searchCandidates;

    /// <summary>
    /// Exposes the WinForms Panel hosting BizHawk's reparented window.
    /// The View binds this into a WindowsFormsHost.
    /// </summary>
    public Panel HostPanel { get; }

    // -------------------------------------------------------------------------
    // Init
    // -------------------------------------------------------------------------
    public MemoryMonitorViewModel()
    {
        HostPanel = _host.CreateHostPanel();

        // Load user-defined custom labels from disk
        Lufia1AddressMap.LoadCustom();

        // Seed default watches for Lufia 1 — verified PAR code addresses
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
            ConnectionStatus = "BizHawk embedded. Load ROM, then open Tools > External Tools > Lufia Forge Monitor.";
        };
        _host.Exited += () =>
        {
            IsBizHawkRunning = false;
            IsConnected      = false;
            ConnectionStatus = "BizHawk closed. Click Launch to restart.";
        };
        _host.StatusChanged += msg => ConnectionStatus = msg;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // Auto-launch BizHawk on startup (fire and forget)
        _ = AutoLaunchAsync();
    }

    private async System.Threading.Tasks.Task AutoLaunchAsync()
    {
        // Small delay to let the UI finish loading
        await System.Threading.Tasks.Task.Delay(500);
        if (!_host.IsRunning && File.Exists(DefaultEmuHawkPath))
        {
            await LaunchBizHawk();
        }
    }

    // -------------------------------------------------------------------------
    // BizHawk launch
    // -------------------------------------------------------------------------
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
            await _host.LaunchAsync(DefaultEmuHawkPath);
            IsBizHawkRunning = _host.IsRunning;
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Launch error: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------------
    // Polling loop
    // -------------------------------------------------------------------------
    private int _attachAttemptCooldown;

    private void Timer_Tick(object? sender, EventArgs e)
    {
        // Check if BizHawk is still alive
        if (IsBizHawkRunning && !_host.IsRunning)
        {
            IsBizHawkRunning = false;
            IsConnected      = false;
            ConnectionStatus = "BizHawk closed. Click Launch to restart.";
            _bridge.Disconnect();
            return;
        }

        // Auto-attach: if BizHawk isn't embedded yet, try to find and embed it
        if (!_host.IsRunning && _attachAttemptCooldown <= 0)
        {
            if (_host.TryAttachToRunning())
            {
                IsBizHawkRunning = true;
            }
            else
            {
                // Don't spam: wait ~2 seconds before trying again (60 ticks at 33ms)
                _attachAttemptCooldown = 60;
            }
        }
        if (_attachAttemptCooldown > 0) _attachAttemptCooldown--;

        // Poll shared memory for WRAM data
        if (!_bridge.IsConnected)
        {
            _bridge.TryConnect();
        }

        if (_bridge.ReadFrame())
        {
            IsConnected = true;
            ConnectionStatus = _host.IsRunning
                ? "Connected - live WRAM streaming"
                : "WRAM streaming (BizHawk not embedded - click Launch)";
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

    // -------------------------------------------------------------------------
    // Hex view
    // -------------------------------------------------------------------------
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
                byte b = wram[row + col];
                sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
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

    // -------------------------------------------------------------------------
    // Label search (known address lookup)
    // -------------------------------------------------------------------------
    partial void OnLabelSearchTextChanged(string value)
    {
        FilteredAddresses.Clear();
        foreach (var addr in Lufia1AddressMap.Search(value).Take(50))
            FilteredAddresses.Add(addr);
    }

    [RelayCommand]
    private void AddKnownWatch(KnownAddress? known)
    {
        if (known == null) return;
        // Avoid duplicates
        if (Watches.Any(w => w.SnesAddr == known.Address)) return;
        Watches.Add(new WatchItem(known.Address, known.Label, known.Size));
    }

    [RelayCommand]
    private void AddAllFromCategory(string? category)
    {
        if (string.IsNullOrEmpty(category)) return;
        foreach (var addr in Lufia1AddressMap.ByCategory(category))
        {
            if (Watches.Any(w => w.SnesAddr == addr.Address)) continue;
            Watches.Add(new WatchItem(addr.Address, addr.Label, addr.Size));
        }
    }

    /// <summary>Save a watch entry as a reusable custom label for all users.</summary>
    [RelayCommand]
    private void SaveAsLabel(WatchItem? item)
    {
        if (item == null) return;
        Lufia1AddressMap.AddCustom(item.SnesAddr, item.Label, item.Size);
        // Refresh the filtered list if a search is active
        OnLabelSearchTextChanged(LabelSearchText);
        OnPropertyChanged(nameof(AddressCategories));
    }

    // -------------------------------------------------------------------------
    // Watchlist
    // -------------------------------------------------------------------------
    private void RefreshWatches()
    {
        var wram = _bridge.Wram;
        var prev = _bridge.PreviousWram;
        if (wram == null) return;

        foreach (var w in Watches)
        {
            int offset = w.SnesAddr - 0x7E0000;
            if (offset < 0 || offset >= WramSize) continue;

            long cur = ReadTyped(wram, offset, w.Size);
            long prv = prev != null ? ReadTyped(prev, offset, w.Size) : cur;

            w.CurrentValue  = cur.ToString();
            w.PreviousValue = prv.ToString();
            w.Delta         = (cur - prv).ToString();
        }
    }

    [RelayCommand]
    private void AddWatch()
    {
        string addrText = WatchAddrText.TrimStart('$', '0', 'x', 'X').Trim();
        if (!int.TryParse(addrText, NumberStyles.HexNumber, null, out int snesAddr))
            return;

        var size  = (WatchSize)WatchTypeIndex;
        string label = string.IsNullOrWhiteSpace(WatchLabelText) ? $"${snesAddr:X6}" : WatchLabelText;
        Watches.Add(new WatchItem(snesAddr, label, size));
        WatchAddrText  = "";
        WatchLabelText = "";
    }

    [RelayCommand]
    private void RemoveWatch(WatchItem? item)
    {
        if (item != null)
            Watches.Remove(item);
    }

    // -------------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------------
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
        int show = Math.Min(500, _searchCandidates!.Count);
        var size = (WatchSize)SearchTypeIndex;
        var wram = _bridge.Wram!;

        for (int i = 0; i < show; i++)
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
        int snesAddr = 0x7E0000 + item.Offset;
        Watches.Add(new WatchItem(snesAddr, $"${snesAddr:X6}", (WatchSize)SearchTypeIndex));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private bool TryParseSearchValue(out long value)
    {
        string s = SearchValueText.Trim();
        if (s.StartsWith('$'))
            return long.TryParse(s[1..], NumberStyles.HexNumber, null, out value);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[2..], NumberStyles.HexNumber, null, out value);
        return long.TryParse(s, out value);
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

    /// <summary>
    /// Walks up from the application directory to find the bundled BizHawk folder.
    /// Looks for a sibling "BizHawk" folder relative to the repo/solution root.
    /// Falls back to a hardcoded path if not found.
    /// </summary>
    private static string FindBundledBizHawk()
    {
        try
        {
            // Start from the executable's directory and walk up looking for BizHawk\EmuHawk.exe
            string? dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                string candidate = Path.Combine(dir, "BizHawk", "EmuHawk.exe");
                if (File.Exists(candidate))
                    return candidate;

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* non-fatal */ }

        // Fallback: check common locations
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
