// Judges turn test runs again from their recorded samples, with the current Core judge, so a rule can be fixed and
// checked without the game (docs/turn-tests.md).
// Usage: dotnet run --project tools/rejudge -- <lane-graph.json> <results-*.jsonl> [runs folder, default: beside the results]
using System.Globalization;
using System.Text.Json;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;

var graph = LoadGraph(args[0]);
if (args[1] == "stats")
{
    Console.WriteLine($"{graph.InferredTurns} junction entries with added turns, {graph.InferredUTurns} U-turns, {graph.TrimmedChains} straight ways in cut back");
    return;
}
if (args[1] == "show")
{
    // show <entry> <exit> [next exit-lane waypoints...]: the turn's vias and where each exit waypoint lies along the
    // exit lane, measured from the last via.
    int entry = int.Parse(args[2]), exit = int.Parse(args[3]);
    var vias = graph.TurnVias(entry, exit);
    string At(int w) => $"{w} ({graph.Position(w).X:0.0}, {graph.Position(w).Z:0.0})";
    Console.WriteLine($"entry {At(entry)}, vias {string.Join(", ", vias.Select(At))}, exit {At(exit)}");
    graph.PlainHeading(exit, out var ex, out var ez);
    var v = graph.Position(vias.Count > 0 ? vias[^1] : entry);
    var lane = new List<int> { exit };
    while (lane.Count < 6 && graph.Successors(lane[^1]).Count == 1) lane.Add(graph.Successors(lane[^1])[0]);
    foreach (var w in lane)
    {
        var p = graph.Position(w);
        float along = (p.X - v.X) * ex + (p.Z - v.Z) * ez, side = (p.X - v.X) * ez - (p.Z - v.Z) * ex;
        Console.WriteLine($"  {At(w)}: {along:0.0} m along the exit lane from the last via, via is {-side:0.0} m to its side");
    }
    return;
}
var resultsPath = args[1];
// --write <file>: also writes the results again with the new verdicts (for tools/tested-steps.py).
var writeAt = Array.IndexOf(args, "--write");
var writePath = writeAt > 0 && writeAt + 1 < args.Length ? args[writeAt + 1] : null;
var folder = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : Path.GetDirectoryName(Path.GetFullPath(resultsPath))!;
var written = new List<string>();
int same = 0, changed = 0, missing = 0;
var verdicts = new Dictionary<string, int>();
foreach (var line in File.ReadLines(resultsPath))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var run = JsonDocument.Parse(line).RootElement;
    if (!run.TryGetProperty("fixture", out var fx) || !run.TryGetProperty("samples", out var samplesName)) { missing++; written.Add(line); continue; }
    var samplesPath = Path.Combine(folder, samplesName.GetString()!);
    if (!File.Exists(samplesPath)) { missing++; written.Add(line); continue; }
    var fixture = ToFixture(fx);
    var judge = new Judge(graph, CaseGeometry.Build(graph, fixture), fixture);
    foreach (var row in File.ReadLines(samplesPath))
    {
        var f = row.Split(',');
        switch (f[0])
        {
            case "S": judge.Add(ToSample(f)); break;
            case "C": judge.Contact(int.Parse(f[1]), string.Join(",", f.Skip(2))); break;
            case "I": judge.Incident(int.Parse(f[1]), string.Join(",", f.Skip(2))); break;
            case "R": judge.Replanned(string.Join(",", f.Skip(1))); break;
        }
    }
    var end = run.TryGetProperty("end", out var e) ? e.GetString() : "";
    var result = judge.Result(end == "time limit");
    var oldVerdict = run.GetProperty("verdict").GetString();
    var oldReasons = run.TryGetProperty("judge", out var j) ? string.Join(" ", j.GetProperty("Reasons").EnumerateArray().Select(x => x.GetString())) : "";
    var newReasons = string.Join(" ", result.Reasons);
    verdicts[result.Verdict] = verdicts.GetValueOrDefault(result.Verdict) + 1;
    var differs = oldVerdict != result.Verdict || oldReasons != newReasons;
    var node = System.Text.Json.Nodes.JsonNode.Parse(line)!.AsObject();
    // An "avoided" run stays avoided; otherwise the new verdict, and the old one kept beside it.
    if (oldVerdict != "avoided") { node["gameVerdict"] = oldVerdict; node["verdict"] = result.Verdict; node["judge"] = System.Text.Json.Nodes.JsonNode.Parse(result.ToJson()); }
    written.Add(node.ToJsonString());
    if (differs) changed++; else same++;
    Console.WriteLine($"{run.GetProperty("run").GetString()} {fixture.Id,-36} {(differs ? "*" : " ")} {oldVerdict} [{oldReasons}] -> {result.Verdict} [{newReasons}] " +
        $"intrusion {result.MaxIntrusion:0.00} dev {result.PlainDeviationMax:0.00} gates {string.Join(",", result.GatesPassed)}" +
        (result.IntrusionDetail != null ? $"\n      {result.IntrusionDetail}" : ""));
}
if (writePath != null) File.WriteAllLines(writePath, written);
Console.WriteLine($"\n{same} unchanged, {changed} changed, {missing} without samples; now: {string.Join(", ", verdicts.Select(v => $"{v.Key} {v.Value}"))}");

static Sample ToSample(string[] f)
{
    float F(int i) => float.Parse(f[i], CultureInfo.InvariantCulture);
    Vec3 V(int i) => new Vec3(F(i), F(i + 1), F(i + 2));
    var footprint = new Vec3[8];
    for (var k = 0; k < 8; k++) footprint[k] = V(13 + k * 3);
    return new Sample
    {
        Step = int.Parse(f[1]), Time = double.Parse(f[2], CultureInfo.InvariantCulture), Centre = V(3), FrontAxle = V(6),
        HeadingDegrees = F(9), SpeedMs = F(10), Waiting = f[11] == "1", NearMiss = F(12), Footprint = footprint,
    };
}

static Fixture ToFixture(JsonElement j)
{
    List<int>? Ints(string name) => j.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetInt32()).ToList() : null;
    string? Str(string name) => j.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
    return new Fixture
    {
        Id = Str("Id") ?? "", Kind = Enum.Parse<CaseKind>(Str("Kind")!), Manoeuvre = Enum.Parse<CaseKind>(Str("Manoeuvre")!),
        Route = Ints("Route")!, Path = Ints("Path"), SlowPath = Ints("SlowPath"),
        EntryIndex = j.GetProperty("EntryIndex").GetInt32(), ExitIndex = j.GetProperty("ExitIndex").GetInt32(),
        TurnGateWaypoint = j.GetProperty("TurnGateWaypoint").GetInt32(), SharpnessDegrees = j.GetProperty("SharpnessDegrees").GetSingle(),
        PairId = Str("PairId"), IsMutation = j.GetProperty("IsMutation").GetBoolean(), ExpectedFailure = Str("ExpectedFailure"),
        RideLine = j.GetProperty("RideLine").GetInt32(),
    };
}

static LaneGraph LoadGraph(string path)
{
    var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    int n = root.GetArrayLength();
    var pos = new Vec3[n]; var nb = new int[n][]; var ol = new int[n][]; var mask = new int[n]; var inter = new bool[n]; var spd = new float[n];
    var i = 0;
    foreach (var w in root.EnumerateArray())
    {
        var p = w.GetProperty("p"); pos[i] = new Vec3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
        nb[i] = w.GetProperty("n").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        ol[i] = w.GetProperty("o").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        foreach (var a in w.GetProperty("a").EnumerateArray()) mask[i] |= 1 << a.GetInt32();
        inter[i] = w.GetProperty("x").GetInt32() == 1; spd[i] = w.GetProperty("s").GetSingle(); i++;
    }
    return new LaneGraph(pos, nb, mask, inter, spd, otherLanes: ol, inferLanes: true, inferTurns: true);
}
