using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace LufiaForge.ViewModels;

public partial class MainViewModel : ObservableObject
{
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

    // the welcome screen, shown until a rom is opened (or it gets closed to use bizhawk without one)
    [ObservableProperty] private bool _welcomeDismissed;
    public bool ShowWelcome => !IsRomLoaded && !WelcomeDismissed;
    partial void OnWelcomeDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowWelcome));

    [RelayCommand]
    private void DismissWelcome() => WelcomeDismissed = true;

    // every change written to the rom, from any tab (edit > undo / redo, ctrl+z / ctrl+y)
    public RomHistory History { get; } = new();

    public string UndoText => History.UndoSteps.Count > 0 ? "Undo: " + History.UndoSteps[^1].What : "Undo";
    public string RedoText => History.RedoSteps.Count > 0 ? "Redo: " + History.RedoSteps[^1].What : "Redo";
    // newest steps first (edit > history)
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
        NewProjectFromThisCommand.NotifyCanExecuteChanged();
        MergeProjectCommand.NotifyCanExecuteChanged();
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
        Instance = this;
    }

    // the main window's view model (the app's error handlers keep a copy of the project through it)
    public static MainViewModel? Instance { get; private set; }

    private bool CanUndo() => History.CanUndo;
    private bool CanRedo() => History.CanRedo;

    // leaving a tab writes its edits (and any other tab's) into the rom so the next tab reads them. a tab or page that
    // gets shown reads the rom again if RomBuffer.Version moved since it last read it
    partial void OnSelectedTabIndexChanged(int oldValue, int newValue) { if (RomBuffer != null) Flush(quiet: true); }

    // writes every tab's edits that aren't in the rom yet (quietly, problems go to the status bar). for a page switch
    // inside a tab, before something that reloads the tabs (a patch), that kind of thing
    public void WritePendingEdits() { if (RomBuffer != null) Flush(quiet: true); }

    // writes the tabs' edits that aren't in the rom yet, so a move in the history starts from everything the user did
    // (otherwise reloading the tabs after drops them, or writes them over the undone rom). returns whether that added
    // a step
    private bool WritePendingBeforeHistory() => WritePendingBeforeHistory("Go on", out _);

    // same as above. go is false when a tab's edits couldn't be written and the user keeps them (the history move
    // reloads every tab, which drops edits that aren't in the rom, they used to get lost without a word)
    private bool WritePendingBeforeHistory(string action, out bool go)
    {
        int before = History.UndoSteps.Count;
        bool ok = Flush(quiet: true);
        go = ok || !HasPendingEdits || MessageBox.Show(
            $"Some edits couldn't be written into the ROM ({StatusDetail}).\n\n{action} anyway? Those edits are lost then.",
            "Lufia Forge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        return History.UndoSteps.Count != before;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        WritePendingBeforeHistory("Undo", out bool go);   // the edits just made are what undo takes back first
        if (!go) return;
        var step = History.Undo();
        if (step == null) return;
        AfterHistoryMove("Undone: " + step.What);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (WritePendingBeforeHistory("Redo", out bool go)) { StatusMessage = "Your latest edits were written first, so there is nothing to redo after them."; return; }
        if (!go) return;
        var step = History.Redo();
        if (step == null) return;
        AfterHistoryMove("Redone: " + step.What);
    }

    // undoes (or redoes) every step up to the one picked in edit > history
    [RelayCommand]
    private void GoToHistory(HistoryEntry? entry)
    {
        if (entry == null) return;
        if (WritePendingBeforeHistory("Move in the history", out bool go)) { StatusMessage = "Your latest edits were written first; pick the step again in Edit > History."; return; }
        if (!go) return;
        int stepCount = entry.IsDone ? History.UndoTo(entry.Step) : History.RedoTo(entry.Step);
        if (stepCount > 0) AfterHistoryMove($"{(entry.IsDone ? "Undone" : "Redone")} {stepCount} step{(stepCount == 1 ? "" : "s")}, back to: {entry.Step.What}");
    }

    private void AfterHistoryMove(string what)
    {
        _pendingChanges.Add(what);
        ReloadModules();
        OnPropertyChanged(nameof(WindowTitle));
        StatusMessage = what + (Project != null ? " (Save Project keeps it)" : "");
    }

    // makes every tab load the rom again (after its bytes got replaced, by an ips patch say)
    public void ReloadModules() => OnPropertyChanged(nameof(RomBuffer));

    public string WindowTitle => RomBuffer == null
        ? "Lufia Forge"
        : Project != null
            ? $"Lufia Forge - {Project.Name}{(TitleDirty ? " *" : "")}"
            : $"Lufia Forge - {Path.GetFileName(RomBuffer.FilePath)}";

    // whether the title shows "*", changed since the project was saved
    private bool TitleDirty => Project != null && RomBuffer != null && (Project.NamesChanged || _savedRom == null || !RomBuffer.SameAs(_savedRom));

    // commands

    [RelayCommand(CanExecute = nameof(IsRomLoaded))]
    private void SaveRom()
    {
        if (RomBuffer == null) return;
        try
        {
            // a new date-stamped rom next to the project each time, the rom the project started from never gets touched
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

    // closes the main window (which asks to save the project first)
    [RelayCommand]
    private void Exit() => Application.Current.MainWindow?.Close();

    // the modules call these when they change the rom

    private readonly List<string> _pendingChanges = new();

    // edits written to the rom in memory since the last save (shown when saving)
    public IReadOnlyList<string> PendingChanges => _pendingChanges;

    // what = short description of the change, listed in the save confirmation.
    // returns whether the rom really changed (writing the same bytes again isn't a change)
    public bool NotifyRomModified(string? what = null)
    {
        if (History.Record(what) == null && History.IsAttached) return false;
        // every change gets a line in the change log, even one with no description. merge tells from the logs which
        // version two projects share, and a change missing there made it pick the wrong one (and revert it)
        _pendingChanges.Add(string.IsNullOrWhiteSpace(what) ? "A change to the ROM (not described)" : what);
        OnPropertyChanged(nameof(WindowTitle));
        StatusMessage = what == null
            ? "Changed (Save Project keeps it)."
            : $"Changed: {what} (Save Project keeps it)";
        return true;
    }

    // saves the project now (the "Save project now" buttons in confirmations)
    public void SaveNow() => SaveProjectNow(quiet: false);
}

// a line in edit > history: a step that's in the rom (click = undo back to before it) or undone (click = redo it)
public sealed record HistoryEntry(RomHistory.Step Step, bool IsDone)
{
    public string Text => (IsDone ? "↶ " : "↷ ") + Step.What + $"   ({Step.When:HH:mm:ss})";
}
