using Microsoft.Extensions.Time.Testing;
using SoftwareUpdateTracker.Core.Scheduling;
using Xunit;

namespace SoftwareUpdateTracker.Core.Tests.Scheduling;

public sealed class CheckSchedulerTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
    private readonly List<CheckTrigger> _checks = [];
    private readonly CheckScheduler _scheduler;

    public CheckSchedulerTests()
    {
        _scheduler = new CheckScheduler(_time, Interval);
        _scheduler.CheckDue += (_, trigger) => _checks.Add(trigger);
    }

    public void Dispose() => _scheduler.Dispose();

    private void Advance(double minutes) => _time.Advance(TimeSpan.FromMinutes(minutes));

    private void CompleteStartupCheck(bool succeeded = true)
    {
        Advance(1);
        _scheduler.Finished(succeeded);
    }

    [Fact]
    public void FirstCheck_RunsOneMinuteAfterStart()
    {
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(_checks);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void AfterACheck_TheNextWaitsTheInterval()
    {
        CompleteStartupCheck();
        Assert.Equal(_time.GetUtcNow() + Interval, _scheduler.NextCheck);
        _time.Advance(Interval - TimeSpan.FromSeconds(1));
        Assert.Equal([CheckTrigger.Startup], _checks);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Scheduled], _checks);
    }

    [Fact]
    public void NextCheck_IsNullWhileAChecksRuns()
    {
        Advance(1);
        Assert.Null(_scheduler.NextCheck);
        _scheduler.Finished(true);
        Assert.NotNull(_scheduler.NextCheck);
    }

    [Fact]
    public void FailedChecks_RetryAfter1_5And15Minutes_ThenWaitTheInterval()
    {
        CompleteStartupCheck(succeeded: false);
        foreach (var minutes in new[] { 1, 5, 15 })
        {
            Assert.Equal(_time.GetUtcNow().AddMinutes(minutes), _scheduler.NextCheck);
            Advance(minutes);
            _scheduler.Finished(false);
        }
        Assert.Equal(_time.GetUtcNow() + Interval, _scheduler.NextCheck);
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Retry, CheckTrigger.Retry, CheckTrigger.Retry], _checks);
    }

    [Fact]
    public void Success_ResetsTheRetries()
    {
        CompleteStartupCheck(succeeded: false);
        Advance(1);
        _scheduler.Finished(true);
        _time.Advance(Interval);
        _scheduler.Finished(false);
        Assert.Equal(_time.GetUtcNow().AddMinutes(1), _scheduler.NextCheck);
    }

    [Fact]
    public void CheckNow_RunsAtOnceAndNotTwiceWhileRunning()
    {
        _scheduler.CheckNow();
        _scheduler.CheckNow();
        Advance(1);
        Assert.Equal([CheckTrigger.Manual], _checks);
    }

    [Fact]
    public void CheckNow_ReplacesTheStartupCheck()
    {
        _scheduler.CheckNow();
        _scheduler.Finished(true);
        Advance(1);
        Assert.Equal([CheckTrigger.Manual], _checks);
        Assert.Equal(_time.GetUtcNow().AddMinutes(-1) + Interval, _scheduler.NextCheck);
    }

    [Fact]
    public void CheckNow_IgnoresOfflineAndBatterySaver()
    {
        _scheduler.SetConditions(online: false, batterySaver: true);
        _scheduler.CheckNow();
        Assert.Equal([CheckTrigger.Manual], _checks);
    }

    [Fact]
    public void OpeningTheFlyout_BeforeAnyCheck_Checks()
    {
        _scheduler.FlyoutOpened();
        Assert.Equal([CheckTrigger.FlyoutOpened], _checks);
    }

    [Fact]
    public void OpeningTheFlyout_Within15Minutes_ShowsCachedData()
    {
        CompleteStartupCheck();
        Advance(15);
        _scheduler.FlyoutOpened();
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void OpeningTheFlyout_After15Minutes_Refreshes()
    {
        CompleteStartupCheck();
        _time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        _scheduler.FlyoutOpened();
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.FlyoutOpened], _checks);
    }

    [Fact]
    public void OpeningTheFlyout_Offline_SkipsQuietly()
    {
        _scheduler.SetConditions(online: false, batterySaver: false);
        _scheduler.FlyoutOpened();
        Assert.Empty(_checks);
    }

    [Fact]
    public void OpeningTheFlyout_InBatterySaver_StillRefreshes()
    {
        _scheduler.SetConditions(online: true, batterySaver: true);
        _scheduler.FlyoutOpened();
        Assert.Equal([CheckTrigger.FlyoutOpened], _checks);
    }

    [Fact]
    public void Offline_HoldsTheCheckUntilTheNetworkReturns()
    {
        _scheduler.SetConditions(online: false, batterySaver: false);
        Advance(10);
        Assert.Empty(_checks);
        _scheduler.SetConditions(online: true, batterySaver: false);
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void BatterySaver_HoldsTheCheckUntilItTurnsOff()
    {
        _scheduler.SetConditions(online: true, batterySaver: true);
        Advance(10);
        Assert.Empty(_checks);
        _scheduler.SetConditions(online: true, batterySaver: false);
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void ConditionsClearingEarly_KeepTheSchedule()
    {
        _scheduler.SetConditions(online: false, batterySaver: false);
        Advance(0.5);
        _scheduler.SetConditions(online: true, batterySaver: false);
        Assert.Empty(_checks);
        Advance(0.5);
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void CheckDueDuringSleep_RunsAMinuteAfterResume()
    {
        CompleteStartupCheck();
        _time.AdjustTime(_time.GetUtcNow() + TimeSpan.FromHours(8));
        _scheduler.Resumed();
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal([CheckTrigger.Startup], _checks);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Resumed], _checks);
    }

    [Fact]
    public void ResumeBeforeTheDueTime_KeepsTheWallClockSchedule()
    {
        CompleteStartupCheck();
        var due = _scheduler.NextCheck;
        _time.AdjustTime(_time.GetUtcNow() + TimeSpan.FromHours(2));
        _scheduler.Resumed();
        Assert.Equal(due, _scheduler.NextCheck);
        _time.Advance(TimeSpan.FromHours(4));
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Scheduled], _checks);
    }

    [Fact]
    public void ShorterIntervalAlreadyPassed_ChecksNow()
    {
        CompleteStartupCheck();
        Advance(120);
        _scheduler.SetInterval(TimeSpan.FromHours(1));
        Assert.Equal([CheckTrigger.Startup, CheckTrigger.Scheduled], _checks);
    }

    [Fact]
    public void LongerInterval_CountsFromTheLastCheck()
    {
        CompleteStartupCheck();
        var last = _time.GetUtcNow();
        Advance(30);
        _scheduler.SetInterval(TimeSpan.FromHours(12));
        Assert.Equal(last + TimeSpan.FromHours(12), _scheduler.NextCheck);
    }

    [Fact]
    public void IntervalChange_LeavesARetryAlone()
    {
        CompleteStartupCheck(succeeded: false);
        var retry = _scheduler.NextCheck;
        _scheduler.SetInterval(TimeSpan.FromHours(1));
        Assert.Equal(retry, _scheduler.NextCheck);
    }

    [Fact]
    public void StrayFinished_IsIgnored()
    {
        _scheduler.Finished(false);
        Advance(1);
        Assert.Equal([CheckTrigger.Startup], _checks);
    }

    [Fact]
    public void Dispose_StopsEverything()
    {
        _scheduler.Dispose();
        Advance(10);
        _scheduler.CheckNow();
        _scheduler.SetConditions(online: true, batterySaver: false);
        Assert.Empty(_checks);
    }
}
