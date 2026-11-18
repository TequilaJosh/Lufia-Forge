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

        // ctrl+z / ctrl+y (ctrl+shift+z) = the app-wide history, unless a text box or a tab took the key first
        // (also in sections popped out into their own windows, PopOutHost passes their keys on through here)
        System.Windows.Input.KeyEventHandler appKeys = (_, e) =>
        {
            if (e.Handled || System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) return;
            bool ctrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
            bool shift = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            if (!ctrl) return;
            // project keys work everywhere, also while typing
            System.Windows.Input.ICommand? project = e.Key switch
            {
                System.Windows.Input.Key.S => shift ? vm.SaveProjectAsCommand : vm.SaveProjectCommand,
                System.Windows.Input.Key.O when !shift => vm.OpenProjectCommand,
                System.Windows.Input.Key.N when !shift => vm.NewProjectCommand,
                _ => null,
            };
            if (project != null) { if (project.CanExecute(null)) project.Execute(null); e.Handled = true; return; }
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            // a map editor's edits not yet applied come first (the last one worked in, while it is on screen)
            if (e.Key == System.Windows.Input.Key.Z && !shift) { if (!Modules.Common.LocalUndoTarget.TryUndo() && vm.UndoCommand.CanExecute(null)) vm.UndoCommand.Execute(null); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Y || e.Key == System.Windows.Input.Key.Z && shift) { if (!Modules.Common.LocalUndoTarget.TryRedo() && vm.RedoCommand.CanExecute(null)) vm.RedoCommand.Execute(null); e.Handled = true; }
        };
        KeyDown += appKeys;

        // quietly check github for a newer release once the window is up
        Loaded += async (_, _) => await UpdateService.CheckAsync(silentIfNone: true, HasUnsavedChanges, vm.SaveBeforeRestartAsync);

        // tabs whose edits get written without asking (saving the project, apply to rom in the top bar...)
        if (MapEditorView.DataContext is IPendingEdits mapEdit) vm.Register(mapEdit);
        if (GameDataView.DataContext is IPendingEdits gameData) vm.Register(gameData);
        if (TextEditorView.DataContext is IPendingEdits text) vm.Register(text);

        // bizhawk plays the project as it is now, a fresh copy of its rom gets written when it's launched
        if (MemoryMonitorView.DataContext is MemoryMonitorViewModel monitor)
            monitor.PrepareRom = vm.WritePlayRom;

        // disassembler "Add to Watchlist" -> memory monitor
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

    // hands the loaded RomBuffer to every module that needs it
    private void PropagateRomToModules()
    {
        var vm  = (MainViewModel)DataContext;
        var rom = vm.RomBuffer;
        if (rom == null) return;

        // lufia 2 gets its own tab. the lufia 1 editors are hidden and never see the rom (they'd read and write
        // lufia 1 places in it). the generic tabs (memory monitor, disassembler, rom info) stay
        bool l2 = rom.IsLufia2;
        foreach (var tab in new[] { MapTab, EventsTab, CutscenesTab, TextTab, GameDataTab })
            tab.Visibility = l2 ? Visibility.Collapsed : Visibility.Visible;
        Lufia2Tab.Visibility = l2 ? Visibility.Visible : Visibility.Collapsed;
        if (l2)
        {
            if (Lufia2View.DataContext is LufiaForge.Modules.Lufia2.Lufia2ViewModel l2Vm) l2Vm.SetRom(rom, vm);
            Lufia2Tab.IsSelected = true;
            if (DisassemblerView.DataContext is DisassemblerViewModel disVm2) disVm2.SetRom(rom);
            if (MemoryMonitorView.DataContext is MemoryMonitorViewModel mmVm2) mmVm2.SetRom(rom);
            if (PatchManagerView.DataContext is PatchManagerViewModel patchVm2) patchVm2.SetRom(rom, vm);   // ips only
            return;
        }
        if (Lufia2Tab.IsSelected) MapTab.IsSelected = true;

        if (TextEditorView.DataContext is TextEditorViewModel textVm)
            textVm.SetRom(rom, vm);

        // game data editors (characters, items, shops, spells, monsters, settings)
        if (GameDataView.DataContext is GameDataViewModel gameVm)
            gameVm.SetRom(rom, vm);

        if (MapEditorView.DataContext is MapEditorViewModel mapVm)
            mapVm.SetRom(rom, vm);

        if (EventsView.DataContext is LufiaForge.Modules.Events.EventsViewModel eventsVm)
            eventsVm.SetRom(rom, vm);

        // cutscenes (the stage editor, took the tile viewer's place)
        if (CutscenesView.DataContext is LufiaForge.Modules.Cutscenes.CutscenesViewModel cutVm)
            cutVm.SetRom(rom, vm);

        // the patch manager needs the MainViewModel too, for NotifyRomModified
        if (PatchManagerView.DataContext is PatchManagerViewModel patchVm)
            patchVm.SetRom(rom, vm);

        if (DisassemblerView.DataContext is DisassemblerViewModel disVm)
            disVm.SetRom(rom);

        // memory monitor, launches bizhawk with the rom by itself
        if (MemoryMonitorView.DataContext is MemoryMonitorViewModel mmVm)
            mmVm.SetRom(rom);
    }

    private void RefreshRomInfo()
    {
        var vm = (MainViewModel)DataContext;
        if (vm.RomBuffer == null) return;

        var rom   = vm.RomBuffer;
        int size  = rom.Length;
        GameStatusText.Text = $"Lufia Forge  |  {GameDetect.Name(rom.Game)} (US SNES)";
        string title = rom.ReadAscii(Lufia1Constants.SnesHeaderOffset, 21).TrimEnd();

        byte mappingByte = rom.ReadByte(Lufia1Constants.SnesMappingOffset);
        string mapping   = mappingByte == Lufia1Constants.LoRomMappingByte ? "LoROM (0x20)"
                         : mappingByte == 0x30 ? "LoROM, FastROM (0x30)" : $"Unknown (0x{mappingByte:X2})";

        ushort checksum   = rom.ReadUInt16Le(Lufia1Constants.SnesChecksumOffset);
        ushort complement = rom.ReadUInt16Le(Lufia1Constants.SnesComplementOffset);

        var items = new List<KeyValuePair<string, string>>
        {
            new("Game:",               GameDetect.Name(rom.Game)),
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
        if (rom.IsLufia2) { items.RemoveAll(i => i.Key.StartsWith("Dict")); RomInfoItems.ItemsSource = items; return; }
        // how this rom has been edited (read from the rom itself, so it's the same in every session)
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

    // closing: asks to save the project if something isn't saved (cancel keeps the app open)
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!((MainViewModel)DataContext).ConfirmLeaveProject()) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(System.EventArgs e)
    {
        if (MemoryMonitorView.DataContext is MemoryMonitorViewModel mmVm)
            mmVm.Dispose();
        base.OnClosed(e);
    }

    private bool HasUnsavedChanges() => ((MainViewModel)DataContext).HasUnsavedProjectChanges();

    private async void CheckUpdatesMenuItem_Click(object sender, RoutedEventArgs e) =>
        await UpdateService.CheckAsync(silentIfNone: false, HasUnsavedChanges, ((MainViewModel)DataContext).SaveBeforeRestartAsync);

    private void ClearStoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        if (vm.RomBuffer is not { } rom) return;
        vm.WritePendingEdits();   // every tab reads the rom again after, so what isn't in it yet goes in first
        var dialog = new Modules.Common.ClearStoryWindow(rom) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var before = rom.ToArray();
        try
        {
            string what;
            if (rom.IsLufia2)
            {
                var cleared = Core.Lufia2.L2StoryClear.Clear(rom, dialog.StartMap, dialog.StartArrival);
                what = $"Story cleared: {cleared.Events} events, {cleared.People} characters, {cleared.Treasure} chests; a new game starts on map {dialog.StartMap:X2}";
            }
            else
            {
                var cleared = Core.Maps.StoryClear.Clear(rom, dialog.StartMap, dialog.StartArrival);
                what = $"Story cleared: {cleared.Events} events, {cleared.People} characters, {cleared.Treasure} treasure spots; a new game starts on map {dialog.StartMap:X2}";
            }
            vm.NotifyRomModified(what);
            vm.ReloadModules();
        }
        catch (Exception ex)
        {
            rom.ReplaceAll(before);
            MessageBox.Show(this, "The story couldn't be cleared (nothing was changed): " + ex.Message, "Start from a clean slate", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Modules.Common.AppSettingsWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        // turned on with a project open, so what it has in the top 2 MB moves now (like when a project opens)
        if (dialog.Snes9xTurnedOn) ((MainViewModel)DataContext).MoveOutOfTop2MB();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();
}
