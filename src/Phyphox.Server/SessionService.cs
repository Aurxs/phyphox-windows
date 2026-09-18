// SPDX-License-Identifier: GPL-3.0-only
using Phyphox.Core;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Phyphox.Devices;
using Phyphox.Storage;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Phyphox.Server;
public sealed record SessionCommand(string Command, string? Buffer = null, double? []? Values = null, string? ElementId = null, string[]? Groups = null, string? RequestId = null);
public sealed partial class SessionService : BackgroundService
{
    readonly object gate = new();
    readonly LibraryService library;
    readonly string recordings;
    readonly string imports;
    readonly SnapshotStore snapshotStore;
    readonly DeviceManager devices;
    readonly List<ActiveInputBinding> bindings = [];
    readonly MediaCoordinator media = new();
    readonly OutputCoordinator outputs;
    readonly BleInputCoordinator bleInputs;
    readonly Queue<DeviceWriteReceipt> outputReceipts = new();
    readonly SemaphoreSlim commandGate = new(1, 1);
    bool starting;
    readonly Queue<string> requests = new();
    readonly List<object> events = [];
    ExperimentRuntime? runtime;
    LibraryEntry? entry;
    string? error;
    long revision;
    StreamWriter? journal;
    string? journalId;
    long journalSequence;
    bool journalHealthy = true;
    bool journalComplete = true;
    string dataOrigin = "experiment";
    public SessionService(LibraryService library, string dataRoot, DeviceManager devices)
    {
        this.library = library;
        this.devices = devices;
        bleInputs = new BleInputCoordinator(devices);
        bleInputs.SetTimeMapper(tick => { lock (gate) return runtime is null ? 0 : Math.Max(0, runtime.ExperimentTime - Stopwatch.GetElapsedTime(tick).TotalSeconds); });
        bleInputs.Faulted += MediaFault;
        outputs = new OutputCoordinator(devices);
        outputs.Faulted += MediaFault;
        outputs.WriteCompleted += receipt => { lock (gate) { outputReceipts.Enqueue(receipt); while (outputReceipts.Count > 100) outputReceipts.Dequeue(); revision++; } };
        media.SetCameraTimeMapper(tick =>
        {
            lock (gate)
                return runtime is null ? 0 : Math.Max(0, runtime.ExperimentTime - Stopwatch.GetElapsedTime(tick).TotalSeconds);
        });
        devices.FrameReceived += ReceiveFrame;
        devices.ConnectionFaulted += DeviceFault;
        recordings = Path.Combine(dataRoot, "recordings");
        Directory.CreateDirectory(recordings);
        imports = Path.Combine(dataRoot, "imports");
        Directory.CreateDirectory(imports);
        snapshotStore = new SnapshotStore(Path.Combine(dataRoot, "snapshots"));
    }

    public object Load(string id)
    {
        lock (gate)
        {
            if (runtime?.State == "running" || starting)
                throw new InvalidOperationException("请先停止当前实验。");
            var selected = library.Get(id);
            if (selected.Item.IsLink)
                throw new InvalidOperationException("这是官方网页链接条目，不是测量实验。");
            var definition = ExperimentParser.Parse(File.ReadAllText(selected.Path));
            var next = new ExperimentRuntime(definition);
            SealJournal();
            journal?.Dispose();
            journal = null;
            outputs.ClearBindings();
            outputReceipts.Clear();
            InvalidateBrowserMedia();
            runtime = next;
            entry = selected;
            ConfigureNetwork();
            error = null;
            events.Clear();
            requests.Clear();
            bindings.Clear();
            revision++;
            dataOrigin = "experiment";
            OpenJournal();
            InitializeInputs();
            if (definition.CapabilityProblems.Count == 0)
            {
                try
                {
                    RunAndRecord();
                }
                catch (Exception ex)
                {
                    journalHealthy = false;
                    error = "初始化分析失败：" + ex.Message;
                }
            }

            return SnapshotUnsafe();
        }
    }

    public async Task<object> LoadAsync(string id, CancellationToken cancellationToken)
    {
        await faultCleanup;
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            lock (gate) if (runtime?.State == "running" || starting) throw new InvalidOperationException("请先停止当前实验。");
            await bleInputs.ClearBindingsAsync();
            return Load(id);
        }
        finally { commandGate.Release(); }
    }

    public object BindOutput(DeviceOutputBinding binding)
    {
        lock (gate)
        {
            if (runtime is null || runtime.State == "running" || starting) throw new InvalidOperationException("请停止实验后配置设备输出。");
            outputs.Bind(runtime.Definition, binding);
            revision++;
            return SnapshotUnsafe();
        }
    }

    void Trigger(string id)
    {
        if (runtime?.State != "running") throw new InvalidOperationException("请先启动实验，再触发外部通信。");
        bool matched = false;
        if (network?.Status.Any(n => n.Id == id) == true) { network.RequestTrigger(id); matched = true; }
        if (outputs.Bindings.Any(b => b.TriggerId == id) || runtime.Definition.Outputs.Any(o => o.Children("input").Any(i => i.Attr("triggerId") == id)))
        { outputs.RequestTrigger(id); matched = true; }
        if (!matched) throw new InvalidOperationException("没有对应的网络或设备输出触发器：" + id);
        runtime.NotifyUserInput();
    }

    void InitializeInputs()
    {
        foreach (var view in runtime!.Definition.Views)
            foreach (var element in view.Elements().Where(e => e.Name.LocalName is "edit" or "toggle" or "dropdown" or "slider"))
                foreach (var output in element.Children("output"))
                {
                    var name = output.Value.Trim();
                    if (!runtime.Buffers.TryGetValue(name, out var buffer) || buffer.Count != 0)
                        continue;
                    var text = element.Attr("default", "0");
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        RecordCommand(new("set", name, [value]));
                        runtime.SetBuffer(name, [value]);
                    }
                }
    }

    public object Command(SessionCommand command)
    {
        lock (gate)
        {
            if (runtime is null)
                throw new InvalidOperationException("请先选择实验。");
            if (command.RequestId is not null && requests.Contains(command.RequestId))
                return SnapshotUnsafe();
            var sequenceBefore = journalSequence;
            try
            {
                switch (command.Command)
                {
                    case "start":
                        var issues = Issues();
                        if (issues.Length != 0)
                            throw new InvalidOperationException(string.Join("；", issues));
                        if (runtime.State == "running")
                            break;
                        RecordCommand(new("start"));
                        runtime.Start();
                        break;
                    case "pause":
                        try
                        {
                            RecordCommand(new("pause"));
                        }
                        finally
                        {
                            runtime.Pause();
                        }

                        break;
                    case "stop":
                        InvalidateBrowserMedia();
                        try
                        {
                            RecordCommand(new("stop"));
                        }
                        finally
                        {
                            runtime.Stop();
                        }

                        SealJournal();
                        break;
                    case "clear":
                        InvalidateBrowserMedia();
                        RecordCommand(new("clear", Groups: command.Groups));
                        if (command.Groups is { Length: > 0 })
                            runtime.ClearGroups(command.Groups);
                        else
                            runtime.Clear();
                        InitializeInputs();
                        error = null;
                        break;
                    case "set":
                        if (command.Buffer is null || !runtime.Buffers.ContainsKey(command.Buffer))
                            throw new InvalidOperationException("未知数据容器。");
                        if (command.Values is not { Length: > 0 and <= 1_000_000 })
                            throw new InvalidOperationException("需要非空数值数组，最多一百万项。");
                        RecordCommand(new("set", command.Buffer, command.Values.Select(v => v ?? double.NaN).ToArray()));
                        runtime.SetBuffer(command.Buffer, command.Values.Select(v => v ?? double.NaN));
                        if (runtime.State != "running" && runtime.Definition.CapabilityProblems.Count == 0)
                            RunAndRecord(true);
                        break;
                    case "trigger":
                        Trigger(command.ElementId ?? throw new ArgumentException("需要触发编号。"));
                        break;
                    case "button":
                        ApplyButton(command.ElementId);
                        break;
                    default:
                        throw new InvalidOperationException("未知操作。");
                }
            }
            catch
            {
                if (journalSequence != sequenceBefore)
                    journalHealthy = false;
                throw;
            }

            revision++;
            if (command.RequestId is not null)
            {
                requests.Enqueue(command.RequestId);
                while (requests.Count > 256)
                    requests.Dequeue();
            }

            return SnapshotUnsafe();
        }
    }

    public async Task<object> CommandAsync(SessionCommand command, CancellationToken ct)
    {
        await faultCleanup;
        await commandGate.WaitAsync(ct);
        try
        {
            if (command.Command == "start")
            {
                ExperimentDefinition definition;
                lock (gate)
                {
                    if (runtime is null)
                        throw new InvalidOperationException("请先选择实验。");
                    if (runtime.State == "running")
                        return SnapshotUnsafe();
                    var issues = Issues();
                    if (issues.Length > 0)
                        throw new InvalidOperationException(string.Join("；", issues));
                    starting = true;
                    definition = runtime.Definition;
                }

                try
                {
                    acquisition = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    await outputs.StartAsync(definition, acquisition.Token);
                    HashSet<int> externallyBound;
                    lock (gate) externallyBound = GenericBoundInputs();
                    await bleInputs.StartAsync(definition, acquisition.Token, externallyBound);
                    await media.StartAsync(NativeMediaDefinition(definition), name =>
                    {
                        lock (gate)
                            return runtime!.Buffers[name].Values;
                    }, IngestMedia, MediaFault, acquisition.Token);
                    lock (gate)
                    {
                        starting = false;
                        StartBrowserMedia();
                        StartBrowserAudioOutputLocked();
                        var result = Command(command);
                        network?.Start();
                        return result;
                    }
                }
                catch
                {
                    lock (gate)
                        starting = false;
                    await StopAcquisitionAsync();
                    throw;
                }
            }

            if (command.Command is "pause" or "stop" or "clear")
                await StopAcquisitionAsync(command.Command == "pause");
            return Command(command);
        }
        finally
        {
            commandGate.Release();
        }
    }

    void IngestMedia(IReadOnlyDictionary<string, double[]> writes, bool replace)
    {
        lock (gate)
        {
            if (runtime?.State != "running")
                return;
            RecordInput(writes, replace ? writes.Keys.ToArray() : []);
            foreach (var(name, values)in writes)
                runtime.ReceiveSamples(name, values, !replace);
            revision++;
        }
    }

    void MediaFault(string message)
    {
        lock (gate)
        {
            journalHealthy = false;
            runtime?.Stop();
            error = message;
            revision++;
            QueueFaultCleanup();
        }
    }

    void ApplyButton(string? elementId)
    {
        var element = runtime!.Definition.Views.SelectMany((v, vi) => v.Elements().Select((e, ei) => (e, id: $"{vi}:{ei}"))).FirstOrDefault(v => v.id == elementId).e;
        if (element is null || element.Name.LocalName != "button")
            throw new InvalidOperationException("未知按钮。");
        foreach (var trigger in element.Children("trigger"))
            Trigger(trigger.Value.Trim());
        var inputs = element.Children("input").ToArray();
        var outputs = element.Children("output").ToArray();
        var values = inputs.Select(i => i.Attr("type", "buffer").ToLowerInvariant() switch
        {
            "empty" => Array.Empty<double>(),
            "value" => new[] { XmlUtil.Number(i.Value) },
            _ => new[] { runtime.Buffers[i.Value.Trim()].Last }}).ToArray();
        for (int i = 0; i < Math.Min(values.Length, outputs.Length); i++)
        {
            string target = outputs[i].Value.Trim();
            bool append = outputs[i].Flag("append");
            RecordCommand(new(append ? "append" : "set", target, values[i]));
            runtime.SetBuffer(target, values[i], append);
        }

        runtime.NotifyUserInput();
        if (runtime.State != "running")
            RunAndRecord(true);
    }

    // Sensor identifiers follow official Android SensorInput.SensorName (45fa55a), not PC hardware assumptions.
    public static string GetInputDisplayName(XElement input) => input.Name.LocalName.ToLowerInvariant() switch
    {
        "sensor" => input.Attr("type").ToLowerInvariant() switch
        {
            "accelerometer" => "加速度传感器（含重力 g）",
            "linear_acceleration" => "线性加速度传感器（不含重力 g）",
            "gravity" => "重力传感器",
            "gyroscope" => "陀螺仪（角速度）",
            "magnetic_field" => "磁场传感器（磁力计）",
            "pressure" => "气压传感器",
            "light" => "光照传感器（照度）",
            "proximity" => "接近传感器",
            "temperature" => "环境温度传感器",
            "humidity" => "相对湿度传感器",
            "attitude" => "姿态传感器",
            "custom" => "自定义传感器",
            var sensorType => string.IsNullOrWhiteSpace(sensorType) ? "传感器" : $"传感器（{sensorType}）"
        },
        "location" => "定位数据（GPS）",
        "depth" => "深度相机",
        "camera" => "摄像头",
        "audio" => "麦克风",
        "bluetooth" => "蓝牙设备",
        var type => $"输入设备（{type}）"
    };

    string[] Issues()
    {
        if (runtime is null)
            return["尚未加载实验"];
        var issues = runtime.Definition.CapabilityProblems.ToList();
        if (runtime.Definition.Analysis.Any(m => m.Name.LocalName == "info"))
            issues.Add("此实验需要真实系统信息提供器，当前会话未配置 info 数据来源。");
        for (var i = 0; i < runtime.Definition.Inputs.Count; i++)
            if (runtime.Definition.Inputs[i].Name.LocalName is not ("audio" or "camera" or "bluetooth") && !bindings.Any(b => b.Definition.InputIndex == i && devices.IsConnected(b.Definition.ConnectionId)))
                issues.Add($"需要{GetInputDisplayName(runtime.Definition.Inputs[i])}，当前尚未接入。");
        foreach (var binding in bindings.Where(b => !devices.IsConnected(b.Definition.ConnectionId)))
            issues.Add($"设备连接 {binding.Definition.ConnectionId} 不可用。");
        foreach (var output in runtime.Definition.Outputs.Where(o => o.Name.LocalName is not ("audio" or "bluetooth")))
            issues.Add($"输出 {output.Name.LocalName} 尚未完成实验设备绑定。");
        issues.AddRange(media.CapabilityIssues(NativeMediaDefinition(runtime.Definition)));
        issues.AddRange(outputs.CapabilityIssues(runtime.Definition));
        issues.AddRange(bleInputs.CapabilityIssues(runtime.Definition, GenericBoundInputs()));
        if (network is not null) issues.AddRange(network.Issues);
        if (error is not null)
            issues.Add(error);
        return issues.Distinct().ToArray();
    }

    public object Snapshot()
    {
        lock (gate)
            return SnapshotUnsafe();
    }

    object SnapshotUnsafe()
    {
        if (runtime is null)
            return new
            {
                status = "empty",
                canStart = false,
                issues = Array.Empty<string>(),
                views = Array.Empty<object>(),
                buffers = new Dictionary<string, double? []>(),
                clearGroups = Array.Empty<string>(),
                revision
            };
        var issues = Issues();
        var buffers = runtime.Buffers.ToDictionary(p => p.Key, p => p.Value.Tail(20000).Select(v => double.IsFinite(v) ? (double? )v : null).ToArray());
        return new
        {
            id = entry!.Item.Id,
            title = runtime.Definition.Title,
            status = starting ? "starting" : runtime.State,
            experimentTime = runtime.ExperimentTime,
            timeMappings = runtime.TimeMappings.ToArray(),
            cycle = runtime.Cycle,
            canStart = issues.Length == 0,
            issues,
            views = runtime.Definition.Views.Select((v, i) => new { label = v.Attr("label", "视图"), elements = v.Elements().Select((e, j) => ViewNode(e, $"{i}:{j}")) }),
            buffers,
            bufferLengths = runtime.Buffers.ToDictionary(p => p.Key, p => p.Value.Count),
            displayLimit = 20000,
            displayOnly = true,
            clearGroups = runtime.Definition.Containers.Select(c => c.ClearGroup).Where(c => !string.IsNullOrEmpty(c) && c != "_").Distinct().ToArray(),
            error,
            revision,
            source = entry.Item.Source,
            dataOrigin,
            hardwareVerified = false,
            network = network?.Status,
            networkDiagnostics = networkDiagnostics.ToArray(),
            droppedNetworkResponses = network?.DroppedResponses ?? 0,
            browserMedia = BrowserMediaSnapshot(),
            browserAudioOutput = BrowserAudioOutputSnapshot(),
            inputBindings = bindings.Select(b => b.Definition),
            bleInputBindings = bleInputs.Bindings,
            outputBindings = outputs.Bindings,
            outputReceipts = outputReceipts.ToArray(),
            outputs = runtime.Definition.Outputs.Select((o, index) => new { index, type = o.Name.LocalName }),
            inputs = runtime.Definition.Inputs.Select((i, index) => new { index, type = i.Name.LocalName, name = GetInputDisplayName(i), sensorType = i.Name.LocalName == "sensor" ? i.Attr("type") : null, outputs = i.Children("output").Select(o => o.Value.Trim()) })
        };
    }

    static object ViewNode(XElement e, string? id = null) => new
    {
        id,
        type = e.Name.LocalName,
        name = e.Name.LocalName,
        attributes = e.Attributes().Where(a => !a.IsNamespaceDeclaration).ToDictionary(a => a.Name.LocalName, a => a.Value),
        text = e.HasElements ? "" : e.Value,
        inputs = e.Children("input").Select(i => new { buffer = i.Value.Trim(), attributes = i.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value) }),
        outputs = e.Children("output").Select(i => new { buffer = i.Value.Trim(), attributes = i.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value) }),
        children = e.Elements().Select(n => ViewNode(n))
    };
    public ExportSnapshot ExportSnapshot()
    {
        lock (gate)
        {
            if (runtime is null)
                throw new InvalidOperationException("尚未加载实验。");
            var times = runtime.TimeMappings.ToList();
            // Export freezes this data copy at a real instant; the live experiment may continue.
            if (runtime.State == "running") times.Add(new("PAUSE", runtime.ExperimentTime, DateTimeOffset.UtcNow));
            return new(runtime.Definition.Title, runtime.Definition.SourceXml, runtime.Buffers.ToDictionary(p => p.Key, p => p.Value.Values), times.ToArray(), revision);
        }
    }

    public object ConfigureCamera(CameraExperimentProfile profile)
    {
        lock (gate)
        {
            if (runtime?.State == "running" || starting)
                throw new InvalidOperationException("请先停止采集后配置摄像头。");
            media.ConfigureCamera(profile);
            revision++;
            return SnapshotUnsafe();
        }
    }

    public Phyphox.Camera.CameraFrame? GetCameraFrame()
    {
        lock (gate)
            return browserCameraFrame ?? media.LatestCameraFrame;
    }

    public object ListRecordings()
    {
        lock (gate)
        {
            journal?.Flush();
            return new
            {
                items = Directory.EnumerateFiles(recordings).Where(p => p.EndsWith(".jsonl", StringComparison.Ordinal) || p.EndsWith(".active", StringComparison.Ordinal)).OrderByDescending(File.GetLastWriteTimeUtc).Select(p =>
                {
                    bool sealedFile = p.EndsWith(".jsonl", StringComparison.Ordinal);
                    var id = Path.GetFileNameWithoutExtension(p) + (sealedFile ? "" : "-active");
                    try
                    {
                        using var input = new StreamReader(p);
                        using var json = JsonDocument.Parse(input.ReadLine() ?? "{}");
                        var h = json.RootElement;
                        bool complete = sealedFile && h.TryGetProperty("schema", out var version) && version.GetInt32() == 2 && h.TryGetProperty("completeReplay", out var flag) && flag.GetBoolean();
                        return new
                        {
                            id,
                            complete,
                            bytes = new FileInfo(p).Length,
                            updatedAt = File.GetLastWriteTimeUtc(p),
                            validation = "not-recomputed",
                            source = "recorded-input"
                        };
                    }
                    catch
                    {
                        return new
                        {
                            id,
                            complete = false,
                            bytes = new FileInfo(p).Length,
                            updatedAt = File.GetLastWriteTimeUtc(p),
                            validation = "unreadable",
                            source = "recorded-input"
                        };
                    }
                }).ToArray()
            };
        }
    }

    string RecordingPath(string id)
    {
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,100}\z"))
            throw new ArgumentException("录制编号无效。");
        var path = Path.Combine(recordings, id.EndsWith("-active", StringComparison.Ordinal) ? id[..^7] + ".active" : id + ".jsonl");
        if (!File.Exists(path))
            throw new KeyNotFoundException("录制文件不存在。");
        return path;
    }

    public object ReplayRecording(string id, CancellationToken cancellationToken)
    {
        string path;
        lock (gate)
        {
            journal?.Flush();
            path = RecordingPath(id);
        }

        if (new FileInfo(path).Length > 256_000_000)
            throw new InvalidOperationException("录制超过当前回放内存限制。");
        using var input = new StreamReader(path);
        var result = ReplayEngine.Replay(ReplayEngine.ReadJsonLines(input), cancellationToken);
        var r = result.Runtime;
        return new
        {
            recordingId = id,
            status = "replayed",
            source = result.Source,
            hardwareOutputEnabled = false,
            physicalMeasurement = false,
            displayOnly = true,
            displayLimit = 20000,
            bufferLengths = r.Buffers.ToDictionary(p => p.Key, p => p.Value.Count),
            cycles = result.CyclesExecuted,
            experimentTime = r.ExperimentTime,
            title = r.Definition.Title,
            buffers = r.Buffers.ToDictionary(p => p.Key, p => p.Value.Tail(20000).Select(v => double.IsFinite(v) ? (double? )v : null).ToArray()),
            views = r.Definition.Views.Select((v, i) => new { label = v.Attr("label", "视图"), elements = v.Elements().Select((e, j) => ViewNode(e, $"{i}:{j}")) })
        };
    }

    public async Task<object> SaveSnapshotAsync(string? requestedId, CancellationToken cancellationToken)
    {
        SessionSnapshot snapshot;
        lock (gate)
        {
            if (runtime is null)
                throw new InvalidOperationException("请先选择实验。");
            snapshot = SessionSnapshot.Capture(runtime);
        }

        string id = requestedId ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        await snapshotStore.SaveAtomicAsync(id, snapshot, cancellationToken);
        return new
        {
            id,
            saved = true,
            kind = "paused-data-snapshot",
            deviceCheckpoint = false
        };
    }

    public object ListSnapshots()
    {
        // Read only names and metadata, without materializing potentially large data arrays.
        var directory = Path.Combine(Path.GetDirectoryName(recordings)!, "snapshots");
        var files = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.snapshot.json") : [];
        return new { items = files.OrderByDescending(File.GetLastWriteTimeUtc).Select(path => new
        {
            id = Path.GetFileName(path)[..^".snapshot.json".Length],
            savedAt = File.GetLastWriteTimeUtc(path),
            title = Path.GetFileName(path)[..^".snapshot.json".Length],
            bytes = new FileInfo(path).Length
        }).ToArray() };
    }

    public async Task<object> RestoreSnapshotAsync(string id, CancellationToken cancellationToken)
    {
        await faultCleanup;
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await snapshotStore.LoadAsync(id, cancellationToken);
            var restored = snapshot.Restore();
            LibraryEntry? selected = library.List().Select(i => library.Get(i.Id)).FirstOrDefault(e => File.ReadAllText(e.Path) == snapshot.SourceXml);
            if (selected is null)
            {
                using var source = new MemoryStream(Encoding.UTF8.GetBytes(snapshot.SourceXml));
                var imported = await library.ImportAsync(source, "restored.phyphox", cancellationToken);
                selected = library.Get(imported.Single().Id);
            }

            await StopAcquisitionAsync();
            await bleInputs.ClearBindingsAsync();
            lock (gate)
            {
                if (runtime is not null)
                {
                    RecordCommand(new("stop"));
                    runtime.Stop();
                    SealJournal();
                }

                journal?.Dispose();
                journal = null;
                outputs.ClearBindings();
                outputReceipts.Clear();
                runtime = restored;
                entry = selected;
                ConfigureNetwork();
                bindings.Clear();
                requests.Clear();
                events.Clear();
                error = null;
                starting = false;
                dataOrigin = "restored-data-snapshot";
                revision++;
                OpenJournal(false);
                return SnapshotUnsafe();
            }
        }
        finally
        {
            commandGate.Release();
        }
    }

    public async Task<object> ImportDataAsync(byte[] fileBytes, string fileName, DataImportRequestOptions options, CancellationToken cancellationToken)
    {
        if (fileBytes.Length == 0 || fileBytes.Length > LibraryService.MaximumImportBytes)
            throw new InvalidOperationException("数据文件必须非空且不超过 32 MiB。");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        ImportedData imported;
        if (extension == ".wav")
        {
            using var input = new MemoryStream(fileBytes, false);
            var wave = WaveImporter.Import(input);
            if (options.WaveMappings is null || options.WaveMappings.Count == 0)
                throw new InvalidOperationException("WAV 需要明确的通道到实验容器映射。");
            if (options.WaveMappings.Keys.Any(k => !wave.Data.Buffers.ContainsKey(k)) || options.WaveMappings.Values.Distinct().Count() != options.WaveMappings.Count)
                throw new InvalidOperationException("WAV 映射通道不存在或目标重复。");
            imported = new(options.WaveMappings.ToDictionary(p => p.Value, p => wave.Data.Buffers[p.Key]), wave.Data.SampleCount, wave.Data.TimeSource, options.WaveMappings.ToDictionary(p => p.Value, p => wave.Data.Units[p.Key]));
        }
        else if (extension is ".csv" or ".tsv")
        {
            var mapping = options.Delimited ?? throw new InvalidOperationException("CSV/TSV 需要明确的列映射。");
            if (extension == ".tsv" && mapping.Delimiter != '\t')
                throw new InvalidOperationException("TSV 分隔符必须为制表符。");
            imported = DelimitedImporter.Import(new UTF8Encoding(false, true).GetString(fileBytes), mapping);
        }
        else
            throw new InvalidOperationException("支持 CSV、TSV 和 PCM/浮点 WAV 数据导入。");
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            string importId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
            lock (gate)
            {
                if (runtime is null || runtime.State == "running" || starting)
                    throw new InvalidOperationException("请先加载实验并停止测量后导入数据。");
                if (imported.Buffers.Keys.Any(k => !runtime.Buffers.ContainsKey(k)))
                    throw new InvalidOperationException("导入目标容器不存在。");
                File.WriteAllBytes(Path.Combine(imports, importId + extension), fileBytes);
                File.WriteAllText(Path.Combine(imports, importId + ".json"), JsonSerializer.Serialize(new { sourceFileName = Path.GetFileName(fileName), options, timeSource = imported.TimeSource, imported.SampleCount, imported.Units }, StorageJson.Options));
                foreach (var pair in imported.Buffers)
                {
                    RecordCommand(new(options.Append ? "append" : "set", pair.Key, pair.Value));
                    runtime.SetBuffer(pair.Key, pair.Value, options.Append);
                }

                dataOrigin = "imported-" + extension[1..];
                error = null;
                if (options.Analyze)
                    RunAndRecord(true);
                revision++;
                return new
                {
                    importId,
                    source = dataOrigin,
                    samples = imported.SampleCount,
                    timeSource = imported.TimeSource,
                    originalPreserved = true,
                    hardwareVerified = false,
                    session = SnapshotUnsafe()
                };
            }
        }
        finally
        {
            commandGate.Release();
        }
    }

    public string? CurrentId
    {
        get
        {
            lock (gate)
                return entry?.Item.Id;
        }
    }

    public object Bind(InputBinding binding)
    {
        lock (gate)
        {
            if (runtime is null || runtime.State == "running" || starting)
                throw new InvalidOperationException("请加载实验并停止采集后配置输入映射。");
            if (bleInputs.Bindings.Any(b => b.ConnectionId == binding.ConnectionId || b.InputIndex == binding.InputIndex))
                throw new InvalidOperationException("该设备或输入已有原版 BLE 绑定，不能重复写入采样。");
            if (!devices.IsConnected(binding.ConnectionId))
                throw new InvalidOperationException("必须连接真实设备后再绑定。");
            if (binding.Mappings.Length == 0 || binding.Mappings.Length > 64)
                throw new InvalidOperationException("需要 1..64 个明确的数据映射。");
            var names = binding.Mappings.Select(m => m.Buffer).Append(binding.TimeBuffer).Where(n => n is not null).ToArray();
            if (names.Distinct().Count() != names.Length || names.Any(n => !runtime.Buffers.ContainsKey(n!)))
                throw new InvalidOperationException("映射容器必须存在且不重复。");
            foreach (var mapping in binding.Mappings)
            {
                if (mapping.Offset < 0 || mapping.Repeating < 0 || mapping.Size < 0 || !double.IsFinite(mapping.Scale) || !double.IsFinite(mapping.Bias))
                    throw new InvalidOperationException("映射偏移、长度或数值换算无效。");
                // Check the conversion name before acquisition; a short probe must not hide unknown codecs.
                if (mapping.Conversion != "string")
                    ByteConversions.Read(new byte[8], mapping.Conversion);
            }

            if (binding.InputIndex is int index)
            {
                if (index < 0 || index >= runtime.Definition.Inputs.Count)
                    throw new InvalidOperationException("输入槽编号不存在。");
                var input = runtime.Definition.Inputs[index];
                var expected = input.Children("output").Select(o => o.Value.Trim()).Distinct().ToArray();
                if (expected.Except(names!).Any())
                    throw new InvalidOperationException("必须映射该输入定义的全部输出，包括时间容器。");
                if (!binding.AllowSourceReplacement)
                    throw new InvalidOperationException("原实验输入替换需要明确设置 allowSourceReplacement，并自行确认单位、时间和校准。");
            }

            PacketFramer? framer = null;
            if (binding.PacketLength > 0 || !string.IsNullOrEmpty(binding.DelimiterHex))
                framer = new PacketFramer(binding.PacketLength, binding.DelimiterHex is null ? null : Convert.FromHexString(binding.DelimiterHex));
            if (devices.Descriptor(binding.ConnectionId).Kind == TransportKind.Serial && framer is null)
                throw new InvalidOperationException("串口必须指定固定包长或分隔符，不能把一次读取当成一个采样包。");
            bindings.RemoveAll(b => b.Definition.ConnectionId == binding.ConnectionId || (binding.InputIndex.HasValue && b.Definition.InputIndex == binding.InputIndex));
            bindings.Add(new(binding, framer));
            revision++;
            return SnapshotUnsafe();
        }
    }

    HashSet<int> GenericBoundInputs() => bindings.Where(b => b.Definition.InputIndex.HasValue && devices.IsConnected(b.Definition.ConnectionId)).Select(b => b.Definition.InputIndex!.Value).ToHashSet();

    public async Task<object> BindInputAsync(InputBinding binding, CancellationToken cancellationToken)
    {
        await faultCleanup;
        await commandGate.WaitAsync(cancellationToken);
        try { return Bind(binding); }
        finally { commandGate.Release(); }
    }

    public async Task<object> BindBleInputAsync(BleInputBinding binding, CancellationToken cancellationToken)
    {
        await faultCleanup;
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            ExperimentDefinition definition;
            lock (gate)
            {
                if (runtime is null || runtime.State == "running" || starting) throw new InvalidOperationException("请加载实验并停止采集后绑定 BLE 输入。");
                if (bindings.Any(b => b.Definition.ConnectionId == binding.ConnectionId || b.Definition.InputIndex == binding.InputIndex)) throw new InvalidOperationException("该设备或输入已有通用绑定，请重新加载实验后改用原版 BLE 绑定。");
                definition = runtime.Definition;
            }
            await bleInputs.BindAsync(definition, binding, cancellationToken);
            lock (gate) { revision++; return SnapshotUnsafe(); }
        }
        finally { commandGate.Release(); }
    }

    void DeviceFault(string connectionId, string message)
    {
        lock (gate)
            if (runtime?.State == "running" && (bindings.Any(b => b.Definition.ConnectionId == connectionId) || outputs.Bindings.Any(b => b.ConnectionId == connectionId)))
                MediaFault("设备连接异常：" + message);
    }

    void ReceiveFrame(string connectionId, DeviceFrame frame)
    {
        lock (gate)
        {
            if (runtime?.State != "running")
                return;
            foreach (var active in bindings.Where(b => b.Definition.ConnectionId == connectionId))
            {
                try
                {
                    IReadOnlyList<byte[]> packets = active.Framer?.Push(frame.Data) ?? [frame.Data];
                    foreach (var packet in packets)
                    {
                        var writes = new Dictionary<string, double[]>();
                        foreach (var mapping in active.Definition.Mappings)
                        {
                            var values = ByteConversions.Convert(packet, mapping.Conversion, mapping.Offset, mapping.Repeating, mapping.Size).Select(v => v * mapping.Scale + mapping.Bias).ToArray();
                            if (values.Length == 0)
                                throw new InvalidDataException($"报文无法解码到 {mapping.Buffer}，已停止采集；未写入伪造数据。");
                            writes[mapping.Buffer] = values;
                        }

                        if (active.Definition.TimeBuffer is string time)
                            writes[time] = [runtime.ExperimentTime];
                        RecordInput(writes);
                        foreach (var(name, values)in writes)
                            runtime.ReceiveSamples(name, values);
                        revision++;
                    }
                }
                catch (Exception ex)
                {
                    journalHealthy = false;
                    runtime.Stop();
                    error = ex.Message;
                    revision++;
                    QueueFaultCleanup();
                }
            }
        }
    }

    void OpenJournal(bool complete = true)
    {
        if (journal is not null)
            return;
        journalId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        journalSequence = 0;
        journalHealthy = true;
        journalComplete = complete;
        journal = new StreamWriter(new FileStream(Path.Combine(recordings, journalId + ".active"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        journal.WriteLine(JsonSerializer.Serialize(new ReplayHeader { SourceXml = runtime!.Definition.SourceXml, CompleteReplay = complete }, StorageJson.Options));
    }

    void Record(ReplayEvent value)
    {
        if (journal is null)
            return;
        try
        {
            journal.WriteLine(JsonSerializer.Serialize(value with { Sequence = ++journalSequence }, StorageJson.Options));
        }
        catch
        {
            journalHealthy = false;
            throw;
        }
    }

    void RecordCommand(ReplayCommand command) => Record(new() { Sequence = 0, Kind = "command", Command = command });
    void RecordInput(IReadOnlyDictionary<string, double[]> writes, string[]? replacements = null) => Record(new() { Sequence = 0, Kind = "input", Buffers = writes.ToDictionary(p => p.Key, p => p.Value), ReplaceBuffers = replacements ?? [] });
    bool RunAndRecord(bool scheduled = false)
    {
        try
        {
            bool ran = scheduled ? runtime!.Tick() : runtime!.RunCycle();
            if (ran)
            {
                var c = runtime!.LastCycleContext!;
                Record(new() { Sequence = 0, Kind = "cycle", Cycle = c.Cycle, ExperimentTime = c.ExperimentTime, LinearTime = c.LinearTime, Offset1970 = c.Offset1970, LinearOffset1970 = c.LinearOffset1970, Info = runtime.LastCycleInfo.Count == 0 ? null : runtime.LastCycleInfo.ToDictionary(p => p.Key, p => p.Value) });
            }

            return ran;
        }
        catch
        {
            journalHealthy = false;
            throw;
        }
    }

    void SealJournal()
    {
        if (journal is null || journalId is null || !journalHealthy)
            return;
        journal.Flush();
        var active = Path.Combine(recordings, journalId + ".active");
        var target = Path.Combine(recordings, journalId + ".jsonl");
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(active, temp);
            using (var output = new FileStream(temp, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true))
                {
                    writer.WriteLine(JsonSerializer.Serialize(new ReplayEvent { Sequence = journalSequence + 1, Kind = "end" }, StorageJson.Options));
                    writer.Flush();
                }

                output.Flush(true);
            }

            File.Move(temp, target, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ioStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var networkWorker = RunNetworkWorker(ioStop.Token);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                lock (gate)
                {
                    if (runtime?.State != "running")
                        continue;
                    try
                    {
                        FlushNetwork();
                        bleInputs.Flush(data => IngestMedia(data, false));
                        media.FlushInputs();
                        FlushBrowserMedia();
                        UpdateBrowserAudioOutputLocked();
                        if (RunAndRecord(true))
                        {
                            media.UpdateOutputs();
                            BrowserAnalysisCompleted();
                            UpdateBrowserAudioOutputLocked();
                            if (runtime.State == "running") outputs.EnqueueAfterAnalysis(runtime.Definition, name => runtime.Buffers[name].Values, name =>
                            {
                                RecordInput(new Dictionary<string, double[]> { [name] = [] }, [name]);
                                runtime.ReceiveSamples(name, [], false);
                            });
                            revision++;
                        }
                    }
                    catch (Exception ex)
                    {
                        journalHealthy = false;
                        runtime.Stop();
                        error = ex.Message;
                        revision++;
                        QueueFaultCleanup();
                    }
                }
            }
        }
        catch (OperationCanceledException)when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await ioStop.CancelAsync();
            await networkWorker;
            devices.FrameReceived -= ReceiveFrame;
            devices.ConnectionFaulted -= DeviceFault;
            await StopAcquisitionAsync();
            await bleInputs.DisposeAsync();
            lock (gate)
            {
                if (runtime is not null)
                {
                    RecordCommand(new("stop"));
                    runtime.Stop();
                    SealJournal();
                }

                journal?.Dispose();
                journal = null;
            }
        }
    }
}

public sealed record ExportSnapshot(string Title, string SourceXml, Dictionary<string, double[]> Buffers, TimeMapping[] TimeMappings, long Revision);
public sealed record DataImportRequestOptions(DelimitedImportOptions? Delimited = null, Dictionary<string, string>? WaveMappings = null, bool Append = false, bool Analyze = false);
