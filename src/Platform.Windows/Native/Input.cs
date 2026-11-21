using System.Runtime.InteropServices;

namespace EasyMouse.Platform.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
internal struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
internal struct INPUT
{
    public uint type;
    public INPUTUNION u;
}

internal static class InputConst
{
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const ushort VK_BACK = 0x08;
    public const ushort VK_TAB = 0x09;
    public const ushort VK_RETURN = 0x0D;
}

internal static partial class NativeInput
{
    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    /// <summary>Injects the whole batch with one SendInput call. Throws if Windows injected fewer events than asked.</summary>
    public static unsafe void Send(ReadOnlySpan<INPUT> inputs)
    {
        if (inputs.IsEmpty) return;
        fixed (INPUT* p = inputs)
        {
            var sent = SendInput((uint)inputs.Length, p, sizeof(INPUT));
            if (sent != (uint)inputs.Length)
            {
                // 0 with ERROR_ACCESS_DENIED = blocked by UIPI (elevated foreground window) or another desktop.
                throw new InputInjectionException(inputs.Length, (int)sent, Marshal.GetLastPInvokeError());
            }
        }
    }
}

/// <summary>SendInput injected fewer events than requested (typically UIPI blocking an elevated window).</summary>
public sealed class InputInjectionException(int requested, int injected, int win32Error)
    : Exception($"SendInput injected {injected} of {requested} events (Win32 error {win32Error}).")
{
    public int Requested { get; } = requested;
    public int Injected { get; } = injected;
    public int Win32Error { get; } = win32Error;
}
