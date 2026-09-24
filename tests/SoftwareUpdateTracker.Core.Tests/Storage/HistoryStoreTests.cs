using Microsoft.Extensions.Time.Testing;
using SoftwareUpdateTracker.Core.History;
using SoftwareUpdateTracker.Core.Storage;
using Xunit;

namespace SoftwareUpdateTracker.Core.Tests.Storage;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));

    private string HistoryPath => _folder.PathOf("history.json");

    public void Dispose() => _folder.Dispose();

    private HistoryStore Store()
    {
        var store = new HistoryStore(HistoryPath, _time);
        store.Load();
        return store;
    }

    private HistoryEntry Entry(string id, TimeSpan age, HistoryResult result = HistoryResult.Updated) => new()
    {
        Time = _time.GetUtcNow() - age,
        Id = id,
        Source = "winget",
        Name = id,
        Result = result,
        FromVersion = "1.0",
        ToVersion = "1.1",
    };

    [Fact]
    public void Add_KeepsNewestFirstAndPersists()
    {
        var store = Store();
        store.Add(Entry("Old", TimeSpan.FromHours(2)));
        store.Add(Entry("New", TimeSpan.Zero));
        store.Add(Entry("Middle", TimeSpan.FromHours(1)));
        Assert.Equal(["New", "Middle", "Old"], Store().Entries.Select(e => e.Id));
    }

    [Fact]
    public void Entries_RoundTripAllFields()
    {
        var failed = Entry("Logitech.GHUB", TimeSpan.Zero, HistoryResult.Failed) with { Reason = "The installer failed", Code = "0x8A150011" };
        Store().Add(failed);
        Assert.Equal(failed, Assert.Single(Store().Entries));
    }

    [Fact]
    public void Load_DropsEntriesOlderThan90Days()
    {
        var store = Store();
        store.Add(Entry("Kept", TimeSpan.FromDays(89)));
        store.Add(Entry("Dropped", TimeSpan.FromDays(91)));
        Assert.Equal(["Kept"], Store().Entries.Select(e => e.Id));
    }

    [Fact]
    public void Add_DropsEntriesThatAgedOut()
    {
        var store = Store();
        store.Add(Entry("Aging", TimeSpan.FromDays(89)));
        _time.Advance(TimeSpan.FromDays(2));
        store.Add(Entry("Fresh", TimeSpan.Zero));
        Assert.Equal(["Fresh"], store.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Clear_EmptiesAndPersists()
    {
        var store = Store();
        store.Add(Entry("A", TimeSpan.Zero));
        store.Clear();
        Assert.Empty(store.Entries);
        Assert.Empty(Store().Entries);
    }

    [Fact]
    public void Entries_CannotBeChangedFromOutside()
    {
        var store = Store();
        store.Add(Entry("A", TimeSpan.Zero));
        Assert.Throws<NotSupportedException>(() => ((IList<HistoryEntry>)store.Entries)[0] = Entry("B", TimeSpan.Zero));
        Assert.Equal("A", Assert.Single(store.Entries).Id);
    }

    [Fact]
    public void FailedSave_LeavesHistoryUnchanged()
    {
        var blocker = _folder.PathOf("blocker");
        File.WriteAllText(blocker, "");
        var store = new HistoryStore(Path.Combine(blocker, "history.json"), _time);
        Assert.ThrowsAny<IOException>(() => store.Add(Entry("A", TimeSpan.Zero)));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void EmptyObject_LoadsEmptyHistory()
    {
        File.WriteAllText(HistoryPath, "{}");
        var store = new HistoryStore(HistoryPath, _time);
        Assert.False(store.Load());
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void CorruptFile_IsKeptAsBakAndHistoryStartsEmpty()
    {
        File.WriteAllText(HistoryPath, "{ broken");
        var store = new HistoryStore(HistoryPath, _time);
        Assert.True(store.Load());
        Assert.Empty(store.Entries);
        Assert.Equal("{ broken", File.ReadAllText(HistoryPath + ".bak"));
    }
}
