# Original BLE input integration

`BleInputCoordinator` binds an explicitly connected real GATT device to an original `<input><bluetooth>` index. It uses XML declarations directly; it does not require mapping each buffer by hand, choose devices automatically, or invent USB protocol equivalents.

## Session hooks

1. Create one coordinator with the shared `DeviceManager` and subscribe to `Faulted`. Fault handlers should schedule full lifecycle cleanup without blocking the reader callback.
2. Set the same monotonic-to-experiment-time mapper used by camera input through `SetTimeMapper(Func<long,double>)`. `extra=time` is local receipt/batch-completion time, not peripheral sample time.
3. Expose `BindAsync(definition, new BleInputBinding(connectionId,inputIndex), ct)` while stopped, awaited outside the Session lock. The explicit BLE connection must use `Subscribe=false`; the coordinator owns XML subscription timing. Bind checks service/name/address filters, characteristic capabilities and conversion syntax, writes authored config once, and subscribes immediately only when `subscribeOnStart=false`.
4. Include `CapabilityIssues(definition)` in preflight. Remove generic missing-device blockers only for input indices with a valid native BLE binding. Reject simultaneous generic and native bindings for the same input/connection, which would otherwise double-ingest the primary characteristic.
5. Await `StartAsync(definition,ct)` outside the Session lock. It activates start-scoped subscriptions and starts bounded sequential polling where declared.
6. Before an analysis cycle, under the Session lock, call `Flush(data => appendTransaction(data))`. The dictionary represents one notification or one completed poll set. Append each value, including empty decoded fields when preserving buffer mark semantics.
7. Await `StopAsync()` outside the Session lock on pause/stop/fault. Polls stop; start-scoped subscriptions are removed. Pre-start subscriptions remain active but their data is discarded until the next start, matching the source lifecycle intent.
8. On experiment replacement/device rebinding/disposal call `ClearBindingsAsync()`, which also removes early subscriptions. Config writes are not replayed on resume or automatic reconnect. There is no automatic reconnect in this coordinator.

`DeviceManager.BleFrameReceived` carries characteristic UUIDs. `FrameReceived` continues to deliver only the selected primary characteristic for legacy raw bindings. A connection with `Subscribe=false` no longer performs an incidental one-off read on connect; explicit polling is owned by the experiment coordinator. Disconnect/read/write errors notify active input coordination.

## Supported semantics

- notification, indication and poll modes; actual characteristic properties choose notify before indicate as in Android's subscription implementation;
- `subscribeOnStart`, nonnegative polling rate (zero means minimum 1 ms scheduling delay, bounded by actual read completion);
- authored configuration bytes, explicit selected service and characteristic UUIDs;
- all existing input conversion names, offsets, repeating strides, lengths, decimal-point substitution and literal formatted-string parsing;
- one `extra=time` output per characteristic; malformed/truncated numeric values become empty fields rather than fabricated samples;
- atomic poll-set ingestion after all requested characteristics have been read; per-notification ingestion for pushed data;
- bounded queue; overflow and communication errors fault acquisition without pretending a lossless stream.

Unsupported/remaining: negotiated MTU checking (nonzero XML requirements block), cross-service characteristic lookup, unsupported properties/descriptors, pairing workflow, device event-time characteristic forwarding, automatic reconnect/config replay, and peripheral clock synchronization. An explicit missing characteristic fails binding rather than falling back to another device. The no-CCCD Android tolerance path is not implemented by the current WinRT transport. Poll failures stop acquisition rather than continuing with stale partial sets.

## Evidence

Source: official Android `BluetoothInput.kt` and `PhyphoxFile.java`, commit 45fa55a0727653ccce439b86acb69c00cf435436.

Focused tests use official `corpus/valid/ble-libraries/micropython-randomNumbers.phyphox` and its published numerical baseline. Float packets are reconstructed from the baseline's numerical values solely to exercise decoding; they are not captured raw packets and are not hardware evidence. Tests also cover repeating int24, short-packet empty outputs, poll parsing, extra-time uniqueness and unsupported MTU rejection. These tests passed. No real BLE device was connected, subscribed, polled or configured.
