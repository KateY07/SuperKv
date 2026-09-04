# SuperKv

SuperKv 是一个面向本机多进程 IPC 的 C# KV NuGet 库。业务代码使用同一套 API，可在两种后端之间切换：

- **Garnet + StackExchange.Redis**：独立服务进程、热读低延迟、原生 TTL 和原子计数。
- **LightningDB (LMDB)**：无需服务进程、内存映射持久化、多进程共享、单写多读。

目标框架为 .NET 8，原始值以 `byte[]` 表示，并提供 UTF-8 字符串和 `System.Text.Json` 扩展。

## 安装

```xml
<PackageReference Include="SuperKv" Version="0.1.0" />
```

本地打包：

```powershell
dotnet pack src/SuperKv/SuperKv.csproj -c Release
```

## Garnet 后端

先启动一个 Garnet 服务进程并监听本机端口，然后在每个业务进程中各创建一个长生命周期客户端：

```csharp
using SuperKv;

await using ISuperKv kv = await SuperKvClient.OpenAsync(new SuperKvOptions
{
    Backend = SuperKvBackend.Garnet,
    KeyPrefix = "my-app:",
    Garnet = new GarnetBackendOptions
    {
        ConnectionString = "127.0.0.1:6379,abortConnect=false"
    }
});

await kv.SetStringAsync("camera:status", "running", TimeSpan.FromMinutes(1));
string? status = await kv.GetStringAsync("camera:status");
long frame = await kv.IncrementAsync("camera:frame");
```

`SuperKvClient` 内部持有并复用一个 `ConnectionMultiplexer`。不要为每次读写创建客户端。

## LightningDB 后端

所有进程使用同一个绝对目录：

```csharp
using SuperKv;

await using ISuperKv kv = await SuperKvClient.OpenAsync(new SuperKvOptions
{
    Backend = SuperKvBackend.LightningDb,
    KeyPrefix = "my-app:",
    LightningDb = new LightningDbBackendOptions
    {
        DirectoryPath = @"C:\ProgramData\MyApp\superkv",
        MapSize = 1024L * 1024 * 1024
    }
});

await kv.SetJsonAsync("camera:state", new { Status = "running", Frame = 42 });
var state = await kv.GetJsonAsync<CameraState>("camera:state");
```

LightningDB 的 `MapSize` 是地址空间上限，不会立即分配同等大小的磁盘文件。LMDB 支持多进程读和单写事务；写入较多时应预留足够大的映射。SuperKv 在该后端自行实现 TTL，过期项会在访问时惰性清理。

## 统一语义

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

说明：

- `GetTimeToLiveAsync` 返回 `null` 时，表示键不存在或键没有过期时间。
- `IncrementAsync` 要求已有值是 UTF-8 十进制 `Int64`；不存在时从 `0` 开始。
- 切换后端只切换访问实现，不会自动搬迁数据。
- LightningDB 0.23 使用 LMDB 1.0 文件格式，与旧版 LightningDB 生成的 LMDB 0.9 数据文件不兼容。

## 验证

```powershell
dotnet build SuperKv.slnx -c Release
dotnet run --project tests/SuperKv.SmokeTests -c Release
```
