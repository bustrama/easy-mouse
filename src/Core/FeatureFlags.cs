namespace EasyMouse.Core;

/// <summary>Compile-time switches for features that ship disabled.</summary>
public static class FeatureFlags
{
    /// <summary>
    /// DEPRECATED: disabled for 0.1, scheduled for removal (see docs/remotes.md). When false the app loads no remotes, shows no
    /// Remotes window or tray entry, pushes no layouts, and refuses every <c>invoke</c> from a phone. Not <c>const</c>, so the
    /// gated code still compiles without unreachable-code warnings.
    /// </summary>
    public static readonly bool Remotes = false;
}
