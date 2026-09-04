using Xunit;

namespace SuperKv.Tests;

[Collection(GarnetCollection.Name)]
public sealed class SuperKvContractTests
{
    readonly GarnetFixture _garnet;

    public SuperKvContractTests(GarnetFixture garnet) => _garnet = garnet;

    [Fact]
    public async Task ValuesRoundTripWithoutAliasing()
    {
        await using ISuperKv kv = await _garnet.OpenClientAsync();
        byte[] source = [0, 1, 127, 128, 255];

        Assert.True(await kv.SetAsync("bytes", source));
        source[0] = 42;
        byte[]? firstRead = await kv.GetAsync("bytes");
        Assert.Equal(new byte[] { 0, 1, 127, 128, 255 }, firstRead);

        firstRead![1] = 42;
        Assert.Equal(new byte[] { 0, 1, 127, 128, 255 }, await kv.GetAsync("bytes"));
        Assert.True(await kv.SetAsync("empty", ReadOnlyMemory<byte>.Empty));
        Assert.Empty((await kv.GetAsync("empty"))!);

        Assert.True(await kv.SetStringAsync("text", "相机-🚀"));
        Assert.Equal("相机-🚀", await kv.GetStringAsync("text"));
        var expected = new CameraState("exposing", 42);
        Assert.True(await kv.SetJsonAsync("json", expected));
        Assert.Equal(expected, await kv.GetJsonAsync<CameraState>("json"));
    }

    [Fact]
    public async Task MissingDeleteAndExistsHaveStableSemantics()
    {
        await using ISuperKv kv = await _garnet.OpenClientAsync();

        Assert.Null(await kv.GetAsync("missing"));
        Assert.Null(await kv.GetStringAsync("missing"));
        Assert.Null(await kv.GetJsonAsync<CameraState>("missing"));
        Assert.Null(await kv.GetTimeToLiveAsync("missing"));
        Assert.False(await kv.ExistsAsync("missing"));
        Assert.False(await kv.DeleteAsync("missing"));

        await kv.SetStringAsync("present", "value");
        Assert.True(await kv.ExistsAsync("present"));
        Assert.True(await kv.DeleteAsync("present"));
        Assert.False(await kv.ExistsAsync("present"));
        Assert.False(await kv.DeleteAsync("present"));
    }

    [Fact]
    public async Task SetConditionsCoverEveryBranch()
    {
        await using ISuperKv kv = await _garnet.OpenClientAsync();

        Assert.False(await kv.SetStringAsync(
            "key", "value", condition: SuperKvSetCondition.OnlyIfPresent));
        Assert.True(await kv.SetStringAsync(
            "key", "first", condition: SuperKvSetCondition.OnlyIfMissing));
        Assert.False(await kv.SetStringAsync(
            "key", "ignored", condition: SuperKvSetCondition.OnlyIfMissing));
        Assert.True(await kv.SetStringAsync(
            "key", "second", condition: SuperKvSetCondition.OnlyIfPresent));
        Assert.Equal("second", await kv.GetStringAsync("key"));
        Assert.True(await kv.SetStringAsync("key", "third"));
        Assert.Equal("third", await kv.GetStringAsync("key"));
    }

    [Fact]
    public async Task TtlIsReportedPreservedAndExpires()
    {
        await using ISuperKv kv = await _garnet.OpenClientAsync();

        await kv.SetStringAsync("permanent", "value");
        Assert.Null(await kv.GetTimeToLiveAsync("permanent"));

        await kv.SetStringAsync("expiring", "value", TimeSpan.FromSeconds(2));
        TimeSpan? ttl = await kv.GetTimeToLiveAsync("expiring");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        await kv.SetStringAsync("counter", "10", TimeSpan.FromSeconds(2));
        Assert.Equal(11, await kv.IncrementAsync("counter"));
        Assert.NotNull(await kv.GetTimeToLiveAsync("counter"));

        await kv.SetStringAsync("short", "value", TimeSpan.FromMilliseconds(150));
        await TestWait.UntilAsync(async () => !await kv.ExistsAsync("short"), TimeSpan.FromSeconds(3));
        Assert.Null(await kv.GetAsync("short"));
        Assert.Null(await kv.GetTimeToLiveAsync("short"));
        await kv.DeleteAsync("short");
        Assert.False(await kv.ExistsAsync("short"));
        Assert.True(await kv.SetStringAsync(
            "short", "renewed", condition: SuperKvSetCondition.OnlyIfMissing));
    }

    sealed record CameraState(string Status, long Frame);
}