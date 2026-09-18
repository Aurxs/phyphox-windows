using Phyphox.Core;
using System.Text.Json;

if (args.FirstOrDefault() == "--doppler-empty")
{
    var definition = ExperimentParser.Parse(File.ReadAllText("../official-reference/phyphox-android/app/src/main/assets/experiments/doppler.phyphox"));
    var runtime = new ExperimentRuntime(definition);
    runtime.RunCycle();
    if (runtime.Buffers["recording"].Values.Length != 0) throw new Exception("Empty microphone unexpectedly filled");
    Console.WriteLine("PASS official Doppler initialization with empty microphone input");
    return 0;
}

if (args.FirstOrDefault() == "--translations")
{
    const string xml = "<phyphox version='1.20'><title>Base</title><category>Base category</category><translations><translation locale='en'><title>English</title></translation><translation locale='zh_Hant'><title>繁體</title></translation><translation locale='zh_Hans'><title>简体标题</title><category>中文分类</category><string original='signal'>信号</string><string original='[1]*2'>不得替换公式</string></translation></translations><data-containers><container init='3'>signal</container><container>result</container></data-containers><views><view label='signal'><value label='signal'><input>signal</input></value></view></views><analysis><formula formula='[1]*2'><input keep='true'>signal</input><output>result</output></formula></analysis></phyphox>";
    var definition = ExperimentParser.Parse(xml, "zh-CN");
    var metadata = new ExperimentLocalization(System.Xml.Linq.XElement.Parse(xml), "zh-rCN");
    if (definition.Title != "简体标题" || metadata.Text("title") != definition.Title || metadata.Text("category") != "中文分类") throw new Exception("Simplified Chinese metadata selection failed");
    var runtime = new ExperimentRuntime(definition); runtime.RunCycle();
    if ((string?)definition.Views[0].Attribute("label") != "信号" || definition.Views[0].Descendants("input").Single().Value != "signal" || runtime.Buffers["result"].Last != 6) throw new Exception("Display translation modified runtime references or computation");
    var lightXml = File.ReadAllText("../official-reference/phyphox-android/app/src/main/assets/experiments/light.phyphox");
    var light = ExperimentParser.Parse(lightXml, "zh-CN");
    if (light.Title != "亮度") throw new Exception("Official light simplified title failed");
    var withoutChinese = System.Xml.Linq.XElement.Parse(lightXml);
    withoutChinese.Descendants("translation").Where(e => ((string?)e.Attribute("locale"))?.StartsWith("zh") == true).ToList().ForEach(e => e.Remove());
    if (ExperimentParser.Parse(withoutChinese.ToString(), "zh-CN").Title != "Light" || new ExperimentLocalization(withoutChinese).Text("title") != "Light") throw new Exception("Official light without Chinese English fallback failed");
    bool rejected = false;
    try { ExperimentParser.Parse("<phyphox version='1.20'><translations><translation locale='en'><description>No required metadata</description></translation></translations></phyphox>"); }
    catch (FormatException) { rejected = true; }
    if (!rejected) throw new Exception("Missing root and selected translation metadata was accepted");
    var acceleratorXml = File.ReadAllText("../official-reference/phyphox-android/app/src/main/assets/experiments/accelerometer.phyphox");
    var englishGraph = ExperimentParser.Parse(acceleratorXml, "en").Views.SelectMany(v => v.Descendants("graph")).First();
    var chineseGraph = ExperimentParser.Parse(acceleratorXml, "zh-CN").Views.SelectMany(v => v.Descendants("graph")).First();
    if ((string?)englishGraph.Attribute("labelX") != "t" || (string?)englishGraph.Attribute("unitX") != "s" || (string?)englishGraph.Attribute("labelY") != "a" || (string?)englishGraph.Attribute("unitY") != "m/s²" || (string?)chineseGraph.Attribute("unitX") != "秒") throw new Exception("Official accelerometer display resource expansion failed");
    if (metadata.Translate("[[unknown_key]]") != "[[unknown_key]]") throw new Exception("Unknown resource was invented");
    Console.WriteLine("PASS translations: simplified aliases, display-only translation and official light English fallback");
    return 0;
}

if (args.FirstOrDefault() == "--button-cycle")
{
    var definition = ExperimentParser.Parse("<phyphox version='1.20'><title>Button cycle</title><category>Checks</category><data-containers><container init='2'>x</container><container>y</container></data-containers><analysis onUserInput='true'><formula formula='[1]*[1]'><input keep='true'>x</input><output>y</output></formula></analysis></phyphox>");
    var runtime = new ExperimentRuntime(definition);
    runtime.Start();
    if (!runtime.Tick() || runtime.Tick()) throw new Exception("onUserInput scheduling failed");
    runtime.NotifyUserInput();
    if (!runtime.Tick() || runtime.Buffers["y"].Last != 4 || runtime.Tick()) throw new Exception("Button request must produce exactly one pending analysis cycle without changing a buffer");
    Console.WriteLine("Button-only user-input scheduling: passed.");
    return 0;
}
if (args.FirstOrDefault() == "--corpus")
    return CorpusAudit.Run(args[1]);
var path = args.FirstOrDefault() ?? "../official-reference/phyphox-docs/corpus/analysis/vectors";
int passed = 0, failed = 0, blocked = 0;
foreach (var file in Directory.GetFiles(path, "*.phyphox", SearchOption.AllDirectories).Order())
{
    try
    {
        var definition = ExperimentParser.Parse(File.ReadAllText(file));
        if (definition.CapabilityProblems.Count > 0)
        {
            blocked++;
            Console.WriteLine($"BLOCKED {Path.GetRelativePath(path, file)}: {string.Join(",", definition.CapabilityProblems)}");
            continue;
        }

        var runtime = new ExperimentRuntime(definition);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(file, ".expected.json")));
        var root = json.RootElement;
        var tol = root.GetProperty("default_tolerance");
        for (int cycle = 1; cycle <= root.GetProperty("cycles").GetInt32(); cycle++)
        {
            runtime.RunCycle();
            foreach (var expectation in root.GetProperty("expect").EnumerateArray().Where(e => e.GetProperty("after_cycle").GetInt32() == cycle))
                foreach (var property in expectation.GetProperty("buffers").EnumerateObject())
                {
                    var actual = runtime.Buffers[property.Name].Values;
                    var expected = property.Value.GetProperty("values").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? XmlUtil.Number(v.GetString()!) : v.GetDouble()).ToArray();
                    double abs = property.Value.TryGetProperty("abs", out var ab) ? ReadNumber(ab) : ReadNumber(tol.GetProperty("abs")), rel = property.Value.TryGetProperty("rel", out var re) ? ReadNumber(re) : ReadNumber(tol.GetProperty("rel"));
                    if (actual.Length != expected.Length)
                        throw new Exception($"cycle {cycle} {property.Name}: length {actual.Length} != {expected.Length}");
                    for (int i = 0; i < actual.Length; i++)
                    {
                        double a = actual[i], e = expected[i];
                        if (double.IsNaN(e) ? !double.IsNaN(a) : double.IsInfinity(e) ? a != e : !double.IsFinite(a) || Math.Abs(a - e) > abs + rel * Math.Abs(e))
                            throw new Exception($"cycle {cycle} {property.Name}[{i}]: {a} != {e}");
                    }
                }
        }

        passed++;
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL {Path.GetRelativePath(path, file)}: {e.Message}");
    }
}

if (args.Skip(1).Contains("--vectors-only"))
{
    Console.WriteLine($"Official golden vectors: passed={passed}, failed={failed}, explicitly blocked={blocked}. Blocked is NOT passed.");
    return failed > 0 || blocked > 0 ? 1 : 0;
}

var lifecycle = ExperimentParser.Parse("""
<phyphox version="1.20"><title>Lifecycle</title><category>test</category><data-containers><container size="4" init="1">gate</container><container size="4">out</container></data-containers><analysis requireFill="gate"><append><input keep="true">gate</input><output append="true">out</output></append></analysis></phyphox>
""");
var life = new ExperimentRuntime(lifecycle);
life.RunCycle();
if (!life.RunCycle())
    throw new Exception("requireFill defaults to one value, not capacity");
life.Start();
if (life.State != "running")
    throw new Exception("Start state");
life.Pause();
var frozen = life.ExperimentTime;
if (life.State != "paused" || life.ExperimentTime != frozen)
    throw new Exception("Pause time");
life.Stop();
if (life.Buffers["out"].Count != 2)
    throw new Exception("Stop retains results");
life.Clear();
if (life.ExperimentTime != 0 || life.Buffers["gate"].Last != 1 || life.Buffers["out"].Count != 0)
    throw new Exception("Clear restores initial state");
foreach (var formula in new[]
{
    "sin(1,2)",
    "unknown(1)",
    "atan2(1)"
}

)
{
    bool rejected = false;
    try
    {
        _ = new Formula(formula);
    }
    catch (FormatException)
    {
        rejected = true;
    }

    if (!rejected)
        throw new Exception("Invalid formula accepted: " + formula);
}

bool dtdRejected = false;
try
{
    ExperimentParser.Parse("<!DOCTYPE phyphox [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><phyphox><title>&x;</title></phyphox>");
}
catch (System.Xml.XmlException)
{
    dtdRejected = true;
}

if (!dtdRejected)
    throw new Exception("DTD accepted");
var fft = ExperimentParser.Parse("""
<phyphox version="1.20"><title>FFT</title><category>test</category><data-containers><container size="5" init="1,2,3,4,5">in</container><container size="5">re</container><container size="5">im</container></data-containers><analysis><fft><input as="re">in</input><output as="re">re</output><output as="im">im</output></fft></analysis></phyphox>
""");
var fftRuntime = new ExperimentRuntime(fft);
fftRuntime.RunCycle();
for (int k = 0; k < 5; k++)
{
    double re = 0, im = 0;
    for (int j = 0; j < 5; j++)
    {
        re += (j + 1) * Math.Cos(-2 * Math.PI * k * j / 5);
        im += (j + 1) * Math.Sin(-2 * Math.PI * k * j / 5);
    }

    if (Math.Abs(fftRuntime.Buffers["re"].Values[k] - re) > 1e-10 || Math.Abs(fftRuntime.Buffers["im"].Values[k] - im) > 1e-10)
        throw new Exception("Arbitrary-length FFT mismatch");
}

if (new Formula("heaviside(0)").Evaluate([], 0) != 1)
    throw new Exception("Heaviside at zero");
var grouped = ExperimentParser.Parse("""
<phyphox version="1.20"><title>Clear</title><category>test</category><data-containers><container init="1">ordinary</container><container init="2" clearGroup="calibration">cal</container><container init="3" clearGroup="_">protected</container></data-containers></phyphox>
""");
var gr = new ExperimentRuntime(grouped);
gr.SetBuffer("ordinary", [9]);
gr.SetBuffer("cal", [8]);
gr.SetBuffer("protected", [7]);
gr.Clear();
if (gr.Buffers["ordinary"].Last != 1 || gr.Buffers["cal"].Last != 8 || gr.Buffers["protected"].Last != 7)
    throw new Exception("Default clear erased grouped data");
gr.SetBuffer("ordinary", [9]);
gr.ClearGroups(["calibration", "_"]);
if (gr.Buffers["ordinary"].Last != 1 || gr.Buffers["cal"].Last != 2 || gr.Buffers["protected"].Last != 7)
    throw new Exception("Selected clear group semantics");
const string aliasSource = """
<phyphox version="1.20"><title>Alias</title><category>Test</category><data-containers><container init="1">a</container><container size="5">out</container></data-containers><analysis><add><input>a</input><input>a</input><output>out</output></add></analysis></phyphox>
""";
var aliases = new ExperimentRuntime(ExperimentParser.Parse(aliasSource));
aliases.RunCycle();
if (aliases.Buffers["out"].Count != 0 || aliases.Buffers["a"].Count != 0)
    throw new Exception("Duplicate consuming input must observe sequential consumption.");
var keptAliases = new ExperimentRuntime(ExperimentParser.Parse(aliasSource.Replace("<input>a</input><input>a</input>", "<input keep=\"true\">a</input><input>a</input>")));
keptAliases.RunCycle();
if (keptAliases.Buffers["out"].Last != 2)
    throw new Exception("Keeping the first alias must preserve the second input.");
var stateSource = File.ReadAllText(Path.GetFullPath(Path.Combine(path, "..", "..", "generated", "events-state.phyphox")));
var restoredState = new ExperimentRuntime(ExperimentParser.Parse(stateSource));
if (restoredState.State != "paused" || restoredState.ExperimentTime != 12.5 || restoredState.TimeMappings.Count != 4 || restoredState.TimeMappings[^1].SystemTime.ToUnixTimeMilliseconds() != 1755080067250L || !restoredState.Buffers["values"].Values.SequenceEqual([1, 2, 3]))
    throw new Exception("Official state fixture did not restore its data and event time.");
restoredState.Start();
if (restoredState.TimeMappings.Count != 5 || restoredState.TimeMappings[^1].ExperimentTime != 12.5)
    throw new Exception("Restored experiment did not continue from paused elapsed time.");
restoredState.Stop();
restoredState.Clear();
if (restoredState.TimeMappings.Count != 0 || restoredState.ExperimentTime != 0)
    throw new Exception("Clear retained historical state time.");
var openStateXml = System.Xml.Linq.XDocument.Parse(stateSource);
openStateXml.Root!.Elements().First(e => e.Name.LocalName == "events").Elements().Last().Remove();
var openState = new ExperimentRuntime(ExperimentParser.Parse(openStateXml.ToString()));
bool openBlocked = false;
try
{
    openState.RunCycle();
}
catch (NotSupportedException)
{
    openBlocked = true;
}

if (!openBlocked || openState.TimeMappings.Count != 3)
    throw new Exception("Open-ended saved interval must not fabricate elapsed time.");
openState.Clear();
openState.Start();
openState.Stop();
Console.WriteLine("Focused input-alias consumption and official saved-state timing restoration: passed.");
Console.WriteLine("Focused clear-group protection: passed.");
Console.WriteLine("Focused arbitrary-length FFT and Heaviside boundary: passed.");
Console.WriteLine("Focused lifecycle, requireFill threshold, formula arity, and XML DTD rejection: passed.");
Console.WriteLine($"Official golden vectors: passed={passed}, failed={failed}, explicitly blocked={blocked}. Blocked is NOT passed.");
return failed > 0 ? 1 : 0;
static double ReadNumber(JsonElement e) => e.ValueKind == JsonValueKind.String ? XmlUtil.Number(e.GetString()!) : e.GetDouble();
