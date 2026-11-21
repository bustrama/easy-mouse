namespace EasyMouse.App.Logic;

/// <summary>The "Code is valid for 1:48" countdown of the QR window.</summary>
public static class Countdown
{
    public static TimeSpan Remaining(DateTimeOffset expiresAt, DateTimeOffset now)
        => expiresAt > now ? expiresAt - now : TimeSpan.Zero;

    /// <summary><c>m:ss</c>, rounded up so the text never shows 0:00 while the code is still valid.</summary>
    public static string Format(TimeSpan remaining)
    {
        int seconds = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
