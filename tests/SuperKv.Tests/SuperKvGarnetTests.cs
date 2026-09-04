using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Garnet;
using Xunit;

namespace SuperKv.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GarnetServerCollection : ICollectionFixture<GarnetServerFixture>
{
    public const string Name = "GarnetServer";
}

public sealed class GarnetServerFixture : IDisposable
{
    readonly GarnetServer _server;

    public GarnetServerFixture()
    {
        int port = GetAvailablePort();
        ConnectionString = $"127.0.0.1:{port},connectTimeout=5000,syncTimeout=5000";
        _server = new GarnetServer(
        [
            "--bind", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--no-pubsub",
            "--no-obj",
            "--index", "16m",
            "--memory", "64m"
        ]);
        _server.Start();
    }

    public string ConnectionString { get; }

    public SuperKvClient Connect(string? prefix = null) =>
        SuperKvClient.Connect(new SuperKvOptions
        {
            Backend = SuperKvBackend.Garnet,
            GarnetConnectionString = ConnectionString,
            KeyPrefix = prefix ?? $"garnet-test:{Guid.NewGuid():N}:"
        });

    public void Dispose() => _server.Dispose();

    static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[Collection(GarnetServerCollection.Name)]
public sealed class SuperKvGarnetTests
{
    readonly GarnetServerFixture _server;

    public SuperKvGarnetTests(GarnetServerFixture server) => _server = server;

    [Fact]
    public void MissingEmptyBinaryAndOverwriteMatchMemoryBackend()
    {
        using SuperKvClient kv = _server.Connect();

        Assert.Null(kv.Get("missing"));

        kv.Set("value", ReadOnlyMemory<byte>.Empty);
        Assert.Empty(kv.Get("value")!);

        kv.Set("value", new byte[] { 0, 1, 127, 128, 255 });
        Assert.Equal(new byte[] { 0, 1, 127, 128, 255 }, kv.Get("value"));

        kv.Set("value", new byte[] { 9, 8 });
        Assert.Equal(new byte[] { 9, 8 }, kv.Get("value"));
    }

    [Fact]
    public void ClientsWithTheSamePrefixShareValues()
    {
        string prefix = $"garnet-shared:{Guid.NewGuid():N}:";
        using SuperKvClient first = _server.Connect(prefix);
        using SuperKvClient second = _server.Connect(prefix);

        first.Set("camera", new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, second.Get("camera"));
    }

    [Fact]
    public void OneClientSupportsConcurrentGetAndSet()
    {
        using SuperKvClient kv = _server.Connect();

        Parallel.For(0, 1_000, index =>
        {
            string key = $"key:{index}";
            byte[] value = BitConverter.GetBytes(index);
            kv.Set(key, value);
            Assert.Equal(value, kv.Get(key));
        });
    }

    [Fact]
    public async Task ApiDoesNotDependOnSynchronizationContext()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                using SuperKvClient kv = _server.Connect();
                kv.Set("key", new byte[] { 4, 5, 6 });
                Assert.Equal(new byte[] { 4, 5, 6 }, kv.Get("key"));
                completion.SetResult(true);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "SuperKv Garnet non-pumping synchronization-context test"
        };

        thread.Start();
        Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)));
    }

    sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
        }

        public override void Send(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("SuperKv attempted to use the synchronization context.");
    }
}
