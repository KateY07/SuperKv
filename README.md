# SuperKv

SuperKv 是一个 Windows 本机多进程内存 KV：

    多个 C# 客户端进程
            ↓ 每个客户端一条持久 Named Pipe
    明确启动的 SuperKv.Server
            ↓
    ConcurrentDictionary<string, byte[]>

它不依赖 Garnet、Redis、数据库或第三方运行时包。服务端不持久化，重启后数据清空。核心客户端、服务端、协议和存储实现全部位于单个 src/SuperKv/SuperKv.cs 文件。

## 安装

客户端库：

    <PackageReference Include="SuperKv" Version="0.1.0" />

服务端工具：

    dotnet tool install --global SuperKv.Server --version 0.1.0
    superkv-server --pipe SuperKv.Default --request-timeout-ms 30000

服务必须明确启动。客户端不会自动启动、选举或托管服务端。

也可以在自己的服务进程中运行：

    using SuperKv;

    var server = new SuperKvMemoryServer(new SuperKvServerOptions
    {
        PipeName = "MyApp.Kv",
        RequestTimeout = TimeSpan.FromSeconds(30)
    });
    server.Run(stoppingToken);

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

同步实现直接执行同步 Pipe I/O，不等待异步 continuation，也不访问 SynchronizationContext，因此不会发生 WinForms/WPF 同步上下文死锁。但同步 IPC 必然占用调用线程直到响应返回；要求界面始终可响应时，应由调用方在线程池或专用工作线程调用。

## 并发与断线语义

- 一个 SuperKvClient 持有一条 Pipe；同一实例上的重叠调用在客户端内部排队，避免请求帧和响应帧交错。
- 不同客户端各自使用独立 Pipe，服务端并行处理并直接访问线程安全的 ConcurrentDictionary。
- 请求采用“长度头 + 消息体”分帧，只有完整收到整帧后才执行命令。
- 客户端声明长度后不继续发送时，服务端在 RequestTimeout 到期后关闭该连接，其他客户端不受影响。
- 通信失败后客户端连接作废，不会自动恢复或重试；调用方应重新 Connect。
- 如果服务端已经执行 Set，但响应在途中丢失，客户端无法判断该次写入是否生效。是否重试由调用方决定；同值覆盖写通常可安全重试。

SuperKv 没有 TTL、删除、存在判断、计数、持久化、自动恢复或分布式能力。

## 验证与打包

    dotnet build SuperKv.slnx -c Release
    dotnet test tests/SuperKv.Tests -c Release
    dotnet run --project tests/SuperKv.SmokeTests -c Release
    dotnet pack src/SuperKv/SuperKv.csproj -c Release -o artifacts
    dotnet pack src/SuperKv.Server/SuperKv.Server.csproj -c Release -o artifacts

测试矩阵、覆盖率和延迟基准见 docs/TESTING.md。GitHub Actions 会构建客户端 NuGet 和显式服务端 Tool 包。