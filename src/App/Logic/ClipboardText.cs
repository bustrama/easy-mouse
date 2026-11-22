using System.Runtime.InteropServices;
using System.Windows;

namespace EasyMouse.App.Logic;

internal static class ClipboardText
{
    /// <summary>Copies text; the clipboard may be locked by another process, which is not an error worth crashing for.</summary>
    public static bool TrySet(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (Exception ex) when (ex is COMException or ExternalException) { return false; }
    }
}
