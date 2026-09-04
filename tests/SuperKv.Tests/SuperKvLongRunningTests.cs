using Xunit;

namespace SuperKv.Tests;

[Collection(GarnetCollection.Name)]
public sealed class SuperKvLongRunningTests
{
    readonly GarnetFixture _garnet;

    public SuperKvLongRunningTests(GarnetFixture garnet) => _garnet = garnet;

    [Fact]
    [Trait("Category", "LongRunning")]
    public async Task SustainedIndependentClientsPreserveAtomicWrites()
    {
        if (!StressSettings.LongRunningEnabled)
            return;

        int clientCount = StressSettings.PositiveInteger("SUPERKV_LONG_CLIENTS", 8, 128);
        int durationSeconds = StressSettings.PositiveInteger("SUPERKV_LONG_DURATION_SECONDS", 120, 3600);
        string prefix = $"soak:{Guid.NewGuid():N}:";
        var clients = new List<ISuperKv>(clientCount);

        try
        {
            for (int i = 0; i < clientCount; i++)
                clients.Add(await _garnet.OpenClientAsync(prefix));

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
            long expectedCount = 0;
            Task[] workers = clients.Select((client, index) => Task.Run(async () =>
            {
                string key = $"status:{index}";
                string value = $"client-{index}";

                while (!cancellation.IsCancellationRequested)
                {
                    Assert.True(await client.SetStringAsync(key, value));
                    Assert.Equal(value, await client.GetStringAsync(key));
                    await client.IncrementAsync("counter");
                    Interlocked.Increment(ref expectedCount);
                }
            })).ToArray();

            await Task.WhenAll(workers);
            Assert.True(expectedCount > 0);
            Assert.Equal(expectedCount, await clients[0].IncrementAsync("counter", 0));
        }
        finally
        {
            foreach (ISuperKv client in clients)
                await client.DisposeAsync();
        }
    }
}