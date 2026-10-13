using LufiaForge.Core;
using LufiaForge.Modules.Disassembler;
using LufiaForge.Modules.GameData;
using LufiaForge.Modules.MapEditor;
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

        // Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z): the app-wide history, unless a text box or a tab took the key first
        KeyDown += (_, e) =>
        {
            if (e.Handled || System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) return;
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            bool ctrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
            bool shift = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            if (!ctrl) return;
            if (e.Key == System.Windows.Input.Key.Z && !shift) { if (vm.UndoCommand.CanExecute(null)) vm.UndoCommand.Execute(null); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Y || e.Key == System.Windows.Input.Key.Z && shift) { if (vm.RedoCommand.CanExecute(null)) vm.RedoCommand.Execute(null); e.Handled = true; }
        };

        // Quietly check GitHub for a newer release once the window is up.
        Loaded += async (_, _) => await UpdateService.CheckAsync(silentIfNone: true, HasUnsavedChanges);

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
            textVm.SetRom(rom, vm);

        // Game Data editors (characters, items, shops, spells, monsters, settings)
        if (GameDataView.DataContext is GameDataViewModel gameVm)
            gameVm.SetRom(rom, vm);

        // Map Editor
        if (MapEditorView.DataContext is MapEditorViewModel mapVm)
            mapVm.SetRom(rom, vm);

        // Events
        if (EventsView.DataContext is LufiaForge.Modules.Events.EventsViewModel eventsVm)
            eventsVm.SetRom(rom, vm);

        // Cutscenes (stage editor; took the Tile Viewer's place)
        if (CutscenesView.DataContext is LufiaForge.Modules.Cutscenes.CutscenesViewModel cutVm)
            cutVm.SetRom(rom, vm);

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
        // how this ROM has been edited (read from the ROM itself, the same in every session)
        try { foreach (var (label, value) in RomEdits.Describe(rom)) items.Add(new(label, value)); }
        catch (System.Exception ex) { items.Add(new("Edits:", "couldn't be read: " + ex.Message)); }

        RomInfoItems.ItemsSource = items;
        try
        {
            string summary = RomEdits.Summary(rom);
            if (summary.Length > 0) vm.StatusDetail = summary;
        }
        catch { }
    }

    protected override void OnClosed(System.EventArgs e)
    {
        // Dispose the memory monitor bridge/timer
        if (MemoryMonitorView.DataContext is MemoryMonitorViewModel mmVm)
            mmVm.Dispose();
        base.OnClosed(e);
    }

    private bool HasUnsavedChanges() => ((MainViewModel)DataContext).RomBuffer?.IsDirty == true;

    private async void CheckUpdatesMenuItem_Click(object sender, RoutedEventArgs e) =>
        await UpdateService.CheckAsync(silentIfNone: false, HasUnsavedChanges);

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();
}
