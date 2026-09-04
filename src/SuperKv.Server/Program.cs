using SuperKv;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: superkv-server [--pipe <name>] [--request-timeout-ms <milliseconds>]");
    return;
}

string pipeName = "SuperKv.Default";
int requestTimeoutMilliseconds = 30_000;

for (int index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length)
        throw new ArgumentException("Every option requires a value. Use --help for usage.");

    switch (args[index])
    {
        case "--pipe":
            pipeName = args[index + 1];
            break;

        case "--request-timeout-ms":
            if (!int.TryParse(args[index + 1], out requestTimeoutMilliseconds) ||
                requestTimeoutMilliseconds <= 0)
            {
                throw new ArgumentException("--request-timeout-ms must be a positive integer.");
            }
            break;

        default:
            throw new ArgumentException($"Unknown option '{args[index]}'. Use --help for usage.");
    }
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var server = new SuperKvMemoryServer(new SuperKvServerOptions
{
    PipeName = pipeName,
    RequestTimeout = TimeSpan.FromMilliseconds(requestTimeoutMilliseconds)
});
Console.WriteLine(
    $"SuperKv.Server listening on pipe '{pipeName}' with a {requestTimeoutMilliseconds} ms request timeout. " +
    "Press Ctrl+C to stop.");
server.Run(shutdown.Token);