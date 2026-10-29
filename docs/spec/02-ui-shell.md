# 第 2 章　WPF 外壳与视图贡献

状态：**已确认**（2026-10-26）　适用范围：上位机软件 v0.x

第 1 章把视图扩展点定义为"与 UI 技术无关的描述符"，并把 UI 贡献点细节列为待定。本章补全这部分，
约定外壳的职责边界与验证方式。只约定改起来代价大的东西；其余等有真实需求再定。

## 2.1 已确认的决策

| 决策 | 结论 |
|---|---|
| 外壳技术 | WPF（.NET 8），仅 Windows。外壳项目 `UltrasoundHost.App.Wpf` 为 `net8.0-windows`；Contracts、Core 与插件保持 `net8.0` |
| 分层位置 | 外壳属于 App 层：引用 Core 与全部插件，是组合根。Contracts/Core/插件不引用 WPF，也不认识任何 UI 类型 |
| 视图贡献 | 插件只实现 `IViewContribution`，返回 `ViewDescriptor`；外壳把 `ViewKind` 映射为控件。**插件不创建窗口、不引用 WPF** |
| 未知 ViewKind | 外壳显示占位提示，不阻止插件装载。插件可以声明当前外壳不认识的种类，未来外壳可以认识更多 |
| 视图可用性 | `ViewDescriptor.Schemes`（None = 任意）与设备上报的 `DeviceCapabilities.Schemes` 按 any-of 匹配（与 1.4 同规则）；不匹配的页签禁用并说明原因 |
| 界面语言 | 操作者可见的界面文字（按钮、页签、表头、状态提示、操作错误提示）为中文；插件 Id、ViewKind 等标识与 SI 单位保留原样。底层库产生的参数校验/能力匹配诊断文字暂不翻译，留待需要时统一。暂不引入多语言资源框架，等有出口需求再做 |
| 插件显示 | 界面用清单里的 `DisplayName`（中文）作为插件名称，`Id` 作为技术标识单列，方便对照日志 |
| 采集编排 | 外壳持有 Application 项目的 `ShellSession`：插件宿主 + 设备 + `FrameFanout`（判伤/显示路径、丢帧计数、参数历史）。窗口只做渲染与交互，不直接碰通道与帧（M2 WP1/WP4） |
| 线程模型 | 帧在后台任务里分发、处理并拷贝为 `FrameSnapshot`；UI 线程只读快照与计数，不持有 `AcquisitionFrame`。显示路径按视图需求节流（当前内置 A 扫为 ≤30 帧/秒），UI 以 ~50 ms 定时器取最新快照，不逐帧编组到 UI 线程 |
| 通道策略 | 显示路径用 `DropOldest`，被丢弃的帧经 `itemDropped` 回调 `Dispose` 并计入 `DisplayDropped`；刷新节流跳过的帧同样计数；判伤路径用 `TryWrite`，队列满计入欠帧并把会话标为"检测不完整"；参数改版后帧按自带版本处理，不再作为过期帧丢弃（05） |
| 显示取数 | 外壳把活动视图的 `DisplayNeeds` 并集交给会话（`DisplayRequest`），只拷贝请求的通道；超过横向点数时用最小值/最大值成对抽取（保峰值，不跳采样）；`Channels=Selected` 的视图由外壳通过 `SelectDisplayChannel` 告知当前通道（M2 WP5） |
| 无窗口验证 | 外壳支持 `--selftest --report <path>`：走同一个 `ShellSession` 代码路径，在无桌面环境也能验证"宿主 → 设备 → 会话检查 → 采集 → 处理 → 快照" |
| 暂不约定 | MVVM 框架、主题与样式规范、停靠布局及持久化、多设备同时连接、视图插件的动态加载、UI 自动化测试 |

## 2.2 视图贡献点

`ViewDescriptor` 的字段语义（定义在 Contracts，见 1.3）：

| 字段 | 约定 |
|---|---|
| `Id` | 视图标识，用于外壳定位页签；内置插件由测试保证它唯一 |
| `Title` | 页签标题，展示用 |
| `ViewKind` | 自由字符串。内置常量集中在 `ViewKinds`（`AScan`、`GateTable`），避免插件与外壳各写一份字符串 |
| `Schemes` | 该视图能显示哪些采集模式；None = 任意。外壳按 any-of 规则启用/禁用页签 |
| `Needs` | 显示取数需求：通道（全部/当前选中/指定列表）、横向最大点数、最大刷新率。默认全通道、不抽取、不限刷新（06 §C3；M2 WP5 已实现并生效） |

实现方式：插件在自己的 `Register` 里注册服务供依赖方使用；外壳收集视图时走
`PluginHost.GetActive<IViewContribution>()`，只取**运行中**插件的贡献（规则 R11），不再从 DI 容器
解析贡献接口。视图只是描述符，**不携带数据访问接口**：数据由外壳的采集会话以 `FrameSnapshot` 推送。
本章新增的 `ViewKinds` 与 `ViewDescriptor.Matches` 只增加成员，因此契约版本从 0.1.0 升到 0.2.0；
M2 WP2 起契约升到 0.3.0（贡献接口，见 06）。

当前内置渲染器：

| ViewKind | 渲染 | 数据 |
|---|---|---|
| `AScan` | 波形图：单通道样本、门区间、阈值线 | 最新 `FrameSnapshot` 的所选 Rx 通道 |
| `GateTable` | 表格：帧号、参数版本、通道、触发、幅度、声程、峰值时间 | 最新帧的 `GateResult` 列表 |

## 2.3 外壳职责与顺序

启动（`App.OnStartup`）：

1. 组装插件列表（唯一硬编码处，同 1.9 的预留点）；
2. `PluginHost.Build → InitializeAsync → StartAsync`；
3. 从容器收集 `IViewContribution`，按描述符建立页签；
4. 显示主窗口。插件被 `Rejected/Faulted` 时仍进窗口，状态与原因可见。

操作顺序：

```
Open（打开模拟板卡，可配通道数）
  → CheckSession（逐插件显示满足情况；不满足则禁用 Start）
  → Start（应用首版参数，开始采集）
  → Apply（Revise 提升参数版本，在途帧按各自版本处理）
  → Stop（停设备、结束分发与消费、释放排队帧并输出最终计数）
```

关闭顺序（窗口关闭 → `App.OnExit`）：停采集 → 释放设备 → 释放宿主（逆序 Stop/Dispose 插件）→
释放容器。任何一步抛出都只记录在该插件的状态或状态栏里，不阻断其余步骤。

失败展示：

- 插件 `Rejected/Faulted`：左侧列表显示状态与原因；
- 会话检查不满足：逐插件列出未满足项，`Start` 保持禁用；
- 参数不合法：状态栏显示具体校验信息，参数不会被应用。

## 2.4 目录与命令

```
src/UltrasoundHost.Application                    会话与采集编排（net8.0，M2 WP1）
src/UltrasoundHost.App.Wpf                        WPF 外壳（net8.0-windows）
src/Plugins/UltrasoundHost.Plugins.View.Basic     基础视图插件（AScan / GateTable）
```

```
dotnet run --project src/UltrasoundHost.App.Wpf
dotnet run --project src/UltrasoundHost.App.Wpf -- --selftest --report out/selftest.txt
```

`--selftest` 不创建窗口，输出检查项文本并以退出码表示结果（0 = 通过）。

## 2.5 当前实现状态

| 项 | 状态 |
|---|---|
| `App.Wpf` 外壳（插件状态、会话检查、开关采集、增益改版、AScan 波形、闸门表格） | 已实现；`--selftest` 通过，窗口可启动 |
| 会话与采集编排（`ShellSession`、`FrameSnapshot`、`SessionOptions`） | 已迁入 `UltrasoundHost.Application`（M2 WP1），CLI 与 WPF 共用；WP5 起显示快照按 `DisplayRequest` 裁剪 |
| 显示取数（`DisplayRequest`、保峰值抽取、刷新节流） | 已实现（M2 WP5）；内置 A 扫视图声明 `Selected` 通道/2000 点/30 Hz |
| `view.basic` 视图插件（两个描述符） | 已实现 |
| 自动测试 | 契约/插件/架构规则 + 会话行为（版本切换、检查失败拒绝启动、设备异常、停止/释放）+ 分发丢帧计数与参数历史不变量由 `UltrasoundHost.Tests` 覆盖；外壳冒烟由 `--selftest` 覆盖 |
| 真实设备插件（串口/UDP/PCIe）、协议 v0.1、聚焦法则、波束合成/TFM | 未开始 |
| MVVM、布局持久化、多设备、UI 自动化测试 | 暂不约定 |
