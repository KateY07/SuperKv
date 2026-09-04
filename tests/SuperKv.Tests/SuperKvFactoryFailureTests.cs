using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;
using Xunit;

namespace SuperKv.Tests;

[Collection(GarnetServerCollection.Name)]
public sealed class SuperKvFactoryFailureTests
{
    readonly GarnetServerFixture _fixture;

    public SuperKvFactoryFailureTests(GarnetServerFixture fixture) => _fixture = fixture;

    [Fact]
    public void SecondServerOnSameEndpointFailsWithoutStoppingFirstServer()
    {
        int port = GarnetServerFixture.GetAvailablePort();
        using SuperKvServer first = CreateServer(port);

        Assert.Throws<InvalidOperationException>(() => SuperKvServer.Create(
            new SuperKvServerOptions { Port = port, MemorySize = "64m" }));
        using SuperKvClient client = CreateClient(first.ConnectionString);
        client.Set("still-running", new byte[] { 1 });
        Assert.Equal(new byte[] { 1 }, client.Get("still-running"));
    }

    [Fact]
    public void ServerCanBeCreatedAgainOnSameEndpointAfterDispose()
    {
        int port = GarnetServerFixture.GetAvailablePort();
        SuperKvServer first = CreateServer(port);
        first.Dispose();
        first.Dispose();

        using SuperKvServer second = CreateServer(port);
        using SuperKvClient client = CreateClient(second.ConnectionString);
        client.Set("restarted", new byte[] { 2 });
        Assert.Equal(new byte[] { 2 }, client.Get("restarted"));
    }

    [Fact]
    public void RestartStartsWithAnEmptyInMemoryStore()
    {
        int port = GarnetServerFixture.GetAvailablePort();
        const string prefix = "restart-contract";

        using (SuperKvServer first = CreateServer(port))
        using (SuperKvClient writer = SuperKvClient.Create(
                   new SuperKvOptions
                   {
                       ConnectionString = first.ConnectionString,
                       KeyPrefix = prefix
                   }))
        {
            writer.Set("transient", new byte[] { 1 });
            Assert.Equal(new byte[] { 1 }, writer.Get("transient"));
        }

        using SuperKvServer second = CreateServer(port);
        using SuperKvClient reader = SuperKvClient.Create(
            new SuperKvOptions
            {
                ConnectionString = second.ConnectionString,
                KeyPrefix = prefix
            });
        Assert.Null(reader.Get("transient"));
    }

    [Fact]
    public void EndpointCanBeReusedAcrossRepeatedCreateDisposeCycles()
    {
        int port = GarnetServerFixture.GetAvailablePort();

        for (int i = 0; i < 10; i++)
        {
            using SuperKvServer server = CreateServer(port);
            using SuperKvClient client = CreateClient(server.ConnectionString);
            byte[] expected = BitConverter.GetBytes(i);
            client.Set("cycle", expected);
            Assert.Equal(expected, client.Get("cycle"));
        }
    }

    [Fact]
    public async Task ServersOnDifferentEndpointsCanBeCreatedConcurrently()
    {
        int firstPort = GarnetServerFixture.GetAvailablePort();
        int secondPort = GarnetServerFixture.GetAvailablePort();
        Assert.NotEqual(firstPort, secondPort);

        Task<SuperKvServer> firstTask = Task.Run(() => CreateServer(firstPort));
        Task<SuperKvServer> secondTask = Task.Run(() => CreateServer(secondPort));
        SuperKvServer[] servers = await Task.WhenAll(firstTask, secondTask)
            .WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            foreach (SuperKvServer server in servers)
            {
                using SuperKvClient client = CreateClient(server.ConnectionString);
                client.Set("ready", new byte[] { 3 });
                Assert.Equal(new byte[] { 3 }, client.Get("ready"));
            }
        }
        finally
        {
            foreach (SuperKvServer server in servers)
                server.Dispose();
        }
    }

    [Fact]
    public void OccupiedPortPreventsServerCreationAndRemainsOwnedByOriginalListener()
    {
        int port = GarnetServerFixture.GetAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        Assert.Throws<SocketException>(() => CreateServer(port));
        Assert.Equal(port, ((IPEndPoint)listener.LocalEndpoint).Port);
        listener.Stop();

        using SuperKvServer server = CreateServer(port);
        using SuperKvClient client = CreateClient(server.ConnectionString);
        client.Set("recovered", new byte[] { 4 });
        Assert.Equal(new byte[] { 4 }, client.Get("recovered"));
    }

    [Fact]
    public async Task ConcurrentCreationOnOneEndpointHasExactlyOneWinner()
    {
        int port = GarnetServerFixture.GetAvailablePort();
        using var start = new ManualResetEventSlim();
        Task<SuperKvServer?>[] attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                try
                {
                    return CreateServer(port);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }))
            .ToArray();

        start.Set();
        SuperKvServer?[] results = await Task.WhenAll(attempts)
            .WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            Assert.Single(results, result => result is not null);
        }
        finally
        {
            foreach (SuperKvServer? server in results)
                server?.Dispose();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("localhost")]
    [InlineData("0.0.0.0")]
    public void ServerRejectsNonLoopbackOrInvalidAddresses(string address)
    {
        Assert.Throws<ArgumentException>(() => SuperKvServer.Create(
            new SuperKvServerOptions { Address = address }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void ServerRejectsInvalidPorts(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SuperKvServer.Create(
            new SuperKvServerOptions { Port = port }));
    }

    [Fact]
    public void ServerRejectsBlankSizingOptions()
    {
        Assert.Throws<ArgumentException>(() => SuperKvServer.Create(
            new SuperKvServerOptions { IndexSize = " " }));
        Assert.Throws<ArgumentException>(() => SuperKvServer.Create(
            new SuperKvServerOptions { MemorySize = " " }));
    }

    [Fact]
    public void InvalidGarnetConfigurationDoesNotLeakEndpointOwnership()
    {
        int port = GarnetServerFixture.GetAvailablePort();

        Exception? exception = Record.Exception(() => SuperKvServer.Create(
            new SuperKvServerOptions
            {
                Port = port,
                IndexSize = "not-a-size",
                MemorySize = "64m"
            }));

        Assert.NotNull(exception);
        using SuperKvServer recovered = CreateServer(port);
        using SuperKvClient client = CreateClient(recovered.ConnectionString);
        client.Set("recovered", new byte[] { 7 });
        Assert.Equal(new byte[] { 7 }, client.Get("recovered"));
    }

    [Fact]
    public void DefaultFactoriesStartAndConnectToTheDefaultEndpoint()
    {
        using SuperKvServer server = SuperKvServer.Create();
        using SuperKvClient client = SuperKvClient.Create();

        client.Set("default", new byte[] { 5 });
        Assert.Equal(new byte[] { 5 }, client.Get("default"));
    }

    [Fact]
    public void Ipv6LoopbackProducesAUsableBracketedConnectionString()
    {
        if (!Socket.OSSupportsIPv6)
            return;

        int port = GarnetServerFixture.GetAvailablePort(IPAddress.IPv6Loopback);
        using SuperKvServer server = SuperKvServer.Create(new SuperKvServerOptions
        {
            Address = "::1",
            Port = port,
            MemorySize = "64m"
        });
        Assert.Equal($"[::1]:{port}", server.ConnectionString);

        using SuperKvClient client = CreateClient(server.ConnectionString);
        client.Set("ipv6", new byte[] { 6 });
        Assert.Equal(new byte[] { 6 }, client.Get("ipv6"));
    }

    [Fact]
    public void ClientRejectsInvalidOptionsBeforeOpeningAConnection()
    {
        Assert.Throws<ArgumentException>(() =>
            SuperKvClient.Create(new SuperKvOptions { ConnectionString = " " }));
        Assert.Throws<ArgumentNullException>(() =>
            SuperKvClient.Create(new SuperKvOptions
            {
                ConnectionString = _fixture.ConnectionString,
                KeyPrefix = null!
            }));

        foreach (TimeSpan timeout in new[]
                 {
                     TimeSpan.Zero,
                     TimeSpan.FromMilliseconds(-1),
                     TimeSpan.FromMilliseconds((double)int.MaxValue + 1)
                 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SuperKvClient.Create(new SuperKvOptions
                {
                    ConnectionString = _fixture.ConnectionString,
                    ConnectTimeout = timeout
                }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SuperKvClient.Create(new SuperKvOptions
                {
                    ConnectionString = _fixture.ConnectionString,
                    OperationTimeout = timeout
                }));
        }
    }

    [Fact]
    public void BorrowedMultiplexerFactoryRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SuperKvClient.Create((IConnectionMultiplexer)null!));

        using ConnectionMultiplexer connection =
            ConnectionMultiplexer.Connect(_fixture.ConnectionString);
        Assert.Throws<ArgumentNullException>(() =>
            SuperKvClient.Create(connection, null!));
    }

    [Fact]
    public void EmptyAndNullKeysAreRejectedForGetAndSet()
    {
        using SuperKvClient client = _fixture.Connect();

        Assert.Throws<ArgumentException>(() => client.Get(string.Empty));
        Assert.Throws<ArgumentNullException>(() => client.Get(null!));
        Assert.Throws<ArgumentException>(() => client.Set(string.Empty, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentNullException>(() => client.Set(null!, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void DisposedBorrowedMultiplexerMakesOperationsFail()
    {
        var connection = ConnectionMultiplexer.Connect(_fixture.ConnectionString);
        using SuperKvClient client = SuperKvClient.Create(connection);
        connection.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Get("key"));
        Assert.Throws<ObjectDisposedException>(() => client.Set("key", new byte[] { 1 }));
    }

    [Fact]
    public void ClientAndServerDisposeAreThreadSafeAndIdempotent()
    {
        int port = GarnetServerFixture.GetAvailablePort();
        using SuperKvServer server = CreateServer(port);
        using SuperKvClient client = CreateClient(server.ConnectionString);

        Parallel.For(0, 32, _ => client.Dispose());
        Parallel.For(0, 32, _ => server.Dispose());

        Assert.Throws<ObjectDisposedException>(() => client.Get("disposed"));
        using SuperKvServer restarted = CreateServer(port);
        using SuperKvClient healthy = CreateClient(restarted.ConnectionString);
        healthy.Set("healthy", new byte[] { 8 });
        Assert.Equal(new byte[] { 8 }, healthy.Get("healthy"));
    }

    [Fact]
    public async Task ExistingClientReconnectsAfterServerRestart()
    {
        int port = GarnetServerFixture.GetAvailablePort();
        SuperKvServer first = CreateServer(port);
        using SuperKvClient client = SuperKvClient.Create(new SuperKvOptions
        {
            ConnectionString = first.ConnectionString,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            OperationTimeout = TimeSpan.FromMilliseconds(250)
        });
        client.Set("before", new byte[] { 1 });
        first.Dispose();
        using SuperKvServer second = CreateServer(port);
        Exception? lastException = null;

        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                client.Set("after", new byte[] { 2 });
                Assert.Equal(new byte[] { 2 }, client.Get("after"));
                return;
            }
            catch (RedisException exception)
            {
                lastException = exception;
                await Task.Delay(100);
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"The existing client did not reconnect within 10 seconds: {lastException}");
    }

    [Fact]
    public async Task DisposingServerDuringConcurrentTrafficFailsCallsWithoutHanging()
    {
        int port = GarnetServerFixture.GetAvailablePort();
        SuperKvServer server = CreateServer(port);
        using ConnectionMultiplexer connection = ConnectionMultiplexer.Connect(
            $"{server.ConnectionString},connectTimeout=1000,syncTimeout=250");
        SuperKvClient[] clients = Enumerable.Range(0, 8)
            .Select(index => SuperKvClient.Create(connection, $"shutdown:{index}:"))
            .ToArray();
        using var start = new ManualResetEventSlim();
        int completed = 0;

        Task[] workers = clients.Select((client, index) => Task.Factory.StartNew(() =>
        {
            start.Wait();
            try
            {
                while (true)
                {
                    client.Set("value", BitConverter.GetBytes(index));
                    client.Get("value");
                    Interlocked.Increment(ref completed);
                }
            }
            catch (Exception exception) when (exception is RedisException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try
        {
            start.Set();
            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref completed) >= clients.Length,
                TimeSpan.FromSeconds(5)));
            server.Dispose();

            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            server.Dispose();
            foreach (SuperKvClient client in clients)
                client.Dispose();
        }
    }

    static SuperKvServer CreateServer(int port) => SuperKvServer.Create(
        new SuperKvServerOptions
        {
            Port = port,
            IndexSize = "16m",
            MemorySize = "64m"
        });

    static SuperKvClient CreateClient(string connectionString) => SuperKvClient.Create(
        new SuperKvOptions
        {
            ConnectionString = connectionString,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            OperationTimeout = TimeSpan.FromSeconds(5)
        });
}
