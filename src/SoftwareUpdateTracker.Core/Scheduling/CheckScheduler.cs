namespace SoftwareUpdateTracker.Core.Scheduling;

public enum CheckTrigger
{
    Startup,
    Scheduled,
    Retry,
    Resumed,
    Manual,
    FlyoutOpened,
}

// One timer for the next check, no polling. Answer every CheckDue with Finished.
public sealed class CheckScheduler : IDisposable
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private TimeSpan _interval;
    private DateTimeOffset _dueAt;
    private CheckTrigger _dueReason = CheckTrigger.Startup;
    private DateTimeOffset? _lastAttempt;
    private int _failures;
    private bool _running;
    private bool _online = true;
    private bool _batterySaver;
    private bool _disposed;

    public CheckScheduler(TimeProvider time, TimeSpan interval)
    {
        _time = time;
        _interval = interval;
        _dueAt = time.GetUtcNow() + StartupDelay;
        _timer = time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_gate) Plan();
    }

    public event EventHandler<CheckTrigger>? CheckDue;

    // Null while a check runs.
    public DateTimeOffset? NextCheck
    {
        get { lock (_gate) return _running ? null : _dueAt; }
    }

    public void CheckNow()
    {
        CheckTrigger? due;
        lock (_gate) due = Begin(CheckTrigger.Manual);
        Raise(due);
    }

    // Refreshes data older than StaleAfter; skipped quietly while offline.
    public void FlyoutOpened()
    {
        CheckTrigger? due = null;
        lock (_gate)
        {
            var fresh = _lastAttempt is { } last && _time.GetUtcNow() - last <= StaleAfter;
            if (_online && !fresh) due = Begin(CheckTrigger.FlyoutOpened);
        }
        Raise(due);
    }

    // Timed checks wait while offline or in Battery saver, then run once both clear.
    public void SetConditions(bool online, bool batterySaver)
    {
        CheckTrigger? due;
        lock (_gate)
        {
            _online = online;
            _batterySaver = batterySaver;
            due = Plan();
        }
        Raise(due);
    }

    public void SetInterval(TimeSpan interval)
    {
        CheckTrigger? due;
        lock (_gate)
        {
            _interval = interval;
            if (_dueReason == CheckTrigger.Scheduled && _lastAttempt is { } last) _dueAt = last + interval;
            due = Plan();
        }
        Raise(due);
    }

    // After sleep or a clock change: a check that came due runs a minute later.
    public void Resumed()
    {
        CheckTrigger? due;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_running && _dueAt <= now)
            {
                _dueAt = now + StartupDelay;
                _dueReason = CheckTrigger.Resumed;
            }
            due = Plan();
        }
        Raise(due);
    }

    public void Finished(bool succeeded)
    {
        CheckTrigger? due;
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            var now = _time.GetUtcNow();
            _lastAttempt = now;
            if (!succeeded && _failures < RetryDelays.Count)
            {
                _dueAt = now + RetryDelays[_failures++];
                _dueReason = CheckTrigger.Retry;
            }
            else
            {
                _failures = 0;
                _dueAt = now + _interval;
                _dueReason = CheckTrigger.Scheduled;
            }
            due = Plan();
        }
        Raise(due);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }

    private void OnTimer()
    {
        CheckTrigger? due;
        lock (_gate) due = Plan();
        Raise(due);
    }

    // Arms the timer, or starts the check when it's already due. Never arms a zero delay.
    private CheckTrigger? Plan()
    {
        if (_disposed || _running) return null;
        if (!_online || _batterySaver)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return null;
        }
        var wait = _dueAt - _time.GetUtcNow();
        if (wait <= TimeSpan.Zero) return Begin(_dueReason);
        _timer.Change(wait, Timeout.InfiniteTimeSpan);
        return null;
    }

    private CheckTrigger? Begin(CheckTrigger trigger)
    {
        if (_disposed || _running) return null;
        _running = true;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return trigger;
    }

    private void Raise(CheckTrigger? trigger)
    {
        if (trigger is { } t) CheckDue?.Invoke(this, t);
    }
}
