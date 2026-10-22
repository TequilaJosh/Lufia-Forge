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
    private static readonly string[] GroupOrder = { "Community", "Battle", "Magic", "Items", "Maps", "Events", "Testing", "Other" };
    /// <summary>The patches grouped for the cards (in this order).</summary>
    public List<PatchGroup> PatchGroups => GroupOrder.Concat(BuiltInPatches.Select(p => p.Category).Where(c => !GroupOrder.Contains(c)).Distinct())   // (a category not listed still shows, at the end)
        .Select(c => new PatchGroup(c == "Community" ? "Community patches (by fans, built in)" : c, BuiltInPatches.Where(p => p.Category == c).ToList())).Where(g => g.Patches.Count > 0).ToList();

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
        bool adding = patch.NeedsUpdate?.Invoke(_rom) == true || !patch.IsApplied(_rom);
        if (adding && patch.ConfirmText != null &&
            MessageBox.Show(patch.ConfirmText, patch.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (adding && patch.NeedsExpandedRom && _rom.Length < Core.Maps.MapWriter.ExpandedSize &&
            MessageBox.Show($"\"{patch.Name}\" adds a small routine to the expanded part of the ROM. Expand the ROM to 2 MB (the game plays the same)?",
                "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { StatusText = "Not added: the ROM wasn't expanded."; return; }
        try
        {
            if (adding) patch.Apply(_rom); else patch.Remove!(_rom);
            _mainVm?.NotifyRomModified((adding ? "Added: " : "Removed: ") + patch.Name);
            if (patch.ReloadsAll) _mainVm?.ReloadModules();
            StatusText = (adding ? "Added: " : "Removed: ") + patch.Name + ". Save Project keeps it.";
        }
        catch (Exception ex) { StatusText = "Not changed: " + ex.Message; }
        RefreshBuiltIn();
    }

    /// <summary>Re-reads which built-in patches the ROM has (other tabs can add them too).</summary>
    public void RefreshBuiltIn()
    {
        foreach (var p in BuiltInPatches) p.Refresh(_rom);
        RefreshSize();
        RefreshFont();
    }

    // -------------------------------------------------------------------------
    // Lufia II font
    // -------------------------------------------------------------------------

    [ObservableProperty] private string _fontText = "";
    [ObservableProperty] private bool _smallFontToo = true;
    [ObservableProperty] private bool _canRestoreFont;

    private void RefreshFont()
    {
        CanRestoreFont = _rom != null && FontPatch.IsApplied(_rom);
        bool restored = _rom != null && CommunityPatches.RestoredApplied(_rom);
        FontText = _rom == null ? "" : CanRestoreFont
            ? "This ROM uses Lufia II's font. The font it had before is kept in the added space and can be put back." +
              (restored ? " (Restored's own font was already close to Lufia II's, so only about 40 letters changed, by a pixel or two.)" : "")
            : restored
            ? "Restored's font: already close to Lufia II's (26 letters are identical). Copying in Lufia II's from your Lufia II ROM changes about 40 more letters slightly, and its thinner small font for names and menu titles."
            : "Lufia 1's own font. Lufia II's dialogue font (and its thinner small font for names and menu titles) can be copied in from your Lufia II ROM (.sfc, .smc or a .zip).";
    }

    [RelayCommand]
    private void UseLufia2Font()
    {
        if (_rom == null) return;
        var dialog = new OpenFileDialog
        {
            Title  = "Your Lufia II ROM",
            Filter = "Lufia II ROM (*.sfc;*.smc;*.zip)|*.sfc;*.smc;*.zip|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        if (_rom.Length < Core.Maps.MapWriter.ExpandedSize && !FontPatch.IsApplied(_rom) &&
            MessageBox.Show("Lufia 1's own font is kept in the expanded part of the ROM so it can be put back. Expand the ROM to 2 MB (the game plays the same)?",
                "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { StatusText = "Font not changed: the ROM wasn't expanded."; return; }
        try
        {
            var l2 = FontPatch.ReadLufia2(dialog.FileName);
            int n = FontPatch.Apply(_rom, l2, SmallFontToo);
            _mainVm?.NotifyRomModified("Lufia II font");
            StatusText = $"Lufia II's font copied in ({n} letters changed). Save Project keeps it.";
        }
        catch (Exception ex) { StatusText = "Font not changed: " + ex.Message; }
        RefreshBuiltIn();
    }

    [RelayCommand]
    private void RestoreFont()
    {
        if (_rom == null || !FontPatch.IsApplied(_rom)) return;
        FontPatch.Restore(_rom);
        _mainVm?.NotifyRomModified("Lufia 1 font restored");
        StatusText = "Lufia 1's own font is back. Save Project keeps it.";
        RefreshBuiltIn();
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
        StatusText = "ROM expanded to 4 MB. Save Project keeps it.";
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

            // data Lufia Forge keeps in the added space (moved maps, scripts, patch routines, its space table):
            // a patch written for another ROM can overwrite or cut it off, leaving pointers to garbage
            if (_rom.Length >= Core.Maps.MapWriter.ExpandedSize)
            {
                var space = Core.Maps.ExpansionSpace.Load(_rom);
                var blocks = space.Entries.Where(b => b.Kind != Core.Maps.ExpansionSpace.Kind.Unknown)
                    .Append(new Core.Maps.ExpansionSpace.Entry(Core.Maps.ExpansionSpace.TableStart, Core.Maps.ExpansionSpace.TableSize, Core.Maps.ExpansionSpace.Kind.Unknown, 0xFFFF));
                int hit = 0;
                foreach (var b in blocks)
                {
                    if (b.End > patched.Length) { hit++; continue; }
                    for (int i = b.Start; i < b.End; i++) if (patched[i] != _rom.ReadByte(i)) { hit++; break; }
                }
                if (hit > 0 && MessageBox.Show(
                        $"This patch overwrites (or cuts off) {hit} block(s) of data Lufia Forge stored in the expanded part of this ROM " +
                        "(moved maps, events, new spells/monsters/sprites, patch routines or its space table). The game would read garbage there.\n\n" +
                        "Apply it anyway? (Usually: apply the patch to a clean ROM first, then make your Lufia Forge edits.)",
                        "Patch overwrites Lufia Forge data", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
            }

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
            // every tab reloads from the patched bytes (open editors still held the old maps, names, offsets)
            _mainVm?.ReloadModules();

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

public sealed record PatchGroup(string Title, List<BuiltInPatch> Patches);
