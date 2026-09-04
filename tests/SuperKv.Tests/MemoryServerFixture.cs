using Xunit;

namespace SuperKv.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryServerCollection : ICollectionFixture<MemoryServerFixture>
{
    public const string Name = "MemoryServer";
}

public sealed class MemoryServerFixture : IAsyncLifetime
{
    readonly CancellationTokenSource _shutdown = new();
    readonly SuperKvMemoryServer _server;
    Task? _serverTask;

    public MemoryServerFixture()
    {
        PipeName = $"SuperKv.Tests.{Guid.NewGuid():N}";
        _server = new SuperKvMemoryServer(new SuperKvServerOptions { PipeName = PipeName });
    }

    public string PipeName { get; }

    public Task InitializeAsync()
    {
        _serverTask = Task.Factory.StartNew(
            () => _server.Run(_shutdown.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public SuperKvClient Connect(string? prefix = null) =>
        SuperKvClient.Connect(new SuperKvOptions
        {
            PipeName = PipeName,
            KeyPrefix = prefix ?? $"test:{Guid.NewGuid():N}:"
        });

    public async Task DisposeAsync()
    {
        await _shutdown.CancelAsync();
        if (_serverTask is not null)
            await _serverTask;
        _shutdown.Dispose();
    }
}