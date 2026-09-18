# Browser media UI focused verification — 2026-09-18

Target: isolated local service http://127.0.0.1:37653, data directory `/private/tmp/phyphox-browser-media-validation`; named headless Playwright session `browser-media-qa`. The user session at port 37652 was not operated.

- After initial load and entering the media page: **0 getUserMedia calls, 0 AudioContext constructions, 0 configure requests**. Guards prevent actual media access during this UI check.
- Loaded the real official **声音频谱** definition through the isolated service. Its sole audio input (`index 0`, buffers `recording`, `rate`) was selected automatically; the microphone authorization button became available.
- A deliberately scripted `NotAllowedError` refusal exercised permission-failure handling. One explicit microphone-button click produced a visible **麦克风: QA: permission explicitly denied...** error, with **0 configure requests, 0 AudioContext constructions and 0 active video streams**. This is a mocked refusal-path test, not a real OS/browser permission-dialog or hardware test.
- `/browser-pcm-worklet.js` returned **200, text/javascript**, containing the expected processor registration. CSP retained **script-src 'self'**; no blob script allowance was added. This checks deployed resource delivery and CSP, not execution of a real audio graph.
- Switching between the experiment library and media page preserved the **same media DOM component**; its wrapper changed hidden state. No new media calls occurred.
- At **1920 × 1080**, no horizontal document overflow. Full-page screenshot visually reviewed: both acquisition cards, speaker controls, explicit error and collapsed Windows native controls readable without overlapping.

Artifacts: `browser-media-1920.png`, `browser-media-result.json`, and raw CLI evidence `browser-media-result.txt`.

Not executed: actual microphone/camera/speaker use, a live PCM→service transmission, playback, OS permission UI, Windows/browser hardware compatibility, latency/calibration, or performance tests. No generated signal was injected into experiment buffers. Only the isolated QA browser was closed; the local test service remains for its owner to manage.
