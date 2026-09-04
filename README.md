# SuperKv

SuperKv 是一个面向本机多进程 IPC 的轻量 C# KV NuGet 库：

```text
多个 C# 进程
    ↓ SuperKv / StackExchange.Redis
127.0.0.1:6379
    ↓
GarnetServer.exe
```

SuperKv 只封装 Garnet 常用 KV 操作，目标框架为 .NET 8。原始值使用 `byte[]`，并提供 UTF-8 字符串和 `System.Text.Json` 扩展。

## 安装

```xml
<PackageReference Include="SuperKv" Version="0.1.0" />
```

## 使用

先启动 Garnet，然后在每个业务进程中创建并复用一个长生命周期客户端：

```csharp
using SuperKv;

await using ISuperKv kv = await SuperKvClient.OpenAsync(new SuperKvOptions
{
    KeyPrefix = "my-app:",
    Garnet = new GarnetOptions
    {
        ConnectionString = "127.0.0.1:6379,abortConnect=false"
    }
});

await kv.SetStringAsync("camera:status", "running", TimeSpan.FromMinutes(1));
string? status = await kv.GetStringAsync("camera:status");
long frame = await kv.IncrementAsync("camera:frame");
```

`SuperKvClient` 内部复用一个 `ConnectionMultiplexer`，不要为每次读写创建客户端。

后台线程、Worker Service 或控制台程序也可使用同步 API：

```csharp
using ISuperKv kv = SuperKvClient.Open(options);
kv.SetString("camera:status", "running");
string? status = kv.GetString("camera:status");
byte[]? payload = kv.GetValue("camera:frame");
```

同步 API 直接使用 StackExchange.Redis 的同步调用，不做 async-over-sync，因此不依赖 `SynchronizationContext`、不会产生该类死锁。同步网络 I/O 仍会占用调用线程；WinForms、WPF、WinUI、MAUI 等 UI 事件处理程序应使用上方异步 API，才能保证界面不被阻塞。

## API

```csharp
bool written = await kv.SetAsync(
    "key",
    new byte[] { 1, 2, 3 },
    timeToLive: TimeSpan.FromSeconds(30),
    condition: SuperKvSetCondition.OnlyIfMissing);

byte[]? value = await kv.GetAsync("key");
bool exists = await kv.ExistsAsync("key");
TimeSpan? ttl = await kv.GetTimeToLiveAsync("key");
bool deleted = await kv.DeleteAsync("key");
```

- `GetTimeToLiveAsync` 返回 `null` 表示键不存在或没有过期时间。
- `IncrementAsync` 要求已有值是十进制 `Int64`；不存在时从 `0` 开始。
- `KeyPrefix` 用于隔离同一 Garnet 实例中的不同应用。
- 取消正在执行的写操作只停止本地等待，服务端操作仍可能已完成。

## 验证

测试会在随机本机端口启动真实 Garnet 2.1.4：

```powershell
dotnet build SuperKv.slnx -c Release
dotnet test tests/SuperKv.Tests -c Release
dotnet run --project tests/SuperKv.SmokeTests -c Release
```

当前 SuperKv 程序集覆盖率：行 100%、分支 91.17%、方法 100%。测试矩阵、覆盖率命令和延迟基准见 [docs/TESTING.md](docs/TESTING.md)。

GitHub Actions 在每次推送/拉取请求中执行快速质量门禁并生成可下载的 NuGet 包；每周及手动工作流执行 .NET SDK、并发客户端数量、持续负载和延迟基准的长矩阵。

本地打包：

```powershell
dotnet pack src/SuperKv/SuperKv.csproj -c Release
```