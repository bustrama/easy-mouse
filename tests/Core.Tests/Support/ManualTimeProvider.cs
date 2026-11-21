namespace EasyMouse.Core.Tests.Support;

/// <summary>Deterministic clock: time moves only through <see cref="Advance"/>; timers fire from there.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
    public override long GetTimestamp() { lock (_gate) return _timestamp; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _utcNow += by;
            _timestamp += by.Ticks;
            due = [.. _timers.Where(t => t.Due is { } d && d <= _utcNow)];
            foreach (var t in due) t.Due = null;
        }
        foreach (var t in due) t.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) { Due = null; return true; }
                Due = owner._utcNow + dueTime;
                owner._timers.Add(this);
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() { lock (owner._gate) owner._timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
