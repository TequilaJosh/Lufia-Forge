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
    private RomBuffer?    _rom;
    private readonly BookmarkStore       _bookmarks = new();
    private readonly CrossReferenceBuilder _xref    = new();

    // toolbar inputs

    [ObservableProperty] private string _newBookmarkLabel = "";
    [ObservableProperty] private string _addressText      = "0x808000";
    [ObservableProperty] private string _byteCountText = "0x200";
    [ObservableProperty] private bool   _flagM         = true;   // 8-bit accumulator by default
    [ObservableProperty] private bool   _flagX         = true;   // 8-bit index by default
    [ObservableProperty] private bool   _followPc      = false;
    [ObservableProperty] private bool   _autoScroll    = true;

    // state / status

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

    public ObservableCollection<DisassemblyLine> DisassemblyLines { get; } = new();
    public ObservableCollection<Bookmark>        AllBookmarks     { get; } = new();

    // cross-references of the selected line
    public ObservableCollection<string> XrefCalledFrom { get; } = new();
    public ObservableCollection<string> XrefCallsTo    { get; } = new();

    // selection + live PC

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

    // MainWindow hooks this up to MemoryMonitorViewModel.AddWatch so we can send addresses over
    // without the disassembler knowing about the memory monitor
    public Action<int, string>? RequestAddWatch { get; set; }

    // back / forward history

    private readonly Stack<int> _backStack    = new();
    private readonly Stack<int> _forwardStack = new();

    public bool CanGoBack    => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

    public DisassemblerViewModel()
    {
        // live CPU snapshots from BizHawk
        BizHawkBridge.CpuSnapshotReady += OnCpuSnapshot;
    }

    private CancellationTokenSource? _xrefCts;

    public void SetRom(RomBuffer rom)
    {
        _rom = rom;
        _bookmarks.Open(rom.FilePath);
        RefreshBookmarkList();
        DisassemblyLines.Clear();
        // a build still going is for the old ROM, so stop it (its index never gets put in)
        _xrefCts?.Cancel();
        _xref.Clear();
        IsXrefBuilt   = false;
        LivePcSnesAddress    = null;
        _currentPcLine       = null;
        _lastFollowPcAddress = null;
        StatusText = "ROM loaded. Enter an address and click Disassemble.";

        // start at the reset vector (where the program starts) if the file's big enough
        if (rom.Length >= 0x8000)
        {
            // LoROM reset vector is at $FFFC (file offset 0x7FFC), a 2-byte little-endian pointer
            ushort resetVector = rom.ReadUInt16Le(0x7FFC);
            AddressText = $"0x{Lufia1Constants.FileOffsetToSnesAddress((resetVector & 0x7FFF)):X6}";
        }
    }

    [RelayCommand]
    private void Disassemble()
    {
        if (_rom == null) { StatusText = "No ROM loaded."; return; }

        if (!TryParseAddress(AddressText, out int startAddress))
        {
            StatusText = $"Invalid address: \"{AddressText}\"  (use hex, e.g. 0x808000 or $808000)";
            return;
        }

        if (!TryParseHex(ByteCountText, out int byteCount) || byteCount <= 0)
            byteCount = 0x200;

        int fileOffset = Lufia1Constants.SnesAddressToFileOffset(startAddress);
        if (fileOffset < 0 || fileOffset >= _rom.Length)
        {
            StatusText = $"Address ${startAddress:X6} is outside the ROM (offset 0x{fileOffset:X6}).";
            return;
        }

        var initialState = new CpuState { M = FlagM, X = FlagX };
        var lines = LinearDisassembler.Disassemble(_rom, fileOffset, byteCount, initialState, _bookmarks.DataRegionAtFile);

        _bookmarks.AnnotateLines(lines);

        DisassemblyLines.Clear();
        foreach (var line in lines)
            DisassemblyLines.Add(line);

        // put the PC highlight back
        if (LivePcSnesAddress.HasValue)
            { if (RomPc(LivePcSnesAddress.Value) is int pc and >= 0) ApplyPcHighlight(pc); }

        StatusText = $"Disassembled {lines.Count} instruction(s) from ${startAddress:X6}  " +
                     $"({byteCount} bytes requested)  |  M={Bit(FlagM)} X={Bit(FlagX)}";
    }

    // navigation

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
        int address = _backStack.Pop();

        if (DisassemblyLines.Count > 0)
            _forwardStack.Push(Lufia1Constants.FileOffsetToSnesAddress(DisassemblyLines[0].FileOffset));

        AddressText = $"${address:X6}";
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        Disassemble();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        int address = _forwardStack.Pop();

        if (DisassemblyLines.Count > 0)
            _backStack.Push(Lufia1Constants.FileOffsetToSnesAddress(DisassemblyLines[0].FileOffset));

        AddressText = $"${address:X6}";
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

    // export

    [RelayCommand]
    private void ExportAsm()
    {
        if (DisassemblyLines.Count == 0)
        {
            StatusText = "Nothing to export: disassemble first.";
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
            sb.AppendLine($"; Lufia Forge disassembly, generated {DateTime.Now:yyyy-MM-dd HH:mm}");
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

                string address = $"${line.SnesAddress:X6}";
                string rawText = line.RawBytesHex.PadRight(10);
                string mnemonic = line.Mnemonic.PadRight(5);
                string operand = line.Operand.PadRight(14);
                string comment = (line.Comment != null ? $"  {line.Comment}" : "") + $"  ; {line.BaseCycles} cyc";

                sb.AppendLine($"{address}  {rawText}  {mnemonic} {operand}{comment}");
            }

            File.WriteAllText(dlg.FileName, sb.ToString());
            StatusText = $"Exported {DisassemblyLines.Count} lines to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    // bookmark commands (the context menu calls these through CommandParameter)

    [RelayCommand]
    private void AddBookmark(DisassemblyLine? line)
    {
        if (line == null) return;

        string label = NewBookmarkLabel.Trim();
        if (string.IsNullOrWhiteSpace(label))
            label = $"sub_{line.SnesAddress:X6}";

        _bookmarks.Add(line.SnesAddress, label);

        // show it on the line right away too
        string bookmarkComment = $"; {label}";
        line.Comment = string.IsNullOrEmpty(line.Comment)
            ? bookmarkComment
            : $"{bookmarkComment}  {line.Comment}";

        RefreshBookmarkList();
        NewBookmarkLabel = "";
        StatusText = $"Bookmark added: {label} @ ${line.SnesAddress:X6}";
    }

    [RelayCommand]
    private void RemoveBookmark(Bookmark? bookmark)
    {
        if (bookmark == null) return;
        _bookmarks.Remove(bookmark.SnesAddress);
        RefreshBookmarkList();

        // take the comment off the line at that address if it's showing
        var line = DisassemblyLines.FirstOrDefault(l => l.SnesAddress == bookmark.SnesAddress);
        if (line != null && line.Comment?.Contains(bookmark.Label) == true)
            line.Comment = null;

        StatusText = $"Bookmark removed: {bookmark.Label}";
    }

    [RelayCommand]
    private void NavigateToBookmark(Bookmark? bookmark)
    {
        if (bookmark == null) return;
        NavigateTo(bookmark.SnesAddress);
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
        // a data region (joined with one right before/after it) shows as .db lines and the XRef builder skips it
        _bookmarks.MarkData(line.SnesAddress, Math.Max(1, line.RawBytes.Length));
        RefreshBookmarkList();
        Disassemble();
        StatusText = $"${line.SnesAddress:X6} marked as data ({Math.Max(1, line.RawBytes.Length)} bytes). Mark the next line too to grow the region; " +
                     "remove its DATA_ bookmark to undo.";
    }

    // add to watchlist

    [RelayCommand]
    private void AddToWatchlist(DisassemblyLine? line)
    {
        if (line == null || RequestAddWatch == null) return;

        // try to get a WRAM address out of the operand first, if there isn't one use the instruction's own address
        // (handy for tracing code paths)
        int address = TryExtractWramAddress(line.Operand) ?? line.SnesAddress;

        // label is "<mnemonic> <operand>" so you can tell where it came from
        string baseLabel = $"{line.Mnemonic} {line.Operand}".Trim();
        string label     = string.IsNullOrEmpty(line.Comment)
            ? baseLabel
            : $"{baseLabel}  {line.Comment}";

        RequestAddWatch(address, label);
        StatusText = $"Added to watchlist: ${address:X6}  ({label})";
    }

    // pulls a WRAM address ($7Exxxx / $7Fxxxx, or $xxxx <= $1FFF) out of a formatted operand. null if there isn't one
    private static int? TryExtractWramAddress(string operand)
    {
        if (string.IsNullOrEmpty(operand)) return null;

        // look for '$' followed by hex digits
        int i = 0;
        while (i < operand.Length)
        {
            int dollar = operand.IndexOf('$', i);
            if (dollar < 0) break;

            int start = dollar + 1;
            int end   = start;
            while (end < operand.Length && Uri.IsHexDigit(operand[end]))
                end++;

            int length = end - start;
            if (length >= 4 && int.TryParse(operand.AsSpan(start, Math.Min(length, 6)),
                                          System.Globalization.NumberStyles.HexNumber,
                                          null, out int address))
            {
                // full 24-bit WRAM address
                if (address >= 0x7E0000 && address <= 0x7FFFFF) return address;

                // 16-bit address in the first WRAM mirror ($0000-$1FFF)
                if (length == 4 && address <= 0x1FFF) return 0x7E0000 | address;
            }

            i = end + 1;
        }
        return null;
    }

    // cross-references

    [RelayCommand]
    private async Task BuildXref()
    {
        if (_rom == null) { StatusText = "No ROM loaded."; return; }
        if (IsXrefBuilding) return;

        IsXrefBuilding = true;
        XrefProgress   = 0;
        StatusText     = "Building cross-reference index: scanning entire ROM…";

        var progress = new Progress<int>(percent =>
        {
            XrefProgress = percent;
        });

        _xrefCts?.Cancel();
        var tokenSource = _xrefCts = new CancellationTokenSource();
        var rom = _rom;
        try
        {
            await _xref.BuildAsync(rom, _bookmarks, progress, tokenSource.Token);
            if (tokenSource.IsCancellationRequested || rom != _rom) throw new OperationCanceledException();
            IsXrefBuilt    = true;
            IsXrefBuilding = false;
            StatusText     = $"Cross-reference complete: {_xref.Xref.Count:N0} unique targets indexed; " +
                             $"{_xref.Unresolved.Count:N0} indirect jumps/calls can't be resolved statically (live PC tracking shows where they go).";
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

    // live PC tracking

    private void OnCpuSnapshot(BizHawkBridge.CpuSnapshot snapshot)
    {
        // over to the UI thread
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LivePcSnesAddress = snapshot.SnesAddress;
            // the lines are labelled in banks $80 up (FileOffsetToSnesAddress) but Lufia 1 runs in $00-$3F, the same ROM,
            // so compared as is the PC never matched. a PC in RAM ($7E/$7F, or below $8000) isn't ROM code, we don't follow it
            int pc = RomPc(snapshot.SnesAddress);
            if (pc < 0) { ClearPcHighlight(); return; }
            ApplyPcHighlight(pc, snapshot.FlagM, snapshot.FlagX);
        });
    }

    // the PC the way the lines label it (banks $80 up), or -1 when it isn't in the ROM (RAM, below $8000)
    private static int RomPc(int snesAddress)
    {
        int bank = snesAddress >> 16 & 0xFF, address = snesAddress & 0xFFFF;
        return address < 0x8000 || bank is 0x7E or 0x7F ? -1 : ((bank & 0x7F) | 0x80) << 16 | address;
    }

    private void ClearPcHighlight()
    {
        if (_currentPcLine == null) return;
        _currentPcLine.IsCurrentPc = false;
        _currentPcLine = null;
    }

    private void ApplyPcHighlight(int snesAddress, bool? liveM = null, bool? liveX = null)
    {
        ClearPcHighlight();

        var line = DisassemblyLines.FirstOrDefault(l => l.SnesAddress == snesAddress);

        // PC went outside the current view, disassemble again if Follow PC is on.
        // _lastFollowPcAddress stops it rebuilding every frame while the PC sits in a loop that's off screen
        if (line == null && FollowPc && snesAddress != _lastFollowPcAddress)
        {
            _lastFollowPcAddress = snesAddress;
            AddressText = $"${snesAddress:X6}";
            // decoded with the CPU's own M and X flags there (with the toolbar's, LDA #$1234 in 16-bit code was read as
            // LDA #$34 and every line after it went wrong)
            if (liveM is bool m) FlagM = m;
            if (liveX is bool x) FlagX = x;
            Disassemble();
            line = DisassemblyLines.FirstOrDefault(l => l.SnesAddress == snesAddress);
        }

        if (line == null) return;

        line.IsCurrentPc = true;
        _currentPcLine   = line;

        // the view handles the scrolling through this event
        if (AutoScroll)
            ScrollRequested?.Invoke(line);
    }

    // fired when the view should scroll a line into view (live PC tracking)
    public event Action<DisassemblyLine>? ScrollRequested;

    private void RefreshBookmarkList()
    {
        AllBookmarks.Clear();
        foreach (var bookmark in _bookmarks.GetAll())
            AllBookmarks.Add(bookmark);
    }

    private void RefreshXrefPanel()
    {
        XrefCalledFrom.Clear();
        XrefCallsTo.Clear();

        if (_selectedLine == null || !_xref.IsBuilt) return;

        foreach (int address in _xref.GetCalledFrom(_selectedLine.SnesAddress))
        {
            string label = _bookmarks.GetByAddress(address)?.Label ?? $"${address:X6}";
            XrefCalledFrom.Add($"${address:X6}  {label}");
        }

        foreach (int address in _xref.GetCallsTo(_selectedLine.SnesAddress))
        {
            string label = _bookmarks.GetByAddress(address)?.Label ?? $"${address:X6}";
            XrefCallsTo.Add($"${address:X6}  {label}");
        }
    }

    private static bool TryParseAddress(string text, out int value)
    {
        text = (text ?? "").Trim();
        // takes $XXXXXX, 0xXXXXXX or plain XXXXXX, all hex (a plain number is hex too, not decimal)
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

    private static string Bit(bool isSet) => isSet ? "1" : "0";
}
