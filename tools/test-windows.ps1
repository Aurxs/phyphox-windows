param([Parameter(Mandatory=$true)][string]$ProgramDirectory, [int]$Port = 37653, [string]$ReportPath = "", [switch]$UsePortableData)
$ErrorActionPreference = 'Stop'
$ProgramDirectory = (Resolve-Path $ProgramDirectory).Path
$runDirectory = Join-Path $env:TEMP ('phyphox-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$report = [ordered]@{
  timestamp = (Get-Date).ToUniversalTime().ToString('o')
  os = [Environment]::OSVersion.VersionString
  osArchitecture = $env:PROCESSOR_ARCHITECTURE
  powershellVersion = $PSVersionTable.PSVersion.ToString()
  user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
  elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  processorCount = [Environment]::ProcessorCount
  dotnetOnPath = [bool](Get-Command dotnet -ErrorAction SilentlyContinue)
  programDirectory = $ProgramDirectory
  checks = @()
  physicalDevices = 'not tested'
  performance = 'not tested'
  portableData = [bool]$UsePortableData
}
$process = $null
try {
  $executable = Join-Path $ProgramDirectory 'Phyphox.Server.exe'
  if (!(Test-Path $executable)) { throw 'Portable server executable missing.' }
  $arguments = @('--headless', '--port', "$Port")
  if (!$UsePortableData) { $arguments += @('--data-dir', ('"' + $runDirectory + '"')) }
  $process = Start-Process -FilePath $executable -WorkingDirectory $env:WINDIR -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDirectory 'stdout.log') -RedirectStandardError (Join-Path $runDirectory 'stderr.log')
  $url = "http://127.0.0.1:$Port"
  $health = $null
  for ($i = 0; $i -lt 60; $i++) {
    if ($process.HasExited) { throw ('Server exited: ' + (Get-Content (Join-Path $runDirectory 'stderr.log') -Raw)) }
    try { $health = Invoke-RestMethod "$url/api/v1/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
  }
  if (!$health) { throw 'Server did not become healthy.' }
  $report.health = $health
  $report.serverAssemblySha256 = (Get-FileHash (Join-Path $ProgramDirectory 'Phyphox.Server.dll') -Algorithm SHA256).Hash
  $report.checks += 'self-contained startup'
  $page = Invoke-WebRequest "$url/" -UseBasicParsing
  if ($page.StatusCode -ne 200 -or $page.Content -notmatch '<div id="root">') { throw 'Browser entry unavailable.' }
  $report.checks += 'offline web entry'
  $headers = @{ 'X-Phyphox-Token' = (Invoke-RestMethod "$url/api/v1/bootstrap").token; 'X-Phyphox-Client' = 'browser' }
  function ApiPost($path, $body) { Invoke-RestMethod "$url/api/v1/$path" -Headers $headers -Method Post -ContentType 'application/json; charset=utf-8' -Body ($body | ConvertTo-Json -Depth 20 -Compress) }
  $library = Invoke-RestMethod "$url/api/v1/library" -Headers $headers
  if (@($library.items | Where-Object source -eq 'official').Count -ne 67) { throw 'Official asset count changed.' }
  $sample = $library.items | Where-Object source -eq 'sample' | Select-Object -First 1
  $snapshot = ApiPost 'session/load' @{id=$sample.id}
  if ($snapshot.buffers.square[0] -ne 4) { throw 'Initial formula result differs.' }
  $snapshot = ApiPost 'session/commands' @{command='set';buffer='x';values=@(3);requestId='windows-check-3'}
  if ($snapshot.buffers.square[0] -ne 9) { throw 'Formula calculation differs.' }
  $snapshot = ApiPost 'session/commands' @{command='start'}
  if ($snapshot.status -ne 'running') { throw 'Start failed.' }
  $snapshot = ApiPost 'session/commands' @{command='pause'}
  if ($snapshot.status -ne 'paused') { throw 'Pause failed.' }
  $snapshot = ApiPost 'session/commands' @{command='stop'}
  if ($snapshot.buffers.square[0] -ne 9) { throw 'Stop erased result.' }
  $report.checks += 'library / formula / start / pause / stop'
  foreach ($format in @('csv','xlsx','state')) {
    $destination = Join-Path $runDirectory ("export.$format")
    Invoke-WebRequest "$url/api/v1/exports?format=$format" -Headers $headers -OutFile $destination -UseBasicParsing
    if ((Get-Item $destination).Length -eq 0) { throw "Empty $format export." }
  }
  $report.checks += 'CSV / XLSX / state exports'
  $recordings = Invoke-RestMethod "$url/api/v1/recordings" -Headers $headers
  $recording = $recordings.items | Where-Object complete -eq $true | Select-Object -First 1
  if (!$recording) { throw 'Completed journal missing.' }
  $replay = ApiPost ('recordings/' + $recording.id + '/replay') @{}
  if ($replay.buffers.square[0] -ne 9 -or $replay.hardwareOutputEnabled) { throw 'Replay calculation or output isolation failed.' }
  $saved = ApiPost 'snapshots/save' @{id='windows-check'}
  $null = ApiPost 'session/commands' @{command='clear'}
  $restored = ApiPost ('snapshots/' + $saved.id + '/restore') @{}
  if ($restored.buffers.square[0] -ne 9 -or $restored.status -ne 'paused') { throw 'Snapshot restoration failed.' }
  $report.checks += 'journal replay / snapshot save and paused restore'
  $backend = Invoke-RestMethod "$url/api/v1/media/camera/backend" -Headers $headers
  $report.cameraBackend = $backend
  $report.checks += 'camera native DLL loaded without activating hardware'
  if ($UsePortableData) {
    if (!(Test-Path (Join-Path $ProgramDirectory 'data/.service.lock'))) { throw 'Default data directory is not relative to executable.' }
    if (!(Test-Path (Join-Path $ProgramDirectory 'data/snapshots/windows-check.snapshot.json'))) { throw 'Portable data snapshot missing.' }
    $report.checks += 'default data path follows executable directory'
  }
  $report.devices = Invoke-RestMethod "$url/api/v1/devices" -Headers $headers
  $report.status = 'passed'
} catch {
  $report.status = 'failed'
  $report.error = $_.Exception.Message
} finally {
  if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
  $report.logsDirectory = $runDirectory
  $json = $report | ConvertTo-Json -Depth 20
  if ($ReportPath) { [IO.File]::WriteAllText($ReportPath, $json, (New-Object Text.UTF8Encoding($false))) }
  $json
}
if ($report.status -ne 'passed') { exit 1 }
