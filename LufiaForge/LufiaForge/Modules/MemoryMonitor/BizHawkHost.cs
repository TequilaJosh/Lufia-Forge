using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LufiaForge.Modules.MemoryMonitor;

/// <summary>
/// Launches BizHawk (EmuHawk.exe) and reparents its main window
/// into a WinForms Panel for embedding inside a WPF WindowsFormsHost.
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

    private const uint WS_EX_APPWINDOW   = 0x00040000;
    private const uint WS_EX_WINDOWEDGE  = 0x00000100;
    private const uint WS_EX_CLIENTEDGE  = 0x00000200;
    private const uint WS_EX_DLGMODALFRAME = 0x00000001;

    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")] private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern uint SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private Process?  _process;
    private IntPtr    _bizHawkHwnd;
    private Panel?    _hostPanel;

    public IntPtr BizHawkHwnd => _bizHawkHwnd;
    public bool   IsRunning   => _process is { HasExited: false } && IsWindow(_bizHawkHwnd);
    public Panel? HostPanel   => _hostPanel;

    public event Action? Attached;
    public event Action? Exited;

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
    /// Launches EmuHawk.exe, waits for its main window, and reparents it
    /// into the host panel. Optionally pass a ROM path to auto-load.
    /// </summary>
    public async Task LaunchAsync(string emuHawkPath, string? romPath = null)
    {
        if (_process is { HasExited: false })
            return; // already running

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

        _process = Process.Start(psi);
        if (_process == null) return;

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _bizHawkHwnd = IntPtr.Zero;
            _hostPanel?.Invoke(() => Exited?.Invoke());
        };

        // Wait for main window handle (up to 15 seconds)
        _bizHawkHwnd = await WaitForMainWindowAsync(_process, TimeSpan.FromSeconds(15));
        if (_bizHawkHwnd == IntPtr.Zero) return;

        // Small delay to let BizHawk fully initialize its window
        await Task.Delay(500);

        ReparentWindow();
        Attached?.Invoke();
    }

    /// <summary>
    /// Reparents BizHawk's main window into our host panel.
    /// </summary>
    private void ReparentWindow()
    {
        if (_hostPanel == null || _bizHawkHwnd == IntPtr.Zero) return;

        // Strip title bar, borders, make it a child window
        uint style = GetWindowLong(_bizHawkHwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX | WS_MINIMIZEBOX);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLong(_bizHawkHwnd, GWL_STYLE, style);

        // Strip extended window styles
        uint exStyle = GetWindowLong(_bizHawkHwnd, GWL_EXSTYLE);
        exStyle &= ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME);
        SetWindowLong(_bizHawkHwnd, GWL_EXSTYLE, exStyle);

        // Reparent into our panel
        SetParent(_bizHawkHwnd, _hostPanel.Handle);

        // Fill the panel
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
                return proc.MainWindowHandle;

            await Task.Delay(200);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_bizHawkHwnd != IntPtr.Zero && IsWindow(_bizHawkHwnd))
        {
            // Unparent before killing so BizHawk can clean up
            SetParent(_bizHawkHwnd, IntPtr.Zero);
        }

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

        _process?.Dispose();
        _hostPanel?.Dispose();
    }
}
