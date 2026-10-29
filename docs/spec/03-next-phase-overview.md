# 03 下一阶段总览（M2：夯实）

状态：**已确认**（2026-10-28）　日期：2026-10-28
前置文档：[01 插件约定](01-plugin-conventions.md)、[02 界面外壳](02-ui-shell.md)
本组文档：03 总览｜[04 目标架构](04-target-architecture.md)｜[05 数据通路与丢帧计数](05-data-path-and-loss-accounting.md)｜[06 契约 0.3](06-contracts-0.3.md)｜[07 约束与质量门禁](07-constraints-and-quality-gates.md)｜[08 路线图](08-roadmap.md)

## 1. 现状

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | 解决方案骨架、契约、内核、插件约定（01） | 完成 |
| M1 | 模拟板卡 → 闸门处理 → WPF 外壳（02），73 个测试通过（以 01 的 §1.12 记录为准） | 完成 |
| **M2** | **夯实：在接真实板卡之前，把数据通路正确性、插件隔离、可测试性变成可执行的约束** | **待验收（WP1–WP7 完成，2026-10-28；见 §10）** |
| M3 起 | 协议、真设备、相控阵、FMC/TFM、记录回放、产品化 | 见 [08](08-roadmap.md) |

## 2. 本阶段目标

一句话：**以后每加一个插件、每改一处契约，错误都由构建和测试自动指出，而不是靠人记住规则。**

具体可检查的目标：

1. 外壳只使用状态为“运行中”的插件所提供的能力（出故障的插件不再被误用）。
2. 判伤路径不会无声丢帧：任何丢弃都有原因、有计数，判伤路径一旦欠帧要有明确告警。
3. 外壳核心逻辑（会话、采集泵）进入 `net8.0` 项目，被普通单元测试覆盖，CLI 与 WPF 共用同一份代码。
4. 满量程、ADC 位数等设备事实进入契约，不再在三处写死 32768。
5. 契约“只增不改”和依赖方向由工具强制，而不是约定。
6. 有一套可复用的“插件契约测试”，以后的串口/UDP 设备、波束合成插件直接继承。

## 3. 本阶段不做（范围护栏）

- 不做真实设备、不定协议字节格式（协议 v0.1 在 M3，但本阶段要把契约里它依赖的事实先留好，见 06）。
- 不做相控阵、聚焦法则、波束合成、FMC/TFM。
- 不做运行时动态加载与隔离（保持编译期装配，接口继续预留）。
- 不做记录/回放的文件格式、报告、校准。
- 不换 UI 框架，不重做界面；只改界面取数方式（WP5）。

## 4. 保留 / 修改 / 新增

**保留（不动，除非发现缺陷）**
- 分层与规则 R1–R5、清单（Manifest）、能力声明与 `CapabilityMatcher`、两阶段匹配。
- 插件生命周期（Discovered … Faulted）及“先注册到临时容器再合并”的做法。
- 帧池化与引用计数规则 F1–F4、参数版本化。
- 模拟器与闸门插件、`BasicViewPlugin`、WPF 薄外壳、`--selftest`（保留为冒烟测试入口）。
- 契约版本策略：0.x 阶段每次变更升次版本，只增不改。

**修改**
- `ShellSession` 迁出 WPF 项目，进入新建的 `UltrasoundHost.Application`（WP1）。
- 外壳取用插件能力的方式：由“从 DI 容器直接取”改为“宿主只返回运行中插件的贡献”（WP2）。
- 参数变更时的旧版本帧：由“直接丢弃”改为“按帧自带的版本取参数处理”（WP4）。
- `FrameSnapshot`：由“复制所有通道”改为“只复制视图声明需要的部分，并做保峰值抽取”（WP5）。
- 设备类型、是否限速等写死项改为会话选项（WP1）。

**新增**
- `UltrasoundHost.Application` 项目；`FrameFanout`（分发）、`LossCounters`、`ParameterHistory`。
- 契约 0.3（06）。
- 架构规则 R6–R11、分析器、公共 API 快照、`verify` 脚本（07）。
- 插件契约测试套件与模拟器故障注入（07）。

## 5. 工作包

| 编号 | 内容 | 依赖 | 细则 | 粗略工作量* |
|---|---|---|---|---|
| WP1 | 建 Application 项目，迁移 `ShellSession`，CLI 与 WPF 共用，补会话测试 | — | 04 §4 | 1–2 天 |
| WP2 | 贡献机制：`PluginHost.GetActive<T>()` 与贡献接口 | WP1 | 06 §C2 | 1 天 |
| WP3 | 契约 0.3（ADC 格式、帧标志、显示需求、实际通道数） | WP2 | 06 | 1–2 天 |
| WP4 | 分发、丢帧计数、参数历史 | WP3 | 05 | 3–4 天 |
| WP5 | 视图取数请求、保峰值抽取、快照池化 | WP4 | 05 §7 | 2 天 |
| WP6 | 架构规则、分析器、API 快照、verify 脚本 | WP1 可并行 | 07 | 2 天 |
| WP7 | 插件契约测试套件、帧泄漏计数、故障注入 | WP3 | 07 §4 | 2–3 天 |

\* 合计约 12–16 人天，是按一到两人团队的粗估，未经实际度量；做完 WP1 后应据实际速度修订。

建议顺序：WP1 → WP2 → WP3 → （WP4 → WP5）与（WP6、WP7）两条线并行。

## 6. 验收标准（M2 完成的定义）

1. `verify` 脚本一条命令通过：`-warnaserror` 编译、全部测试、格式检查。
2. 架构测试覆盖 R1–R11，并各有一条“故意违反会失败”的反例验证（至少在评审时手工验证一次）。
3. 会话测试覆盖：参数版本切换、停止顺序、会话检查失败拒绝启动、设备异常传播、插件故障后其能力不可见。
4. 慢消费者场景：显示路径丢帧且计数；判伤路径欠帧时产生告警且计数；两种场景下“收到 = 处理 + 丢弃”恒成立，停止后无未归还帧。
5. 契约公共 API 快照已建立，`ContractsInfo.Current = 0.3.0`，改动契约而不更新快照会编译失败。
6. 模拟器通过插件契约测试套件；套件可被新设备插件通过继承一个基类复用。
7. 01、02 文档同步更新，新增规则在文档中有编号和强制手段。

## 7. 待拍板的决策

| 编号 | 问题 | 本文档默认采用 | 影响 |
|---|---|---|---|
| D1 | 外壳如何只取到健康插件的贡献 | 方案 B：贡献由插件对象自己实现，宿主提供 `GetActive<T>()`（06 §C2） | WP2 |
| D2 | 满量程/ADC 位数是否现在进契约 | 现在进（06 §C1） | WP3 |
| D3 | 并行接收通道数（128/64/32/16） | 仍未定；本阶段不依赖，M3 前必须定 | 08 |
| D4 | 目标市场与适用标准 | 仍未定；本阶段不依赖，M6 前必须定 | 08 |
| D5 | FPGA 波束合成由谁做、何时 | 仍未定；M4 前必须定 | 08 |
| D6 | 默认采样率、PRF | 沿用模拟器值，待真实板卡约束 | M3 |

若你对 D1、D2 选别的方案，改 06 与 WP2、WP3 即可，其余文档不受影响。

> 2026-10-28 已确认：D1 采用方案 B，D2 现在进契约；本文与 04–08 一并确认，WP1 开工。

## 9. 执行记录

| 日期 | 工作包 | 结果 |
|---|---|---|
| 2026-10-28 | WP1 | 新建 `UltrasoundHost.Application`（net8.0，只引用 Contracts/Core）；`ShellSession`、`FrameSnapshot`、`SessionOptions` 迁入；WPF 外壳改为引用 Application，`--selftest` 保留为薄入口；CLI 删除自带采集循环、改用同一会话；新增 8 个会话测试与 R6 架构测试。`dotnet build` 0 警告 0 错误，82 个测试通过。WP2 才落地的“故障插件能力不可见”、R7–R11、分析器与 API 快照不在本次范围。 |
| 2026-10-28 | WP2 | 契约升到 0.3.0：新增 `IProcessorContribution`、`IDeviceFactoryContribution`（06 §C2）；`PluginHost.GetActive<T>()` 只返回 Running 插件；Application 的处理器、视图、设备工厂全部改走 `GetActive`；三个内置插件实现贡献接口；新增 GetActive 测试、故障处理器/故障视图不可见测试与 R11 架构测试；版本反例测试改为按当前版本推导。`dotnet build` 0 警告 0 错误，88 个测试通过。 |
| 2026-10-28 | WP3 | 契约 0.3 完成：`AdcFormat` + 帧 `Adc`、`FrameFlags`、`ViewDescriptor.Needs`（`DisplayNeeds`）、`AcquisitionParameters.ActiveReceiveChannels`；处理器/视图改用 `Adc.FullScaleCounts`，模拟器支持 12/14/16 位并携带帧头事实；新增 ADC/帧头/显示需求/实际通道数测试与 Q5 字面量扫描测试。`dotnet build` 0 警告 0 错误，97 个测试通过。 |
| 2026-10-28 | WP4 | 数据通路拆分：`FrameFanout`（唯一读者、序号断档/溢出/重复检测）、判伤与显示两条路径、`LossCounters`（Received / SequenceGaps / DeviceOverflow / SequenceViolations / 每路径 Offered/Processed/Dropped/UnknownVersion / InspectionIncomplete）、`ParameterHistory`（默认 16 版，先登记后应用）；会话停止按 05 §8 顺序排空并计数；外壳显示中文计数与“检测不完整”；新增分发丢帧、断档、重复序号、未知版本等测试（共 103）。记录路径的消费者按 05 预留给 M6 存储插件。 |
| 2026-10-28 | WP5 | 显示取数：`DisplayRequest` 取活动视图 `DisplayNeeds` 并集；显示消费只拷贝请求的通道，按最小/最大值成对做保峰值抽取（不跳采样），并按 `MaxRefreshHz` 节流（跳过的帧计入显示丢帧）；`FrameSnapshot` 携带实际通道号、原始采样时间与记录窗口，A 扫按快照时间绘制；外壳 `SelectDisplayChannel` 驱动 `Selected` 模式。新增请求并集/选择性拷贝/保峰值/节流相关测试（DP8，共 108）。快照缓冲池化未做，当前为不可变拷贝 + 30 Hz 级节流（05 §7 允许不可变拷贝保证 UI 稳定），列入后续性能优化。 |
| 2026-10-28 | WP6 | 质量门禁：新增 R7（只有 App.*/Tests 引用插件）、R8（插件无 UI 框架/无 -windows）、R9（插件 Id 前缀与 Kind 一致且唯一）、项目归类检查；Q2 用 `BannedApiAnalyzers` 3.3.4 + `BannedSymbols.txt` 在库项目生效（禁止 `Console`/`.Result`/`.Wait()`/`GetResult()`/`Thread.Sleep`/`new Thread`）；Q1 用源码检查保证库内 `await` 均带 `ConfigureAwait(false)`（可用分析器包没有 CA2007）；R10 用 `ApiSnapshotTests` 的契约 API 快照 + 版本行检查（离线环境无法生成 PublicApiAnalyzers 快照，改用等价的 B 级测试，见 07 §9）；新增 `scripts/verify.ps1`。本地验证：`build -warnaserror` 0 警告、114 个测试通过、`dotnet format --verify-no-changes` 通过。 |
| 2026-10-28 | WP7 | 插件契约测试套件（`PluginContractTestsBase` PG1–PG4、`DeviceContractTestsBase` DC1–DC9、`ProcessorContractTestsBase` PC1–PC4、`ViewContractTestsBase` VC1–VC3，新插件继承即可复用）；模拟器故障注入（序号断档、溢出标志、未知参数版本、写延迟、注入异常，可从 `SessionOptions.DeviceSettings` 打开）；`AcquisitionFrame` 帧泄漏计数（进程级在外帧数，会话停止非零记错误）；测试关闭并行保证计数稳定；DP7（设备异常、处理器异常）与 DP9（100 次启停无泄漏）通过。共 147 个测试。 |