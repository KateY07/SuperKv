using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;
using Xunit;

namespace SuperKv.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GarnetServerCollection : ICollectionFixture<GarnetServerFixture>
{
    public const string Name = "GarnetServer";
}

public sealed class GarnetServerFixture : IDisposable
{
    readonly SuperKvServer _server;

    public GarnetServerFixture()
    {
        int port = GetAvailablePort(IPAddress.Loopback);
        _server = SuperKvServer.Create(new SuperKvServerOptions
        {
            Port = port,
            IndexSize = "16m",
            MemorySize = "1g"
        });
        ConnectionString = _server.ConnectionString;
    }

    public string ConnectionString { get; }

    public SuperKvClient Connect(string? prefix = null) =>
        SuperKvClient.Create(new SuperKvOptions
        {
            ConnectionString = ConnectionString,
            KeyPrefix = prefix ?? $"garnet-test:{Guid.NewGuid():N}:"
        });

    public void Dispose() => _server.Dispose();

    internal static int GetAvailablePort() => GetAvailablePort(IPAddress.Loopback);

    internal static int GetAvailablePort(IPAddress address)
    {
        using var listener = new TcpListener(address, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[Collection(GarnetServerCollection.Name)]
public sealed class SuperKvGarnetTests
{
    static readonly string[] ExpectedClientMethods = ["Create", "Dispose", "Get", "Set"];
    readonly GarnetServerFixture _server;

    public SuperKvGarnetTests(GarnetServerFixture server) => _server = server;

    [Fact]
    public void MissingEmptyBinaryAndOverwriteHaveStableSemantics()
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

        Parallel.For(
            0,
            StressSettings.OperationCount(2_000),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            index =>
        {
            string key = $"key:{index}";
            byte[] value = BitConverter.GetBytes(index);
            kv.Set(key, value);
            Assert.Equal(value, kv.Get(key));
        });
    }

    [Fact]
    public void UnicodeKeysAndLargeBinaryValuesRoundTrip()
    {
        using SuperKvClient kv = _server.Connect("garnet:边界:📷:");
        string key = "相机:" + new string('k', 4096);
        byte[] expected = new byte[1024 * 1024];
        Random.Shared.NextBytes(expected);

        kv.Set(key, expected);

        Assert.Equal(expected, kv.Get(key));
    }

    [Fact]
    public void ValuesRoundTripWithoutAliasing()
    {
        using SuperKvClient kv = _server.Connect();
        byte[] source = [0, 1, 127, 128, 255];

        kv.Set("bytes", source);
        source[0] = 42;
        byte[] firstRead = kv.Get("bytes")!;
        Assert.Equal(new byte[] { 0, 1, 127, 128, 255 }, firstRead);

        firstRead[1] = 42;
        Assert.Equal(new byte[] { 0, 1, 127, 128, 255 }, kv.Get("bytes"));
    }

    [Fact]
    public async Task IndependentClientsCanReadAndOverwriteOneHotKeyConcurrently()
    {
        string prefix = $"garnet-hot:{Guid.NewGuid():N}:";
        using SuperKvClient first = _server.Connect(prefix);
        using SuperKvClient second = _server.Connect(prefix);
        using SuperKvClient reader = _server.Connect(prefix);
        byte[] firstValue = new byte[4096];
        byte[] secondValue = new byte[4096];
        Array.Fill(firstValue, (byte)1);
        Array.Fill(secondValue, (byte)2);
        first.Set("hot", firstValue);

        Task firstWriter = Task.Run(() =>
        {
            for (int i = 0; i < StressSettings.OperationCount(500); i++)
                first.Set("hot", firstValue);
        });
        Task secondWriter = Task.Run(() =>
        {
            for (int i = 0; i < StressSettings.OperationCount(500); i++)
                second.Set("hot", secondValue);
        });
        Task hotReader = Task.Run(() =>
        {
            for (int i = 0; i < StressSettings.OperationCount(500); i++)
            {
                byte[] actual = reader.Get("hot")!;
                Assert.True(actual.SequenceEqual(firstValue) || actual.SequenceEqual(secondValue));
            }
        });

        await Task.WhenAll(firstWriter, secondWriter, hotReader)
            .WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void DisposalIsIdempotentAndRejectsFurtherCalls()
    {
        SuperKvClient client = _server.Connect();
        client.Dispose();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Get("key"));
        Assert.Throws<ObjectDisposedException>(() => client.Set("key", ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void DistinctPrefixAndKeyBoundariesCannotCollide()
    {
        using SuperKvClient first = _server.Connect("ab");
        using SuperKvClient second = _server.Connect("a");

        first.Set("c", new byte[] { 1 });
        second.Set("bc", new byte[] { 2 });

        Assert.Equal(new byte[] { 1 }, first.Get("c"));
        Assert.Equal(new byte[] { 2 }, second.Get("bc"));
    }

    [Fact]
    public void SharedMultiplexerIsNotOwnedByClients()
    {
        using ConnectionMultiplexer connection = ConnectionMultiplexer.Connect(_server.ConnectionString);
        SuperKvClient first = SuperKvClient.Create(connection, "shared:");
        using SuperKvClient second = SuperKvClient.Create(connection, "shared:");

        first.Set("value", new byte[] { 1 });
        first.Dispose();

        second.Set("value", new byte[] { 2 });
        Assert.Equal(new byte[] { 2 }, second.Get("value"));
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task ConnectionTimeoutIsBounded()
    {
        int port = GarnetServerFixture.GetAvailablePort();
        var options = new SuperKvOptions
        {
            ConnectionString = $"127.0.0.1:{port}",
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            OperationTimeout = TimeSpan.FromMilliseconds(100)
        };

        await Assert.ThrowsAnyAsync<RedisConnectionException>(async () =>
        {
            using SuperKvClient client = await Task.Run(() => SuperKvClient.Create(options))
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        });
    }

    [Fact]
    public void OperationFailureIsBoundedAfterServerStops()
    {
        int port = GarnetServerFixture.GetAvailablePort();
        using SuperKvServer server = SuperKvServer.Create(new SuperKvServerOptions
        {
            Port = port,
            MemorySize = "64m"
        });
        using SuperKvClient client = SuperKvClient.Create(new SuperKvOptions
        {
            ConnectionString = server.ConnectionString,
            OperationTimeout = TimeSpan.FromMilliseconds(100)
        });
        client.Set("before-stop", new byte[] { 1 });
        server.Dispose();
        var stopwatch = Stopwatch.StartNew();

        Exception? exception = Record.Exception(() => client.Get("after-stop"));

        stopwatch.Stop();
        Assert.NotNull(exception);
        Assert.IsAssignableFrom<RedisException>(exception);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void OptionsAndFactoryOnlyApiAreValidated()
    {
        Assert.Throws<ArgumentException>(() =>
            SuperKvClient.Create(new SuperKvOptions { ConnectionString = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SuperKvClient.Create(new SuperKvOptions { ConnectTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SuperKvClient.Create(new SuperKvOptions { OperationTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() =>
            SuperKvServer.Create(new SuperKvServerOptions { Address = "0.0.0.0" }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SuperKvServer.Create(new SuperKvServerOptions { Port = 0 }));
        Assert.Empty(typeof(SuperKvClient).GetConstructors());
        Assert.Empty(typeof(SuperKvServer).GetConstructors());

        string[] clientMethods = typeof(SuperKvClient)
            .GetMethods(System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Distinct()
            .Order()
            .ToArray();
        Assert.Equal(ExpectedClientMethods, clientMethods);
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
