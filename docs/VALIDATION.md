# 已执行验证与未完成验收

日期：2026-09-18。以下“通过”只指对应测试范围，不表示完整产品验收。以下历史完整检查对应旧版 Windows 主服务 DLL，SHA256：`E95A071071F293BED3B01EAB796C0F7717447BE0B67DE3646668F7F17E42EB6D`，已与虚拟机报告核对一致。

## 本次用户界面更新

新增分类列表、实验顶部操作、保存弹窗、切换/清空/恢复/导入覆盖保护，设备与数据导入表单；补齐官方简体中文翻译与显示单位资源。 按后续明确需求新增浏览器麦克风、摄像头和扬声器桥接。最终 Windows 11 ARM / Edge 的 1920（三列）和 2560（四列）宽屏、图表滚轮与媒体页面无自动访问检查通过，见 [最终增量报告](validation/windows-ux-wide-validation.json)。宽屏与浏览器媒体检查对应服务 DLL SHA256：`58B04672C2CDFB16B46EBD8CAF504E187B02A40BF345501E9F0B982F001316A4`。当前前端 TypeScript/Vite 构建和原有 5 项显示测试通过。新版本对应的针对性验证见 [界面更新验证记录](validation/ux-validation.md)。下方 117 数值向量、完整解析语料、路径迁移等记录属于此前版本；本次没有无故重复全量验证，也不把历史结果表述成新包已经重跑。随后按用户反馈仅精简文案并新增具体传感器名称；这部分以官方 XML 名称验证和本机浏览器定向检查记录为准，未重复运行 Windows 全流程。

## 实际环境

开发机：macOS ARM64；.NET SDK10.0.401、运行时10.0.12、Node22。用户授权的Parallels虚拟机：Windows11 ARM、build26200、4逻辑CPU/8GB配置；服务进程架构X64、OS架构Arm64，因此是**x64仿真验证**。当前登录普通用户、未提升权限、PATH没有dotnet。报告中Windows NT 10.0.26200是Windows11内核版本号，不能误写成Windows10已测试。

## 已通过

| 范围 | 实际检查 | 证据和边界 |
|---|---|---|
| Windows发布 | win-x64自包含Release发布，零编译错误 | 编译不替代运行；许可目标路径冲突已修复 |
| Windows便携运行 | 自包含启动、本地网页静态入口、67官方条目、公式2→4/3→9、启停、三种导出、日志回放与快照保存/恢复 | [最终虚拟机报告](validation/windows-arm-final-validation.json)；导出在Windows脚本只查非空，内容另由HTTP专项核对 |
| 路径便携 | 将同一包从C盘临时目录移到含中文/空格的目录，经临时SUBST盘符P启动；从Windows目录作为当前工作目录启动；默认数据与快照写到程序旁 | [迁移路径报告](validation/windows-arm-relocated-validation.json)；SUBST映射随后已删除。这不是实物U盘/不同物理电脑验收 |
| 原生相机DLL | 无FFmpeg独立插件仍能加载OpenCvSharpExtern并返回4.13.0 | 上述报告cameraBackend.deviceOpened=false；未打开摄像头 |
| 官方数值 | 117个官方黄金向量全部按原容差通过；未更改期望值 | [Windows数值报告](validation/core-final-numerical.txt)；macOS也通过。包含输入重复消费、事件恢复、FFT等专项。无硬件值被伪造 |
| 官方解析 | 127接受、39拒绝、1个声明1.21跳过，0预期分类不符 | [Windows解析报告](validation/core-final-corpus.txt)；仅证明解析分类 |
| 服务集成 | 令牌/Origin/Host、请求幂等、库/公式/控制、CSV与XLSX内容、state init、快照列表/保存/恢复、CSV映射导入、记录重算、真实本地HTTP接收→分析→回放、设备缺失阻断、ZIP路径拒绝 | [本机HTTP记录](validation/service-integration.txt)；使用实际HTTP fixture，未冒充外部仪器 |
| 网络协议 | 30项HTTP/MQTT/MQTTS检查，包含本机真实broker、TLS、拒绝错误证书/主机名/凭据、断线重订阅和消息错误隔离 | 见src/Phyphox.Network/README.md；macOS loopback，非外部broker/Windows协议验收 |
| 设备/媒体纯计算 | 转换、帧、输出模板/触发/keep、BLE官方声明与已公布数字基线、partial ZIP/CRC；音频PCM/混合；相机ROI/颜色/曝光数学与映射 | 只验证代码与合成/重建协议输入，**不是实机采样记录**；各项目tests及状态文件保留说明 |
| 网页 | TypeScript/Vite构建；5项图表专项；真实本地浏览器验证参数/启停/下载、快照/回放/导入、图表PNG/色阶/柱图/history；检查媒体页没有自动硬件请求 | web/output/playwright/verification.md与PNG；开发机浏览器验证，Windows浏览器验证另记 |

## Windows 浏览器补充验证

在同一Windows11 ARM虚拟机，已安装的Edge 153.0.4234.32使用全新临时浏览器目录访问实际最终包服务，React DOM确认公式输入2、结果4.0000，截图已目视检查。测试后仅关闭测试启动的Edge与服务，没有安装浏览器/Node或打开媒体设备。见[Edge报告](validation/windows-edge-validation.json)、[实际截图](validation/windows-edge-formula.png)。这不替代Windows浏览器全部控件、设备和性能验收。

## 排障记录

- 首次Windows最终验证的长EncodedCommand经虚拟机执行接口超时，未启动测试，未记为通过；改为共享目录中的脚本文件和短启动命令后执行成功。
- 最终包第一轮功能检查本身全部通过；外层脚本以Windows默认编码读取UTF8 JSON导致中文损坏并报解析错误。已明确使用UTF8读取，复用已完成报告，仅继续尚未执行的路径迁移检查。
- 这些是验证工具/打包问题，均保留与软件运行结果的区分。

## 未执行或尚未完成

Windows10 x64、Windows11 x64物理电脑、原生ARM64构建、实物U盘与两台电脑换盘符、离线断网实测、BLE/USB真实设备的扫描/连接/采集/控制/拔插/重连、实际录音/播放/相机/深度/GNSS、Windows浏览器完整交互、4C8G长稳/内存/刷新/采样时延/丢样、断电与磁盘空间不足、完整格式/视图组合和全部实验逐项验收、原生依赖完整公开分发许可审计。

GitHub Actions工作流只是配置文件，未在GitHub触发。未执行Android构建或真机基准。当前开发包不能描述为“所有原版实验兼容”“全部设备已支持”或“完整产品通过验收”。后续门槛在COMPATIBILITY与DEVELOPMENT-PLAN中逐项跟踪。
