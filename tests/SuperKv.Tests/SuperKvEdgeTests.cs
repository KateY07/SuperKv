using System.Text.Json;
using Xunit;

namespace SuperKv.Tests;

[Collection(GarnetCollection.Name)]
public sealed class SuperKvEdgeTests
{
    readonly GarnetFixture _garnet;

    public SuperKvEdgeTests(GarnetFixture garnet) => _garnet = garnet;

    [Fact]
    public async Task IncrementHandlesSignsInvalidDataAndOverflow()
    {
        await using ISuperKv kv = await _garnet.OpenClientAsync();

        Assert.Equal(1, await kv.IncrementAsync("new"));
        Assert.Equal(-1, await kv.IncrementAsync("new", -2));
        await kv.SetStringAsync("number", "41");
        Assert.Equal(42, await kv.IncrementAsync("number"));

        await kv.SetStringAsync("invalid", "not-a-number");
        await Assert.ThrowsAnyAsync<Exception>(async () => await kv.IncrementAsync("invalid"));
        await kv.SetStringAsync("overflow", long.MaxValue.ToString());
        await Assert.ThrowsAnyAsync<Exception>(async () => await kv.IncrementAsync("overflow"));
    }

    [Fact]
    public async Task PrefixesIsolateClients()
    {
        await using ISuperKv first = await _garnet.OpenClientAsync($"one:{Guid.NewGuid():N}:");
        await using ISuperKv second = await _garnet.OpenClientAsync($"two:{Guid.NewGuid():N}:");

        await first.SetStringAsync("key", "first");
        await second.SetStringAsync("key", "second");
        Assert.Equal("first", await first.GetStringAsync("key"));
        Assert.Equal("second", await second.GetStringAsync("key"));
    }

    [Fact]
    public async Task JsonOptionsAndInvalidJsonAreHonored()
    {
        await using ISuperKv kv = await _garnet.OpenClientAsync();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var expected = new JsonValue("camera", 7);

        await kv.SetJsonAsync("valid", expected, serializerOptions: options);
        Assert.Equal(expected, await kv.GetJsonAsync<JsonValue>("valid", options));
        await kv.SetStringAsync("invalid", "not-json");
        await Assert.ThrowsAsync<JsonException>(async () => await kv.GetJsonAsync<JsonValue>("invalid"));
    }

    [Fact]
    public async Task OptionsKeysTtlAndConditionAreValidated()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SuperKvClient.OpenAsync(
            new SuperKvOptions { Garnet = null! }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await SuperKvClient.OpenAsync(
            new SuperKvOptions { KeyPrefix = null! }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await SuperKvClient.OpenAsync(
            new SuperKvOptions { Garnet = new GarnetOptions { ConnectionString = " " } }));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await SuperKvClient.OpenAsync(
            new SuperKvOptions(), new CancellationToken(canceled: true)));

        await using ISuperKv kv = await _garnet.OpenClientAsync();
        Assert.Throws<ArgumentException>(() => kv.GetAsync(string.Empty));
        Assert.Throws<ArgumentException>(() => kv.SetAsync(string.Empty, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => kv.DeleteAsync(string.Empty));
        Assert.Throws<ArgumentException>(() => kv.ExistsAsync(string.Empty));
        Assert.Throws<ArgumentException>(() => kv.IncrementAsync(string.Empty));
        Assert.Throws<ArgumentException>(() => kv.GetTimeToLiveAsync(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => kv.SetAsync("key", ReadOnlyMemory<byte>.Empty, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => kv.SetAsync("key", ReadOnlyMemory<byte>.Empty, TimeSpan.FromTicks(-1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await kv.SetAsync(
            "key", ReadOnlyMemory<byte>.Empty, condition: (SuperKvSetCondition)999));
    }

    [Fact]
    public async Task DisposalIsIdempotentAndRejectsFurtherCalls()
    {
        ISuperKv kv = await _garnet.OpenClientAsync();
        await kv.DisposeAsync();
        await kv.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => kv.GetAsync("key"));
        Assert.Throws<ObjectDisposedException>(() => kv.SetAsync("key", ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ObjectDisposedException>(() => kv.DeleteAsync("key"));
        Assert.Throws<ObjectDisposedException>(() => kv.ExistsAsync("key"));
        Assert.Throws<ObjectDisposedException>(() => kv.IncrementAsync("key"));
        Assert.Throws<ObjectDisposedException>(() => kv.GetTimeToLiveAsync("key"));
    }

    [Fact]
    public async Task ExtensionsRejectNullReceiversAndNullStrings()
    {
        ISuperKv? missing = null;
        Assert.Throws<ArgumentNullException>(() => missing!.SetStringAsync("key", "value"));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await missing!.GetStringAsync("key"));
        Assert.Throws<ArgumentNullException>(() => missing!.SetJsonAsync("key", new JsonValue("x", 1)));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await missing!.GetJsonAsync<JsonValue>("key"));

        await using ISuperKv kv = await _garnet.OpenClientAsync();
        Assert.Throws<ArgumentNullException>(() => kv.SetStringAsync("key", null!));
    }

    sealed record JsonValue(string Name, int Count);
}