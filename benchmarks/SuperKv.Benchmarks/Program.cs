using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using SuperKv;
using System.Net;
using System.Net.Sockets;

Job job = Environment.GetEnvironmentVariable("SUPERKV_BENCHMARK_JOB")?.ToUpperInvariant() switch
{
    "MEDIUM" => Job.MediumRun,
    "LONG" => Job.LongRun,
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
    SuperKvServer? _server;
    SuperKvClient? _client;
    byte[] _value = null!;

    [Params(16, 128, 1024, 65536)]
    public int ValueSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        int port = GetAvailablePort();
        _server = SuperKvServer.Create(new SuperKvServerOptions
        {
            Port = port,
            IndexSize = "16m",
            MemorySize = "128m"
        });
        _client = SuperKvClient.Create(new SuperKvOptions
        {
            ConnectionString = _server.ConnectionString
        });
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
    public void Cleanup()
    {
        _client?.Dispose();
        _server?.Dispose();
    }

    static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
