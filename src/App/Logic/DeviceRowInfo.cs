using EasyMouse.Core.Devices;

namespace EasyMouse.App.Logic;

public enum DeviceRowKind { Connected, Seen, ExpiresSoon }

/// <summary>A paired phone as the Devices page shows it, with the 90-day expiry worked out.</summary>
public sealed record DeviceRowInfo(
    string Id,
    string Name,
    DeviceRowKind Kind,
    int PairedDaysAgo,
    int LastSeenDaysAgo,
    int DaysUntilExpiry)
{
    /// <summary>Warn when this few days are left before an unused phone is forgotten.</summary>
    public const int SoonDays = 14;

    public bool Connected => Kind == DeviceRowKind.Connected;

    public static DeviceRowInfo From(Device device, DateTimeOffset now, TimeSpan expiry, bool connected)
    {
        int paired = WholeDays(now - device.CreatedUtc);
        int seen = WholeDays(now - device.LastSeenUtc);
        // Days left, counted in whole days that remain (a phone with 12 hours left shows 1 day, never 0 or negative).
        int left = (int)Math.Max(0, Math.Ceiling((expiry - (now - device.LastSeenUtc)).TotalDays));
        var kind = connected ? DeviceRowKind.Connected : left <= SoonDays ? DeviceRowKind.ExpiresSoon : DeviceRowKind.Seen;
        return new DeviceRowInfo(device.Id, device.Name, kind, paired, seen, left);
    }

    private static int WholeDays(TimeSpan span) => (int)Math.Max(0, Math.Floor(span.TotalDays));
}
