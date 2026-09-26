using TinyTracker.Core.Checking;
using TinyTracker.Core.History;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;

namespace TinyTracker.Core.Installing;

// Installs one package at a time, because parallel installers conflict. After each package it reads the app again,
// flags a phantom update, saves the result and writes History.
public sealed class InstallQueue : IInstaller, IDisposable
{
    // Reading the app again is a winget list, which takes seconds.
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);
    // While bytes don't come, the shown speed is refreshed this often, so it falls to zero.
    public static readonly TimeSpan SpeedRefresh = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly List<Entry> _waiting = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly IPackageUpgrader _upgrader;
    private readonly IPackageSource _source;
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly TimeProvider _time;
    private readonly FileLog _log;
    private readonly InstallTimings _timings;
    private Entry? _current;
    private Task _worker = Task.CompletedTask;
    private Task _stopped = Task.CompletedTask;
    private bool _working;
    private bool _disposed;

    public InstallQueue(IPackageUpgrader upgrader, IPackageSource source, SettingsStore settings, HistoryStore history, TimeProvider time, FileLog log, InstallTimings? timings = null)
    {
        _upgrader = upgrader;
        _source = source;
        _settings = settings;
        _history = history;
        _time = time;
        _log = log;
        _timings = timings ?? InstallTimings.Default;
    }

    // Raised on worker threads, in order. Every request ends with a Done item.
    public event EventHandler<InstallItem>? Changed;

    // Completes once the worker has stopped after Dispose, so its last History write has landed.
    public Task Stopped
    {
        get { lock (_gate) return _stopped; }
    }

    // An app already waiting or installing isn't added twice.
    public void Enqueue(IEnumerable<InstallRequest> requests)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var request in requests)
            {
                if (_waiting.Any(e => Same(e.Request.Package, request.Package)) || (_current is { } current && Same(current.Request.Package, request.Package))) continue;
                var entry = new Entry(request, _time);
                _waiting.Add(entry);
                Raise(entry.Item);
            }
            if (_working || _waiting.Count == 0) return;
            _working = true;
            _worker = Task.Run(WorkAsync);
        }
    }

    // A waiting app leaves the queue. A download stops; an installer that already started finishes.
    public void Cancel(PackageKey package)
    {
        lock (_gate)
        {
            if (_waiting.FirstOrDefault(e => Same(e.Request.Package, package)) is { } waiting)
            {
                _waiting.Remove(waiting);
                Raise(waiting.Finish(new InstallDone(new UpgradeOutcome(UpgradeResult.Cancelled))));
            }
            else if (_current is { } current && Same(current.Request.Package, package))
            {
                current.Cancel();
            }
        }
    }

    public void Dispose()
    {
        Task worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _waiting.Clear();
            worker = _worker;
            _stopped = StopAsync(worker);
        }
    }

    private static bool Same(PackageKey a, PackageKey b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase);

    // Each handler runs on its own: one that throws is logged, and neither the others nor the queue stop.
    private void Raise(InstallItem item)
    {
        if (_disposed || Changed is not { } changed) return;
        foreach (var handler in changed.GetInvocationList().Cast<EventHandler<InstallItem>>())
        {
            try
            {
                handler(this, item);
            }
            catch (Exception e)
            {
                _log.Error($"Install update of {item.Request.Package.Id} not delivered", e);
            }
        }
    }

    // Cancels on the thread pool, not on the caller's thread, and disposes once the worker stopped using the source.
    private async Task StopAsync(Task worker)
    {
        await _stop.CancelAsync();
        await worker;
        _stop.Dispose();
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            Entry entry;
            lock (_gate)
            {
                _current = null;
                if (_disposed || _waiting.Count == 0)
                {
                    _working = false;
                    return;
                }
                entry = _waiting[0];
                _waiting.RemoveAt(0);
                _current = entry;
            }
            InstallDone done;
            try
            {
                done = await InstallAsync(entry);
            }
            catch (Exception e)
            {
                _log.Error($"Install of {entry.Request.Package.Id} failed", e);
                done = new InstallDone(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other));
            }
            lock (_gate)
            {
                _current = null;
                Raise(entry.Finish(done));
            }
        }
    }

    private async Task<InstallDone> InstallAsync(Entry entry)
    {
        var request = entry.Request;
        var outcome = await UpgradeAsync(entry);
        if (_stop.IsCancellationRequested) return new InstallDone(outcome);
        var read = await ReadAgainAsync(request);
        var installed = read?.Installed.FirstOrDefault(p => Same(new PackageKey(p.Id, p.Source), request.Package));
        // A cancel can land just as winget starts the installer; then the update went through.
        if (outcome.Result == UpgradeResult.Cancelled && installed is not null && PackageVersion.Same(installed.InstalledVersion, request.ToVersion))
            outcome = new UpgradeOutcome(UpgradeResult.Updated);
        var phantom = outcome.Result == UpgradeResult.Updated && installed is not null
            && PhantomRule.IsPhantom(request.FromVersion, installed.InstalledVersion, request.ToVersion, installed.AvailableVersion);
        var after = read is null ? null : Save(request, installed, read.NotInCatalog, phantom);
        var done = new InstallDone(outcome, phantom, after);
        Record(request, done);
        return done;
    }

    // Retries a busy winget and a stalled download, and gives up at the cap.
    private async Task<UpgradeOutcome> UpgradeAsync(Entry entry)
    {
        using var cap = new CancellationTokenSource(_timings.Cap, _time);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cap.Token, _stop.Token);
        var stalls = 0;
        var busy = 0;
        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(entry.Token, wait.Token);
            IProgress<UpgradeProgress> progress;
            lock (_gate) progress = entry.StartAttempt(attempt, this);
            var upgrade = Task.Run(() => _upgrader.UpgradeAsync(entry.Request.Package, entry.Request.ToVersion, progress, attempt.Token));
            UpgradeOutcome outcome;
            try
            {
                outcome = await upgrade.WaitAsync(wait.Token);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested)
            {
                // Cancelled before it's disposed, so a download stops. winget can't stop a started installer, so the queue stops waiting for it.
                attempt.Cancel();
                _ = upgrade.ContinueWith(t => _log.Info($"{entry.Request.Package.Id} ended after the queue moved on: {(t.IsCompletedSuccessfully ? t.Result.Result : "error")}"), TaskScheduler.Default);
                return _stop.IsCancellationRequested ? new UpgradeOutcome(UpgradeResult.Cancelled) : new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.TookTooLong);
            }
            finally
            {
                lock (_gate) entry.EndAttempt();
            }
            if (outcome.Result == UpgradeResult.Cancelled && !entry.Token.IsCancellationRequested)
            {
                if (cap.IsCancellationRequested) return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.TookTooLong);
                if (entry.Stalled && ++stalls < 2) continue;
                if (entry.Stalled) return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Stalled);
            }
            if (outcome.Result != UpgradeResult.Busy || busy++ >= _timings.BusyRetries) return outcome;
            using var delay = CancellationTokenSource.CreateLinkedTokenSource(entry.Token, wait.Token);
            var pause = Task.Delay(_timings.BusyRetryDelay, _time, delay.Token);
            lock (_gate) Raise(entry.Waiting(busy: true));
            try
            {
                await pause;
            }
            catch (OperationCanceledException)
            {
                return entry.Token.IsCancellationRequested || _stop.IsCancellationRequested
                    ? new UpgradeOutcome(UpgradeResult.Cancelled)
                    : new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.TookTooLong);
            }
        }
    }

    private async Task<CatalogRead?> ReadAgainAsync(InstallRequest request)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ReadTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _stop.Token);
            return await _source.ReadAsync([new TrackedApp { Id = request.Package.Id, Source = request.Package.Source, Name = request.Name }], linked.Token);
        }
        catch (Exception e) when (e is PackageSourceException or OperationCanceledException)
        {
            _log.Warn($"{request.Package.Id} couldn't be read again: {e.Message}");
            return null;
        }
    }

    // Merged inside Update, like a check, so a change saved meanwhile survives. An app no longer tracked stays out.
    private AppCheck? Save(InstallRequest request, PackageSnapshot? installed, IReadOnlyList<PackageKey> notInCatalog, bool phantom)
    {
        AppCheck? after = null;
        try
        {
            _settings.Update(file =>
            {
                if (file.Apps.FirstOrDefault(a => a.Matches(request.Package.Id, request.Package.Source)) is not { } app) return file;
                if (phantom) app = PhantomRule.Flag(app, request.ToVersion);
                after = CheckMerge.Apply([app], installed is null ? [] : [installed], _time.GetUtcNow(), notInCatalog)[0];
                var merged = after.App;
                return file with { Apps = file.Apps.Select(a => a.Matches(merged.Id, merged.Source) ? merged : a).ToList() };
            });
            return after;
        }
        catch (IOException e)
        {
            _log.Warn($"Install result of {request.Package.Id} not saved: {e.Message}");
            return null;
        }
    }

    private void Record(InstallRequest request, InstallDone done)
    {
        try
        {
            _history.Add(new HistoryEntry
            {
                Time = _time.GetUtcNow(),
                Id = request.Package.Id,
                Source = request.Package.Source,
                Name = request.Name,
                Result = done.HistoryResult,
                FromVersion = request.FromVersion,
                ToVersion = request.ToVersion,
                Reason = done.Reason,
                Code = done.Outcome.Code,
            });
        }
        catch (IOException e)
        {
            _log.Warn($"History of {request.Package.Id} not saved: {e.Message}");
        }
    }

    // Guarded by the queue's lock.
    private sealed class Entry(InstallRequest request, TimeProvider time)
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly SpeedMeter _speed = new(time);
        private InstallItem _item = new(request, InstallStage.Waiting);
        private CancellationTokenSource? _attempt;
        private ITimer? _stallTimer;
        private ITimer? _speedTimer;
        private int _attempts;

        public InstallRequest Request => request;
        public InstallItem Item => _item;
        public CancellationToken Token => _cancel.Token;
        public bool Stalled { get; private set; }

        public void Cancel() => _ = _cancel.CancelAsync();

        public InstallItem Finish(InstallDone done) => _item = _item with { Stage = InstallStage.Done, Done = done };

        public InstallItem Waiting(bool busy) => _item = _item with { Stage = InstallStage.Waiting, Busy = busy, Progress = default, BytesPerSecond = 0 };

        public IProgress<UpgradeProgress> StartAttempt(CancellationTokenSource attempt, InstallQueue queue)
        {
            var number = ++_attempts;
            _attempt = attempt;
            Stalled = false;
            _item = _item with { Progress = default, BytesPerSecond = 0 };
            _speed.Add(0);
            // Watches the wait in winget's queue until the first progress, then the download.
            _stallTimer = time.CreateTimer(_ => queue.OnStall(this, number), null, queue._timings.StallAfter, Timeout.InfiniteTimeSpan);
            _speedTimer = time.CreateTimer(_ => queue.OnSpeedTick(this, number), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return new Reporter(progress => queue.OnProgress(this, number, progress));
        }

        public void EndAttempt()
        {
            _stallTimer?.Dispose();
            _stallTimer = null;
            _speedTimer?.Dispose();
            _speedTimer = null;
            _attempt = null;
        }

        public bool IsAttempt(int number) => number == _attempts && _attempt is not null;

        // A download counts as moving when its byte count or fraction changes. A wait that already said so keeps Busy while queued.
        public InstallItem Report(UpgradeProgress progress, TimeSpan stallAfter)
        {
            var moved = progress.Stage != _item.Progress.Stage || progress.BytesDownloaded != _item.Progress.BytesDownloaded || progress.DownloadFraction != _item.Progress.DownloadFraction;
            var downloading = progress.Stage == UpgradeStage.Downloading;
            if (downloading) _speed.Add(progress.BytesDownloaded);
            if (moved) _stallTimer?.Change(progress.Stage is UpgradeStage.Downloading or UpgradeStage.Queued ? stallAfter : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _speedTimer?.Change(downloading ? SpeedRefresh : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var stage = progress.Stage switch
            {
                UpgradeStage.Queued => InstallStage.Waiting,
                UpgradeStage.Downloading => InstallStage.Downloading,
                _ => InstallStage.Installing,
            };
            return _item = _item with { Stage = stage, Progress = progress, BytesPerSecond = _speed.BytesPerSecond, Busy = stage == InstallStage.Waiting && _item.Busy };
        }

        // Null when the shown speed is already right. The timer runs again only while there is speed left to fall.
        public InstallItem? RefreshSpeed()
        {
            var speed = _speed.BytesPerSecond;
            if (speed > 0) _speedTimer?.Change(SpeedRefresh, Timeout.InfiniteTimeSpan);
            return speed == _item.BytesPerSecond ? null : _item = _item with { BytesPerSecond = speed };
        }

        public InstallItem QueuedLong() => _item = _item with { Stage = InstallStage.Waiting, Busy = true };

        public void Stall()
        {
            Stalled = true;
            try
            {
                _ = _attempt?.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnProgress(Entry entry, int attempt, UpgradeProgress progress)
    {
        lock (_gate)
        {
            if (entry.IsAttempt(attempt)) Raise(entry.Report(progress, _timings.StallAfter));
        }
    }

    // Still queued in winget: another install is likely running, so say so and keep waiting. Downloading: the download stalled.
    private void OnStall(Entry entry, int attempt)
    {
        lock (_gate)
        {
            if (!entry.IsAttempt(attempt)) return;
            // This attempt's own progress: a retry starts without any.
            if (entry.Item.Progress.Stage == UpgradeStage.Queued)
            {
                Raise(entry.QueuedLong());
                return;
            }
            _log.Warn($"{entry.Request.Package.Id} stalled: no download progress for {_timings.StallAfter.TotalMinutes:0.#} minutes");
            entry.Stall();
        }
    }

    private void OnSpeedTick(Entry entry, int attempt)
    {
        lock (_gate)
        {
            if (entry.IsAttempt(attempt) && entry.Item.Stage == InstallStage.Downloading && entry.RefreshSpeed() is { } item) Raise(item);
        }
    }

    // Reports on the calling thread, unlike Progress<T>, which would post to the UI thread.
    private sealed class Reporter(Action<UpgradeProgress> report) : IProgress<UpgradeProgress>
    {
        public void Report(UpgradeProgress value) => report(value);
    }
}
