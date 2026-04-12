using LufiaForge.Core;
using LufiaForge.Modules.Disassembler;
using LufiaForge.Modules.MemoryMonitor;
using LufiaForge.Modules.PatchManager;
using LufiaForge.Modules.TextEditor;
using LufiaForge.Modules.TileViewer;
using LufiaForge.ViewModels;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace LufiaForge;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = (MainViewModel)DataContext;
        vm.PropertyChanged += Vm_PropertyChanged;

        // Wire cross-module: Disassembler "Add to Watchlist" → Memory Monitor
        if (DisassemblerView.DataContext  is DisassemblerViewModel  disVm &&
            MemoryMonitorView.DataContext is MemoryMonitorViewModel  mmVm)
        {
            disVm.RequestAddWatch = mmVm.AddWatch;
        }
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.RomBuffer)) return;

        RefreshRomInfo();
        PropagateRomToModules();
    }

    /// <summary>Push the loaded RomBuffer into every module that needs it.</summary>
    private void PropagateRomToModules()
    {
        var vm  = (MainViewModel)DataContext;
        var rom = vm.RomBuffer;
        if (rom == null) return;

        // Text Editor
        if (TextEditorView.DataContext is TextEditorViewModel textVm)
            textVm.SetRom(rom);

        // Tile Viewer
        if (TileViewerView.DataContext is TileViewerViewModel tileVm)
            tileVm.SetRom(rom);

        // Patch Manager (also needs reference to MainViewModel for NotifyRomModified)
        if (PatchManagerView.DataContext is PatchManagerViewModel patchVm)
            patchVm.SetRom(rom, vm);

        // Disassembler
        if (DisassemblerView.DataContext is DisassemblerViewModel disVm)
            disVm.SetRom(rom);

        // Memory Monitor — auto-launches BizHawk with the ROM
        if (MemoryMonitorView.DataContext is MemoryMonitorViewModel mmVm)
            mmVm.SetRom(rom);
    }

    private void RefreshRomInfo()
    {
        var vm = (MainViewModel)DataContext;
        if (vm.RomBuffer == null) return;

        var rom   = vm.RomBuffer;
        int size  = rom.Length;
        string title = rom.ReadAscii(Lufia1Constants.SnesHeaderOffset, 21).TrimEnd();

        byte mappingByte = rom.ReadByte(Lufia1Constants.SnesMappingOffset);
        string mapping   = mappingByte == Lufia1Constants.LoRomMappingByte
            ? "LoROM (0x20)" : $"Unknown (0x{mappingByte:X2})";

        ushort checksum   = rom.ReadUInt16Le(Lufia1Constants.SnesChecksumOffset);
        ushort complement = rom.ReadUInt16Le(Lufia1Constants.SnesComplementOffset);

        var items = new List<KeyValuePair<string, string>>
        {
            new("Internal Title:",     title),
            new("File:",               System.IO.Path.GetFileName(rom.FilePath)),
            new("ROM Size:",           $"{size / 1024} KB  ({size:N0} bytes)"),
            new("Mapping Mode:",       mapping),
            new("Stored Checksum:",    $"0x{checksum:X4}"),
            new("Stored Complement:",  $"0x{complement:X4}"),
            new("Sum Check:",          (checksum + complement == 0xFFFF) ? "Valid (sums to 0xFFFF)" : "Invalid"),
            new("SNES Header Offset:", $"0x{Lufia1Constants.SnesHeaderOffset:X5}"),
            new("Dict Start:",         $"0x{Lufia1Constants.DictionaryStartOffset:X6}"),
            new("Dict End:",           $"0x{Lufia1Constants.DictionaryEndOffset:X6}"),
        };

        RomInfoItems.ItemsSource = items;
    }

    protected override void OnClosed(System.EventArgs e)
    {
        // Dispose the memory monitor bridge/timer
        if (MemoryMonitorView.DataContext is MemoryMonitorViewModel mmVm)
            mmVm.Dispose();
        base.OnClosed(e);
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Lufia Forge v1.0\n\n" +
            "A ROM hack toolkit for Lufia & The Fortress of Doom (SNES, US)\n\n" +
            "Phase 1: ROM loader, IPS patch apply/export, core infrastructure\n" +
            "Phase 2: Text editor with full encoding support, script export\n" +
            "Phase 3: Tile & sprite viewer (2bpp / 4bpp / 8bpp, ROM palette)\n" +
            "Phase 4: IPS Patch Manager UI\n\n" +
            "Text encoding research: Vegetaman (2010), Digisalt / AllOriginal.tbl (flobo 2011).",
            "About Lufia Forge",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
