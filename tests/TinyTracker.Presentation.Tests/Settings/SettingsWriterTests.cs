using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Presentation.Settings;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Settings;

public sealed class SettingsWriterTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TestUi _ui = new();
    private readonly SettingsStore _store;
    private readonly SettingsWriter _writer;

    public SettingsWriterTests()
    {
        _store = new SettingsStore(_folder.PathOf("settings.json"));
        var time = new FakeTimeProvider(Now);
        _writer = new SettingsWriter(_store, new FileLog(_folder.PathOf("app.log"), time), _ui.Post);
    }

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changes_AreSavedInOrder_OffTheCallersThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var insideUpdate = false;
        var ranInline = false;
        for (var i = 1; i <= 20; i++)
        {
            var hours = i % 2 == 0 ? 12 : 24;
            Volatile.Write(ref insideUpdate, true);
            _writer.Update(file =>
            {
                // The test's own pool thread may run later saves once it awaits; only an inline save sees it inside Update.
                if (Environment.CurrentManagedThreadId == caller && Volatile.Read(ref insideUpdate)) ranInline = true;
                return file with { Settings = file.Settings with { CheckIntervalHours = hours } };
            });
            Volatile.Write(ref insideUpdate, false);
        }
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(12, _store.Current.Settings.CheckIntervalHours);
        Assert.False(ranInline);
    }

    [Fact]
    public async Task Result_ComesBackThroughThePost()
    {
        bool? saved = null;
        _writer.Update(file => file with { Settings = file.Settings with { SilentMode = true } }, ok => saved = ok);
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Null(saved);
        _ui.Pump();
        Assert.True(saved);
    }

    [Fact]
    public async Task FileThatCantBeSaved_IsRefused()
    {
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        bool? saved = null;
        _writer.Update(file => file with { Settings = file.Settings with { SilentMode = true } }, ok => saved = ok);
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _ui.Pump();
        Assert.False(saved);
        Assert.False(_store.Current.Settings.SilentMode);
    }

    [Fact]
    public async Task BrokenChange_DoesNotStopTheNextOne()
    {
        _writer.Update(_ => throw new InvalidOperationException("bug"));
        _writer.Update(file => file with { Settings = file.Settings with { SilentMode = true } });
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(_store.Current.Settings.SilentMode);
    }
}
