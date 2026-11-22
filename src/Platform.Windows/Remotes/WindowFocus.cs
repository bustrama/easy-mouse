using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EasyMouse.Remotes.Execution;
using EasyMouse.Remotes.Model;

namespace EasyMouse.Platform.Windows.Remotes;

/// <summary>A top-level window as far as "bring it to the front" is concerned.</summary>
public sealed record WindowInfo(nint Handle, uint ProcessId, string ExeName, string Title);

/// <summary>The windowing calls the focus executor needs. The real one talks to Win32; tests use a fake.</summary>
public interface IWindowSystem
{
    /// <summary>Visible, un-owned, non-tool, non-cloaked top-level windows with a title, front-most first.</summary>
    IReadOnlyList<WindowInfo> MainWindows();
    nint Foreground();
    bool IsMinimized(nint handle);
    /// <summary>Un-minimises the window (only called for minimised windows, so maximised or fullscreen players keep their state).</summary>
    void Restore(nint handle);
    /// <summary>Asks Windows to make it the foreground window. May be refused; the caller checks <see cref="Foreground"/>.</summary>
    void RequestForeground(nint handle);
}

/// <summary>"stremio*.exe" style file-name patterns (the validator allows letters, digits, . _ - and the wildcards * and ?).</summary>
public static class ProcessPattern
{
    public static bool IsMatch(string pattern, string exeFileName)
    {
        if (Test(pattern, exeFileName)) return true;
        // "vlc" should also match vlc.exe: a pattern without an extension is compared with the name minus ".exe".
        return !pattern.Contains('.') && exeFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && Test(pattern, exeFileName[..^4]);
    }

    private static bool Test(string pattern, string name)
        => Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
}

/// <summary>
/// Brings the remote's target program to the front. Fails closed: when the window did not become the foreground window
/// the action fails with "failed", so the key presses that follow never reach another program.
/// </summary>
public sealed class FocusExecutor(IWindowSystem windows, TimeSpan? wait = null, TimeProvider? time = null) : IActionExecutor<FocusAction>
{
    private readonly TimeSpan _wait = wait ?? TimeSpan.FromMilliseconds(600);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task ExecuteAsync(FocusAction action, ActionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.TargetProcess)) throw new ActionFailedException("unsupported", "remote has no target program");

        var matches = windows.MainWindows().Where(w => ProcessPattern.IsMatch(context.TargetProcess, w.ExeName)).ToList();
        if (matches.Count == 0) throw new ActionFailedException("failed", "the program is not running or has no window");
        var fg = windows.Foreground();
        var target = matches.FirstOrDefault(w => w.Handle == fg) ?? matches[0]; // already in front wins; else the top-most one
        if (target.Handle == fg) return;

        if (windows.IsMinimized(target.Handle)) windows.Restore(target.Handle);
        windows.RequestForeground(target.Handle);

        var deadline = _time.GetUtcNow() + _wait;
        while (windows.Foreground() != target.Handle)
        {
            if (_time.GetUtcNow() >= deadline)
                throw new ActionFailedException("failed", "Windows did not let the program come to the front");
            await Task.Delay(TimeSpan.FromMilliseconds(25), _time, cancellationToken).ConfigureAwait(false);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(60), _time, cancellationToken).ConfigureAwait(false); // let the window start taking keys
    }
}

/// <summary>The real thing, on user32.</summary>
public sealed unsafe class Win32WindowSystem : IWindowSystem
{
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x2;

    public IReadOnlyList<WindowInfo> MainWindows()
    {
        var list = new List<nint>();
        var handle = GCHandle.Alloc(list);
        try { WinWindows.EnumWindows(&Collect, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }

        var result = new List<WindowInfo>();
        foreach (var h in list)
        {
            if (!WinWindows.IsWindowVisible(h) || WinWindows.GetWindow(h, WinWindows.GW_OWNER) != 0) continue;
            if ((WinWindows.GetWindowLongPtr(h, WinWindows.GWL_EXSTYLE) & (WinWindows.WS_EX_TOOLWINDOW | WinWindows.WS_EX_NOACTIVATE)) != 0) continue;
            if (WinWindows.DwmGetWindowAttribute(h, WinWindows.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            var title = TitleOf(h);
            if (title.Length == 0) continue;
            Native.Win32.GetWindowThreadProcessId(h, out var pid);
            var exe = ExeOf(pid);
            if (exe is null) continue;
            result.Add(new WindowInfo(h, pid, exe, title));
        }
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Collect(nint hwnd, nint state)
    {
        ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(hwnd);
        return 1;
    }

    private static string TitleOf(nint h)
    {
        var len = WinWindows.GetWindowTextLength(h);
        if (len <= 0) return "";
        var buffer = new char[len + 1];
        fixed (char* p = buffer) len = WinWindows.GetWindowText(h, p, buffer.Length);
        return new string(buffer, 0, Math.Max(0, len));
    }

    private static string? ExeOf(uint pid)
    {
        var process = Native.Win32.OpenProcess(WinWindows.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            fixed (char* p = buffer)
                return WinWindows.QueryFullProcessImageName(process, 0, p, ref size) ? Path.GetFileName(new string(p, 0, (int)size)) : null;
        }
        finally { Native.Win32.CloseHandle(process); }
    }

    public nint Foreground() => Native.Win32.GetForegroundWindow();

    public bool IsMinimized(nint handle) => WinWindows.IsIconic(handle);

    public void Restore(nint handle) => WinWindows.ShowWindow(handle, WinWindows.SW_RESTORE);

    public void RequestForeground(nint handle)
    {
        // Windows only lets a process that owns the foreground (or just received input) take it. Attaching to the
        // foreground window's input queue is the documented way to be treated as such; a tapped Alt key is the fallback.
        var fg = Native.Win32.GetForegroundWindow();
        var fgThread = fg == 0 ? 0 : Native.Win32.GetWindowThreadProcessId(fg, out _);
        var me = WinWindows.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && WinWindows.AttachThreadInput(me, fgThread, true);
        try
        {
            WinWindows.BringWindowToTop(handle);
            if (WinWindows.SetForegroundWindow(handle)) return;
            WinWindows.keybd_event(VK_MENU, 0, 0, 0);
            WinWindows.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
            WinWindows.SetForegroundWindow(handle);
        }
        finally
        {
            if (attached) WinWindows.AttachThreadInput(me, fgThread, false);
        }
    }
}
