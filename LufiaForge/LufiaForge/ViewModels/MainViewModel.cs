using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace LufiaForge.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRomLoaded))]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private RomBuffer? _romBuffer;

    [ObservableProperty] private string _statusMessage = "Ready. Start a project from your ROM, or open one, to begin.";
    [ObservableProperty] private string _statusDetail  = "";
    [ObservableProperty] private bool   _isLoading     = false;
    [ObservableProperty] private int    _selectedTabIndex = 0;

    public bool IsRomLoaded => RomBuffer != null;

    /// <summary>The welcome screen: shown until a ROM is opened (or it's closed to use BizHawk without one).</summary>
    [ObservableProperty] private bool _welcomeDismissed;
    public bool ShowWelcome => !IsRomLoaded && !WelcomeDismissed;
    partial void OnWelcomeDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowWelcome));

    [RelayCommand]
    private void DismissWelcome() => WelcomeDismissed = true;

    /// <summary>Every change written to the ROM, from any tab (Edit > Undo / Redo, Ctrl+Z / Ctrl+Y).</summary>
    public RomHistory History { get; } = new();

    public string UndoText => History.UndoSteps.Count > 0 ? "Undo: " + History.UndoSteps[^1].What : "Undo";
    public string RedoText => History.RedoSteps.Count > 0 ? "Redo: " + History.RedoSteps[^1].What : "Redo";
    /// <summary>The newest steps first (Edit > History).</summary>
    public List<HistoryEntry> HistoryEntries =>
        History.RedoSteps.Select(s => new HistoryEntry(s, false)).Concat(History.UndoSteps.Reverse().Select(s => new HistoryEntry(s, true))).Take(40).ToList();

    partial void OnRomBufferChanged(RomBuffer? value)
    {
        History.Attach(value);
        SaveRomCommand.NotifyCanExecuteChanged();
        SaveRomAsCommand.NotifyCanExecuteChanged();
        SaveProjectCommand.NotifyCanExecuteChanged();
        SaveProjectAsCommand.NotifyCanExecuteChanged();
        ApplyAllCommand.NotifyCanExecuteChanged();
    }

    private void HistoryChanged()
    {
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        OnPropertyChanged(nameof(HistoryEntries));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    public MainViewModel()
    {
        History.Changed += HistoryChanged;
        if (Application.Current != null) StartProjectSupport();
    }

    private bool CanUndo() => History.CanUndo;
    private bool CanRedo() => History.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var step = History.Undo();
        if (step == null) return;
        AfterHistoryMove("Undone: " + step.What);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var step = History.Redo();
        if (step == null) return;
        AfterHistoryMove("Redone: " + step.What);
    }

    /// <summary>Undoes (or redoes) every step up to the chosen one in Edit > History.</summary>
    [RelayCommand]
    private void GoToHistory(HistoryEntry? entry)
    {
        if (entry == null) return;
        int n = entry.IsDone ? History.UndoTo(entry.Step) : History.RedoTo(entry.Step);
        if (n > 0) AfterHistoryMove($"{(entry.IsDone ? "Undone" : "Redone")} {n} step{(n == 1 ? "" : "s")}, back to: {entry.Step.What}");
    }

    private void AfterHistoryMove(string what)
    {
        _pendingChanges.Add(what);
        ReloadModules();
        OnPropertyChanged(nameof(WindowTitle));
        StatusMessage = what + (Project != null ? " (Save Project keeps it)" : "");
    }

    /// <summary>Makes every tab load the ROM again (after its bytes were replaced, e.g. by an IPS patch).</summary>
    public void ReloadModules() => OnPropertyChanged(nameof(RomBuffer));

    public string WindowTitle => RomBuffer == null
        ? "Lufia Forge"
        : Project != null
            ? $"Lufia Forge - {Project.Name}{(TitleDirty ? " *" : "")}"
            : $"Lufia Forge - {Path.GetFileName(RomBuffer.FilePath)}";

    /// <summary>Whether the title shows "*": changed since the project was saved.</summary>
    private bool TitleDirty => Project != null && RomBuffer != null && (_savedRom == null || !RomBuffer.SameAs(_savedRom));

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsRomLoaded))]
    private void SaveRom()
    {
        if (RomBuffer == null) return;
        try
        {
            // a new date-stamped ROM next to the project each time; the ROM the project started from is never touched
            Flush(quiet: false);
            string copyPath = RomBuffer.SaveDatedCopy();
            StatusMessage = $"Exported: {Path.GetFileName(copyPath)}.";
            StatusDetail  = copyPath;
            Modules.Common.InfoDialog.Show("ROM exported", "✔ ROM exported",
                $"Everything in the project so far was written to a new dated ROM ({new FileInfo(copyPath).Length / 1024} KB) next to the project. " +
                "The project itself is kept by Save Project, not by exporting.\n\n" +
                "Load the new file in your emulator to play with the changes. Start from an in-game save rather than " +
                "an old savestate (savestates carry the old ROM contents).",
                path: copyPath, pathLabel: "Exported to");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export: {ex.Message}", "Export Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRomLoaded))]
    private void SaveRomAs()
    {
        if (RomBuffer == null) return;

        var dialog = new SaveFileDialog
        {
            Title      = "Export ROM As",
            Filter     = "SNES ROM (*.sfc)|*.sfc|All files (*.*)|*.*",
            FileName   = Path.GetFileNameWithoutExtension(RomBuffer.FilePath) + ".sfc",
            InitialDirectory = Path.GetDirectoryName(RomBuffer.FilePath)
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            Flush(quiet: false);
            RomBuffer.WriteCopy(dialog.FileName);
            StatusMessage = "ROM exported.";
            StatusDetail  = dialog.FileName;
            Modules.Common.InfoDialog.Show("ROM exported", "✔ ROM exported",
                "Everything in the project so far was written to the file below.", path: dialog.FileName, pathLabel: "Exported to");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export: {ex.Message}", "Export Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Closes the main window (which asks to save the project first).</summary>
    [RelayCommand]
    private void Exit() => Application.Current.MainWindow?.Close();

    // -------------------------------------------------------------------------
    // Called by child modules when they modify the ROM
    // -------------------------------------------------------------------------

    private readonly List<string> _pendingChanges = new();

    /// <summary>Edits written to the ROM in memory since the last save (shown when saving).</summary>
    public IReadOnlyList<string> PendingChanges => _pendingChanges;

    /// <param name="what">Short description of the change, listed in the save confirmation.</param>
    /// <returns>Whether the ROM really changed (writing the same bytes again isn't a change).</returns>
    public bool NotifyRomModified(string? what = null)
    {
        if (History.Record(what) == null && History.IsAttached) return false;
        if (!string.IsNullOrWhiteSpace(what)) _pendingChanges.Add(what);
        OnPropertyChanged(nameof(WindowTitle));
        StatusMessage = what == null
            ? "Changed (Save Project keeps it)."
            : $"Changed: {what} (Save Project keeps it)";
        return true;
    }

    /// <summary>Save the project now (the "Save project now" buttons in confirmations).</summary>
    public void SaveNow() => SaveProjectNow(quiet: false);
}

/// <summary>A line in Edit > History: a step that is in the ROM (click: undo back to before it) or undone (click: redo it).</summary>
public sealed record HistoryEntry(RomHistory.Step Step, bool IsDone)
{
    public string Text => (IsDone ? "↶ " : "↷ ") + Step.What + $"   ({Step.When:HH:mm:ss})";
}
