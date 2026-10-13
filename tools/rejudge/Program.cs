// Judges turn test runs again from their recorded samples, with the current Core judge, so a rule can be fixed and
// checked without the game (docs/turn-tests.md).
// Usage: dotnet run --project tools/rejudge -- <lane-graph.json> <results-*.jsonl> [runs folder, default: beside the results]
//        dotnet run --project tools/rejudge -- <lane-graph.json> feasible <car> [options]: T1, see Feasible.cs
using System.Globalization;
using System.Text.Json;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;

var graph = LoadGraph(args[0]);
if (args[1] == "feasible")
{
    // T1 (docs/implementation-plan.md, "Tests"): every catalogue turn and U-turn simulated and judged for an NWH car.
    Feasible.Run(args[0], args.Skip(2).ToArray());
    return;
}
if (args[1] == "stats")
{
    Console.WriteLine($"{graph.InferredTurns} junction entries with added turns, {graph.InferredUTurns} U-turns and {graph.InferredWideUTurns} wider ones, {graph.TrimmedChains} straight ways in cut back");
    return;
}
if (args[1] == "carriageway")
{
    // carriageway x z [y]: whether a point counts as carriageway for the pavement rule (a junction's box, near a lane).
    var p = new Vec3(float.Parse(args[2], CultureInfo.InvariantCulture), args.Length > 4 ? float.Parse(args[4], CultureInfo.InvariantCulture) : 0f, float.Parse(args[3], CultureInfo.InvariantCulture));
    Console.WriteLine($"({p.X}, {p.Z}): junction box {graph.Junctions.Contains(p)}, carriageway {Pavement.OnALane(graph, p)} ({graph.Junctions.Count} junction boxes)");
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
if (args[1] == "drive")
{
    // drive <entry> <exit> <aim> [samples.csv]: the turn as the ride's world check models it (Core's TurnDrive, the
    // 6.7 m Dash at turn speed), and how far each pose lies from the recorded track's middle.
    // "u" for the exit or the aim: the entry's U-turn target.
    int entry = int.Parse(args[2]);
    int exit = args[3] == "u" ? graph.UTurn(entry) : int.Parse(args[3]), aim = args[4] == "u" ? graph.UTurn(entry) : int.Parse(args[4]);
    var targets = graph.TurnVias(entry, exit).Select(graph.Position).Append(graph.Position(aim)).ToList();
    targets.AddRange(DrivenRides.Core.Ride.TurnDrive.LaneAfter(graph, aim, 20f));
    if (!graph.PlainHeading(entry, out var dx, out var dz)) Console.WriteLine($"no plain heading at {entry}");
    var poses = DrivenRides.Core.Ride.TurnDrive.Drive(graph.Position(entry), dx, dz, targets,
        new DrivenRides.Core.Ride.CarShape(4.02f, 35f, 2.4f, Env("COM_UP", 0.6f), Env("COM_AHEAD", 0.39f)), Env("SPEED_KMH", 25f) / 3.6f, 3f, 1f) ?? new();
    var track = new List<(float X, float Z)>();
    if (args.Length > 5)
        foreach (var line in File.ReadLines(args[5]).Where(l => l.StartsWith("S,")))
        {
            var c = line.Split(',');
            track.Add((float.Parse(c[3], CultureInfo.InvariantCulture), float.Parse(c[5], CultureInfo.InvariantCulture)));
        }
    var worst = 0.0;
    foreach (var p in poses)
    {
        var off = track.Count == 0 ? 0 : track.Min(t => Math.Sqrt((t.X - p.X) * (t.X - p.X) + (t.Z - p.Z) * (t.Z - p.Z)));
        worst = Math.Max(worst, off);
        Console.WriteLine($"  ({p.X:0.00}, {p.Z:0.00}) heading {p.Heading * 180 / Math.PI:0}" + (track.Count > 0 ? $", {off:0.00} m from the track" : ""));
    }
    if (track.Count > 0) Console.WriteLine($"worst {worst:0.00} m");
    return;
}
if (args[1] == "arcline")
{
    // arcline <entry> <exit>: the driver's arc for the 6.7 m Dash, one point per line.
    var line = DrivenRides.Core.Ride.TurnArc.Fit(graph, int.Parse(args[2]), int.Parse(args[3]), Env("MIN_RADIUS", 5.74f));
    if (line != null) foreach (var p in line.Points) Console.WriteLine(FormattableString.Invariant($"{p.X:0.00} {p.Z:0.00}"));
    return;
}
if (args[1] == "arcs")
{
    // arcs [lookahead] [entry exit]: which catalogue turns get a driver's arc for the 6.7 m Dash, and how closely
    // the car follows it (front axle off the line, the middle of the car off it when it comes out).
    var look = args.Length > 2 ? float.Parse(args[2], CultureInfo.InvariantCulture) : 3f;
    // CAR="wheel distance,max steer,front axle,centre up,centre ahead" for another car (from the game log).
    var shape = (Environment.GetEnvironmentVariable("CAR") ?? "4.02,35,2.4,0.6,0.39").Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
    var dash = new DrivenRides.Core.Ride.CarShape(shape[0], shape[1], shape[2], shape[3], shape[4]);
    // FRONT_LOOK: the lookahead counted from the front axle (the pivot's lookahead is front axle + this).
    if (Environment.GetEnvironmentVariable("FRONT_LOOK") is string fl) look = dash.FrontAxle + float.Parse(fl, CultureInfo.InvariantCulture);
    var fixtures = Catalogue.Build(graph).Where(f => f.Kind == CaseKind.Turn);
    if (args.Length > 4) fixtures = fixtures.Where(f => f.Id == $"turn-{args[3]}-{args[4]}");
    int fitted = 0, total = 0;
    var worstAll = new List<double>();
    var rearAll = new List<double>();
    var middleAll = new List<double>();
    var radii = new List<double>();
    var wiggles = new List<double>();
    foreach (var f in fixtures)
    {
        total++;
        int entry = f.Route[f.EntryIndex], exit = f.Route[f.ExitIndex];
        var line = DrivenRides.Core.Ride.TurnArc.Fit(graph, entry, exit, Env("MIN_RADIUS", 5.74f));
        if (line == null) continue;
        fitted++;
        var poses = DrivenRides.Core.Ride.TurnDrive.Pursue(line, dash, 25 / 3.6f, Env("FACTOR", 3f), look, 0.25f, Environment.GetEnvironmentVariable("FROM_FRONT") == "1");
        if (poses == null) { Console.WriteLine($"{f.Id}: the car never gets round"); continue; }
        double Off(double x, double z)
        {
            var best = double.MaxValue;
            for (var i = 1; i < line.Points.Count; i++)
            {
                var a = line.Points[i - 1]; var b = line.Points[i];
                double dx = b.X - a.X, dz = b.Z - a.Z, span = dx * dx + dz * dz;
                var f = span > 0 ? Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / span, 0, 1) : 0;
                best = Math.Min(best, Math.Sqrt(Math.Pow(a.X + dx * f - x, 2) + Math.Pow(a.Z + dz * f - z, 2)));
            }
            return best;
        }
        // From where the rear axle passes the entry waypoint.
        var at = 0f;
        poses = poses.Where(p => (at = line.Project((float)(p.X - 1.62 * Math.Sin(p.Heading)), (float)(p.Z - 1.62 * Math.Cos(p.Heading)), at)) >= DrivenRides.Core.Ride.TurnArc.LeadIn).ToList();
        if (poses.Count == 0) continue;
        var worst = poses.Max(p => Off(p.X + 2.4 * Math.Sin(p.Heading), p.Z + 2.4 * Math.Cos(p.Heading)));
        // The rear axle, 1.62 m behind the pivot: how far the back of the car cuts inside.
        var rear = poses.Max(p => Off(p.X - 1.62 * Math.Sin(p.Heading), p.Z - 1.62 * Math.Cos(p.Heading)));
        var middle = poses.Max(p => Off(p.X, p.Z));
        rearAll.Add(rear); middleAll.Add(middle); radii.Add(line.Radius);
        var flips = 0; var lastRate = 0.0;
        for (var q = 1; q < poses.Count; q++)
        {
            var rate = Math.IEEERemainder(poses[q].Heading - poses[q - 1].Heading, 2 * Math.PI);
            if (Math.Abs(rate) < 0.002) continue;
            if (lastRate != 0 && Math.Sign(rate) != Math.Sign(lastRate)) flips++;
            lastRate = rate;
        }
        wiggles.Add(flips);
        var last = poses[^1];
        var outLine = line.Points.Min(q => Math.Sqrt(Math.Pow(q.X - last.X, 2) + Math.Pow(q.Z - last.Z, 2)));
        worstAll.Add(worst);
        if (args.Length > 4 || worst > 0.6) Console.WriteLine($"{f.Id}: {f.SharpnessDegrees:0} degrees, line {line.Length:0} m, front axle up to {worst:0.00} m off, middle {outLine:0.00} m off at the end");
    }
    string Spread(List<double> v) { v.Sort(); return v.Count == 0 ? "-" : $"median {v[v.Count / 2]:0.00} m, worst {v[^1]:0.00} m"; }
    radii.Sort();
    Console.WriteLine($"radius: min {radii[0]:0.0}, quartiles {radii[radii.Count / 4]:0.0} / {radii[radii.Count / 2]:0.0} / {radii[radii.Count * 3 / 4]:0.0}, max {radii[^1]:0.0} m");
    Console.WriteLine($"steering reversals per turn: {Spread(wiggles).Replace(" m", "")}");
    Console.WriteLine($"{fitted} of {total} turns get an arc; off the line: front axle {Spread(worstAll)}; middle {Spread(middleAll)}; rear axle {Spread(rearAll)}");
    return;
}
var resultsPath = args[1];
// --parking <m>: the parking lane's width the pavement rule allows past the outermost lane (Pavement.ParkingLane).
if (Array.IndexOf(args, "--left") > 0) Pavement.Left = true;
if (Array.IndexOf(args, "--parking") is var pk && pk > 0 && pk + 1 < args.Length) Pavement.ParkingLane = float.Parse(args[pk + 1], CultureInfo.InvariantCulture);
// --write <file>: also writes the results again with the new verdicts (for tools/tested-steps.py).
var writeAt = Array.IndexOf(args, "--write");
var writePath = writeAt > 0 && writeAt + 1 < args.Length ? args[writeAt + 1] : null;
var folder = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : Path.GetDirectoryName(Path.GetFullPath(resultsPath))!;
var written = new List<string>();
int same = 0, changed = 0, missing = 0, exempted = 0;
var lastY = 0f;
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
            case "S": { var sample = ToSample(f); lastY = sample.Footprint[0].Y; judge.Add(sample); break; }
            case "C": judge.Contact(int.Parse(f[1]), string.Join(",", f.Skip(2))); break;
            case "I": judge.Incident(int.Parse(f[1]), string.Join(",", f.Skip(2))); break;
            case "R": judge.Replanned(string.Join(",", f.Skip(1))); break;
            // Recorded with the game's pavement (the pedestrians' NavMesh): its findings, and no kerb estimate.
            case "N": judge.PavementAt = _ => false; break;
            case "P":
            {
                // The game's finding, unless its point is carriageway by today's rule (Pavement.OnALane: a junction's
                // box, any lane).
                var what = string.Join(",", f.Skip(2));
                var m = System.Text.RegularExpressions.Regex.Match(what, @"\((-?[0-9.]+); (-?[0-9.]+)\)");
                if (m.Success && Pavement.OnALane(graph, new Vec3(float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), lastY, float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))))
                {
                    exempted++;
                    break;
                }
                judge.OnThePavement(int.Parse(f[1]), what);
                break;
            }
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
Console.WriteLine($"\n{same} unchanged, {changed} changed, {missing} without samples, {exempted} recorded pavement findings now carriageway; now: {string.Join(", ", verdicts.Select(v => $"{v.Key} {v.Value}"))}");

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

static float Env(string name, float fallback) =>
    float.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

static LaneGraph LoadGraph(string path) => GraphFile.Load(path, out _);
