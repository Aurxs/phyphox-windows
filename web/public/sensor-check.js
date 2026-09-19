/* SPDX-License-Identifier: GPL-3.0-only */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const hasMedia = typeof navigator.mediaDevices?.getUserMedia === 'function';
  const hasMotion = typeof window.DeviceMotionEvent !== 'undefined';
  const motionPermission = typeof window.DeviceMotionEvent?.requestPermission === 'function';
  const results = {
    schemaVersion: 1,
    environment: { protocol: location.protocol, secureContext: window.isSecureContext, getUserMedia: typeof navigator.mediaDevices?.getUserMedia, deviceMotionEvent: hasMotion, motionRequestPermission: motionPermission },
    microphone: { interfaceExposed: hasMedia, permission: 'not-requested', receivedData: false, status: 'not-tested' },
    camera: { interfaceExposed: hasMedia, permission: 'not-requested', receivedData: false, status: 'not-tested' },
    motion: { interfaceExposed: hasMotion, permission: 'not-requested', receivedData: false, status: 'not-tested' }
  };
  const sessions = { microphone: 0, camera: 0, motion: 0 };
  const streams = {};
  let audio, audioFrame, videoFrame, videoFrameNative = false, motionTimer, cameraTimer;
  const preview = $('preview');
  function output() { $('result').value = JSON.stringify(results, null, 2); }
  function status(kind, value, text) { results[kind].status = value; const node = $(kind + '-status'); if (node.textContent !== text) node.textContent = text; output(); }
  function error(kind, e) {
    const name = e?.name || 'Error', message = e?.message || String(e);
    results[kind].error = { name, message: message.replace(/(?:https?|wss?):\/\/[^\s]+/g, '[网址已隐去]').replace(/\b(?:\d{1,3}\.){3}\d{1,3}\b/g, '[地址已隐去]') };
    if (name === 'NotAllowedError') results[kind].permission = 'denied-or-blocked';
    status(kind, 'error', name + ': ' + message);
  }
  for (const [label, value] of Object.entries({ '当前来源（不导出）': location.origin, '安全上下文': String(window.isSecureContext), 'getUserMedia 类型': results.environment.getUserMedia, 'DeviceMotionEvent 存在': String(hasMotion), '运动权限请求接口': String(motionPermission) })) {
    const dt = document.createElement('dt'), dd = document.createElement('dd'); dt.textContent = label; dd.textContent = value; $('environment').append(dt, dd);
  }
  $('context-note').textContent = window.isSecureContext ? '当前网页被浏览器识别为安全上下文；仍需逐项授权并验证实际数据。' : '当前不是安全上下文。若接口未暴露，网页脚本无法弹出对应授权；可再用电脑给出的 HTTPS 地址比较。';
  function release(kind) {
    sessions[kind]++;
    streams[kind]?.getTracks().forEach(track => track.stop()); delete streams[kind];
    if (kind === 'microphone') { cancelAnimationFrame(audioFrame); audio?.close().catch(() => {}); audio = null; $('level').value = 0; $('level-value').textContent = '已停止'; }
    if (kind === 'camera') { clearTimeout(cameraTimer); if (videoFrameNative) preview.cancelVideoFrameCallback?.(videoFrame); else cancelAnimationFrame(videoFrame); preview.pause(); preview.srcObject = null; }
    if (kind === 'motion') { window.removeEventListener('devicemotion', onMotion); clearTimeout(motionTimer); }
    $(kind).disabled = false;
  }
  function begin(kind) {
    release(kind); results[kind] = { interfaceExposed: kind === 'motion' ? hasMotion : hasMedia, permission: 'requested', receivedData: false, status: 'requesting' }; $(kind).disabled = true;
    status(kind, 'requesting', '正在请求权限，请查看浏览器提示…'); return sessions[kind];
  }
  async function media(kind) {
    const token = begin(kind);
    if (!hasMedia) { results[kind].permission = 'not-requested'; status(kind, 'unavailable', '接口未暴露：navigator.mediaDevices.getUserMedia 不可用。'); $(kind).disabled = false; return; }
    try {
      // Construct/resume AudioContext during the gesture for mobile browsers.
      if (kind === 'microphone') {
        const Audio = window.AudioContext || window.webkitAudioContext;
        if (!Audio) throw new Error('AudioContext 不可用，无法验证实际音频样本');
        audio = new Audio(); await audio.resume();
        if (token !== sessions[kind]) return;
      }
      const stream = await navigator.mediaDevices.getUserMedia(kind === 'microphone' ? { audio: true } : { video: true });
      if (token !== sessions[kind]) { stream.getTracks().forEach(track => track.stop()); return; }
      streams[kind] = stream; results[kind].permission = 'granted'; status(kind, 'waiting', '已授权，等待实际数据…');
      stream.getTracks().forEach(track => track.addEventListener('ended', () => { if (token !== sessions[kind]) return; release(kind); status(kind, 'ended', '浏览器已结束采集'); }));
      if (kind === 'microphone') {
        const node = audio.createAnalyser(); node.fftSize = 1024; audio.createMediaStreamSource(stream).connect(node);
        const samples = new Float32Array(node.fftSize), context = audio;
        let shownLevel = 0, lastDisplay = -Infinity, lastTick = performance.now();
        const tick = (now = performance.now()) => {
          if (token !== sessions[kind]) return;
          node.getFloatTimeDomainData(samples);
          const rms = Math.sqrt(samples.reduce((sum, value) => sum + value * value, 0) / samples.length);
          // Display -72..0 dBFS. A linear rms*5 hid normal low-level microphone
          // signals at the bottom few percent; only the display is scaled.
          const dbfs = rms > 0 && Number.isFinite(rms) ? 20 * Math.log10(rms) : -Infinity;
          const running = context.state === 'running', track = stream.getAudioTracks()[0];
          const target = running && !track?.muted ? Math.max(0, Math.min(1, (dbfs + 72) / 72)) : 0;
          const elapsed = Math.max(0, Math.min(100, now - lastTick)); lastTick = now;
          shownLevel += (target - shownLevel) * (1 - Math.exp(-elapsed / (target > shownLevel ? 35 : 180)));
          $('level').value = shownLevel;
          if (now - lastDisplay >= 150) {
            $('level-value').textContent = !running ? '音频处理暂停，请重新申请麦克风' : track?.muted ? '麦克风暂时没有提供数据' : !Number.isFinite(dbfs) ? '静音 / 暂无非零样本' : `${dbfs < -72 ? '< −72' : dbfs.toFixed(1)} dBFS`;
            lastDisplay = now;
          }
          // Zero-filled output can mean silence or unavailable input: do not call it working.
          if (context.state === 'running' && samples.some(value => Number.isFinite(value) && value !== 0) && !results[kind].receivedData) { results[kind].receivedData = true; status(kind, 'working', '已收到非零音频样本，音量条实时更新。'); }
          else if (!results[kind].receivedData && $(kind + '-status').textContent !== '已授权，尚未收到非零音频样本；请对着手机说话。') status(kind, 'waiting', '已授权，尚未收到非零音频样本；请对着手机说话。');
          audioFrame = requestAnimationFrame(tick);
        }; tick();
      } else {
        preview.srcObject = stream;
        const received = () => { if (token !== sessions[kind]) return; results[kind].receivedData = true; status(kind, 'working', '已收到摄像头画面，正在本机预览。'); clearTimeout(cameraTimer); };
        videoFrameNative = typeof preview.requestVideoFrameCallback === 'function';
        if (videoFrameNative) videoFrame = preview.requestVideoFrameCallback(received);
        else { const tick = () => { if (token !== sessions[kind]) return; const count = preview.getVideoPlaybackQuality?.().totalVideoFrames ?? preview.webkitDecodedFrameCount ?? 0; if (count > 0) received(); else videoFrame = requestAnimationFrame(tick); }; videoFrame = requestAnimationFrame(tick); }
        cameraTimer = setTimeout(() => { if (token === sessions[kind] && !results[kind].receivedData) status(kind, 'waiting', '已授权，但尚未验证到实际视频帧。'); }, 5000);
        await preview.play();
      }
    } catch (e) { if (token !== sessions[kind]) return; release(kind); error(kind, e); }
  }
  function onMotion(event) {
    const values = {};
    for (const key of ['acceleration', 'accelerationIncludingGravity', 'rotationRate']) {
      const input = event[key], fields = key === 'rotationRate' ? ['alpha', 'beta', 'gamma'] : ['x', 'y', 'z'];
      values[key] = Object.fromEntries(fields.map(field => [field, Number.isFinite(input?.[field]) ? input[field] : null]));
    }
    $('motion-values').textContent = JSON.stringify(values, null, 2);
    if (Object.values(values).some(group => Object.values(group).some(value => value !== null))) {
      results.motion.receivedData = true; results.motion.availableComponents = Object.fromEntries(Object.entries(values).map(([key, group]) => [key, Object.entries(group).filter(([, value]) => value !== null).map(([field]) => field)]));
      clearTimeout(motionTimer); status('motion', 'working', '已收到有效运动数据；缺失分量显示为 null。');
    }
  }
  $('motion').onclick = async () => {
    const token = begin('motion');
    if (!hasMotion) { results.motion.permission = 'not-requested'; status('motion', 'unavailable', 'DeviceMotionEvent 接口未暴露。'); $('motion').disabled = false; return; }
    try {
      // Call synchronously from this click handler, before any other await.
      const permission = motionPermission ? await window.DeviceMotionEvent.requestPermission() : 'not-required-by-api';
      if (token !== sessions.motion) return;
      results.motion.permission = permission;
      if (permission === 'denied') { status('motion', 'denied', '运动传感器权限被拒绝。'); $('motion').disabled = false; return; }
      window.addEventListener('devicemotion', onMotion);
      status('motion', 'waiting', '等待有效数据，请轻轻移动手机…');
      motionTimer = setTimeout(() => { if (token === sessions.motion && !results.motion.receivedData) status('motion', 'waiting', '尚未收到有效数据；接口存在或授权成功不代表采集成功。'); }, 5000);
    } catch (e) { if (token !== sessions.motion) return; release('motion'); error('motion', e); }
  };
  function stop() { for (const kind of ['microphone', 'camera', 'motion']) { const active = $(kind).disabled; release(kind); if (active) status(kind, 'stopped', results[kind].receivedData ? '已停止；本轮已验证收到数据。' : '已停止；本轮未验证到数据。'); } }
  $('microphone').onclick = () => media('microphone'); $('camera').onclick = () => media('camera'); $('stop').onclick = stop;
  window.addEventListener('pagehide', stop); document.addEventListener('visibilitychange', () => { if (document.hidden) stop(); });
  $('copy').onclick = async () => { output(); try { if (!navigator.clipboard?.writeText) throw new Error('剪贴板接口不可用'); await navigator.clipboard.writeText($('result').value); $('export-status').textContent = '已复制本地结果。'; } catch { $('result').focus(); $('result').select(); $('export-status').textContent = '自动复制不可用，结果已选中，请使用系统复制菜单。'; } };
  $('download').onclick = () => { output(); const url = URL.createObjectURL(new Blob([$('result').value], { type: 'application/json' })); const link = document.createElement('a'); link.href = url; link.download = 'phyphox-sensor-check.json'; document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); $('export-status').textContent = '已请求下载本地结果。'; };
  output();
})();
