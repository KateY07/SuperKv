using Xunit;

namespace SuperKv.Tests;

[Collection(MemoryServerCollection.Name)]
public sealed class SuperKvSynchronousTests
{
    readonly MemoryServerFixture _server;

    public SuperKvSynchronousTests(MemoryServerFixture server) => _server = server;

    [Fact]
    public async Task ApiDoesNotDependOnSynchronizationContext()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                using SuperKvClient kv = SuperKvClient.Connect(new SuperKvOptions
                {
                    PipeName = _server.PipeName,
                    KeyPrefix = $"sync-context:{Guid.NewGuid():N}:",
                    ConnectTimeout = TimeSpan.FromSeconds(5)
                });
                kv.Set("key", new byte[] { 1, 2, 3 });
                Assert.Equal(new byte[] { 1, 2, 3 }, kv.Get("key"));
                completion.SetResult(true);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "SuperKv non-pumping synchronization-context test"
        };

        thread.Start();
        Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)));
    }

    sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
        }

        public override void Send(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("SuperKv attempted to use the synchronization context.");
    }
}