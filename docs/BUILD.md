# 可复现构建

## 固定工具与来源

- .NET SDK 10.0.401，global.json固定；自包含运行时10.0.12。
- Node.js 22；npm使用package-lock.json，不手工漂移依赖版本。
- NuGet主要包均在csproj固定版本。Windows发布显式传入`RuntimeIdentifier=win-x64`，确保BLE目标和原生相机包纳入。
- 官方资料固定提交见IMPLEMENTATION.md。官方docs测试语料不嵌入发布包；测试时在相邻`official-reference/phyphox-docs`准备固定提交。

## Windows构建机

安装 SDK 和 Node.js 后，可双击项目根目录的 `build-exe.cmd`，或运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-exe.ps1
# 可指定 ZIP 路径；相对路径以项目根目录为基准。
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-exe.ps1 -Output "artifacts/my-build.zip"
```

默认生成 `artifacts/phyphox-windows-win-x64.zip` 和 `.zip.sha256`。脚本在独立临时目录构建前端并发布自包含 EXE，检查必要文件后压缩，随后清理临时目录；不会把旧发布目录中的测量数据打进包。重复执行会替换同名 ZIP 和校验文件。ZIP 内是完整便携程序文件夹，不是安装器或单文件 EXE。

GitHub 上使用 **Actions → Build Windows EXE → Run workflow** 手动打包；推送 `v*` 标签也会触发。工作流读取 `global.json` 安装 SDK，调用同一本地脚本，上传 ZIP 和校验文件到该次运行的 Artifacts（保留 30 天）。下载的 Artifact 外层 ZIP 解压后，需再解压其中的程序 ZIP。此流程只做构建及包内容检查；现有 `Windows portable verification` 工作流继续承担服务验证，不会自动创建 Release。

安装SDK和Node后，在项目根执行：

```powershell
.\tools\publish-windows.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-windows.ps1 -ProgramDirectory .\artifacts\win-x64 -ReportPath .\artifacts\windows-check.json
```

发布脚本只生成文件夹，不注册服务、不安装驱动。运行包双击 `Phyphox.Server.exe`，会显示任务栏后端窗口并在服务就绪后打开默认浏览器；关闭窗口会停止服务。`start-phyphox.cmd` 兼容启动该窗口。Windows 发布包含 WinForms 所需运行时，SDK 只用于构建。自动化使用 `--headless` 禁用窗口及浏览器；`--no-browser` 保留窗口但不自动打开浏览器。CI 使用 `tools/test-desktop.ps1` 检查窗口、后端启动及关窗退出。

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
