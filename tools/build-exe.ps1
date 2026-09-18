param([string]$Output = 'artifacts/phyphox-windows-win-x64.zip')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
$staging = $null
try {
  foreach ($command in @('dotnet', 'npm')) {
    if (!(Get-Command $command -ErrorAction SilentlyContinue)) {
      throw "Missing $command. Install the .NET SDK specified in global.json and Node.js 22, then try again."
    }
  }
  $archive = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
  if ([IO.Path]::GetExtension($archive) -ne '.zip') { throw 'Output must be a .zip file.' }
  $staging = Join-Path (Get-Location).Path ('artifacts/build-' + [Guid]::NewGuid().ToString('N'))
  $portable = Join-Path $staging 'phyphox-windows-win-x64'
  New-Item -ItemType Directory -Path $portable -Force | Out-Null
  & (Join-Path $PSScriptRoot 'publish-windows.ps1') -Output $portable
  foreach ($file in @('Phyphox.Server.exe', 'wwwroot/index.html', 'start-phyphox.cmd', 'LICENSE', 'manifest.json')) {
    if (!(Test-Path (Join-Path $portable $file) -PathType Leaf)) { throw "Package is missing $file" }
  }
  # Build in a fresh directory so previous portable data and stale binaries are never packaged.
  $temporaryArchive = Join-Path $staging 'package.zip'
  Compress-Archive -Path $portable -DestinationPath $temporaryArchive -CompressionLevel Optimal
  New-Item -ItemType Directory -Path (Split-Path $archive -Parent) -Force | Out-Null
  Move-Item $temporaryArchive $archive -Force
  $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
  "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256" -Encoding ASCII
  Write-Host "Package: $archive"
  Write-Host "SHA256:  $archive.sha256"
  Write-Host 'Extract the complete ZIP and run start-phyphox.cmd.'
} finally {
  if ($staging -and (Test-Path $staging)) { Remove-Item $staging -Recurse -Force }
  Pop-Location
}
