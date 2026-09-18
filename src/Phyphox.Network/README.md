# Local-service HTTP / MQTT experiment adapter

Source basis: official Android `NetworkConnection.java`, `NetworkService.java`, `NetworkConversion.java`, `PhyphoxFile.java` network parser, and official network fixture experiments. Derived implementation remains GPL-3.0-only.

Implemented: explicitly started HTTP GET/POST and MQTT/MQTTS JSON/CSV experiment connections, periodic scheduling, id-based triggers, GET last-value sends, POST number/array sends, JSON object-path and CSV column receives, none conversion, append/overwrite defaults and deprecated clear aliases. Normal HTTPS certificate validation is retained; redirect responses are rejected rather than silently changing the declared destination. Configured connections do not send traffic before Start.

## Integration contract

```csharp
var adapter = new NetworkCoordinator(
    networkElement,
    readBuffer: name => ReadLockedCopy(name),
    fault: message => PublishDiagnostic(message),
    metadata: key => GetRealMetadataOrNull(key),
    timeInfo: () => GetRealTimeEventSnapshot(),
    capture: requests => CaptureAndConsumeUnderOneEngineLock(requests),
    resource: name => LoadExperimentCertificateResource(name));
```

`capture` must copy all requested buffers and clear only buffers with any `Keep == false` **in the same transaction**, before HTTP is dispatched. Respect static-buffer ordinary-clear rules. Without this callback, keep=false is explicitly blocked in Issues, because clearing buffers after the asynchronous reply could delete newly arrived data. No fabricated metadata is supplied.

- Inspect `Issues` before marking a network experiment runnable.
- `Start()` authorizes network activity; `Stop()` cancels outstanding requests and discards late results.
- Invoke `TickAsync` from a background worker, never await HTTP while holding the engine lock. At most one tick is active. Connections due in that tick execute concurrently.
- `RequestTrigger(id)` schedules an explicitly identified request during an active network session. Interval zero does not automatically send.
- Drain `Pending` at an engine transaction boundary. Apply each `NetworkWrite` in order: ordinary clear when `Append == false`, then append Values. `ConsumedBuffers` is empty; consumption occurs atomically at capture time.
- Queue batches are ordered by completion. The queue is bounded to 128 pending responses; oldest batches are discarded on overflow and `DroppedResponses` increments. Android also permits overwritten parked responses, so gap-free polling is not a compatibility promise.
- Read `Status` for per-connection failure/completion state and actual MQTT Connected state. HTTP error, malformed response, timeout, disconnect and size violations are diagnostics; they do not terminate the coordinator.

Default request timeout: five seconds. Maximum decompressed response: 8 MiB. Limits are init-only options for an embedding service.

## Explicit remaining limits

Discovery remains unsupported and is reported as a capability issue. Windows and external-broker acceptance have not been executed. MQTTnet is pinned to 4.3.7.1207 (MIT); its official license is included as `MQTTnet.LICENSE.txt`. Paused-session manual triggers require embedding lifecycle handling; the coordinator rejects triggers while stopped. Exact Android response-overwrite timing is intentionally not reproduced; bounded completion batches preserve more available data. Unknown metadata values require a real provider and fail explicitly. Named certificate resources must be supplied by the embedding service through resource(name); safe single filenames are enforced, PEM/DER CA certificates are loaded, and missing/invalid resources fail without falling back to different trust. No system trust store is modified.

## Verification

The executable `tests/Phyphox.Network.Tests` imports the **official** Python HTTP fixture and binds its server to `127.0.0.1` on an ephemeral port. It uses official GET/POST experiment files with only documented host/port substitutions. The HTTP fixture plus an MQTTnet loopback broker passed 30 focused checks on the macOS development host: GET receive, GET scalar roundtrip, POST array roundtrip, pre-start/stop inactivity, malformed/empty/500/timeout/unreachable containment, bounded response, atomic consumption requirement, explicit triggers, overwrite output, metadata refusal, JSON/CSV conversion plus MQTT JSON/CSV loopback, QoS1, broker restart/re-subscription, receive-only subscriptions and malformed-message recovery. An ephemeral in-memory CA/server certificate verifies MQTTS JSON/CSV and rejection of untrusted CA, wrong hostname and wrong credentials. No certificate-verification bypass is used.

Run from `phyphox-windows`:

```sh
dotnet run --project tests/Phyphox.Network.Tests -- ../official-reference/phyphox-docs
```

No external platform or cloud endpoint is contacted by these tests.

## MQTT behavior

- Protocol MQTT 3.1.1; random session client id and clean session. `persistence=true` for JSON means QoS1, matching the current official source, not durable disk storage. CSV sends QoS0.
- JSON publishes one object to `sendTopic`; CSV publishes each send to its `id` topic. `receiveTopic` is subscribed before publication; received bytes pass through declared conversion and queued NetworkWrite batches.
- Interval-zero connections still connect/subscribe after Start, enabling receive-only CSV service connections. Publication waits for an explicit trigger when interval is zero.
- Disconnection is exposed through Status. Subsequent ticks retry connection at most once per second and resubscribe. No implicit application-level resend is added for non-idempotent commands.
- MQTTS uses TLS1.2/1.3, explicit format username/password, and normal system trust or the declared CA resource. Hostname and validity checks remain enabled.

Official dependency references: [fixed package](https://www.nuget.org/packages/MQTTnet/4.3.7.1207), [TLS builder](https://github.com/dotnet/MQTTnet/blob/v4.3.7.1207/Source/MQTTnet/Client/Options/MqttClientTlsOptionsBuilder.cs), [transport trust implementation](https://github.com/dotnet/MQTTnet/blob/v4.3.7.1207/Source/MQTTnet/Implementations/MqttTcpChannel.cs).
