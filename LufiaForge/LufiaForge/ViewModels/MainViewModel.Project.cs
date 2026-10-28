using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LufiaForge.ViewModels;

/// <summary>
/// Projects: picking a ROM starts a named project (<see cref="LufiaProject"/>); edits go to the ROM in memory and are
/// kept by saving the project (Ctrl+S, every 5 minutes on its own, and when closing after a question). ROM files are
/// only written when one is needed: Export ROM, and the copy BizHawk plays.
/// </summary>
public partial class MainViewModel
{
    public static readonly TimeSpan AutosaveEvery = TimeSpan.FromMinutes(5);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(ProjectText))]
    private LufiaProject? _project;

    /// <summary>The ROM as the project was last saved (to tell whether anything changed since).</summary>
    private byte[]? _savedRom;

    public ObservableCollection<RecentProjects.Entry> RecentList { get; } = new();
    public bool HasRecent => RecentList.Count > 0;

    public string ProjectText => Project == null ? "" : $"{Project.Name}";

    private readonly List<IPendingEdits> _editors = new();

    /// <summary>A tab or panel whose edits are written by themselves (see <see cref="IPendingEdits"/>).</summary>
    public void Register(IPendingEdits editor) { if (!_editors.Contains(editor)) _editors.Add(editor); ApplyAllCommand.NotifyCanExecuteChanged(); }
    public void Unregister(IPendingEdits editor) => _editors.Remove(editor);

    /// <summary>Whether a tab has edits that aren't in the ROM yet.</summary>
    public bool HasPendingEdits => _editors.ToList().Any(e => { try { return e.HasPendingEdits; } catch { return false; } });

    /// <summary>Apply to ROM (top bar): every tab's edits are written to the ROM now.</summary>
    [RelayCommand(CanExecute = nameof(IsRomLoaded))]
    private void ApplyAll()
    {
        if (RomBuffer == null) return;
        bool had = HasPendingEdits;
        var errors = WriteAllPending();
        if (errors.Count > 0)
            MessageBox.Show("Some edits couldn't be written:\n\n" + string.Join("\n", errors), "Apply to ROM", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            StatusMessage = had ? "Every tab's edits are in the project now (Save Project keeps them)." : "Nothing to apply: every edit is already in the project.";
    }

    private static bool IsModal(Window w) =>
        (bool)(typeof(Window).GetField("_showingAsDialog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(w) ?? false);

    /// <summary>Writes every tab's pending edits; returns the problems.</summary>
    private List<string> WriteAllPending()
    {
        // a box being typed in hands its text over first (it would be put back to the old value otherwise)
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox tb)
            System.Windows.Data.BindingOperations.GetBindingExpression(tb, System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        var errors = new List<string>();
        foreach (var e in _editors.ToList())
        {
            try
            {
                if (!e.HasPendingEdits) continue;
                if (e.WritePendingEdits() is { } error) errors.Add(error);
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        return errors;
    }

    private DispatcherTimer? _autosave;

    /// <summary>When the user last typed, clicked or scrolled (the autosave waits for a pause).</summary>
    private static DateTime _lastInput = DateTime.MinValue;

    private void StartProjectSupport()
    {
        RefreshRecent();
        foreach (var ev in new RoutedEvent[] { UIElement.PreviewKeyDownEvent, UIElement.PreviewMouseDownEvent, UIElement.PreviewMouseWheelEvent, UIElement.PreviewMouseUpEvent })
            EventManager.RegisterClassHandler(typeof(Window), ev, new RoutedEventHandler((_, _) => _lastInput = DateTime.Now), true);
        _autosave = new DispatcherTimer { Interval = AutosaveEvery };
        _autosave.Tick += (_, _) => Autosave();
        _autosave.Start();
        Core.Maps.MapNames.ProjectNamesChanged += () =>
        {
            if (Project == null) return;
            Project.MapNamesChanged = true;
            OnPropertyChanged(nameof(WindowTitle));
        };
    }

    public void RefreshRecent()
    {
        RecentList.Clear();
        foreach (var e in RecentProjects.Load()) RecentList.Add(e);
        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>Unsaved: the ROM differs from the last save, or a tab has edits that aren't in the ROM yet.</summary>
    public bool HasUnsavedProjectChanges()
    {
        if (Project == null || RomBuffer == null) return false;
        if (HasPendingEdits || Project.MapNamesChanged) return true;
        return _savedRom == null || !RomBuffer.SameAs(_savedRom);
    }

    // ── new / open ──

    /// <summary>Picks a ROM and starts a project from it.</summary>
    [RelayCommand]
    private void NewProject()
    {
        if (!ConfirmLeaveProject()) return;
        var dialog = new OpenFileDialog
        {
            Title = "Pick the Lufia 1 or Lufia II ROM to start a project from",
            Filter = "SNES ROM files (*.sfc;*.smc)|*.sfc;*.smc|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        var report = RomLoader.Load(dialog.FileName);
        switch (report.Result)
        {
            case RomLoadResult.Success: break;
            case RomLoadResult.ChecksumMismatch:
                MessageBox.Show(report.Message, "Checksum Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case RomLoadResult.WrongGame:
                StatusMessage = "Load failed: Wrong game."; StatusDetail = report.Message;
                MessageBox.Show(report.Message, "Wrong ROM", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            default:
                StatusMessage = "Load failed."; StatusDetail = report.Message;
                MessageBox.Show(report.Message, "Error Loading ROM", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
        }
        var ask = new Modules.Common.NewProjectWindow(dialog.FileName) { Owner = Application.Current?.MainWindow };
        if (ask.ShowDialog() != true || report.Buffer == null) return;
        try
        {
            var rom = report.Buffer.ToArray();
            var project = LufiaProject.Create(ask.ProjectPath, ask.ProjectName, rom, dialog.FileName);
            CarryOverNotes(dialog.FileName, PlayRomPath(project));
            Use(project, rom);
            StatusMessage = $"Project \"{project.Name}\" started from {Path.GetFileName(dialog.FileName)}.";
            StatusDetail = project.Path;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The project couldn't be made: {ex.Message}", "New project", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenProject()
    {
        if (!ConfirmLeaveProject()) return;
        var dialog = new OpenFileDialog
        {
            Title = "Open a Lufia Forge project",
            Filter = $"Lufia Forge projects (*{LufiaProject.Extension})|*{LufiaProject.Extension}|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(LufiaProject.DefaultFolder) ? LufiaProject.DefaultFolder : null,
        };
        if (dialog.ShowDialog() == true) Open(dialog.FileName);
    }

    [RelayCommand]
    private void OpenRecent(RecentProjects.Entry? entry)
    {
        if (entry == null) return;
        if (!File.Exists(entry.Path))
        {
            if (MessageBox.Show($"The project \"{entry.Name}\" isn't at\n{entry.Path}\nany more. Take it off the list?", "Recent projects",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                RecentProjects.Remove(entry.Path);
                RefreshRecent();
            }
            return;
        }
        if (!ConfirmLeaveProject()) return;
        Open(entry.Path);
    }

    private void Open(string path)
    {
        try
        {
            var (project, rom) = LufiaProject.Open(path);
            Use(project, rom);
            StatusMessage = $"Project \"{project.Name}\" opened (saved {project.Saved:yyyy-MM-dd HH:mm})." + _openNote;
            StatusDetail = project.Path;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The project couldn't be opened: {ex.Message}", "Open project", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Use(LufiaProject project, byte[] rom)
    {
        _pendingChanges.Clear();
        Project = project;
        Core.Maps.MapNames.ProjectNames = project.MapNames;
        _savedRom = (byte[])rom.Clone();
        // what the project started from: another hack's data is known from it (RomIdentity, TableGuard)
        var buffer = new RomBuffer(rom, PlayRomPath(project)) { Original = project.BaseRom };
        TableGuard.Protect(buffer);
        RomBuffer = buffer;
        FixKnownBugs(buffer);
        RecentProjects.Add(project.Path, project.Name);
        RefreshRecent();
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>Mistakes in the game Lufia Forge puts right as a ROM is opened (each one Edit, Undo step): Lufia II's
    /// shrine maps CF and F1, drawn wrong in the US game (<see cref="Core.Lufia2.L2ShrineFix"/>).</summary>
    private string _openNote = "";

    private void FixKnownBugs(RomBuffer rom)
    {
        _openNote = "";
        if (!Core.Lufia2.L2ShrineFix.Needed(rom)) return;
        try
        {
            Core.Lufia2.L2ShrineFix.Apply(rom);
            NotifyRomModified("Lufia II: shrine maps CF and F1 fixed as in the European release (block set 17 and tile set 11; maps E7-EB keep their look)");
            OnPropertyChanged(nameof(RomBuffer));   // the pages read the fixed ROM
            _openNote = " Shrine maps CF and F1, garbled in the US game, were fixed as in the European release (Edit, Undo takes it back).";
        }
        catch (Exception ex) { _openNote = " Lufia II's shrine maps CF and F1 couldn't be fixed: " + ex.Message; }
    }

    /// <summary>The ROM file next to the project (what Export ROM and BizHawk use): "&lt;project&gt;.sfc".</summary>
    private static string PlayRomPath(LufiaProject p) => Path.ChangeExtension(p.Path, ".sfc");

    /// <summary>Bookmarks and story flag names kept next to the ROM go with the project.</summary>
    private static void CarryOverNotes(string romPath, string projectRom)
    {
        foreach (var ext in new[] { ".lfbookmarks.json", ".lfflags.json" })
        {
            try
            {
                string from = Path.ChangeExtension(romPath, null) + ext, to = Path.ChangeExtension(projectRom, null) + ext;
                if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
            }
            catch { }
        }
    }

    /// <summary>Writes the project's ROM as it is now next to the project (for BizHawk) and returns its path.</summary>
    public string? WritePlayRom()
    {
        if (RomBuffer == null) return null;
        try
        {
            Flush(quiet: true);
            RomBuffer.WriteCopy(RomBuffer.FilePath);
            return RomBuffer.FilePath;
        }
        catch (Exception ex)
        {
            StatusMessage = "The ROM for BizHawk couldn't be written: " + ex.Message;
            return File.Exists(RomBuffer.FilePath) ? RomBuffer.FilePath : null;
        }
    }

    // ── save ──

    [RelayCommand(CanExecute = nameof(IsRomLoaded))]
    private void SaveProject() => SaveProjectNow(quiet: false);

    [RelayCommand(CanExecute = nameof(IsRomLoaded))]
    private void SaveProjectAs()
    {
        if (Project == null || RomBuffer == null) return;
        var d = new SaveFileDialog
        {
            Title = "Save project as",
            Filter = $"Lufia Forge projects (*{LufiaProject.Extension})|*{LufiaProject.Extension}",
            FileName = Path.GetFileName(Project.Path),
            InitialDirectory = Path.GetDirectoryName(Project.Path),
        };
        if (d.ShowDialog() != true) return;
        Flush(quiet: false);
        try
        {
            var rom = RomBuffer.ToArray();
            Project.Name = Path.GetFileNameWithoutExtension(d.FileName);
            Project.SaveAs(d.FileName, rom);
            _savedRom = rom;
            RomBuffer.SetFilePath(PlayRomPath(Project));
            RecentProjects.Add(Project.Path, Project.Name);
            RefreshRecent();
            OnPropertyChanged(nameof(Project));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ProjectText));
            StatusMessage = $"Project saved as \"{Project.Name}\".";
            StatusDetail = Project.Path;
        }
        catch (Exception ex) { MessageBox.Show($"The project couldn't be saved: {ex.Message}", "Save project", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>Writes the tabs' pending edits to the ROM; with quiet, problems only go to the status bar.</summary>
    private bool Flush(bool quiet)
    {
        var errors = WriteAllPending();
        if (errors.Count == 0) return true;
        string msg = "Some edits couldn't be saved into the project yet: " + string.Join("; ", errors);
        if (quiet) StatusDetail = msg;
        else MessageBox.Show(msg + "\n\nThe rest is saved; those edits stay in their tab.", "Save project", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>Saves the project; false when it couldn't be.</summary>
    public bool SaveProjectNow(bool quiet)
    {
        if (Project == null || RomBuffer == null) return true;
        Flush(quiet);
        try
        {
            var rom = RomBuffer.ToArray();
            Project.Changes.AddRange(_pendingChanges.Select(c => $"{DateTime.Now:yyyy-MM-dd HH:mm}  {c}"));
            _pendingChanges.Clear();
            Project.Save(rom);
            _savedRom = rom;
            OnPropertyChanged(nameof(WindowTitle));
            StatusMessage = quiet ? $"Project saved on its own at {DateTime.Now:HH:mm}." : $"Project \"{Project.Name}\" saved.";
            StatusDetail = Project.Path;
            return true;
        }
        catch (Exception ex)
        {
            if (quiet) StatusMessage = "The project couldn't be saved on its own: " + ex.Message;
            else MessageBox.Show($"The project couldn't be saved: {ex.Message}", "Save project", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void Autosave()
    {
        if (Project == null || _autosave == null) return;
        // not in the middle of something (typing, a drag, a dialog): try again a little later
        bool busy = (DateTime.Now - _lastInput).TotalSeconds < 4 || System.Windows.Input.Mouse.Captured != null ||
                    Application.Current?.Windows.OfType<Window>().Any(w => w.IsVisible && w != Application.Current.MainWindow && w.Owner != null && IsModal(w)) == true;
        if (busy) { _autosave.Interval = TimeSpan.FromSeconds(20); return; }
        _autosave.Interval = AutosaveEvery;
        Flush(quiet: true);
        if (!HasUnsavedProjectChanges()) return;
        SaveProjectNow(quiet: true);
    }

    /// <summary>Before leaving the project (closing the app, opening another): asks to save it when something isn't
    /// saved. False = stay.</summary>
    public bool ConfirmLeaveProject()
    {
        if (Project == null) return true;
        Flush(quiet: true);   // edits still in the tabs go into the ROM first
        if (!HasUnsavedProjectChanges()) return true;
        var answer = MessageBox.Show($"Save the changes to the project \"{Project.Name}\"?", "Lufia Forge",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;
        return SaveProjectNow(quiet: false);
    }
}
