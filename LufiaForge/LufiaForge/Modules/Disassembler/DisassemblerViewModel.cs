using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Modules.MemoryMonitor;

namespace LufiaForge.Modules.Disassembler;

public partial class DisassemblerViewModel : ObservableObject
{
    // -------------------------------------------------------------------------
    // Dependencies
    // -------------------------------------------------------------------------

    private RomBuffer?    _rom;
    private readonly BookmarkStore       _bookmarks = new();
    private readonly CrossReferenceBuilder _xref    = new();

    // -------------------------------------------------------------------------
    // Observable properties — toolbar inputs
    // -------------------------------------------------------------------------

    [ObservableProperty] private string _newBookmarkLabel = "";
    [ObservableProperty] private string _addressText      = "0x808000";
    [ObservableProperty] private string _byteCountText = "0x200";
    [ObservableProperty] private bool   _flagM         = true;   // 8-bit accumulator by default
    [ObservableProperty] private bool   _flagX         = true;   // 8-bit index by default
    [ObservableProperty] private bool   _followPc      = false;
    [ObservableProperty] private bool   _autoScroll    = true;

    // -------------------------------------------------------------------------
    // Observable properties — state / status
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private bool _canGoBackInternal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoForward))]
    private bool _canGoForwardInternal;

    [ObservableProperty] private bool   _isXrefBuilt;
    [ObservableProperty] private int    _xrefProgress;
    [ObservableProperty] private bool   _isXrefBuilding;
    [ObservableProperty] private string _statusText = "Load a ROM to begin.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentPc))]
    private int? _livePcSnesAddress;

    public bool HasCurrentPc => LivePcSnesAddress.HasValue;

    // -------------------------------------------------------------------------
    // Collections
    // -------------------------------------------------------------------------

    public ObservableCollection<DisassemblyLine> DisassemblyLines { get; } = new();
    public ObservableCollection<Bookmark>        AllBookmarks     { get; } = new();

    // Cross-reference lists for the currently selected line
    public ObservableCollection<string> XrefCalledFrom { get; } = new();
    public ObservableCollection<string> XrefCallsTo    { get; } = new();

    // -------------------------------------------------------------------------
    // Selection + live PC
    // -------------------------------------------------------------------------

    private DisassemblyLine? _selectedLine;
    public DisassemblyLine? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (SetProperty(ref _selectedLine, value))
                RefreshXrefPanel();
        }
    }

    private DisassemblyLine? _currentPcLine;
    private int?             _lastFollowPcAddress;

    /// <summary>
    /// Wired by MainWindow to MemoryMonitorViewModel.AddWatch so the
    /// disassembler can push addresses across without a direct dependency.
    /// </summary>
    public Action<int, string>? RequestAddWatch { get; set; }

    // -------------------------------------------------------------------------
    // Navigation history
    // -------------------------------------------------------------------------

    private readonly Stack<int> _backStack    = new();
    private readonly Stack<int> _forwardStack = new();

    public bool CanGoBack    => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

    // -------------------------------------------------------------------------
    // Init
    // -------------------------------------------------------------------------

    public DisassemblerViewModel()
    {
        // Subscribe to BizHawk live CPU snapshots
        BizHawkBridge.CpuSnapshotReady += OnCpuSnapshot;
    }

    public void SetRom(RomBuffer rom)
    {
        _rom = rom;
        _bookmarks.Open(rom.FilePath);
        RefreshBookmarkList();
        DisassemblyLines.Clear();
        _xref.Xref.Clear();
        IsXrefBuilt   = false;
        LivePcSnesAddress    = null;
        _currentPcLine       = null;
        _lastFollowPcAddress = null;
        StatusText = "ROM loaded. Enter an address and click Disassemble.";

        // Default to the reset vector (program entry point) if file is big enough
        if (rom.Length >= 0x8000)
        {
            // LoROM reset vector is at $FFFC (file offset 0x7FFC) — 2-byte LE pointer
            ushort resetVec = rom.ReadUInt16Le(0x7FFC);
            AddressText = $"0x{Lufia1Constants.FileOffsetToSnesAddress((resetVec & 0x7FFF)):X6}";
        }
    }

    // -------------------------------------------------------------------------
    // Disassemble command
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void Disassemble()
    {
        if (_rom == null) { StatusText = "No ROM loaded."; return; }

        if (!TryParseAddress(AddressText, out int startAddr))
        {
            StatusText = $"Invalid address: \"{AddressText}\"  (use hex, e.g. 0x808000 or $808000)";
            return;
        }

        if (!TryParseHex(ByteCountText, out int byteCount) || byteCount <= 0)
            byteCount = 0x200;

        int fileOffset = Lufia1Constants.SnesAddressToFileOffset(startAddr);
        if (fileOffset < 0 || fileOffset >= _rom.Length)
        {
            StatusText = $"Address ${startAddr:X6} is outside the ROM (offset 0x{fileOffset:X6}).";
            return;
        }

        var initialState = new CpuState { M = FlagM, X = FlagX };
        var lines = LinearDisassembler.Disassemble(_rom, fileOffset, byteCount, initialState);

        _bookmarks.AnnotateLines(lines);

        DisassemblyLines.Clear();
        foreach (var line in lines)
            DisassemblyLines.Add(line);

        // Re-apply current PC highlight
        if (LivePcSnesAddress.HasValue)
            ApplyPcHighlight(LivePcSnesAddress.Value);

        StatusText = $"Disassembled {lines.Count} instruction(s) from ${startAddr:X6}  " +
                     $"({byteCount} bytes requested)  |  M={Bit(FlagM)} X={Bit(FlagX)}";
    }

    // -------------------------------------------------------------------------
    // Navigation
    // -------------------------------------------------------------------------

    public void NavigateTo(int snesAddress, bool pushHistory = true)
    {
        if (_rom == null) return;

        if (pushHistory && DisassemblyLines.Count > 0)
        {
            _backStack.Push(Lufia1Constants.FileOffsetToSnesAddress(DisassemblyLines[0].FileOffset));
            _forwardStack.Clear();
        }

        AddressText = $"${snesAddress:X6}";
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        Disassemble();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (_backStack.Count == 0) return;
        int addr = _backStack.Pop();

        if (DisassemblyLines.Count > 0)
            _forwardStack.Push(Lufia1Constants.FileOffsetToSnesAddress(DisassemblyLines[0].FileOffset));

        AddressText = $"${addr:X6}";
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        Disassemble();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        int addr = _forwardStack.Pop();

        if (DisassemblyLines.Count > 0)
            _backStack.Push(Lufia1Constants.FileOffsetToSnesAddress(DisassemblyLines[0].FileOffset));

        AddressText = $"${addr:X6}";
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        Disassemble();
    }

    [RelayCommand]
    private void FollowJump(DisassemblyLine? line)
    {
        if (line?.JumpTarget == null) return;
        NavigateTo(line.JumpTarget.Value);
    }

    // -------------------------------------------------------------------------
    // Export
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void ExportAsm()
    {
        if (DisassemblyLines.Count == 0)
        {
            StatusText = "Nothing to export — disassemble first.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title      = "Export Disassembly",
            Filter     = "Assembly source (*.asm)|*.asm|Text file (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = "asm",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"; Lufia Forge disassembly — generated {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"; ROM: {Path.GetFileName(_rom?.FilePath ?? "unknown")}");
            sb.AppendLine($"; Flags: M={Bit(FlagM)} X={Bit(FlagX)}");
            sb.AppendLine();

            foreach (var line in DisassemblyLines)
            {
                if (line.IsData)
                {
                    sb.AppendLine($"                {line.Mnemonic,-8}              ; [DATA?] ${line.SnesAddress:X6}");
                    continue;
                }

                string addr    = $"${line.SnesAddress:X6}";
                string raw     = line.RawBytesHex.PadRight(10);
                string mnem    = line.Mnemonic.PadRight(5);
                string operand = line.Operand.PadRight(14);
                string comment = line.Comment != null ? $"  {line.Comment}" : "";

                sb.AppendLine($"{addr}  {raw}  {mnem} {operand}{comment}");
            }

            File.WriteAllText(dlg.FileName, sb.ToString());
            StatusText = $"Exported {DisassemblyLines.Count} lines to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------------
    // Bookmark commands (called from context menu via CommandParameter binding)
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void AddBookmark(DisassemblyLine? line)
    {
        if (line == null) return;

        string label = NewBookmarkLabel.Trim();
        if (string.IsNullOrWhiteSpace(label))
            label = $"sub_{line.SnesAddress:X6}";

        _bookmarks.Add(line.SnesAddress, label);

        // Update inline comment on the visible line
        string bookmarkComment = $"; {label}";
        line.Comment = string.IsNullOrEmpty(line.Comment)
            ? bookmarkComment
            : $"{bookmarkComment}  {line.Comment}";

        RefreshBookmarkList();
        NewBookmarkLabel = "";
        StatusText = $"Bookmark added: {label} @ ${line.SnesAddress:X6}";
    }

    [RelayCommand]
    private void RemoveBookmark(Bookmark? bm)
    {
        if (bm == null) return;
        _bookmarks.Remove(bm.SnesAddress);
        RefreshBookmarkList();

        // Clear the comment from any currently visible line at that address
        var line = DisassemblyLines.FirstOrDefault(l => l.SnesAddress == bm.SnesAddress);
        if (line != null && line.Comment?.Contains(bm.Label) == true)
            line.Comment = null;

        StatusText = $"Bookmark removed: {bm.Label}";
    }

    [RelayCommand]
    private void NavigateToBookmark(Bookmark? bm)
    {
        if (bm == null) return;
        NavigateTo(bm.SnesAddress);
    }

    [RelayCommand]
    private void CopyLine(DisassemblyLine? line)
    {
        if (line == null) return;
        string text = $"${line.SnesAddress:X6}  {line.RawBytesHex,-10}  {line.Mnemonic} {line.Operand}";
        if (!string.IsNullOrEmpty(line.Comment)) text += $"  {line.Comment}";
        System.Windows.Clipboard.SetText(text);
    }

    [RelayCommand]
    private void CopyAddress(DisassemblyLine? line)
    {
        if (line == null) return;
        System.Windows.Clipboard.SetText($"${line.SnesAddress:X6}");
    }

    [RelayCommand]
    private void MarkAsData(DisassemblyLine? line)
    {
        if (line == null) return;
        // Add a bookmark flagged as "data region" so XRef builder skips it
        _bookmarks.Add(line.SnesAddress, $"DATA_{line.SnesAddress:X6}", "Marked as data", "#C0392B");
        RefreshBookmarkList();
        StatusText = $"${line.SnesAddress:X6} marked as data.";
    }

    // -------------------------------------------------------------------------
    // Add to Watchlist
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void AddToWatchlist(DisassemblyLine? line)
    {
        if (line == null || RequestAddWatch == null) return;

        // Try to pull a WRAM address out of the operand first.
        // Fall back to the instruction address itself (useful for tracing code paths).
        int address = TryExtractWramAddress(line.Operand) ?? line.SnesAddress;

        // Build a label: "<mnemonic> <operand>" so the user knows where it came from
        string baseLabel = $"{line.Mnemonic} {line.Operand}".Trim();
        string label     = string.IsNullOrEmpty(line.Comment)
            ? baseLabel
            : $"{baseLabel}  {line.Comment}";

        RequestAddWatch(address, label);
        StatusText = $"Added to watchlist: ${address:X6}  ({label})";
    }

    /// <summary>
    /// Attempts to parse a WRAM address ($7Exxxx / $7Fxxxx, or $xxxx ≤ $1FFF)
    /// from a formatted operand string. Returns null if no WRAM address is found.
    /// </summary>
    private static int? TryExtractWramAddress(string operand)
    {
        if (string.IsNullOrEmpty(operand)) return null;

        // Walk through the operand looking for '$' followed by hex digits
        int i = 0;
        while (i < operand.Length)
        {
            int dollar = operand.IndexOf('$', i);
            if (dollar < 0) break;

            int start = dollar + 1;
            int end   = start;
            while (end < operand.Length && Uri.IsHexDigit(operand[end]))
                end++;

            int len = end - start;
            if (len >= 4 && int.TryParse(operand.AsSpan(start, Math.Min(len, 6)),
                                          System.Globalization.NumberStyles.HexNumber,
                                          null, out int addr))
            {
                // Full 24-bit WRAM address
                if (addr >= 0x7E0000 && addr <= 0x7FFFFF) return addr;

                // 16-bit address in first WRAM mirror ($0000–$1FFF)
                if (len == 4 && addr <= 0x1FFF) return 0x7E0000 | addr;
            }

            i = end + 1;
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Cross-reference builder
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task BuildXref()
    {
        if (_rom == null) { StatusText = "No ROM loaded."; return; }
        if (IsXrefBuilding) return;

        IsXrefBuilding = true;
        XrefProgress   = 0;
        StatusText     = "Building cross-reference index — scanning entire ROM…";

        var progress = new Progress<int>(p =>
        {
            XrefProgress = p;
        });

        try
        {
            await _xref.BuildAsync(_rom, _bookmarks, progress);
            IsXrefBuilt    = true;
            IsXrefBuilding = false;
            StatusText     = $"Cross-reference complete — {_xref.Xref.Count:N0} unique targets indexed.";
            RefreshXrefPanel();
        }
        catch (OperationCanceledException)
        {
            IsXrefBuilding = false;
            StatusText     = "Cross-reference build cancelled.";
        }
        catch (Exception ex)
        {
            IsXrefBuilding = false;
            StatusText     = $"Cross-reference failed: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------------
    // Live PC tracking
    // -------------------------------------------------------------------------

    private void OnCpuSnapshot(BizHawkBridge.CpuSnapshot snapshot)
    {
        // Marshal to UI thread
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LivePcSnesAddress = snapshot.SnesAddress;
            ApplyPcHighlight(snapshot.SnesAddress);
        });
    }

    private void ApplyPcHighlight(int snesAddress)
    {
        // Clear old highlight
        if (_currentPcLine != null)
        {
            _currentPcLine.IsCurrentPc = false;
            _currentPcLine = null;
        }

        var line = DisassemblyLines.FirstOrDefault(l => l.SnesAddress == snesAddress);

        // PC moved outside the current view — re-disassemble if Follow PC is on.
        // _lastFollowPcAddress guards against re-building every frame while the PC
        // sits in an off-screen loop.
        if (line == null && FollowPc && snesAddress != _lastFollowPcAddress)
        {
            _lastFollowPcAddress = snesAddress;
            AddressText = $"${snesAddress:X6}";
            Disassemble();
            line = DisassemblyLines.FirstOrDefault(l => l.SnesAddress == snesAddress);
        }

        if (line == null) return;

        line.IsCurrentPc = true;
        _currentPcLine   = line;

        // AutoScroll signal is raised via an event that the View handles
        if (AutoScroll)
            ScrollRequested?.Invoke(line);
    }

    /// <summary>Fired when the view should scroll a specific line into view (live PC tracking).</summary>
    public event Action<DisassemblyLine>? ScrollRequested;

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void RefreshBookmarkList()
    {
        AllBookmarks.Clear();
        foreach (var bm in _bookmarks.GetAll())
            AllBookmarks.Add(bm);
    }

    private void RefreshXrefPanel()
    {
        XrefCalledFrom.Clear();
        XrefCallsTo.Clear();

        if (_selectedLine == null || !_xref.IsBuilt) return;

        foreach (int addr in _xref.GetCalledFrom(_selectedLine.SnesAddress))
        {
            string label = _bookmarks.GetByAddress(addr)?.Label ?? $"${addr:X6}";
            XrefCalledFrom.Add($"${addr:X6}  {label}");
        }

        foreach (int addr in _xref.GetCallsTo(_selectedLine.SnesAddress))
        {
            string label = _bookmarks.GetByAddress(addr)?.Label ?? $"${addr:X6}";
            XrefCallsTo.Add($"${addr:X6}  {label}");
        }
    }

    private static bool TryParseAddress(string text, out int value)
    {
        text = (text ?? "").Trim();
        // Accept $XXXXXX, 0xXXXXXX, or plain decimal
        if (text.StartsWith('$'))
            return int.TryParse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return TryParseHex(text, out value);
    }

    private static bool TryParseHex(string text, out int value)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        else if (text.StartsWith('$'))
            text = text[1..];
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string Bit(bool v) => v ? "1" : "0";
}
