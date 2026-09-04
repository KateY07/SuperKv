using System.Diagnostics;
using System.Reflection;
using System.Text;
using SuperKv;

if (args is ["--child", var childPipe, var childPrefix])
{
    using SuperKvClient child = Connect(childPipe, childPrefix);
    Assert(Encoding.UTF8.GetString(child.Get("status")!) == "running",
        "The child should see the parent value.");
    child.Set("child", Encoding.UTF8.GetBytes("written"));
    return;
}

string pipeName = $"SuperKv.Smoke.{Guid.NewGuid():N}";
string prefix = $"smoke:{Guid.NewGuid():N}:";
using var shutdown = new CancellationTokenSource();
var server = new SuperKvMemoryServer(new SuperKvServerOptions { PipeName = pipeName });
Task serverTask = Task.Factory.StartNew(
    () => server.Run(shutdown.Token),
    CancellationToken.None,
    TaskCreationOptions.LongRunning,
    TaskScheduler.Default);

try
{
    using SuperKvClient first = Connect(pipeName, prefix);
    using SuperKvClient second = Connect(pipeName, prefix);
    first.Set("status", Encoding.UTF8.GetBytes("running"));
    Assert(Encoding.UTF8.GetString(second.Get("status")!) == "running",
        "A second client should see the value.");

    second.Set("sync", new byte[] { 1, 2, 3 });
    Assert(first.Get("sync")!.SequenceEqual(new byte[] { 1, 2, 3 }),
        "Synchronous Get/Set should work across clients.");

    await RunSecondProcessAsync(pipeName, prefix);
    Assert(Encoding.UTF8.GetString(first.Get("child")!) == "written",
        "The parent should see the child process write.");

    Console.WriteLine("SuperKv Named Pipe cross-process Get/Set smoke tests passed.");
}
finally
{
    await shutdown.CancelAsync();
    await serverTask;
}

static SuperKvClient Connect(string pipeName, string prefix) =>
    SuperKvClient.Connect(new SuperKvOptions { PipeName = pipeName, KeyPrefix = prefix });

static async Task RunSecondProcessAsync(string pipeName, string prefix)
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
    startInfo.ArgumentList.Add(pipeName);
    startInfo.ArgumentList.Add(prefix);

    using Process child = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Cannot start the child process.");
    string standardOutput = await child.StandardOutput.ReadToEndAsync();
    string standardError = await child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    if (child.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Child process failed with exit code {child.ExitCode}.{Environment.NewLine}{standardOutput}{standardError}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}