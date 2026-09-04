using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LightningDB;
using StackExchange.Redis;

namespace SuperKv;

/// <summary>SuperKv storage backend.</summary>
public enum SuperKvBackend
{
    /// <summary>A local or remote Garnet server accessed through RESP.</summary>
    Garnet,

    /// <summary>An embedded memory-mapped LMDB database accessed through LightningDB.</summary>
    LightningDb
}

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
public sealed class GarnetBackendOptions
{
    /// <summary>StackExchange.Redis connection string for Garnet.</summary>
    public string ConnectionString { get; init; } = "127.0.0.1:6379,abortConnect=false";

    /// <summary>Logical database number.</summary>
    public int Database { get; init; }
}

/// <summary>Storage settings for the LightningDB backend.</summary>
public sealed class LightningDbBackendOptions
{
    /// <summary>Shared LMDB environment directory. All processes must use the same path.</summary>
    public string DirectoryPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SuperKv",
        "data");

    /// <summary>Maximum memory-map size in bytes. This is address-space reservation, not immediate disk use.</summary>
    public long MapSize { get; init; } = 1024L * 1024 * 1024;
}

/// <summary>Configuration used to open a <see cref="SuperKvClient"/>.</summary>
public sealed class SuperKvOptions
{
    /// <summary>Selected backend.</summary>
    public SuperKvBackend Backend { get; init; } = SuperKvBackend.Garnet;

    /// <summary>Prefix prepended to every key, useful for application isolation.</summary>
    public string KeyPrefix { get; init; } = string.Empty;

    /// <summary>Garnet connection settings.</summary>
    public GarnetBackendOptions Garnet { get; init; } = new();

    /// <summary>LightningDB storage settings.</summary>
    public LightningDbBackendOptions LightningDb { get; init; } = new();
}

/// <summary>Backend-neutral byte-oriented key-value operations.</summary>
public interface ISuperKv : IAsyncDisposable
{
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

    /// <summary>
    /// Gets the remaining lifetime. A null result means either that the key is absent or that it has no expiry.
    /// </summary>
    ValueTask<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Long-lived, thread-safe SuperKv client. Create one instance per process and reuse it.
/// </summary>
public sealed class SuperKvClient : ISuperKv
{
    readonly ISuperKvBackend _backend;
    int _disposed;

    SuperKvClient(ISuperKvBackend backend) => _backend = backend;

    /// <summary>Opens the selected backend.</summary>
    public static async ValueTask<SuperKvClient> OpenAsync(
        SuperKvOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SuperKvOptions();
        ValidateOptions(options);

        ISuperKvBackend backend = options.Backend switch
        {
            SuperKvBackend.Garnet => await GarnetBackend.OpenAsync(options, cancellationToken).ConfigureAwait(false),
            SuperKvBackend.LightningDb => LightningDbBackend.Open(options, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Backend, "Unknown backend.")
        };

        return new SuperKvClient(backend);
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
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _backend.DisposeAsync().ConfigureAwait(false);
    }

    static void ValidateOptions(SuperKvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Garnet);
        ArgumentNullException.ThrowIfNull(options.LightningDb);

        if (options.KeyPrefix is null)
            throw new ArgumentException("KeyPrefix cannot be null.", nameof(options));

        if (options.Backend == SuperKvBackend.Garnet && string.IsNullOrWhiteSpace(options.Garnet.ConnectionString))
            throw new ArgumentException("A Garnet connection string is required.", nameof(options));

        if (options.Backend == SuperKvBackend.LightningDb)
        {
            if (string.IsNullOrWhiteSpace(options.LightningDb.DirectoryPath))
                throw new ArgumentException("A LightningDB directory is required.", nameof(options));
            if (options.LightningDb.MapSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "LightningDB MapSize must be positive.");
        }
    }

    static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
    }

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

interface ISuperKvBackend : IAsyncDisposable
{
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

    public async ValueTask<bool> SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive,
        SuperKvSetCondition condition,
        CancellationToken cancellationToken)
    {
        When when = condition switch
        {
            SuperKvSetCondition.Always => When.Always,
            SuperKvSetCondition.OnlyIfMissing => When.NotExists,
            SuperKvSetCondition.OnlyIfPresent => When.Exists,
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown set condition.")
        };

        Task<bool> operation = _database.StringSetAsync(
            FormatKey(key),
            value.ToArray(),
            timeToLive,
            when);
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

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    RedisKey FormatKey(string key) => _keyPrefix + key;
}

sealed class LightningDbBackend : ISuperKvBackend
{
    const int HeaderLength = 12;
    const uint EnvelopeMagic = 0x31564B53; // "SKV1" in little endian
    const int MaxLmdbKeyBytes = 511;

    readonly LightningEnvironment _environment;
    readonly byte[] _keyPrefix;

    LightningDbBackend(LightningEnvironment environment, string keyPrefix)
    {
        _environment = environment;
        _keyPrefix = Encoding.UTF8.GetBytes(keyPrefix);
    }

    public static LightningDbBackend Open(SuperKvOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.GetFullPath(options.LightningDb.DirectoryPath);
        Directory.CreateDirectory(path);

        var configuration = new EnvironmentConfiguration { MapSize = options.LightningDb.MapSize };
        var environment = new LightningEnvironment(path, configuration);

        try
        {
            environment.Open();
            using var transaction = environment.BeginTransaction();
            using var database = transaction.OpenDatabase(
                configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });
            transaction.Commit();
            return new LightningDbBackend(environment, options.KeyPrefix);
        }
        catch
        {
            environment.Dispose();
            throw;
        }
    }

    public ValueTask<bool> SetAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive,
        SuperKvSetCondition condition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encodedKey = EncodeKey(key);
        byte[] envelope = CreateEnvelope(value.Span, timeToLive);

        using var transaction = _environment.BeginTransaction();
        using var database = transaction.OpenDatabase();

        var (resultCode, _, existingValue) = transaction.Get(database, encodedKey);
        bool exists = resultCode == MDBResultCode.Success && !IsExpired(ReadExpiration(existingValue.AsSpan()));
        bool shouldWrite = condition switch
        {
            SuperKvSetCondition.Always => true,
            SuperKvSetCondition.OnlyIfMissing => !exists,
            SuperKvSetCondition.OnlyIfPresent => exists,
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown set condition.")
        };

        if (!shouldWrite)
            return ValueTask.FromResult(false);

        transaction.Put(database, encodedKey, envelope);
        transaction.Commit();
        return ValueTask.FromResult(true);
    }

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encodedKey = EncodeKey(key);
        byte[]? result;
        bool expired;

        using (var transaction = _environment.BeginTransaction(TransactionBeginFlags.ReadOnly))
        using (var database = transaction.OpenDatabase())
        {
            var (resultCode, _, value) = transaction.Get(database, encodedKey);
            if (resultCode != MDBResultCode.Success)
                return ValueTask.FromResult<byte[]?>(null);

            ReadOnlySpan<byte> envelope = value.AsSpan();
            expired = IsExpired(ReadExpiration(envelope));
            result = expired ? null : envelope[HeaderLength..].ToArray();
        }

        if (expired)
            DeleteIfExpired(encodedKey);

        return ValueTask.FromResult(result);
    }

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encodedKey = EncodeKey(key);

        using var transaction = _environment.BeginTransaction();
        using var database = transaction.OpenDatabase();
        var (resultCode, _, value) = transaction.Get(database, encodedKey);
        bool existed = resultCode == MDBResultCode.Success && !IsExpired(ReadExpiration(value.AsSpan()));
        if (resultCode == MDBResultCode.Success)
            transaction.Delete(database, encodedKey);
        transaction.Commit();
        return ValueTask.FromResult(existed);
    }

    public ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encodedKey = EncodeKey(key);
        bool exists;
        bool expired = false;

        using (var transaction = _environment.BeginTransaction(TransactionBeginFlags.ReadOnly))
        using (var database = transaction.OpenDatabase())
        {
            var (resultCode, _, value) = transaction.Get(database, encodedKey);
            exists = resultCode == MDBResultCode.Success;
            if (exists)
            {
                expired = IsExpired(ReadExpiration(value.AsSpan()));
                exists = !expired;
            }
        }

        if (expired)
            DeleteIfExpired(encodedKey);

        return ValueTask.FromResult(exists);
    }

    public ValueTask<long> IncrementAsync(
        string key,
        long delta,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encodedKey = EncodeKey(key);

        using var transaction = _environment.BeginTransaction();
        using var database = transaction.OpenDatabase();
        var (resultCode, _, value) = transaction.Get(database, encodedKey);

        long current = 0;
        long expiration = 0;
        if (resultCode == MDBResultCode.Success)
        {
            ReadOnlySpan<byte> envelope = value.AsSpan();
            expiration = ReadExpiration(envelope);
            if (!IsExpired(expiration))
            {
                string text = Encoding.UTF8.GetString(envelope[HeaderLength..]);
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out current))
                    throw new InvalidOperationException($"Value at key '{key}' is not a 64-bit integer.");
            }
            else
            {
                expiration = 0;
            }
        }

        long updated = checked(current + delta);
        byte[] payload = Encoding.UTF8.GetBytes(updated.ToString(CultureInfo.InvariantCulture));
        byte[] updatedEnvelope = CreateEnvelope(payload, expiration);
        transaction.Put(database, encodedKey, updatedEnvelope);
        transaction.Commit();
        return ValueTask.FromResult(updated);
    }

    public ValueTask<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encodedKey = EncodeKey(key);
        long expiration;

        using (var transaction = _environment.BeginTransaction(TransactionBeginFlags.ReadOnly))
        using (var database = transaction.OpenDatabase())
        {
            var (resultCode, _, value) = transaction.Get(database, encodedKey);
            if (resultCode != MDBResultCode.Success)
                return ValueTask.FromResult<TimeSpan?>(null);

            expiration = ReadExpiration(value.AsSpan());
        }

        if (expiration == 0)
            return ValueTask.FromResult<TimeSpan?>(null);

        long remainingTicks = expiration - DateTime.UtcNow.Ticks;
        if (remainingTicks <= 0)
        {
            DeleteIfExpired(encodedKey);
            return ValueTask.FromResult<TimeSpan?>(null);
        }

        return ValueTask.FromResult<TimeSpan?>(TimeSpan.FromTicks(remainingTicks));
    }

    public ValueTask DisposeAsync()
    {
        _environment.Dispose();
        return ValueTask.CompletedTask;
    }

    byte[] EncodeKey(string key)
    {
        int keyByteCount = Encoding.UTF8.GetByteCount(key);
        int length = checked(_keyPrefix.Length + keyByteCount);
        if (length > MaxLmdbKeyBytes)
            throw new ArgumentException($"The UTF-8 key and prefix cannot exceed {MaxLmdbKeyBytes} bytes.", nameof(key));

        byte[] encoded = GC.AllocateUninitializedArray<byte>(length);
        _keyPrefix.CopyTo(encoded, 0);
        Encoding.UTF8.GetBytes(key, encoded.AsSpan(_keyPrefix.Length));
        return encoded;
    }

    void DeleteIfExpired(byte[] encodedKey)
    {
        using var transaction = _environment.BeginTransaction();
        using var database = transaction.OpenDatabase();
        var (resultCode, _, value) = transaction.Get(database, encodedKey);
        if (resultCode == MDBResultCode.Success && IsExpired(ReadExpiration(value.AsSpan())))
        {
            transaction.Delete(database, encodedKey);
            transaction.Commit();
        }
    }

    static byte[] CreateEnvelope(ReadOnlySpan<byte> payload, TimeSpan? timeToLive)
    {
        long expiration = timeToLive is { } ttl
            ? DateTime.UtcNow.Add(ttl).Ticks
            : 0;
        return CreateEnvelope(payload, expiration);
    }

    static byte[] CreateEnvelope(ReadOnlySpan<byte> payload, long expiration)
    {
        byte[] envelope = GC.AllocateUninitializedArray<byte>(checked(HeaderLength + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(envelope, EnvelopeMagic);
        BinaryPrimitives.WriteInt64LittleEndian(envelope.AsSpan(sizeof(uint)), expiration);
        payload.CopyTo(envelope.AsSpan(HeaderLength));
        return envelope;
    }

    static long ReadExpiration(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(envelope) != EnvelopeMagic)
            throw new InvalidDataException("The LightningDB value is not in the SuperKv v1 format.");

        return BinaryPrimitives.ReadInt64LittleEndian(envelope[sizeof(uint)..]);
    }

    static bool IsExpired(long expiration) => expiration != 0 && expiration <= DateTime.UtcNow.Ticks;
}
