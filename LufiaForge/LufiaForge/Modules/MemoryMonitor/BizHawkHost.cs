using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LufiaForge.Modules.MemoryMonitor;

/// <summary>
/// Launches BizHawk (EmuHawk.exe) and reparents its main window
/// into a WinForms Panel for embedding inside a WPF WindowsFormsHost.
/// Also supports attaching to an already-running BizHawk instance.
/// </summary>
public sealed class BizHawkHost : IDisposable
{
    // -------------------------------------------------------------------------
    // Win32 interop
    // -------------------------------------------------------------------------
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

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private Process? _process;
    private IntPtr   _bizHawkHwnd;
    private Panel?   _hostPanel;
    private bool     _weOwnProcess; // true if we launched it, false if we attached

    public IntPtr BizHawkHwnd => _bizHawkHwnd;
    public bool   IsRunning   => _bizHawkHwnd != IntPtr.Zero && IsWindow(_bizHawkHwnd);
    public Panel? HostPanel   => _hostPanel;

    public event Action? Attached;
    public event Action? Exited;
    public event Action<string>? StatusChanged;

    /// <summary>
    /// Creates the WinForms Panel that will host the embedded BizHawk window.
    /// </summary>
    public Panel CreateHostPanel()
    {
        _hostPanel = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(18, 12, 28),
        };
        _hostPanel.Resize += (_, _) => ResizeBizHawk();
        return _hostPanel;
    }

    /// <summary>
    /// Tries to find a running BizHawk and embed it (non-async, for timer use).
    /// Returns true if it found and embedded BizHawk.
    /// </summary>
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

    /// <summary>
    /// Launches EmuHawk.exe, waits for its main window, and reparents it.
    /// If BizHawk is already running, attaches to the existing instance.
    /// </summary>
    public async Task LaunchAsync(string emuHawkPath, string? romPath = null)
    {
        // If already embedded, do nothing
        if (IsRunning) return;

        // First, check if BizHawk is already running
        if (TryFindRunningBizHawk())
        {
            StatusChanged?.Invoke("Found running BizHawk - embedding...");
            await Task.Delay(300);
            ReparentWindow();
            Attached?.Invoke();
            StatusChanged?.Invoke("BizHawk embedded.");
            return;
        }

        // Launch new instance
        StatusChanged?.Invoke("Starting BizHawk...");

        var args = "";
        if (!string.IsNullOrEmpty(romPath))
            args = $"\"{romPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName         = emuHawkPath,
            Arguments        = args,
            WorkingDirectory = System.IO.Path.GetDirectoryName(emuHawkPath) ?? "",
            UseShellExecute  = true,
        };

        try
        {
            _process = Process.Start(psi);
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
            try { _hostPanel?.Invoke(() => Exited?.Invoke()); } catch { }
        };

        StatusChanged?.Invoke("Waiting for BizHawk window...");

        // Wait for the main window to appear
        _bizHawkHwnd = await WaitForMainWindowAsync(_process, TimeSpan.FromSeconds(20));

        if (_bizHawkHwnd == IntPtr.Zero)
        {
            // Fallback: search by process ID
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

        // Extra delay for BizHawk to finish initializing its UI
        await Task.Delay(1500);

        ReparentWindow();
        Attached?.Invoke();
        StatusChanged?.Invoke("BizHawk embedded successfully.");
    }

    /// <summary>
    /// Find an already-running EmuHawk process and grab its window.
    /// </summary>
    private bool TryFindRunningBizHawk()
    {
        var procs = Process.GetProcessesByName("EmuHawk");
        foreach (var p in procs)
        {
            try
            {
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    _process      = p;
                    _bizHawkHwnd  = p.MainWindowHandle;
                    _weOwnProcess = false;

                    _process.EnableRaisingEvents = true;
                    _process.Exited += (_, _) =>
                    {
                        _bizHawkHwnd = IntPtr.Zero;
                        try { _hostPanel?.Invoke(() => Exited?.Invoke()); } catch { }
                    };

                    return true;
                }

                // Fallback: find by PID
                IntPtr hwnd = FindWindowByProcessId((uint)p.Id);
                if (hwnd != IntPtr.Zero)
                {
                    _process      = p;
                    _bizHawkHwnd  = hwnd;
                    _weOwnProcess = false;

                    _process.EnableRaisingEvents = true;
                    _process.Exited += (_, _) =>
                    {
                        _bizHawkHwnd = IntPtr.Zero;
                        try { _hostPanel?.Invoke(() => Exited?.Invoke()); } catch { }
                    };

                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// Reparents BizHawk's main window into our host panel.
    /// </summary>
    private void ReparentWindow()
    {
        if (_hostPanel == null || _bizHawkHwnd == IntPtr.Zero) return;

        // Strip title bar, borders, taskbar presence
        uint style = GetWindowLong(_bizHawkHwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_POPUP);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLong(_bizHawkHwnd, GWL_STYLE, style);

        // Strip extended styles
        uint exStyle = GetWindowLong(_bizHawkHwnd, GWL_EXSTYLE);
        exStyle &= ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME);
        SetWindowLong(_bizHawkHwnd, GWL_EXSTYLE, exStyle);

        // Reparent into our panel
        SetParent(_bizHawkHwnd, _hostPanel.Handle);

        // Make visible and fill panel
        ShowWindow(_bizHawkHwnd, SW_SHOW);
        ResizeBizHawk();
    }

    /// <summary>Resize BizHawk to fill the host panel.</summary>
    private void ResizeBizHawk()
    {
        if (_hostPanel == null || _bizHawkHwnd == IntPtr.Zero) return;
        if (!IsWindow(_bizHawkHwnd)) return;

        MoveWindow(_bizHawkHwnd, 0, 0, _hostPanel.Width, _hostPanel.Height, true);
    }

    /// <summary>Give BizHawk keyboard/mouse focus.</summary>
    public void Focus()
    {
        if (_bizHawkHwnd != IntPtr.Zero && IsWindow(_bizHawkHwnd))
            SetForegroundWindow(_bizHawkHwnd);
    }

    /// <summary>Polls the process until a MainWindowHandle appears.</summary>
    private static async Task<IntPtr> WaitForMainWindowAsync(Process proc, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited) return IntPtr.Zero;

            proc.Refresh();
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                // Extra check: make sure the window has a title and is visible
                string title = GetWindowTitle(proc.MainWindowHandle);
                if (title.Length > 0 && IsWindowVisible(proc.MainWindowHandle))
                    return proc.MainWindowHandle;
            }

            await Task.Delay(250);
        }
        return IntPtr.Zero;
    }

    /// <summary>Find a visible top-level window belonging to a specific process ID.</summary>
    private static IntPtr FindWindowByProcessId(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid != pid) return true;
            if (!IsWindowVisible(hWnd)) return true;

            string title = GetWindowTitle(hWnd);
            if (title.Contains("BizHawk", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("EmuHawk", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("SNES", StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false; // stop enumeration
            }
            return true;
        }, IntPtr.Zero);

        // If nothing matched by title, just grab the first visible window
        if (found == IntPtr.Zero)
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowPid);
                if (windowPid != pid) return true;
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
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_bizHawkHwnd != IntPtr.Zero && IsWindow(_bizHawkHwnd))
        {
            // Unparent so the window reappears normally
            SetParent(_bizHawkHwnd, IntPtr.Zero);

            // Restore standard styles
            uint style = GetWindowLong(_bizHawkHwnd, GWL_STYLE);
            style &= ~WS_CHILD;
            style |= WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_VISIBLE;
            SetWindowLong(_bizHawkHwnd, GWL_STYLE, style);

            ShowWindow(_bizHawkHwnd, SW_SHOW);
        }

        // Only kill the process if we launched it
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
