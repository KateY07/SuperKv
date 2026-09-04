using Xunit;

namespace SuperKv.Tests;

[Collection(MemoryServerCollection.Name)]
public sealed class SuperKvContractTests
{
    readonly MemoryServerFixture _server;

    public SuperKvContractTests(MemoryServerFixture server) => _server = server;

    [Fact]
    public void ValuesRoundTripWithoutAliasing()
    {
        using SuperKvClient kv = _server.Connect();
        byte[] source = [0, 1, 127, 128, 255];

        kv.Set("bytes", source);
        source[0] = 42;
        byte[]? firstRead = kv.Get("bytes");
        Assert.Equal(new byte[] { 0, 1, 127, 128, 255 }, firstRead);

        firstRead![1] = 42;
        Assert.Equal(new byte[] { 0, 1, 127, 128, 255 }, kv.Get("bytes"));
    }

    [Fact]
    public void MissingEmptyAndOverwriteHaveStableSemantics()
    {
        using SuperKvClient kv = _server.Connect();

        Assert.Null(kv.Get("missing"));

        kv.Set("empty", ReadOnlyMemory<byte>.Empty);
        byte[]? empty = kv.Get("empty");
        Assert.NotNull(empty);
        Assert.Empty(empty);

        kv.Set("value", new byte[] { 1 });
        kv.Set("value", new byte[] { 2, 3 });
        Assert.Equal(new byte[] { 2, 3 }, kv.Get("value"));
    }

    [Fact]
    public void ClientsWithTheSamePrefixShareValues()
    {
        string prefix = $"shared:{Guid.NewGuid():N}:";
        using SuperKvClient first = _server.Connect(prefix);
        using SuperKvClient second = _server.Connect(prefix);

        first.Set("camera", new byte[] { 7, 8, 9 });
        Assert.Equal(new byte[] { 7, 8, 9 }, second.Get("camera"));

        second.Set("camera", new byte[] { 10 });
        Assert.Equal(new byte[] { 10 }, first.Get("camera"));
    }
}