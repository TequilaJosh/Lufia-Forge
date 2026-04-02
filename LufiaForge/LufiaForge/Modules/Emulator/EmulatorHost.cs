using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LufiaForge.Modules.Emulator;

/// <summary>
/// HwndHost that launches Snes9x 1.62.3 Win32 as a child process and
/// reparents its main window into the WPF layout.
///
/// Focus strategy: Snes9x pauses when it receives WM_KILLFOCUS. We combat
/// this by (a) sending WM_ACTIVATE=WA_ACTIVE on a 200ms keep-alive timer so
/// it continually believes it is the active window, and (b) intercepting
/// WM_KILLFOCUS sent to the host HWND and suppressing it.
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
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // Window styles / messages
    private const int GWL_STYLE     = -16;
    private const int WS_CHILD      = 0x40000000;
    private const int WS_CAPTION    = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int SW_SHOW       = 5;

    private const uint WM_ACTIVATE    = 0x0006;
    private const uint WM_SETFOCUS    = 0x0007;
    private const uint WM_KILLFOCUS   = 0x0008;
    private const uint WM_NCACTIVATE  = 0x0086;
    private const uint WM_KEYDOWN     = 0x0100;
    private const uint WM_KEYUP       = 0x0101;
    private const uint WM_SYSKEYDOWN  = 0x0104;
    private const IntPtr WA_ACTIVE    = (IntPtr)1;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private Process?         _process;
    private IntPtr           _snes9xHwnd = IntPtr.Zero;
    private IntPtr           _hostHwnd   = IntPtr.Zero;
    private DispatcherTimer? _keepAliveTimer;

    public string Snes9xPath { get; set; } = "";
    public string RomPath    { get; set; } = "";

    public bool IsRunning  => _process != null && !_process.HasExited;
    public bool IsAttached => IsRunning && _snes9xHwnd != IntPtr.Zero && IsWindow(_snes9xHwnd);

    public event EventHandler?         EmulatorExited;
    public event EventHandler<string>? StatusChanged;

    // -------------------------------------------------------------------------
    // HwndHost overrides
    // -------------------------------------------------------------------------
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hostHwnd = CreateWindowEx(0, "STATIC", "", 0x50000000 /* WS_CHILD|WS_VISIBLE */,
            0, 0, 800, 600, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _hostHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Shutdown();
    }

    // Intercept messages sent to our host HWND — suppress WM_KILLFOCUS so it
    // never propagates into the embedded Snes9x child window.
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch ((uint)msg)
        {
            case WM_KILLFOCUS:
                // Snes9x child window would inherit this — suppress it and
                // immediately re-activate instead
                if (_snes9xHwnd != IntPtr.Zero)
                    PostMessage(_snes9xHwnd, WM_ACTIVATE, WA_ACTIVE, IntPtr.Zero);
                handled = true;
                return IntPtr.Zero;

            case WM_NCACTIVATE:
                // Keep Snes9x title (if any visible) painted as active
                if (_snes9xHwnd != IntPtr.Zero)
                    PostMessage(_snes9xHwnd, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
                break;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public async Task<bool> LaunchAsync()
    {
        if (string.IsNullOrWhiteSpace(Snes9xPath) || !File.Exists(Snes9xPath))
        {
            StatusChanged?.Invoke(this, "Snes9x path not configured.");
            return false;
        }

        Shutdown();
        StatusChanged?.Invoke(this, "Launching Snes9x...");

        var args = string.IsNullOrWhiteSpace(RomPath) ? "" : $"\"{RomPath}\"";

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
            StopKeepAlive();
            EmulatorExited?.Invoke(this, EventArgs.Empty);
        };

        _snes9xHwnd = await WaitForMainWindowAsync(_process, TimeSpan.FromSeconds(10));
        if (_snes9xHwnd == IntPtr.Zero)
        {
            StatusChanged?.Invoke(this, "Snes9x launched but window not found.");
            return false;
        }

        if (_hostHwnd != IntPtr.Zero)
        {
            ReparentWindow(_snes9xHwnd, _hostHwnd);
            FitToHost();
        }

        StartKeepAlive();
        StatusChanged?.Invoke(this, "Snes9x running.");
        return true;
    }

    public void Shutdown()
    {
        StopKeepAlive();
        if (_process == null) return;
        try
        {
            if (!_process.HasExited) { _process.Kill(); _process.WaitForExit(2000); }
        }
        catch { }
        finally
        {
            _process.Dispose();
            _process    = null;
            _snes9xHwnd = IntPtr.Zero;
        }
    }

    /// <summary>Resize the embedded window to fill the host panel.</summary>
    public void FitToHost()
    {
        if (_snes9xHwnd == IntPtr.Zero || _hostHwnd == IntPtr.Zero) return;
        GetClientRect(_hostHwnd, out RECT rc);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w > 0 && h > 0) MoveWindow(_snes9xHwnd, 0, 0, w, h, true);
    }

    // -------------------------------------------------------------------------
    // Keyboard forwarding
    // -------------------------------------------------------------------------

    /// <summary>Forward a WPF Key as WM_KEYDOWN or WM_KEYUP to Snes9x.</summary>
    public void ForwardKey(System.Windows.Input.Key key, bool isDown)
    {
        if (_snes9xHwnd == IntPtr.Zero) return;
        uint msg  = isDown ? WM_KEYDOWN : WM_KEYUP;
        int  vk   = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        PostMessage(_snes9xHwnd, msg, (IntPtr)vk, MakeLParam(vk, isDown));
    }

    private static IntPtr MakeLParam(int vk, bool isDown)
    {
        // lParam bit layout for WM_KEYDOWN: bits 0-15 repeat count, 24 extended, 31 transition
        int repeat      = 1;
        int extended    = (vk >= 0x21 && vk <= 0x2E) || vk == 0x0D ? 1 : 0;
        int transition  = isDown ? 0 : 1;
        int prevState   = isDown ? 0 : 1;
        return (IntPtr)(repeat | (extended << 24) | (prevState << 30) | (transition << 31));
    }

    // -------------------------------------------------------------------------
    // Keep-alive (prevent Snes9x from pausing on focus loss)
    // -------------------------------------------------------------------------

    private void StartKeepAlive()
    {
        StopKeepAlive();
        _keepAliveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _keepAliveTimer.Tick += KeepAlive_Tick;
        _keepAliveTimer.Start();
    }

    private void StopKeepAlive()
    {
        if (_keepAliveTimer == null) return;
        _keepAliveTimer.Stop();
        _keepAliveTimer.Tick -= KeepAlive_Tick;
        _keepAliveTimer = null;
    }

    private void KeepAlive_Tick(object? sender, EventArgs e)
    {
        if (_snes9xHwnd == IntPtr.Zero || !IsWindow(_snes9xHwnd)) return;
        // Send WM_ACTIVATE = WA_ACTIVE so Snes9x believes it is always the active window.
        // This prevents its pause-on-focus-loss logic from triggering.
        PostMessage(_snes9xHwnd, WM_ACTIVATE, WA_ACTIVE, IntPtr.Zero);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static async Task<IntPtr> WaitForMainWindowAsync(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            if (process.HasExited) return IntPtr.Zero;
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                return process.MainWindowHandle;
        }
        return IntPtr.Zero;
    }

    private static void ReparentWindow(IntPtr child, IntPtr newParent)
    {
        int style = GetWindowLong(child, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME);
        style |= WS_CHILD;
        SetWindowLong(child, GWL_STYLE, style);
        SetParent(child, newParent);
        ShowWindow(child, SW_SHOW);
    }
}
