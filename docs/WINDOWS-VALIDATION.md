# Windows 验证环境

## 适用环境

- 推荐：Windows 11 x64 实机，4 核 / 8 GB。用真实 BLE、USB、音频和摄像头完成对应验收。
- Windows 10 x64：构建使用 Windows 10 build 19041 API 基线；仍需按目标版本和驱动单独验证。API 目标不等于已获得系统兼容认证。
- Windows 11 ARM：可运行 x64 包进行仿真兼容验证，报告必须保留 OS 和进程架构，不能当成 x64 实机。
- GitHub Actions：已提供 Windows Server runner 工作流，仅用于构建、核心测试与便携启动。工作流文件的存在不等于已经在 GitHub 执行。

## 无开发环境测试

解压完整发布目录，运行 `start-phyphox.cmd`，按照终端地址打开浏览器。程序携带 .NET 运行时。驱动、浏览器、操作系统媒体组件不由自包含 .NET 发布替代。

运行 `powershell -NoProfile -ExecutionPolicy Bypass -File test-windows.ps1 -ProgramDirectory . -ReportPath validation.json` 可进行聚焦的启动、实验库、公式、控制和导出检查。此调用只对该 PowerShell 进程设置执行策略，不修改系统策略。

测试脚本只操作新建的临时数据目录，在结束时终止自己启动的服务进程；这项检查不等于优雅退出或掉电恢复验证。

## 本任务已经提供的虚拟机

用户授权使用 Parallels Windows 11 ARM 虚拟机。检查到 4 个逻辑处理器、8 GB 内存，Parallels Tools 已安装。以当前登录用户运行，不使用 SYSTEM 身份代替普通用户验证。具体是否提升权限、是否预装 dotnet、实际测试结果以机器生成的报告为准。

设备自动共享情况不代表已经执行设备测试。麦克风、摄像头实际采集和 BLE/USB 测量单独记录。

## 已执行的路径检查

最终包在用户VM的普通账户（未提升权限、无dotnet PATH）运行。随后移动到中文/空格目录并通过临时P盘符启动，默认data目录正确随EXE定位。SUBST盘符已清理；这不等于真实U盘或第二台电脑测试。具体SHA256与结果见VALIDATION.md。Edge实际页面渲染也已单独记录。
