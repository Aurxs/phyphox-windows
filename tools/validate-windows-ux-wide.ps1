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
$script:sequence=0;$script:stage='preflight';$script:requests=[Collections.Generic.List[object]]::new();$script:browserErrors=[Collections.Generic.List[object]]::new()
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
   if($reply.method -in @('Log.entryAdded','Runtime.exceptionThrown')){$script:browserErrors.Add($reply)}
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
 Invoke-Cdp 'Runtime.enable' @{}|Out-Null
 Invoke-Cdp 'Log.enable' @{}|Out-Null
 Invoke-Cdp 'Emulation.setDeviceMetricsOverride' @{width=1440;height=1000;deviceScaleFactor=1;mobile=$false}|Out-Null
 $monitor=@'
(()=>{window.__mediaUse={capture:0,contexts:0};if(navigator.mediaDevices?.getUserMedia){const original=navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);navigator.mediaDevices.getUserMedia=(...args)=>{window.__mediaUse.capture++;return original(...args)}}if(window.AudioContext){const Original=window.AudioContext;window.AudioContext=new Proxy(Original,{construct(target,args){window.__mediaUse.contexts++;return Reflect.construct(target,args)}})}})();
'@
 Invoke-Cdp 'Page.addScriptToEvaluateOnNewDocument' @{source=$monitor}|Out-Null
 Invoke-Cdp 'Page.navigate' @{url=$url}|Out-Null

 $report.scope='Increment only: 1920/2560 layout, trusted graph wheel and corrected serial profile form selector. Previously passed calculation/save/localization flows not repeated.'
 $script:stage='wide library layout'
 Wait-Dom "document.querySelectorAll('.experiment-row').length>0" 'Library did not render.'
 $report.wideLayouts=@()
 foreach($width in @(1920,2560)){
  Invoke-Cdp 'Emulation.setDeviceMetricsOverride' @{width=$width;height=1440;deviceScaleFactor=1;mobile=$false}|Out-Null
  Start-Sleep -Milliseconds 300
  $layout=Evaluate "(()=>{const main=document.querySelector('main').getBoundingClientRect();const groups=[...document.querySelectorAll('.experiment-row')].map(e=>({x:Math.round(e.getBoundingClientRect().x),width:Math.round(e.getBoundingClientRect().width)}));return {viewport:innerWidth,scrollWidth:document.documentElement.scrollWidth,main:{x:main.x,width:main.width},groups,columns:new Set(groups.map(e=>e.x)).size}})()"
  if($layout.scrollWidth -gt ($width+2) -or $layout.columns -lt 2){throw ('Wide library layout failed: '+($layout|ConvertTo-Json -Depth 5 -Compress))}
  $report.wideLayouts+= $layout
  Save-Screenshot ('windows-ux-wide-library-'+$width+'.png')
 }
 Add-Check 'library uses multiple columns without page horizontal overflow at 1920 and 2560' $report.wideLayouts
 $script:stage='served UI asset SHA256'
 $assetExpression=@'
(async()=>{const nodes=[...document.querySelectorAll('script[src],link[rel="stylesheet"][href]')];const urls=[...new Set(nodes.map(n=>n.src||n.href))];if(!urls.length)throw Error('No built assets found');return await Promise.all(urls.map(async href=>{const u=new URL(href);if(u.origin!==location.origin)throw Error('Unexpected remote UI asset');const r=await fetch(u);if(!r.ok)throw Error('Asset fetch failed');const data=await r.arrayBuffer();const digest=await crypto.subtle.digest('SHA-256',data);return {path:u.pathname,bytes:data.byteLength,sha256:[...new Uint8Array(digest)].map(b=>b.toString(16).padStart(2,'0')).join('')}}))})()
'@
 $assets=@(Evaluate $assetExpression)
 foreach($asset in $assets){$relative=[Uri]::UnescapeDataString($asset.path.TrimStart('/')).Replace('/',[IO.Path]::DirectorySeparatorChar);$assetPath=[IO.Path]::GetFullPath((Join-Path (Join-Path $ProgramDirectory 'wwwroot') $relative));$assetRoot=[IO.Path]::GetFullPath((Join-Path $ProgramDirectory 'wwwroot'))+[IO.Path]::DirectorySeparatorChar;if(!$assetPath.StartsWith($assetRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Asset path escaped the published web root.'};$diskHash=(Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash;if($diskHash -ne $asset.sha256){throw ('Served asset hash mismatch: '+$asset.path)}}
 if(!($assets|Where-Object path -Like '*.js') -or !($assets|Where-Object path -Like '*.css')){throw 'Built CSS and JS asset hashes were not both verified.'}
 $report.assetHashes=$assets
 Add-Check 'browser-served CSS/JS bytes match published package SHA256' $assets

 $script:stage='trusted graph wheel'
 Click-Element "[...document.querySelectorAll('.experiment-row')].find(e=>e.querySelector('strong')?.textContent.trim()==='加速度 (含 g)')"
 Wait-Dom "document.querySelectorAll('.graph canvas').length>0" 'Graph not rendered.'
 $position=Evaluate "(()=>{const e=document.querySelector('.graph canvas');e.scrollIntoView({block:'center'});const r=e.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2,scroll:scrollY,width:r.width}})()"
 Invoke-Cdp 'Input.dispatchMouseEvent' @{type='mouseWheel';x=$position.x;y=$position.y;deltaX=0;deltaY=-100}|Out-Null
 Start-Sleep -Milliseconds 300
 $scrollAfter=Evaluate 'scrollY'
 $browserIssues=@($script:browserErrors|Where-Object {($_|ConvertTo-Json -Depth 12 -Compress) -match 'passive|Unable to preventDefault|exceptionThrown'})
 if($browserIssues.Count -gt 0){throw ('Graph wheel raised console exception/intervention: '+($browserIssues|ConvertTo-Json -Depth 12 -Compress))}
 if([Math]::Abs($scrollAfter-$position.scroll) -gt 2){throw 'Graph wheel unexpectedly scrolled the document.'}
 $report.wheel=@{event='CDP trusted mouseWheel';before=$position.scroll;after=$scrollAfter;canvasWidth=$position.width;issues=$browserIssues;note='Empty unavailable-hardware graph: tests wheel interaction and passive-event errors, not measured-data zoom accuracy.'}
 Save-Screenshot 'windows-ux-wide-graph.png'
 Add-Check 'trusted canvas wheel cancels page scrolling without passive-listener error' $report.wheel
 $script:stage='device form without device activation'
 Click-Element "[...document.querySelectorAll('.app-sidebar nav button')].find(e=>/连接设备|Connect devices/.test(e.textContent))"
 Wait-Dom "document.querySelectorAll('.device-transports button').length===3" 'New device forms did not render.'
 $deviceForm=Evaluate "({transportChoices:[...document.querySelectorAll('.device-transports button')].map(e=>e.textContent),formValues:[...document.querySelector('.device-form-grid').querySelectorAll('input:not([type=checkbox]),select')].map(e=>e.value),details:document.querySelectorAll('.device-page details').length})"
 if(($deviceForm.formValues|Where-Object {$_ -ne ''}).Count -gt 0){throw 'Serial connection form unexpectedly guessed communication settings.'}
 Save-Screenshot 'windows-ux-wide-device-form.png'
 Add-Check 'device form renders without assumed serial settings or hardware scan' $deviceForm
 $script:stage='media controls without automatic activation'
 Click-Element "[...document.querySelectorAll('.app-sidebar nav button')].find(e=>/音频与摄像头|Audio & camera/.test(e.textContent))"
 Wait-Dom "!document.querySelector('.media-workspace').hidden && !!document.querySelector('.browser-speaker')" 'Media workspace did not render.'
 $mediaDom=Evaluate "({title:document.querySelector('.media-workspace h1')?.textContent,text:document.querySelector('.media-workspace')?.innerText,captureCalls:window.__mediaUse.capture,audioContexts:window.__mediaUse.contexts,enabledBanner:!!document.querySelector('.media-active'),volume:document.querySelector('.browser-speaker input[type=range]')?.value})"
 if($mediaDom.captureCalls -ne 0 -or $mediaDom.audioContexts -ne 0 -or $mediaDom.enabledBanner -or $mediaDom.text -notmatch '麦克风' -or $mediaDom.text -notmatch '相机' -or $mediaDom.text -notmatch '浏览器扬声器'){throw 'Media controls missing or activated without explicit consent.'}
 $report.media=$mediaDom
 Save-Screenshot 'windows-ux-wide-media.png'
 Add-Check 'browser microphone camera and speaker controls render with zero capture or AudioContext creation' $mediaDom
 Invoke-Cdp 'Runtime.evaluate' @{expression='document.title';returnByValue=$true}|Out-Null
 $hardwareRequests=@($script:requests|Where-Object {$_.url -match '/devices/(scan|connect|ble/experiment)|/media/(camera/probe|audio/devices|browser/.*/configure|browser/configure)'})
 if($hardwareRequests.Count -gt 0){throw 'UI verification unexpectedly requested hardware discovery or connection.'}
 $report.requests=@($script:requests);$report.hardwareRequests=$hardwareRequests
 $report.browserMessages=@($script:browserErrors)
 $report.status='passed';Write-Output 'Windows UX checks passed on the actual published package and installed Edge.'
} catch {
 $report.error=$_.Exception.Message;$report.failedStage=$script:stage
 Write-Output ('Windows UX check failed at '+$script:stage+': '+$report.error)
 if($socket -and $socket.State -eq [Net.WebSockets.WebSocketState]::Open){try{Save-Screenshot 'windows-ux-wide-failure.png'}catch{}}
} finally {
 if($socket){$socket.Dispose()}
 if($edgeProcess){try{if(!$edgeProcess.HasExited){& taskkill.exe /PID $edgeProcess.Id /T /F | Out-Null}}catch{$report.cleanup.edgeError=$_.Exception.Message}}
 # Only orphan processes carrying this task's unique profile directory are eligible for cleanup.
 try {$orphans=@(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'"|Where-Object {$_.CommandLine -and $_.CommandLine.Contains($edgeProfileDirectory)});foreach($orphan in $orphans){& taskkill.exe /PID $orphan.ProcessId /T /F | Out-Null}}catch{$report.cleanup.edgeOrphanError=$_.Exception.Message}
 if($server){try{if(!$server.HasExited){& taskkill.exe /PID $server.Id /T /F | Out-Null}}catch{$report.cleanup.serverError=$_.Exception.Message}}
 foreach($name in @('server.stdout.log','server.stderr.log','edge.stdout.log','edge.stderr.log')){try{$log=Join-Path $work $name;if(Test-Path -LiteralPath $log){Copy-Item -LiteralPath $log -Destination (Join-Path $SharedDirectory ('windows-ux-wide-'+$name)) -Force}}catch{}}
 try {Remove-Item -LiteralPath $work -Recurse -Force;$report.cleanup.temporaryDirectoryRemoved=$true}catch{$report.cleanup.temporaryDirectoryRemoved=$false;$report.cleanup.remainingDirectory=$work}
 try {$leftovers=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|Where-Object {$_.LocalPort -in @(37655,37656)});$report.cleanup.portsReleased=($leftovers.Count -eq 0)}catch{$report.cleanup.portsReleased='unknown'}
 if($report.status -eq 'passed' -and $report.cleanup.portsReleased -ne $true){$report.status='failed';$report.error='Validation processes did not release the dedicated ports.'}
 $report.checkedAt=[DateTimeOffset]::UtcNow.ToString('o')
 if(Test-Path -LiteralPath $SharedDirectory){[IO.File]::WriteAllText((Join-Path $SharedDirectory 'windows-ux-wide-validation.json'),($report|ConvertTo-Json -Depth 30),$utf8)}
 $report|ConvertTo-Json -Depth 30
}

if($report.status -ne 'passed'){exit 1}
