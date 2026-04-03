using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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

    // Default BizHawk path - configurable later
    private const string DefaultEmuHawkPath = @"D:\BizHawk\EmuHawk.exe";

    // -------------------------------------------------------------------------
    // Observable properties
    // -------------------------------------------------------------------------
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isBizHawkRunning;
    [ObservableProperty] private string _connectionStatus = "Click Launch to start BizHawk.";
    [ObservableProperty] private string _hexText = "  Waiting for BizHawk connection...";
    [ObservableProperty] private int    _hexOffset;
    [ObservableProperty] private string _frameInfo = "";
    [ObservableProperty] private string _searchValueText = "";
    [ObservableProperty] private int    _searchTypeIndex;
    [ObservableProperty] private string _searchStatus = "";
    [ObservableProperty] private string _watchAddrText = "7E0000";
    [ObservableProperty] private string _watchLabelText = "";
    [ObservableProperty] private int    _watchTypeIndex;

    public ObservableCollection<WatchItem>        Watches       { get; } = new();
    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();

    public string[] SizeOptions => ["U8", "U16", "U32", "S8", "S16"];

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

        // Seed default watches for Lufia 1
        Watches.Add(new WatchItem(0x7E0B9E, "Hero HP",    WatchSize.U16));
        Watches.Add(new WatchItem(0x7E0BA0, "Hero Max HP", WatchSize.U16));
        Watches.Add(new WatchItem(0x7E0BA2, "Hero MP",    WatchSize.U16));
        Watches.Add(new WatchItem(0x7E0BA4, "Hero Max MP", WatchSize.U16));
        Watches.Add(new WatchItem(0x7E0B86, "Hero Level", WatchSize.U8));
        Watches.Add(new WatchItem(0x7E0BF4, "Gold",       WatchSize.U32));
        Watches.Add(new WatchItem(0x7E0016, "Map ID",     WatchSize.U16));
        Watches.Add(new WatchItem(0x7E0078, "X Position", WatchSize.U16));
        Watches.Add(new WatchItem(0x7E007A, "Y Position", WatchSize.U16));

        _host.Attached += () => IsBizHawkRunning = true;
        _host.Exited   += () =>
        {
            IsBizHawkRunning = false;
            IsConnected      = false;
            ConnectionStatus = "BizHawk closed. Click Launch to restart.";
        };

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    // -------------------------------------------------------------------------
    // BizHawk launch
    // -------------------------------------------------------------------------
    [RelayCommand]
    private async System.Threading.Tasks.Task LaunchBizHawk()
    {
        if (_host.IsRunning) return;

        ConnectionStatus = "Launching BizHawk...";
        try
        {
            await _host.LaunchAsync(DefaultEmuHawkPath);
            if (_host.IsRunning)
            {
                ConnectionStatus = "BizHawk running. Load a ROM, then open Tools > External Tools > Lufia Forge Monitor.";
                IsBizHawkRunning = true;
            }
            else
            {
                ConnectionStatus = "Failed to launch BizHawk. Check path.";
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Launch error: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------------
    // Polling loop
    // -------------------------------------------------------------------------
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

        if (!_bridge.IsConnected)
        {
            _bridge.TryConnect();
        }

        if (_bridge.ReadFrame())
        {
            IsConnected      = true;
            ConnectionStatus = "Connected - live WRAM streaming";
            FrameInfo        = $"Frame: {_bridge.FrameCount:N0}";
            RefreshHexView();
            RefreshWatches();
        }
        else if (!_bridge.IsConnected && IsConnected)
        {
            IsConnected      = false;
            ConnectionStatus = IsBizHawkRunning
                ? "BizHawk running - open Tools > External Tools > Lufia Forge Monitor"
                : "Disconnected - click Launch to start BizHawk.";
            FrameInfo        = "";
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
