namespace EasyMouse.Core.Abstractions;

/// <summary>
/// The PC refused injected input right now: typically an elevated foreground window (UIPI), the UAC prompt or the
/// lock screen. It is an expected condition, not a bug: the session drops that input and stays connected (the state
/// frame already tells the phone, see <c>host.elevated</c>).
/// </summary>
public class InputBlockedException(string message) : Exception(message);
