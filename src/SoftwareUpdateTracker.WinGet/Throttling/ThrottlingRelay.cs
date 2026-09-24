using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SoftwareUpdateTracker.WinGet.Throttling;

public sealed class ThrottlingRelay(TimeProvider time, IReadOnlySet<int>? allowedPorts = null) : IAsyncDisposable
{
    private const int MaxHeadBytes = 8192;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TokenBucket _bucket = new(time);
    private readonly IReadOnlySet<int> _allowedPorts = allowedPorts ?? new HashSet<int> { 443 };
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;
    private long _bytesDownloaded;
    private long _firstByteAt;
    private long _lastByteAt;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Uri ProxyUri => new($"http://127.0.0.1:{Port}");
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);

    public TimeSpan TransferDuration
    {
        get
        {
            var first = Interlocked.Read(ref _firstByteAt);
            return first == 0 ? TimeSpan.Zero : time.GetElapsedTime(first, Interlocked.Read(ref _lastByteAt));
        }
    }

    public long LimitBytesPerSecond
    {
        get => _bucket.BytesPerSecond;
        set => _bucket.BytesPerSecond = value;
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, ct);
                var parts = head?.Split("\r\n")[0].Split(' ');
                if (parts is not { Length: 3 } || parts[0] != "CONNECT")
                {
                    await WriteStatusAsync(stream, "405 Method Not Allowed", ct);
                    return;
                }
                if (!TryParseTarget(parts[1], out var host, out var port) || !_allowedPorts.Contains(port))
                {
                    await WriteStatusAsync(stream, "403 Forbidden", ct);
                    return;
                }
                using var upstream = new TcpClient();
                try { await upstream.ConnectAsync(host, port, ct); }
                catch (SocketException)
                {
                    await WriteStatusAsync(stream, "502 Bad Gateway", ct);
                    return;
                }
                await WriteStatusAsync(stream, "200 Connection Established", ct);
                var remote = upstream.GetStream();
                await Task.WhenAny(stream.CopyToAsync(remote, ct), CopyThrottledAsync(remote, stream, ct));
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    private async Task CopyThrottledAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await from.ReadAsync(buffer, ct)) > 0)
        {
            var wait = _bucket.Take(read);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, time, ct);
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
            var now = time.GetTimestamp();
            Interlocked.CompareExchange(ref _firstByteAt, now, 0);
            Interlocked.Exchange(ref _lastByteAt, now);
            Interlocked.Add(ref _bytesDownloaded, read);
        }
    }

    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            if (await stream.ReadAsync(buffer.AsMemory(length, 1), ct) == 0) return null;
            length++;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                return Encoding.ASCII.GetString(buffer, 0, length);
        }
        return null;
    }

    private static bool TryParseTarget(string target, out string host, out int port)
    {
        var colon = target.LastIndexOf(':');
        host = colon > 0 ? target[..colon].Trim('[', ']') : "";
        port = 0;
        return colon > 0 && int.TryParse(target[(colon + 1)..], out port) && host.Length > 0;
    }

    private static Task WriteStatusAsync(Stream stream, string status, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n"), ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_acceptLoop is not null) await _acceptLoop;
        _stop.Dispose();
    }
}
