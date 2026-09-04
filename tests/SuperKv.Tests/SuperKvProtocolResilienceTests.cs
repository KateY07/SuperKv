using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Xunit;

namespace SuperKv.Tests;

[Collection(MemoryServerCollection.Name)]
public sealed class SuperKvProtocolResilienceTests
{
    readonly MemoryServerFixture _server;

    public SuperKvProtocolResilienceTests(MemoryServerFixture server) => _server = server;

    public static TheoryData<byte[]> MalformedRequests => new()
    {
        { new byte[] { 1, 255, 1, (byte)'k' } },
        { new byte[] { 2, 1, 1, (byte)'k' } },
        { new byte[] { 1, 1, 1, (byte)'k', 42 } },
        { new byte[] { 1, 2, 1, (byte)'k', 2, 0, 0, 0, 7 } }
    };

    public static TheoryData<byte[], bool> InvalidResponses => new()
    {
        { new byte[] { 2, 0 }, false },
        { new byte[] { 1, 9 }, false },
        { new byte[] { 1, 0, 0, 42 }, false },
        { new byte[] { 1, 0, 1, 2, 0, 0, 0, 7 }, false },
        { CreateErrorResponse("rejected"), false },
        { new byte[] { 1, 0, 42 }, true }
    };

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public async Task CompleteMalformedRequestsReturnErrorWithoutStoppingServer(byte[] request)
    {
        using NamedPipeClientStream raw = ConnectRaw(_server.PipeName);
        await WriteFrameAsync(raw, request);
        byte[] response = await ReadFrameAsync(raw);

        Assert.True(response.Length >= 2);
        Assert.Equal(1, response[0]);
        Assert.Equal(1, response[1]);

        using SuperKvClient healthy = _server.Connect();
        healthy.Set("healthy", new byte[] { 8 });
        Assert.Equal(new byte[] { 8 }, healthy.Get("healthy"));
    }

    [Fact]
    public async Task InvalidFrameLengthClosesOnlyTheOffendingConnection()
    {
        using NamedPipeClientStream raw = ConnectRaw(_server.PipeName);
        byte[] length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, -1);
        await raw.WriteAsync(length);
        await raw.FlushAsync();

        bool disconnected = await Task.Run(() =>
        {
            try
            {
                return raw.ReadByte() < 0;
            }
            catch (IOException)
            {
                return true;
            }
        }).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(disconnected);

        using SuperKvClient healthy = _server.Connect();
        healthy.Set("after-invalid-length", new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, healthy.Get("after-invalid-length"));
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task InvalidOrErrorResponseInvalidatesClientConnection(byte[] response, bool useSet)
    {
        string pipeName = $"SuperKv.Fake.{Guid.NewGuid():N}";
        await using var fakeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
        Task serverTask = ServeOneResponseAsync(fakeServer, response);

        using SuperKvClient client = SuperKvClient.Connect(new SuperKvOptions { PipeName = pipeName });
        Exception? firstFailure = Record.Exception(() =>
        {
            if (useSet)
                client.Set("key", new byte[] { 1 });
            else
                client.Get("key");
        });

        Assert.NotNull(firstFailure);
        Assert.Throws<IOException>(() => client.Get("after-failure"));
        await serverTask;
    }

    static NamedPipeClientStream ConnectRaw(string pipeName)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
        pipe.Connect(1000);
        return pipe;
    }

    static async Task ServeOneResponseAsync(NamedPipeServerStream server, byte[] response)
    {
        await server.WaitForConnectionAsync();
        await ReadFrameAsync(server);
        await WriteFrameAsync(server, response);
    }

    static async Task WriteFrameAsync(Stream stream, byte[] frame)
    {
        byte[] length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, frame.Length);
        await stream.WriteAsync(length);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        byte[] length = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length);
        byte[] frame = new byte[BinaryPrimitives.ReadInt32LittleEndian(length)];
        await stream.ReadExactlyAsync(frame);
        return frame;
    }

    static byte[] CreateErrorResponse(string message)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)1);
        writer.Write((byte)1);
        writer.Write(message);
        writer.Flush();
        return stream.ToArray();
    }
}