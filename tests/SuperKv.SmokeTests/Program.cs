using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Garnet;
using SuperKv;

if (args is ["--child", var childConnection, var childPrefix])
{
    await using ISuperKv child = await OpenAsync(childConnection, childPrefix);
    Assert(await child.GetStringAsync("status") == "running", "The child should see the parent value.");
    Assert(await child.IncrementAsync("frames", 2) == 7, "The child should update the shared counter.");
    return;
}

int port = GetFreeTcpPort();
string connectionString = $"127.0.0.1:{port},abortConnect=true,connectTimeout=5000";
string prefix = $"smoke:{Guid.NewGuid():N}:";
using var server = new GarnetServer(
    ["--bind", "127.0.0.1", "--port", port.ToString()]);
server.Start();

await using ISuperKv first = await OpenAsync(connectionString, prefix);
await using ISuperKv second = await OpenAsync(connectionString, prefix);
Assert(await first.SetStringAsync("status", "running"), "Set should succeed.");
Assert(await second.GetStringAsync("status") == "running", "A second client should see the value.");
Assert(!await first.SetStringAsync(
    "status", "stopped", condition: SuperKvSetCondition.OnlyIfMissing),
    "OnlyIfMissing should reject an existing key.");

await first.SetJsonAsync("camera", new CameraState("exposing", 42));
Assert(await second.GetJsonAsync<CameraState>("camera") == new CameraState("exposing", 42),
    "JSON should round-trip.");
Assert(await first.IncrementAsync("frames") == 1, "A missing counter should start at zero.");
Assert(await second.IncrementAsync("frames", 4) == 5, "Counters should be shared and atomic.");
await RunSecondProcessAsync(connectionString, prefix);
Assert(await first.IncrementAsync("frames", 0) == 7, "The parent should see the child process write.");

await first.SetStringAsync("temporary", "value", TimeSpan.FromMilliseconds(100));
await WaitUntilAsync(async () => !await second.ExistsAsync("temporary"), TimeSpan.FromSeconds(3));
Assert(await second.GetStringAsync("temporary") is null, "Expired values should be absent.");
Assert(await first.DeleteAsync("status"), "Delete should report an existing key.");
Assert(!await second.ExistsAsync("status"), "Deleted keys should be absent.");
Console.WriteLine("SuperKv Garnet cross-process smoke tests passed.");

static async ValueTask<ISuperKv> OpenAsync(string connectionString, string prefix) =>
    await SuperKvClient.OpenAsync(new SuperKvOptions
    {
        KeyPrefix = prefix,
        Garnet = new GarnetOptions { ConnectionString = connectionString }
    });

static async Task RunSecondProcessAsync(string connectionString, string prefix)
{
    string processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot locate the current process executable.");
    var startInfo = new ProcessStartInfo(processPath)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    startInfo.ArgumentList.Add("--child");
    startInfo.ArgumentList.Add(connectionString);
    startInfo.ArgumentList.Add(prefix);

    using Process child = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Cannot start the child process.");
    string standardOutput = await child.StandardOutput.ReadToEndAsync();
    string standardError = await child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    if (child.ExitCode != 0)
        throw new InvalidOperationException(
            $"Child process failed with exit code {child.ExitCode}.{Environment.NewLine}{standardOutput}{standardError}");
}

static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
{
    DateTime deadline = DateTime.UtcNow + timeout;
    while (!await predicate())
    {
        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException("Condition was not reached before the timeout.");
        await Task.Delay(20);
    }
}

static int GetFreeTcpPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed record CameraState(string Status, long Frame);