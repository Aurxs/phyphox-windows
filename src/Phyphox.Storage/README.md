# Storage and deterministic replay

Implemented without SQLite or a database dependency. The host owns library/catalog policy and serializes access to live runtime state.

## Import

`DelimitedImporter.Import(text, options)` supports comma, semicolon, and tab delimiters, quoted fields (including escaped quotes/newlines), explicit zero-based column-to-buffer mappings, unit labels and explicit scale/offset. Numeric parsing uses invariant decimal notation. Ragged rows and invalid numeric cells fail with row/column information; they do not become zero. Time comes from an explicitly selected nondecreasing time column or supplied sample rate. Without either, the result explicitly has no time buffer. Column time is interpreted as seconds; convert source units before import or provide a scaled source column.

`WaveImporter.Import(stream, prefix)` supports little-endian RIFF PCM 8/16/24/32-bit and IEEE float32/64, including matching WAVE_FORMAT_EXTENSIBLE PCM/float subtypes. It validates sample rate, block alignment, byte rate and chunk lengths. Output channels are normalized PCM buffers `<prefix>1`, `<prefix>2`, etc. The `time` buffer is derived from the file's sample rate, not guessed. Compressed codecs and unsupported valid-bit layouts are rejected. Import is offline and never opens an audio device.

## Snapshots

`SessionSnapshot.Capture(runtime)` must be called under the host's session lock. It copies buffer arrays, source XML, experiment time and recorded time mappings. `SnapshotStore(directory)` accepts only simple snapshot IDs, writes a temporary file in the same directory, flushes it, and atomically renames it. Failed writes never report a committed snapshot. `LoadAsync(id)` validates the schema; `snapshot.Restore()` creates a **paused data snapshot**, not a live device/engine checkpoint. No devices reconnect, and dynamic internal execution state is not claimed to survive.

## Replay JSONL v2

Header:

```json
{"schema":2,"kind":"phyphox-input-cycle-journal","sourceXml":"<phyphox ...>","completeReplay":true,"engineVersion":"phyphox-windows-core-v1"}
```

Subsequent records have a contiguous `sequence`, beginning at 1:

- `kind: "input"`: exact engine ingress `buffers` mapping names to numeric arrays. Default is append. Optional `replaceBuffers` names the buffers replaced by this ingress batch. These writes do not count as user input.
- `kind: "command"`: `command` object containing `command` (`start`, `pause`, `stop`, `clear`, `set`, `append`), optional `buffer`, `values`, `groups`. Only accepted commands belong in the journal. Device output commands cannot be executed by the replay engine.
- `kind: "cycle"`: **pre-execution** `cycle` index, `experimentTime`, `linearTime`, `offset1970`, `linearOffset1970`, all exact values used by that run. Optional `info` captures actual information-provider keys. All recorded info keys needed by `start` must also be supplied on that start event, since start performs availability checks.
- `kind: "end"`: mandatory final record. A crash-truncated file without this marker is refused as incomplete.

IEEE NaN/Infinity use the serializer's quoted named floating values. The host must record all initial view-default writes, the pre-run analysis cycle, user writes, and ingress batches in the exact order submitted to the single session writer. Merely writing commands and completed-cycle timestamps cannot create a complete journal. If recording begins after initialization, either include its exact initialization history or declare it incomplete; a source XML alone cannot reconstruct omitted writes.

`ReplayEngine.ReadJsonLines(reader)` validates the header and reads events. `ReplayEngine.Replay(journal)` checks sequence and timing completeness, drives the actual Core kernel on the recorded schedule, and returns recorded-input recomputation results. Missing sample batches cannot be reconstructed, and old schema1 `completeReplay:false` logs are rejected. Run replay on its own runtime: it has no device adapters, and `HardwareOutputEnabled` is always false. Results retain recorded-data provenance and are not physical-device acceptance. Treat the returned runtime as a replay result; create a paused data snapshot if transferring results into a live host session.

## Verification

Focused executable tests cover quoted CSV/TSV, malformed rows/quotes/numbers, explicit/no-time behavior, a real PCM WAV byte stream and metadata, numerical replay through Core, JSONL roundtrip, rejection of old/incomplete and hardware events, atomic snapshot roundtrip, and ID path containment. No Windows device, disk-failure injection, large-load, or SQLite catalog verification is claimed.
