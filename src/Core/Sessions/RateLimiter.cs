namespace EasyMouse.Core.Sessions;

/// <summary>Token bucket. Not thread-safe by itself; each session owns one and uses it from its receive loop.</summary>
public sealed class RateLimiter
{
    private readonly TimeProvider _time;
    private readonly double _perSecond;
    private readonly double _burst;
    private double _tokens;
    private long _lastTimestamp;

    public RateLimiter(double perSecond, int burst, TimeProvider time)
    {
        _time = time;
        _perSecond = perSecond;
        _burst = burst;
        _tokens = burst;
        _lastTimestamp = time.GetTimestamp();
    }

    public bool TryTake()
    {
        var now = _time.GetTimestamp();
        _tokens = Math.Min(_burst, _tokens + _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds * _perSecond);
        _lastTimestamp = now;
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}
