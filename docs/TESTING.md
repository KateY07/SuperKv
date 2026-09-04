# 测试与性能验证

SuperKv 是薄封装，因此测试分为两层：

1. 全面验证 SuperKv 自己负责的工厂、配置、生命周期、键编码、连接所有权和同步 API。
2. 把 Garnet 当作不可信黑盒，以真实服务执行并发、断服、重启、跨进程、边界值和长时间压力测试。

实现不复制 Garnet 的 RESP、存储、并发控制或重试逻辑。

## 快速测试矩阵

| 范围 | 场景 |
|---|---|
| 基础契约 | 缺失返回 `null`、空值、覆盖写、任意二进制、返回值与输入值无别名 |
| 键边界 | Unicode、4096 字符键、无歧义前缀隔离、空键和 null 键 |
| 值边界 | 0 B、普通二进制、1 MiB 随机值 |
| 客户端工厂 | 空连接串、null 前缀、连接/操作超时的零值、负值和溢出值 |
| 服务工厂 | 非回环地址、空容量参数、非法端口、已占用端口 |
| 重复创建 | 同端点第二次创建、8 路同端点创建竞争、不同端点并发创建 |
| 生命周期 | 并发重复释放、同端点反复创建/释放 10 次、重启数据清空、Garnet 参数失败后端点恢复 |
| 连接所有权 | 自有 multiplexer、借用 multiplexer、多个客户端共享、底层连接先释放 |
| 并发 | 单客户端 8 路并发、多个客户端热点键并发读写且值不撕裂 |
| 故障 | 服务停止后的有界失败、负载中停服、服务重启后原客户端重连 |
| 同步上下文 | 不泵送的自定义 `SynchronizationContext` 中同步 Get/Set |
| 多进程 | 父进程启动 Garnet，真实子进程读写同一数据 |
| API 面 | 客户端和服务端构造函数不公开，均通过静态 `Create` |

快速测试中的所有等待、重连和并发用例都带外层硬超时，故障不会无限挂住测试进程。

## 本地快速验证

    dotnet format SuperKv.slnx --verify-no-changes --no-restore
    dotnet build SuperKv.slnx -c Release --no-restore
    dotnet test tests/SuperKv.Tests -c Release --no-build --filter "Category!=LongRunning"
    dotnet run --project tests/SuperKv.SmokeTests -c Release --no-build

## 长时残酷测试

GitHub Actions 每周及手动运行以下 12 个组合：

- .NET SDK：8、10
- 客户端数量：1、8、32
- 连接模式：每客户端独占 multiplexer、所有客户端共享 multiplexer
- 默认每个组合持续 1800 秒

每个工作线程为独立长驻线程，持续执行：

- 0 B、1 B、16 B、128 B、1 KiB、64 KiB、1 MiB 值循环；
- 每种值大小使用固定循环槽位，保证工作集装入 1 GiB 内存窗口并持续原位覆盖；
- 所有客户端竞争同一 4 KiB 热点键，并检查响应绝不撕裂；
- 周期性读取确定不存在的键；
- 任一线程失败立即取消其余线程；
- 总时长之外另有 15 秒硬看门狗。

本地运行：

    $env:SUPERKV_LONG_TESTS = '1'
    $env:SUPERKV_LONG_CLIENTS = '32'
    $env:SUPERKV_LONG_CONNECTION_MODE = 'shared'
    $env:SUPERKV_LONG_DURATION_SECONDS = '1800'
    dotnet test tests/SuperKv.Tests -c Release --filter "Category=LongRunning"

将 `SUPERKV_LONG_CONNECTION_MODE` 改为 `owned` 可测试每客户端独占连接。`SUPERKV_STRESS_MULTIPLIER` 可将快速并发用例放大 1–100 倍。

## 覆盖率

    dotnet test tests/SuperKv.Tests/SuperKv.Tests.csproj -c Release +      --filter "Category!=LongRunning" +      /p:CollectCoverage=true +      /p:CoverletOutput=../../TestResults/coverage.cobertura.xml +      /p:CoverletOutputFormat=cobertura +      "/p:Include=[SuperKv]*" +      "/p:Exclude=[SuperKv.Tests]*"

CI 要求行覆盖率和分支覆盖率均不低于 90%。

当前快速套件实测：行覆盖率 97.24%、分支覆盖率 100%、方法覆盖率 100%。

## 延迟基准

    $env:SUPERKV_BENCHMARK_JOB = 'medium'
    dotnet run --project benchmarks/SuperKv.Benchmarks -c Release -- --filter '*'

BenchmarkDotNet 测量 Garnet 同步 Get/Set 的 P50、P95 与内存分配，值大小覆盖 16 B、128 B、1 KiB、64 KiB。共享 Runner 用于发现明显回归；绝对延迟应在固定硬件、电源计划和系统负载下复核。
