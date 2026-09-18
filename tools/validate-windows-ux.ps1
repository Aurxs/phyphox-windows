param(
 [string]$SharedDirectory='\\Mac\Home\Downloads\phyphox-validation-01a0b35e',
 [string]$ProgramDirectory=''
)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new()
$utf8=[Text.UTF8Encoding]::new($false)
$work=Join-Path $env:TEMP ('phyphox-ux-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$server=$null;$edgeProcess=$null;$socket=$null;$edgeProfileDirectory=Join-Path $work 'edge-profile'
$script:sequence=0;$script:stage='preflight';$script:requests=[Collections.Generic.List[object]]::new()
$report=@{status='failed';checks=@();platform='Windows 11 ARM virtual machine';packageArchitecture='x64';physicalDevices='not opened or tested';externalPlatforms='not accessed';servicePort=37655;edgeDebugPort=37656;cleanup=@{}}
function Add-Check([string]$Name,$Details){$report.checks+=@{name=$Name;passed=$true;details=$Details};Write-Output ('PASS: '+$Name)}
function Invoke-Cdp([string]$Method,$Parameters) {
 $script:sequence++
 $message=@{id=$script:sequence;method=$Method;params=$Parameters}|ConvertTo-Json -Depth 30 -Compress
 $bytes=[Text.Encoding]::UTF8.GetBytes($message);$cancel=[Threading.CancellationTokenSource]::new(20000)
 try {
  $socket.SendAsync([ArraySegment[byte]]::new($bytes),[Net.WebSockets.WebSocketMessageType]::Text,$true,$cancel.Token).GetAwaiter().GetResult()|Out-Null
  do {
   $stream=[IO.MemoryStream]::new();$buffer=New-Object byte[] 32768
   try {
    do {$part=$socket.ReceiveAsync([ArraySegment[byte]]::new($buffer),$cancel.Token).GetAwaiter().GetResult();if($part.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close){throw 'Edge closed the debug connection.'};$stream.Write($buffer,0,$part.Count)}while(!$part.EndOfMessage)
    $reply=[Text.Encoding]::UTF8.GetString($stream.ToArray())|ConvertFrom-Json
   } finally {$stream.Dispose()}
   if($reply.method -eq 'Network.requestWillBeSent'){$script:requests.Add(@{url=$reply.params.request.url;method=$reply.params.request.method})}
  }while($reply.id -ne $script:sequence)
  if($reply.error){throw ($reply.error|ConvertTo-Json -Compress)}
  if($reply.result.exceptionDetails){throw ($reply.result.exceptionDetails|ConvertTo-Json -Depth 8 -Compress)}
  return $reply.result
 } finally {$cancel.Dispose()}
}
function Evaluate([string]$Expression){$answer=Invoke-Cdp 'Runtime.evaluate' @{expression=$Expression;returnByValue=$true;awaitPromise=$true};return $answer.result.value}
function Wait-Dom([string]$Expression,[string]$Failure){for($attempt=0;$attempt -lt 60;$attempt++){if(Evaluate $Expression){return};Start-Sleep -Milliseconds 200};throw $Failure}
function Click-Element([string]$Expression){
 $box=Evaluate "(()=>{const element=($Expression);if(!element)throw Error('UI element missing');if(element.disabled)throw Error('UI element disabled');element.scrollIntoView({block:'center'});const r=element.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()"
 Invoke-Cdp 'Input.dispatchMouseEvent' @{type='mousePressed';x=$box.x;y=$box.y;button='left';clickCount=1}|Out-Null
 Invoke-Cdp 'Input.dispatchMouseEvent' @{type='mouseReleased';x=$box.x;y=$box.y;button='left';clickCount=1}|Out-Null
}
function Save-Screenshot([string]$Name){$image=Invoke-Cdp 'Page.captureScreenshot' @{format='png';captureBeyondViewport=$false};[IO.File]::WriteAllBytes((Join-Path $SharedDirectory $Name),[Convert]::FromBase64String($image.data))}
function Session-State {return Invoke-RestMethod "$url/api/v1/session" -Headers $headers -TimeoutSec 5}
try {
 if(!(Test-Path -LiteralPath $SharedDirectory)){throw 'Task shared directory unavailable. No VM sharing settings will be changed.'}
 if(!$ProgramDirectory){
  $pointer=Join-Path $SharedDirectory 'windows-ux-program-path.txt'
  if(!(Test-Path -LiteralPath $pointer)){throw 'New UX package pointer missing. Refusing to use the old windows-final-program-path.txt package.'}
  $ProgramDirectory=[IO.File]::ReadAllText($pointer,[Text.Encoding]::UTF8).Trim()
 }
 $executable=Join-Path $ProgramDirectory 'Phyphox.Server.exe'
 if(!(Test-Path -LiteralPath $executable)){throw 'New UX portable executable unavailable.'}
 $listeners=Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|Where-Object {$_.LocalPort -in @(37655,37656)}
 if($listeners){throw 'Reserved validation ports 37655/37656 are already in use; existing processes were not stopped.'}
 $edge=@((Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),(Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'))|Where-Object {Test-Path -LiteralPath $_}|Select-Object -First 1
 if(!$edge){throw 'Installed Edge unavailable. No browser will be installed.'}
 $report.programDirectory=$ProgramDirectory;$report.edgeVersion=(Get-Item -LiteralPath $edge).VersionInfo.ProductVersion
 $report.serverAssemblySha256=(Get-FileHash -LiteralPath (Join-Path $ProgramDirectory 'Phyphox.Server.dll') -Algorithm SHA256).Hash
 $report.coreAssemblySha256=(Get-FileHash -LiteralPath (Join-Path $ProgramDirectory 'Phyphox.Core.dll') -Algorithm SHA256).Hash
 $script:stage='isolated service startup'
 $server=Start-Process -FilePath $executable -WorkingDirectory $env:WINDIR -ArgumentList @('--port','37655','--data-dir',('"'+(Join-Path $work 'data')+'"')) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work 'server.stdout.log') -RedirectStandardError (Join-Path $work 'server.stderr.log')
 $url='http://127.0.0.1:37655';$health=$null
 for($i=0;$i -lt 40;$i++){if($server.HasExited){throw 'Isolated UX service exited.'};try{$health=Invoke-RestMethod "$url/api/v1/health" -TimeoutSec 2;break}catch{Start-Sleep -Milliseconds 500}}
 if(!$health){throw 'Isolated UX service did not start.'}
 $report.health=$health
 $headers=@{'X-Phyphox-Token'=(Invoke-RestMethod "$url/api/v1/bootstrap").token;'X-Phyphox-Client'='browser'}
 Add-Check 'isolated portable service starts from unrelated working directory' @{cwd=$env:WINDIR;dataIsTemporary=$true;processArchitecture=$health.processArchitecture;osArchitecture=$health.osArchitecture}
 $script:stage='installed Edge startup'
 $edgeProcess=Start-Process -FilePath $edge -ArgumentList @('--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--disable-background-networking','--remote-debugging-address=127.0.0.1','--remote-debugging-port=37656','--window-size=1440,1000',('--user-data-dir="'+$edgeProfileDirectory+'"'),'about:blank') -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work 'edge.stdout.log') -RedirectStandardError (Join-Path $work 'edge.stderr.log')
 $tab=$null
 for($i=0;$i -lt 40;$i++){try{$tabs=Invoke-RestMethod 'http://127.0.0.1:37656/json/list' -TimeoutSec 2;foreach($candidate in $tabs){if($candidate.type -eq 'page'){$tab=$candidate;break}};if($tab){break}}catch{};Start-Sleep -Milliseconds 300}
 if(!$tab){throw 'Installed Edge did not expose an isolated local page.'}
 $socket=[Net.WebSockets.ClientWebSocket]::new();$connectTimeout=[Threading.CancellationTokenSource]::new(15000)
 try{$socket.ConnectAsync([Uri]$tab.webSocketDebuggerUrl,$connectTimeout.Token).GetAwaiter().GetResult()}finally{$connectTimeout.Dispose()}
 Invoke-Cdp 'Page.enable' @{}|Out-Null
 Invoke-Cdp 'Network.enable' @{}|Out-Null
 Invoke-Cdp 'Emulation.setDeviceMetricsOverride' @{width=1440;height=1000;deviceScaleFactor=1;mobile=$false}|Out-Null
 Invoke-Cdp 'Page.navigate' @{url=$url}|Out-Null
 $script:stage='new library layout'
 Wait-Dom "document.querySelectorAll('.experiment-row').length>0 && document.querySelectorAll('.source-tabs button').length===4" 'New experiment library did not render.'
 $libraryDom=Evaluate "({heading:document.querySelector('main h1')?.innerText,categories:document.querySelectorAll('.experiment-group').length,rows:document.querySelectorAll('.experiment-row').length,search:!!document.querySelector('.search-field input[type=search]'),welcome:!!document.querySelector('.welcome-strip button'),sidebar:document.querySelectorAll('.app-sidebar nav button').length,viewport:innerWidth,scrollWidth:document.documentElement.scrollWidth})"
 if(!$libraryDom.search -or !$libraryDom.welcome -or $libraryDom.sidebar -lt 5 -or $libraryDom.scrollWidth -gt ($libraryDom.viewport+2)){throw 'Library navigation/search/layout checks failed.'}
 $localized=Evaluate "({rawSensors:[...document.querySelectorAll('.category-heading h2')].some(e=>e.textContent.trim()==='原始传感器'),acceleration:[...document.querySelectorAll('.experiment-row strong')].some(e=>e.textContent.trim()==='加速度 (含 g)'),brightness:[...document.querySelectorAll('.experiment-row strong')].some(e=>e.textContent.trim()==='亮度'),formula:[...document.querySelectorAll('.experiment-row strong')].some(e=>e.textContent.trim()==='公式计算工作台')})"
 if(!$localized.rawSensors -or !$localized.acceleration -or !$localized.brightness -or !$localized.formula){throw ('Expected Chinese experiment labels not rendered: '+($localized|ConvertTo-Json -Compress))}
 $report.localizedLabels=$localized
 $report.libraryDom=$libraryDom
 Save-Screenshot 'windows-ux-library.png'
 Add-Check 'new categorized library, search, source filters and navigation render' $libraryDom
 $script:stage='served UI asset SHA256'
 $assetExpression=@'
(async()=>{const nodes=[...document.querySelectorAll('script[src],link[rel="stylesheet"][href]')];const urls=[...new Set(nodes.map(n=>n.src||n.href))];if(!urls.length)throw Error('No built assets found');return await Promise.all(urls.map(async href=>{const u=new URL(href);if(u.origin!==location.origin)throw Error('Unexpected remote UI asset');const r=await fetch(u);if(!r.ok)throw Error('Asset fetch failed');const data=await r.arrayBuffer();const digest=await crypto.subtle.digest('SHA-256',data);return {path:u.pathname,bytes:data.byteLength,sha256:[...new Uint8Array(digest)].map(b=>b.toString(16).padStart(2,'0')).join('')}}))})()
'@
 $assets=@(Evaluate $assetExpression)
 foreach($asset in $assets){$relative=[Uri]::UnescapeDataString($asset.path.TrimStart('/')).Replace('/',[IO.Path]::DirectorySeparatorChar);$assetPath=[IO.Path]::GetFullPath((Join-Path (Join-Path $ProgramDirectory 'wwwroot') $relative));$assetRoot=[IO.Path]::GetFullPath((Join-Path $ProgramDirectory 'wwwroot'))+[IO.Path]::DirectorySeparatorChar;if(!$assetPath.StartsWith($assetRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Asset path escaped the published web root.'};$diskHash=(Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash;if($diskHash -ne $asset.sha256){throw ('Served asset hash mismatch: '+$asset.path)}}
 if(!($assets|Where-Object path -Like '*.js') -or !($assets|Where-Object path -Like '*.css')){throw 'Built CSS and JS asset hashes were not both verified.'}
 $report.assetHashes=$assets
 Add-Check 'browser-served CSS/JS bytes match published package SHA256' $assets
 $script:stage='official sensor localization and unavailable-hardware state'
 Click-Element "[...document.querySelectorAll('.experiment-row')].find(e=>e.querySelector('strong')?.textContent.trim()==='加速度 (含 g)')"
 Wait-Dom "document.querySelector('.experiment-title h1')?.textContent.trim()==='加速度 (含 g)' && document.querySelectorAll('[role=tab]').length>=2" 'Official accelerometer UI did not render.'
 $sensorDom=Evaluate "({title:document.querySelector('.experiment-title h1')?.innerText,tabs:[...document.querySelectorAll('[role=tab]')].map(e=>e.textContent.trim()),captions:[...document.querySelectorAll('.graph-caption')].map(e=>e.textContent),startDisabled:document.querySelector('.measure-button')?.disabled,status:document.querySelector('.status-pill')?.textContent.trim(),unresolved:document.querySelector('main')?.innerText.includes('[[')})"
 $sensorState=Session-State
 $sensorViews=$sensorState.views|ConvertTo-Json -Depth 40 -Compress
 if(!$sensorDom.startDisabled -or $sensorState.canStart -or $sensorDom.status -ne '需要配置' -or $sensorDom.unresolved -or $sensorViews.Contains('[[')){throw ('Sensor capability/localization check failed: '+($sensorDom|ConvertTo-Json -Depth 10 -Compress))}
 foreach($label in $sensorDom.tabs){if($label -notmatch '[㐀-鿿]'){throw ('Sensor tab is not localized: '+$label)}}
 $report.sensor=$sensorDom
 Save-Screenshot 'windows-ux-sensor-needs-setup.png'
 Add-Check 'official accelerometer translated tabs/units and disabled start with needs-setup status' $sensorDom
 Click-Element "document.querySelector('.back-button')"
 Wait-Dom "!!document.querySelector('.welcome-strip button')" 'Back-to-library action failed.'
 $script:stage='open formula through visible library control'
 Click-Element "document.querySelector('.welcome-strip button')"
 Wait-Dom "!!document.querySelector('dialog[open] .dialog-actions') || !!document.querySelector('.experiment-toolbar')" 'Formula navigation did not respond.'
 if(Evaluate "!!document.querySelector('dialog[open] .dialog-actions')"){Click-Element "[...document.querySelectorAll('dialog[open] .dialog-actions button')].find(e=>/^(直接切换|Switch without saving)$/.test(e.textContent.trim()))"}
 Wait-Dom "!!document.querySelector('.experiment-toolbar') && document.querySelector('input[type=number]')?.value==='2' && [...document.querySelectorAll('.panel.value')].some(e=>e.innerText.includes('4.0000'))" 'Formula UI did not initialize input 2 and square 4.'
 $initial=Session-State
 if($initial.buffers.x[-1] -ne 2 -or $initial.buffers.square[-1] -ne 4){throw 'Actual formula backend initialization differs from rendered UI.'}
 Add-Check 'formula opened through library UI with initial actual 2 squared equals 4' @{id=$initial.id;input=2;square=4}
 $script:stage='actual browser input 3 and result 9'
 Click-Element "document.querySelector('input[type=number]')"
 Invoke-Cdp 'Input.dispatchKeyEvent' @{type='keyDown';key='a';code='KeyA';windowsVirtualKeyCode=65;modifiers=2}|Out-Null
 Invoke-Cdp 'Input.dispatchKeyEvent' @{type='keyUp';key='a';code='KeyA';windowsVirtualKeyCode=65;modifiers=2}|Out-Null
 Invoke-Cdp 'Input.insertText' @{text='3'}|Out-Null
 Invoke-Cdp 'Input.dispatchKeyEvent' @{type='keyDown';key='Enter';code='Enter';windowsVirtualKeyCode=13}|Out-Null
 Invoke-Cdp 'Input.dispatchKeyEvent' @{type='keyUp';key='Enter';code='Enter';windowsVirtualKeyCode=13}|Out-Null
 Wait-Dom "document.querySelector('input[type=number]')?.value==='3' && [...document.querySelectorAll('.panel.value')].some(e=>e.innerText.includes('9.0000'))" 'Typing 3 did not produce rendered 9.0000.'
 $computed=Session-State
 if($computed.buffers.x[-1] -ne 3 -or $computed.buffers.square[-1] -ne 9){throw 'Backend did not compute actual 3 squared equals 9 after browser input.'}
 $report.formula=@{input=$computed.buffers.x[-1];square=$computed.buffers.square[-1];cycle=$computed.cycle;source='real browser input and actual local engine'}
 Save-Screenshot 'windows-ux-formula-nine.png'
 Add-Check 'actual browser input 3 computes and renders 9' $report.formula
 $script:stage='clear cancel preserves data'
 $before=Session-State;$beforeBuffers=$before.buffers|ConvertTo-Json -Depth 30 -Compress
 Click-Element "[...document.querySelectorAll('.experiment-toolbar button')].find(e=>/清空数据|Clear data/.test(e.getAttribute('aria-label')||''))"
 Wait-Dom "!!document.querySelector('dialog[open] .dialog-actions button.danger')" 'Clear confirmation did not appear.'
 Save-Screenshot 'windows-ux-clear-dialog.png'
 Click-Element "[...document.querySelectorAll('dialog[open] .dialog-actions button')].find(e=>/^(取消|Cancel)$/.test(e.textContent.trim()))"
 Wait-Dom "!document.querySelector('dialog[open]')" 'Cancel did not dismiss the clear confirmation.'
 $after=Session-State;$afterBuffers=$after.buffers|ConvertTo-Json -Depth 30 -Compress
 if($beforeBuffers -ne $afterBuffers -or $after.buffers.square[-1] -ne 9 -or $after.cycle -ne $before.cycle){throw 'Canceling clear changed data or calculation cycle.'}
 $report.clearCancel=@{allBuffersPreserved=$true;cyclePreserved=$true;input=$after.buffers.x[-1];square=$after.buffers.square[-1]}
 Add-Check 'cancel clear preserves all actual buffers and calculation cycle' $report.clearCancel
 $script:stage='save and export menu'
 Click-Element "[...document.querySelectorAll('.experiment-toolbar button')].find(e=>/保存与导出|Save & export/.test(e.getAttribute('aria-label')||''))"
 Wait-Dom "document.querySelectorAll('dialog[open] .save-options button').length===4" 'Four save/export actions did not appear.'
 $saveMenu=Evaluate "({title:document.querySelector('dialog[open] h2')?.innerText,options:[...document.querySelectorAll('dialog[open] .save-options button')].map(e=>({text:e.innerText,disabled:e.disabled}))})"
 if(($saveMenu.options|Where-Object disabled).Count -gt 0){throw 'Save/export actions were unexpectedly disabled.'}
 $saveText=($saveMenu.options.text -join ' ')
 if($saveText -notmatch '\.phyphox' -or $saveText -notmatch 'CSV' -or $saveText -notmatch '\.xlsx'){throw 'Save menu omitted required state/CSV/XLSX formats.'}
 $report.saveMenu=$saveMenu
 Save-Screenshot 'windows-ux-save-menu.png'
 Add-Check 'save menu exposes local save, state, CSV/ZIP and XLSX actions' $saveMenu
 Click-Element "document.querySelector('dialog[open] .dialog-heading button')"
 Wait-Dom "!document.querySelector('dialog[open]')" 'Save dialog did not close.'
 $script:stage='device form without device activation'
 Click-Element "[...document.querySelectorAll('.app-sidebar nav button')].find(e=>/连接设备|Connect devices/.test(e.textContent))"
 Wait-Dom "document.querySelectorAll('.device-transports button').length===3" 'New device forms did not render.'
 $deviceForm=Evaluate "({transportChoices:[...document.querySelectorAll('.device-transports button')].map(e=>e.textContent),formValues:[...document.querySelector('.device-form-grid').querySelectorAll('input:not([type=checkbox]),select')].map(e=>e.value),details:document.querySelectorAll('.device-page details').length})"
 if(($deviceForm.formValues|Where-Object {$_ -ne ''}).Count -gt 0){throw 'Serial connection form unexpectedly guessed communication settings.'}
 Save-Screenshot 'windows-ux-device-form.png'
 Add-Check 'device form renders without assumed serial settings or hardware scan' $deviceForm
 Invoke-Cdp 'Runtime.evaluate' @{expression='document.title';returnByValue=$true}|Out-Null
 $hardwareRequests=@($script:requests|Where-Object {$_.url -match '/devices/(scan|connect|ble/experiment)|/media/(camera/probe|audio/devices)'})
 if($hardwareRequests.Count -gt 0){throw 'UI verification unexpectedly requested hardware discovery or connection.'}
 $report.requests=@($script:requests);$report.hardwareRequests=$hardwareRequests
 [IO.File]::WriteAllText((Join-Path $SharedDirectory 'windows-ux-dom.json'),((@{library=$libraryDom;formula=$report.formula;saveMenu=$saveMenu;deviceForm=$deviceForm})|ConvertTo-Json -Depth 20),$utf8)
 $report.status='passed';Write-Output 'Windows UX checks passed on the actual published package and installed Edge.'
} catch {
 $report.error=$_.Exception.Message;$report.failedStage=$script:stage
 Write-Output ('Windows UX check failed at '+$script:stage+': '+$report.error)
 if($socket -and $socket.State -eq [Net.WebSockets.WebSocketState]::Open){try{Save-Screenshot 'windows-ux-failure.png'}catch{}}
} finally {
 if($socket){$socket.Dispose()}
 if($edgeProcess){try{if(!$edgeProcess.HasExited){& taskkill.exe /PID $edgeProcess.Id /T /F | Out-Null}}catch{$report.cleanup.edgeError=$_.Exception.Message}}
 # Only orphan processes carrying this task's unique profile directory are eligible for cleanup.
 try {$orphans=@(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'"|Where-Object {$_.CommandLine -and $_.CommandLine.Contains($edgeProfileDirectory)});foreach($orphan in $orphans){& taskkill.exe /PID $orphan.ProcessId /T /F | Out-Null}}catch{$report.cleanup.edgeOrphanError=$_.Exception.Message}
 if($server){try{if(!$server.HasExited){& taskkill.exe /PID $server.Id /T /F | Out-Null}}catch{$report.cleanup.serverError=$_.Exception.Message}}
 foreach($name in @('server.stdout.log','server.stderr.log','edge.stdout.log','edge.stderr.log')){try{$log=Join-Path $work $name;if(Test-Path -LiteralPath $log){Copy-Item -LiteralPath $log -Destination (Join-Path $SharedDirectory ('windows-ux-'+$name)) -Force}}catch{}}
 try {Remove-Item -LiteralPath $work -Recurse -Force;$report.cleanup.temporaryDirectoryRemoved=$true}catch{$report.cleanup.temporaryDirectoryRemoved=$false;$report.cleanup.remainingDirectory=$work}
 try {$leftovers=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|Where-Object {$_.LocalPort -in @(37655,37656)});$report.cleanup.portsReleased=($leftovers.Count -eq 0)}catch{$report.cleanup.portsReleased='unknown'}
 if($report.status -eq 'passed' -and $report.cleanup.portsReleased -ne $true){$report.status='failed';$report.error='Validation processes did not release the dedicated ports.'}
 $report.checkedAt=[DateTimeOffset]::UtcNow.ToString('o')
 if(Test-Path -LiteralPath $SharedDirectory){[IO.File]::WriteAllText((Join-Path $SharedDirectory 'windows-ux-validation.json'),($report|ConvertTo-Json -Depth 30),$utf8)}
 $report|ConvertTo-Json -Depth 30
}

if($report.status -ne 'passed'){exit 1}
