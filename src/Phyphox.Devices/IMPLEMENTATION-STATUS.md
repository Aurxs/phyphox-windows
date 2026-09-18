# Device implementation status

These are real OS adapters, not synthetic devices. None has been exercised against Windows or physical hardware. Successful cross-compilation does not demonstrate device compatibility.

| Area | Implemented | Remaining |
|---|---|---|
| Common | Explicit transport-specific profiles, raw byte frames, UTC receive and monotonic receive time, cancellation, unavailable-platform error | Service integration, timestamp synchronization, device protocol/units |
| Serial | Registry enumeration, USB instance identity where available, Win32 overlapped I/O, explicit baud/data/parity/stop bits, cancellation, timeout | Stable identity for all virtual-port providers, flow-control settings, hardware reconnect validation |
| HID | Interface enumeration, overlapped Input/Output Report I/O, exact configured output report size, Feature Report Get/Set, HID usage and report-size capabilities | UI profile validation against descriptors, asynchronous timeout for synchronous feature calls, protocol-specific decoding |
| BLE | Five-second active advertisement scan, Windows GATT device access, explicit service/characteristic selection, notification/indication, polling read, write with/without response, serialized secondary characteristic config/read and multiple notification subscriptions, cancellation and operation timeouts, connection loss and receive overflow errors | Pairing UI, descriptor configuration beyond CCCD, negotiated MTU reporting; opt-in bounded reconnect restores primary profile only, never commands or additional subscriptions |
| Protocol helpers | Official numeric input conversions including int24 and IEEE values, literal formatted strings, explicit serial framing, official experiment transfer header/10 MB size/CRC32 check and notification/read download orchestration with control start/stop; official output/config conversion names and Java narrowing semantics | Partial ZIP handling, event-time transfer, differential validation of extreme floating-point string spellings |

`DeviceFrame.Data` is a transport read or a GATT value. Serial reads may split/combine protocol packets; apply `PacketFramer` only when the device protocol explicitly defines fixed-size or delimiter framing. HID profiles count the report ID byte. No USB protocol is inferred from VID/PID or COM port presence.

Source provenance: numeric conversion behavior derives from official `Bluetooth/ConversionsInput.java`; BLE transfer framing derives from official `Bluetooth/BluetoothExperimentLoader.kt`, Android commit 45fa55a0727653ccce439b86acb69c00cf435436. The repository license and modified-source notices apply.

Reconnect is opt-in via `ReconnectingDeviceTransport`, with bounded backoff and gap events. Its caller must record all gaps and explicitly restore any additional subscriptions after `Reconnected`; one-shot outputs are never repeated. The BLE downloader uses a dedicated `IBleDeviceTransport` and always releases it. The protocol fixture is not a real-device test.

## Integrated service status

The Server now includes declarative BLE input coordination (typed multi-characteristic frames, XML conversion/config/poll/subscription timing), explicit BLE/USB output coordination, and BLE experiment download/import endpoints. Official stored partial ZIP payloads are validated and normalized before safe library import. Refer to Server `BLE-INPUTS.md` and `DEVICE-OUTPUTS.md` for integration boundaries. Generic input bindings may explicitly replace a BLE source; the Session supplies those indices through the optional `externallyBoundInputs` parameter so native BLE constraints are not incorrectly applied to a declared replacement. Native and generic bindings must remain mutually exclusive for an input/connection.

These are implementation and pure-fixture verification results, not hardware acceptance. Real BLE/USB connect/config/acquisition/output/download, pairing, negotiated MTU, peripheral clock/event-time synchronization and long-running driver behavior have not been verified on actual devices.
