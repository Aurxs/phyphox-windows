import './mobile.css';

type MotionKind = 'accelerometer' | 'gyroscope' | 'linear_acceleration';
type Kind = MotionKind | 'audio' | 'camera';
type Sample = {t: number; x: number; y: number; z: number};
type Packet = Record<string, unknown>;
type Pending = {runId: string; sequence: number; sent: number};
type Queued = {packet: Packet; at: number; duration: number};
type Capture = {generation: number; stream?: MediaStream; context?: AudioContext; source?: MediaStreamAudioSourceNode; node?: AudioWorkletNode; gain?: GainNode; timer?: ReturnType<typeof setTimeout>; observed?: boolean};
type WakeLock = {release(): Promise<void>};
const motionKinds: MotionKind[] = ['accelerometer', 'gyroscope', 'linear_acceleration'];
const kinds: Kind[] = [...motionKinds, 'audio', 'camera'];
const root = document.querySelector<HTMLDivElement>('#app')!;
root.innerHTML = `<div class="brand">PHYPHOX WINDOWS · 手机传感器</div><h1>把手机连接到实验</h1>
<p>手机与 Windows 保持同一局域网，无需互联网。扫码打开本机 HTTPS 页面，按浏览器提示进入后授权；传感器数据只在设备间直接传输。</p>
<section class="card"><h2>连接</h2><p id="status" role="status" aria-live="polite">等待连接</p><div class="actions"><button id="connect" class="primary">连接 Windows</button><button id="stop" class="danger" disabled>停止并释放传感器</button></div><p id="notice" role="alert"></p></section>
<section class="card"><h2>授权当前实验需要的传感器</h2><p>手机麦克风 → 启用麦克风；手机相机颜色 → 启用摄像头；手机运动 → 启用运动传感器。首版每次绑定一种输入，授权后回到电脑绑定并开始。</p><div class="actions"><button id="motion" disabled>启用运动传感器</button><button id="audio" disabled>启用麦克风</button><button id="camera" disabled>启用摄像头</button></div><ul id="details"><li>尚未授权任何传感器</li></ul><video id="preview" autoplay muted playsinline hidden aria-label="摄像头本地预览"></video></section>
<p class="small">请保持此页面在前台。切换应用、锁屏或隐藏页面会停止采集。音频为单声道 PCM；图像默认 320 × 240、最高 5 帧/秒，过大的图像会跳过并显示计数。浏览器采样能力因设备而异。</p>`;
const el = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const status = el('status'), notice = el('notice'), details = el('details');
const connectButton = el<HTMLButtonElement>('connect'), stopButton = el<HTMLButtonElement>('stop');
const motionButton = el<HTMLButtonElement>('motion'), audioButton = el<HTMLButtonElement>('audio'), cameraButton = el<HTMLButtonElement>('camera');
const preview = el<HTMLVideoElement>('preview');
const fragment = new URLSearchParams(location.hash.slice(1));
const roomId = fragment.get('room') ?? '', joinToken = fragment.get('token') ?? '';
// Pairing secrets stay in this page's memory, never in requests, history, or storage.
history.replaceState(null, '', location.pathname + location.search);
fragment.delete('room'); fragment.delete('token');
let socket: WebSocket | undefined, peer: RTCPeerConnection | undefined, channel: RTCDataChannel | undefined;
let paired = false, connected = false, connecting = false, generation = 0, runEpoch = 0;
let runId: string | null = null, active = false, measuring = false, motionListening = false, motionPending = false;
let motionTimer: ReturnType<typeof setTimeout> | undefined;
let motionAttempt = 0, faulting = false;
let wakeLock: WakeLock | undefined;
let wakeAttempt = 0;
let captures: Partial<Record<'audio' | 'camera', Capture>> = {};
let capabilities: {motion: Record<MotionKind, boolean>; audio?: {sampleRate: number}; camera?: {width: number; height: number}} = {motion: {accelerometer: false, gyroscope: false, linear_acceleration: false}};
const sequences = new Map<Kind, number>(), pending = new Map<Kind, Pending>(), queues = new Map<Kind, Queued[]>();
let cameraSkipped = 0, packetsSent = 0;
const candidateQueue: RTCIceCandidateInit[] = [];
let signalChain = Promise.resolve();
function render() {
  motionButton.disabled = !connected || motionListening || motionPending;
  audioButton.disabled = !connected || !!captures.audio;
  cameraButton.disabled = !connected || !!captures.camera;
  connectButton.disabled = connecting || connected || !!socket || !roomId || !joinToken || !isSecureContext;
  stopButton.disabled = !connected && !connecting;
  const lines: string[] = [];
  if (motionPending) lines.push('运动传感器：等待真实数据…');
  if (motionListening) lines.push(`运动传感器：${motionKinds.filter(k => capabilities.motion[k]).map(k => ({accelerometer:'含重力加速度',gyroscope:'角速度',linear_acceleration:'线性加速度'})[k]).join('、') || '尚无可用数据'}`);
  if (captures.audio) lines.push(capabilities.audio ? `麦克风：${capabilities.audio.sampleRate} Hz · 单声道 PCM` : '麦克风：正在初始化…');
  if (captures.camera) lines.push(capabilities.camera ? `摄像头：${capabilities.camera.width} × ${capabilities.camera.height} · ≤5 帧/秒` : '摄像头：正在初始化…');
  lines.push(`已发送 ${packetsSent} 个数据包；跳过 ${cameraSkipped} 帧图像`);
  details.replaceChildren(...lines.map(text => {const li = document.createElement('li'); li.textContent = text; return li;}));
}
function send(packet: Packet): boolean {
  if (channel?.readyState !== 'open') return false;
  const json = JSON.stringify(packet);
  if (new TextEncoder().encode(json).length > 65536 || channel.bufferedAmount > 65536) {
    fault('传输容量不足，采集已停止。请减少传感器数量后重新启用。');
    return false;
  }
  try {channel.send(json); return true;} catch {fault('设备间连接无法发送数据，采集已停止。'); return false;}
}
function publishCapabilities() {if (connected) send({type:'capabilities', ...capabilities}); render();}
function signal(data: unknown) {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({type:'signal', data}));
  else if (!connected) throw new Error('配对连接已关闭，请重新扫码。');
}
function hostCandidate(candidate: string) {return /(?:^|\s)typ host(?:\s|$)/.test(candidate);}
function validateDescription(description: RTCSessionDescriptionInit) {
  if (description.type !== 'offer' || typeof description.sdp !== 'string' || description.sdp.length > 60000) throw new Error('无效的连接描述。');
  for (const line of description.sdp.split(/\r?\n/)) if (line.startsWith('a=candidate:') && !hostCandidate(line)) throw new Error('连接包含非局域网候选地址，已拒绝。');
}
async function receiveSignal(data: {description?: RTCSessionDescriptionInit; candidate?: RTCIceCandidateInit | null}, current: number) {
  if (!paired || current !== generation) return;
  if (!peer) {
    const rtc = new RTCPeerConnection({iceServers: [], iceTransportPolicy:'all'}); peer = rtc;
    rtc.onicecandidate = event => {
      if (current !== generation || !event.candidate || !hostCandidate(event.candidate.candidate)) return;
      try {signal({candidate:event.candidate.toJSON()});} catch {disconnect('本地配对服务已关闭，请重新扫码。');}
    };
    rtc.ondatachannel = event => {
      if (current !== generation || event.channel.label !== 'phone-v1' || !event.channel.ordered || event.channel.maxRetransmits !== null || event.channel.maxPacketLifeTime !== null || channel) {event.channel.close(); return;}
      channel = event.channel;
      channel.onopen = () => {if (current !== generation) return; connected = true; connecting = false; status.textContent = '已连接，请启用需要的传感器'; render(); publishCapabilities();};
      channel.onmessage = event => receiveControl(event.data);
      channel.onclose = () => {if (current === generation) disconnect('与 Windows 的连接已关闭，请重新扫码。');};
      channel.onerror = () => {if (current === generation) disconnect('设备间数据通道发生错误，请重新扫码。');};
    };
    rtc.onconnectionstatechange = () => {if (current === generation && (rtc.connectionState === 'failed' || rtc.connectionState === 'closed' || rtc.connectionState === 'disconnected')) disconnect('局域网直连已断开。请检查网络后重新扫码。');};
  }
  const rtc = peer;
  if (data.description) {
    validateDescription(data.description);
    await rtc.setRemoteDescription(data.description);
    if (current !== generation) return;
    for (const candidate of candidateQueue.splice(0)) await rtc.addIceCandidate(candidate);
    const answer = await rtc.createAnswer();
    if (current !== generation) return;
    await rtc.setLocalDescription(answer);
    if (current !== generation) return;
    signal({description:rtc.localDescription});
  } else if (data.candidate) {
    if (typeof data.candidate.candidate !== 'string' || !hostCandidate(data.candidate.candidate)) throw new Error('仅支持局域网直连。');
    if (rtc.remoteDescription) await rtc.addIceCandidate(data.candidate);
    else {if (candidateQueue.length >= 64) throw new Error('连接候选地址过多。'); candidateQueue.push(data.candidate);}
  }
}
function resetRun(nextId: string | null, nextActive: boolean, nextMeasuring: boolean) {
  runEpoch++; runId = nextId; active = nextActive && !!nextId; measuring = nextMeasuring;
  sequences.clear(); pending.clear(); queues.clear();
  captures.audio?.node?.port.postMessage({type:'run',epoch:runEpoch,active:active || !captures.audio.observed});
  status.textContent = active ? (measuring ? '实验正在采集' : '已连接 · 发送就绪探测，等待电脑开始实验') : '已连接 · 等待电脑选择输入';
  if (active) void acquireWakeLock(); else releaseWakeLock();
}
function receiveControl(value: unknown) {
  if (typeof value !== 'string' || value.length > 65536) {fault('电脑发送的数据格式无效。'); return;}
  try {
    const data = JSON.parse(value);
    if (data.type === 'run') {
      if (!(data.runId === null || typeof data.runId === 'string') || typeof data.active !== 'boolean' || (data.active && !data.runId)) throw new Error('无效的实验状态。');
      if (data.runId !== runId || data.active !== active || !!data.measuring !== measuring) resetRun(data.runId, data.active, !!data.measuring);
    } else if (data.type === 'ack') {
      const kind = data.kind as Kind, flight = pending.get(kind);
      if (flight && data.runId === flight.runId && data.sequence === flight.sequence) {pending.delete(kind); pump(kind);}
    } else if (data.type === 'ping' && typeof data.at === 'number' && Number.isFinite(data.at)) send({type:'pong',at:data.at});
    else if (data.type === 'capabilities-request') publishCapabilities();
    else if (data.type === 'release') {stopCaptures(); notice.textContent = 'Windows 已释放手机传感器，可重新授权。';}
    else if (data.type === 'stop') fault('Windows 已停止手机采集。');
  } catch {fault('电脑发送了无法识别的实验控制信息。');}
}
function enqueue(kind: Kind, packet: Packet, duration = 0) {
  if (!active || !runId) return;
  const queue = queues.get(kind) ?? [];
  queue.push({packet,at:performance.now(),duration}); queues.set(kind, queue);
  const limit = kind === 'audio' ? 500 : 250;
  if (queue.length > 256 || (kind !== 'camera' && (performance.now() - queue[0].at > limit || queue.reduce((sum, entry) => sum + entry.duration, 0) > limit))) {fault('网络或电脑处理过慢，采集已停止以避免缺样。'); return;}
  pump(kind);
}
function pump(kind: Kind) {
  if (!active || !runId || pending.has(kind)) return;
  const queued = queues.get(kind)?.shift(); if (!queued) return;
  const sequence = sequences.get(kind) ?? 0; sequences.set(kind, sequence + 1);
  pending.set(kind, {runId,sequence,sent:performance.now()});
  if (send({...queued.packet,runId,sequence})) packetsSent++;
}
function releaseCapture(kind: 'audio' | 'camera') {
  const capture = captures[kind]; if (!capture) return;
  delete captures[kind];
  if (capture.timer) clearTimeout(capture.timer);
  capture.stream?.getTracks().forEach(track => {track.onended = null; track.stop();});
  if (capture.node) {capture.node.port.onmessage = null; capture.node.disconnect();}
  capture.source?.disconnect(); capture.gain?.disconnect(); void capture.context?.close().catch(() => {});
  if (kind === 'camera') {preview.srcObject = null; preview.hidden = true; delete capabilities.camera;} else delete capabilities.audio;
}
function stopCaptures() {
  resetRun(null, false, false);
  window.removeEventListener('devicemotion', motionEvent); motionListening = false; motionPending = false; motionAttempt++;
  if (motionTimer) clearTimeout(motionTimer);
  capabilities.motion = {accelerometer:false,gyroscope:false,linear_acceleration:false};
  releaseCapture('audio'); releaseCapture('camera'); publishCapabilities();
}
function stopByUser(message: string) {
  stopCaptures();
  send({type:'stop',message});
  status.textContent = '仍与 Windows 连接 · 采集已停止';
  notice.textContent = message;
}
function fault(message: string) {
  if (faulting) return;
  faulting = true;
  // Use a small direct message so a saturated sender cannot recurse through send().
  if (channel?.readyState === 'open' && channel.bufferedAmount < 131072) {try {channel.send(JSON.stringify({type:'fault',message}));} catch { /* Closing connection. */ }}
  stopCaptures(); notice.textContent = message; faulting = false;
}
function disconnect(message: string) {
  generation++; connected = false; connecting = false; paired = false;
  stopCaptures();
  const oldChannel = channel, oldPeer = peer, oldSocket = socket;
  channel = undefined; peer = undefined; socket = undefined;
  if (oldChannel) {oldChannel.onclose = null; oldChannel.onerror = null; oldChannel.close();}
  oldPeer?.close(); oldSocket?.close(); candidateQueue.length = 0;
  status.textContent = '未连接'; notice.textContent = message;
  render(); connectButton.disabled = true;
}
async function acquireWakeLock() {
  const current = generation, attempt = ++wakeAttempt;
  try {
    const api = navigator as Navigator & {wakeLock?: {request(type: 'screen'): Promise<WakeLock>}};
    if (!wakeLock && api.wakeLock && !document.hidden) {
      const lock = await api.wakeLock.request('screen');
      if (current !== generation || attempt !== wakeAttempt || !active || document.hidden) await lock.release(); else wakeLock = lock;
    }
  } catch {notice.textContent = '无法保持屏幕常亮，请勿锁屏或切换应用。';}
}
function releaseWakeLock() {wakeAttempt++; const lock = wakeLock; wakeLock = undefined; void lock?.release().catch(() => {});}
function finiteVector(value: {x: number | null; y: number | null; z: number | null} | null): value is {x: number; y: number; z: number} {return !!value && [value.x,value.y,value.z].every(v => typeof v === 'number' && Number.isFinite(v));}
function motionEvent(event: DeviceMotionEvent) {
  if (!motionListening || document.hidden) return;
  // Event receipt is monotonic; never use Date.now() for the phone clock.
  const t = performance.now() / 1000;
  const values: Partial<Record<MotionKind, {x: number; y: number; z: number}>> = {};
  if (finiteVector(event.accelerationIncludingGravity)) values.accelerometer = event.accelerationIncludingGravity;
  if (finiteVector(event.acceleration)) values.linear_acceleration = event.acceleration;
  const rotation = event.rotationRate;
  if (rotation && [rotation.alpha,rotation.beta,rotation.gamma].every(v => typeof v === 'number' && Number.isFinite(v))) {
    // Browser device axes: beta around x, gamma around y, alpha around z.
    const radians = Math.PI / 180;
    values.gyroscope = {x:rotation.beta! * radians,y:rotation.gamma! * radians,z:rotation.alpha! * radians};
  }
  let changed = false;
  for (const kind of motionKinds) {
    const value = values[kind]; if (!value) continue;
    if (!capabilities.motion[kind]) {capabilities.motion[kind] = true; changed = true;}
    // DeviceMotion vector coordinates are prototype getters, not enumerable fields.
    const sample: Sample = {t,x:value.x,y:value.y,z:value.z}; enqueue(kind, {type:'motion',kind,samples:[sample]});
  }
  if (changed) {motionPending = false; publishCapabilities();}
}
async function enableMotion() {
  if (!connected || motionListening || motionPending) return;
  const current = generation, attempt = ++motionAttempt; motionPending = true; notice.textContent = ''; render();
  try {
    const api = window.DeviceMotionEvent as typeof DeviceMotionEvent & {requestPermission?: () => Promise<string>};
    if (!api) throw new Error('此浏览器不提供运动传感器接口。');
    // iOS requires this call directly in the user's click handler, before any await.
    if (api.requestPermission && await api.requestPermission() !== 'granted') throw new Error('运动传感器权限未获准。');
    if (current !== generation || attempt !== motionAttempt || !motionPending || !connected || document.hidden) return;
    motionListening = true; window.addEventListener('devicemotion', motionEvent);
    motionTimer = setTimeout(() => {
      if (current === generation && attempt === motionAttempt && motionPending) {
        window.removeEventListener('devicemotion', motionEvent); motionListening = false; motionPending = false; motionAttempt++;
        notice.textContent = '未收到真实运动数据，此设备或浏览器可能不支持；请检查权限后重试。'; render();
      }
    }, 6000);
    render();
  } catch (error) {if (current === generation && attempt === motionAttempt) {motionPending = false; notice.textContent = String(error); render();}}
}
function currentCapture(kind: 'audio' | 'camera', capture: Capture) {return captures[kind] === capture && capture.generation === generation && connected && !document.hidden;}
async function enableAudio() {
  if (!connected || captures.audio) return;
  const capture: Capture = {generation}; captures.audio = capture; notice.textContent = ''; render();
  try {
    if (!navigator.mediaDevices?.getUserMedia) throw new Error('此浏览器不支持麦克风采集。');
    const context = new AudioContext(); capture.context = context;
    const resume = context.resume().catch(() => {});
    const stream = await navigator.mediaDevices.getUserMedia({audio:{channelCount:{ideal:1},echoCancellation:false,noiseSuppression:false,autoGainControl:false},video:false});
    if (!currentCapture('audio',capture)) {stream.getTracks().forEach(track => track.stop()); return;}
    capture.stream = stream;
    stream.getTracks().forEach(track => {track.onended = () => {if (currentCapture('audio',capture)) fault('麦克风已断开或权限被撤销。');};});
    await resume;
    if (!currentCapture('audio',capture)) return;
    if (!context.audioWorklet || context.state !== 'running') throw new Error('无法启动音频处理，请使用支持 AudioWorklet 的浏览器。');
    await context.audioWorklet.addModule(new URL('../../phone-pcm-worklet.js', location.href).href);
    if (!currentCapture('audio',capture)) return;
    capture.source = context.createMediaStreamSource(stream);
    capture.node = new AudioWorkletNode(context,'phyphox-phone-pcm',{numberOfInputs:1,numberOfOutputs:1,outputChannelCount:[1]});
    capture.gain = context.createGain(); capture.gain.gain.value = 0;
    // Observe real PCM once before advertising capability, including in standby.
    capture.node.port.postMessage({type:'run',epoch:runEpoch,active:true});
    capture.node.port.onmessage = event => {
      if (!currentCapture('audio',capture)) return;
      const data = event.data;
      if (data.epoch !== runEpoch) return;
      if (data.type === 'overrun') {fault('音频处理未能及时接收数据，已停止以避免缺样。'); return;}
      if (!(data.samples instanceof Float32Array)) return;
      capture.node?.port.postMessage({type:'ack',epoch:runEpoch});
      if (!capture.observed) {
        capture.observed = true; if (capture.timer) clearTimeout(capture.timer);
        capabilities.audio = {sampleRate:context.sampleRate}; publishCapabilities();
        if (!active) capture.node?.port.postMessage({type:'run',epoch:runEpoch,active:false});
      }
      enqueue('audio',{type:'audio',sampleRate:context.sampleRate,samples:Array.from(data.samples)},data.samples.length / context.sampleRate * 1000);
    };
    capture.source.connect(capture.node); capture.node.connect(capture.gain); capture.gain.connect(context.destination);
    capture.timer = setTimeout(() => {if (currentCapture('audio',capture) && !capture.observed) {releaseCapture('audio'); publishCapabilities(); notice.textContent = '麦克风未提供真实音频数据，请重新授权。';}},6000);
  } catch (error) {if (currentCapture('audio',capture)) {releaseCapture('audio'); publishCapabilities(); notice.textContent = `无法启用麦克风：${String(error)}`;}}
}
async function enableCamera() {
  if (!connected || captures.camera) return;
  const capture: Capture = {generation}; captures.camera = capture; notice.textContent = ''; render();
  try {
    if (!navigator.mediaDevices?.getUserMedia) throw new Error('此浏览器不支持摄像头采集。');
    const stream = await navigator.mediaDevices.getUserMedia({video:{width:{ideal:320},height:{ideal:240},frameRate:{ideal:5,max:5},facingMode:{ideal:'environment'}},audio:false});
    if (!currentCapture('camera',capture)) {stream.getTracks().forEach(track => track.stop()); return;}
    capture.stream = stream;
    stream.getTracks().forEach(track => {track.onended = () => {if (currentCapture('camera',capture)) fault('摄像头已断开或权限被撤销。');};});
    preview.srcObject = stream; preview.hidden = false;
    await Promise.race([preview.play(), new Promise<never>((_, reject) => setTimeout(() => reject(new Error('摄像头预览启动超时。')),6000))]);
    if (!currentCapture('camera',capture)) return;
    const canvas = document.createElement('canvas'), context = canvas.getContext('2d',{colorSpace:'srgb'});
    if (!context) throw new Error('无法创建图像处理画布。');
    const started = performance.now();
    const frame = () => {
      if (!currentCapture('camera',capture)) return;
      try {
        if (preview.readyState >= 2 && preview.videoWidth && preview.videoHeight) {
          const scale = Math.min(320 / preview.videoWidth,240 / preview.videoHeight,1);
          canvas.width = Math.max(1,Math.round(preview.videoWidth * scale)); canvas.height = Math.max(1,Math.round(preview.videoHeight * scale));
          if (!capture.observed) {capture.observed = true; capabilities.camera = {width:canvas.width,height:canvas.height}; publishCapabilities();}
          if (active) {
            if (pending.has('camera')) cameraSkipped++;
            else {
              context.drawImage(preview,0,0,canvas.width,canvas.height);
              const dataBase64 = canvas.toDataURL('image/jpeg',0.65).split(',')[1];
              if (dataBase64.length > 60000) cameraSkipped++;
              else enqueue('camera',{type:'camera',mimeType:'image/jpeg',dataBase64,width:canvas.width,height:canvas.height,t:performance.now() / 1000});
            }
          }
        } else if (!capture.observed && performance.now() - started > 6000) throw new Error('摄像头未提供真实图像。');
        capture.timer = setTimeout(frame,200);
      } catch (error) {if (currentCapture('camera',capture)) {releaseCapture('camera'); publishCapabilities(); notice.textContent = String(error);}}
    };
    frame();
  } catch (error) {if (currentCapture('camera',capture)) {releaseCapture('camera'); publishCapabilities(); notice.textContent = `无法启用摄像头：${String(error)}`;}}
}
connectButton.onclick = () => {
  if (!isSecureContext || !roomId || !joinToken || connecting || connected) return;
  connecting = true; notice.textContent = ''; status.textContent = '正在配对…'; render();
  const current = generation;
  const url = new URL('/signal',location.href); url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const ws = new WebSocket(url); socket = ws;
  let timeout = setTimeout(() => {if (current === generation && !connected) disconnect('等待电脑确认超时，请重新扫码并在 Windows 上确认接入。');},65000);
  ws.onopen = () => {if (current === generation) {ws.send(JSON.stringify({type:'join',version:1,roomId,joinToken,name:'手机浏览器'})); status.textContent = '等待 Windows 确认这部手机…';}};
  ws.onmessage = event => {
    if (current !== generation) return;
    if (typeof event.data !== 'string' || event.data.length > 65536) {disconnect('配对服务返回无效数据。'); return;}
    try {
      const message = JSON.parse(event.data);
      if (message.type === 'paired') {
        paired = true; status.textContent = '已配对，正在建立局域网直连…';
        clearTimeout(timeout);
        timeout = setTimeout(() => {if (current === generation && !connected) disconnect('无法建立局域网直连。请检查同一网络、电脑防火墙或 Wi-Fi 客户端隔离后重新扫码。');},30000);
      }
      else if (message.type === 'signal' && paired) signalChain = signalChain.then(() => receiveSignal(message.data,current)).catch(() => {if (current === generation) disconnect('无法协商局域网连接，请重新扫码。');});
      else if (message.type === 'error') disconnect(typeof message.message === 'string' ? message.message.slice(0,500) : '配对失败。');
      else if (message.type === 'peer-left' && !connected) disconnect('电脑已退出配对，请重新扫码。');
    } catch {disconnect('配对消息无法读取。');}
  };
  ws.onclose = () => {clearTimeout(timeout); if (current !== generation) return; if (connected) notice.textContent = '配对服务已断开；已建立的局域网连接继续工作。'; else disconnect('配对连接已关闭，请重新扫码。');};
  ws.onerror = () => {if (current === generation && !connected) disconnect('无法连接配对服务，请检查网络和 HTTPS 配置。');};
};
motionButton.onclick = () => void enableMotion(); audioButton.onclick = () => void enableAudio(); cameraButton.onclick = () => void enableCamera();
stopButton.onclick = () => {if (connecting) disconnect('已取消连接，请重新扫码。'); else stopByUser('已停止并释放传感器。需要继续时重新启用传感器，再到电脑绑定输入。');};
document.addEventListener('visibilitychange',() => {if (document.hidden && connected) stopByUser('页面已进入后台，传感器已停止。返回后重新启用，再到电脑绑定输入。'); else if (document.hidden && connecting) disconnect('配对页面已进入后台，请重新扫码连接。');});
window.addEventListener('pagehide',() => {fault('手机页面已关闭。'); disconnect('页面已关闭。');});
// Scanning a fresh QR into this tab may only change the fragment, without
// restarting the module. Reload explicitly so the next page reads the new
// one-use credentials; no sensor authorization is carried into the new page.
window.addEventListener('hashchange',() => {
  const next = new URLSearchParams(location.hash.slice(1));
  if (!next.get('room') || !next.get('token')) return;
  disconnect('正在打开新的手机连接…');
  location.reload();
});
setInterval(() => {
  if (active) {
    const now = performance.now();
    for (const [kind,flight] of pending) if (now - flight.sent > (kind === 'audio' ? 500 : kind === 'camera' ? 1500 : 250)) {fault('电脑未及时确认数据，采集已停止以避免缺样。'); break;}
  }
  render();
},100);
if (!isSecureContext) notice.textContent = '需要可信 HTTPS 页面才能授权传感器。请使用 Windows 提供的安全二维码。';
else if (!roomId || !joinToken) notice.textContent = '此链接缺少配对信息，请扫描 Windows 设置中生成的二维码。';
render();
