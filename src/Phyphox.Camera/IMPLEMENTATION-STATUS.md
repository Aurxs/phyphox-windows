# Windows camera module status

`WindowsCameraService` uses OpenCV's explicitly selected `MSMF` (Microsoft Media Foundation) backend. It never requests browser camera access and never synthesizes depth data. No camera has been opened during development.

## Public behavior

- `ProbeAsync(maximumIndex)` is an **opt-in active probe**: it opens indices `0..maximumIndex-1` (maximum 16), reads one frame, and reports actual dimensions and driver-reported FPS. It may illuminate the camera LED. It is not passive device enumeration, does not determine USB versus built-in origin, and does not supply a stable PnP identity. Indices can change when devices are added/removed.
- `CaptureAsync(request)` owns one camera until completion, captures in a worker thread, returns service-encoded JPEG preview plus numerical ROI analysis, actual dimensions, driver-reported FPS and reception timestamps.
- Requested width/height/FPS are requests, not guarantees; results always describe the actual frame. FPS=0 means the backend did not provide a valid nominal rate. It is not measured throughput.
- Explicit camera controls use set + diagnostic Get, but the pinned OpenCV MSMF implementation returns the property default from GetRange rather than trustworthy current state. Accepted writes are marked `backend-default-only-unverified`; Required controls always fail. These values must not be displayed as verified current controls.
- Controls use backend-specific units. In particular OpenCV exposure values are not automatically converted into seconds, ISO, or aperture metadata.
- The queue contains at most four processed frames. Overflow faults capture rather than silently replacing measurement data. Preview-only throttling belongs to the consumer.
- Cancellation is checked between native calls. OpenCV's synchronous MSMF open/read cannot be reliably interrupted by a CancellationToken. Shutdown waits five seconds and reports a timeout if native I/O is stuck; the worker retains ownership until native I/O returns, avoiding unsafe concurrent native disposal. Process isolation/native async cancellation is a remaining reliability improvement before unrestricted production use.

## Numerical semantics

CPU analysis implements source-backed sRGB linearization, Rec.709 luma and luminance weights, per-pixel HSV with circular mean hue (degrees), average saturation/value, and row/column averaged linear-luminance spectrum. Spectra are indexed in the unrotated Windows frame pixel coordinates. ROI may be an explicit in-frame pixel rectangle or a normalized rectangle resolved against each actual captured frame; no assumed requested resolution is used for normalized coordinates.

These formulas come from official Android `camera/analyzer/{LuminanceAnalyzer,HSVAnalyzer,SpectroscopyAnalyzer}.java` at commit `45fa55a0727653ccce439b86acb69c00cf435436`. CPU arithmetic does not emulate the Android GPU's multi-pass packing/quantization or mobile orientation transforms, so byte-exact numerical equivalence has not been established.

Raw linear luminance and spectrum are relative values, not lux or calibrated wavelength intensity. Exposure-corrected fields are absent unless explicit valid physical metadata is supplied. When supplied, the correction follows the official expression `2^apertureValue / 2 * 100/ISO * (1e9/60)/shutterNanoseconds`; the caller must know what these values represent and must not derive them from arbitrary control readbacks. Spectroscopy needs optical hardware, wavelength and intensity calibration. Ordinary UVC input does not imply calibrated photometry.

## Dependencies and distribution

Pinned NuGet packages: OpenCvSharp4 4.13.0.20260627 and OpenCvSharp4.runtime.win 4.13.0.20260627. The runtime package is selected for Windows builds or explicit `-p:RuntimeIdentifier=win-x64`. No macOS native runtime is installed/loaded. Managed package source commit: `b161e7e012f5101f6d5dc68a835c59db6cc88b18`; underlying OpenCV 4.13.0. Apache-2.0 licenses are copied from `licenses/` into published output.

The native runtime package includes an optional FFmpeg DLL, though this service only uses MSMF. Full bundled third-party notices/source obligations still require release packaging review, or omission of unused FFmpeg from the final distribution. Inspecting the x64 OpenCvSharpExtern PE import table found Windows system DLL imports including MFPlat/MF/MFReadWrite/d3d11 and no direct VC runtime import. This is not a clean-machine load test. Windows N editions or stripped systems can lack Media Foundation.

## Verification

Passed pure managed tests: Rec.709 weights, sRGB transfer function, hue/saturation, ROI spectrum, exposure correction, invalid ROI, explicit non-Windows rejection. Tests did not instantiate OpenCV native objects, activate a camera, or display an image.

Still unverified: Windows 10/11 native loading; camera permission, enumeration and hot unplug; control effects; exposure and spectrum calibration; sustained 4C8G performance; driver stalls and real shutdown behavior. Depth, device-specific camera SDKs and stable device identifiers are not implemented.

## XML session bridge

`MediaCoordinator` now maps normal-camera XML scalar outputs (luma, hue, saturation, value, experiment time) and exposure-corrected luminance, spectrum, and physical exposure fields when an explicit verified fixed-exposure profile supplies actual metadata. Spectrum containers replace; scalar fields append. It waits for the first real camera frame during start and retains the latest JPEG for preview.

`VerifiedAutoExposureMode` is an external profile declaration backed by real-device verification, not runtime attestation. It defaults to unknown and is never inferred from MSMF Get. It must be revalidated if the camera configuration or device changes. The coordinator does not issue camera control writes under this declaration. More advanced AE strategies, locked physical settings and unverified exposure demands are explicit capability blockers. Default camera selection is index 0, with no fallback to another camera.

The concrete control limitation was verified against https://github.com/opencv/opencv/blob/4.13.0/modules/videoio/src/cap_msmf.cpp : `readComplexPropery` obtains `Get`, then always overwrites with `GetRange` defaults; the exposure-mode getter compares the resulting property value against an auto flag. A corrected native control bridge or a verified patched OpenCV build is required before automatic control verification can be promised.

Additional pure mapping tests passed: normalized ROI at actual dimensions, scalar/time mappings, missing-clock rejection, physical-metadata gating, calibrated spectrum replacement, and preflight of three official audio experiments. No devices were opened.

## Integrated package validation, 2026-09-18
The final x64 portable package excludes the optional FFmpeg DLL. On the user's Windows 11 ARM VM (x64 emulation, ordinary user, no dotnet on PATH), NativeBackendInfo successfully loaded the native DLL and returned OpenCV 4.13.0. No camera was opened. Earlier hardware, control and calibration limitations remain.
