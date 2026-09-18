// SPDX-License-Identifier: GPL-3.0-only
using Phyphox.Core;
using Phyphox.Network;

namespace Phyphox.Server;

public sealed partial class SessionService
{
    NetworkCoordinator? network;
    readonly Queue<string> networkDiagnostics = new();
    CancellationTokenSource? acquisition;
    Task faultCleanup = Task.CompletedTask;

    // Called at a session boundary under gate. No HTTP request is made here.
    void ConfigureNetwork()
    {
        network?.Dispose();
        networkDiagnostics.Clear();
        var owner = runtime!;
        var ownerId = entry!.Item.Id;
        network = new NetworkCoordinator(owner.Definition.Root.Child("network"),
            name => { lock (gate) return owner.Buffers[name].Values; },
            message => { lock (gate) { if (runtime != owner) return; networkDiagnostics.Enqueue(message); while (networkDiagnostics.Count > 20) networkDiagnostics.Dequeue(); revision++; } },
            timeInfo: () => { lock (gate) return new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d, events = owner.TimeMappings.Select(t => new { @event = t.Event, experimentTime = t.ExperimentTime, systemTime = t.SystemTime.ToUnixTimeMilliseconds() / 1000d }).ToArray() }; },
            capture: requests =>
            {
                lock (gate)
                {
                    if (runtime != owner || owner.State != "running") throw new OperationCanceledException("The experiment is no longer running.");
                    var values = requests.Select(r => r.Buffer).Distinct().ToDictionary(n => n, n => owner.Buffers[n].Values);
                    var consumed = requests.Where(r => !r.Keep && !owner.Buffers[r.Buffer].Definition.Static).Select(r => r.Buffer).Distinct().ToArray();
                    if (consumed.Length > 0)
                    {
                        var empty = consumed.ToDictionary(n => n, _ => Array.Empty<double>());
                        RecordInput(empty, consumed);
                        foreach (var name in consumed) owner.ReceiveSamples(name, [], false);
                        revision++;
                    }
                    return values;
                }
            },
            resource: name => File.ReadAllBytes(library.CertificateResource(ownerId, name) ?? throw new FileNotFoundException("The declared TLS certificate resource is unavailable.")));
    }

    void FlushNetwork()
    {
        while (network?.Pending.TryDequeue(out var batch) == true)
        {
            // Preserve duplicate-target order and append/replace semantics in the replay journal.
            foreach (var write in batch.Writes)
            {
                if (!runtime!.Buffers.ContainsKey(write.Buffer)) throw new InvalidDataException("Network response references an unknown buffer: " + write.Buffer);
                RecordInput(new Dictionary<string, double[]> { [write.Buffer] = write.Values }, write.Append ? [] : [write.Buffer]);
                runtime.ReceiveSamples(write.Buffer, write.Values, write.Append);
            }
            revision++;
        }
    }

    async Task RunNetworkWorker(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                NetworkCoordinator? current;
                lock (gate) current = runtime?.State == "running" ? network : null;
                if (current is null) continue;
                try { await current.TickAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { /* A completed experiment was replaced. */ }
                catch (Exception ex) { lock (gate) { if (current == network) { networkDiagnostics.Enqueue(ex.Message); while (networkDiagnostics.Count > 20) networkDiagnostics.Dequeue(); } } }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    void QueueFaultCleanup()
    {
        if (!faultCleanup.IsCompleted) return;
        var owner = acquisition;
        // Never await the producer from its own callback, and never stop a later session.
        faultCleanup = Task.Run(async () =>
        {
            if (owner is not null) await owner.CancelAsync();
            await commandGate.WaitAsync();
            try
            {
                if (ReferenceEquals(owner, acquisition)) await StopAcquisitionAsync();
            }
            catch (Exception ex) { lock (gate) error = (error ?? "采集已停止") + "；释放设备时：" + ex.Message; }
            finally { commandGate.Release(); }
        });
    }

    async Task StopAcquisitionAsync(bool preserveBrowserMedia = false)
    {
        lock (gate) { if (!preserveBrowserMedia) InvalidateBrowserMedia(); else PauseBrowserAudioOutputLocked(); }
        network?.Stop();
        if (acquisition is { } active) await active.CancelAsync();
        try { await Task.WhenAll(media.StopAsync(), outputs.StopAsync(), bleInputs.StopAsync()); }
        finally { acquisition?.Dispose(); acquisition = null; }
    }
}
