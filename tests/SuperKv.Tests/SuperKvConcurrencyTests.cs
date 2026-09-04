using Xunit;

namespace SuperKv.Tests;

[Collection(GarnetCollection.Name)]
public sealed class SuperKvConcurrencyTests
{
    readonly GarnetFixture _garnet;

    public SuperKvConcurrencyTests(GarnetFixture garnet) => _garnet = garnet;

    [Fact]
    public async Task SharedClientHandlesThirtyTwoConcurrentWriters()
    {
        await using ISuperKv kv = await _garnet.OpenClientAsync();
        const int workers = 32;
        int operationsPerWorker = StressSettings.OperationCount(250);

        Task[] tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < operationsPerWorker; i++)
                await kv.IncrementAsync("counter");
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(workers * operationsPerWorker, await kv.IncrementAsync("counter", 0));
    }

    [Fact]
    public async Task EightIndependentClientsDoNotLoseWrites()
    {
        string prefix = $"multi:{Guid.NewGuid():N}:";
        int operationsPerClient = StressSettings.OperationCount(200);
        var clients = new List<ISuperKv>();
        try
        {
            for (int i = 0; i < 8; i++)
                clients.Add(await _garnet.OpenClientAsync(prefix));

            Task[] tasks = clients.Select(client => Task.Run(async () =>
            {
                for (int i = 0; i < operationsPerClient; i++)
                    await client.IncrementAsync("counter");
            })).ToArray();

            await Task.WhenAll(tasks);
            Assert.Equal(8 * operationsPerClient, await clients[0].IncrementAsync("counter", 0));
        }
        finally
        {
            foreach (ISuperKv client in clients)
                await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task OnlyOneConcurrentCreateWins()
    {
        await using ISuperKv kv = await _garnet.OpenClientAsync();
        Task<bool>[] attempts = Enumerable.Range(0, 64)
            .Select(index => kv.SetStringAsync(
                "winner", index.ToString(), condition: SuperKvSetCondition.OnlyIfMissing).AsTask())
            .ToArray();

        bool[] results = await Task.WhenAll(attempts);
        Assert.Single(results, result => result);
        Assert.NotNull(await kv.GetStringAsync("winner"));
    }

    [Fact]
    public async Task ConcurrentHotReadsReturnUnchangedPayload()
    {
        await using ISuperKv kv = await _garnet.OpenClientAsync();
        int operationsPerReader = StressSettings.OperationCount(200);
        byte[] expected = new byte[4096];
        Random.Shared.NextBytes(expected);
        await kv.SetAsync("payload", expected);

        Task[] readers = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < operationsPerReader; i++)
                Assert.Equal(expected, await kv.GetAsync("payload"));
        })).ToArray();

        await Task.WhenAll(readers);
    }
}

static class StressSettings
{
    public static bool LongRunningEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("SUPERKV_LONG_TESTS"), "1", StringComparison.Ordinal);

    public static int OperationCount(int baseline) =>
        checked(baseline * PositiveInteger("SUPERKV_STRESS_MULTIPLIER", 1, 100));

    public static int PositiveInteger(string name, int fallback, int maximum)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        if (!int.TryParse(value, out int parsed) || parsed <= 0 || parsed > maximum)
            throw new InvalidOperationException($"{name} must be an integer from 1 through {maximum}.");

        return parsed;
    }
}