# Core implementation status

Source basis: official Android commit `45fa55a0727653ccce439b86acb69c00cf435436`, phyphox-docs `3dcfdae14f3c3a957208c367a026a0589f6dd702` spec and numerical fixtures. Ported code is GPL-3.0-or-later.

## Implemented and validated

All 54 analysis module entrypoints are implemented. The actual 117 official numerical fixtures pass with their original tolerances (no modified reference values), with zero failures and zero blocked fixtures. Additional focused checks cover lifecycle, requireFill default threshold, protected/selected clear groups, arbitrary-length FFT versus an independent DFT, Heaviside at zero, formula arity and XML DTD rejection.

The format audit reads all 167 valid/generated/invalid files: 127 accepted, 39 rejected, one skipped because it declares 1.21; zero mismatches against the official expected acceptance classifications. This is **parsing evidence only**, not runtime/device/interaction certification.

Parser includes namespaces (none, HTTP or HTTPS), case-insensitive element and enum handling while retaining case-sensitive attribute names, typed attribute checks, declared input/output components, analysis slot cardinality/type/ordering, graph pairing, link and translation checks, container references, numeric lexical rules and duplicate metadata last-wins. Unknown attributes remain tolerated according to official rules. `FormatSchema.json` is an embedded subset of official declarative rules, generated with `tools/generate_schema.rb`; runtime has no YAML dependency.

Runtime includes named buffers, bounded FIFO behavior, initial/static data, module static execution tracking, ordered kernels, consumption and append semantics including deprecated clear, cycle ranges, requireFill/dynamic threshold and first-run exemption, Tick scheduling, monotonic experiment time plus wall-clock mapping, grouped clearing, and parameter updates. Timer linearTime and offset1970 follow official mapping semantics. No synthetic device input exists.

## Explicit compatibility boundaries

- FFT uses managed double-precision radix-2/Bluestein and preserves arbitrary input length. Android native FFTW float32 bit identity and large-load performance are not certified.
- `info` requires an `IExperimentInfoProvider`. Any requested hardware key without a real provider value blocks start/run explicitly; unavailable battery, Wi-Fi or audio state is never invented.
- `imagedecode` uses pinned pure-managed StbImageSharp 2.30.16. PNG/JPEG/BMP/etc decoding comes from that library; the official opaque PNG numerical fixture passes. Embedded ICC-profile images are explicitly blocked pending color-managed conversion, rather than silently misreporting wide-gamut luminance. Decodes are capped at 16 megapixels. See the included license notice.
- Negative periodicity overlap/search minima are explicitly rejected; adaptive mode is ported but the official golden fixture only covers a user-specified range.
- Parser audit coverage does not prove every combination of attributes, hardware resource, translation, or multi-view behavior. Conventional saved .phyphox START/PAUSE events are restored in source order with Unix-millisecond timestamps; a final PAUSE fixes elapsed time and restores paused state. An unclosed final START preserves stored data but blocks computation/start until clear, because no final elapsed time can be inferred. Complete UI initialization remains a host integration responsibility.
- No Windows hardware, device/driver, performance, or full product acceptance is claimed by these tests.

## Integration

`ExperimentParser.Parse(xml, locale)` returns a definition; `ExperimentRuntime(definition, infoProvider?)` owns state. Caller serializes access. `RunCycle()` drives the direct numerical kernel; `Tick()` respects scheduling. `SetBuffer()` is a user operation; `ReceiveSamples()` appends real adapter samples without falsely raising an onUserInput event. `Buffers.Values` returns snapshots. Clear() resets ordinary buffers; ClearGroups() additionally resets selected groups; `_` is protected. Both reset experiment time once.

Rebuild schema from the repository root:

```sh
ruby phyphox-windows/src/Phyphox.Core/tools/generate_schema.rb official-reference/phyphox-docs/spec/{root,input,output,analysis,views,network}.yml > phyphox-windows/src/Phyphox.Core/FormatSchema.json
```
