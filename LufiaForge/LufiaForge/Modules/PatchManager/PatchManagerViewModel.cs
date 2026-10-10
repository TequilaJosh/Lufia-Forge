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

    /// <summary>Fixes and features Lufia Forge adds with one click.</summary>
    public List<BuiltInPatch> BuiltInPatches { get; } = BuiltInPatch.All();

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
        RefreshBuiltIn();
    }

    // -------------------------------------------------------------------------
    // Built-in patches
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleBuiltIn(BuiltInPatch? patch)
    {
        if (_rom == null || patch == null) return;
        bool adding = !patch.IsApplied(_rom);
        if (adding && _rom.Length < Core.Maps.MapWriter.ExpandedSize &&
            MessageBox.Show($"\"{patch.Name}\" adds a small routine to the expanded part of the ROM. Expand the ROM to 2 MB (the game plays the same)?",
                "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { StatusText = "Not added: the ROM wasn't expanded."; return; }
        try
        {
            if (adding) patch.Apply(_rom); else patch.Remove!(_rom);
            _mainVm?.NotifyRomModified((adding ? "Added: " : "Removed: ") + patch.Name);
            StatusText = (adding ? "Added: " : "Removed: ") + patch.Name + ". Save the ROM to keep it.";
        }
        catch (Exception ex) { StatusText = "Not changed: " + ex.Message; }
        RefreshBuiltIn();
    }

    /// <summary>Re-reads which built-in patches the ROM has (other tabs can add them too).</summary>
    public void RefreshBuiltIn()
    {
        foreach (var p in BuiltInPatches) p.Refresh(_rom);
        RefreshSize();
    }

    // -------------------------------------------------------------------------
    // ROM size
    // -------------------------------------------------------------------------

    [ObservableProperty] private string _romSizeText = "";
    [ObservableProperty] private bool _canExpandTo4Mb;

    private void RefreshSize()
    {
        if (_rom == null) { RomSizeText = ""; CanExpandTo4Mb = false; return; }
        double mb = _rom.Length / 1048576.0;
        string free = "";
        if (_rom.Length >= Core.Maps.MapWriter.ExpandedSize)
        {
            var space = Core.Maps.ExpansionSpace.Load(_rom);
            long used = space.Entries.Sum(e => (long)e.Length);
            long room = _rom.Length - Core.Maps.ExpansionSpace.FirstUsable - Core.Maps.ExpansionSpace.TableSize;
            free = $" About {Math.Max(0, room - used) / 1024:N0} KB of the added space is still free.";
        }
        RomSizeText = $"This ROM is {mb:0.#} MB.{free} Lufia Forge grows it by itself when edits need room (1 MB → 2 MB → 4 MB); " +
                      "4 MB is the most a Lufia (LoROM) cartridge can address.";
        CanExpandTo4Mb = _rom.Length < Core.Maps.MapWriter.MaxSize;
    }

    [RelayCommand]
    private void ExpandTo4Mb()
    {
        if (_rom == null || _rom.Length >= Core.Maps.MapWriter.MaxSize) return;
        if (MessageBox.Show("Expand the ROM to 4 MB now? The game plays the same; the new space is used for maps, events, text, music and graphics you add.",
                "Expand to 4 MB", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _rom.Expand(Core.Maps.MapWriter.MaxSize);
        Core.Maps.ExpansionSpace.Load(_rom).Save();
        _rom.FixChecksum();
        _mainVm?.NotifyRomModified("ROM expanded to 4 MB");
        StatusText = "ROM expanded to 4 MB. Save the ROM to keep it.";
        RefreshBuiltIn();
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

            if (patched.Length != _rom.Length &&
                MessageBox.Show(
                    $"This patch changes the ROM size from {_rom.Length:N0} to {patched.Length:N0} bytes " +
                    $"({_rom.Length / 1024 / 1024.0:0.#} MB → {patched.Length / 1024 / 1024.0:0.#} MB). Patches that add new " +
                    "data (for example ones made by Lufia Forge from an edited ROM) do this. Continue?",
                    "ROM size changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

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
