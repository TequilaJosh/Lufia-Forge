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

    /// <summary>
    /// A dialog is open on the UI thread: a window shown with ShowDialog, a message box or a file picker. The timers
    /// still tick inside its loop, so nothing may write the ROM or save the project behind it (an editor halfway
    /// through Apply may be asking a question in it). Message boxes and file pickers aren't WPF windows: they are the
    /// thread's visible windows of the dialog class (#32770).
    /// </summary>
    public static bool IsDialogOpen()
    {
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) return true;
        if (Application.Current?.Windows.OfType<Window>().Any(w => w.IsVisible && IsModal(w)) == true) return true;
        bool found = false;
        var name = new System.Text.StringBuilder(16);
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            name.Clear();
            if (IsWindowVisible(hwnd) && GetClassName(hwnd, name, 16) > 0 && name.ToString() == "#32770") { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowsProc callback, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

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
            Filter = "SNES ROM files (*.sfc;*.smc)|*.sfc;*.smc|The Dutch Lufia II (*.sfc;*.smc;*.zip)|*.sfc;*.smc;*.zip|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        if (TryDutch(dialog.FileName)) return;
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
        // a project made before Lufia Forge knew the ROM (the Dutch Lufia II was taken for Lufia 1)
        if (GameDetect.Detect(rom) == GameKind.Unknown) throw new InvalidDataException(GameDetect.WhyNot(rom));
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
    /// shrine maps CF and F1, drawn wrong in the US game (<see cref="Core.Lufia2.L2ShrineFix"/>), and on an 8 MB Lufia II
    /// ROM made before 2026-11-01 the battle window frame read from the upper 4 MB (<see cref="Core.Lufia2.L2Rom.FixLowBankReads"/>),
    /// and puzzle blocks Lufia Forge wrote before then past 2.5 MB, where their commands can't reach RAM
    /// (<see cref="Core.Lufia2.L2PuzzleBlock.MoveIntoReach"/>), and a version 2 battle script block, whose runner hook
    /// skipped the fan hacks' monster scripts with offsets that wrap round their bank (<see cref="Core.Lufia2.L2AiScripts.UpdateHooks"/>).</summary>
    private string _openNote = "";

    private void FixKnownBugs(RomBuffer rom)
    {
        _openNote = "";
        if (Core.Lufia2.L2Rom.NeedsLowBankFix(rom))
        {
            Core.Lufia2.L2Rom.FixLowBankReads(rom);
            NotifyRomModified("Lufia II: battle window border fixed for the 8 MB ROM (read from bank $81, not the upper 4 MB)");
            OnPropertyChanged(nameof(RomBuffer));
            _openNote = " The hero's battle window border, broken on 8 MB ROMs, was fixed (Edit, Undo takes it back).";
        }
        if (rom.IsLufia2 && Core.Lufia2.L2AiScripts.UpdateHooks(rom))
        {
            NotifyRomModified("Lufia II: battle script hooks updated (monster scripts that wrap round their bank run again)");
            OnPropertyChanged(nameof(RomBuffer));
            _openNote += " Lufia Forge's battle script hooks were updated: in fan hacks some monsters' scripts didn't run with the old ones (Edit, Undo takes it back).";
        }
        if (rom.IsLufia2 && Core.Lufia2.L2PuzzleBlock.OutOfReach(rom).Count > 0)
        {
            var (moved, problem) = Core.Lufia2.L2PuzzleBlock.MoveIntoReach(rom);
            if (moved.Count > 0)
            {
                string maps = moved.Count > 8 ? $"{moved.Count} maps" : (moved.Count > 1 ? "maps " : "map ") + string.Join(", ", moved.Select(m => m.ToString("X2")));
                NotifyRomModified($"Lufia II: puzzle blocks of {maps} moved where their commands reach RAM (the event space)");
                OnPropertyChanged(nameof(RomBuffer));
                _openNote += $" The puzzles Lufia Forge wrote on {moved.Count} map{(moved.Count > 1 ? "s" : "")} sat where the game can't reach RAM from them and were moved (Edit, Undo takes it back).";
            }
            if (problem != null) _openNote += " A puzzle block couldn't be moved where the game reaches RAM from it: " + problem;
        }
        if (!Core.Lufia2.L2ShrineFix.Needed(rom)) return;
        try
        {
            Core.Lufia2.L2ShrineFix.Apply(rom);
            NotifyRomModified("Lufia II: shrine maps CF and F1 fixed as in the European release (block set 17 and tile set 11; maps E7-EB keep their look)");
            OnPropertyChanged(nameof(RomBuffer));   // the pages read the fixed ROM
            _openNote += " Shrine maps CF and F1, garbled in the US game, were fixed as in the European release (Edit, Undo takes it back).";
        }
        catch (Exception ex) { _openNote += " Lufia II's shrine maps CF and F1 couldn't be fixed: " + ex.Message; }
    }

    /// <summary>The Dutch Lufia II picked for a new project: Lufia Forge edits the US game's layout, so the Dutch text
    /// goes into a Lufia II (USA) ROM or a hack built on one, picked next (<see cref="Core.Lufia2.L2Dutch"/>). Returns
    /// false when the file isn't the Dutch ROM.</summary>
    private bool TryDutch(string path)
    {
        byte[] dutch;
        try { dutch = Core.Lufia2.L2Dutch.ReadFile(path); }   // (a .zip too)
        catch { return false; }
        if (MessageBox.Show("This is the Dutch Lufia II, a translation built on the European release, which keeps its data in other places " +
                "than the US game.\n\nLufia Forge edits Lufia II (USA) and the hacks built on it, so it puts the Dutch translation into one of " +
                "those: the dialogue, the names, the descriptions and the fonts with the Dutch letters. The game then runs at 60 Hz, and a hack " +
                "like Frue Lufia keeps its fixes. The menus and battle messages stay English.\n\nPick the Lufia II (USA) ROM, or a ROM of a hack " +
                "built on it, next.", "Dutch Lufia II", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return true;
        var pick = new OpenFileDialog
        {
            Title = "Pick the Lufia II (USA) ROM (or a hack built on it) to put the Dutch text into",
            Filter = "SNES ROM files (*.sfc;*.smc)|*.sfc;*.smc|All files (*.*)|*.*",
        };
        if (pick.ShowDialog() != true) return true;
        var report = RomLoader.Load(pick.FileName);
        if (report.Buffer == null || report.Buffer.Game != GameKind.Lufia2)
        {
            MessageBox.Show("That isn't Lufia II (USA) or a hack built on it. " + report.Message, "Dutch Lufia II", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }
        var ask = new Modules.Common.NewProjectWindow(path) { Owner = Application.Current?.MainWindow };
        if (ask.ShowDialog() != true) return true;
        try
        {
            var rom = report.Buffer.ToArray();
            var project = LufiaProject.Create(ask.ProjectPath, ask.ProjectName, rom, pick.FileName);
            Use(project, rom);
            var done = Core.Lufia2.L2Dutch.Import(RomBuffer!, dutch);
            NotifyRomModified("Dutch translation from " + Path.GetFileName(path));
            ReloadModules();
            StatusMessage = $"Project \"{project.Name}\" started from {Path.GetFileName(pick.FileName)} with the Dutch translation: {done} Save Project keeps it.";
            StatusDetail = project.Path;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The project couldn't be made: {ex.Message}", "New project", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return true;
    }

    /// <summary>The ROM file next to the project (what Export ROM names its copies after, and where the bookmarks and
    /// flag names are kept): "&lt;project&gt;.sfc". BizHawk plays it too, unless another file is there
    /// (<see cref="PlayCopies"/>).</summary>
    private static string PlayRomPath(LufiaProject p) => Path.ChangeExtension(p.Path, ".sfc");

    /// <summary>Bookmarks and story flag names kept next to the ROM go with the project (with <paramref name="replace"/>,
    /// also over the notes already at the new place: Save Project As over another project).</summary>
    private static void CarryOverNotes(string romPath, string projectRom, bool replace = false)
    {
        foreach (var ext in new[] { ".lfbookmarks.json", ".lfflags.json" })
        {
            try
            {
                string from = Path.ChangeExtension(romPath, null) + ext, to = Path.ChangeExtension(projectRom, null) + ext;
                if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(from) && (replace || !File.Exists(to))) File.Copy(from, to, overwrite: true);
            }
            catch { }
        }
    }

    /// <summary>The BizHawk window <see cref="PlayInBizHawk"/> opened last (closed before a new one opens).</summary>
    private static System.Diagnostics.Process? _played;

    /// <summary>Writes the project's ROM as it is now and opens it in BizHawk, in a window of its own (the one opened
    /// here before is closed first). Returns what happened, for a page's status line.</summary>
    public string PlayInBizHawk()
    {
        string? rom = WritePlayRom();
        if (rom == null) return "The ROM couldn't be written for BizHawk.";
        string exe = Modules.MemoryMonitor.MemoryMonitorViewModel.FindBundledBizHawk();
        if (!File.Exists(exe)) return $"BizHawk wasn't found (looked for {exe}); the ROM is at {rom}.";
        try
        {
            if (_played is { HasExited: false } old)
            {
                old.CloseMainWindow();
                if (!old.WaitForExit(3000)) old.Kill();
            }
        }
        catch (Exception) { }
        try
        {
            _played = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"\"{rom}\"")
            { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });
        }
        catch (Exception ex) { return "BizHawk couldn't be opened: " + ex.Message; }
        string? warn = Modules.MemoryMonitor.BizHawkHost.CoreWarning(exe, rom);
        return "BizHawk is opening the ROM: start a new game." + (warn != null ? " " + warn : "");
    }

    /// <summary>Writes the project's ROM as it is now next to the project (for BizHawk) and returns its path. A file
    /// there that Lufia Forge didn't write (the ROM the project started from, say) is never overwritten: the copy goes
    /// to "&lt;project&gt;.play.sfc" then (<see cref="PlayCopies"/>).</summary>
    public string? WritePlayRom()
    {
        if (RomBuffer == null || Project == null) return null;
        string target = PlayCopies.Target(RomBuffer.FilePath, Project.BaseRom, Project.Created);
        try
        {
            Flush(quiet: true);
            RomBuffer.WriteCopy(target);
            PlayCopies.Remember(target);
            return target;
        }
        catch (Exception ex)
        {
            StatusMessage = "The ROM for BizHawk couldn't be written: " + ex.Message;
            return File.Exists(target) ? target : null;
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
        try { SaveProjectTo(d.FileName); }
        catch (Exception ex) { MessageBox.Show($"The project couldn't be saved: {ex.Message}", "Save project", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>Save Project As, once the file is picked: when the save fails the project keeps its old file and name
    /// (later saves go there, not to the file that couldn't be written). The bookmarks and flag names go along.</summary>
    public void SaveProjectTo(string path)
    {
        if (Project == null || RomBuffer == null) return;
        var rom = RomBuffer.ToArray();
        string oldName = Project.Name, oldRom = RomBuffer.FilePath;
        Project.Name = Path.GetFileNameWithoutExtension(path);
        try { Project.SaveAs(path, rom); }
        catch { Project.Name = oldName; throw; }
        _savedRom = rom;
        CarryOverNotes(oldRom, PlayRomPath(Project), replace: true);
        RomBuffer.SetFilePath(PlayRomPath(Project));
        RecentProjects.Add(Project.Path, Project.Name);
        RefreshRecent();
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ProjectText));
        // the bookmarks and flag names are kept next to the project's ROM file: the pages open them at the new place
        if (!string.Equals(oldRom, RomBuffer.FilePath, StringComparison.OrdinalIgnoreCase)) ReloadModules();
        StatusMessage = $"Project saved as \"{Project.Name}\".";
        StatusDetail = Project.Path;
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
        bool busy = (DateTime.Now - _lastInput).TotalSeconds < 4 || System.Windows.Input.Mouse.Captured != null || IsDialogOpen();
        if (busy) { _autosave.Interval = TimeSpan.FromSeconds(20); return; }
        _autosave.Interval = AutosaveEvery;
        Flush(quiet: true);
        if (!HasUnsavedProjectChanges()) return;
        SaveProjectNow(quiet: true);
    }

    /// <summary>Before an update restarts Lufia Forge (the restart skips the question on closing): waits until no dialog
    /// is open, then writes the tabs' edits and saves the project. False when something couldn't be kept (the restart
    /// must not happen then).</summary>
    public async Task<bool> SaveBeforeRestartAsync()
    {
        while (IsDialogOpen()) await Task.Delay(500);
        if (Project == null || RomBuffer == null) return true;
        if (!HasUnsavedProjectChanges()) return true;
        if (!SaveProjectNow(quiet: false)) return false;
        return !HasPendingEdits;   // edits a tab couldn't write would be lost
    }

    /// <summary>
    /// Lufia Forge has to close after an error on another thread (the app's own error handler can't keep it open):
    /// when the project has changes that aren't saved, a copy of it as it is now goes next to it, "&lt;name&gt; (recovered
    /// &lt;date&gt;).lfproj", leaving the project file itself alone. Edits still in a tab aren't in it. Returns the
    /// copy's path, or null.
    /// </summary>
    public string? SaveRecoveryCopy()
    {
        try
        {
            if (Project is not { } project || RomBuffer is not { } buffer) return null;
            var rom = buffer.ToArray();
            if (!project.MapNamesChanged && _savedRom != null && rom.AsSpan().SequenceEqual(_savedRom)) return null;
            string path = Path.Combine(Path.GetDirectoryName(project.Path) ?? "",
                $"{Path.GetFileNameWithoutExtension(project.Path)} (recovered {DateTime.Now:yyyy-MM-dd HH-mm-ss}){LufiaProject.Extension}");
            project.SaveCopy(path, rom);
            return path;
        }
        catch { return null; }
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
