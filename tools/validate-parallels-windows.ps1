param([Parameter(Mandatory=$true)][string]$SharedDirectory, [switch]$ResumeRelocation)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new()
if (!$ResumeRelocation) {
$root=Join-Path $env:TEMP ('phyphox-final-'+[Guid]::NewGuid().ToString('N'))
$portable=Join-Path $root 'portable'
Write-Output 'Extracting final portable validation package.'
Expand-Archive -LiteralPath (Join-Path $SharedDirectory 'phyphox-win-x64-final-validation.zip') -DestinationPath $portable
Write-Output 'Running final package validation.'
$report=Join-Path $SharedDirectory 'windows-arm-final-validation.json'
& (Join-Path $portable 'test-windows.ps1') -ProgramDirectory $portable -Port 37653 -ReportPath $report | Out-Null
} else {
 $report=Join-Path $SharedDirectory 'windows-arm-final-validation.json'
 $existing=Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json
 $portable=$existing.programDirectory
 $root=Split-Path $portable -Parent
}
$r=Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json
if($r.status -ne 'passed'){Get-Content $report -Raw -Encoding UTF8;throw 'Portable test failed'}
$r | Select-Object status,elevated,dotnetOnPath,processorCount,checks,serverAssemblySha256 | ConvertTo-Json -Depth 5
$r.health | ConvertTo-Json -Depth 5
$r.cameraBackend | Select-Object version,backend,deviceOpened | ConvertTo-Json
# Use Unicode code points so Windows PowerShell 5.1 can read this ASCII script on any code page.
$name='portable moved '+[char]0x4e2d+[char]0x6587
$relocated=Join-Path $root $name
Move-Item -LiteralPath $portable -Destination $relocated
$letter=@('P','Q','R','S','T') | Where-Object { !(Test-Path ($_.ToString()+':\')) } | Select-Object -First 1
if(!$letter){throw 'No unused test drive letter'}
& subst.exe ($letter+':') $root
if($LASTEXITCODE -ne 0){throw 'Drive mapping failed'}
try {
 $mapped=$letter+':\'+$name
 $report=Join-Path $SharedDirectory 'windows-arm-relocated-validation.json'
 & (Join-Path $mapped 'test-windows.ps1') -ProgramDirectory $mapped -Port 37654 -UsePortableData -ReportPath $report | Out-Null
 $r=Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json
 if($r.status -ne 'passed'){Get-Content $report -Raw -Encoding UTF8;throw 'Relocated portable test failed'}
 $r | Select-Object status,programDirectory,portableData,checks | ConvertTo-Json -Depth 5
} finally { & subst.exe ($letter+':') /d }
[IO.File]::WriteAllText((Join-Path $SharedDirectory 'windows-final-program-path.txt'),$relocated,[Text.UTF8Encoding]::new($false))
