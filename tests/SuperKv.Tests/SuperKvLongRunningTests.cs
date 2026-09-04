using Xunit;
using StackExchange.Redis;

namespace SuperKv.Tests;

[Collection(GarnetServerCollection.Name)]
public sealed class SuperKvLongRunningTests
{
    readonly GarnetServerFixture _server;

    public SuperKvLongRunningTests(GarnetServerFixture server) => _server = server;

    [Fact]
    [Trait("Category", "LongRunning")]
    public async Task SustainedBoundaryConcurrencyAndPressure()
    {
        if (!StressSettings.LongRunningEnabled)
            return;

        int clientCount = StressSettings.PositiveInteger("SUPERKV_LONG_CLIENTS", 32, 128);
        int durationSeconds = StressSettings.PositiveInteger("SUPERKV_LONG_DURATION_SECONDS", 1800, 7200);
        string connectionMode = StressSettings.ConnectionMode;
        string prefix = $"soak:{Guid.NewGuid():N}:";
        int[] sizes = [0, 1, 16, 128, 1024, 64 * 1024, 1024 * 1024];
        byte[][] payloads = sizes.Select(size =>
        {
            byte[] value = new byte[size];
            Random.Shared.NextBytes(value);
            return value;
        }).ToArray();
        byte[][] hotValues = Enumerable.Range(1, clientCount)
            .Select(index => Enumerable.Repeat((byte)index, 4096).ToArray())
            .ToArray();
        var clients = new List<SuperKvClient>(clientCount);
        ConnectionMultiplexer? sharedConnection = null;
        long completedPairs = 0;

        try
        {
            if (connectionMode == "shared")
                sharedConnection = ConnectionMultiplexer.Connect(_server.ConnectionString);

            for (int i = 0; i < clientCount; i++)
            {
                clients.Add(sharedConnection is null
                    ? _server.Connect(prefix)
                    : SuperKvClient.Create(sharedConnection, prefix));
            }

            clients[0].Set("hot", hotValues[0]);
            using var duration = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));

            Task[] workers = clients.Select((client, clientIndex) => Task.Factory.StartNew(() =>
            {
                try
                {
                    int iteration = 0;
                    while (!duration.IsCancellationRequested)
                    {
                        int payloadIndex = (iteration + clientIndex) % payloads.Length;
                        byte[] expected = payloads[payloadIndex];
                        string key = $"client:{clientIndex}:size:{payloadIndex}:slot:{iteration & 7}";
                        client.Set(key, expected);
                        Assert.Equal(expected, client.Get(key));

                        if ((iteration & 15) == 0)
                        {
                            client.Set("hot", hotValues[clientIndex]);
                            byte[]? hot = client.Get("hot");
                            Assert.NotNull(hot);
                            Assert.Equal(4096, hot.Length);
                            Assert.InRange(hot[0], (byte)1, (byte)clientCount);
                            Assert.All(hot, value => Assert.Equal(hot[0], value));
                        }

                        if ((iteration & 127) == 0)
                            Assert.Null(client.Get($"missing:{clientIndex}:{iteration}"));

                        Interlocked.Increment(ref completedPairs);
                        iteration++;
                    }
                }
                catch
                {
                    duration.Cancel();
                    throw;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            Task allWorkers = Task.WhenAll(workers);
            await allWorkers.WaitAsync(TimeSpan.FromSeconds(durationSeconds + 15));
            Assert.True(completedPairs >= clientCount);
        }
        finally
        {
            foreach (SuperKvClient client in clients)
                client.Dispose();
            sharedConnection?.Dispose();
        }
    }
}

static class StressSettings
{
    public static bool LongRunningEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("SUPERKV_LONG_TESTS"), "1", StringComparison.Ordinal);

    public static string ConnectionMode
    {
        get
        {
            string mode = Environment.GetEnvironmentVariable("SUPERKV_LONG_CONNECTION_MODE") ?? "owned";
            if (mode is not ("owned" or "shared"))
                throw new InvalidOperationException(
                    "SUPERKV_LONG_CONNECTION_MODE must be 'owned' or 'shared'.");
            return mode;
        }
    }

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
