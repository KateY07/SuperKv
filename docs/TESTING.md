# 测试与性能验证

SuperKv 的客户端、内存服务端和协议实现集中在一个 SuperKv.cs。测试围绕同步 Get/Set、Named Pipe 边界、Garnet 兼容性和多进程共享语义展开。

## 覆盖范围

| 层级 | 验证内容 |
|---|---|
| 静态分析 | SDK latest-recommended analyzers、格式检查、警告视为错误、禁止同步等待异步任务 |
| API 契约 | Get 缺失返回 null、空值、覆盖写、值副本隔离、连接超时、参数验证和释放 |
| 同一客户端 | 32 路调用共享一条 Pipe，客户端内部排队且协议帧不交错 |
| 多客户端 | 12 条独立 Pipe 并行写入，16 条 Pipe 热读和热写 |
| 极端输入 | Unicode/超长键、0 B、1 B、1 MiB 值 |
| 不完整命令 | 合法长度头后停发，单帧超时关闭该连接，健康客户端继续工作 |
| 多进程 | 父进程启动服务，真实子进程读写同一份内存数据 |
| 同步上下文 | API 在不泵送的自定义 SynchronizationContext 中完成 |
| Garnet 集成 | 启动真实 Garnet，验证缺失键、空值、二进制值、覆盖、共享前缀和同客户端并发 |
| 性能 | BenchmarkDotNet 测量同步 Get/Set，覆盖 16 B 至 64 KiB 值 |
| 长时压力 | 1/8/32 客户端，循环 0 B 至 1 MiB 边界值、冷热键和读写校验 |

## 快速验证

    dotnet format SuperKv.slnx --verify-no-changes --no-restore
    dotnet build SuperKv.slnx -c Release --no-restore
    dotnet test tests/SuperKv.Tests -c Release --filter "Category!=LongRunning"
    dotnet run --project tests/SuperKv.SmokeTests -c Release

## 覆盖率

    dotnet test tests/SuperKv.Tests/SuperKv.Tests.csproj -c Release \
      --filter "Category!=LongRunning" \
      /p:CollectCoverage=true \
      /p:CoverletOutput=../../TestResults/coverage.cobertura.xml \
      /p:CoverletOutputFormat=cobertura \
      "/p:Include=[SuperKv]*" \
      "/p:Exclude=[SuperKv.Tests]*"

当前快速套件实测：行覆盖率 97.08%、分支覆盖率 92.10%、方法覆盖率 100%。CI 门槛为行 90%、分支 90%。

## GitHub Actions

- CI and NuGet package：每次推送和拉取请求运行格式、静态分析、构建、快速测试、覆盖率、跨进程测试，并只生成一个 SuperKv NuGet 包。
- Long test matrix：每周或手动执行 .NET 8/10 SDK × 1/8/32 客户端矩阵；默认每个持续测试运行 1800 秒。
- 长工作流另外运行同步 Get/Set 的 BenchmarkDotNet 延迟基准并上传报告。

## 本地 30 分钟压力测试

    $env:SUPERKV_LONG_TESTS = '1'
    $env:SUPERKV_LONG_CLIENTS = '32'
    $env:SUPERKV_LONG_DURATION_SECONDS = '1800'
    dotnet test tests/SuperKv.Tests -c Release --filter "Category=LongRunning"

## 延迟基准

    $env:SUPERKV_BENCHMARK_JOB = 'medium'
    dotnet run --project benchmarks/SuperKv.Benchmarks -c Release -- --filter '*'

云端共享 Runner 适合发现明显回归；微秒级绝对延迟应在固定硬件、电源计划和系统负载下复核。
