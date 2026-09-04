using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace SuperKv;

public enum SuperKvBackend
{
    Memory,
    Garnet
}

public sealed class SuperKvOptions
{
    public SuperKvBackend Backend { get; init; } = SuperKvBackend.Memory;

    public string PipeName { get; init; } = "SuperKv.Default";

    public string GarnetConnectionString { get; init; } = "127.0.0.1:6379";

    public string KeyPrefix { get; init; } = string.Empty;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class SuperKvServerOptions
{
    public string PipeName { get; init; } = "SuperKv.Default";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class SuperKvClient : IDisposable
{
    readonly SuperKvBackend _backend;
    readonly string _pipeName;
    readonly string _garnetConnectionString;
    readonly string _keyPrefix;
    readonly int _connectTimeoutMilliseconds;
    readonly SemaphoreSlim _pipeGate = new(1, 1);
    NamedPipeClientStream? _pipe;
    ConnectionMultiplexer? _garnetConnection;
    IDatabase? _garnetDatabase;
    int _disposed;

    SuperKvClient(SuperKvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.KeyPrefix);

        if (!Enum.IsDefined(options.Backend))
            throw new ArgumentOutOfRangeException(nameof(options), "Unsupported SuperKv backend.");

        if (options.Backend == SuperKvBackend.Memory)
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PipeName);
        else
            ArgumentException.ThrowIfNullOrWhiteSpace(options.GarnetConnectionString);

        if (options.ConnectTimeout <= TimeSpan.Zero ||
            options.ConnectTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "ConnectTimeout must be greater than zero and at most Int32.MaxValue milliseconds.");
        }

        _backend = options.Backend;
        _pipeName = options.PipeName;
        _garnetConnectionString = options.GarnetConnectionString;
        _keyPrefix = options.KeyPrefix;
        _connectTimeoutMilliseconds = checked((int)Math.Ceiling(options.ConnectTimeout.TotalMilliseconds));
    }

    public static SuperKvClient Connect(SuperKvOptions? options = null)
    {
        var client = new SuperKvClient(options ?? new SuperKvOptions());

        if (client._backend == SuperKvBackend.Garnet)
        {
            try
            {
                client.ConnectGarnet();
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        var pipe = client.CreatePipe();

        try
        {
            pipe.Connect(client._connectTimeoutMilliseconds);
            client._pipe = pipe;
            return client;
        }
        catch (TimeoutException exception)
        {
            pipe.Dispose();
            throw new TimeoutException(
                "Could not connect to SuperKv pipe '" + client._pipeName + "'. Start the server first.",
                exception);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public void Set(string key, ReadOnlyMemory<byte> value)
    {
        string qualifiedKey = QualifyKey(key);

        if (_backend == SuperKvBackend.Garnet)
        {
            bool stored = GetGarnetDatabase().StringSet(qualifiedKey, value.ToArray());
            if (!stored)
                throw new IOException("Garnet did not acknowledge the SET command.");
            return;
        }

        byte[] request = SuperKvProtocol.CreateSetRequest(qualifiedKey, value.Span);
        Execute(request, static response =>
        {
            SuperKvProtocol.ReadSetResponse(response);
            return true;
        });
    }

    public byte[]? Get(string key)
    {
        string qualifiedKey = QualifyKey(key);

        if (_backend == SuperKvBackend.Garnet)
        {
            RedisValue value = GetGarnetDatabase().StringGet(qualifiedKey);
            return value.IsNull ? null : (byte[]?)value;
        }

        byte[] request = SuperKvProtocol.CreateGetRequest(qualifiedKey);
        return Execute(request, SuperKvProtocol.ReadGetResponse);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        NamedPipeClientStream? pipe = Interlocked.Exchange(ref _pipe, null);
        pipe?.Dispose();
        ConnectionMultiplexer? garnetConnection = Interlocked.Exchange(ref _garnetConnection, null);
        _garnetDatabase = null;
        garnetConnection?.Dispose();
        _pipeGate.Dispose();
    }

    void ConnectGarnet()
    {
        ConfigurationOptions configuration = ConfigurationOptions.Parse(_garnetConnectionString);
        configuration.AbortOnConnectFail = true;
        configuration.ConnectRetry = 0;
        configuration.ConnectTimeout = _connectTimeoutMilliseconds;

        ConnectionMultiplexer connection = ConnectionMultiplexer.Connect(configuration);
        _garnetConnection = connection;
        _garnetDatabase = connection.GetDatabase();
    }

    IDatabase GetGarnetDatabase()
    {
        ThrowIfDisposed();
        return _garnetDatabase ?? throw new IOException("The Garnet connection is closed.");
    }


    NamedPipeClientStream CreatePipe() => new(
        ".",
        _pipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);

    T Execute<T>(byte[] request, Func<byte[], T> readResponse)
    {
        ThrowIfDisposed();
        _pipeGate.Wait();

        try
        {
            NamedPipeClientStream pipe = _pipe ?? throw new IOException("The SuperKv connection is closed.");
            SuperKvProtocol.WriteFrame(pipe, request);
            byte[] response = SuperKvProtocol.ReadFrame(pipe);
            return readResponse(response);
        }
        catch
        {
            BreakConnection();
            throw;
        }
        finally
        {
            _pipeGate.Release();
        }
    }


    string QualifyKey(string key)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(key);
        return string.Concat(_keyPrefix, key);
    }

    void BreakConnection()
    {
        NamedPipeClientStream? pipe = Interlocked.Exchange(ref _pipe, null);
        pipe?.Dispose();
    }

    void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

public sealed class SuperKvMemoryServer
{
    readonly string _pipeName;
    readonly TimeSpan _requestTimeout;
    readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    int _running;

    public SuperKvMemoryServer(SuperKvServerOptions? options = null)
    {
        options ??= new SuperKvServerOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PipeName);
        if (options.RequestTimeout <= TimeSpan.Zero ||
            options.RequestTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "RequestTimeout must be greater than zero and at most Int32.MaxValue milliseconds.");
        }

        _pipeName = options.PipeName;
        _requestTimeout = options.RequestTimeout;
    }

    public void Run(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("This SuperKv server instance is already running.");

        using var ownership = new Semaphore(1, 1, CreateOwnershipName(_pipeName));
        bool ownsServer = false;
        NamedPipeServerStream? listener = null;

        try
        {
            ownsServer = ownership.WaitOne(0);

            if (!ownsServer)
                throw new InvalidOperationException($"A SuperKv server already owns pipe '{_pipeName}'.");

            listener = CreateListener();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using CancellationTokenRegistration stopWaiting = cancellationToken.Register(
                        static state => ((NamedPipeServerStream)state!).Dispose(),
                        listener);
                    listener.WaitForConnection();
                }
                catch (Exception exception) when (
                    cancellationToken.IsCancellationRequested &&
                    exception is ObjectDisposedException or IOException)
                {
                    break;
                }

                NamedPipeServerStream connected = listener;
                listener = CreateListener();
                _ = HandleClientAsync(connected, cancellationToken);
            }
        }
        finally
        {
            listener?.Dispose();
            if (ownsServer)
                ownership.Release();
            Volatile.Write(ref _running, 0);
        }
    }

    NamedPipeServerStream CreateListener() => new(
        _pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);

    async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                while (pipe.IsConnected)
                {
                    requestTimeout.CancelAfter(_requestTimeout);
                    byte[] request = await SuperKvProtocol.ReadFrameAsync(pipe, requestTimeout.Token)
                        .ConfigureAwait(false);
                    requestTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    byte[] response;

                    try
                    {
                        response = Process(request);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        response = SuperKvProtocol.CreateErrorResponse(exception.Message);
                    }

                    await SuperKvProtocol.WriteFrameAsync(pipe, response, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (
                exception is EndOfStreamException or IOException or InvalidDataException or OperationCanceledException)
            {
            }
        }
    }

    byte[] Process(byte[] frame)
    {
        SuperKvRequest request = SuperKvProtocol.ReadRequest(frame);

        switch (request.Operation)
        {
            case SuperKvOperation.Set:
                _values[request.Key] = request.Value!;
                return SuperKvProtocol.CreateSetResponse();

            case SuperKvOperation.Get:
                _values.TryGetValue(request.Key, out byte[]? value);
                return SuperKvProtocol.CreateGetResponse(value);

            default:
                throw new InvalidDataException("Unsupported SuperKv operation.");
        }
    }

    static string CreateOwnershipName(string pipeName)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(pipeName));
        return $"Local\\SuperKv.Server.{Convert.ToHexString(hash)}";
    }
}

enum SuperKvOperation : byte
{
    Get = 1,
    Set = 2
}

readonly record struct SuperKvRequest(SuperKvOperation Operation, string Key, byte[]? Value);

static class SuperKvProtocol
{
    const byte Version = 1;
    const byte Success = 0;
    const byte Error = 1;
    const int MaxFrameLength = 128 * 1024 * 1024;

    public static byte[] CreateGetRequest(string key) => CreatePayload(writer =>
    {
        writer.Write(Version);
        writer.Write((byte)SuperKvOperation.Get);
        writer.Write(key);
    });

    public static byte[] CreateSetRequest(string key, ReadOnlySpan<byte> value)
    {
        byte[] copiedValue = value.ToArray();
        return CreatePayload(writer =>
        {
            writer.Write(Version);
            writer.Write((byte)SuperKvOperation.Set);
            writer.Write(key);
            writer.Write(copiedValue.Length);
            writer.Write(copiedValue);
        });
    }

    public static SuperKvRequest ReadRequest(byte[] frame)
    {
        using var stream = new MemoryStream(frame, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        ReadAndValidateVersion(reader);

        var operation = (SuperKvOperation)reader.ReadByte();
        string key = reader.ReadString();
        ArgumentException.ThrowIfNullOrEmpty(key);
        byte[]? value = operation == SuperKvOperation.Set ? ReadBytes(reader) : null;
        EnsureFullyRead(stream);
        return new SuperKvRequest(operation, key, value);
    }

    public static byte[] CreateSetResponse() => CreatePayload(writer =>
    {
        writer.Write(Version);
        writer.Write(Success);
    });

    public static byte[] CreateGetResponse(byte[]? value) => CreatePayload(writer =>
    {
        writer.Write(Version);
        writer.Write(Success);
        writer.Write(value is not null);

        if (value is not null)
        {
            writer.Write(value.Length);
            writer.Write(value);
        }
    });

    public static byte[] CreateErrorResponse(string message) => CreatePayload(writer =>
    {
        writer.Write(Version);
        writer.Write(Error);
        writer.Write(message);
    });

    public static void ReadSetResponse(byte[] frame)
    {
        using var stream = OpenSuccessResponse(frame, out BinaryReader reader);
        reader.Dispose();
        EnsureFullyRead(stream);
    }

    public static byte[]? ReadGetResponse(byte[] frame)
    {
        using MemoryStream stream = OpenSuccessResponse(frame, out BinaryReader reader);
        using (reader)
        {
            bool found = reader.ReadBoolean();
            byte[]? value = found ? ReadBytes(reader) : null;
            EnsureFullyRead(stream);
            return value;
        }
    }

    public static void WriteFrame(Stream stream, byte[] frame)
    {
        ValidateFrameLength(frame.Length);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, frame.Length);
        stream.Write(length);
        stream.Write(frame);
        stream.Flush();
    }

    public static byte[] ReadFrame(Stream stream)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        stream.ReadExactly(length);
        int frameLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        ValidateFrameLength(frameLength);
        byte[] frame = GC.AllocateUninitializedArray<byte>(frameLength);
        stream.ReadExactly(frame);
        return frame;
    }

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        ValidateFrameLength(frame.Length);
        byte[] length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, frame.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] length = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        int frameLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        ValidateFrameLength(frameLength);
        byte[] frame = GC.AllocateUninitializedArray<byte>(frameLength);
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        return frame;
    }

    static byte[] CreatePayload(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            write(writer);
        return stream.ToArray();
    }

    static MemoryStream OpenSuccessResponse(byte[] frame, out BinaryReader reader)
    {
        var stream = new MemoryStream(frame, writable: false);
        reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        try
        {
            ReadAndValidateVersion(reader);
            byte status = reader.ReadByte();

            if (status == Error)
                throw new InvalidOperationException(reader.ReadString());
            if (status != Success)
                throw new InvalidDataException("Invalid SuperKv response status.");

            return stream;
        }
        catch
        {
            reader.Dispose();
            stream.Dispose();
            throw;
        }
    }

    static byte[] ReadBytes(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        ValidateFrameLength(length);
        byte[] value = reader.ReadBytes(length);

        if (value.Length != length)
            throw new EndOfStreamException("Unexpected end of SuperKv payload.");

        return value;
    }

    static void ReadAndValidateVersion(BinaryReader reader)
    {
        if (reader.ReadByte() != Version)
            throw new InvalidDataException("Unsupported SuperKv protocol version.");
    }

    static void EnsureFullyRead(MemoryStream stream)
    {
        if (stream.Position != stream.Length)
            throw new InvalidDataException("The SuperKv frame contains trailing data.");
    }

    static void ValidateFrameLength(int length)
    {
        if (length < 0 || length > MaxFrameLength)
            throw new InvalidDataException($"SuperKv frame length must be between 0 and {MaxFrameLength} bytes.");
    }
}
