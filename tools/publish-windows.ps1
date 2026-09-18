param([string]$Output = "artifacts/win-x64", [switch]$SkipWebBuild)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
  if (!$SkipWebBuild) {
  Push-Location web
  try { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' }; npm run build; if ($LASTEXITCODE) { throw 'web build failed' } } finally { Pop-Location }
  }
  $Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
  if (Test-Path (Join-Path $Output 'Phyphox.Server.dll')) {
    throw 'Output contains an older flat package. Choose a new -Output directory; keep your existing data folder.'
  }
  $program = Join-Path $Output 'app'
  New-Item -ItemType Directory -Path $Output -Force | Out-Null
  $launcher = Join-Path $Output 'phyphox.exe'
  dotnet publish src/Phyphox.Server/Phyphox.Server.csproj -c Release -r win-x64 --self-contained true -p:RuntimeIdentifier=win-x64 -p:UseSharedCompilation=false "-p:PortableLauncherPath=$launcher" -m:1 -o $program
  if ($LASTEXITCODE) { throw 'Windows publish failed' }
  Get-ChildItem $program -Recurse -Filter 'opencv_videoio_ffmpeg*.dll' | Remove-Item
  Copy-Item tools/test-windows.ps1, LICENSE -Destination $program
  if (Test-Path THIRD-PARTY-NOTICES.md) { Copy-Item THIRD-PARTY-NOTICES.md -Destination $program }
  Copy-Item docs -Destination $program -Recurse -Force
  if (Test-Path README.md) { Copy-Item README.md -Destination $program }
  @'
双击 phyphox.exe 即可开始使用。
浏览器会自动打开，任务栏窗口负责运行后端。
关闭浏览器不会停止服务；关闭后端窗口会停止服务。
app 文件夹包含程序依赖，请勿删除或单独移动 EXE。
data 文件夹会在首次启动时创建，用来保存你的实验数据。
更新时请解压到新文件夹，再在程序停止后复制旧 data 文件夹。
'@ | Set-Content (Join-Path $Output '开始使用.txt') -Encoding UTF8
  # The source archive carries the guide here; the working tree keeps it beside the project.
  $guide = @('user-guide/phyphox-Windows-使用指南.pdf', '../使用指南/phyphox-Windows-使用指南.pdf') | Where-Object { Test-Path $_ } | Select-Object -First 1
  if ($guide) { Copy-Item $guide -Destination (Join-Path $Output '使用指南.pdf') }
  Get-ChildItem $Output -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $program 'manifest.json') } | ForEach-Object { [PSCustomObject]@{path=$_.FullName.Substring($Output.Length+1);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash} } | ConvertTo-Json | Set-Content (Join-Path $program 'manifest.json') -Encoding UTF8
  Write-Host "Published $Output. Build success is not Windows/device acceptance."
} finally { Pop-Location }
