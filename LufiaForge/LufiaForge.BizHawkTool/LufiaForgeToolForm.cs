using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using BizHawk.Client.Common;
using BizHawk.Client.EmuHawk;

namespace LufiaForge.BizHawkTool;

[ExternalTool("Lufia Forge Monitor",
    Description = "Live WRAM viewer, watchlist, and memory search for Lufia 1 (SNES)")]
[ExternalToolApplicability.SingleSystem("SNES")]
public sealed class LufiaForgeToolForm : ToolFormBase, IExternalToolForm
{
    // -------------------------------------------------------------------------
    // BizHawk API injection
    // -------------------------------------------------------------------------
    public ApiContainer? _apiContainer { get; set; }
    private ApiContainer APIs => _apiContainer!;

    protected override string WindowTitleStatic => "Lufia Forge Monitor";

    // -------------------------------------------------------------------------
    // WRAM constants
    // -------------------------------------------------------------------------
    private const int    WramSize    = 0x20000; // 128 KB
    private const string WramDomain  = "WRAM";
    private const int    HexRows     = 24;
    private const int    HexCols     = 16;

    // -------------------------------------------------------------------------
    // UI controls
    // -------------------------------------------------------------------------
    private TabControl    _tabs          = null!;
    private TextBox       _hexView       = null!;
    private ListView      _watchList     = null!;
    private TextBox       _watchAddr     = null!;
    private TextBox       _watchLabel    = null!;
    private ComboBox      _watchType     = null!;
    private TextBox       _searchValue   = null!;
    private ComboBox      _searchType    = null!;
    private ListView      _searchResults = null!;
    private Label         _statusLabel   = null!;
    private NumericUpDown _hexOffset     = null!;

    // -------------------------------------------------------------------------
    // Shared memory (MemoryMappedFile) for Lufia Forge WPF bridge
    // -------------------------------------------------------------------------
    private const string MmfName       = "LufiaForge_WRAM";
    private const int    MmfHeaderSize = 256;
    private const int    MmfTotalSize  = MmfHeaderSize + WramSize; // 256 + 131072
    private MemoryMappedFile?         _mmf;
    private MemoryMappedViewAccessor? _mmfAccessor;
    private uint _frameCounter;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private byte[]? _wram;
    private byte[]? _prevWram;
    private List<int>? _searchCandidates; // RAM offsets surviving Next Scan

    // -------------------------------------------------------------------------
    // Init
    // -------------------------------------------------------------------------
    public LufiaForgeToolForm()
    {
        BuildUI();
        InitSharedMemory();
    }

    private void InitSharedMemory()
    {
        try
        {
            _mmf         = MemoryMappedFile.CreateOrOpen(MmfName, MmfTotalSize);
            _mmfAccessor = _mmf.CreateViewAccessor();
        }
        catch { /* WPF app won't connect — not fatal */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Signal disconnected
        try { _mmfAccessor?.Write(20, (uint)0); } catch { }
        _mmfAccessor?.Dispose();
        _mmf?.Dispose();
        base.OnFormClosing(e);
    }

    // -------------------------------------------------------------------------
    // BizHawk callbacks
    // -------------------------------------------------------------------------

    /// <summary>Called when a new ROM is loaded or the tool is opened.</summary>
    public override void Restart()
    {
        _wram           = null;
        _prevWram       = null;
        _searchCandidates = null;
        _searchResults.Items.Clear();
        _statusLabel.Text = "ROM loaded — monitoring WRAM.";
    }

    /// <summary>Called every frame by BizHawk.</summary>
    public override void UpdateValues(ToolFormUpdateType type)
    {
        if (type is not (ToolFormUpdateType.PreFrame or ToolFormUpdateType.FastPreFrame))
            return;

        if (!IsWramAvailable()) return;

        _prevWram = _wram;
        _wram     = ReadWram();

        WriteToSharedMemory();
        RefreshHexView();
        RefreshWatchlist();
    }

    private void WriteToSharedMemory()
    {
        if (_mmfAccessor == null || _wram == null) return;
        try
        {
            _frameCounter++;
            // Magic: "LFWM"
            _mmfAccessor.Write(0,  (byte)'L');
            _mmfAccessor.Write(1,  (byte)'F');
            _mmfAccessor.Write(2,  (byte)'W');
            _mmfAccessor.Write(3,  (byte)'M');
            _mmfAccessor.Write(4,  _frameCounter);
            _mmfAccessor.Write(8,  DateTime.UtcNow.Ticks);
            _mmfAccessor.Write(16, (uint)WramSize);
            _mmfAccessor.Write(20, (uint)1); // flags: connected
            _mmfAccessor.WriteArray(MmfHeaderSize, _wram, 0, _wram.Length);
        }
        catch { /* non-fatal */ }
    }

    // -------------------------------------------------------------------------
    // WRAM helpers
    // -------------------------------------------------------------------------
    private bool IsWramAvailable()
    {
        try { return APIs.Memory.GetMemoryDomainList().Contains(WramDomain); }
        catch { return false; }
    }

    private byte[] ReadWram()
    {
        var data = APIs.Memory.ReadByteRange(0, WramSize, WramDomain);
        var arr  = new byte[WramSize];
        for (int i = 0; i < WramSize; i++) arr[i] = data[i];
        return arr;
    }

    // -------------------------------------------------------------------------
    // Hex view
    // -------------------------------------------------------------------------
    private void RefreshHexView()
    {
        if (_wram == null || !_tabs.SelectedTab?.Text?.StartsWith("Hex") == true) return;

        int start = (int)_hexOffset.Value & ~0xF;
        int end   = Math.Min(WramSize, start + HexRows * HexCols);

        var sb = new StringBuilder();
        for (int row = start; row < end; row += HexCols)
        {
            sb.Append($"$7E{row:X4}  ");
            for (int col = 0; col < HexCols && row + col < end; col++)
            {
                sb.Append($"{_wram[row + col]:X2} ");
                if (col == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int col = 0; col < HexCols && row + col < end; col++)
            {
                byte b = _wram[row + col];
                sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            }
            sb.AppendLine();
        }
        _hexView.Text = sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Watchlist
    // -------------------------------------------------------------------------
    private void RefreshWatchlist()
    {
        if (_wram == null) return;
        foreach (ListViewItem item in _watchList.Items)
        {
            if (item.Tag is not WatchEntry w) continue;
            int offset = w.SnesAddr - 0x7E0000;
            if (offset < 0 || offset >= WramSize) continue;

            long cur  = ReadTyped(_wram,  offset, w.Type);
            long prev = _prevWram != null ? ReadTyped(_prevWram, offset, w.Type) : cur;

            item.SubItems[2].Text = cur.ToString();
            item.SubItems[3].Text = prev.ToString();
            item.SubItems[4].Text = (cur - prev).ToString();
        }
    }

    private void AddWatch_Click(object? sender, EventArgs e)
    {
        string addrText = _watchAddr.Text.TrimStart('$', '0', 'x', 'X').Trim();
        if (!int.TryParse(addrText, System.Globalization.NumberStyles.HexNumber,
                          null, out int snesAddr)) return;

        var w    = new WatchEntry(snesAddr, _watchLabel.Text, (WatchSize)_watchType.SelectedIndex);
        var item = new ListViewItem($"${snesAddr:X6}");
        item.SubItems.Add(string.IsNullOrWhiteSpace(w.Label) ? $"${snesAddr:X6}" : w.Label);
        item.SubItems.Add("--");  // current
        item.SubItems.Add("--");  // previous
        item.SubItems.Add("0");   // delta
        item.Tag = w;
        _watchList.Items.Add(item);
        _watchAddr.Text = "";
    }

    private void RemoveWatch_Click(object? sender, EventArgs e)
    {
        foreach (ListViewItem item in _watchList.SelectedItems)
            _watchList.Items.Remove(item);
    }

    // -------------------------------------------------------------------------
    // Memory search
    // -------------------------------------------------------------------------
    private void FirstScan_Click(object? sender, EventArgs e)
    {
        if (_wram == null) { _statusLabel.Text = "No ROM loaded."; return; }
        if (!TryParseSearchValue(out long target))
        {
            _statusLabel.Text = "Invalid search value.";
            return;
        }

        var size = (WatchSize)_searchType.SelectedIndex;
        _searchCandidates = Enumerable.Range(0, WramSize)
            .Where(i => {
                if (i + SizeBytes(size) > WramSize) return false;
                return ReadTyped(_wram, i, size) == target;
            })
            .ToList();

        ShowSearchResults();
    }

    private void NextScan_Click(object? sender, EventArgs e)
    {
        if (_wram == null || _searchCandidates == null) { _statusLabel.Text = "Run First Scan first."; return; }
        if (!TryParseSearchValue(out long target)) { _statusLabel.Text = "Invalid search value."; return; }

        var size = (WatchSize)_searchType.SelectedIndex;
        _searchCandidates = _searchCandidates
            .Where(i => i + SizeBytes(size) <= WramSize && ReadTyped(_wram, i, size) == target)
            .ToList();

        ShowSearchResults();
    }

    private void ResetSearch_Click(object? sender, EventArgs e)
    {
        _searchCandidates = null;
        _searchResults.Items.Clear();
        _statusLabel.Text = "Search reset.";
    }

    private void ShowSearchResults()
    {
        _searchResults.Items.Clear();
        int show = Math.Min(500, _searchCandidates!.Count);
        var size = (WatchSize)_searchType.SelectedIndex;
        for (int i = 0; i < show; i++)
        {
            int offset = _searchCandidates[i];
            var item   = new ListViewItem($"${0x7E0000 + offset:X6}");
            item.SubItems.Add(ReadTyped(_wram!, offset, size).ToString());
            item.Tag = offset;
            _searchResults.Items.Add(item);
        }
        _statusLabel.Text = _searchCandidates.Count switch
        {
            0    => "No matches.",
            > 500 => $"{_searchCandidates.Count:N0} candidates (showing 500) — use Next Scan.",
            _    => $"{_searchCandidates.Count} result(s) found.",
        };
    }

    private void AddResultToWatchlist_Click(object? sender, EventArgs e)
    {
        foreach (ListViewItem item in _searchResults.SelectedItems)
        {
            if (item.Tag is not int offset) continue;
            int snesAddr = 0x7E0000 + offset;
            var w     = new WatchEntry(snesAddr, $"${snesAddr:X6}", (WatchSize)_searchType.SelectedIndex);
            var wItem = new ListViewItem($"${snesAddr:X6}");
            wItem.SubItems.Add(w.Label);
            wItem.SubItems.Add("--");
            wItem.SubItems.Add("--");
            wItem.SubItems.Add("0");
            wItem.Tag = w;
            _watchList.Items.Add(wItem);
        }
    }

    // -------------------------------------------------------------------------
    // Typed read helpers
    // -------------------------------------------------------------------------
    private static long ReadTyped(byte[] ram, int offset, WatchSize size) => size switch
    {
        WatchSize.U8  => ram[offset],
        WatchSize.U16 => (ushort)(ram[offset] | (ram[offset + 1] << 8)),
        WatchSize.U32 => (uint)(ram[offset] | (ram[offset+1]<<8) | (ram[offset+2]<<16) | (ram[offset+3]<<24)),
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

    private bool TryParseSearchValue(out long value)
    {
        string s = _searchValue.Text.Trim();
        if (s.StartsWith("$"))  return long.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out value);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return long.TryParse(s, out value);
    }

    // -------------------------------------------------------------------------
    // UI builder
    // -------------------------------------------------------------------------
    private void BuildUI()
    {
        Size            = new Size(720, 560);
        MinimumSize     = new Size(600, 400);
        BackColor       = Color.FromArgb(28, 20, 40);
        ForeColor       = Color.FromArgb(220, 200, 255);
        Font            = new Font("Segoe UI", 9f);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.SelectedIndexChanged += (_, _) => RefreshHexView();

        _tabs.TabPages.Add(BuildHexTab());
        _tabs.TabPages.Add(BuildWatchlistTab());
        _tabs.TabPages.Add(BuildSearchTab());

        _statusLabel = new Label
        {
            Dock      = DockStyle.Bottom,
            Height    = 22,
            Text      = "Open a Lufia 1 (SNES) ROM in BizHawk to begin.",
            ForeColor = Color.FromArgb(160, 140, 200),
            Padding   = new Padding(4, 4, 0, 0),
        };

        Controls.Add(_tabs);
        Controls.Add(_statusLabel);
    }

    private TabPage BuildHexTab()
    {
        var page = new TabPage("Hex View") { BackColor = Color.FromArgb(28, 20, 40) };

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(4, 4, 4, 0) };
        var lblOffset = new Label { Text = "Offset:", AutoSize = true, ForeColor = ForeColor };
        _hexOffset = new NumericUpDown
        {
            Minimum = 0, Maximum = WramSize - 1, Increment = 256,
            Width = 80, Left = 60, BackColor = Color.FromArgb(45, 35, 60), ForeColor = ForeColor,
        };
        _hexOffset.ValueChanged += (_, _) => RefreshHexView();
        var btnUp   = new Button { Text = "▲", Width = 28, Left = 148, BackColor = Color.FromArgb(60,50,80), ForeColor = ForeColor, FlatStyle = FlatStyle.Flat };
        var btnDown = new Button { Text = "▼", Width = 28, Left = 180, BackColor = Color.FromArgb(60,50,80), ForeColor = ForeColor, FlatStyle = FlatStyle.Flat };
        btnUp.Click   += (_, _) => { _hexOffset.Value = Math.Max(0, (int)_hexOffset.Value - 256); };
        btnDown.Click += (_, _) => { _hexOffset.Value = Math.Min(WramSize - 256, (int)_hexOffset.Value + 256); };
        toolbar.Controls.AddRange(new Control[] { lblOffset, _hexOffset, btnUp, btnDown });

        _hexView = new TextBox
        {
            Dock      = DockStyle.Fill,
            Multiline = true,
            ReadOnly  = true,
            ScrollBars = ScrollBars.Vertical,
            Font      = new Font("Cascadia Code", 9.5f) ?? new Font("Courier New", 9.5f),
            BackColor = Color.FromArgb(18, 12, 28),
            ForeColor = Color.FromArgb(200, 220, 255),
            BorderStyle = BorderStyle.None,
            Text      = "  Open a Lufia 1 ROM to see live WRAM values.",
        };

        page.Controls.Add(_hexView);
        page.Controls.Add(toolbar);
        return page;
    }

    private TabPage BuildWatchlistTab()
    {
        var page = new TabPage("Watchlist") { BackColor = Color.FromArgb(28, 20, 40) };

        // Toolbar for adding watches
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4, 4, 4, 0) };
        _watchAddr  = MakeTextBox("7E0000", 80); _watchAddr.Left = 4;
        _watchLabel = MakeTextBox("Label",  100); _watchLabel.Left = 90;
        _watchType  = new ComboBox { Items = { "U8","U16","U32","S8","S16" }, SelectedIndex = 0,
                        Width = 52, Left = 196, BackColor = Color.FromArgb(45,35,60), ForeColor = ForeColor,
                        DropDownStyle = ComboBoxStyle.DropDownList };
        var btnAdd  = MakeButton("+", 30, 254, AddWatch_Click);
        var btnRem  = MakeButton("Remove", 60, 290, RemoveWatch_Click);
        toolbar.Controls.AddRange(new Control[] { _watchAddr, _watchLabel, _watchType, btnAdd, btnRem });

        _watchList = new ListView
        {
            Dock        = DockStyle.Fill,
            View        = View.Details,
            FullRowSelect = true,
            GridLines   = true,
            BackColor   = Color.FromArgb(18, 12, 28),
            ForeColor   = Color.FromArgb(200, 220, 255),
            Font        = new Font("Cascadia Code", 9f) ?? new Font("Courier New", 9f),
        };
        _watchList.Columns.Add("Address", 80);
        _watchList.Columns.Add("Label",   100);
        _watchList.Columns.Add("Current", 70);
        _watchList.Columns.Add("Prev",    70);
        _watchList.Columns.Add("Delta",   70);

        page.Controls.Add(_watchList);
        page.Controls.Add(toolbar);
        return page;
    }

    private TabPage BuildSearchTab()
    {
        var page = new TabPage("Search") { BackColor = Color.FromArgb(28, 20, 40) };

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4, 4, 4, 0) };
        _searchValue = MakeTextBox("Value ($hex or decimal)", 140); _searchValue.Left = 4;
        _searchType  = new ComboBox { Items = { "U8","U16","U32","S8","S16" }, SelectedIndex = 0,
                         Width = 52, Left = 150, BackColor = Color.FromArgb(45,35,60), ForeColor = ForeColor,
                         DropDownStyle = ComboBoxStyle.DropDownList };
        var btnFirst = MakeButton("First Scan", 80, 208, FirstScan_Click);
        var btnNext  = MakeButton("Next Scan",  75, 294, NextScan_Click);
        var btnReset = MakeButton("Reset",      50, 375, ResetSearch_Click);
        toolbar.Controls.AddRange(new Control[] { _searchValue, _searchType, btnFirst, btnNext, btnReset });

        _searchResults = new ListView
        {
            Dock      = DockStyle.Fill,
            View      = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = Color.FromArgb(18, 12, 28),
            ForeColor = Color.FromArgb(200, 220, 255),
            Font      = new Font("Cascadia Code", 9f) ?? new Font("Courier New", 9f),
        };
        _searchResults.Columns.Add("Address", 90);
        _searchResults.Columns.Add("Value",   80);

        var btnAddToWatch = MakeButton("Add to Watchlist", 120, 0, AddResultToWatchlist_Click);
        btnAddToWatch.Dock = DockStyle.Bottom;

        page.Controls.Add(_searchResults);
        page.Controls.Add(btnAddToWatch);
        page.Controls.Add(toolbar);
        return page;
    }

    // -------------------------------------------------------------------------
    // UI helpers
    // -------------------------------------------------------------------------
    private TextBox MakeTextBox(string placeholder, int width)
        => new()
        {
            Width     = width,
            Top       = 4,
            Text      = placeholder,
            BackColor = Color.FromArgb(45, 35, 60),
            ForeColor = Color.FromArgb(160, 140, 200),
            BorderStyle = BorderStyle.FixedSingle,
        };

    private Button MakeButton(string text, int width, int left, EventHandler click)
    {
        var btn = new Button
        {
            Text      = text,
            Width     = width,
            Left      = left,
            Top       = 4,
            Height    = 24,
            BackColor = Color.FromArgb(60, 50, 80),
            ForeColor = ForeColor,
            FlatStyle = FlatStyle.Flat,
        };
        btn.Click += click;
        return btn;
    }
}

// -------------------------------------------------------------------------
// Simple data models
// -------------------------------------------------------------------------

internal enum WatchSize { U8, U16, U32, S8, S16 }

internal sealed record WatchEntry(int SnesAddr, string Label, WatchSize Type);
