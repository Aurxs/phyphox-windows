# phyphox Windows 实验工作台

浏览器提供界面及麦克风、摄像头、扬声器入口；本地服务负责实验计算、存储和 BLE、USB 通信。

这是依据 phyphox 官方 Android 1.2.1 源码开发的 Windows 移植版，当前为**开发验证版**，不代表完整产品或设备兼容性已验收。双击 EXE 启动本地服务并自动打开浏览器；不依赖手机、账号或云服务。

[使用指南 PDF](user-guide/phyphox-Windows-使用指南.pdf) · [构建说明](docs/BUILD.md) · [兼容清单](docs/COMPATIBILITY.md)

## 运行便携包

1. 解压完整便携程序文件夹（内含 `start-phyphox.cmd` 和 EXE）到电脑或可写 U 盘，不要只复制 EXE。
2. 双击 `Phyphox.Server.exe`（也兼容 `start-phyphox.cmd`），任务栏会出现“phyphox 实验工作台”后端窗口，不显示命令行窗口。
3. 服务就绪后自动打开默认浏览器，地址为 `http://127.0.0.1:端口`，端口默认自动分配。后端窗口可最小化，并提供“打开实验页面”按钮和可复制的地址。
4. 在“实验库”点击“试试公式实验”，或搜索“公式计算工作台”，先熟悉输入与结果。官方硬件实验必须连接、配置对应设备才能运行。
5. 关闭浏览器后服务仍运行。完成后点击后端窗口的“停止服务并退出”或关闭该窗口，服务会停止并释放数据目录，再安全拔出 U 盘。

程序随包携带 .NET 10 运行时，不要求用户安装 .NET/SDK/Node，不安装系统服务、启动项或文件关联。需要电脑已有浏览器及相应设备驱动。网页资源均在包内，不使用 CDN。实验主动声明的 HTTP/MQTT 功能当然需要其指定的网络连接。

默认数据目录为 EXE 所在目录下的 `data/`，按相对位置查找资源，可以改变文件夹名称和盘符。U 盘只读、空间不足或被安全策略禁止执行时不能保证运行。USB 专用驱动的安装要求取决于具体设备，程序“免安装”不等于任意设备免驱动。

```powershell
.\Phyphox.Server.exe --port 37650
.\Phyphox.Server.exe --data-dir "D:\我的实验数据"
# 自动化验证可禁用窗口及浏览器；仅禁用自动打开浏览器可用 --no-browser。
.\Phyphox.Server.exe --headless --port 37650
```

默认仅监听本机。浏览器显示界面，并在用户明确授权后提供麦克风、普通相机及扬声器的媒体入口；采集得到的真实音频和图像交给服务分析和保存，播放内容由服务生成。BLE、串口、HID、网络通信、实验引擎与文件保存仍在本地服务执行，没有 Web Bluetooth/WebUSB/Web Serial 依赖。浏览器媒体需要 localhost 或 HTTPS、安全上下文及相应浏览器支持。

## 页面入口与日常操作

新版实验库按分类展示，可用“官方实验 / 入门与示例 / 我的实验”筛选或搜索。当前实验顶部提供开始、暂停/继续、清空确认和保存入口；“停止实验”位于“实验说明与更多操作”中。已有数据时切换实验会提示直接切换或保存后切换。

- **连接设备**：扫描并选择实际设备，按各自协议填写参数，再关联实验输入或输出。
- **数据与回放**：保存快照、确认后恢复，使用表单映射导入 CSV/TSV/WAV，或查看不驱动设备的独立回放。
- **音频与摄像头**：优先手动授权浏览器麦克风、相机与扬声器；Windows 原生媒体放在高级入口。进入页面不会自动打开或播放，页面顶部会显示正在使用的媒体来源。

普通数据导入表单的列号从 **1** 开始，高级 JSON 的索引仍从 **0** 开始。保存、恢复、导出及原版界面的对齐边界见 [新版界面使用说明](docs/USER-INTERFACE.md)；首次使用也可打开页面右上方的帮助。

## 已有功能

- `.phyphox` 1.20 解析、67 个官方实验资源、文件/ZIP 导入、用户实验删除与资源读取。
- 54 类分析模块、公式、数据容器、按源顺序计算、启停/暂停/清空及分组清空、时间映射。
- 分页视图、实时数值、图表、输入控件、深浅主题和中文/英文界面。
- Windows BLE、USB 串口/HID 实际适配代码及显式设备选择、配置、收发、实验输入/输出绑定；设备异常不会改用假数据。真实设备仍待验收。
- 浏览器真实 PCM / 图像到服务的媒体桥接，以及服务生成音频的浏览器播放入口；WASAPI 音频、MSMF 普通相机保留。普通图像不提供已校准物理曝光、光谱或深度；实际录放音、相机、浏览器差异与测量正确性待验收。
- HTTP GET/POST、MQTT/MQTTS，网络数据到实验容器、分析与记录的闭环。
- CSV/TSV/WAV 映射导入、测量快照保存/暂停恢复、输入与计算周期记录、独立数值回放、CSV ZIP/XLSX/状态文件导出。

“文件可打开”“数值测试通过”“真实设备可用”“交互等价”分别判断。原手机传感器、定位和深度相机不能凭普通电脑硬件自动提供；缺能力时会阻止启动。Android 源码分析见 [架构报告](docs/ANDROID-ARCHITECTURE.md)。详细边界在 [兼容清单](docs/COMPATIBILITY.md)，已执行检查在 [验证记录](docs/VALIDATION.md)。

## 数据在哪里

| 子目录 | 内容 |
|---|---|
| `data/experiments` | 用户导入的实验与附属资源 |
| `data/recordings` | `.active` 未封口记录、`.jsonl` 已封口记录 |
| `data/snapshots` | 具名暂停数据快照 |
| `data/imports` | 原始导入数据和映射回执 |
| `assets` | 固定版本官方实验及数值样例 |

回放是记录数据的重新计算，关闭硬件输出，也不替换当前实时会话。数据快照不是驱动/引擎内存检查点，恢复后需重新确认设备绑定。保存到原版 `.phyphox` 的是数据与时间状态，不承诺所有 Android 状态都能逐位互通。浏览器自身缓存与 Windows 系统日志可能保留，本项目不承诺宿主零痕迹。

## 源码与构建

### 一键打包 EXE

- **GitHub Actions**：将代码推送到 GitHub 后，打开 **Actions → Build Windows EXE → Run workflow**。完成后在该次运行的 **Artifacts** 下载 `phyphox-windows-win-x64`，解压下载文件，再解压其中的便携包 ZIP。推送 `v*` 标签也会自动打包；产物保留 30 天，不自动发布 Release。
- **Windows 本地**：安装下面指定的 .NET SDK 和 Node.js 后，双击项目根目录的 `build-exe.cmd`；也可在 PowerShell 执行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-exe.ps1`。

本地输出为 `artifacts/phyphox-windows-win-x64.zip` 和同名 `.sha256` 校验文件。解压完整 ZIP 后双击 `Phyphox.Server.exe`，无需另外安装 .NET。必须保留配套文件，不能只复制 EXE。打包需要联网下载构建依赖。

工具链固定于 `global.json` 的 .NET SDK 10.0.401；前端使用 Node.js 22 与 `web/package-lock.json`。Windows 发布目标为 x64，Windows API 基线 build 19041。当前 Windows ARM VM 验证使用 x64 仿真包，并未交付原生 ARM64 包；Windows 10 实机仍需验证。

```powershell
.\tools\publish-windows.ps1
```

输出为 `artifacts/win-x64`。构建机需要获取依赖；发布包运行不要求网络。详情见 [构建说明](docs/BUILD.md)、[Windows 验证方法](docs/WINDOWS-VALIDATION.md) 和 [开发与交付计划](docs/DEVELOPMENT-PLAN.md)。

本项目是独立移植，不是 phyphox 官方 Windows 发布。采用 GPL-3.0；修改来源与依赖许可见 `LICENSE`、`THIRD-PARTY-NOTICES.md`、`licenses/`。不应移除这些资料后重新分发。
