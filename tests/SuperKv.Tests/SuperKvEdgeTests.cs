using System.Buffers.Binary;
using System.IO.Pipes;
using Xunit;

namespace SuperKv.Tests;

[Collection(MemoryServerCollection.Name)]
public sealed class SuperKvEdgeTests
{
    readonly MemoryServerFixture _server;

    public SuperKvEdgeTests(MemoryServerFixture server) => _server = server;

    [Fact]
    public void PrefixesUnicodeKeysAndLargeValuesRoundTrip()
    {
        string firstPrefix = $"one:{Guid.NewGuid():N}:";
        string secondPrefix = $"two:{Guid.NewGuid():N}:";
        using SuperKvClient first = _server.Connect(firstPrefix);
        using SuperKvClient second = _server.Connect(secondPrefix);
        string key = "相机:📷:" + new string('k', 4096);
        byte[] large = new byte[1024 * 1024];
        Random.Shared.NextBytes(large);

        first.Set(key, large);
        second.Set(key, new byte[] { 1 });

        Assert.Equal(large, first.Get(key));
        Assert.Equal(new byte[] { 1 }, second.Get(key));
    }

    [Fact]
    public void OptionsAndKeysAreValidated()
    {
        Assert.Equal(SuperKvBackend.Memory, new SuperKvOptions().Backend);
        Assert.Throws<ArgumentException>(() => SuperKvClient.Connect(
            new SuperKvOptions { PipeName = " " }));
        Assert.Throws<ArgumentException>(() => SuperKvClient.Connect(
            new SuperKvOptions
            {
                Backend = SuperKvBackend.Garnet,
                GarnetConnectionString = " "
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SuperKvClient.Connect(
            new SuperKvOptions { Backend = (SuperKvBackend)42 }));
        Assert.ThrowsAny<ArgumentException>(() => SuperKvClient.Connect(
            new SuperKvOptions { KeyPrefix = null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SuperKvClient.Connect(
            new SuperKvOptions { ConnectTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SuperKvClient.Connect(
            new SuperKvOptions { ConnectTimeout = TimeSpan.FromDays(30) }));
        Assert.Throws<ArgumentException>(() => new SuperKvMemoryServer(
            new SuperKvServerOptions { PipeName = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SuperKvMemoryServer(
            new SuperKvServerOptions { RequestTimeout = TimeSpan.Zero }));

        using SuperKvClient kv = _server.Connect();
        Assert.Throws<ArgumentException>(() => kv.Get(string.Empty));
        Assert.Throws<ArgumentException>(() => kv.Set(string.Empty, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void ServerMustBeStartedExplicitlyAndConnectTimeoutIsBounded()
    {
        var options = new SuperKvOptions
        {
            PipeName = $"SuperKv.Missing.{Guid.NewGuid():N}",
            ConnectTimeout = TimeSpan.FromMilliseconds(50)
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        TimeoutException exception = Assert.Throws<TimeoutException>(() => SuperKvClient.Connect(options));

        stopwatch.Stop();
        Assert.Contains("Start the server first", exception.Message);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IncompleteFrameTimesOutAndOnlyThatConnectionIsClosed()
    {
        string pipeName = $"SuperKv.Partial.{Guid.NewGuid():N}";
        using var shutdown = new CancellationTokenSource();
        var server = new SuperKvMemoryServer(new SuperKvServerOptions
        {
            PipeName = pipeName,
            RequestTimeout = TimeSpan.FromMilliseconds(100)
        });
        Task serverTask = Task.Factory.StartNew(
            () => server.Run(shutdown.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            using var stalled = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
            stalled.Connect(1000);

            byte[] length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, 4096);
            stalled.Write(length);
            stalled.WriteByte(1);
            stalled.Flush();

            await Task.Delay(300);
            bool disconnected = await Task.Run(() =>
            {
                try
                {
                    return stalled.ReadByte() < 0;
                }
                catch (IOException)
                {
                    return true;
                }
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(disconnected);

            using SuperKvClient healthy = SuperKvClient.Connect(new SuperKvOptions { PipeName = pipeName });
            healthy.Set("after-timeout", new byte[] { 7 });
            Assert.Equal(new byte[] { 7 }, healthy.Get("after-timeout"));
        }
        finally
        {
            await shutdown.CancelAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task ExactlyOneServerOwnsAPipe()
    {
        string pipeName = $"SuperKv.Ownership.{Guid.NewGuid():N}";
        using var shutdown = new CancellationTokenSource();
        var first = new SuperKvMemoryServer(new SuperKvServerOptions { PipeName = pipeName });
        var second = new SuperKvMemoryServer(new SuperKvServerOptions { PipeName = pipeName });
        Task firstTask = Task.Factory.StartNew(
            () => first.Run(shutdown.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        using SuperKvClient readiness = SuperKvClient.Connect(new SuperKvOptions { PipeName = pipeName });

        Assert.Throws<InvalidOperationException>(() => second.Run(shutdown.Token));
        Assert.Throws<InvalidOperationException>(() => first.Run(shutdown.Token));

        await shutdown.CancelAsync();
        await firstTask;
    }

    [Fact]
    public void ClientPublicApiIsExactlyConnectGetSetAndDispose()
    {
        Assert.Empty(typeof(SuperKvClient).GetConstructors());

        string[] methods = typeof(SuperKvClient)
            .GetMethods(System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Order()
            .ToArray();

        Assert.Equal(new[] { "Connect", "Dispose", "Get", "Set" }, methods);
    }

    [Fact]
    public void DisposalIsIdempotentAndRejectsFurtherCalls()
    {
        SuperKvClient kv = _server.Connect();
        kv.Dispose();
        kv.Dispose();

        Assert.Throws<ObjectDisposedException>(() => kv.Get("key"));
        Assert.Throws<ObjectDisposedException>(() => kv.Set("key", ReadOnlyMemory<byte>.Empty));
    }
}
