using System.Net;
using System.Net.Sockets;
using Garnet;
using Xunit;

namespace SuperKv.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GarnetCollection : ICollectionFixture<GarnetFixture>
{
    public const string Name = "Garnet";
}

public sealed class GarnetFixture : IDisposable
{
    readonly GarnetServer _server;

    public GarnetFixture()
    {
        int port = GetFreeTcpPort();
        ConnectionString = $"127.0.0.1:{port},abortConnect=true,connectTimeout=5000,syncTimeout=5000";
        _server = new GarnetServer(["--bind", "127.0.0.1", "--port", port.ToString()]);
        _server.Start();
    }

    public string ConnectionString { get; }

    public async ValueTask<ISuperKv> OpenClientAsync(string? prefix = null) =>
        await SuperKvClient.OpenAsync(new SuperKvOptions
        {
            KeyPrefix = prefix ?? $"test:{Guid.NewGuid():N}:",
            Garnet = new GarnetOptions { ConnectionString = ConnectionString }
        });

    public void Dispose() => _server.Dispose();

    static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

static class TestWait
{
    public static async Task UntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!await predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not reached before the timeout.");
            await Task.Delay(20);
        }
    }
}