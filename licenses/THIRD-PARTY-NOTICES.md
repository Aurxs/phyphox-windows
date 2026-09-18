# Third-party notices / 第三方组件与许可清单

清点日期：2026-09-18。依据当前源码项目引用、NuGet 缓存内元数据与许可、前端 `package-lock.json` 和实际 `node_modules`、官方固定版本源码。此文件记录归属和证据，不表示全部再分发兼容性已经审定。许可证原文不得用本摘要替代。

## 1. 运行时与应用依赖

| 组件 | 固定版本或证据 | 许可与归属证据 | 本目录资料 |
|---|---|---|---|
| phyphox Android 派生实现与实验资源 | Android `45fa55a0727653ccce439b86acb69c00cf435436`；资源沿用实际子模块文件 | GNU GPL v3 文本；README 明示 Copyright 2016 Dr. Sebastian Staacks, 2nd Institute of Physics, RWTH Aachen University | `phyphox-android-GPL3-LICENSE.txt`、`phyphox-experiments-GPL-3.0.txt` |
| .NET Core 自包含运行时 | Microsoft.NETCore.App.Runtime.win-x64 10.0.12 | 包标注 MIT；Microsoft；另含各自条款的第三方代码 | `dotnet-runtime-LICENSE.txt`、`dotnet-runtime-third-party-LICENSE.txt` |
| ASP.NET Core 自包含运行时 | Microsoft.AspNetCore.App.Runtime.win-x64 10.0.12 | 包标注 MIT；Microsoft/.NET Foundation 等原始归属见原文 | `aspnetcore-runtime-LICENSE.txt`、`aspnetcore-runtime-third-party-LICENSE.txt` |
| .NET apphost | Microsoft.NETCore.App.Host.win-x64 10.0.12 | MIT 与包内第三方通知；生成的启动 EXE 也属于发布内容 | `dotnet-apphost-LICENSE.txt`、`dotnet-apphost-third-party-LICENSE.txt` |
| Windows SDK .NET 投影 | Microsoft.Windows.SDK.NET.Ref 10.0.19041.57；RuntimeList 内 SDK.NET FileVersion 10.0.19041.55 | 包的许可链接指向 Microsoft Windows SDK Windows 10 条款，不能自动归为 MIT | `WindowsSDK-LICENSE.rtf`、对应 readable 文本、`package-metadata/WindowsSDK-RuntimeList.xml` |
| WinRT.Runtime | 上述包携带，AssemblyVersion 2.2.0.0 / FileVersion 2.2.0.48161 | CsWinRT 上游 MIT，Microsoft Corporation；包级 SDK 条款适用范围仍需核对 | `CsWinRT-LICENSE.txt`；来源为官方仓库当前 LICENSE，未声称对应该二进制的精确源码 tag |
| React、react-dom | **19.3.0**（锁文件实际解析版本，不是 package.json 下限 19.1.1） | MIT；Meta Platforms, Inc. and affiliates | `React-LICENSE.txt`、`ReactDOM-LICENSE.txt` |
| scheduler | **0.28.0**，react-dom 生产依赖 | MIT；Meta Platforms, Inc. and affiliates | `Scheduler-LICENSE.txt` |
| NAudio.Wasapi、NAudio.Core | 2.2.1 | MIT；包元数据 © Mark Heath 2023，官方许可文本自身为 Copyright 2020 Mark Heath，均原样保留 | `NAudio-LICENSE.txt`，对应 nuspec |
| MQTTnet | 4.3.7.1207，仓库提交 `740d605fd2c25922fad6ea60e78e8370356e95dd` | MIT；.NET Foundation and Contributors | `MQTTnet-LICENSE.txt`，对应 nuspec |
| StbImageSharp | 2.30.16，包提交 `125af70cb557033f2c46aec8e82eaaf72ac49817` | `Unlicense OR MIT`；发行选择 Unlicense；包作者 StbImageSharpTeam，不补写不存在的版权年份 | `StbImageSharp.LICENSE.txt`，对应 nuspec |
| OpenCvSharp4、OpenCvSharp4.runtime.win | 4.13.0.20260627，提交 `b161e7e012f5101f6d5dc68a835c59db6cc88b18` | Apache-2.0；作者 shimat，包版权字段 Copyright 2008–2026 | `OpenCvSharp-LICENSE.txt`、两个 nuspec |
| OpenCV / opencv_contrib | Native build info: OpenCV 4.13.0，源码短提交 fe38fc6 / d99ad2a | OpenCV 主许可 Apache-2.0；其中第三方源文件保留原许可，不能整体覆盖为 Apache | `OpenCV-LICENSE.txt`、`OpenCvSharp-NATIVE-BUILD-INFO.txt`及下节 |

`Microsoft.Windows.SDK.NET.Ref` 虽名为 Ref，最终 Windows 程序会携带其 SDK.NET 与 WinRT.Runtime DLL，不能把整个包一律列为纯构建依赖。`.NET` 的 `THIRD-PARTY-NOTICES` 原文已完整复制，不以“MIT”一行替代。

## 2. OpenCV 原生 DLL 内的静态组件

下表版本来自所用 `OpenCvSharpExtern.dll` 的构建信息或二进制版本字符串。已保留原件及其版权人文字，不从项目名称推测归属。

| 组件 | 已识别版本 | 已收集官方资料 | 来源固定点 |
|---|---|---|---|
| zlib | 1.3.1 | `zlib-1.3.1-LICENSE.txt` | madler/zlib `v1.3.1` |
| libjpeg-turbo / IJG | 3.1.3 | `libjpeg-turbo-3.1.3-LICENSE.md` 与**完整** `README.ijg`；IJG、BSD-3-Clause 及 SIMD 的说明均保留 | libjpeg-turbo/libjpeg-turbo `3.1.3` |
| libpng | 1.6.55 | `libpng-1.6.55-LICENSE.txt`（包括历史通知） | pnggroup/libpng `v1.6.55` |
| libtiff | 4.7.1 | `libtiff-4.7.1-LICENSE.md` | libtiff/libtiff 官方 GitLab `v4.7.1` |
| libwebp / libsharpyuv | 1.6.0 | `libwebp-1.6.0-COPYING.txt`、`PATENTS.txt` | webmproject/libwebp `v1.6.0` |
| OpenJPEG | 2.5.3 | `OpenJPEG-2.5.3-LICENSE.txt` | uclouvain/openjpeg `v2.5.3` |
| OpenEXR / IlmImf | 2.3.0 | `OpenEXR-2.3.0-LICENSE.txt` | AcademySoftwareFoundation/openexr `v2.3.0` |
| Protocol Buffers | 3.19.1 | `protobuf-3.19.1-LICENSE.txt` | protocolbuffers/protobuf `v3.19.1` |
| Intel ITT notify | OpenCV 4.13.0 携带源文件 | `ittnotify-OpenCV-4.13.0-NOTICE.txt` 保留 Copyright 2005–2019 Intel 与双许可 SPDX；选择 BSD-3-Clause，另附 Intel 官方许可正文 | 固定 OpenCV tag 的 header；正文来自 Intel ittapi 官方 LICENSES，下载哈希在来源清单 |
| Intel IPPICV / IPP IW / IPP HAL | 2022.2.0 | **已补齐**归档内 EULA.rtf、第三方程序清单、ICV/IW readme 与可读转换，不再仅标为“缺文件” | opencv/opencv_3rdparty `c934a2a15a6df020446ac3dfa07e3acf72b63a8f` 内 Windows x64 归档，MD5 **7c0973976ab0716bc33f03a76a50017f** 与官方 CMake 一致 |

IPP 归档 EULA 是 **Intel Simplified Software License (October 2022)**，不是 MIT/Apache；其二进制使用、不得修改及再分发通知条件须按原文核对。归档 `third-party-programs.txt` 对该 IPP 版本声明无另列第三方程序，这不代表整个 OpenCV DLL 没有第三方组件。

**正式对外发布门槛：以下审计尚未完成，当前仅为用户本机开发验证包，不得写成已全部合规。**

- IPP 的 EULA 已取得，但其与本项目 GPL 派生组合发行的适用性、IW 源码包装层与该二进制 EULA 的对应授权范围仍需确认；不据“NuGet 包标 Apache”推定解决。
- Native build info 明示 `Non-free algorithms: YES`，并包含 contrib 模块。当前仅使用图像/相机功能不等于二进制中其他代码不再需要审计。正式发布可考虑重建最小 OpenCV：关闭 IPP、FFmpeg、nonfree 及不用模块，再验证 MSMF/图像能力。
- 已收集主要可识别静态依赖的顶层许可；仍需对构建时的 vcpkg 补丁、各源文件例外、MSVC 静态运行时代码与完整链接清单逐项核对。现有包没有提供完整静态组件 SBOM/NOTICE，不能宣称已穷尽。
- SDK.NET / WinRT 运行时 DLL 的具体再分发权应按 SDK 包条款及所选文件核对；保存了官方 SDK 条款，但未以 MIT 覆盖整个 targeting pack。
- 项目源文件现有 `GPL-3.0-only` 与部分 `GPL-3.0-or-later` 标识需在源码交付前统一核对上游授予范围；本清单不擅自扩张上游授权。

## 3. 独立 FFmpeg 插件：已从当前便携包排除

NuGet 原生包带 `opencv_videoio_ffmpeg4130_64.dll`。其字符串表明 FFmpeg **4.4.6**，各 libav 组件标注 **LGPL 2.1 or later**；已保留 OpenCV 4.13.0 官方 `FFmpeg-OpenCV-README.txt` 和 LGPL 正文以记录获取的包，而非把它归类为 Apache。

当前相机服务显式使用 `VideoCapture(index, VideoCaptureAPIs.MSMF)`；OpenCV 固定版本 `cap.cpp` 只进入指定后端；所用 `OpenCvSharpExtern.dll` 的 PE 导入表不直接导入 FFmpeg 插件。OpenCV 官方 readme 也明确说明 Windows 插件是运行时加载的可选视频编解码后端，可以排除而保留其他后端。

当前打包脚本和实际验证包已排除该独立 DLL。Windows 11 ARM 虚拟机中的 x64 进程已在无该插件时通过 native-backend 加载并返回 OpenCV 4.13.0；这只证明 DLL 加载，不等于 MSMF 真机采集通过。若以后恢复 FFmpeg 视频能力，必须重新列入分发清单并落实对应版本的源码/构建与 LGPL 义务；不能仅重新拷回 DLL。

## 4. 构建期与系统组件，和运行交付分开

- Node.js、npm、TypeScript、Vite、Babel、esbuild、Rollup、类型声明等用于构建；网页运行只需要构建后的 JS/CSS。锁文件中的 dev 条目另列 `BUILD-ONLY-NPM-DEPENDENCIES.json`。**若分发 node_modules、SDK 或构建工具本体，必须另行携带其许可，不能使用本产品运行时清单代替。**
- SDK 10.0.401、targeting/reference packs 用于构建。缓存中的 Microsoft.WindowsDesktop.App.Runtime 10.0.12 不代表本应用最终需要该运行时；已保留其 LICENSE 供核对，但应以最终 deps/runtimeconfig 和 DLL 清单确认，不能把下载过的包一律称为发布依赖。
- OpenCvSharp nuspec 声明 System.Memory 4.6.3；当前 net10 解析资产未单独解析该 NuGet 包，框架提供同名 API。最终发布如出现独立包版本，应补其包内许可证，不能只据 nuspec 推断实际附带文件。
- Windows 自带 Media Foundation、WASAPI、HID、蓝牙与系统 DLL 由操作系统提供，当前程序不复制这些系统组件；设备厂商驱动也不包含在本许可证目录。
- “phyphox”“RWTH Aachen University”名称及相关标志的商标约束仍存在；GPL 源码许可不等于获得官方背书或商标授权。

## 5. 来源、完整性与交付复核

本目录 `package-metadata/` 保留实际 nuspec 与生产 npm package.json；`COPY-SOURCES.json` 保存本地拷贝来源与 SHA-256；`UPSTREAM-SOURCES.json` 保存上游固定版本 URL、归档来源与下载文本哈希。RTF 的 readable 文本仅为阅读副本，原 RTF 保留为正式来源。

最终打包应：保留本目录；核对实际 zip/deps 文件与本表；确认 FFmpeg 排除结果；把本项目对应源码、构建步骤、上游版本及修改说明一起交付。未完成的静态组件及授权兼容审计必须留在发行说明，不把“文件已收齐”表述为“许可审计全部通过”。
