// SPDX-License-Identifier: GPL-3.0-or-later
// Port of phyphox Android experiment execution semantics (RWTH Aachen University).
using System.Diagnostics;
using System.Xml.Linq;

namespace Phyphox.Core;
public sealed class ExperimentRuntime
{
    readonly Dictionary<string, DataBuffer> buffers;
    readonly IExperimentInfoProvider? infoProvider;
    readonly HashSet<XElement> executed = [];
    double? replayTime;
    bool restoredOpenInterval;
    AnalysisTimeContext? replayTiming;
    readonly Stopwatch clock = new();
    double elapsed;
    bool first = true;
    bool userInput = true;
    long lastAnalysis;
    public ExperimentDefinition Definition { get; }
    public IReadOnlyDictionary<string, DataBuffer> Buffers => buffers;
    public string State { get; private set; } = "ready";
    public long Cycle { get; private set; }
    public double ExperimentTime => replayTime ?? (elapsed + clock.Elapsed.TotalSeconds);
    public List<TimeMapping> TimeMappings { get; } = [];
    public AnalysisCycleContext? LastCycleContext { get; private set; }
    public IReadOnlyDictionary<string, double> LastCycleInfo { get; private set; } = new Dictionary<string, double>();

    public ExperimentRuntime(ExperimentDefinition definition, IExperimentInfoProvider? infoProvider = null)
    {
        this.infoProvider = infoProvider;
        Definition = definition;
        buffers = definition.Containers.ToDictionary(c => c.Name, c => new DataBuffer(c));
        TimeMappings.AddRange(definition.RestoredTimeMappings);
        if (TimeMappings.Count > 0)
        {
            // Official state saving stops measurement first. A final PAUSE pins the
            // elapsed experiment time; restored mappings never restart hardware.
            elapsed = TimeMappings[^1].ExperimentTime;
            State = "paused";
            restoredOpenInterval = TimeMappings[^1].Event == "START";
        }
    }

    // A button can request a cycle without writing a data buffer.
    public void NotifyUserInput() => userInput = true;

    void EnsureSupported()
    {
        if (restoredOpenInterval)
            throw new NotSupportedException("Saved state ends with START and has no final PAUSE timestamp. Elapsed time cannot be reconstructed; inspect the stored data or clear before starting.");
        foreach (var key in Definition.Analysis.Where(m => m.Name.LocalName == "info").SelectMany(m => m.Children("output")).Select(o => o.Attr("as")))
            if (infoProvider == null || !infoProvider.TryGetValue(key, out _))
                throw new NotSupportedException($"Device information unavailable: {key}");
        if (Definition.CapabilityProblems.Count > 0)
            throw new NotSupportedException(string.Join("; ", Definition.CapabilityProblems));
    }

    public void Start()
    {
        EnsureSupported();
        if (State == "running")
            return;
        first = true;
        TimeMappings.Add(new("START", ExperimentTime, DateTimeOffset.UtcNow));
        clock.Start();
        State = "running";
    }

    public void Pause()
    {
        if (State != "running")
            return;
        elapsed += clock.Elapsed.TotalSeconds;
        clock.Reset();
        TimeMappings.Add(new("PAUSE", ExperimentTime, DateTimeOffset.UtcNow));
        State = "paused";
    }

    public void Stop()
    {
        Pause();
        State = "stopped";
    }

    public void Clear(string? group = null) => ResetBuffers(b => b.Definition.ClearGroup == null || group != null && group != "_" && b.Definition.ClearGroup == group);
    void ResetBuffers(Func<DataBuffer, bool> selected)
    {
        Stop();
        var names = buffers.Values.Where(selected).Select(b => b.Definition.Name).ToHashSet();
        foreach (var name in names)
            buffers[name].Reset();
        foreach (var module in Definition.Analysis)
            if (module.Elements().Any(e => names.Contains(e.Value.Trim())))
                executed.Remove(module);
        elapsed = 0;
        replayTime = null;
        replayTiming = null;
        Cycle = 0;
        TimeMappings.Clear();
        restoredOpenInterval = false;
        first = true;
        userInput = true;
        State = "ready";
    }

    public void ReceiveSamples(string name, IEnumerable<double> samples, bool append = true)
    {
        if (!append)
            buffers[name].Clear();
        buffers[name].Append(samples);
    }

    public void SetBuffer(string name, IEnumerable<double> data, bool append = false)
    {
        userInput = true;
        var b = buffers[name];
        if (!append)
            b.Clear();
        b.Append(data);
    }

    public void ClearGroups(IEnumerable<string> groups)
    {
        var set = groups.ToHashSet();
        ResetBuffers(b => b.Definition.ClearGroup == null || b.Definition.ClearGroup != "_" && set.Contains(b.Definition.ClearGroup));
    }

    public bool Tick()
    {
        var a = Definition.Root.Child("analysis");
        if (State != "running" && !userInput)
            return false;
        if (a?.Flag("onUserInput") == true && !userInput)
            return false;
        double delay = XmlUtil.Number(a?.Attr("sleep", "0") ?? "0");
        var dynamic = a?.Attr("dynamicSleep");
        if (!string.IsNullOrEmpty(dynamic) && buffers.TryGetValue(dynamic, out var b))
            delay = double.IsFinite(b.Last) ? b.Last : delay;
        if (!first && !userInput && Stopwatch.GetElapsedTime(lastAnalysis).TotalSeconds < delay)
            return false;
        if (State != "running")
            Cycle = 0;
        bool ran = RunCycle();
        if (ran)
        {
            lastAnalysis = Stopwatch.GetTimestamp();
            userInput = false;
        }

        return ran;
    }

    /// <summary>Replays the recorded numerical schedule only; it never performs device I/O.</summary>
    public bool RunCycleAt(long cycle, double experimentTime, double linearTime, double offset1970, double linearOffset1970)
    {
        if (cycle < 0 || !double.IsFinite(experimentTime) || experimentTime < 0 || !double.IsFinite(linearTime) || !double.IsFinite(offset1970) || !double.IsFinite(linearOffset1970))
            throw new ArgumentException("Invalid replay timing.");
        replayTime = experimentTime;
        replayTiming = new(linearTime, offset1970, linearOffset1970);
        Cycle = cycle;
        return RunCycle();
    }

    /// <summary>Restores a paused data snapshot, not a live device or engine checkpoint.</summary>
    public void RestoreDataSnapshot(IReadOnlyDictionary<string, double[]> data, double experimentTime, IEnumerable<TimeMapping>? mappings = null)
    {
        if (!double.IsFinite(experimentTime) || experimentTime < 0 || data.Count != buffers.Count || data.Keys.Any(k => !buffers.ContainsKey(k)))
            throw new ArgumentException("Snapshot does not match this experiment.");
        Stop();
        clock.Reset();
        replayTime = null;
        replayTiming = null;
        elapsed = experimentTime;
        restoredOpenInterval = false;
        Cycle = 0;
        executed.Clear();
        foreach (var pair in data)
            buffers[pair.Key].Restore(pair.Value);
        TimeMappings.Clear();
        if (mappings != null)
            TimeMappings.AddRange(mappings);
        first = false;
        userInput = false;
        State = "paused";
    }

    public bool RunCycle()
    {
        EnsureSupported();
        var a = Definition.Root.Child("analysis");
        var gate = a?.Attr("requireFill");
        int threshold = int.Parse(a?.Attr("requireFillThreshold", "1") ?? "1");
        var dynamicGate = a?.Attr("requireFillDynamic");
        if (!string.IsNullOrEmpty(dynamicGate) && buffers[dynamicGate].Count > 0)
            threshold = (int)buffers[dynamicGate].Last;
        if (!first && !string.IsNullOrEmpty(gate) && buffers[gate].Count < threshold)
            return false;
        first = false;
        var now = DateTimeOffset.UtcNow;
        var experimentTime = ExperimentTime;
        var timing = replayTiming ?? new AnalysisTimeContext(TimeMappings.Count == 0 ? 0 : (now - TimeMappings[0].SystemTime).TotalSeconds, (TimeMappings.LastOrDefault()?.SystemTime ?? now).ToUnixTimeMilliseconds() / 1000.0, (TimeMappings.FirstOrDefault()?.SystemTime ?? now).ToUnixTimeMilliseconds() / 1000.0);
        LastCycleContext = new(Cycle, experimentTime, timing.LinearTime, timing.Offset1970, timing.LinearOffset1970);
        var infoValues = new Dictionary<string, double>();
        foreach (var key in Definition.Analysis.Where(m => m.Name.LocalName == "info").SelectMany(m => m.Children("output")).Select(o => o.Attr("as")).Distinct())
        {
            if (infoProvider == null || !infoProvider.TryGetValue(key, out var value))
                throw new NotSupportedException($"Device information unavailable: {key}");
            infoValues[key] = value;
        }

        LastCycleInfo = infoValues;
        var cycleInfo = new CycleInfoProvider(infoValues);
        foreach (var module in Definition.Analysis)
        {
            if (!InCycle(module.Attr("cycles"), Cycle))
                continue;
            var outs = module.Children("output").ToArray();
            if (executed.Contains(module) && outs.All(o => buffers[o.Value.Trim()].Definition.Static))
                continue;
            var ins = module.Children("input").ToArray();
            var values = new double[ins.Length][];
            for (int inputIndex = 0; inputIndex < ins.Length; inputIndex++)
            {
                var input = ins[inputIndex];
                values[inputIndex] = input.Attr("type", "buffer") switch
                {
                    "value" => [XmlUtil.Number(input.Value)],
                    "empty" => [],
                    _ => buffers[input.Value.Trim()].Values
                };
                // Match AnalysisModule.updateIfNotStatic: snapshot then consume
                // each input in order, so a later alias sees earlier consumption.
                if (input.Attr("type", "buffer") == "buffer" && !input.Flag("keep", !input.Flag("clear", true)) && !buffers[input.Value.Trim()].Definition.Static)
                    buffers[input.Value.Trim()].Clear();
            }

            var result = AnalysisKernel.Evaluate(module, ins, values, outs.Select(o => buffers[o.Value.Trim()].Definition.Size).ToArray(), experimentTime, cycleInfo, timing);
            for (var i = 0; i < outs.Length; i++)
            {
                var o = outs[i];
                var b = buffers[o.Value.Trim()];
                if (b.Definition.Static && b.Written)
                    continue;
                if (i >= result.Length || result[i] == null)
                    continue;
                if (!o.Flag("append", !o.Flag("clear", true)))
                    b.Clear();
                b.Append(result[i]!);
                b.Written = true;
            }

            executed.Add(module);
        }

        Cycle++;
        return true;
    }

    sealed class CycleInfoProvider(IReadOnlyDictionary<string, double> values) : IExperimentInfoProvider
    {
        public bool TryGetValue(string key, out double value) => values.TryGetValue(key, out value);
    }

    static bool InCycle(string text, long cycle)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;
        foreach (var part in text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Split('-');
            if (s.Length == 1 && long.Parse(s[0]) == cycle)
                return true;
            if (s.Length == 2 && (s[0] == "" || cycle >= long.Parse(s[0])) && (s[1] == "" || cycle <= long.Parse(s[1])))
                return true;
        }

        return false;
    }
}

public sealed record TimeMapping(string Event, double ExperimentTime, DateTimeOffset SystemTime);
public sealed record AnalysisCycleContext(long Cycle, double ExperimentTime, double LinearTime, double Offset1970, double LinearOffset1970);
