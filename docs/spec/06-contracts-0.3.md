# 06 契约 0.3

状态：**已确认**（2026-10-28）　日期：2026-10-28
对应工作包：WP2、WP3
原则：只增不改；新增成员都有默认值，使现有插件源码兼容；任何变更同步升 `ContractsInfo.Current` 并更新公共 API 快照。

注意：以下按契约的**语义**描述；落笔前请先核对现有类型里是否已有同名或近似字段，以最小改动为准。类型形状是建议，不是定稿。

## C1 ADC 格式与满量程

问题：32768 目前在 `GatePeakProcessor`、`AscanPlot`、模拟器里各写一份。真实板卡常见 12/14/16 位，写死会让幅度百分比错误，而且错得不明显。

变更：
- `DeviceCapabilities` 增加 `Adc`（`AdcFormat`）：位数 `Bits`、是否有符号 `Signed`、对齐方式 `RightAligned`（默认右对齐、补符号位）。
- 约定：帧样本类型仍为 `short`；满量程计数 `FullScaleCounts = 2^(Bits−1)`（有符号）。16 位时即 32768，与现状一致。
- `AcquisitionFrame` 带上它产生时的 `Adc`（帧头事实，处理器不需要再去问设备）。
- 所有处理、视图使用 `FullScaleCounts`；仓库内禁止再出现字面量 32768 的幅度换算（07 里用测试/搜索守住）。
- 默认值：`Bits = 16`，旧设备代码不改也行为不变。

测试：模拟器配成 12 位时，同一回波的幅度百分比不变（幅度由计数/满量程得出）。

## C2 贡献机制与 `GetActive<T>()`（决策 D1，默认方案 B）

问题：插件在 `Register` 成功后若在 `Initialize`/`Start` 出错，状态变 Faulted，但它已合并进容器的服务仍可被解析，外壳会继续使用它的处理器或视图。

变更：
- 契约新增贡献接口（插件对象自己实现）：
  - `IViewContribution`（已有）：贡献视图描述。
  - `IProcessorContribution`：`IFrameProcessor CreateProcessor()`。
  - `IDeviceFactoryContribution`：`IDeviceFactory Factory { get; }`。
- 宿主新增 `IReadOnlyList<T> GetActive<T>()`：返回状态为 Running 且实现了 `T` 的插件，按拓扑启动顺序。
- 规则 R11：外壳与 Application 枚举插件能力只能走 `GetActive<T>()`。
- DI 容器的用途收窄为“插件之间的服务依赖”。依赖者若依赖的插件 Faulted，则依赖者按现有规则被 Rejected，不会用到出故障的服务，所以 DI 路径本身是安全的。
- 迁移：`SimulatorPlugin`、`GatePeakPlugin`、`BasicViewPlugin` 实现对应贡献接口；它们原有的 DI 注册可保留给依赖者，外壳不再读取。

备选方案 A（宿主给服务打插件归属标记，提供“仅运行中插件的服务”解析器）：要覆盖按实现类型、工厂委托、多重注册等形式，代码多、易漏，故不作默认。

测试：
- Faulted 插件的贡献不出现在 `GetActive<T>()`；
- 外壳在“处理插件 Faulted”时拒绝启动采集并给出原因；
- 视图列表不含 Faulted 插件的视图。

## C3 显示需求 `DisplayNeeds`

变更：`ViewDescriptor` 增加可选的 `Needs`：
- `Channels`：`All`｜`Selected`（当前选中的一路）｜`Explicit(list)`；
- `MaxPointsAcross`：横向最多点数（null 表示不抽取）；
- `MaxRefreshHz`：最大刷新率（null 表示不限）。

默认值等于现状（全部通道、不抽取、不限）。使用见 05 §7。

## C4 帧标志与序号语义

变更：
- `AcquisitionFrame.Sequence` 语义写入契约：同一设备同一次采集内严格递增、步长 1，由设备（或其协议层）赋值。
- 新增 `FrameFlags`：`None`、`DeviceOverflow`（设备侧 FIFO 溢出，本帧之前有数据丢失）、`Truncated`（本帧数据不完整）。
- `FrameFlags.Truncated` 的帧判伤路径视同欠帧处理（计入 `Dropped[Inspection]` 并告警）。

理由：协议 v0.1 一定会有序号和溢出标志，先在契约里占位，协议与契约就不会互相等。

## C5 实际接收通道数

问题：能力声明里的 `ReceiveChannels` 是范围；`MainWindow.xaml.cs` 取 `.Min` 作为“当前通道数”，范围一变就错。

变更：`AcquisitionParameters` 增加 `ActiveReceiveChannels`（若已有等价字段则复用），`ValidateAgainst` 校验其在设备能力范围内；界面和会话使用它，不再从能力范围里推。默认值 1，与现状一致。

## C6 `ContractsInfo` 与测试

- `ContractsInfo.Current`：0.2.0 → 0.3.0。
- 现有 `PluginHostTests` 里“要求更高契约版本应被拒绝”的数据（现为 `0.3.0`）会变成**当前版本**，必须改成大于当前的值（如 `0.4.0`），否则该测试会误失败或失去意义。建议改为由 `ContractsInfo.Current` 计算出下一个次版本，以后不必再手改。
- 建立公共 API 快照基线（07 §3）。

## 兼容性小结

| 变更 | 对现有插件源码 | 对已编译插件 |
|---|---|---|
| C1 ADC | 兼容（有默认值） | 重编译即可 |
| C2 贡献接口 | 需让三个现有插件实现接口（本阶段一起改） | 同上 |
| C3 显示需求 | 兼容 | 兼容 |
| C4 帧标志 | 兼容 | 兼容 |
| C5 通道数 | 兼容 | 兼容 |

本阶段没有已发布的第三方插件，兼容性压力很小；保持“只增不改”是为了养成习惯，并让契约快照检查有意义。
