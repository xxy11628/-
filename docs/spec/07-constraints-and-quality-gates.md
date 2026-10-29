# 07 约束与质量门禁

状态：**已确认**（2026-10-28）　日期：2026-10-28
对应工作包：WP6、WP7
目的：把“约定”变成“违反就失败”。约束分三级，优先用高级别的手段。

| 级别 | 手段 | 失败时机 |
|---|---|---|
| A | 编译期：分析器、`-warnaserror`、API 快照 | 编译 |
| B | 测试期：架构测试、契约测试套件、泄漏计数 | `dotnet test` |
| C | 评审期：检查清单 | 合并前人工 |

能用 A 就不用 B，能用 B 就不用 C。

## 1. 规则总表

| 规则 | 内容 | 级别 | 手段 |
|---|---|---|---|
| R1–R5 | 分层与依赖（已有） | B | `ArchitectureTests`（已有，解析 csproj 与反射） |
| R6 | Application 只引用 Contracts、Core，不含 UI 类型 | B | 架构测试：检查项目引用与程序集引用，禁止 `PresentationFramework` 等 |
| R7 | 只有 App.* 与 Tests 引用 Plugins.* | B | 架构测试：扫描所有 csproj |
| R8 | 插件不启用 UseWPF/WinForms，不用 `-windows` 目标框架 | B | 架构测试 |
| R9 | 插件 Id 前缀与 Kind 一致、全局唯一 | B | 架构测试：实例化各插件读清单 |
| R10 | 契约公共 API 变更需更新快照并升版本 | A | `PublicApiAnalyzers`（Contracts） + 测试断言 `ContractsInfo` 与快照版本一致 |
| R11 | 枚举插件能力只用 `GetActive<T>()` | B | 架构测试：Application、App.* 中禁止出现 `GetServices<IViewContribution>` 等对贡献接口的容器解析（语法检索或禁用 API） |
| Q1 | 库项目 `await` 一律 `ConfigureAwait(false)` | A | 分析器 CA2007（对非 UI 项目设为 error） |
| Q2 | 禁用阻塞调用：`.Result`、`.Wait()`、`.GetAwaiter().GetResult()`、`Thread.Sleep`、`new Thread`、库中的 `Console.*` | A | `BannedApiAnalyzers` + `BannedSymbols.txt`；组合根与测试可单独放行 |
| Q3 | 帧必须归还：每个 `Rent` 恰好对应一次最终 `Dispose` | B | 帧泄漏计数 + 测试断言（§5） |
| Q4 | 所有丢弃有计数、有原因 | B | 05 的不变量测试 |
| Q5 | 幅度换算不得出现字面量 32768 | B | 一条测试：扫描源码，除契约中的计算处外不得出现 |
| Q6 | 构建零警告 | A | `-warnaserror`（`Directory.Build.props` 中对 CI 开启） |
| Q7 | 格式统一 | A | `.editorconfig` + `dotnet format --verify-no-changes` |
| Q8 | 每个后台任务有归属：由会话创建、停止时等待 | C | 评审清单；`Task.Run` 未观察异常的情形由分析器 CA2008/VSTHRD 类规则覆盖（先评估再启用） |

分析器包与具体版本请在 nuget.org 上核实后再写入 `Directory.Packages.props`；本文不指定版本，因为这里没有访问 nuget 的条件，未核实。

## 2. 架构测试的写法要求

- 每条规则一个测试，失败信息写明违反了哪条规则、哪个项目/类型。
- 新规则落地时，先用故意违反的改动验证该测试确实会失败（评审时手工做一次并记录在 PR 描述里）。
- 规则读取的是 csproj 的 XML 与已加载程序集，不依赖目录结构约定，以免被改名绕过。
- 新增项目（如 Application）若未被任何规则覆盖，应有一条“所有项目都已分类”的测试失败，逼着作者给它归类。

## 3. 公共 API 快照

- 在 Contracts 项目启用 `Microsoft.CodeAnalysis.PublicApiAnalyzers`，建立 `PublicAPI.Shipped.txt` 与 `PublicAPI.Unshipped.txt`。
- 约定：0.3 完成时把当前 API 整理为 Shipped；此后新增成员出现在 Unshipped，经评审并发布版本时并入 Shipped。
- 删除或修改 Shipped 中的成员会产生编译错误，从而把“只增不改”落实。0.x 阶段若确需破坏性变更，必须同时升次版本并在 01 文档中记录，且在 PR 中显式说明。
- 一条测试断言：`ContractsInfo.Current` 的版本与 Shipped 文件头部登记的版本一致，防止改了接口忘了升版本。

## 4. 插件契约测试套件（WP7）

做法：在 Tests（或新建 `UltrasoundHost.Testing`）中提供抽象基类，各插件的测试项目继承并提供被测实例。目的是“任何设备/处理器只要通过这套测试，就能放进系统”。

### 4.1 所有插件（`PluginContractTests`）

| 编号 | 检查 |
|---|---|
| PG1 | 清单完整：Id、版本、Kind、DisplayName 非空；Id 前缀符合 R9 |
| PG2 | `Register` 可对全新的 `ServiceCollection` 调用，不解析任何服务，不依赖静态状态 |
| PG3 | 依赖的 Id 都存在于已知插件集合，无环 |
| PG4 | 在 `Start` 抛异常后，`Stop` 仍可安全调用 |

### 4.2 设备（`DeviceContractTests`）

| 编号 | 检查 |
|---|---|
| DC1 | 能力声明自洽（范围下限不大于上限，探头阵元数与通道数相容） |
| DC2 | 越界参数被拒绝（`ValidateAgainst` 报错，或 `ApplyAsync` 抛出明确异常） |
| DC3 | 应用版本 n 之后产生的帧，`ParamVersion` 在有限帧数内变为 n，且不会出现比 n 更大的值 |
| DC4 | `StartAsync`/`StopAsync` 幂等；`StopAsync` 在限定时间内返回 |
| DC5 | 写入通道失败时由设备释放该帧（F4）；停止并排空后池中未归还数为 0 |
| DC6 | `Sequence` 严格递增、步长 1（或按 C4 用标志声明断档） |
| DC7 | 帧形状（Tx、Rx、Samples）与能力和参数一致；ADC 格式与能力一致 |
| DC8 | 取消令牌被遵守；取消后不再写入通道 |
| DC9 | 运行中 `DisposeAsync` 安全，不抛异常、不泄漏 |

### 4.3 处理器（`ProcessorContractTests`）

| 编号 | 检查 |
|---|---|
| PC1 | 同一输入重复处理，结果相同（确定性） |
| PC2 | 不修改帧样本；`Process` 返回后不继续持有帧（若要持有须 `Retain`） |
| PC3 | 帧版本与所给参数版本不一致时明确拒绝 |
| PC4 | 幅度结果与 ADC 位数无关（同一信号在 12/16 位下得到相同百分比） |

### 4.4 视图（`ViewContractTests`）

| 编号 | 检查 |
|---|---|
| VC1 | 视图 Id 唯一、标题非空、种类非空 |
| VC2 | `Matches` 对设备方案的判断符合声明 |
| VC3 | `Needs` 取值合法（点数、刷新率为正） |

### 4.5 模拟器的故障注入

给 `SimulatorOptions` 增加测试用开关：注入序号断档、注入溢出标志、注入未知参数版本、注入写入延迟、注入异常。DC 与 DP 系列测试用它构造场景，真实设备插件出来之前，这些场景就已经可测。

## 5. 帧泄漏计数

现状：帧未归还时 `ArrayPool` 只是悄悄新分配，没有任何报错，F1–F4 没人盯。

做法：
- `AcquisitionFrame` 增加调试用的“在外帧数”计数（`Interlocked`，`Rent` 加一、最终释放减一）。
- 因为计数是进程级静态，涉及它的测试放进同一个 xunit 集合并关闭该集合内的并行（`[Collection("FramePool")]`），测试开始记基线、结束断言回到基线。
- 发布构建里该计数保留（成本是一次原子加减）；会话在停止时若发现非零，记录错误日志。

## 6. 构建与验证脚本

- `scripts/verify.ps1`（以及需要时的 `verify.sh`）：`dotnet restore`、`dotnet build -warnaserror`、`dotnet test`、`dotnet format --verify-no-changes`，任何一步失败即退出非零。
- 约定：合并前必须本地通过 `verify`。
- 将来有持续集成时，CI 只是调用同一个脚本，不另写一套。
- WPF 项目只能在 Windows 上编译；CLI 与测试跨平台。因此 WP1 把核心逻辑搬出 WPF 项目后，绝大部分测试可在任何平台跑。

## 7. 评审检查清单（C 级）

合并前由作者自查、评审人复核：

- [ ] 新增/修改契约：版本已升、API 快照已更新、01 文档已记录。
- [ ] 新增插件：通过对应的契约测试套件；Id 与 Kind 一致；没有引用其他插件。
- [ ] 新增后台任务：有归属，停止时被等待，带超时。
- [ ] 任何丢弃数据的代码路径：有计数、有原因（Q4）。
- [ ] 没有在 UI 线程上等待后台任务。
- [ ] 文档与代码一致（本组文档里的规则编号被代码和测试引用）。

## 8. 新增插件的完成定义

1. 清单与依赖正确，注册无副作用；
2. 通过相应的契约测试套件（§4）；
3. 有一条端到端测试，经过会话从设备到判伤结果；
4. 文档里有一节说明：能力声明、已知限制、配置项；
5. `verify` 通过。

## 9. 实现备注（2026-10-28，M2 WP6）

| 规则 | 计划手段 | 实际落地 | 原因 |
|---|---|---|---|
| R7–R9 | 架构测试 | `ArchitectureTests` 已覆盖 | 按计划 |
| R10 | PublicApiAnalyzers + 快照 | `ApiSnapshotTests` 契约 API 快照 + `ContractsInfo.Current` 版本行检查（B 级） | 当前离线环境没有可用版本：5.x 需要 Roslyn 4.12，SDK 为 4.11；3.3.4 的代码修复无法在 `dotnet format` 中生成 `PublicAPI.*.txt`。快照变更仍会失败，只增不改的约束保留 |
| Q1 | CA2007 分析器 | `ArchitectureTests.Q1...` 源码检查（B 级） | SDK 与可获取的 NetAnalyzers 包中均未包含 CA2007；导入 8.0.0 包验证不到该规则 |
| Q2 | BannedApiAnalyzers | 已接入库项目，`BannedSymbols.txt` 生效（RS0030，探针验证） | 按计划；App.* 与测试按 07 说明豁免 |
| Q6/Q7 | `-warnaserror` + `dotnet format` | `scripts/verify.ps1` | 按计划；沙箱内 `dotnet test` 受 testhost 限制，用 xunit console 等价执行 |

后续换成 CI 镜像（VS 2022 / 新 SDK）时，可把 R10、Q1 升级为 A 级分析器，本表随之更新。
**WP7 落地（2026-10-28）**：§4 的插件契约套件以抽象基类形式放在 `tests/UltrasoundHost.Tests`（`PluginContractTestsBase`、`DeviceContractTestsBase`、`ProcessorContractTestsBase`、`ViewContractTestsBase`），内置插件各自继承；§4.5 的模拟器故障注入通过 `SimulatorOptions` 的测试开关提供（也可从 `SessionOptions.DeviceSettings` 打开）；§5 的帧泄漏计数落在 `AcquisitionFrame.OutstandingFrames`（internal），会话停止时检查，测试以 `FramePool` 集合 + 全局关闭并行保证基线稳定。