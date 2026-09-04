using Xunit;

namespace SuperKv.Tests;

[Collection(MemoryServerCollection.Name)]
public sealed class SuperKvLongRunningTests
{
    readonly MemoryServerFixture _server;

    public SuperKvLongRunningTests(MemoryServerFixture server) => _server = server;

    [Fact]
    [Trait("Category", "LongRunning")]
    public async Task SustainedBoundaryConcurrencyAndPressure()
    {
        if (!StressSettings.LongRunningEnabled)
            return;

        int clientCount = StressSettings.PositiveInteger("SUPERKV_LONG_CLIENTS", 32, 128);
        int durationSeconds = StressSettings.PositiveInteger("SUPERKV_LONG_DURATION_SECONDS", 1800, 7200);
        string prefix = $"soak:{Guid.NewGuid():N}:";
        int[] sizes = [0, 1, 16, 128, 1024, 64 * 1024, 1024 * 1024];
        byte[][] payloads = sizes.Select(size =>
        {
            byte[] value = new byte[size];
            Random.Shared.NextBytes(value);
            return value;
        }).ToArray();
        byte[] hotValue = new byte[4096];
        Random.Shared.NextBytes(hotValue);
        var clients = new List<SuperKvClient>(clientCount);
        long completedPairs = 0;

        try
        {
            for (int i = 0; i < clientCount; i++)
                clients.Add(_server.Connect(prefix));

            clients[0].Set("hot", hotValue);
            using var duration = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));

            Task[] workers = clients.Select((client, clientIndex) => Task.Factory.StartNew(() =>
            {
                int iteration = 0;
                while (!duration.IsCancellationRequested)
                {
                    byte[] expected = payloads[(iteration + clientIndex) % payloads.Length];
                    string key = $"client:{clientIndex}:slot:{iteration & 63}";
                    client.Set(key, expected);
                    Assert.Equal(expected, client.Get(key));

                    if ((iteration & 15) == 0)
                        Assert.Equal(hotValue, client.Get("hot"));

                    Interlocked.Increment(ref completedPairs);
                    iteration++;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            await Task.WhenAll(workers);
            Assert.True(completedPairs >= clientCount);
        }
        finally
        {
            foreach (SuperKvClient client in clients)
                client.Dispose();
        }
    }
}