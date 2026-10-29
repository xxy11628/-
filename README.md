# UltrasoundHost（超声探伤/成像上位机）

一套用 .NET 8 写的超声上位机：**模块化单体 + 插件架构**。电脑通过串口/网口控制超声板卡，接收回波数据，做闸门峰值判伤、A 扫显示和丢帧计数；没有板卡时用模拟器或回环假板卡开发。

- 当前状态：M2（契约 0.3、数据通路、显示取数、质量门禁、插件契约套件）已完成；M3（协议 v0.1 + 真实设备）进行中。
- 质量门禁：`dotnet build -warnaserror` 0 警告，**168 个测试**通过，`dotnet format --verify-no-changes` 通过。

## 先读什么

| 你是 | 先读 |
|---|---|
| 第一次接触这个项目 | [新人向架构与目标说明书](docs/overview-for-beginners.md) |
| 要写插件 | [01 插件约定](docs/spec/01-plugin-conventions.md) |
| 要改界面 | [02 WPF 外壳与视图贡献](docs/spec/02-ui-shell.md) |
| 要理解数据面/丢帧 | [04 目标架构](docs/spec/04-target-architecture.md)、[05 数据通路与丢帧计数](docs/spec/05-data-path-and-loss-accounting.md) |
| 要接板卡/协议 | [协议 v0.1 草案](docs/protocol/v0.1.md)、[板卡信息问卷](docs/protocol/hardware-info-request.md) |
| 要看下一步计划 | [08 路线图](docs/spec/08-roadmap.md) |

## 快速开始

前置：Windows + .NET 8 SDK（WPF 外壳仅 Windows；Contracts/Core/Application/CLI 为 `net8.0`）。

```powershell
dotnet build UltrasoundHost.sln
dotnet test  UltrasoundHost.sln

# 无界面跑 30 帧
dotnet run --project src/UltrasoundHost.App.Cli -- --frames 30

# WPF 界面（中文）
dotnet run --project src/UltrasoundHost.App.Wpf

# 无桌面环境自检
dotnet run --project src/UltrasoundHost.App.Wpf -- --selftest --report out/selftest.txt

# 一键质量门禁
./scripts/verify.ps1
```

## 目录

```
src/UltrasoundHost.Contracts        契约：公共接口与数据类型
src/UltrasoundHost.Core             插件宿主：装配、生命周期、故障隔离
src/UltrasoundHost.Application      会话、帧分发、丢帧计数、参数历史、显示取数
src/UltrasoundHost.App.Wpf          WPF 中文界面（组合根）
src/UltrasoundHost.App.Cli          命令行版本（组合根）
src/Plugins/                         设备/处理/视图插件（只依赖 Contracts）
tests/UltrasoundHost.Tests           单元、架构、契约套件与协议测试
docs/                                规格、协议、新人文档
scripts/verify.ps1                   质量门禁脚本
```

## 设计要点（一句话版）

- 依赖单向：`Contracts ← Core ← Application ← App.*`；插件只依赖 Contracts，只有 App.* 能引用插件。
- 数据面：设备产帧 → `FrameFanout` 分发到判伤/记录/显示三条路；显示可丢帧但必须计数，判伤/记录不允许无声丢帧。
- 帧池化 + 引用计数，借了必须还；停止时检查“未归还帧数 = 0”。
- 参数有版本，帧自带版本；处理用帧的版本，不猜当前参数。
- 契约只增不改（版本 + 公共 API 快照）；换板卡只换设备插件。

更完整的解释见 [新人向架构与目标说明书](docs/overview-for-beginners.md)。
