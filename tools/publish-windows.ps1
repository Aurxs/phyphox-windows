param([string]$Output = "artifacts/win-x64", [switch]$SkipWebBuild)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
  if (!$SkipWebBuild) {
  Push-Location web
  try { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' }; npm run build; if ($LASTEXITCODE) { throw 'web build failed' } } finally { Pop-Location }
  }
  dotnet publish src/Phyphox.Server/Phyphox.Server.csproj -c Release -r win-x64 --self-contained true -p:RuntimeIdentifier=win-x64 -p:UseSharedCompilation=false -m:1 -o $Output
  if ($LASTEXITCODE) { throw 'Windows publish failed' }
  Get-ChildItem $Output -Recurse -Filter 'opencv_videoio_ffmpeg*.dll' | Remove-Item
  Copy-Item tools/start-phyphox.cmd, tools/test-windows.ps1, LICENSE -Destination $Output
  if (Test-Path THIRD-PARTY-NOTICES.md) { Copy-Item THIRD-PARTY-NOTICES.md -Destination $Output }
  Copy-Item docs -Destination $Output -Recurse -Force
  if (Test-Path README.md) { Copy-Item README.md -Destination $Output }
  # The source archive carries the guide here; the working tree keeps it beside the project.
  $guide = @('user-guide/phyphox-Windows-使用指南.pdf', '../使用指南/phyphox-Windows-使用指南.pdf') | Where-Object { Test-Path $_ } | Select-Object -First 1
  if ($guide) { Copy-Item $guide -Destination (Join-Path $Output '使用指南.pdf') }
  Get-ChildItem $Output -Recurse -File | Where-Object { $_.FullName -ne (Join-Path (Resolve-Path $Output).Path 'manifest.json') } | ForEach-Object { [PSCustomObject]@{path=$_.FullName.Substring((Resolve-Path $Output).Path.Length+1);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash} } | ConvertTo-Json | Set-Content (Join-Path $Output 'manifest.json') -Encoding UTF8
  Write-Host "Published $Output. Build success is not Windows/device acceptance."
} finally { Pop-Location }
