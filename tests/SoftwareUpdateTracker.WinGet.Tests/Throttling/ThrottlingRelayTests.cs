using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SoftwareUpdateTracker.WinGet.Throttling;
using Xunit;

namespace SoftwareUpdateTracker.WinGet.Tests.Throttling;

public class ThrottlingRelayTests
{
    [Fact]
    public async Task Connect_TunnelsBytesAtTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, port) = StartServer(200_000, ct);
        await using var relay = new ThrottlingRelay(TimeProvider.System, new HashSet<int> { port }) { LimitBytesPerSecond = 100_000 };
        relay.Start();

        var stopwatch = Stopwatch.StartNew();
        var (status, received) = await ConnectAndReadAsync(relay.Port, port, ct);
        stopwatch.Stop();

        Assert.StartsWith("HTTP/1.1 200", status);
        Assert.Equal(200_000, received);
        Assert.Equal(200_000, relay.BytesDownloaded);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 1.6, 3.5);
        await server;
    }

    [Fact]
    public async Task Unlimited_IsFast()
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, port) = StartServer(2_000_000, ct);
        await using var relay = new ThrottlingRelay(TimeProvider.System, new HashSet<int> { port });
        relay.Start();

        var stopwatch = Stopwatch.StartNew();
        var (_, received) = await ConnectAndReadAsync(relay.Port, port, ct);

        Assert.Equal(2_000_000, received);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        await server;
    }

    [Fact]
    public async Task LiftingTheLimitMidTransfer_TakesEffectQuickly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, port) = StartServer(300_000, ct);
        await using var relay = new ThrottlingRelay(TimeProvider.System, new HashSet<int> { port }) { LimitBytesPerSecond = 2_000 };
        relay.Start();

        var stopwatch = Stopwatch.StartNew();
        var read = ConnectAndReadAsync(relay.Port, port, ct);
        await Task.Delay(1000, ct);
        relay.LimitBytesPerSecond = 0;
        var (_, received) = await read;

        Assert.Equal(300_000, received);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
        await server;
    }

    [Fact]
    public async Task DisallowedPort_IsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var relay = new ThrottlingRelay(TimeProvider.System);
        relay.Start();
        var (status, _) = await ConnectAndReadAsync(relay.Port, 8080, ct);
        Assert.StartsWith("HTTP/1.1 403", status);
    }

    [Fact]
    public async Task NonConnectMethod_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var relay = new ThrottlingRelay(TimeProvider.System);
        relay.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.Port, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n"), ct);
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer, ct);
        Assert.StartsWith("HTTP/1.1 405", Encoding.ASCII.GetString(buffer, 0, read));
    }

    private static (Task Serve, int Port) StartServer(int bytes, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await client.GetStream().WriteAsync(new byte[bytes], ct);
            listener.Stop();
        }, ct);
        return (serve, port);
    }

    private static async Task<(string Status, long Received)> ConnectAndReadAsync(int relayPort, int targetPort, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relayPort, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n"), ct);
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, ct) == 1)
            head.Append((char)one[0]);
        long total = 0;
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) total += read;
        return (head.ToString(), total);
    }
}
