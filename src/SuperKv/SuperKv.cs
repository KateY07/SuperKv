using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace SuperKv;

/// <summary>Condition applied when setting a key.</summary>
public enum SuperKvSetCondition
{
    /// <summary>Create or replace the key.</summary>
    Always,

    /// <summary>Write only when the key does not currently exist.</summary>
    OnlyIfMissing,

    /// <summary>Write only when the key currently exists.</summary>
    OnlyIfPresent
}

/// <summary>Connection settings for the Garnet backend.</summary>
public sealed class GarnetOptions
{
    /// <summary>StackExchange.Redis connection string for Garnet.</summary>
    public string ConnectionString { get; init; } = "127.0.0.1:6379,abortConnect=false";

    /// <summary>Logical database number.</summary>
    public int Database { get; init; }
}

/// <summary>Configuration used to open a <see cref="SuperKvClient"/>.</summary>
public sealed class SuperKvOptions
{
    /// <summary>Prefix prepended to every key, useful for application isolation.</summary>
    public string KeyPrefix { get; init; } = string.Empty;

    /// <summary>Garnet connection settings.</summary>
    public GarnetOptions Garnet { get; init; } = new();
}

/// <summary>Backend-neutral byte-oriented key-value operations.</summary>
public interface ISuperKv : IDisposable, IAsyncDisposable
{
    /// <summary>Synchronously creates or updates a value. Throws on a thread with a synchronization context.</summary>
    bool SetValue(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive = null,
        SuperKvSetCondition condition = SuperKvSetCondition.Always);

    /// <summary>Synchronously gets a copy of a value, or <see langword="null"/> when absent.</summary>
    byte[]? GetValue(string key);

    /// <summary>Synchronously deletes a key and returns whether it existed.</summary>
    bool Delete(string key);

    /// <summary>Synchronously returns whether a non-expired key exists.</summary>
    bool Exists(string key);

    /// <summary>Synchronously atomically adds <paramref name="delta"/> to an integer value.</summary>
    long Increment(string key, long delta = 1);

    /// <summary>Synchronously gets the remaining lifetime.</summary>
    TimeSpan? GetTimeToLive(string key);

    /// <summary>Creates or updates a value.</summary>
    ValueTask<bool> SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive = null,
        SuperKvSetCondition condition = SuperKvSetCondition.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a copy of a value, or <see langword="null"/> when it is absent or expired.</summary>
    ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes a key and returns whether it existed.</summary>
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Returns whether a non-expired key exists.</summary>
    ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Atomically adds <paramref name="delta"/> to an integer value.</summary>
    ValueTask<long> IncrementAsync(string key, long delta = 1, CancellationToken cancellationToken = default);

    /// <summary>Gets the remaining lifetime. Null means absent or no expiry.</summary>
    ValueTask<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Long-lived, thread-safe SuperKv client. Create one instance per process and reuse it.</summary>
public sealed class SuperKvClient : ISuperKv
{
    readonly ISuperKvBackend _backend;
    int _disposed;

    SuperKvClient(ISuperKvBackend backend) => _backend = backend;

    /// <summary>Opens Garnet synchronously.</summary>
    public static SuperKvClient Open(SuperKvOptions? options = null)
    {
        options ??= new SuperKvOptions();
        ValidateOptions(options);
        return new SuperKvClient(GarnetBackend.Open(options));
    }

    /// <summary>Opens Garnet asynchronously.</summary>
    public static async ValueTask<SuperKvClient> OpenAsync(
        SuperKvOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SuperKvOptions();
        ValidateOptions(options);

        ISuperKvBackend backend = await GarnetBackend.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        return new SuperKvClient(backend);
    }

    /// <inheritdoc />
    public bool SetValue(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive = null,
        SuperKvSetCondition condition = SuperKvSetCondition.Always)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        ValidateTimeToLive(timeToLive);
        return _backend.SetValue(key, value, timeToLive, condition);
    }

    /// <inheritdoc />
    public byte[]? GetValue(string key)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        return _backend.GetValue(key);
    }

    /// <inheritdoc />
    public bool Delete(string key)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        return _backend.Delete(key);
    }

    /// <inheritdoc />
    public bool Exists(string key)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        return _backend.Exists(key);
    }

    /// <inheritdoc />
    public long Increment(string key, long delta = 1)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        return _backend.Increment(key, delta);
    }

    /// <inheritdoc />
    public TimeSpan? GetTimeToLive(string key)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        return _backend.GetTimeToLive(key);
    }

    /// <inheritdoc />
    public ValueTask<bool> SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive = null,
        SuperKvSetCondition condition = SuperKvSetCondition.Always,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        ValidateTimeToLive(timeToLive);
        return _backend.SetAsync(key, value, timeToLive, condition, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        return _backend.GetAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        return _backend.DeleteAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        return _backend.ExistsAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<long> IncrementAsync(
        string key,
        long delta = 1,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        return _backend.IncrementAsync(key, delta, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<TimeSpan?> GetTimeToLiveAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        return _backend.GetTimeToLiveAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _backend.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _backend.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    static void ValidateOptions(SuperKvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Garnet);

        if (options.KeyPrefix is null)
            throw new ArgumentException("KeyPrefix cannot be null.", nameof(options));

        if (string.IsNullOrWhiteSpace(options.Garnet.ConnectionString))
            throw new ArgumentException("A Garnet connection string is required.", nameof(options));
    }

    static void ValidateKey(string key) => ArgumentException.ThrowIfNullOrEmpty(key);

    static void ValidateTimeToLive(TimeSpan? timeToLive)
    {
        if (timeToLive is { } ttl && ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeToLive), "TTL must be positive.");
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}

/// <summary>Convenience operations for strings and JSON values.</summary>
public static class SuperKvExtensions
{
    /// <summary>Synchronously stores a UTF-8 string.</summary>
    public static bool SetString(
        this ISuperKv kv,
        string key,
        string value,
        TimeSpan? timeToLive = null,
        SuperKvSetCondition condition = SuperKvSetCondition.Always)
    {
        ArgumentNullException.ThrowIfNull(kv);
        ArgumentNullException.ThrowIfNull(value);
        return kv.SetValue(key, Encoding.UTF8.GetBytes(value), timeToLive, condition);
    }

    /// <summary>Synchronously gets a UTF-8 string.</summary>
    public static string? GetString(this ISuperKv kv, string key)
    {
        ArgumentNullException.ThrowIfNull(kv);
        byte[]? value = kv.GetValue(key);
        return value is null ? null : Encoding.UTF8.GetString(value);
    }

    /// <summary>Synchronously serializes and stores a JSON value.</summary>
    public static bool SetJson<T>(
        this ISuperKv kv,
        string key,
        T value,
        TimeSpan? timeToLive = null,
        SuperKvSetCondition condition = SuperKvSetCondition.Always,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(kv);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, serializerOptions);
        return kv.SetValue(key, json, timeToLive, condition);
    }

    /// <summary>Synchronously gets and deserializes a JSON value.</summary>
    public static T? GetJson<T>(
        this ISuperKv kv,
        string key,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(kv);
        byte[]? json = kv.GetValue(key);
        return json is null ? default : JsonSerializer.Deserialize<T>(json, serializerOptions);
    }

    /// <summary>Stores a UTF-8 string.</summary>
    public static ValueTask<bool> SetStringAsync(
        this ISuperKv kv,
        string key,
        string value,
        TimeSpan? timeToLive = null,
        SuperKvSetCondition condition = SuperKvSetCondition.Always,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kv);
        ArgumentNullException.ThrowIfNull(value);
        return kv.SetAsync(key, Encoding.UTF8.GetBytes(value), timeToLive, condition, cancellationToken);
    }

    /// <summary>Gets a UTF-8 string.</summary>
    public static async ValueTask<string?> GetStringAsync(
        this ISuperKv kv,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kv);
        byte[]? value = await kv.GetAsync(key, cancellationToken).ConfigureAwait(false);
        return value is null ? null : Encoding.UTF8.GetString(value);
    }

    /// <summary>Serializes and stores a JSON value.</summary>
    public static ValueTask<bool> SetJsonAsync<T>(
        this ISuperKv kv,
        string key,
        T value,
        TimeSpan? timeToLive = null,
        SuperKvSetCondition condition = SuperKvSetCondition.Always,
        JsonSerializerOptions? serializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kv);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, serializerOptions);
        return kv.SetAsync(key, json, timeToLive, condition, cancellationToken);
    }

    /// <summary>Gets and deserializes a JSON value.</summary>
    public static async ValueTask<T?> GetJsonAsync<T>(
        this ISuperKv kv,
        string key,
        JsonSerializerOptions? serializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kv);
        byte[]? json = await kv.GetAsync(key, cancellationToken).ConfigureAwait(false);
        return json is null ? default : JsonSerializer.Deserialize<T>(json, serializerOptions);
    }
}

interface ISuperKvBackend : IDisposable, IAsyncDisposable
{
    bool SetValue(string key, ReadOnlyMemory<byte> value, TimeSpan? timeToLive, SuperKvSetCondition condition);
    byte[]? GetValue(string key);
    bool Delete(string key);
    bool Exists(string key);
    long Increment(string key, long delta);
    TimeSpan? GetTimeToLive(string key);

    ValueTask<bool> SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive,
        SuperKvSetCondition condition,
        CancellationToken cancellationToken);

    ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken);
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken);
    ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken);
    ValueTask<long> IncrementAsync(string key, long delta, CancellationToken cancellationToken);
    ValueTask<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken cancellationToken);
}

sealed class GarnetBackend : ISuperKvBackend
{
    readonly ConnectionMultiplexer _connection;
    readonly IDatabase _database;
    readonly string _keyPrefix;

    GarnetBackend(ConnectionMultiplexer connection, int database, string keyPrefix)
    {
        _connection = connection;
        _database = connection.GetDatabase(database);
        _keyPrefix = keyPrefix;
    }

    public static GarnetBackend Open(SuperKvOptions options) =>
        new(ConnectionMultiplexer.Connect(options.Garnet.ConnectionString), options.Garnet.Database, options.KeyPrefix);

    public static async ValueTask<GarnetBackend> OpenAsync(
        SuperKvOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionMultiplexer connection = await ConnectionMultiplexer
            .ConnectAsync(options.Garnet.ConnectionString)
            .ConfigureAwait(false);
        return new GarnetBackend(connection, options.Garnet.Database, options.KeyPrefix);
    }

    public bool SetValue(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive,
        SuperKvSetCondition condition) =>
        _database.StringSet(FormatKey(key), value.ToArray(), timeToLive, MapCondition(condition));

    public byte[]? GetValue(string key)
    {
        RedisValue value = _database.StringGet(FormatKey(key));
        return value.IsNull ? null : (byte[]?)value;
    }

    public bool Delete(string key) => _database.KeyDelete(FormatKey(key));

    public bool Exists(string key) => _database.KeyExists(FormatKey(key));

    public long Increment(string key, long delta) => _database.StringIncrement(FormatKey(key), delta);

    public TimeSpan? GetTimeToLive(string key) => _database.KeyTimeToLive(FormatKey(key));

    public async ValueTask<bool> SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive,
        SuperKvSetCondition condition,
        CancellationToken cancellationToken)
    {
        Task<bool> operation = _database.StringSetAsync(
            FormatKey(key),
            value.ToArray(),
            timeToLive,
            MapCondition(condition));
        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        RedisValue value = await _database.StringGetAsync(FormatKey(key))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return value.IsNull ? null : (byte[]?)value;
    }

    public async ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
        await _database.KeyDeleteAsync(FormatKey(key)).WaitAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
        await _database.KeyExistsAsync(FormatKey(key)).WaitAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<long> IncrementAsync(
        string key,
        long delta,
        CancellationToken cancellationToken) =>
        await _database.StringIncrementAsync(FormatKey(key), delta)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken cancellationToken) =>
        await _database.KeyTimeToLiveAsync(FormatKey(key))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Dispose() => _connection.Dispose();

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    static When MapCondition(SuperKvSetCondition condition) => condition switch
    {
        SuperKvSetCondition.Always => When.Always,
        SuperKvSetCondition.OnlyIfMissing => When.NotExists,
        SuperKvSetCondition.OnlyIfPresent => When.Exists,
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown set condition.")
    };

    RedisKey FormatKey(string key) => _keyPrefix + key;
}