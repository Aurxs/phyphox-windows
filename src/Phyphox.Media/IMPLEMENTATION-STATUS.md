# Windows audio implementation

Implemented locally in the service, never through browser microphone APIs:

- Active capture/render endpoint enumeration and default endpoint marking.
- Shared-mode WASAPI capture using the endpoint's actual mix format and sample rate. PCM integer and IEEE float data are decoded, averaged across channels into mono, and emitted in batches carrying the actual rate, sample index, receive UTC, and receive monotonic clock.
- Bounded capture queue; overflow terminates the stream with a visible error instead of silently claiming a lossless recording.
- Shared-mode WASAPI playback with explicit source sample rate. Windows/NAudio may resample to the endpoint mix rate, both rates returned in the playback result.
- Mono direct samples copied to stereo, looping, sine/square/sawtooth tone, white noise, duration, linear pan, optional amplitude normalization, clipping, and cancellation.
- Non-Windows calls explicitly report unavailable. No device is fabricated.

`WasapiAudioService.PlayAsync` returns when finite playback finishes; loop playback runs until canceled. Multiple play/capture calls are not automatically coordinated: the session owner must serialize replacement/start/stop and retain cancellation tokens. Parameters are an immutable snapshot per playback request; dynamic parameter changes require restarting playback and do not yet maintain oscillator phase across requests.

Receive timestamps are not audio-device sample clock timestamps. Channel downmix is arithmetic average; anti-phase stereo can cancel. No channel selection, requested capture rate conversion, exclusive mode, microphone gain control, input/output latency compensation, or camera support is implemented here. Device removal/busy/permission errors propagate. Audio playback can produce audible sound only on Windows; none was played during development.

The mixer follows `official-reference/phyphox-android/.../AudioOutput.java` at Android commit `45fa55a0727653ccce439b86acb69c00cf435436` (4096-entry sine lookup, linear pan, duration and direct signal behavior). Negative/above-Nyquist tone frequencies and durations above 24h are rejected as explicit Windows request validation. Noise uses the runtime PRNG, and the float audio output path is not bit-identical to Android's final PCM16 conversion.

Dependency: `NAudio.Wasapi` 2.2.1 and transitive `NAudio.Core` 2.2.1, MIT, official repository https://github.com/naudio/NAudio. The exact v2.2.1 license is in `NAudio-LICENSE.txt` and copied into the published licenses directory. Package versions are pinned in the project file.

Focused verification completed on macOS: PCM decoding, finite direct sample duplication, loop, sine lookup and panning, noise channel routing, non-Windows capability rejection. These are unit-level checks only. Windows endpoint enumeration, recording, physical loopback, device unplug, latency, and sustained performance remain unverified.
