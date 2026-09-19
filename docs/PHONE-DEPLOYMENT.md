# 手机传感器：完全离线接入与维护

本功能按用户明确要求采用**完全离线**架构：没有域名、云配对服务、外部网页、STUN/TURN 或互联网下载依赖。Windows 本机服务托管手机页面和配对信令；手机与电脑连接同一局域网。此文替代此前“联网配对”部署方案。`Phyphox.Pairing` 是进程内共享房间/信令类库，随 Windows 工作台运行，不包含独立服务器入口，也无需公网部署。

## 浏览器首次连接与 HTTPS

普通 `http://电脑IP` 不是安全上下文；本功能由 Windows 自动提供本地 HTTPS。默认流程不要求手机安装 CA：用户扫码打开 HTTPS 页面，如出现证书提示，核对电脑显示的 IP/端口后，在浏览器提供选项时明确继续，再点击传感器授权。

“证书提示已继续”“安全上下文/API 可见”“真实采集成功”应分别记录。继续访问不保证每个浏览器都开放采集，但也不能一概认定必须安装系统 CA。用户已报告 Android Chrome / iPhone Safari 均可申请权限；逐项真实硬件数据与实验全链路仍待验收，不承诺所有版本的首次扫码都没有提示。

依据：[WebKit 2019 年移除 getUserMedia 证书专项检查](https://bugs.webkit.org/show_bug.cgi?id=205493)；当前 [WebKit 安全上下文实现](https://github.com/WebKit/WebKit/blob/main/Source/WebCore/dom/Document.cpp)及[来源可信性实现](https://github.com/WebKit/WebKit/blob/main/Source/WebCore/page/SecurityOrigin.cpp)；当前 [Chromium 权限来源检查](https://github.com/chromium/chromium/blob/main/components/permissions/permission_context_base.cc)。源码只支持这条验证路线，不等于全部已发布手机版本的通过记录。

每次安装生成独立根 CA，默认有效两年。启用接入时根据所选私有 IPv4 地址签发含该 IP SAN 的服务器证书，有效期最多 30 天。程序不会自动安装电脑/手机系统证书，也不会自动修改 Windows 防火墙。

## 用户流程

1. 在电脑“设置 → 手机传感器”启用接入，选择手机能够访问的局域网地址。默认关闭，只监听选定私有 IPv4。
2. 生成并扫描本地 HTTPS 配对二维码。端口动态分配，以当前界面为准。
3. 如浏览器显示证书提示，核对 IP/端口，在提供继续选项时明确继续。凭证经 URL fragment 传递，页面读取后清除。
4. 手机点击连接，Windows 确认接入；手机再按按钮授权所需输入。
5. 在实验中绑定手机输入并开始。验证版只绑定一个输入；以真实数据到达判断能力。
6. 停用会停止相关采集、撤销配对并关闭两个局域网监听端口。

## 独立传感器检查

HTTP 和 HTTPS 两个当前端口均提供 `/sensor-check.html`，无需配对、不接入实验、不上传采集数据。先比较 `isSecureContext` 和接口暴露，再主动请求权限，检查真实音频采样、相机预览及运动事件有效分量。HTTP 是限制对照组；不得将接口存在或授权通过当成真实采集成功。记录设备型号、OS/浏览器版本、证书提示操作及每项结果。用户已反馈两类手机浏览器可申请权限；真实数据及全链路记录仍待补，详见[来源验证记录](validation/phone-browser-origin.md)。

## 可选兼容办法：手动信任 CA

仅在浏览器不允许继续或继续后仍阻止采集时考虑此办法。打开 HTTP 证书引导页，对照电脑界面的完整 SHA-256 指纹，再安装公开根证书。HTTP 引导页本身不建立信任。证书安装不是默认操作；组织策略禁止安装时也不能据此直接判断默认浏览器路线一定失败。

Android / Chrome：下载 `phyphox-phone-ca.cer`，在系统设置搜索“安装证书”或“CA 证书”。各厂商菜单不同，参见 [Google Pixel 证书说明](https://support.google.com/pixelphone/answer/2844832?hl=en)。完成后重新扫码。

iPhone / Safari：下载 `phyphox-phone-ca.mobileconfig`；在“设置 → 通用 → VPN 与设备管理”安装；再到“设置 → 通用 → 关于本机 → 证书信任设置”开启对应 CA 完全信任。参见 [Apple 手动证书信任说明](https://support.apple.com/102390)。描述文件只含公共根证书，不含 VPN、代理、Wi-Fi、MDM 或私钥。

同一根 CA 通常无需重复安装；IP 改变后重新启用网关签发叶证书。根更换或失效时，已安装信任的手机需重新核对处理。安装时间可能超过二维码有效期，完成后生成新配对码。不再使用或信任该电脑时可移除曾安装的 CA。

## 服务暴露范围

本机管理服务继续只监听 loopback，保留原有 token、Host 和 Origin 防护。启用/停用、网卡选择、租约、实验数据接收等管理请求仅来自本机工作台。电脑与本地配对 Hub 的 WebSocket 需要本机一次性 ticket，加入凭据无法换取桌面管理权限。

两个独立 LAN 监听由一个受控网关提供：

| 监听 | 允许路径 | 用途 |
|---|---|---|
| HTTP，引导端口 | `/setup`、`/setup.js`、`/phone-ca.cer`、`/phone-ca.mobileconfig`、`/sensor-check.html`、`/sensor-check.js` | 可选证书说明、公共证书及独立诊断 |
| HTTPS，采集端口 | `/phone.html`、`/assets/*`、两个 PCM worklet、`/signal`、`/health`、`/sensor-check.html`、`/sensor-check.js` | 手机采集页面和受限信令 |

HTTP 仅额外提供独立诊断页，不提供正式手机采集页面、WebSocket、实验或文件 API；HTTPS 也没有电脑管理接口。请求 Host 必须精确匹配当前选择的 IP:端口；手机 WebSocket Origin 必须精确匹配该 HTTPS origin。局域网监听不会代理任意 URL，也不会公开整个数据目录。静态页面不包含第三方脚本或在线字体。CSP 只允许自身和当前精确 WSS origin，不能通过本地页面转向云端。

信令采用一次性 256-bit 凭证、120 秒二维码有效期、60 秒电脑确认窗口。字段、消息大小、房间数、速率和连接数均有界；配对凭据绑定连接，不支持旧二维码复用或跨 socket 恢复。只转发受限 SDP/ICE，不转发传感器数据。信令及 SDP 只交换 host ICE 候选，不配置 STUN/TURN 服务器。连接后的路径检查接受成功候选对的两端分别为 host 或 prflx，拒绝 srflx、relay 与未知类型，避免把连接检查产生的 peer-reflexive 直连误判为中继；类型定义见 [W3C RTCIceCandidateType](https://www.w3.org/TR/webrtc/#dom-rtcicecandidatetype)。候选类型不保证设备属于同一个物理 Wi-Fi；虚拟网卡/VPN 也可能提供地址，使用者应选择正确网卡。

## 证书与数据目录

根私钥存储于本机数据目录下 `phone-certificates/authority.json`，其中包含加密 PFX 与随机密码。密码与 PFX 放在同一受限文件中，因此**不是独立密码保护，也不能声称可防御已获取该文件的攻击者**。保护依赖电脑用户账户及文件权限：Windows 目录 ACL 仅当前用户完全控制；Unix 目录权限 700、文件 600。绝不把此文件发送给手机、提交仓库或复制到普通共享目录。手机只能下载公共 DER 根证书。

初次写入采用临时文件、落盘、原子移动，已有身份不会被覆盖。损坏、过期或无私钥时显示错误并保留原文件，由电脑所有者停用功能后明确维护。维护更换根证书会使所有原手机信任失效。程序不自动向系统证书存储写入 CA。服务器叶证书仅存在内存，启用时签发，停用释放。

## 构建和聚焦验证

先在 `web` 构建手机/电脑两个入口，再构建已有 Windows 服务。正常启动无需 `--phone-origin`、域名或配对服务器配置。验证命令：

```sh
dotnet run --project tests/Phyphox.Pairing.Tests
dotnet run --project tests/Phyphox.PhoneGateway.Tests
dotnet run --project tests/Phyphox.PhoneLease.Tests
```

证书网关测试只使用临时目录和测试进程的 CustomRootTrust，不安装系统信任。覆盖根身份复用、IP SAN、离线证书链、HTTP 与管理 API 隔离、HTTPS 页面、错误根存储不覆盖；存在私有 IPv4 网卡时运行真实监听测试，否则明确报告跳过。

仍须用真实 Windows + Android Chrome / iPhone Safari 完成扫码打开、证书提示及继续后的行为、安全上下文、扫码配对、传感器权限、设备直连、停止、后台/锁屏和网络变化验证。桌面浏览器测试或软件单元测试不能替代实机验收。不建议关闭整个防火墙；若 Windows 提示网络访问，仅为可信私有网络和当前程序作用户明确允许的配置。没有可用私有网卡、访客 Wi-Fi 隔离、企业网络阻断时，需要先解决网络/设备策略问题。

P0 数据限制：相机时间目前按服务接收/实验时间标记，尚未实现手机采样时钟映射；JSON 数据通道和单输入验证版不能冒充完整多传感器最终协议。
