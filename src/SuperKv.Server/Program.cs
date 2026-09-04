using SuperKv;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: superkv-server [--address <loopback-ip>] [--port <number>] [--index <size>] [--memory <size>]");
    return;
}

string address = "127.0.0.1";
int port = 6379;
string indexSize = "16m";
string memorySize = "1g";

for (int index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length)
        throw new ArgumentException("Every option requires a value. Use --help for usage.");

    switch (args[index])
    {
        case "--address":
            address = args[index + 1];
            break;

        case "--port":
            if (!int.TryParse(args[index + 1], out port))
            {
                throw new ArgumentException("--port must be an integer.");
            }
            break;

        case "--index":
            indexSize = args[index + 1];
            break;

        case "--memory":
            memorySize = args[index + 1];
            break;

        default:
            throw new ArgumentException($"Unknown option '{args[index]}'. Use --help for usage.");
    }
}

using var stopped = new ManualResetEventSlim();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopped.Set();
};

using SuperKvServer server = SuperKvServer.Create(new SuperKvServerOptions
{
    Address = address,
    Port = port,
    IndexSize = indexSize,
    MemorySize = memorySize
});
Console.WriteLine($"SuperKv.Server listening at {server.ConnectionString}. Press Ctrl+C to stop.");
stopped.Wait();
