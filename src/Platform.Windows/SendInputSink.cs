using System.Runtime.InteropServices;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Platform.Windows.Native;

namespace EasyMouse.Platform.Windows;

/// <summary>
/// Injects input with SendInput: one call per logical action. Remembers which mouse buttons and keys are down so
/// <see cref="ReleaseAll"/> (call it when the phone disconnects) and <see cref="Dispose"/> never leave anything stuck.
/// Cannot reach elevated windows, the UAC prompt or the lock screen; those calls throw <see cref="InputInjectionException"/>.
/// </summary>
public sealed class SendInputSink : IInputSink, IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<MouseButton> _buttons = [];
    private readonly HashSet<ushort> _keys = [];
    private bool _disposed;

    public void MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            NativeInput.Send([InputBuilder.MouseMove(dx, dy)]);
        }
    }

    public void Button(MouseButton button, bool down)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            NativeInput.Send([InputBuilder.MouseButton(button, down)]);
            if (down) _buttons.Add(button); else _buttons.Remove(button);
        }
    }

    public void Wheel(int dx, int dy)
    {
        Span<INPUT> buffer = stackalloc INPUT[2];
        var n = InputBuilder.Wheel(dx, dy, buffer);
        if (n == 0) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            NativeInput.Send(buffer[..n]);
        }
    }

    public void Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var events = InputBuilder.Text(text);
        if (events.Count == 0) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            NativeInput.Send(CollectionsMarshal.AsSpan(events));
        }
    }

    public void Key(ushort virtualKey, KeyAction action)
    {
        var down = (action & KeyAction.Down) != 0;
        var up = (action & KeyAction.Up) != 0;
        if (!down && !up) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            Span<INPUT> buffer = stackalloc INPUT[2];
            var n = 0;
            if (down) buffer[n++] = InputBuilder.Key(virtualKey, true);
            if (up) buffer[n++] = InputBuilder.Key(virtualKey, false);
            NativeInput.Send(buffer[..n]);
            if (up) _keys.Remove(virtualKey); else _keys.Add(virtualKey);
        }
    }

    /// <summary>Lifts every mouse button and key this sink pressed, in one batch. Safe to call at any time.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            if (_buttons.Count == 0 && _keys.Count == 0) return;
            var batch = new List<INPUT>(_buttons.Count + _keys.Count);
            foreach (var b in _buttons) batch.Add(InputBuilder.MouseButton(b, false));
            foreach (var k in _keys) batch.Add(InputBuilder.Key(k, false));
            // Forget first: if injection is blocked (secure desktop), retrying would not help.
            _buttons.Clear();
            _keys.Clear();
            NativeInput.Send(CollectionsMarshal.AsSpan(batch));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { ReleaseAll(); }
            finally { _disposed = true; }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
