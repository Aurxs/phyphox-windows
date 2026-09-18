# Device output and BLE experiment-transfer bridge

## Integration

- Register `app.MapDeviceTransferEndpoints()` after the existing authorization middleware. Both endpoints are POST, take `{deviceId}`, and only accept a device actually found by DeviceManager scanning. `/api/v1/devices/ble/experiment/download` returns CRC-verified XML/ZIP bytes; `/import` additionally invokes the existing safe library importer and never runs the experiment.
- Keep one `OutputCoordinator(devices)` per experiment session. On experiment replacement, stop it and clear bindings. Expose `Bind(definition, DeviceOutputBinding)` while stopped. Include `CapabilityIssues(definition)` in preflight and remove only the generic BLE-output blocker that this coordinator replaces.
- Await `StartAsync` outside the experiment lock, before accepting the running state. It verifies connected GATT characteristic/write capabilities and executes authored `<config>` writes once for this explicit start.
- After a successful analysis cycle, call `EnqueueAfterAnalysis` under the experiment state lock with snapshot and buffer-consumption callbacks. It does not perform device I/O on the analysis thread. `keep=false` buffers clear only after queue acceptance.
- Pass button trigger IDs to `RequestTrigger`. The next analysis boundary handles all requested triggers once. USB packets require an explicit trigger unless `SendOnEachAnalysis` was deliberately selected.
- Await `StopAsync` outside the state lock on pause/stop/unload/fault. It cancels queued/in-flight work and clears pending items. Fault handlers must update/schedule lifecycle work rather than synchronously waiting for this same output worker.
- Record `WriteCompleted` and `Faulted` events. A successful write means the transport accepted/completed the write operation; it is not proof the physical instrument executed the intended action. An interrupted in-flight write can have an uncertain result and is never retried automatically.

## Protocol boundaries

Original BLE output requires an explicit connected device binding and an output index. Characteristic UUIDs, byte conversions, offsets, config bytes, trigger IDs and keep semantics come from the actual XML. The connection's explicit service is used; a supplied experiment service filter must match. Requested nonzero MTU is blocked until actual negotiated size checking is implemented.

USB does not inherit BLE protocol compatibility. It requires a named `ProtocolId`, complete hexadecimal packet template, explicit buffer conversions and offsets. HID additionally requires the exact report length and report ID matching the selected profile. No baud rate, command prefix, checksum, protocol or device model is guessed. A protocol requiring a dynamically computed checksum needs an appropriate explicit adapter before it can be declared supported.

Continuous (untriggered) values may be coalesced per binding/characteristic to the latest pending value, matching the Android intent. Triggered packets receive distinct queue entries and are never coalesced. The queue is bounded; overflow faults output rather than silently losing control actions. No automatic reconnect, retry or replay of config/control writes occurs in this bridge.

BLE download uses a separate connection and refuses a device already connected for acquisition. Per-device gates prevent racing duplicate connect/download requests. Official phyphox transfer framing, 10 MB size bound, payload CRC32, packet timeout and control 1/0 are used. A five-minute overall bound applies. Stored partial ZIP payloads with the official PK0708 CRC/size trailer are validated and rebuilt as one `a.phyphox` archive entry. Other compressed partial variants are rejected.

## Evidence

Pure tests passed for conversion offsets, keep consumption ordering, trigger routing, USB explicit packet assembly and bounds, and partial ZIP CRC validation/reconstruction. Server compilation has passed. No real BLE/USB device was available; scanning, config writes, control outputs, reconnect, experiment download and device behavior remain unverified. No simulator is exposed as a real transport.
