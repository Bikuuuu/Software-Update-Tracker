using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Scheduling;
using SoftwareUpdateTracker.Core.Storage;
using SoftwareUpdateTracker.Core.Tracking;
using SoftwareUpdateTracker.Core.Versions;

namespace SoftwareUpdateTracker.Core.Checking;

// Apps: a row per tracked app the check covered. Detail: the technical reason behind a problem.
public sealed record CheckCompleted(CheckTicket Ticket, DateTimeOffset At, IReadOnlyList<AppCheck> Apps, CheckProblem Problem, string? Detail = null);

// Runs each check the scheduler asks for, saves what it learns, and answers every CheckDue with Finished.
public sealed class CheckRunner : IDisposable
{
    // A check ends a minute before the scheduler's watchdog would give up on it.
    public static readonly TimeSpan Deadline = CheckScheduler.CheckTimeout - TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly CheckScheduler _scheduler;
    private readonly SettingsStore _store;
    private readonly IPackageSource _source;
    private readonly IReleaseDates _dates;
    private readonly TimeProvider _time;
    private readonly FileLog _log;
    private CancellationTokenSource? _running;
    private bool _disposed;

    public CheckRunner(CheckScheduler scheduler, SettingsStore store, IPackageSource source, IReleaseDates dates, TimeProvider time, FileLog log)
    {
        _scheduler = scheduler;
        _store = store;
        _source = source;
        _dates = dates;
        _time = time;
        _log = log;
        scheduler.CheckDue += OnCheckDue;
    }

    // Raised on a worker thread after each check, unless a newer check replaced it.
    public event EventHandler<CheckCompleted>? Completed;

    public void Dispose()
    {
        CancellationTokenSource? running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            running = _running;
            _running = null;
        }
        _scheduler.CheckDue -= OnCheckDue;
        Cancel(running);
    }

    private void OnCheckDue(object? sender, CheckTicket ticket)
    {
        var run = new CancellationTokenSource(Deadline, _time);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                run.Dispose();
                return;
            }
            // A new ticket means the scheduler gave up on the previous check.
            previous = _running;
            _running = run;
        }
        Cancel(previous);
        _ = Task.Run(() => RunAsync(ticket, run));
    }

    private static void Cancel(CancellationTokenSource? run)
    {
        try
        {
            run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(CheckTicket ticket, CancellationTokenSource run)
    {
        IReadOnlyList<AppCheck> apps = [];
        var problem = CheckProblem.None;
        string? detail = null;
        try
        {
            apps = await CheckAsync(run.Token);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            problem = CheckProblem.TimedOut;
        }
        catch (PackageSourceException e)
        {
            (problem, detail) = (e.Problem, e.Message);
        }
        catch (IOException e)
        {
            (problem, detail) = (CheckProblem.SettingsNotSaved, e.Message);
        }
        catch (Exception e)
        {
            (problem, detail) = (CheckProblem.Failed, $"{e.GetType().Name}: {e.Message}");
            _log.Error("Check failed", e);
        }

        bool current;
        lock (_gate)
        {
            current = _running == run;
            if (current) _running = null;
        }
        run.Dispose();
        // A replaced check stays silent: the scheduler has moved on to a newer ticket.
        if (!current) return;
        if (problem is not (CheckProblem.None or CheckProblem.Failed)) _log.Warn($"Check {problem}: {detail}");
        _scheduler.Finished(ticket, problem == CheckProblem.None);
        Completed?.Invoke(this, new CheckCompleted(ticket, _time.GetUtcNow(), apps, problem, detail));
    }

    private async Task<IReadOnlyList<AppCheck>> CheckAsync(CancellationToken ct)
    {
        var requested = _store.Current.Apps;
        if (requested.Count == 0) return [];
        var read = await _source.ReadAsync(requested, ct);
        ct.ThrowIfCancellationRequested();
        return await AddReleaseDatesAsync(Merge(requested, read), ct);
    }

    // Merges inside Update, so a toggle saved during the check survives.
    // Apps added during the check wait for the next one; apps removed stay removed.
    private IReadOnlyList<AppCheck> Merge(IReadOnlyList<TrackedApp> requested, CatalogRead read)
    {
        IReadOnlyList<AppCheck> checks = [];
        _store.Update(file =>
        {
            var covered = file.Apps.Where(app => requested.Any(r => r.Matches(app.Id, app.Source))).ToList();
            checks = CheckMerge.Apply(covered, read.Installed, _time.GetUtcNow(), read.NotInCatalog);
            var merged = checks.Select(c => c.App).ToList();
            return file with { Apps = file.Apps.Select(app => merged.FirstOrDefault(m => m.Matches(app.Id, app.Source)) ?? app).ToList() };
        });
        return checks;
    }

    // A date is fetched once per offered version and kept with the offer; a miss falls back to first seen.
    private async Task<IReadOnlyList<AppCheck>> AddReleaseDatesAsync(IReadOnlyList<AppCheck> checks, CancellationToken ct)
    {
        var dates = new List<(TrackedApp App, string Version, DateOnly Date)>();
        foreach (var check in checks)
        {
            if (check.Status != AppStatus.Available || check.App.Offer is not { ReleaseDate: null } offer) continue;
            if (await DateOf(check.Package?.Id ?? check.App.Id, offer.Version, ct) is { } date) dates.Add((check.App, offer.Version, date));
        }
        if (dates.Count == 0) return checks;
        _store.Update(file => file with { Apps = file.Apps.Select(app => Dated(app, dates)).ToList() });
        return checks.Select(check => check with { App = Dated(check.App, dates) }).ToList();
    }

    private async Task<DateOnly?> DateOf(string id, string version, CancellationToken ct)
    {
        try
        {
            return await _dates.GetAsync(id, version, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warn($"Release date of {id} {version}: {e.Message}");
            return null;
        }
    }

    // Only the version the date was fetched for gets it.
    private static TrackedApp Dated(TrackedApp app, IReadOnlyList<(TrackedApp App, string Version, DateOnly Date)> dates)
    {
        foreach (var (dated, version, date) in dates)
        {
            if (dated.Matches(app.Id, app.Source) && app.Offer is { } offer && PackageVersion.Same(offer.Version, version))
                return app with { Offer = offer with { ReleaseDate = date } };
        }
        return app;
    }
}
