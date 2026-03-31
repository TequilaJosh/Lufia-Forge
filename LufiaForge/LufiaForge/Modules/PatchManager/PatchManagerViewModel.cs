using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.ViewModels;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace LufiaForge.Modules.PatchManager;

public partial class PatchManagerViewModel : ObservableObject
{
    private RomBuffer?    _rom;
    private MainViewModel? _mainVm;
    private string?       _loadedPatchPath;

    // -------------------------------------------------------------------------
    // Collections and properties
    // -------------------------------------------------------------------------

    public ObservableCollection<IpsPatchRecord> PatchRecords { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecords))]
    private string _loadedPatchName = "(no patch loaded)";

    [ObservableProperty] private string _statusText  = "Load a ROM, then load an IPS patch file.";
    [ObservableProperty] private string _summaryText = "No records loaded.";

    public bool HasRecords => PatchRecords.Count > 0;

    // -------------------------------------------------------------------------
    // Init
    // -------------------------------------------------------------------------

    public void SetRom(RomBuffer rom, MainViewModel mainVm)
    {
        _rom    = rom;
        _mainVm = mainVm;
        StatusText = "ROM loaded. Load an IPS patch file or export a patch from your current edits.";
    }

    // -------------------------------------------------------------------------
    // Load Patch
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void LoadPatch()
    {
        var dialog = new OpenFileDialog
        {
            Title  = "Load IPS Patch File",
            Filter = "IPS patch files (*.ips)|*.ips|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        _loadedPatchPath = dialog.FileName;
        LoadedPatchName  = Path.GetFileName(_loadedPatchPath);

        try
        {
            ParsePatchRecords(_loadedPatchPath);
            StatusText = $"Loaded: {LoadedPatchName}  |  {PatchRecords.Count} records";
        }
        catch (Exception ex)
        {
            PatchRecords.Clear();
            SummaryText = "Parse error.";
            StatusText  = $"Error reading patch: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------------
    // Apply Patch
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void ApplyPatch()
    {
        if (_rom == null)
        {
            MessageBox.Show("No ROM is loaded.", "Apply Patch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_loadedPatchPath == null)
        {
            MessageBox.Show("No IPS patch file is loaded. Use 'Load IPS Patch...' first.",
                "Apply Patch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            byte[] patched = IpsHandler.ApplyPatch(_rom, _loadedPatchPath);

            if (patched.Length != _rom.Length)
            {
                MessageBox.Show(
                    $"This patch would change the ROM size from {_rom.Length:N0} to {patched.Length:N0} bytes.\n\n" +
                    "ROM expansion is not supported in this version. The patch cannot be applied.",
                    "Size Mismatch",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var confirm = MessageBox.Show(
                $"Apply \"{LoadedPatchName}\" to the ROM?\n\n" +
                $"This will modify {PatchRecords.Count} region(s) in memory.\n" +
                "The ROM will be marked as modified (unsaved).",
                "Confirm Apply Patch",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            _rom.ReplaceAll(patched);
            _mainVm?.NotifyRomModified();

            StatusText = $"Patch applied: {LoadedPatchName}  |  {PatchRecords.Count} records";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to apply patch:\n\n{ex.Message}",
                "Patch Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"Apply failed: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------------
    // Export Patch
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void ExportPatch()
    {
        if (_rom == null)
        {
            MessageBox.Show("No ROM is loaded.", "Export Patch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Step 1: Select the original (unmodified) ROM to diff against
        var openDialog = new OpenFileDialog
        {
            Title  = "Select Original (Unmodified) Lufia 1 ROM",
            Filter = "SNES ROM files (*.sfc;*.smc)|*.sfc;*.smc|All files (*.*)|*.*",
            FileName = Path.GetFileName(_rom.FilePath)
        };

        if (openDialog.ShowDialog() != true) return;

        // Step 2: Choose output path
        var saveDialog = new SaveFileDialog
        {
            Title            = "Save IPS Patch",
            Filter           = "IPS patch files (*.ips)|*.ips",
            FileName         = $"Lufia1_patch_{DateTime.Now:yyyyMMdd_HHmmss}.ips",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (saveDialog.ShowDialog() != true) return;

        try
        {
            IpsHandler.ExportPatch(_rom, openDialog.FileName, saveDialog.FileName);

            string name = Path.GetFileName(saveDialog.FileName);
            StatusText = $"Patch exported: {name}";

            MessageBox.Show($"IPS patch saved:\n{saveDialog.FileName}",
                "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export patch:\n\n{ex.Message}",
                "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -------------------------------------------------------------------------
    // Parse patch records for display
    // -------------------------------------------------------------------------

    private void ParsePatchRecords(string patchPath)
    {
        PatchRecords.Clear();

        byte[] data = File.ReadAllBytes(patchPath);

        if (data.Length < 5 ||
            data[0] != 'P' || data[1] != 'A' ||
            data[2] != 'T' || data[3] != 'C' || data[4] != 'H')
            throw new InvalidDataException("Not a valid IPS file (missing PATCH header).");

        int pos        = 5;
        int totalBytes = 0;

        while (pos + 3 <= data.Length)
        {
            if (data[pos] == 'E' && data[pos + 1] == 'O' && data[pos + 2] == 'F') break;
            if (pos + 5 > data.Length) break;

            int offset = (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2];
            pos += 3;

            int size = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (size == 0)
            {
                if (pos + 3 > data.Length) break;
                int runLen  = (data[pos] << 8) | data[pos + 1];
                byte fill   = data[pos + 2];
                pos += 3;

                PatchRecords.Add(new IpsPatchRecord
                {
                    Offset  = offset,
                    Size    = runLen,
                    IsRle   = true,
                    RleFill = fill
                });
                totalBytes += runLen;
            }
            else
            {
                if (pos + size > data.Length) break;
                pos += size;

                PatchRecords.Add(new IpsPatchRecord
                {
                    Offset = offset,
                    Size   = size,
                    IsRle  = false
                });
                totalBytes += size;
            }
        }

        SummaryText = $"{PatchRecords.Count} records  |  {totalBytes:N0} bytes affected  |  " +
                      $"{new FileInfo(patchPath).Length:N0} byte patch file";

        OnPropertyChanged(nameof(HasRecords));
    }
}
