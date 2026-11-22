using System.ComponentModel;
using System.Runtime.InteropServices;
using EasyMouse.Core.Abstractions;
using EasyMouse.Platform.Windows.Native;

namespace EasyMouse.Platform.Windows;

/// <summary>
/// Keeps a <see cref="HostStatus"/> current: Locked from session lock/unlock notifications (WTS), Elevated when the
/// foreground window belongs to a process more privileged than we are, so UIPI would drop our input. It runs a hidden
/// window with a message loop on its own thread. Never needs elevation itself.
/// </summary>
public sealed class HostStatusMonitor : IDisposable
{
    [ThreadStatic] private static HostStatusMonitor? t_current;

    private readonly HostStatus _status;
    private readonly bool _selfElevated;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Exception? _startupError;
    private nint _hwnd;
    private bool _locked;
    private bool _elevated;
    private bool _disposed;

    public HostStatusMonitor(HostStatus status)
    {
        _status = status;
        _selfElevated = QueryElevation(Win32.GetCurrentProcess()) != ProcessElevation.NotElevated;
        _thread = new Thread(Run) { IsBackground = true, Name = "EasyMouse.HostStatusMonitor" };
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null) throw new InvalidOperationException("Host status monitor failed to start.", _startupError);
    }

    /// <summary>True when the foreground window belongs to a process we cannot inject input into.</summary>
    public static bool IsForegroundBlocked(bool selfElevated) => ElevationPolicy.BlocksInput(selfElevated, ForegroundElevation());

    private unsafe void Run()
    {
        t_current = this;
        var className = "EasyMouse.HostStatusMonitor." + Guid.NewGuid().ToString("N");
        nint hook = 0;
        var registered = false;
        var instance = Win32.GetModuleHandleW(0);
        fixed (char* name = className)
        {
            try
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = instance,
                    lpszClassName = name,
                };
                if (Win32.RegisterClassEx(wc) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                registered = true;
                // A hidden top-level window (not message-only) so WM_WTSSESSION_CHANGE is delivered reliably.
                _hwnd = Win32.CreateWindowEx(0x80 /* WS_EX_TOOLWINDOW */, name, null, 0, 0, 0, 0, 0, 0, 0, instance, 0);
                if (_hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (!Win32.WTSRegisterSessionNotification(_hwnd, 0 /* NOTIFY_FOR_THIS_SESSION */)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, 0, &ForegroundChanged, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
                if (hook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());

                _locked = IsInputDesktopUnavailable();
                _elevated = ElevationPolicy.BlocksInput(_selfElevated, ForegroundElevation());
                _status.Update(_locked, _elevated);
            }
            catch (Exception ex)
            {
                _startupError = ex;
            }
            finally
            {
                _ready.Set();
            }

            if (_startupError is null)
            {
                while (Win32.GetMessage(out var msg, 0, 0, 0) > 0) Win32.DispatchMessage(msg);
            }

            if (hook != 0) Win32.UnhookWinEvent(hook);
            if (_hwnd != 0)
            {
                Win32.WTSUnRegisterSessionNotification(_hwnd);
                Win32.DestroyWindow(_hwnd);
            }
            if (registered) Win32.UnregisterClass(name, instance);
        }
        t_current = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_WTSSESSION_CHANGE:
                if ((int)wParam == Win32.WTS_SESSION_LOCK) t_current?.Set(locked: true);
                else if ((int)wParam == Win32.WTS_SESSION_UNLOCK) t_current?.Set(locked: false);
                return 0;
            case Win32.WM_APP: // shutdown request from Dispose
                Win32.DestroyWindow(hwnd);
                return 0;
            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return 0;
            default:
                return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void ForegroundChanged(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
        => t_current?.Set(elevated: ElevationPolicy.BlocksInput(t_current._selfElevated, ForegroundElevation()));

    private void Set(bool? locked = null, bool? elevated = null)
    {
        if (locked is { } l) _locked = l;
        if (elevated is { } e) _elevated = e;
        else if (locked == false) _elevated = ElevationPolicy.BlocksInput(_selfElevated, ForegroundElevation());
        _status.Update(_locked, _elevated);
    }

    /// <summary>The input desktop cannot be opened while the workstation is locked or a secure desktop (UAC) is up.</summary>
    internal static bool IsInputDesktopUnavailable()
    {
        var desktop = Win32.OpenInputDesktop(0, false, Win32.DESKTOP_SWITCHDESKTOP);
        if (desktop == 0) return true;
        Win32.CloseDesktop(desktop);
        return false;
    }

    internal static ProcessElevation ForegroundElevation()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == 0) return ProcessElevation.NotElevated;
        if (Win32.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0) return ProcessElevation.NotElevated;
        return ElevationOfProcess(pid);
    }

    private static readonly Lazy<bool> s_selfElevated = new(() => QueryElevation(Win32.GetCurrentProcess()) != ProcessElevation.NotElevated);

    /// <summary>True when a click or wheel turn would land on a window of a process we cannot inject input into (UIPI drops it silently).</summary>
    internal static bool IsCursorWindowBlocked() => ElevationPolicy.BlocksInput(s_selfElevated.Value, CursorWindowElevation());

    /// <summary>Elevation of the process that owns the window under the mouse pointer. Mouse clicks and wheel turns land there, not on the foreground window.</summary>
    internal static ProcessElevation CursorWindowElevation()
    {
        if (!Win32.GetCursorPos(out var point)) return ProcessElevation.NotElevated;
        var hwnd = Win32.WindowFromPoint(point);
        if (hwnd == 0) return ProcessElevation.NotElevated;
        if (Win32.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0) return ProcessElevation.NotElevated;
        return ElevationOfProcess(pid);
    }

    internal static ProcessElevation ElevationOfProcess(uint pid)
    {
        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
        {
            // Access denied: protected or SYSTEM process. Any other failure: the process is already gone.
            return Marshal.GetLastPInvokeError() == Win32.ERROR_ACCESS_DENIED ? ProcessElevation.Inaccessible : ProcessElevation.NotElevated;
        }
        try { return QueryElevation(process); }
        finally { Win32.CloseHandle(process); }
    }

    private static ProcessElevation QueryElevation(nint process)
    {
        if (!Win32.OpenProcessToken(process, Win32.TOKEN_QUERY, out var token))
        {
            // A non-elevated caller cannot open the token of an elevated process: that is the signal we want.
            return Marshal.GetLastPInvokeError() == Win32.ERROR_ACCESS_DENIED ? ProcessElevation.Inaccessible : ProcessElevation.NotElevated;
        }
        try
        {
            if (!Win32.GetTokenInformation(token, Win32.TokenElevation, out var elevated, sizeof(uint), out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return elevated != 0 ? ProcessElevation.Elevated : ProcessElevation.NotElevated;
        }
        finally { Win32.CloseHandle(token); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != 0) Win32.PostMessage(_hwnd, Win32.WM_APP, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
