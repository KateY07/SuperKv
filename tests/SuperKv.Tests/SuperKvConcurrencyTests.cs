using Xunit;

namespace SuperKv.Tests;

[Collection(MemoryServerCollection.Name)]
public sealed class SuperKvConcurrencyTests
{
    readonly MemoryServerFixture _server;

    public SuperKvConcurrencyTests(MemoryServerFixture server) => _server = server;

    [Fact]
    public async Task SharedClientSerializesThirtyTwoConcurrentCallersWithoutFrameCorruption()
    {
        using SuperKvClient kv = _server.Connect();
        const int workers = 32;
        int operationsPerWorker = StressSettings.OperationCount(200);

        Task[] tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            string key = $"worker:{worker}";
            for (int iteration = 0; iteration < operationsPerWorker; iteration++)
            {
                byte[] expected = BitConverter.GetBytes(((long)worker << 32) | (uint)iteration);
                kv.Set(key, expected);
                Assert.Equal(expected, kv.Get(key));
            }
        })).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task IndependentClientsWriteToConcurrentDictionaryInParallel()
    {
        const int clientCount = 12;
        int operationsPerClient = StressSettings.OperationCount(150);
        string prefix = $"multi:{Guid.NewGuid():N}:";
        var clients = new List<SuperKvClient>(clientCount);

        try
        {
            for (int i = 0; i < clientCount; i++)
                clients.Add(_server.Connect(prefix));

            Task[] tasks = clients.Select((client, clientIndex) => Task.Run(() =>
            {
                for (int iteration = 0; iteration < operationsPerClient; iteration++)
                {
                    string key = $"client:{clientIndex}:item:{iteration}";
                    client.Set(key, BitConverter.GetBytes(iteration));
                }
            })).ToArray();

            await Task.WhenAll(tasks);

            for (int clientIndex = 0; clientIndex < clientCount; clientIndex++)
            {
                for (int iteration = 0; iteration < operationsPerClient; iteration++)
                {
                    string key = $"client:{clientIndex}:item:{iteration}";
                    Assert.Equal(BitConverter.GetBytes(iteration), clients[0].Get(key));
                }
            }
        }
        finally
        {
            foreach (SuperKvClient client in clients)
                client.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentHotReadsReturnUnchangedPayload()
    {
        string prefix = $"reads:{Guid.NewGuid():N}:";
        byte[] expected = new byte[64 * 1024];
        Random.Shared.NextBytes(expected);
        var clients = new List<SuperKvClient>();

        try
        {
            for (int i = 0; i < 16; i++)
                clients.Add(_server.Connect(prefix));

            clients[0].Set("payload", expected);
            int readsPerClient = StressSettings.OperationCount(100);
            Task[] readers = clients.Select(client => Task.Run(() =>
            {
                for (int i = 0; i < readsPerClient; i++)
                    Assert.Equal(expected, client.Get("payload"));
            })).ToArray();

            await Task.WhenAll(readers);
        }
        finally
        {
            foreach (SuperKvClient client in clients)
                client.Dispose();
        }
    }

    [Fact]
    public async Task IndependentClientsCanOverwriteOneHotKeyConcurrently()
    {
        string prefix = $"writes:{Guid.NewGuid():N}:";
        byte[] value = new byte[4096];
        Random.Shared.NextBytes(value);
        var clients = Enumerable.Range(0, 16)
            .Select(_ => _server.Connect(prefix))
            .ToArray();

        try
        {
            int writesPerClient = StressSettings.OperationCount(200);
            await Task.WhenAll(clients.Select(client => Task.Run(() =>
            {
                for (int i = 0; i < writesPerClient; i++)
                    client.Set("hot", value);
            })));

            Assert.Equal(value, clients[0].Get("hot"));
        }
        finally
        {
            foreach (SuperKvClient client in clients)
                client.Dispose();
        }
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