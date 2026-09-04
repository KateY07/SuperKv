# 测试与性能验证

SuperKv 是薄封装，测试聚焦本库拥有的行为和边界，不重复验证 Garnet 服务端内部算法。

## 覆盖范围

| 层级 | 当前验证 |
|---|---|
| 静态分析 | SDK `latest-recommended` analyzers、代码风格检查、警告视为错误 |
| API 契约 | 同步/异步字节、字符串、JSON、缺失值、删除、存在判断、全部 Set 条件、TTL、原子计数 |
| 边界 | 空键、无效 TTL、非法条件、无效 JSON、非数字计数、Int64 溢出、重复释放、释放后访问 |
| 高并发 | 32 路共享客户端执行 8000 次原子写；8 个独立客户端竞争写；64 路 NX；32 路热读；操作量可按环境变量放大 |
| 多进程 | 父进程启动真实 Garnet，子进程读取父进程数据并原子更新计数 |
| 持续负载 | 1/8/32 个独立客户端持续执行 SET/GET/INCR，并核对无丢失原子计数 |
| 性能 | BenchmarkDotNet 对比 SuperKv 与原始 StackExchange.Redis 的 GET/SET/INCR |

## GitHub Actions

- `CI and NuGet package` 在每次推送和拉取请求中执行格式检查、静态分析、构建、快速测试、90% 行/分支覆盖率门禁、多进程 smoke test，并上传 `.nupkg` 与 Cobertura 报告。
- `Long test matrix` 每周日 03:17（北京时间）运行，也可在 Actions 页面手动触发。矩阵覆盖 .NET 8/10 SDK 与 1/8/32 个独立客户端；常规并发操作量默认放大 10 倍，每个持续负载用例默认运行 120 秒。
- 长工作流同时运行完整 GET/SET/INCR × 4 种值大小的 BenchmarkDotNet `MediumRun`，报告保存为工作流产物。手动触发时可改用 `short` 或 `long`。

GitHub 托管 Runner 的单个 Job 最长可运行 6 小时；本仓库另设 30 分钟压力测试和 180 分钟基准测试超时，避免故障任务无界占用额度。云端共享 Runner 适合发现明显回归，微秒级绝对延迟仍应在固定硬件上复核。
## 正式测试

```powershell
dotnet test tests/SuperKv.Tests/SuperKv.Tests.csproj -c Release
```

## 覆盖率

只统计 `SuperKv`，不把 Garnet、StackExchange.Redis 或测试程序集算入指标：

```powershell
dotnet test tests/SuperKv.Tests/SuperKv.Tests.csproj -c Release `
  /p:CollectCoverage=true `
  /p:CoverletOutput=../../TestResults/coverage.cobertura.xml `
  /p:CoverletOutputFormat=cobertura `
  '/p:Include=[SuperKv]*' `
  '/p:Exclude=[SuperKv.Tests]*'
```

当前实测结果：

| Line | Branch | Method |
|---:|---:|---:|
| 100% | 91.17% | 100% |

### 2026-09-04 GET ShortRun 基线

环境：Windows 11、Intel Core Ultra 9 285H、.NET 8.0.29、Garnet 2.1.4，1 次 launch、3 次 warmup、3 次测量。

| 值大小 | Raw 均值 | SuperKv 均值 | 均值比率 | Raw 分配 | SuperKv 分配 |
|---:|---:|---:|---:|---:|---:|
| 16 B | 41.08 µs | 41.95 µs | 1.02 | 352 B | 472 B |
| 128 B | 34.80 µs | 52.82 µs | 1.52 | 464 B | 584 B |
| 1 KiB | 41.30 µs | 57.80 µs | 1.40 | 1,360 B | 1,480 B |
| 64 KiB | 99.50 µs | 114.54 µs | 1.16 | 65,896 B | 66,016 B |

短样本的置信区间较宽，只适合验证基准链路并建立初始参考。稳定回归判断应增加 launch 和 iteration，并保证电源计划及后台负载一致。完整报告由 BenchmarkDotNet 输出到 `BenchmarkDotNet.Artifacts/results`，该目录不提交 Git。
本地选择更长的 BenchmarkDotNet job：

```powershell
$env:SUPERKV_BENCHMARK_JOB = 'medium' # 可选 short、medium、long
dotnet run --project benchmarks/SuperKv.Benchmarks -c Release -- --filter '*'
```

本地运行与 Actions 等价的长压力测试：

```powershell
$env:SUPERKV_LONG_TESTS = '1'
$env:SUPERKV_LONG_CLIENTS = '32'
$env:SUPERKV_LONG_DURATION_SECONDS = '120'
$env:SUPERKV_STRESS_MULTIPLIER = '10'
dotnet test tests/SuperKv.Tests -c Release
```
## 多进程 smoke test

```powershell
dotnet run --project tests/SuperKv.SmokeTests -c Release
```

## 延迟基准

```powershell
dotnet run --project benchmarks/SuperKv.Benchmarks -c Release -- --filter '*'
```

只测试 GET：

```powershell
dotnet run --project benchmarks/SuperKv.Benchmarks -c Release -- --filter '*Get*'
```

报告包含均值、P50、P95、吞吐相关统计和每次操作的托管分配。基准在 16 B、128 B、1 KiB、64 KiB 四种值大小下，将 SuperKv 与同一 Garnet 实例上的原始 StackExchange.Redis 调用直接对比。

绝对延迟依赖 CPU、电源计划、调试器、防病毒软件和系统负载。回归判断应优先观察同机同批次的 SuperKv/Raw 比值；建议把 P95 比值恶化 10% 作为调查阈值，而不是跨机器比较固定微秒数。