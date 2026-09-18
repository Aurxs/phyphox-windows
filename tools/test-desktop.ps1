param([Parameter(Mandatory=$true)][string]$ProgramDirectory, [int]$Port = 37655)
$ErrorActionPreference = 'Stop'
$executable = Join-Path (Resolve-Path $ProgramDirectory).Path 'Phyphox.Server.exe'
$data = Join-Path $env:TEMP ('phyphox-desktop-' + [Guid]::NewGuid().ToString('N'))
$process = $null
try {
  $process = Start-Process $executable -ArgumentList @('--no-browser', '--port', "$Port", '--data-dir', ('"' + $data + '"')) -PassThru
  $ready = $false
  for ($i = 0; $i -lt 60; $i++) {
    $process.Refresh()
    if ($process.HasExited) { throw 'Desktop process exited before startup.' }
    try {
      $null = Invoke-RestMethod "http://127.0.0.1:$Port/api/v1/health" -TimeoutSec 1
      if ($process.MainWindowHandle -ne 0) { $ready = $true; break }
    } catch { }
    Start-Sleep -Milliseconds 500
  }
  if (!$ready) { throw 'Desktop window and healthy backend did not become ready.' }
  if (!$process.CloseMainWindow()) { throw 'Could not close the desktop window.' }
  if (!$process.WaitForExit(15000)) { throw 'Closing the window did not stop the backend.' }
  if ($process.ExitCode -ne 0) { throw "Desktop exited with code $($process.ExitCode)." }
  # A clean shutdown must release the portable data lock.
  $lock = [IO.File]::Open((Join-Path $data '.service.lock'), 'Open', 'ReadWrite', 'None')
  $lock.Dispose()
  Write-Host 'Desktop window, backend startup, close-to-stop and data-lock release passed.'
} finally {
  if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
  if (Test-Path $data) { Remove-Item $data -Recurse -Force }
}
