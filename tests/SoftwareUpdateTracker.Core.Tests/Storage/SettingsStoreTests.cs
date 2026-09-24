using SoftwareUpdateTracker.Core.Settings;
using SoftwareUpdateTracker.Core.Storage;
using SoftwareUpdateTracker.Core.Tracking;
using Xunit;

namespace SoftwareUpdateTracker.Core.Tests.Storage;

public sealed class SettingsStoreTests : IDisposable
{
    private static readonly DateTimeOffset Seen = new(2026, 9, 25, 8, 30, 0, TimeSpan.Zero);
    private readonly TempFolder _folder = new();

    private string SettingsPath => _folder.PathOf("settings.json");

    public void Dispose() => _folder.Dispose();

    private SettingsStore Loaded(out bool recovered)
    {
        var store = new SettingsStore(SettingsPath);
        recovered = store.Load();
        return store;
    }

    [Fact]
    public void MissingFile_LoadsDefaultsWithoutCreatingAnything()
    {
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(new AppSettings(), store.Current.Settings);
        Assert.Empty(store.Current.Apps);
        Assert.Empty(Directory.GetFileSystemEntries(_folder.Root));
    }

    [Fact]
    public void MissingFolder_LoadsDefaults()
    {
        var store = new SettingsStore(_folder.PathOf(Path.Combine("missing", "settings.json")));
        Assert.False(store.Load());
        Assert.Empty(store.Current.Apps);
    }

    [Fact]
    public void Update_RoundTripsEverything()
    {
        var app = new TrackedApp
        {
            Id = "Mozilla.Firefox",
            Source = "winget",
            Name = "Mozilla Firefox",
            Auto = true,
            SkippedVersion = "130.0",
            Offer = new Offer { Version = "131.0", FirstSeen = Seen, ReleaseDate = new DateOnly(2026, 9, 20), LastAutoAttempt = Seen.AddHours(1), Phantom = true },
        };
        var settings = new AppSettings { CheckIntervalHours = 12, SilentMode = true, SpeedLimitEnabled = true, SpeedLimitKBps = 900, OpenShortcut = null };
        new SettingsStore(SettingsPath).Update(f => f with { Settings = settings, Apps = [app] });

        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(settings, store.Current.Settings);
        Assert.Equal(app, Assert.Single(store.Current.Apps));
    }

    [Fact]
    public void Save_LeavesNoTempFile()
    {
        new SettingsStore(SettingsPath).Update(f => f);
        Assert.Equal(["settings.json"], Directory.GetFiles(_folder.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_WritesReadableJsonInUtc()
    {
        new SettingsStore(SettingsPath).Update(f => f with
        {
            Apps = [new TrackedApp { Id = "A", Source = "winget", Offer = new Offer { Version = "2", FirstSeen = Seen } }],
        });
        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"checkIntervalHours\": 6", json);
        Assert.Contains("\"modifiers\": \"Alt, Control\"", json);
        Assert.Contains("\"firstSeen\": \"2026-09-25T08:30:00+00:00\"", json);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{ "apps": "x" }""")]
    [InlineData("""{ "apps": [{ "source": "winget" }] }""")]
    [InlineData("""{ "settings": null }""")]
    [InlineData("""{ "settings": { "openShortcut": { "modifiers": "Hyper", "key": 85 } } }""")]
    [InlineData("""{ "apps": [{ "id": "A", "source": "winget", "offer": { "version": "1", "firstSeen": "yesterday" } }] }""")]
    [InlineData("""{ "settings": { "checkIntervalHours": 99999999999 } }""")]
    public void CorruptFile_IsKeptAsBakAndDefaultsLoad(string content)
    {
        File.WriteAllText(SettingsPath, content);
        var store = Loaded(out var recovered);
        Assert.True(recovered);
        Assert.Equal(new AppSettings(), store.Current.Settings);
        Assert.Equal(content, File.ReadAllText(SettingsPath + ".bak"));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void BinaryGarbage_IsTreatedAsCorrupt()
    {
        File.WriteAllBytes(SettingsPath, [0xFF, 0xFE, 0x00, 0xC3, 0x28]);
        Loaded(out var recovered);
        Assert.True(recovered);
    }

    [Fact]
    public void SecondCorruption_ReplacesTheOnlyBackup()
    {
        File.WriteAllText(SettingsPath, "first");
        Loaded(out _);
        File.WriteAllText(SettingsPath, "second");
        Loaded(out _);
        Assert.Equal("second", File.ReadAllText(SettingsPath + ".bak"));
        Assert.Single(Directory.GetFiles(_folder.Root));
    }

    [Fact]
    public void StaleTempFile_IsRemovedOnLoad()
    {
        File.WriteAllText(SettingsPath + ".tmp", "half written");
        Loaded(out _);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void HandEditedFile_WithCommentsTrailingCommasAndUnknownFields_Loads()
    {
        File.WriteAllText(SettingsPath, """
            {
              // edited by hand
              "settings": { "checkIntervalHours": 12, "futureOption": true, },
              "apps": [ { "id": "Mozilla.Firefox", "source": "winget", "auto": true }, ],
            }
            """);
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(12, store.Current.Settings.CheckIntervalHours);
        Assert.True(Assert.Single(store.Current.Apps).Auto);
    }

    [Fact]
    public void Load_NormalizesOutOfRangeValuesAndBadApps()
    {
        File.WriteAllText(SettingsPath, """
            {
              "settings": { "checkIntervalHours": 5, "autoInstallWaitDays": 2, "speedLimitKBps": 0 },
              "apps": [
                { "id": "Mozilla.Firefox", "source": "winget" },
                { "id": "mozilla.firefox", "source": "WinGet" },
                { "id": " ", "source": "winget" }
              ]
            }
            """);
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(6, store.Current.Settings.CheckIntervalHours);
        Assert.Equal(0, store.Current.Settings.AutoInstallWaitDays);
        Assert.Equal(17500, store.Current.Settings.SpeedLimitKBps);
        Assert.Equal("Mozilla.Firefox", Assert.Single(store.Current.Apps).Id);
    }

    [Fact]
    public async Task ParallelUpdates_AreAllKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SettingsStore(SettingsPath);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(
            () => store.Update(f => f with { Apps = [.. f.Apps, new TrackedApp { Id = $"App.{i}", Source = "winget" }] }), ct)));

        var reloaded = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(20, reloaded.Current.Apps.Count);
    }

    [Fact]
    public void Update_ReturnsAndKeepsTheNormalizedFile()
    {
        var store = new SettingsStore(SettingsPath);
        var saved = store.Update(f => f with { Settings = f.Settings with { CheckIntervalHours = 7 } });
        Assert.Equal(6, saved.Settings.CheckIntervalHours);
        Assert.Same(saved, store.Current);
    }
}
