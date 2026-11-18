using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LufiaForge.Modules.MemoryMonitor;

// starts BizHawk (EmuHawk.exe) and moves its main window into a WinForms Panel, so it sits inside a WPF
// WindowsFormsHost. can also grab a BizHawk that's already running
public sealed class BizHawkHost : IDisposable
{
    // win32 interop
    private const int GWL_STYLE   = -16;
    private const int GWL_EXSTYLE = -20;

    private const uint WS_CHILD       = 0x40000000;
    private const uint WS_VISIBLE     = 0x10000000;
    private const uint WS_CAPTION     = 0x00C00000;
    private const uint WS_THICKFRAME  = 0x00040000;
    private const uint WS_SYSMENU     = 0x00080000;
    private const uint WS_MAXIMIZEBOX = 0x00010000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_POPUP       = 0x80000000;

    private const uint WS_EX_APPWINDOW    = 0x00040000;
    private const uint WS_EX_WINDOWEDGE   = 0x00000100;
    private const uint WS_EX_CLIENTEDGE   = 0x00000200;
    private const uint WS_EX_DLGMODALFRAME = 0x00000001;

    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST    = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST  = new(-2);
    private const uint SWP_NOMOVE  = 0x0002;
    private const uint SWP_NOSIZE  = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // state
    private Process? _process;
    private IntPtr   _bizHawkHwnd;
    private Panel?   _hostPanel;
    private bool     _weOwnProcess; // true if we launched it, false if we attached

    public IntPtr BizHawkHwnd => _bizHawkHwnd;
    public bool   IsRunning   => _bizHawkHwnd != IntPtr.Zero && IsWindow(_bizHawkHwnd);
    public bool   IsUndocked  { get; private set; }
    public Panel? HostPanel   => _hostPanel;

    public event Action? Attached;
    public event Action? Exited;
    public event Action<string>? StatusChanged;
    public event Action<bool>?   DockStateChanged;  // true = undocked

    // makes the WinForms Panel the docked BizHawk window goes in
    public Panel CreateHostPanel()
    {
        _hostPanel = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(18, 12, 28),
        };
        _hostPanel.Resize += (_, _) => { ResizeBizHawk(); _hostPanel.Invalidate(); };
        // while no BizHawk is docked here, say how to get one
        _hostPanel.Paint += (_, e) =>
        {
            if (IsRunning && !IsUndocked) return;
            string text = IsUndocked
                ? "BizHawk is in its own window.\nClick Dock to bring it back here."
                : "BizHawk isn't docked here.\n\nClick Launch BizHawk to play the open ROM in this space,\nwith Lufia Forge's live memory view on the right.\n\nA BizHawk you started yourself stays in its own window;\nits memory still shows on the right.";
            using var font = new System.Drawing.Font("Segoe UI", 11f);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(150, 140, 175));
            var panelArea = _hostPanel.ClientRectangle;
            using var centered = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
            e.Graphics.DrawString(text, font, brush, panelArea, centered);
        };
        return _hostPanel;
    }

    // looks for a running BizHawk and docks it (not async, the timer calls this). true if it found one and docked it
    public bool TryAttachToRunning()
    {
        if (IsRunning) return true;
        if (_hostPanel == null) return false;

        if (!TryFindRunningBizHawk()) return false;

        StatusChanged?.Invoke("Found running BizHawk - embedding...");
        ReparentWindow();
        Attached?.Invoke();
        return true;
    }

    // starts EmuHawk.exe, waits for its main window and docks it. if BizHawk is already running we take that one
    // the last LaunchAsync took a BizHawk that was already running (so it didn't get the ROM)
    public bool LastLaunchAttached { get; private set; }

    public async Task LaunchAsync(string emuHawkPath, string? romPath = null)
    {
        // already docked
        if (IsRunning) return;
        if (CoreWarning(emuHawkPath, romPath) is { } warning)
        {
            StatusChanged?.Invoke(warning);
            System.Windows.MessageBox.Show(warning, "BizHawk", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }

        // is BizHawk running already?
        LastLaunchAttached = false;
        if (TryFindRunningBizHawk())
        {
            LastLaunchAttached = true;
            StatusChanged?.Invoke("Found running BizHawk - embedding...");
            await Task.Delay(300);
            ReparentWindow();
            Attached?.Invoke();
            StatusChanged?.Invoke("BizHawk embedded.");
            return;
        }

        // start a new one
        StatusChanged?.Invoke("Starting BizHawk...");

        var arguments = "";
        // the spell order watcher starts with every ROM BizHawk opens from here (it logs next to itself)
        string watcherScript = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(emuHawkPath) ?? "", "Lua", "LufiaForge_WatchSpellOrder.lua");
        if (System.IO.File.Exists(watcherScript))
            arguments = $"\"--lua={watcherScript}\" ";
        if (!string.IsNullOrEmpty(romPath))
            arguments += $"\"{romPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName         = emuHawkPath,
            Arguments        = arguments,
            WorkingDirectory = System.IO.Path.GetDirectoryName(emuHawkPath) ?? "",
            UseShellExecute  = true,
        };

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Failed to start: {ex.Message}");
            return;
        }

        if (_process == null)
        {
            StatusChanged?.Invoke("Process.Start returned null.");
            return;
        }

        _weOwnProcess = true;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _bizHawkHwnd = IntPtr.Zero;
            try { _hostPanel?.Invoke(() => { _hostPanel.Invalidate(); Exited?.Invoke(); }); } catch { }
        };

        StatusChanged?.Invoke("Waiting for BizHawk window...");

        _bizHawkHwnd = await WaitForMainWindowAsync(_process, TimeSpan.FromSeconds(20));

        if (_bizHawkHwnd == IntPtr.Zero)
        {
            // fallback: look for it by process ID
            StatusChanged?.Invoke("MainWindowHandle not found, searching by PID...");
            await Task.Delay(2000);
            _bizHawkHwnd = FindWindowByProcessId((uint)_process.Id);
        }

        if (_bizHawkHwnd == IntPtr.Zero)
        {
            StatusChanged?.Invoke("Could not find BizHawk window. Try clicking 'Launch' again.");
            return;
        }

        StatusChanged?.Invoke($"Found BizHawk window (0x{_bizHawkHwnd:X}). Embedding...");

        // give BizHawk a moment to finish setting up its UI
        await Task.Delay(1500);

        ReparentWindow();
        Attached?.Invoke();
        StatusChanged?.Invoke("BizHawk embedded successfully.");
    }

    // an 8 MB Lufia II ROM (ExLoROM) can't run in BizHawk's older "BSNES" core. it only maps 4 MB and the game stops
    // at the Natsume logo. BSNESv115+ and Snes9x run it fine. returns the warning when BizHawk's config picks that
    // core, or null. we only read the config, never write it
    public static string? CoreWarning(string emuHawkPath, string? romPath)
    {
        try
        {
            if (string.IsNullOrEmpty(romPath) || !System.IO.File.Exists(romPath) || new System.IO.FileInfo(romPath).Length != Core.RomBuffer.ExLoRomSize) return null;
            string configPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(emuHawkPath) ?? "", "config.ini");
            if (!System.IO.File.Exists(configPath)) return null;
            var coreMatch = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText(configPath), @"""SNES""\s*:\s*""([^""]*)""");
            if (!coreMatch.Success || coreMatch.Groups[1].Value != "BSNES") return null;
            return "This ROM is 8 MB, and BizHawk is set to its older BSNES core, which can't run an 8 MB ROM (the game stops at the Natsume logo). " +
                   "In BizHawk pick Config > Preferred Cores > SNES > BSNESv115+ (or Snes9x), then open the ROM again.";
        }
        catch (Exception) { return null; }
    }

    // finds an EmuHawk that's already running and grabs its window
    private bool TryFindRunningBizHawk()
    {
        var emuHawks = Process.GetProcessesByName("EmuHawk");
        foreach (var emuHawk in emuHawks)
        {
            try
            {
                emuHawk.Refresh();
                if (emuHawk.MainWindowHandle != IntPtr.Zero)
                {
                    _process      = emuHawk;
                    _bizHawkHwnd  = emuHawk.MainWindowHandle;
                    _weOwnProcess = false;

                    _process.EnableRaisingEvents = true;
                    _process.Exited += (_, _) =>
                    {
                        _bizHawkHwnd = IntPtr.Zero;
                        try { _hostPanel?.Invoke(() => { _hostPanel.Invalidate(); Exited?.Invoke(); }); } catch { }
                    };

                    return true;
                }

                // fallback: find it by PID
                IntPtr windowHandle = FindWindowByProcessId((uint)emuHawk.Id);
                if (windowHandle != IntPtr.Zero)
                {
                    _process      = emuHawk;
                    _bizHawkHwnd  = windowHandle;
                    _weOwnProcess = false;

                    _process.EnableRaisingEvents = true;
                    _process.Exited += (_, _) =>
                    {
                        _bizHawkHwnd = IntPtr.Zero;
                        try { _hostPanel?.Invoke(() => { _hostPanel.Invalidate(); Exited?.Invoke(); }); } catch { }
                    };

                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    // moves BizHawk's main window into our host panel
    private void ReparentWindow()
    {
        if (_hostPanel == null || _bizHawkHwnd == IntPtr.Zero) return;

        // no title bar, borders or taskbar button
        uint style = GetWindowLong(_bizHawkHwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_POPUP);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLong(_bizHawkHwnd, GWL_STYLE, style);

        // and none of the extended styles
        uint exStyle = GetWindowLong(_bizHawkHwnd, GWL_EXSTYLE);
        exStyle &= ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME);
        SetWindowLong(_bizHawkHwnd, GWL_EXSTYLE, exStyle);

        SetParent(_bizHawkHwnd, _hostPanel.Handle);

        // show it and fill the panel
        ShowWindow(_bizHawkHwnd, SW_SHOW);
        ResizeBizHawk();
    }

    // sizes BizHawk to fill the host panel
    private void ResizeBizHawk()
    {
        if (_hostPanel == null || _bizHawkHwnd == IntPtr.Zero) return;
        if (!IsWindow(_bizHawkHwnd)) return;

        MoveWindow(_bizHawkHwnd, 0, 0, _hostPanel.Width, _hostPanel.Height, true);
    }

    // dock / undock

    // takes BizHawk out of the host panel and makes it a floating always-on-top window, so you can see it on any tab
    public void Undock()
    {
        if (!IsRunning || IsUndocked) return;

        // give it back its title bar and borders
        uint style = GetWindowLong(_bizHawkHwnd, GWL_STYLE);
        style &= ~(WS_CHILD);
        style |= WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_VISIBLE;
        SetWindowLong(_bizHawkHwnd, GWL_STYLE, style);

        uint exStyle = GetWindowLong(_bizHawkHwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_APPWINDOW;
        SetWindowLong(_bizHawkHwnd, GWL_EXSTYLE, exStyle);

        // no parent = a top-level window again
        SetParent(_bizHawkHwnd, IntPtr.Zero);

        // always on top so it stays over Lufia Forge on any tab
        SetWindowPos(_bizHawkHwnd, HWND_TOPMOST, 100, 100, 800, 600,
                     SWP_SHOWWINDOW);

        IsUndocked = true;
        DockStateChanged?.Invoke(true); _hostPanel?.Invalidate();
        StatusChanged?.Invoke("BizHawk undocked: floating window active.");
    }

    // puts BizHawk back in the host panel
    public void Dock()
    {
        if (!IsRunning || !IsUndocked) return;

        // drop always-on-top before moving it back in
        SetWindowPos(_bizHawkHwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE);

        ReparentWindow();

        IsUndocked = false;
        DockStateChanged?.Invoke(false); _hostPanel?.Invalidate();
        StatusChanged?.Invoke("BizHawk re-docked.");
    }

    // gives BizHawk the keyboard and mouse
    public void Focus()
    {
        if (_bizHawkHwnd != IntPtr.Zero && IsWindow(_bizHawkHwnd))
            SetForegroundWindow(_bizHawkHwnd);
    }

    // polls the process until it has a MainWindowHandle
    private static async Task<IntPtr> WaitForMainWindowAsync(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) return IntPtr.Zero;

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                // and the window has a title and is visible
                string title = GetWindowTitle(process.MainWindowHandle);
                if (title.Length > 0 && IsWindowVisible(process.MainWindowHandle))
                    return process.MainWindowHandle;
            }

            await Task.Delay(250);
        }
        return IntPtr.Zero;
    }

    // finds a visible top-level window that belongs to this process ID
    private static IntPtr FindWindowByProcessId(uint processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid != processId) return true;
            if (!IsWindowVisible(hWnd)) return true;

            string title = GetWindowTitle(hWnd);
            if (title.Contains("BizHawk", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("EmuHawk", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("SNES", StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false; // stop looking
            }
            return true;
        }, IntPtr.Zero);

        // no title matched, so just take the first visible window
        if (found == IntPtr.Zero)
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowPid);
                if (windowPid != processId) return true;
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindowTextLength(hWnd) == 0) return true;

                found = hWnd;
                return false;
            }, IntPtr.Zero);
        }

        return found;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        int titleLength = GetWindowTextLength(hWnd);
        if (titleLength <= 0) return "";
        var sb = new StringBuilder(titleLength + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_bizHawkHwnd != IntPtr.Zero && IsWindow(_bizHawkHwnd))
        {
            // let go of it so it shows up as a normal window again
            SetParent(_bizHawkHwnd, IntPtr.Zero);

            // with its normal styles back
            uint style = GetWindowLong(_bizHawkHwnd, GWL_STYLE);
            style &= ~WS_CHILD;
            style |= WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_VISIBLE;
            SetWindowLong(_bizHawkHwnd, GWL_STYLE, style);

            ShowWindow(_bizHawkHwnd, SW_SHOW);
        }

        // only kill the process if we started it
        if (_weOwnProcess)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.CloseMainWindow();
                    if (!_process.WaitForExit(3000))
                        _process.Kill();
                }
            }
            catch { }
        }

        _process?.Dispose();
        _hostPanel?.Dispose();
    }
}
