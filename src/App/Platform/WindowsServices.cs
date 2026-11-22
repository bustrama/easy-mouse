using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Interop;
using EasyMouse.App.Logic;
using EasyMouse.Core.Devices;
using Microsoft.Win32;

namespace EasyMouse.App.Platform;

/// <summary>Device secrets at rest: DPAPI, current user scope.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly byte[] Entropy;

    public DpapiSecretProtector(string purpose = "EasyMouse.DeviceStore.v1") => Entropy = System.Text.Encoding.UTF8.GetBytes(purpose);

    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>"Start with Windows": a value in HKCU Run (no admin rights needed).</summary>
public static class Autostart
{
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutostartCommand.RunKeyPath);
        return key?.GetValue(AutostartCommand.ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AutostartCommand.RunKeyPath);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the program path.");
            key.SetValue(AutostartCommand.ValueName, AutostartCommand.Build(exe));
        }
        else key.DeleteValue(AutostartCommand.ValueName, throwOnMissingValue: false);
    }
}

/// <summary>A global hotkey (RegisterHotKey) on a hidden message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int Id = 0x4D45; // "ME"
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;

    public event EventHandler? Pressed;

    /// <summary>Registers the hotkey, replacing any earlier one. False if another program already owns the combination.</summary>
    public bool Register(Hotkey hotkey)
    {
        Unregister();
        _source ??= CreateSource();
        bool ok = RegisterHotKey(_source.Handle, Id, (uint)hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey);
        Registered = ok;
        return ok;
    }

    public bool Registered { get; private set; }

    public void Unregister()
    {
        if (_source is not null && Registered) UnregisterHotKey(_source.Handle, Id);
        Registered = false;
    }

    private HwndSource CreateSource()
    {
        var source = new HwndSource(new HwndSourceParameters("EasyMouseHotkey") { ParentWindow = HwndMessage, Width = 0, Height = 0 });
        source.AddHook(WndProc);
        return source;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>Elevation for exactly one command: making the network Private. Everything else in the app runs unelevated.</summary>
public static class NetworkProfileFixer
{
    /// <summary>
    /// Runs <c>Set-NetConnectionProfile</c> for one interface in an elevated PowerShell (UAC prompt).
    /// Returns false if the user declined the prompt.
    /// </summary>
    public static bool MakePrivate(int interfaceIndex)
    {
        if (interfaceIndex <= 0) throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        // Full path: a bare name is searched in the app folder and PATH, and this process is started elevated.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            // Only an integer is interpolated into the command line: nothing from the network or settings.
            Arguments = $"-NoProfile -WindowStyle Hidden -Command \"Set-NetConnectionProfile -InterfaceIndex {interfaceIndex} -NetworkCategory Private\"",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(psi);
            process?.WaitForExit(30_000);
            return process is { HasExited: true, ExitCode: 0 };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the user said no at UAC
        {
            return false;
        }
    }
}

/// <summary>One running copy: a named mutex, plus an event so a second launch can ask the first to show its QR window.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _quit;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, EventWaitHandle show, EventWaitHandle quit)
    {
        _mutex = mutex;
        _show = show;
        _quit = quit;
    }

    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out bool created);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Quit");
        if (created) return new SingleInstance(mutex, show, quit);
        show.Set(); // tell the running copy
        mutex.Dispose();
        show.Dispose();
        quit.Dispose();
        return null;
    }

    /// <summary>Asks a running copy to exit (the installer and uninstaller do this before touching files). False if none is running.</summary>
    public static bool RequestQuit(string name)
    {
        if (!EventWaitHandle.TryOpenExisting($@"Local\{name}.Quit", out var quit)) return false;
        using (quit) return quit.Set();
    }

    /// <summary>Calls <paramref name="onQuitRequested"/> (on a pool thread) when <see cref="RequestQuit"/> was called by another process.</summary>
    public void ListenForQuit(Action onQuitRequested)
    {
        var token = _cts.Token;
        _ = Task.Run(() =>
        {
            var handles = new[] { _quit, token.WaitHandle };
            if (WaitHandle.WaitAny(handles) == 0) onQuitRequested();
        });
    }

    /// <summary>Calls <paramref name="onShowRequested"/> (on a pool thread) each time another copy was started.</summary>
    public void ListenForShow(Action onShowRequested)
    {
        var token = _cts.Token;
        _ = Task.Run(() =>
        {
            var handles = new[] { _show, token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0) onShowRequested();
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _show.Dispose();
        _quit.Dispose();
        _cts.Dispose();
    }
}
