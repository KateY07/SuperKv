# SuperKv

SuperKv 1.0 是 Garnet 的同步 C# 薄封装，只公开 `Create`、`Get`、`Set` 和释放所需的最小 API。

- 唯一后端：Garnet + StackExchange.Redis
- 服务和客户端均通过静态 `Create` 创建
- 服务必须由应用显式创建，不会由客户端自动启动
- 默认只监听 `127.0.0.1`
- Memory/Named Pipe 后端已停止支持，并在 1.0 中移除

## 安装

    <PackageReference Include="SuperKv" Version="1.0.0" />

一个 NuGet 包同时包含客户端和可嵌入的 Garnet 服务。

## 启动服务

    using SuperKv;

    using SuperKvServer server = SuperKvServer.Create(new SuperKvServerOptions
    {
        Address = "127.0.0.1",
        Port = 6379,
        IndexSize = "16m",
        MemorySize = "1g"
    });

    Console.WriteLine(server.ConnectionString);
    Console.ReadLine();

`Create` 成功返回时服务已经启动。`Dispose` 停止服务并释放端口。同一进程内对同一端点重复或并发创建只允许一个成功；已被其他进程占用的端口会使创建失败。

默认服务是纯内存、非持久化实例；停止、崩溃或重新创建服务后，原有键值不会恢复。

也可以运行仓库中的宿主：

    dotnet run --project src/SuperKv.Server -- --address 127.0.0.1 --port 6379 --index 16m --memory 1g

## 创建客户端

    using SuperKv;

    using SuperKvClient kv = SuperKvClient.Create(new SuperKvOptions
    {
        ConnectionString = "127.0.0.1:6379",
        KeyPrefix = "camera-app",
        ConnectTimeout = TimeSpan.FromSeconds(5),
        OperationTimeout = TimeSpan.FromSeconds(5)
    });

    kv.Set("camera:status", "running"u8.ToArray());
    byte[]? status = kv.Get("camera:status");

不存在的键返回 `null`；空字节数组与不存在的键不同。连接和操作超时最终由 StackExchange.Redis 执行。

`KeyPrefix` 使用无歧义的长度前缀编码，避免不同 `prefix + key` 组合意外落到同一个 Garnet 键。该编码与 0.x 版本不兼容。

## 共享连接

需要大量短生命周期客户端或多个键空间时，可复用应用已有的 multiplexer：

    using StackExchange.Redis;
    using SuperKv;

    using ConnectionMultiplexer connection =
        ConnectionMultiplexer.Connect("127.0.0.1:6379");

    using SuperKvClient cameras = SuperKvClient.Create(connection, "cameras");
    using SuperKvClient jobs = SuperKvClient.Create(connection, "jobs");

借用的 `IConnectionMultiplexer` 不归 `SuperKvClient` 所有，释放客户端不会关闭它。由连接字符串创建的客户端拥有并在释放时关闭自己的连接。

## 同步与并发

`Get` 和 `Set` 是纯同步 API，不等待异步 continuation，也不使用 `SynchronizationContext`，因此不会产生 async-over-sync 的 WinForms/WPF 同步上下文死锁。

同步网络调用仍会占用调用线程，最长由 `OperationTimeout` 或连接字符串策略约束。需要界面始终响应时，应从线程池或专用工作线程调用。一个客户端和一个 multiplexer 都允许并发使用；并发与断线恢复由 StackExchange.Redis/Garnet 实现，SuperKv 不重复实现队列、协议、缓存或重试引擎。

响应丢失后无法判断 `Set` 是否已经执行。是否重试由调用方决定；同值覆盖写通常可以安全重试。

## 安全边界

内嵌 `SuperKvServer` 只接受 IPv4/IPv6 回环地址，避免意外暴露到网络。若需要认证、TLS、持久化、集群或远程访问，应独立部署和配置 Garnet，再让 `SuperKvClient` 连接该实例。

`MemorySize` 是 Garnet hybrid log 的内存窗口，不是无限容量承诺。默认纯内存模式下，工作集超过该窗口可能淘汰旧记录；SuperKv 不在其上重复实现容量或持久化层。需要大于内存的数据集时，应按 Garnet 配置启用 storage tier。

## 验证

    dotnet format SuperKv.slnx --verify-no-changes --no-restore
    dotnet build SuperKv.slnx -c Release
    dotnet test tests/SuperKv.Tests -c Release --filter "Category!=LongRunning"
    dotnet run --project tests/SuperKv.SmokeTests -c Release
    dotnet pack src/SuperKv/SuperKv.csproj -c Release -o artifacts

完整异常矩阵、30 分钟残酷测试和延迟基准见 `docs/TESTING.md`。
