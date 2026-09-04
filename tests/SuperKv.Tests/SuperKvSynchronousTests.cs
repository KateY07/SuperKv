using Xunit;

namespace SuperKv.Tests;

[Collection(GarnetCollection.Name)]
public sealed class SuperKvSynchronousTests
{
    readonly GarnetFixture _garnet;

    public SuperKvSynchronousTests(GarnetFixture garnet) => _garnet = garnet;

    [Fact]
    public void SynchronousApiCoversBytesStringsJsonTtlConditionsAndCounters()
    {
        using ISuperKv kv = SuperKvClient.Open(new SuperKvOptions
        {
            KeyPrefix = $"sync:{Guid.NewGuid():N}:",
            Garnet = new GarnetOptions { ConnectionString = _garnet.ConnectionString }
        });

        byte[] source = [0, 1, 127, 128, 255];
        Assert.True(kv.SetValue("bytes", source));
        source[0] = 42;
        Assert.Equal(new byte[] { 0, 1, 127, 128, 255 }, kv.GetValue("bytes"));
        Assert.Null(kv.GetValue("missing"));

        Assert.True(kv.SetString("text", "相机-🚀"));
        Assert.Equal("相机-🚀", kv.GetString("text"));
        Assert.Null(kv.GetString("missing"));

        var expected = new CameraState("capturing", 17);
        Assert.True(kv.SetJson("json", expected));
        Assert.Equal(expected, kv.GetJson<CameraState>("json"));
        Assert.Null(kv.GetJson<CameraState>("missing"));

        Assert.False(kv.SetString("conditional", "missing", condition: SuperKvSetCondition.OnlyIfPresent));
        Assert.True(kv.SetString("conditional", "first", condition: SuperKvSetCondition.OnlyIfMissing));
        Assert.False(kv.SetString("conditional", "ignored", condition: SuperKvSetCondition.OnlyIfMissing));
        Assert.True(kv.SetString("conditional", "second", condition: SuperKvSetCondition.OnlyIfPresent));
        Assert.Equal("second", kv.GetString("conditional"));

        Assert.True(kv.SetString("ttl", "value", TimeSpan.FromSeconds(10)));
        Assert.InRange(kv.GetTimeToLive("ttl")!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Assert.Null(kv.GetTimeToLive("missing"));

        Assert.Equal(1, kv.Increment("counter"));
        Assert.Equal(5, kv.Increment("counter", 4));
        Assert.True(kv.Exists("counter"));
        Assert.True(kv.Delete("counter"));
        Assert.False(kv.Exists("counter"));
        Assert.False(kv.Delete("counter"));
    }

    [Fact]
    public void SynchronousApiValidatesInputsAndDisposal()
    {
        ISuperKv kv = SuperKvClient.Open(new SuperKvOptions
        {
            KeyPrefix = $"sync-edge:{Guid.NewGuid():N}:",
            Garnet = new GarnetOptions { ConnectionString = _garnet.ConnectionString }
        });

        Assert.Throws<ArgumentException>(() => kv.GetValue(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            kv.SetValue("key", ReadOnlyMemory<byte>.Empty, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            kv.SetValue("key", ReadOnlyMemory<byte>.Empty, condition: (SuperKvSetCondition)999));
        Assert.Throws<ArgumentNullException>(() => kv.SetString("key", null!));

        kv.Dispose();
        kv.Dispose();
        Assert.Throws<ObjectDisposedException>(() => kv.GetValue("key"));
    }

    sealed record CameraState(string Status, long Frame);
}