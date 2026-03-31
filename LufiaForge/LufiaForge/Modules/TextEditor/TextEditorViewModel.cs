using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace LufiaForge.Modules.TextEditor;

public partial class TextEditorViewModel : ObservableObject
{
    private RomBuffer? _rom;

    // -------------------------------------------------------------------------
    // Collections
    // -------------------------------------------------------------------------

    public ObservableCollection<DialogueEntry> AllEntries      { get; } = new();
    public ObservableCollection<DialogueEntry> FilteredEntries { get; } = new();
    public ObservableCollection<(int Offset, string Word)> DictionaryWords { get; } = new();

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntries))]
    [NotifyCanExecuteChangedFor(nameof(CommitEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertEntryCommand))]
    private DialogueEntry? _selectedEntry;

    [ObservableProperty] private string  _searchText    = "";
    [ObservableProperty] private string  _editBuffer    = "";
    [ObservableProperty] private string  _statusText    = "Load a ROM, then click Scan.";
    [ObservableProperty] private int     _scanProgress  = 0;
    [ObservableProperty] private bool    _isScanning    = false;
    [ObservableProperty] private bool    _showModifiedOnly = false;
    [ObservableProperty] private int     _selectedTabIndex = 0; // 0=Dialogue, 1=Dictionary

    public bool HasEntries => AllEntries.Count > 0;
    public int  TotalCount    => AllEntries.Count;
    public int  ModifiedCount => AllEntries.Count(e => e.IsModified);

    // -------------------------------------------------------------------------
    // Init
    // -------------------------------------------------------------------------

    public void SetRom(RomBuffer rom)
    {
        _rom = rom;
        AllEntries.Clear();
        FilteredEntries.Clear();
        DictionaryWords.Clear();
        SelectedEntry = null;
        StatusText    = "ROM loaded. Click 'Scan Dialogue' to find all text strings.";
    }

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task ScanDialogueAsync()
    {
        if (_rom == null) return;

        IsScanning = true;
        ScanProgress = 0;
        StatusText = "Scanning ROM for dialogue strings...";
        AllEntries.Clear();
        FilteredEntries.Clear();

        var progress = new Progress<int>(p =>
        {
            ScanProgress = p;
            StatusText   = $"Scanning... {p}%";
        });

        try
        {
            var cts    = new CancellationTokenSource();
            var rom    = _rom;

            var entries = await Task.Run(
                () => DialogueScanner.ScanForDialogue(rom, progress, cts.Token));

            foreach (var e in entries)
                AllEntries.Add(e);

            ApplyFilter();

            // Also load dictionary
            var words = DialogueScanner.ScanDictionary(rom);
            foreach (var w in words)
                DictionaryWords.Add(w);

            StatusText = $"Found {AllEntries.Count} dialogue strings, {DictionaryWords.Count} dictionary words.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning   = false;
            ScanProgress = 100;
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(ModifiedCount));
        }
    }

    [RelayCommand(CanExecute = nameof(CanCommitEdit))]
    private void CommitEdit()
    {
        if (SelectedEntry == null || _rom == null) return;

        string newText = EditBuffer;
        byte[] encoded = TextDecoder.Encode(newText);

        if (encoded.Length > SelectedEntry.RawByteLength)
        {
            MessageBox.Show(
                $"Encoded text is {encoded.Length} bytes but original was {SelectedEntry.RawByteLength} bytes.\n\n" +
                $"Your new text is {encoded.Length - SelectedEntry.RawByteLength} bytes too long.\n\n" +
                "Please shorten the text, or use dictionary words (e.g. [DICT:...]) to compress.\n" +
                "Expanding dialogue beyond its original size requires pointer table editing (coming in a future update).",
                "Text Too Long",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Pad to original length with 0x00 if shorter (safe - 0x00 = string terminator)
        if (encoded.Length < SelectedEntry.RawByteLength)
        {
            var padded = new byte[SelectedEntry.RawByteLength];
            Array.Copy(encoded, padded, encoded.Length);
            encoded = padded;
        }

        _rom.WriteBytes(SelectedEntry.RomOffset, encoded);
        SelectedEntry.EditedText = newText;

        OnPropertyChanged(nameof(ModifiedCount));
        ApplyFilter();
        StatusText = $"Committed edit at offset {SelectedEntry.OffsetHex}.";
    }

    private bool CanCommitEdit() => SelectedEntry != null;

    [RelayCommand(CanExecute = nameof(CanCommitEdit))]
    private void RevertEntry()
    {
        if (SelectedEntry == null || _rom == null) return;

        // Re-decode from the live ROM bytes (in case of earlier writes)
        var result = TextDecoder.Decode(_rom, SelectedEntry.RomOffset);
        SelectedEntry.EditedText = result.Text;
        EditBuffer = result.Text;

        OnPropertyChanged(nameof(ModifiedCount));
        StatusText = $"Reverted entry at {SelectedEntry.OffsetHex}.";
    }

    [RelayCommand]
    private void ExportScript()
    {
        if (AllEntries.Count == 0) return;

        var dialog = new SaveFileDialog
        {
            Title      = "Export Script as Text File",
            Filter     = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName   = $"Lufia1_Script_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (dialog.ShowDialog() != true) return;

        var lines = AllEntries.Select(e =>
            $"[{e.Index:D4}] Offset: {e.OffsetHex}  SNES: {e.SnesAddress}  Bytes: {e.RawByteLength}\n" +
            e.DecodedText + "\n" +
            (e.IsModified ? $"--- EDITED ---\n{e.EditedText}\n" : "") +
            new string('-', 60));

        File.WriteAllLines(dialog.FileName, lines);
        StatusText = $"Script exported to {dialog.FileName}";
    }

    [RelayCommand]
    private void ExportDictionary()
    {
        if (DictionaryWords.Count == 0) return;

        var dialog = new SaveFileDialog
        {
            Title      = "Export Dictionary",
            Filter     = "Text files (*.txt)|*.txt",
            FileName   = $"Lufia1_Dictionary_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (dialog.ShowDialog() != true) return;

        var lines = DictionaryWords.Select(w => $"0x{w.Offset:X6}  (ptr: 0x{(w.Offset - Lufia1Constants.DictionaryBaseOffset):X4})  \"{w.Word}\"");
        File.WriteAllLines(dialog.FileName, lines);
        StatusText = $"Dictionary exported.";
    }

    // -------------------------------------------------------------------------
    // Selection / filtering
    // -------------------------------------------------------------------------

    partial void OnSelectedEntryChanged(DialogueEntry? value)
    {
        if (value != null)
        {
            EditBuffer = value.EditedText;
            StatusText = $"Entry #{value.Index}  |  {value.OffsetHex}  ({value.SnesAddress})  |  {value.RawByteLength} bytes";
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowModifiedOnlyChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredEntries.Clear();
        var source = AllEntries.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
            source = source.Where(e =>
                e.DecodedText.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                e.OffsetHex.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        if (ShowModifiedOnly)
            source = source.Where(e => e.IsModified);

        foreach (var e in source)
            FilteredEntries.Add(e);
    }
}
