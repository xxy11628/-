# 第 1 章　插件体系约定

状态：**已确认**（2026-10-25）　适用范围：上位机软件 v0.x
本章只约定"改起来代价大"的东西；其余等有真实需求再定。

## 1.1 已确认的决策

| 决策 | 结论 |
|---|---|
| 架构 | 微内核 + 插件（Plugin / Microkernel），起步形态为模块化单体 |
| 插件粒度 | 一个领域一个插件项目，插件内部的小功能不再拆成独立插件 |
| 插件依赖 | 允许在清单里按 Id 声明依赖，内核按拓扑序启动；**禁止循环依赖**；**禁止直接引用对方程序集** |
| 加载方式 | 编译期装配；接口按将来可动态加载预留（见 1.9） |
| 运行时 | .NET 8（LTS）。Contracts、Core 与插件为 `net8.0`；WPF 外壳（`App.Wpf`）为 `net8.0-windows`（见第 2 章） |
| 部署平台 | 仅 Windows |

## 1.2 程序集分层与依赖规则

```
Contracts            接口 + 数据契约，唯一的"公共语言"
Core                 插件宿主（装载、排序、能力匹配、生命周期、异常隔离）
Application          会话与采集编排（只引用 Contracts、Core；M2 新增，见 04）
Plugins.*            每个领域一个插件项目
App.*                装配点：唯一同时知道 Core、Application 和全部插件的地方
```

| 规则 | 内容 |
|---|---|
| R1 | Contracts 只含接口与数据契约，不含业务逻辑，只依赖 `Microsoft.Extensions.DependencyInjection.Abstractions` |
| R2 | Core 引用 Contracts，**不引用任何具体插件** |
| R3 | 插件**只引用 Contracts** |
| R4 | 插件之间不互相引用程序集；只在清单里按 Id 声明依赖，运行时通过 DI 取得对方在 Contracts 中定义的接口 |
| R5 | App 负责装配：创建插件实例列表交给 `PluginHost` |
| R6 | Application 只引用 Contracts、Core，且不得出现任何 UI 类型（M2 新增，见 04） |

`ArchitectureTests` 用反射自动检查 R2、R3、R4 与 R6。

## 1.3 扩展点

| 扩展点 | 接口 | 状态 |
|---|---|---|
| 设备 | `IDeviceFactory` / `IDevice` / `IDeviceFactoryContribution` | 已定义（M2 WP2 增加贡献接口，外壳经 `GetActive<T>()` 取用） |
| 处理 | `IFrameProcessor` / `IProcessorContribution` | 已定义（闸门峰值检测为首个实现；波束合成、TFM 以后也是处理节点） |
| 视图 | `IViewContribution` | 已定义并与 WPF 外壳接入（`view.basic` 首批实现，细节见第 2 章） |
| 检测技术 | `ITechniquePlugin` | **预留**：PA 阶段有真实需求时再定 |
| 存储 / 报告 | — | **预留** |

M2 WP2 起，插件把能力以贡献接口暴露；`Application` 与外壳只通过 `PluginHost.GetActive<T>()`
枚举**运行中**插件的贡献，插件在 `Initialize/Start` 失败后其贡献不可见（规则 R11，见 04、07）。

## 1.4 清单与能力声明

每个插件提供 `PluginManifest`：

| 字段 | 含义 |
|---|---|
| `Id` / `Version` / `Kind` / `DisplayName` | 标识信息；`Id` 在宿主内唯一 |
| `MinContractsVersion` | 需要的最低契约版本；**主版本不同即拒绝装载** |
| `DependsOn` | 依赖的插件 Id 列表 |
| `HostRequires` | 对运行环境的要求（显存、特性标签，如 `gpu.cuda`） |
| `DeviceNeeds` | 对设备的要求（阵元数范围、接收通道范围、采集模式集合、带宽、同步） |
| `DeviceProvides` | 设备类插件声明的静态上限能力 |

**约束式声明**：写范围、集合、预算，不写死数字。例如"阵元数 `[16..4096]`"而不是"128"，这样 128 扩到 1024 以上时声明不用改。

**两阶段匹配**：

1. **装载期**：`HostRequires` 对照 `HostCapabilities`（宿主环境）。不满足 → 该插件 `Rejected`，其他插件照常运行。
2. **会话期**：`DeviceNeeds` 对照设备连接后上报的 `DeviceCapabilities`，由 `PluginHost.CheckSession` 给出匹配结果与不满足的原因，在采集开始前就能拒绝，而不是运行时才崩。

匹配语义：

| 字段 | 规则 |
|---|---|
| 范围（阵元数、接收通道） | 设备范围与需求范围**有交集**即满足 |
| 采集模式 / 探头拓扑 | 需求为"任一"（any-of）；需求为 None 表示不关心 |
| 带宽 | 设备提供值 ≥ 需求最小值 |
| 多板同步 | 需求为真时设备必须支持 |
| 标签 | 需求的标签必须全部存在 |

## 1.5 生命周期

```
Discovered ─► Registered ─► Initialized ─► Running ─► Stopped
     │             │              │            │
     └─────────────┴──────────────┴────────────┴─► Rejected（前置条件不满足，含依赖项失效的级联）
     └─────────────┴──────────────┴────────────┴─► Faulted（该插件自己的调用抛了异常）
```

- 启动顺序为依赖拓扑序（无依赖关系的插件保持传入顺序），停止与释放为逆序。
- `Register` 阶段插件把服务写入**临时容器**，成功后才合并到全局容器；`Register` 抛异常不会留下半截注册。
- `Rejected` = 前置条件不满足（契约版本、宿主能力、依赖缺失/失效/环）；`Faulted` = 该插件自己的 `Register / Initialize / Start / Stop` 抛了异常。两者都不影响无关插件。
- 依赖项变成 `Rejected` 或 `Faulted` 后，尚未走到下一阶段的依赖方一律 `Rejected`（原因里写明是哪个依赖），不会被初始化或启动。
- **已知限制**：已成功合并进容器的服务不会因为插件后来出故障而被移除。使用方应先看 `PluginHost.Statuses` 再依赖某个插件。
- 取消（`OperationCanceledException` 且令牌已取消）在 `Initialize / Start` 中向外传播，不当作插件故障。

## 1.6 通信：控制面与数据面

- **控制面**：DI 服务 + 接口调用（状态、参数、设备管理）。
- **数据面**：`AcquisitionFrame` 经 `Channel<T>` 流转，**大数据不走事件总线**；M2 WP4 起由唯一的 `FrameFanout` 再分发到判伤、记录、显示三条路径；M2 WP5 起显示路径按活动视图 `DisplayNeeds` 的并集只拷贝需要的通道，并做保峰值抽取与刷新节流。

帧所有权规则：

| 编号 | 规则 |
|---|---|
| F1 | 帧由 `AcquisitionFrame.Rent` 从内存池租出，引用计数初值为 1 |
| F2 | 从通道读到帧的一方拥有它，用完 `Dispose`；要交给第二个消费者先 `Retain` |
| F3 | 生产者写通道失败（取消、关闭）要自己 `Dispose`；通道丢帧通过 `itemDropped` 回调 `Dispose` |
| F4 | `Dispose` 之后不得继续持有 `Data` / `Span` |

显示路径可以丢旧帧（`DropOldest`，丢弃计数）；判伤与记录路径不允许无声丢帧，队列满按欠帧计数并告警（详见 05）。任何帧的去向只有"被处理"或"按原因丢弃计数"两种。

**帧泄漏计数**（07 §5）：`AcquisitionFrame` 维护进程级"在外帧数"；会话停止排空后非零即记为错误，测试断言其回到基线（M2 WP7）。

**参数版本**：`AcquisitionParameters.Version` 严格递增；每帧携带 `ParamVersion`；会话用参数历史（默认保留最近 16 版）按帧自带版本查参数处理，查不到记 `UnknownVersion` 并视为判伤欠帧；处理器遇到与所给参数不一致的帧直接抛异常。

## 1.7 数据契约与版本策略

- Contracts 用语义化版本（`ContractsInfo.Current`）。同主版本内只新增可选成员，不删除、不改语义；不兼容的改动新建类型。
- 帧形状为 `(Tx, Rx, Samples)` 三维；`AScan` 约定 `Tx = 1`，每个 `Rx` 通道是一条独立的脉冲回波。
- 帧头事实由设备填写：ADC 格式（`Adc`，幅度一律按 `FullScaleCounts` 换算，不再写死满量程）、帧标志（`Flags`：设备溢出/截断）、序号（同一设备同一次采集内严格递增、步长 1，06 §C4）。
- `AcquisitionParameters.ActiveReceiveChannels` 表示本次实际使用的接收通道数；界面与处理链读它，而不是从能力范围里猜（06 §C5）。
- 单位全部 SI（米、秒、赫兹）；通道与阵元编号内存用 `int`，协议用 `uint16`（预留 `uint32`）。
- 单帧只能放进一个缓冲区（不超过 `Array.MaxLength`）。1024 阵元以上的 FMC 单帧远超此限，必须分块或不采全矩阵，方案**待定**，现在会抛出明确的 `NotSupportedException`。

## 1.8 隔离与异常边界

- 内核在每个插件调用边界（`Register` / `Initialize` / `Start` / `Stop` / `Dispose`）捕获异常，标记该插件故障，不让它拖垮整体。
- 插件自己创建的后台线程或任务必须自己捕获异常，内核捕获不到。
- 真正的隔离（独立加载上下文、独立进程）**不在本阶段范围**。

## 1.9 动态加载的预留点

现在唯一"硬编码"的是 App 里的插件实例列表。将来改为"扫描目录 + `AssemblyLoadContext` 加载"时，只替换这一步；清单、依赖、生命周期、能力匹配都不变。前提是 R1–R4 一直守住（测试会盯着）。

## 1.10 起步最小集与暂不约定

**已固化**：程序集分层（R1–R5）、设备/处理/视图三类扩展点、清单与能力声明、生命周期、核心数据契约（`AcquisitionFrame`、`ProbeModel`、`AcquisitionParameters`、参数版本）。

**暂不约定**：动态加载与卸载、沙箱或独立进程、第三方 SDK、插件签名与授权、热更新。

## 1.11 待定项

| 项 | 依赖 |
|---|---|
| 并行接收通道数（128 路并行 / 复用 64 / 32 / 16 路） | 硬件方案 |
| FPGA 合成版本的负责人与时间；协议 v0.1 草案与法则下载格式 | 硬件团队 |
| 目标市场与适用标准（决定校准、记录、报告要求） | 业务 |
| `ITechniquePlugin` 接口 | PA 阶段 |
| 超大帧（1024 阵元 FMC）的分块方案 | 面阵阶段 |

## 1.12 当前实现状态

| 项 | 状态 |
|---|---|
| Contracts / Core / Application / 模拟板卡 / 闸门处理 / 视图插件 / CLI 装配点 / WPF 外壳 / 测试 | 已编写 |
| **编译与测试** | **已执行（2026-10-28）**：`dotnet build -warnaserror` 0 警告 0 错误；`dotnet test` 168 个测试全部通过（M2 WP1–WP7 + M3 回环套件）；`dotnet format --verify-no-changes` 通过 |
| NuGet 包版本（`Directory.Packages.props`） | 已首次还原验证：Microsoft.Extensions.* 8.0.x、xunit 2.9.2、Microsoft.NET.Test.Sdk 17.11.1 均可用 |
| WPF 外壳与视图贡献点 | 已实现（外壳可启动，`--selftest` 通过；约定见第 2 章） |
| M2 WP1：Application 项目、会话迁移、CLI/WPF 共用、会话测试 | 已完成（2026-10-28，见 03 §9） |
| M2 WP2：`GetActive<T>()` 与贡献接口、故障插件贡献不可见 | 已完成（2026-10-28，见 03 §9） |
| M2 WP3：ADC/满量程、帧标志、显示需求、实际通道数（契约 0.3） | 已完成（2026-10-28，见 03 §9） |
| M2 WP4：帧分发、丢帧计数、参数历史、序号断档检测 | 已完成（2026-10-28，见 03 §9；记录路径消费者留待 M6） |
| M2 WP5：显示取数请求、保峰值抽取、刷新节流 | 已完成（2026-10-28，见 03 §9；快照池化作为性能优化待实测） |
| M2 WP6：R7–R10、分析器、API 快照、verify 脚本 | 已完成（2026-10-28，见 03 §9 与 07 §9；R10/Q1 用测试级门禁，原因见 07 §9） |
| M2 WP7：插件契约套件、故障注入、帧泄漏计数、DP7/DP9 | 已完成（2026-10-28，见 03 §9/§10） |
| M3 起步：协议 v0.1 草案、SerialUdp 插件骨架、回环假板卡、设备契约套件通过 | 已编写（2026-10-28，见 docs/protocol/v0.1.md；真实串口/UDP 传输待硬件确认） |
| 真实设备插件（串口/UDP/PCIe）、协议定稿、聚焦法则、波束合成/TFM | 未开始 |

## 1.13 代码结构与命令

```
src/UltrasoundHost.Contracts                          契约
src/UltrasoundHost.Core                               插件宿主
src/UltrasoundHost.Application                        会话与采集编排（net8.0）
src/Plugins/UltrasoundHost.Plugins.Device.Simulator   模拟板卡（设备插件）
src/Plugins/UltrasoundHost.Plugins.Device.SerialUdp   串口+网口插件（回环已实现，真实传输待定）
src/Plugins/UltrasoundHost.Plugins.Processing.Basic   闸门峰值检测（处理插件）
src/Plugins/UltrasoundHost.Plugins.View.Basic         基础视图（视图插件）
src/UltrasoundHost.App.Cli                            命令行装配点（演示用）
src/UltrasoundHost.App.Wpf                            WPF 外壳（net8.0-windows）
tests/UltrasoundHost.Tests                            宿主、契约、插件与架构规则测试
scripts/verify.ps1                                    质量门禁：restore → build -warnaserror → test → format verify
docs/overview-for-beginners.md                        新人向架构与目标说明（先读这个）
docs/spec/01–08                                       详细规格与路线图
docs/protocol/v0.1.md                                 协议 v0.1 草案
docs/protocol/hardware-info-request.md                 板卡信息问卷（给硬件/固件，小白版）
docs/protocol/hardware-info-example.md                 板卡信息填写示例（虚构板卡）
```

```
dotnet build UltrasoundHost.sln
dotnet test  UltrasoundHost.sln
./scripts/verify.ps1
dotnet run --project src/UltrasoundHost.App.Cli -- --frames 30
dotnet run --project src/UltrasoundHost.App.Wpf
dotnet run --project src/UltrasoundHost.App.Wpf -- --selftest --report out/selftest.txt
```

没有桌面环境时用 `--selftest` 验证外壳链路（见第 2 章）。
