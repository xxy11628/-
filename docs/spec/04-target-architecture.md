# 04 目标架构

状态：**已确认**（2026-10-28）　日期：2026-10-28
说明：本文是“M2 完成后”的架构图景，同时给出更远阶段的位置（标注“预留”），使本阶段的改动不挡后路。

## 1. 架构模式

微内核（插件）架构，落地为**模块化单体 + 端口适配器**：内核只负责装配、生命周期、能力匹配；业务能力都是插件；插件只依赖契约。编译期装配，运行时加载接口预留。

## 2. 项目与引用方向

```
                      ┌────────────────────────────┐
                      │  App.Wpf (net8.0-windows)  │  组合根 + 渲染
                      │  App.Cli (net8.0)          │  组合根 + 无界面运行
                      └──────┬───────────┬─────────┘
                             │           │ 只有 App.* 可以引用 Plugins.*
              ┌──────────────▼──┐   ┌────▼────────────────────────┐
              │  Application    │   │ Plugins.Device.*            │
              │  (net8.0, 新增) │   │ Plugins.Processing.*        │
              │  会话/采集泵/   │   │ Plugins.View.*              │
              │  分发/计数      │   │ （预留）Beamformer.* Storage.*│
              └───────┬─────────┘   │  Technique.* Report.*       │
                      │             └──────────┬──────────────────┘
              ┌───────▼─────────┐              │ 只引用 Contracts
              │  Core           │              │
              │  宿主/匹配/生命周期│              │
              └───────┬─────────┘              │
                      └─────────┬──────────────┘
                        ┌───────▼────────┐
                        │  Contracts     │  无项目引用，仅 DI.Abstractions
                        └────────────────┘
```

| 项目 | 目标框架 | 允许引用 | 职责 |
|---|---|---|---|
| Contracts | net8.0 | 仅 `Microsoft.Extensions.DependencyInjection.Abstractions` | 全部公共接口与数据类型 |
| Core | net8.0 | Contracts | 清单校验、拓扑启动、能力匹配、生命周期、故障隔离 |
| Application（新） | net8.0 | Contracts、Core | 会话、采集泵、分发、丢帧计数、参数历史、显示取数 |
| Plugins.* | net8.0 | Contracts | 各领域能力；不得用 UI 框架 |
| App.Cli | net8.0 | Application、Contracts、Core、Plugins.* | 组合根（无界面） |
| App.Wpf | net8.0-windows | 同上 | 组合根 + WPF 渲染 |
| Tests | net8.0 | 以上全部 | 单元、架构、契约套件 |

规则：

| 编号 | 规则 | 状态 |
|---|---|---|
| R1 | Contracts 无项目引用，只依赖 DI.Abstractions | 已有 |
| R2 | Core 只引用 Contracts | 已有 |
| R3 | 插件只引用 Contracts | 已有 |
| R4 | 插件之间不互相引用，只在清单里按 Id 声明依赖 | 已有 |
| R5 | 组合根（App.*）负责装配 | 已有 |
| R6 | Application 只引用 Contracts、Core，且不得出现任何 UI 类型 | 新增 |
| R7 | 只有 App.* 与 Tests 可以引用 Plugins.* | 新增 |
| R8 | 插件项目不得启用 `UseWPF`/`UseWindowsForms`，目标框架不得带 `-windows` | 新增 |
| R9 | 插件 Id 以其 Kind 小写加点作前缀（`device.`、`processing.`、`view.`…） | 新增（现有三个已符合） |
| R10 | 契约公共 API 变更必须更新快照，并同步 `ContractsInfo` 版本 | 新增 |
| R11 | 外壳与 Application 枚举插件能力只能通过 `GetActive<T>()`，不得直接遍历 DI 容器 | 新增 |

强制手段见 [07](07-constraints-and-quality-gates.md)。

## 3. 运行时组成

```
 设备插件(IDevice)                 Application                           界面
 ┌────────────────┐   channel   ┌───────────────────────────────┐
 │ 采集线程/回调   │──帧(池化)──►│ FrameFanout（单读者）           │
 └────────────────┘             │  ├─► 判伤路径 队列(不得无声丢)──► 处理器 → 判伤结果/告警
                                │  ├─► 记录路径 队列(不得无声丢)──► 存储插件（预留）
                                │  └─► 显示路径 队列(可丢,计数) ──► 快照(按视图需求) ──► UI 定时取最新
                                │  LossCounters / ParameterHistory
                                └───────────────────────────────┘
```

要点：

- 控制面（参数、命令、状态）与数据面（帧）分开，参数带版本，帧自带版本。
- 帧只在一处被分发，各路径通过 `Retain()` 共享同一块池化内存，最后一个释放者归还（规则 F1–F4 不变）。
- UI 线程只读取已拷贝的快照，不接触池化帧。
- 判伤结果由判伤路径产生，不依赖显示是否跟得上。

## 4. Application 项目（WP1 细则）

迁入与新增的类型：

| 类型 | 来源 | 说明 |
|---|---|---|
| `SessionController`（暂沿用 `ShellSession` 名亦可） | 由 `ShellSession` 迁入 | 会话入口：初始化宿主、打开设备、会话检查、启动/停止采集、改参数 |
| `SessionOptions` | 新增 | 设备类型、设备设置、是否限速、队列容量；替代写死的 `"simulator"` 与 `"paced"` |
| `DeviceCatalog` | 新增 | 通过 `GetActive<IDeviceFactoryContribution>()` 列出可用设备类型 |
| `FrameFanout`、`LossCounters`、`ParameterHistory` | 新增（WP4） | 见 05 |
| `DisplayRequest`、`DisplaySnapshot` | 新增（WP5） | 见 05 §7 |

迁移步骤（建议逐步提交，每步都能编译通过）：

1. 新建 `UltrasoundHost.Application`，加入解决方案，先建空项目并加架构测试 R6。
2. 把 `ShellSession.cs`、`FrameSnapshot.cs` 原样移入，命名空间改为 `UltrasoundHost.Application`；WPF 项目引用 Application，窗口只改 `using`。
3. 让 CLI 删除自己的采集循环，改用 `SessionController`。
4. 把 `SelfTest` 的逻辑改写为 xunit 测试（模拟器 + 闸门插件 + 视图插件），`--selftest` 保留为调用同一组装逻辑的薄入口。
5. 补会话测试（见 03 §6 第 3 条）。
6. 再做 WP2 起的行为修改，避免“搬家”和“改行为”混在同一次提交。

## 5. 插件种类与位置

| 种类 | 现有 | 预留 | 说明 |
|---|---|---|---|
| Device | Simulator | SerialUdp（M3）、PCIe、Replay（M6） | 回放设备让记录数据走同一条处理链，是测试与复核的基础 |
| Processing | Basic（闸门峰值） | 滤波、包络、DAC 补偿 | 帧 → 处理结果 |
| Beamformer | — | HardwarePassthrough、HostCpu、HostGpu（M4/M5） | 帧 → 波束线 `BeamformedLine`，共用下游 |
| View | Basic（A 扫、闸门表） | S 扫、C 扫、TFM 图 | 通过 `IViewContribution` 贡献 |
| Storage | — | 记录/回放（M6） | 记录路径的消费者 |
| Technique | — | 检测工艺（M6） | 把设备、处理、视图、判据组织成一种检测方法 |
| Report | — | 报告（M7） | 读取记录与判伤结果 |

## 6. 线程模型

- 设备插件自行管理采集线程，只通过 `ChannelWriter` 写入；不得在回调里做处理。
- Application 有一个分发任务，每条路径各有一个消费任务；全部后台任务由会话创建、由会话在停止时等待。
- UI 线程只调用会话的非阻塞读取（最新快照、计数、状态），退出时的等待必须有超时。
- 插件不得自行 `new Thread`；需要后台工作时使用宿主提供的方式（07 的禁用 API 清单强制）。

## 7. 为更远阶段预留（本阶段不实现）

- **多板卡/多通道**：`DeviceCapabilities` 已有多板同步声明；会话层允许 N 个设备，本阶段只保证接口不排斥。
- **1024+ 阵元/面阵**：`ProbeModel` 是阵元位置列表；帧形状是（Tx, Rx, Samples）；超过单数组上限的帧走分块，`Rent` 目前对此抛 `NotSupportedException`，M6 实现分块。
- **动态加载**：清单与 `PluginHost` 不假设插件来自本进程的引用；将来加 `PluginLoader`（独立加载上下文）即可，规则 R3、R4 已保证插件不依赖对方程序集。
- **FPGA 与主机两版波束合成**：同一接口 `IBeamformer`，主机 CPU 版含逐位对齐 FPGA 的模式，用同一批黄金向量验证（M4）。
