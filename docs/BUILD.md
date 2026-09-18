# 可复现构建

## 固定工具与来源

- .NET SDK 10.0.401，global.json固定；自包含运行时10.0.12。
- Node.js 22；npm使用package-lock.json，不手工漂移依赖版本。
- NuGet主要包均在csproj固定版本。Windows发布显式传入`RuntimeIdentifier=win-x64`，确保BLE目标和原生相机包纳入。
- 官方资料固定提交见IMPLEMENTATION.md。官方docs测试语料不嵌入发布包；测试时在相邻`official-reference/phyphox-docs`准备固定提交。

## Windows构建机

安装SDK和Node后，在项目根执行：

```powershell
.\tools\publish-windows.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-windows.ps1 -ProgramDirectory .\artifacts\win-x64 -ReportPath .\artifacts\windows-check.json
```

发布脚本只生成文件夹，不注册服务、不安装驱动。运行包使用`start-phyphox.cmd`。SDK只用于构建，不是最终用户的依赖。

## 跨平台开发与定向检查

```sh
cd web && npm ci && npm test && npm run build
cd ..
dotnet build src/Phyphox.Server -m:1 -nr:false -p:UseSharedCompilation=false
dotnet run --project tests/Phyphox.Core.Tests -- ../official-reference/phyphox-docs/corpus/analysis/vectors
dotnet run --project tests/Phyphox.Core.Tests -- --corpus ../official-reference/phyphox-docs/corpus
dotnet run --project tests/Phyphox.Network.Tests -- ../official-reference/phyphox-docs
```

只执行与本次修改相关检查；设备/媒体/存储专门项目位于tests。没有实机时不把这些测试当驱动验收。

交叉发布：

```sh
dotnet publish src/Phyphox.Server -c Release -r win-x64 --self-contained true -p:RuntimeIdentifier=win-x64 -m:1 -nr:false -p:UseSharedCompilation=false -o artifacts/win-x64
```

还需按publish-windows.ps1复制启动脚本、许可和文档并生成manifest。相机仅使用MSMF，应移除发布目录中的可选`opencv_videoio_ffmpeg*.dll`插件；交叉编译成功不证明原生DLL能在Windows加载。

`.github/workflows/windows.yml`是可选择使用的CI配置，当前没有上传或在GitHub执行。Windows Server CI也不能替代Win10/Win11实机与BLE/USB验收。

源码包包括src、web源码和lockfile、tests、tools、assets、许可和文档；不把node_modules/bin/obj/用户测量数据当对应源码分发。发布包的文件校验值记录在manifest.json。
