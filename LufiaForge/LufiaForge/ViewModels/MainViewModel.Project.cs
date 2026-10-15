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

    /// <summary>Tabs with edits that aren't in the ROM yet (the map editor's unapplied map): asked to write them
    /// before the project is saved. Each returns false (with the reason) when it couldn't.</summary>
    public event Func<(bool Ok, string? Error)>? FlushPendingEdits;

    /// <summary>Whether a tab has edits that aren't in the ROM yet.</summary>
    public Func<bool>? HasPendingEdits { get; set; }

    private DispatcherTimer? _autosave;

    private void StartProjectSupport()
    {
        RefreshRecent();
        _autosave = new DispatcherTimer { Interval = AutosaveEvery };
        _autosave.Tick += (_, _) => Autosave();
        _autosave.Start();
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
        if (HasPendingEdits?.Invoke() == true) return true;
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
            Title = "Pick the Lufia 1 ROM to start a project from",
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
            StatusMessage = $"Project \"{project.Name}\" opened (saved {project.Saved:yyyy-MM-dd HH:mm}).";
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
        _savedRom = (byte[])rom.Clone();
        RomBuffer = new RomBuffer(rom, PlayRomPath(project));
        RecentProjects.Add(project.Path, project.Name);
        RefreshRecent();
        OnPropertyChanged(nameof(WindowTitle));
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
        if (FlushPendingEdits == null) return true;
        bool ok = true;
        foreach (Func<(bool Ok, string? Error)> f in FlushPendingEdits.GetInvocationList())
        {
            var (done, error) = f();
            if (done) continue;
            ok = false;
            string msg = "Some map edits couldn't be saved into the project yet: " + error;
            if (quiet) StatusDetail = msg;
            else MessageBox.Show(msg + "\n\nThe rest of the project is saved; the map keeps its edits in the Map Editor.", "Save project", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return ok;
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
        if (Project == null || !HasUnsavedProjectChanges()) return;
        SaveProjectNow(quiet: true);
    }

    /// <summary>Before leaving the project (closing the app, opening another): asks to save it when something isn't
    /// saved. False = stay.</summary>
    public bool ConfirmLeaveProject()
    {
        if (Project == null || !HasUnsavedProjectChanges()) return true;
        var answer = MessageBox.Show($"Save the changes to the project \"{Project.Name}\"?", "Lufia Forge",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;
        return SaveProjectNow(quiet: false);
    }
}
