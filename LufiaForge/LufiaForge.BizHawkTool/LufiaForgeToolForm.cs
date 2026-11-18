using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using BizHawk.Client.Common;
using BizHawk.Client.EmuHawk;
using BizHawk.Emulation.Common;

namespace LufiaForge.BizHawkTool;

[ExternalTool("Lufia Forge Monitor",
    Description = "Live WRAM viewer, watchlist, and memory search for Lufia 1 (SNES)")]
[ExternalToolApplicability.SingleSystem("SNES")]
public sealed class LufiaForgeToolForm : ToolFormBase, IExternalToolForm
{
    // BizHawk fills this in (API injection)
    public ApiContainer? _apiContainer { get; set; }
    private ApiContainer APIs => _apiContainer!;

    protected override string WindowTitleStatic => "Lufia Forge Monitor";

    // WRAM
    private const int    WramSize    = 0x20000; // 128 KB
    private const string WramDomain  = "WRAM";
    private const int    HexRows     = 24;
    private const int    HexCols     = 16;

    // UI controls
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

    // shared memory (a MemoryMappedFile) that the Lufia Forge app reads
    private const string MmfName       = "LufiaForge_WRAM";
    private const int    MmfHeaderSize = 256;
    private const int    MmfTotalSize  = MmfHeaderSize + WramSize; // 256 + 131072
    private MemoryMappedFile?         _mmf;
    private MemoryMappedViewAccessor? _mmfAccessor;
    private uint _frameCounter;

    // state
    private byte[]? _wram;
    private byte[]? _prevWram;
    private List<int>? _searchCandidates; // RAM offsets still left after Next Scan


    // setup
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
        catch { /* the Lufia Forge app just won't connect, that's all */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // tell the app we're gone
        try { _mmfAccessor?.Write(20, (uint)0); } catch { }
        _mmfAccessor?.Dispose();
        _mmf?.Dispose();
        base.OnFormClosing(e);
    }

    // BizHawk callbacks

    // BizHawk calls this when a new ROM loads or the tool opens
    public override void Restart()
    {
        _wram                    = null;
        _prevWram                = null;
        _searchCandidates        = null;
        _searchResults.Items.Clear();
        _statusLabel.Text = "ROM loaded - monitoring WRAM.";

    }


    // BizHawk calls this every frame
    public override void UpdateValues(ToolFormUpdateType type)
    {
        if (type is not (ToolFormUpdateType.PreFrame or ToolFormUpdateType.FastPreFrame))
            return;

        if (!IsWramAvailable()) return;

        _prevWram = _wram;
        _wram     = ReadWram();

        WriteToSharedMemory();
        PollLoadRomCommand();
        RefreshHexView();
        RefreshWatchlist();
    }

    private void WriteToSharedMemory()
    {
        if (_mmfAccessor == null || _wram == null) return;
        try
        {
            _frameCounter++;
            // flags: 1 connected, 2 being written, 4 registers are this frame's, 8 this tool sets 2 and 4. Lufia Forge
            // skips a frame it read while 2 was set, or whose counter changed while it read (a copy that's half one
            // frame and half the next), so the counter gets written last, after the WRAM and the registers
            _mmfAccessor.Write(20, (uint)(1 | 2 | 8));
            // magic: "LFWM"
            _mmfAccessor.Write(0,  (byte)'L');
            _mmfAccessor.Write(1,  (byte)'F');
            _mmfAccessor.Write(2,  (byte)'W');
            _mmfAccessor.Write(3,  (byte)'M');
            _mmfAccessor.Write(8,  DateTime.UtcNow.Ticks);
            _mmfAccessor.Write(16, (uint)WramSize);
            _mmfAccessor.WriteArray(MmfHeaderSize, _wram, 0, _wram.Length);

            // CPU registers (bytes 24-30), read from BizHawk's register list
            bool registers = WriteCpuRegisters();
            _mmfAccessor.Write(4,  _frameCounter);
            _mmfAccessor.Write(20, (uint)(1 | 8 | (registers ? 4 : 0)));
        }
        catch { /* not worth stopping for */ }
    }

    // writes the CPU registers. false if BizHawk gave none (the Snes9x core)
    private bool WriteCpuRegisters()
    {
        if (_mmfAccessor == null) return false;
        try
        {
            // needs the bsnes core (Config -> Cores -> SNES -> BSNES). Snes9x doesn't do
            // GetCpuFlagsAndRegisters and gives back nothing
            var cpuRegisters = APIs.Emulation.GetRegisters();
            if (cpuRegisters.Count == 0)
            {
                if (_frameCounter % 120 == 0)
                    _statusLabel.Text = "PC unavailable - switch to BSNES core (Config → Cores → SNES).";
                return false;
            }

            cpuRegisters.TryGetValue("PC", out ulong pcRaw);
            ushort programCounter;
            byte   programBank;
            if (pcRaw > 0xFFFF)           // some bsnes builds pack the full 24 bits into "PC"
            {
                programBank = (byte)((pcRaw >> 16) & 0xFF);
                programCounter = (ushort)(pcRaw & 0xFFFF);
            }
            else
            {
                programCounter = (ushort)(pcRaw & 0xFFFF);
                programBank = cpuRegisters.TryGetValue("PB", out ulong pbRaw) ? (byte)(pbRaw & 0xFF) : (byte)0;
            }

            byte   statusFlags = cpuRegisters.TryGetValue("P",  out ulong pRaw)  ? (byte)(pRaw  & 0xFF)    : (byte)0;
            ushort directPage  = cpuRegisters.TryGetValue("D",  out ulong dRaw)  ? (ushort)(dRaw & 0xFFFF) : (ushort)0;
            byte   dataBank    = cpuRegisters.TryGetValue("DB", out ulong dbRaw) ? (byte)(dbRaw  & 0xFF)   : (byte)0;

            _mmfAccessor.Write(24, programCounter);
            _mmfAccessor.Write(26, programBank);
            _mmfAccessor.Write(27, statusFlags);
            _mmfAccessor.Write(28, directPage);
            _mmfAccessor.Write(30, dataBank);

            if (_frameCounter % 60 == 0)
                _statusLabel.Text = $"PC=${((int)programBank << 16) | programCounter:X6}  |  ROM loaded - monitoring WRAM + PC.";
            return true;
        }
        catch { return false; }
    }

    // ROM auto-load command (the Lufia Forge app writes it when the user opens a ROM)
    private static readonly string LoadRomCommandFile =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LufiaForge_LoadROM.txt");

    private void PollLoadRomCommand()
    {
        // only check once a second (~60 frames) so we're not hitting the disk all the time
        if (_frameCounter % 60 != 0) return;
        if (!System.IO.File.Exists(LoadRomCommandFile)) return;

        try
        {
            string romPath = System.IO.File.ReadAllText(LoadRomCommandFile).Trim();
            System.IO.File.Delete(LoadRomCommandFile);

            if (!string.IsNullOrEmpty(romPath) && System.IO.File.Exists(romPath))
            {
                APIs.EmuClient.OpenRom(romPath);
                _statusLabel.Text = $"Loaded: {System.IO.Path.GetFileName(romPath)}";
            }
        }
        catch { /* not worth stopping for */ }
    }

    // WRAM
    private bool IsWramAvailable()
    {
        try { return APIs.Memory.GetMemoryDomainList().Contains(WramDomain); }
        catch { return false; }
    }

    private byte[] ReadWram()
    {
        var data = APIs.Memory.ReadByteRange(0, WramSize, WramDomain);
        var copy = new byte[WramSize];
        for (int i = 0; i < WramSize; i++) copy[i] = data[i];
        return copy;
    }

    // hex view
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
                byte cell = _wram[row + col];
                sb.Append(cell >= 0x20 && cell <= 0x7E ? (char)cell : '.');
            }
            sb.AppendLine();
        }
        _hexView.Text = sb.ToString();
    }

    // watchlist
    private void RefreshWatchlist()
    {
        if (_wram == null) return;
        foreach (ListViewItem item in _watchList.Items)
        {
            if (item.Tag is not WatchEntry watch) continue;
            int offset = watch.SnesAddr - 0x7E0000;
            // a 2 or 4 byte watch on the last bytes of WRAM would read past the end
            if (offset < 0 || offset + SizeBytes(watch.Type) > WramSize) continue;

            long current  = ReadTyped(_wram,  offset, watch.Type);
            long previous = _prevWram != null ? ReadTyped(_prevWram, offset, watch.Type) : current;

            item.SubItems[2].Text = current.ToString();
            item.SubItems[3].Text = previous.ToString();
            item.SubItems[4].Text = (current - previous).ToString();
        }
    }

    private void AddWatch_Click(object? sender, EventArgs e)
    {
        string addressText = _watchAddr.Text.TrimStart('$', '0', 'x', 'X').Trim();
        if (!int.TryParse(addressText, System.Globalization.NumberStyles.HexNumber,
                          null, out int snesAddress)) return;

        var watch = new WatchEntry(snesAddress, _watchLabel.Text, (WatchSize)_watchType.SelectedIndex);
        var item = new ListViewItem($"${snesAddress:X6}");
        item.SubItems.Add(string.IsNullOrWhiteSpace(watch.Label) ? $"${snesAddress:X6}" : watch.Label);
        item.SubItems.Add("--");  // current
        item.SubItems.Add("--");  // previous
        item.SubItems.Add("0");   // delta
        item.Tag = watch;
        _watchList.Items.Add(item);
        _watchAddr.Text = "";
    }

    private void RemoveWatch_Click(object? sender, EventArgs e)
    {
        foreach (ListViewItem item in _watchList.SelectedItems)
            _watchList.Items.Remove(item);
    }

    // memory search
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
        int shown = Math.Min(500, _searchCandidates!.Count);
        var size = (WatchSize)_searchType.SelectedIndex;
        for (int i = 0; i < shown; i++)
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
            > 500 => $"{_searchCandidates.Count:N0} candidates (showing 500) - use Next Scan.",
            _    => $"{_searchCandidates.Count} result(s) found.",
        };
    }

    private void AddResultToWatchlist_Click(object? sender, EventArgs e)
    {
        foreach (ListViewItem item in _searchResults.SelectedItems)
        {
            if (item.Tag is not int offset) continue;
            int snesAddress = 0x7E0000 + offset;
            var watch     = new WatchEntry(snesAddress, $"${snesAddress:X6}", (WatchSize)_searchType.SelectedIndex);
            var watchItem = new ListViewItem($"${snesAddress:X6}");
            watchItem.SubItems.Add(watch.Label);
            watchItem.SubItems.Add("--");
            watchItem.SubItems.Add("--");
            watchItem.SubItems.Add("0");
            watchItem.Tag = watch;
            _watchList.Items.Add(watchItem);
        }
    }

    // reading values by type
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
        string text = _searchValue.Text.Trim();
        if (text.StartsWith("$"))  return long.TryParse(text[1..], System.Globalization.NumberStyles.HexNumber, null, out value);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return long.TryParse(text, out value);
    }

    // building the UI
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

        // toolbar for adding watches
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

    // small UI makers
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
        var button = new Button
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
        button.Click += click;
        return button;
    }
}

// data

internal enum WatchSize { U8, U16, U32, S8, S16 }

internal sealed record WatchEntry(int SnesAddr, string Label, WatchSize Type);
