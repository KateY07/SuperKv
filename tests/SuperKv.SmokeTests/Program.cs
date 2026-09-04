using System.Diagnostics;
using System.Reflection;
using SuperKv;

if (args is ["--child", var childDirectory])
{
    await RunChildProcessAsync(childDirectory);
    return;
}

string directory = Path.Combine(Path.GetTempPath(), "SuperKv.SmokeTests", Guid.NewGuid().ToString("N"));

try
{
    var options = new SuperKvOptions
    {
        Backend = SuperKvBackend.LightningDb,
        KeyPrefix = "smoke:",
        LightningDb = new LightningDbBackendOptions
        {
            DirectoryPath = directory,
            MapSize = 64L * 1024 * 1024
        }
    };

    await using ISuperKv first = await SuperKvClient.OpenAsync(options);
    await using ISuperKv second = await SuperKvClient.OpenAsync(options);

    Assert(await first.SetStringAsync("status", "running"), "Set should succeed.");
    Assert(await second.GetStringAsync("status") == "running", "A second client should see the value.");

    Assert(!await first.SetStringAsync(
        "status",
        "stopped",
        condition: SuperKvSetCondition.OnlyIfMissing), "OnlyIfMissing should reject an existing key.");
    Assert(await first.GetStringAsync("status") == "running", "Rejected sets must not change the value.");

    await first.SetJsonAsync("camera", new CameraState("exposing", 42));
    CameraState? state = await second.GetJsonAsync<CameraState>("camera");
    Assert(state == new CameraState("exposing", 42), "JSON should round-trip.");

    Assert(await first.IncrementAsync("frames") == 1, "A missing counter should start at zero.");
    Assert(await second.IncrementAsync("frames", 4) == 5, "Counters should be shared and atomic.");

    await RunSecondProcessAsync(directory);
    Assert(await first.IncrementAsync("frames", 0) == 7, "The parent should see the child process write.");

    await first.SetStringAsync("temporary", "value", TimeSpan.FromMilliseconds(30));
    await first.SetStringAsync("stale", "value", TimeSpan.FromMilliseconds(30));
    await Task.Delay(80);
    Assert(await second.GetStringAsync("temporary") is null, "Expired values should be hidden.");
    Assert(!await second.DeleteAsync("stale"), "Deleting a logically expired key should report false.");
    Assert(await second.SetStringAsync(
        "temporary",
        "renewed",
        condition: SuperKvSetCondition.OnlyIfMissing), "An expired key should count as missing.");

    Assert(await first.DeleteAsync("status"), "Delete should report an existing key.");
    Assert(!await second.ExistsAsync("status"), "Deleted keys should be absent.");

    Console.WriteLine("SuperKv LightningDB smoke tests passed.");
}
finally
{
    if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task RunSecondProcessAsync(string directory)
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
    startInfo.ArgumentList.Add(directory);

    using Process child = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Cannot start the child process.");
    string standardOutput = await child.StandardOutput.ReadToEndAsync();
    string standardError = await child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();

    if (child.ExitCode != 0)
        throw new InvalidOperationException(
            $"Child process failed with exit code {child.ExitCode}.{Environment.NewLine}{standardOutput}{standardError}");
}

static async Task RunChildProcessAsync(string directory)
{
    await using ISuperKv kv = await SuperKvClient.OpenAsync(CreateOptions(directory));
    Assert(await kv.GetStringAsync("status") == "running", "The child should see the parent process value.");
    Assert(await kv.IncrementAsync("frames", 2) == 7, "The child should update the shared counter.");
}

static SuperKvOptions CreateOptions(string directory) => new()
{
    Backend = SuperKvBackend.LightningDb,
    KeyPrefix = "smoke:",
    LightningDb = new LightningDbBackendOptions
    {
        DirectoryPath = directory,
        MapSize = 64L * 1024 * 1024
    }
};

sealed record CameraState(string Status, long Frame);
