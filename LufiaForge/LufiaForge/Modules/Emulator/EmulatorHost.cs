using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LufiaForge.Modules.Emulator;

/// <summary>
/// HwndHost that launches Snes9x 1.62.3 Win32 as a child process and
/// reparents its main window into the WPF layout.
///
/// Usage:
///   1. Set Snes9xPath and (optionally) RomPath before calling LaunchAsync.
///   2. Bind Width/Height changes to SizeChanged to keep the game filling the panel.
///   3. Call Shutdown() on tab close / app exit.
/// </summary>
public sealed class EmulatorHost : HwndHost
{
    // -------------------------------------------------------------------------
    // Win32 imports
    // -------------------------------------------------------------------------
    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private const int GWL_STYLE     = -16;
    private const int WS_CHILD      = 0x40000000;
    private const int WS_VISIBLE    = 0x10000000;
    private const int WS_CAPTION    = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int SW_SHOW       = 5;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private Process?  _process;
    private IntPtr    _snes9xHwnd  = IntPtr.Zero;
    private IntPtr    _hostHwnd    = IntPtr.Zero;

    public string Snes9xPath { get; set; } = "";
    public string RomPath    { get; set; } = "";

    public bool IsRunning  => _process != null && !_process.HasExited;
    public bool IsAttached => IsRunning && _snes9xHwnd != IntPtr.Zero && IsWindow(_snes9xHwnd);

    public event EventHandler? EmulatorExited;
    public event EventHandler<string>? StatusChanged;

    // -------------------------------------------------------------------------
    // HwndHost overrides
    // -------------------------------------------------------------------------
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // Create a plain Win32 child window to serve as the host container.
        // Snes9x will be reparented into this window.
        _hostHwnd = CreateHostWindow(hwndParent.Handle);
        return new HandleRef(this, _hostHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Shutdown();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Launch Snes9x and wait for its main window, then reparent it.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> LaunchAsync()
    {
        if (string.IsNullOrWhiteSpace(Snes9xPath) || !File.Exists(Snes9xPath))
        {
            StatusChanged?.Invoke(this, "Snes9x path not configured.");
            return false;
        }

        Shutdown(); // clean up any previous instance

        StatusChanged?.Invoke(this, "Launching Snes9x...");

        var args = string.IsNullOrWhiteSpace(RomPath)
            ? ""
            : $"\"{RomPath}\"";

        _process = Process.Start(new ProcessStartInfo
        {
            FileName        = Snes9xPath,
            Arguments       = args,
            UseShellExecute = false,
        });

        if (_process == null)
        {
            StatusChanged?.Invoke(this, "Failed to start Snes9x process.");
            return false;
        }

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _snes9xHwnd = IntPtr.Zero;
            EmulatorExited?.Invoke(this, EventArgs.Empty);
        };

        // Wait up to 8 seconds for Snes9x to open its main window
        _snes9xHwnd = await WaitForMainWindowAsync(_process, TimeSpan.FromSeconds(8));

        if (_snes9xHwnd == IntPtr.Zero)
        {
            StatusChanged?.Invoke(this, "Snes9x launched but window not found.");
            return false;
        }

        // Reparent into our host window
        if (_hostHwnd != IntPtr.Zero)
        {
            ReparentWindow(_snes9xHwnd, _hostHwnd);
            FitToHost();
        }

        StatusChanged?.Invoke(this, "Snes9x running.");
        return true;
    }

    public void Shutdown()
    {
        if (_process == null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(2000);
            }
        }
        catch { /* ignore */ }
        finally
        {
            _process.Dispose();
            _process     = null;
            _snes9xHwnd  = IntPtr.Zero;
        }
    }

    /// <summary>Resize the embedded Snes9x window to fill the host panel.</summary>
    public void FitToHost()
    {
        if (_snes9xHwnd == IntPtr.Zero || _hostHwnd == IntPtr.Zero) return;

        // Get host client rect
        GetClientRect(_hostHwnd, out RECT rc);
        int w = rc.Right  - rc.Left;
        int h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return;

        MoveWindow(_snes9xHwnd, 0, 0, w, h, true);
    }

    // -------------------------------------------------------------------------
    // Keyboard forwarding  (WPF keyboard shortcuts pass through to Snes9x)
    // -------------------------------------------------------------------------
    public void ForwardKey(System.Windows.Input.Key key, bool isDown)
    {
        if (_snes9xHwnd == IntPtr.Zero) return;
        uint msg     = isDown ? 0x0100u : 0x0101u; // WM_KEYDOWN / WM_KEYUP
        IntPtr vk    = (IntPtr)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        PostMessage(_snes9xHwnd, msg, vk, IntPtr.Zero);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static IntPtr CreateHostWindow(IntPtr parent)
    {
        // We create a plain transparent Win32 static window.
        // WPF's HwndHost will manage its lifetime.
        const uint WS_CHILD_VISIBLE = 0x50000000;
        return CreateWindowEx(0, "STATIC", "", WS_CHILD_VISIBLE,
            0, 0, 800, 600, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    private static async Task<IntPtr> WaitForMainWindowAsync(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            if (process.HasExited) return IntPtr.Zero;

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                return process.MainWindowHandle;
        }
        return IntPtr.Zero;
    }

    private static void ReparentWindow(IntPtr child, IntPtr newParent)
    {
        // Strip title bar / resize border from the embedded window
        int style = GetWindowLong(child, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME);
        style |= WS_CHILD;
        SetWindowLong(child, GWL_STYLE, style);

        SetParent(child, newParent);
        ShowWindow(child, SW_SHOW);
    }
}
