using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using SuperKv;

Job job = Environment.GetEnvironmentVariable("SUPERKV_BENCHMARK_JOB")?.ToLowerInvariant() switch
{
    "medium" => Job.MediumRun,
    "long" => Job.LongRun,
    _ => Job.ShortRun
};
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(job)
    .AddColumn(StatisticColumn.P50, StatisticColumn.P95);
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SuperKvBenchmarks
{
    CancellationTokenSource? _shutdown;
    Task? _serverTask;
    SuperKvClient? _client;
    byte[] _value = null!;

    [Params(16, 128, 1024, 65536)]
    public int ValueSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        string pipeName = $"SuperKv.Benchmark.{Guid.NewGuid():N}";
        _shutdown = new CancellationTokenSource();
        var server = new SuperKvMemoryServer(new SuperKvServerOptions { PipeName = pipeName });
        _serverTask = Task.Factory.StartNew(
            () => server.Run(_shutdown.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _client = SuperKvClient.Connect(new SuperKvOptions { PipeName = pipeName });
        _value = new byte[ValueSize];
        Random.Shared.NextBytes(_value);
        _client.Set("read", _value);
    }

    [Benchmark]
    [BenchmarkCategory("GET")]
    public byte[]? Get() => _client!.Get("read");

    [Benchmark]
    [BenchmarkCategory("SET")]
    public void Set() => _client!.Set("write", _value);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _client?.Dispose();
        if (_shutdown is not null)
            await _shutdown.CancelAsync();
        if (_serverTask is not null)
            await _serverTask;
        _shutdown?.Dispose();
    }
}