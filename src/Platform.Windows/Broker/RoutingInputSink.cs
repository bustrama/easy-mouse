using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Platform.Windows.Native;

namespace EasyMouse.Platform.Windows.Broker;

/// <summary>
/// Decides, per action, whether the normal sink or the broker takes it. The broker is used only when
/// <paramref name="useBroker"/> says so (the setting is on and the foreground window or desktop is out of reach of normal input);
/// everything else goes through the unprivileged sink exactly as before. When the route changes, whatever the previous side still
/// holds down is lifted first, so a key is never left pressed on the side that is no longer used.
/// </summary>
public sealed class RoutingInputSink(IInputSink normal, IInputSink broker, Func<InputKind, bool> useBroker) : IInputSink, IDisposable
{
    private readonly object _gate = new();
    private IInputSink? _last;

    public void MoveRelative(int dx, int dy) => Pick(InputKind.Pointer).MoveRelative(dx, dy);
    public void Button(MouseButton button, bool down) => Pick(InputKind.Pointer).Button(button, down);
    public void Wheel(int dx, int dy) => Pick(InputKind.Pointer).Wheel(dx, dy);
    public void Text(string text) => Pick(InputKind.Keyboard).Text(text);
    public void Key(ushort virtualKey, KeyAction action) => Pick(InputKind.Keyboard).Key(virtualKey, action);

    public void ReleaseAll()
    {
        InputBlockedException? first = null;
        foreach (var sink in new[] { normal, broker })
        {
            try { sink.ReleaseAll(); }
            catch (InputBlockedException ex) { first ??= ex; }
        }
        lock (_gate) _last = null;
        if (first is not null) throw first;
    }

    private IInputSink Pick(InputKind kind)
    {
        var target = useBroker(kind) ? broker : normal;
        IInputSink? previous;
        lock (_gate)
        {
            previous = _last;
            _last = target;
        }
        if (previous is not null && !ReferenceEquals(previous, target))
        {
            try { previous.ReleaseAll(); }
            catch (InputBlockedException) { /* that side is unreachable right now; nothing more can be done from here */ }
        }
        return target;
    }

    public void Dispose()
    {
        (normal as IDisposable)?.Dispose();
        (broker as IDisposable)?.Dispose();
    }
}

/// <summary>Which kind of target an action has: keys go to the foreground window, the mouse to the window under the pointer.</summary>
public enum InputKind { Keyboard, Pointer }

/// <summary>
/// "Does this input need the broker right now?": the feature is on AND normal input would not get through: the foreground window is
/// elevated (keys), the window under the pointer is elevated (mouse), or a secure desktop such as the UAC prompt is showing.
/// Windows drops input to an elevated window silently, so the answer has to come from looking, not from an error.
/// </summary>
public sealed class BrokerRoute(
    Func<HostStatus?> status,
    Func<bool> brokerEnabled,
    Func<long>? nowMilliseconds = null,
    Func<bool>? secureDesktopUp = null,
    Func<bool>? pointerWindowBlocked = null)
{
    private static readonly long CacheMilliseconds = 100;
    private readonly Func<long> _now = nowMilliseconds ?? (() => Environment.TickCount64);
    private readonly Func<bool> _secureDesktopUp = secureDesktopUp ?? HostStatusMonitor.IsInputDesktopUnavailable;
    private readonly Func<bool> _pointerWindowBlocked = pointerWindowBlocked ?? HostStatusMonitor.IsCursorWindowBlocked;
    private long? _desktopCheckedAt, _pointerCheckedAt;
    private bool _desktopBlocked, _pointerBlocked;

    public bool UseBroker(InputKind kind)
    {
        if (!brokerEnabled()) return false;
        var s = status();
        if (s is null || s.Locked) return false; // locked: the session drops input anyway, and the broker refuses it too
        if (s.Elevated || Cached(ref _desktopCheckedAt, ref _desktopBlocked, _secureDesktopUp)) return true;
        return kind == InputKind.Pointer && Cached(ref _pointerCheckedAt, ref _pointerBlocked, _pointerWindowBlocked);
    }

    private bool Cached(ref long? checkedAt, ref bool value, Func<bool> probe)
    {
        var now = _now();
        if (checkedAt is not { } at || now - at >= CacheMilliseconds)
        {
            value = probe();
            checkedAt = now;
        }
        return value;
    }
}
