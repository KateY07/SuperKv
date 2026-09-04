using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SuperKv;

if (args is ["--child", var childConnectionString, var childPrefix])
{
    using SuperKvClient child = Connect(childConnectionString, childPrefix);
    Assert(Encoding.UTF8.GetString(child.Get("status")!) == "running",
        "The child should see the parent value.");
    child.Set("child", Encoding.UTF8.GetBytes("written"));
    return;
}

int port = GetAvailablePort();
string connectionString = $"127.0.0.1:{port},connectTimeout=5000,syncTimeout=5000";
string prefix = $"smoke:{Guid.NewGuid():N}:";
using SuperKvServer server = SuperKvServer.Create(new SuperKvServerOptions
{
    Port = port,
    IndexSize = "16m",
    MemorySize = "64m"
});

using SuperKvClient first = Connect(connectionString, prefix);
using SuperKvClient second = Connect(connectionString, prefix);
first.Set("status", Encoding.UTF8.GetBytes("running"));
Assert(Encoding.UTF8.GetString(second.Get("status")!) == "running",
    "A second client should see the value.");

second.Set("sync", new byte[] { 1, 2, 3 });
Assert(first.Get("sync")!.SequenceEqual(new byte[] { 1, 2, 3 }),
    "Synchronous Get/Set should work across clients.");

await RunSecondProcessAsync(connectionString, prefix).ConfigureAwait(false);
Assert(Encoding.UTF8.GetString(first.Get("child")!) == "written",
    "The parent should see the child process write.");

Console.WriteLine("SuperKv Garnet cross-process Get/Set smoke tests passed.");

static SuperKvClient Connect(string connectionString, string prefix) =>
    SuperKvClient.Create(new SuperKvOptions
    {
        ConnectionString = connectionString,
        KeyPrefix = prefix
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
    string standardOutput = await child.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
    string standardError = await child.StandardError.ReadToEndAsync().ConfigureAwait(false);
    await child.WaitForExitAsync().ConfigureAwait(false);
    if (child.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Child process failed with exit code {child.ExitCode}.{Environment.NewLine}{standardOutput}{standardError}");
    }
}

static int GetAvailablePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
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
