namespace KeyGrant.Licensing.Tests.Support;

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire only when the test moves time on (<see cref="Advance"/>),
/// in due order, as fake timers do. The licence clock is separate (<see cref="Harness.SetClock"/>).
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = new();
    private DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
    private long sequence;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return now;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (gate) return now.UtcTicks;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Whether a live timer falls due exactly <paramref name="dueIn"/> from now.</summary>
    public bool HasTimerDueIn(TimeSpan dueIn)
    {
        lock (gate) return timers.Exists(t => t.Due == now + dueIn);
    }

    public int LiveTimers
    {
        get
        {
            lock (gate) return timers.Count;
        }
    }

    /// <summary>Move time on, firing every timer that falls due on the way, in order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (gate) target = now + by;
        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(t => t.Due <= target).OrderBy(t => t.Due).ThenBy(t => t.Sequence).FirstOrDefault();
                if (next is null)
                {
                    now = target;
                    break;
                }
                if (next.Due > now) now = next.Due;
                if (next.Period is { } period && period > TimeSpan.Zero) next.Due += period;
                else timers.Remove(next);
            }
            next.Fire();
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan) return;
            timer.Due = now + dueTime;
            timer.Period = period == Timeout.InfiniteTimeSpan ? null : period;
            timer.Sequence = ++sequence;
            timers.Add(timer);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (gate) timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; set; }

        public TimeSpan? Period { get; set; }

        public long Sequence { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime, period);
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
