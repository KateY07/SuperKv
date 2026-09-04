using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using Garnet;
using StackExchange.Redis;

namespace SuperKv;

public sealed class SuperKvOptions
{
    public string ConnectionString { get; init; } = "127.0.0.1:6379";

    public string KeyPrefix { get; init; } = string.Empty;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class SuperKvServerOptions
{
    public string Address { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 6379;

    public string IndexSize { get; init; } = "16m";

    public string MemorySize { get; init; } = "1g";
}

public sealed class SuperKvClient : IDisposable
{
    readonly IDatabase _database;
    readonly IConnectionMultiplexer? _ownedConnection;
    readonly string _keyPrefix;
    int _disposed;

    SuperKvClient(
        IDatabase database,
        IConnectionMultiplexer? ownedConnection,
        string keyPrefix)
    {
        _database = database;
        _ownedConnection = ownedConnection;
        _keyPrefix = keyPrefix;
    }

    public static SuperKvClient Create(SuperKvOptions? options = null)
    {
        options ??= new SuperKvOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        ArgumentNullException.ThrowIfNull(options.KeyPrefix);
        int connectTimeout = ValidateTimeout(options.ConnectTimeout, nameof(options.ConnectTimeout));
        int operationTimeout = ValidateTimeout(options.OperationTimeout, nameof(options.OperationTimeout));

        ConfigurationOptions configuration = ConfigurationOptions.Parse(options.ConnectionString);
        configuration.AbortOnConnectFail = true;
        configuration.ConnectRetry = 0;
        configuration.ConnectTimeout = connectTimeout;
        configuration.SyncTimeout = operationTimeout;

        ConnectionMultiplexer connection = ConnectionMultiplexer.Connect(configuration);
        return new SuperKvClient(connection.GetDatabase(), connection, options.KeyPrefix);
    }

    public static SuperKvClient Create(
        IConnectionMultiplexer connection,
        string keyPrefix = "",
        int database = -1)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(keyPrefix);
        return new SuperKvClient(connection.GetDatabase(database), null, keyPrefix);
    }

    public void Set(string key, ReadOnlyMemory<byte> value)
    {
        GetDatabase().StringSet(QualifyKey(key), value.ToArray());
    }

    public byte[]? Get(string key)
    {
        RedisValue value = GetDatabase().StringGet(QualifyKey(key));
        return value.IsNull ? null : (byte[]?)value;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _ownedConnection?.Dispose();
    }

    IDatabase GetDatabase()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _database;
    }

    RedisKey QualifyKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return string.Concat(
            _keyPrefix.Length.ToString(CultureInfo.InvariantCulture),
            ":",
            _keyPrefix,
            key);
    }

    static int ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Timeout must be greater than zero and at most Int32.MaxValue milliseconds.");
        }

        return checked((int)Math.Ceiling(timeout.TotalMilliseconds));
    }
}

public sealed class SuperKvServer : IDisposable
{
    static readonly ConcurrentDictionary<string, byte> ActiveEndpoints = new(StringComparer.Ordinal);
    readonly GarnetServer _server;
    readonly string _endpointKey;
    int _disposed;

    SuperKvServer(GarnetServer server, string endpointKey, string connectionString)
    {
        _server = server;
        _endpointKey = endpointKey;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static SuperKvServer Create(SuperKvServerOptions? options = null)
    {
        options ??= new SuperKvServerOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Address);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.IndexSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MemorySize);

        if (!IPAddress.TryParse(options.Address, out IPAddress? address) ||
            !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException(
                "Address must be a loopback IP address.",
                nameof(options));
        }

        if (options.Port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be from 1 through 65535.");

        string endpointKey = $"{address}:{options.Port}";
        if (!ActiveEndpoints.TryAdd(endpointKey, 0))
            throw new InvalidOperationException($"A SuperKv server already owns endpoint '{endpointKey}'.");

        try
        {
            EnsureEndpointIsAvailable(address, options.Port);
            return CreateStartedServer(options, address, endpointKey);
        }
        catch
        {
            ActiveEndpoints.TryRemove(endpointKey, out _);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _server.Dispose();
        }
        finally
        {
            ActiveEndpoints.TryRemove(_endpointKey, out _);
        }
    }

    static void EnsureEndpointIsAvailable(IPAddress address, int port)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true
        };
        socket.Bind(new IPEndPoint(address, port));
    }

    static SuperKvServer CreateStartedServer(
        SuperKvServerOptions options,
        IPAddress address,
        string endpointKey)
    {
        var server = new GarnetServer(
        [
            "--bind", address.ToString(),
            "--port", options.Port.ToString(CultureInfo.InvariantCulture),
            "--no-pubsub",
            "--no-obj",
            "--index", options.IndexSize,
            "--memory", options.MemorySize
        ]);

        try
        {
            server.Start();
            string host = address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{address}]"
                : address.ToString();
            return new SuperKvServer(server, endpointKey, $"{host}:{options.Port}");
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }
}
