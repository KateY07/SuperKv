# SuperKv

SuperKv 是一个只提供同步 Get/Set 的本机多进程内存 KV，支持两个可切换后端：

- `Memory`（默认）：Named Pipe + `ConcurrentDictionary<string, byte[]>`
- `Garnet`：StackExchange.Redis + 外部 Garnet 服务

两种后端都要求先明确启动服务；客户端不会自动启动、选举或托管服务端。核心客户端、内存服务端和协议仍集中在单个 `src/SuperKv/SuperKv.cs` 文件。

## 安装

客户端库：

    <PackageReference Include="SuperKv" Version="0.2.0" />

默认内存服务端已经包含在同一个 `SuperKv` 包中，应在明确的服务进程里启动：

    using SuperKv;

    var server = new SuperKvMemoryServer(new SuperKvServerOptions
    {
        PipeName = "MyApp.Kv",
        RequestTimeout = TimeSpan.FromSeconds(30)
    });
    server.Run(stoppingToken);

仓库中的现成宿主可直接运行，但不再单独打包：

    dotnet run --project src/SuperKv.Server -- --pipe SuperKv.Default --request-timeout-ms 30000

Garnet 服务：

    dotnet tool install --global garnet-server --version 2.1.4
    garnet-server --bind 127.0.0.1 --port 6379 --no-pubsub --no-obj

## 客户端 API

客户端只提供静态 Connect、同步 Get/Set 和释放：

    using SuperKv;

    using SuperKvClient kv = SuperKvClient.Connect(new SuperKvOptions
    {
        PipeName = "MyApp.Kv",
        KeyPrefix = "camera-app:",
        ConnectTimeout = TimeSpan.FromSeconds(5)
    });

    kv.Set("camera:status", "running"u8.ToArray());
    byte[]? status = kv.Get("camera:status"); // 不存在时返回 null

ConnectTimeout 只限制建立连接的等待时间。构造函数不公开，客户端必须通过静态 Connect 创建。

切换到 Garnet：

    using SuperKvClient kv = SuperKvClient.Connect(new SuperKvOptions
    {
        Backend = SuperKvBackend.Garnet,
        GarnetConnectionString = "127.0.0.1:6379,syncTimeout=5000",
        KeyPrefix = "camera-app:",
        ConnectTimeout = TimeSpan.FromSeconds(5)
    });

    kv.Set("camera:status", "running"u8.ToArray());
    byte[]? status = kv.Get("camera:status");

Garnet 后端由每个 `SuperKvClient` 长期持有并复用一个 `ConnectionMultiplexer`；不要为每次 Get/Set 重建客户端。

同步实现直接调用后端的同步 I/O，不等待异步 continuation，也不访问 SynchronizationContext，因此不会发生 WinForms/WPF 同步上下文死锁。但同步 IPC 必然占用调用线程直到响应或超时；要求界面始终可响应时，应由调用方在线程池或专用工作线程调用。Garnet 的操作超时可通过连接字符串中的 `syncTimeout` 设置。

## 并发与断线语义

- Memory：一个客户端持有一条 Pipe；同一实例上的重叠调用内部排队，不同客户端通过独立 Pipe 并行访问 ConcurrentDictionary。
- Memory：请求采用“长度头 + 消息体”分帧；不完整消息超时后只关闭对应连接，通信失败后需重新 Connect。
- Garnet：同一客户端的并发调用由 StackExchange.Redis 多路复用，连接中断后的恢复由 ConnectionMultiplexer 管理。
- 两种后端都无法在响应丢失后判断 Set 是否已执行；是否重试由调用方决定，同值覆盖写通常可安全重试。

SuperKv 公共 API 不提供 TTL、删除、存在判断、计数、持久化、自动恢复或分布式能力。

## 验证与打包

    dotnet build SuperKv.slnx -c Release
    dotnet test tests/SuperKv.Tests -c Release
    dotnet run --project tests/SuperKv.SmokeTests -c Release
    dotnet pack src/SuperKv/SuperKv.csproj -c Release -o artifacts

测试矩阵、覆盖率和延迟基准见 docs/TESTING.md。GitHub Actions 只构建一个 `SuperKv` NuGet 包。
