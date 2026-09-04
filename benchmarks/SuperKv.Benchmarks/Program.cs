using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using Garnet;
using StackExchange.Redis;
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
    GarnetServer? _server;
    ConnectionMultiplexer? _rawConnection;
    IDatabase? _rawDatabase;
    ISuperKv? _superKv;
    byte[] _value = null!;

    [Params(16, 128, 1024, 65536)]
    public int ValueSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        int port = GetFreeTcpPort();
        string connectionString = $"127.0.0.1:{port},abortConnect=true,connectTimeout=5000";
        _server = new GarnetServer(["--bind", "127.0.0.1", "--port", port.ToString()]);
        _server.Start();

        _rawConnection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        _rawDatabase = _rawConnection.GetDatabase();
        _superKv = await SuperKvClient.OpenAsync(new SuperKvOptions
        {
            KeyPrefix = "super:",
            Garnet = new GarnetOptions { ConnectionString = connectionString }
        });

        _value = new byte[ValueSize];
        Random.Shared.NextBytes(_value);
        await _rawDatabase.StringSetAsync("raw:read", _value);
        await _rawDatabase.StringSetAsync("raw:counter", 0);
        await _superKv.SetAsync("read", _value);
        await _superKv.SetStringAsync("counter", "0");
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("GET")]
    public Task<RedisValue> RawGet() => _rawDatabase!.StringGetAsync("raw:read");

    [Benchmark]
    [BenchmarkCategory("GET")]
    public ValueTask<byte[]?> SuperKvGet() => _superKv!.GetAsync("read");

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("SET")]
    public Task<bool> RawSet() => _rawDatabase!.StringSetAsync("raw:write", _value);

    [Benchmark]
    [BenchmarkCategory("SET")]
    public ValueTask<bool> SuperKvSet() => _superKv!.SetAsync("write", _value);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("INCR")]
    public Task<long> RawIncrement() => _rawDatabase!.StringIncrementAsync("raw:counter");

    [Benchmark]
    [BenchmarkCategory("INCR")]
    public ValueTask<long> SuperKvIncrement() => _superKv!.IncrementAsync("counter");

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_superKv is not null)
            await _superKv.DisposeAsync();
        if (_rawConnection is not null)
            await _rawConnection.DisposeAsync();
        _server?.Dispose();
    }

    static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}