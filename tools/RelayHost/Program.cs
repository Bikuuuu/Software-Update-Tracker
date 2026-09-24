using System.Diagnostics;
using SoftwareUpdateTracker.WinGet.Throttling;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: RelayHost <limitKBps> <command> [args...]");
    return 2;
}

await using var relay = new ThrottlingRelay(TimeProvider.System) { LimitBytesPerSecond = long.Parse(args[0]) * 1024 };
relay.Start();
var proxy = relay.ProxyUri.ToString().TrimEnd('/');
Console.WriteLine($"relay {proxy} limit={args[0]} KB/s");

var start = new ProcessStartInfo(args[1]) { UseShellExecute = false };
foreach (var arg in args.Skip(2)) start.ArgumentList.Add(arg);
start.ArgumentList.Add("--proxy");
start.ArgumentList.Add(proxy);
var stopwatch = Stopwatch.StartNew();
using var process = Process.Start(start)!;
await process.WaitForExitAsync();
stopwatch.Stop();

var seconds = relay.TransferDuration.TotalSeconds;
var kbps = seconds > 0 ? relay.BytesDownloaded / 1024.0 / seconds : 0;
Console.WriteLine($"RESULT exit={process.ExitCode} bytes={relay.BytesDownloaded} seconds={stopwatch.Elapsed.TotalSeconds:F1} downloadKBps={kbps:F0}");
return process.ExitCode;
