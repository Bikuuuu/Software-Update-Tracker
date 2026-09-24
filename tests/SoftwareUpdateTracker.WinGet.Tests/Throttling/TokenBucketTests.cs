using Microsoft.Extensions.Time.Testing;
using SoftwareUpdateTracker.WinGet.Throttling;
using Xunit;

namespace SoftwareUpdateTracker.WinGet.Tests.Throttling;

public class TokenBucketTests
{
    [Fact]
    public void Unlimited_NeverWaits() =>
        Assert.Equal(TimeSpan.Zero, new TokenBucket(new FakeTimeProvider()).Take(10_000_000));

    [Fact]
    public void StartsEmpty_FirstChunkWaitsForItsShare() =>
        Assert.Equal(TimeSpan.FromMilliseconds(500), new TokenBucket(new FakeTimeProvider()) { BytesPerSecond = 1000 }.Take(500));

    [Fact]
    public void SteadyRate_WaitsProportionally()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(time) { BytesPerSecond = 1000 };
        bucket.Take(500);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), bucket.Take(500));
    }

    [Fact]
    public void LongIdle_AllowsAtMostOneSecondBurst()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(time) { BytesPerSecond = 1000 };
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.Zero, bucket.Take(1000));
        Assert.Equal(TimeSpan.FromMilliseconds(500), bucket.Take(500));
    }

    [Fact]
    public void RaisingTheLimit_ShortensWaits()
    {
        var bucket = new TokenBucket(new FakeTimeProvider()) { BytesPerSecond = 1000 };
        bucket.BytesPerSecond = 4000;
        Assert.Equal(TimeSpan.FromMilliseconds(250), bucket.Take(1000));
    }

    [Fact]
    public void ZeroMeansUnlimited_EvenAfterBeingLimited()
    {
        var bucket = new TokenBucket(new FakeTimeProvider()) { BytesPerSecond = 1000 };
        bucket.Take(5000);
        bucket.BytesPerSecond = 0;
        Assert.Equal(TimeSpan.Zero, bucket.Take(5000));
    }

    [Fact]
    public void TinyLimit_StillMakesProgress() =>
        Assert.Equal(TimeSpan.FromSeconds(16), new TokenBucket(new FakeTimeProvider()) { BytesPerSecond = 1024 }.Take(16 * 1024));
}
