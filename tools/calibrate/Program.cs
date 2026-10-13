// Calibrates Core's CarModel against the turn rig's NWH traces (docs/implementation-plan.md, "What the car can drive":
// the measured error becomes the feasibility check's margin). For each <run>-nwh.csv, with its samples file and the
// results file beside it:
//   1. the wheel: NWH's steering angle against the input the driver wrote (input x lock at the speed), with the
//      smoothing and the wheel's rate fitted over all traces;
//   2. the body, open loop: from each recorded pose, the bicycle model driven by the recorded wheel angle and speed for
//      1 and 2 s, and how far it ends from where the car was;
//   3. closed loop, on rig cases driven by the driver as it was then (0.1.51-nwh1: pure pursuit from the rear axle,
//      no lag allowance) on the line the rig built, at the recorded speed: how far the model's rear axle is from the
//      line against the car's at the same point. That difference is the margin to use.
// Usage: dotnet run --project tools/calibrate -- <lane-graph.json> <run-nwh.csv>...
using System.Globalization;
using System.Text.Json;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var graph = GraphFile.Load(args[0], out _);
var runs = new List<Run>();
foreach (var path in args.Skip(1))
{
    Run? run;
    try { run = Run.Load(path); }
    catch (InvalidDataException ex) { Console.WriteLine($"{Path.GetFileName(path)}: {ex.Message}"); continue; }
    if (run == null) { Console.WriteLine($"{Path.GetFileName(path)}: no samples or results beside it"); continue; }
    runs.Add(run);
    Console.WriteLine($"{run.Name}: {run.Case} ({run.Car.Name}, {run.Version}), {run.Rows.Count} steps, samples offset {run.Offset}");
}
const float Dt = 0.02f;

// 1. The wheel.
(double Rms, double Max) WheelError(float smooth, float rate)
{
    double sum = 0, max = 0;
    var n = 0;
    foreach (var run in runs)
    {
        double smoothed = 0, wheel = 0;
        foreach (var r in run.Rows)
        {
            var error = Math.Abs(wheel - r.Angle);
            sum += error * error;
            max = Math.Max(max, error);
            n++;
            var lockNow = run.Car.LockAt(Math.Abs(r.Speed));
            var want = Math.Clamp(r.Input * lockNow, -lockNow, lockNow);
            smoothed += (want - smoothed) * (smooth > 0 ? Math.Min(1, Dt / smooth) : 1);
            wheel += Math.Clamp(smoothed - wheel, -rate * Dt, rate * Dt);
        }
    }
    return (Math.Sqrt(sum / Math.Max(1, n)), max);
}
var best = (Smooth: CarModel.SmoothSeconds, Rate: CarModel.WheelRate, Rms: WheelError(CarModel.SmoothSeconds, CarModel.WheelRate).Rms);
for (var smooth = 0f; smooth <= 0.4001f; smooth += 0.02f)
    for (var rate = 60f; rate <= 600f; rate += 20f)
    {
        var (rms, _) = WheelError(smooth, rate);
        if (rms < best.Rms - 1e-6) best = (smooth, rate, rms);
    }
var defaults = WheelError(CarModel.SmoothSeconds, CarModel.WheelRate);
var fitted = WheelError(best.Smooth, best.Rate);
Console.WriteLine($"\nWheel: NWH's angle against the model's from the written input. Model's values ({CarModel.SmoothSeconds:0.00} s, {CarModel.WheelRate:0}°/s): RMS {defaults.Rms:0.00}°, worst {defaults.Max:0.0}°. " +
                  $"Fitted: smoothing {best.Smooth:0.00} s, rate {best.Rate:0}°/s: RMS {fitted.Rms:0.00}°, worst {fitted.Max:0.0}°.");

// 2. Open loop.
foreach (var seconds in new[] { 1f, 2f })
{
    var errors = new List<double>();
    var modelled = new List<double>();
    foreach (var run in runs)
    {
        var steps = (int)Math.Round(seconds / Dt);
        for (var k = 0; k + steps < run.Rows.Count; k += 5)
        {
            if (run.Rows[k].Speed < 1f || run.Pose(k) is not var (x0, z0, h0)) continue;
            if (run.Pose(k + steps) is not var (x1, z1, h1)) continue;
            // The recorded wheel, and the model's wheel from the recorded input.
            double x = x0, z = z0, h = h0;
            var car = new CarModel(run.Car.Wheelbase, run.Car.LockAt, x0, z0, h0, run.Rows[k].Speed) { Smooth = best.Smooth, Rate = best.Rate, TrackWidth = run.Car.TrackWidth };
            car.SetWheel(run.Rows[k].Angle);
            for (var j = k; j < k + steps; j++)
            {
                var r = run.Rows[j];
                h += r.Speed * CarModel.Curvature(r.Angle, run.Car.Wheelbase, run.Car.TrackWidth) * Dt;
                x += r.Speed * Math.Sin(h) * Dt;
                z += r.Speed * Math.Cos(h) * Dt;
                car.Speed = r.Speed;
                car.Step(new DriveOutput { SteerDegrees = r.Input * run.Car.LockAt(Math.Abs(r.Speed)), Desired = r.Speed }, Dt);
            }
            // Sideways to the car's recorded heading at the end, the error that matters for a lane.
            double Side(double px, double pz) => Math.Abs((px - x1) * Math.Cos(h1) - (pz - z1) * Math.Sin(h1));
            errors.Add(Side(x, z));
            modelled.Add(Side(car.X, car.Z));
        }
    }
    Console.WriteLine($"Open loop, {seconds:0} s from each recorded pose ({errors.Count} starts), sideways: recorded wheel {Spread(errors)}; modelled wheel {Spread(modelled)}.");
}

// The front track: NWH's Ackermann makes the car turn wider than a bicycle with the same wheel angle (CarModel.Curvature).
// Fitted per car to the open loop over 1 s, against the track the car's spec gives.
var tracks = new Dictionary<string, float>();
foreach (var group in runs.GroupBy(r => r.Car.Name))
{
    double Error(float track)
    {
        double sum = 0;
        var n = 0;
        foreach (var run in group)
            for (var k = 0; k + 50 < run.Rows.Count; k += 5)
            {
                if (run.Rows[k].Speed < 1f || run.Pose(k) is not var (x, z, h) || run.Pose(k + 50) is not var (x1, z1, h1)) continue;
                for (var j = k; j < k + 50; j++)
                {
                    var r = run.Rows[j];
                    h += r.Speed * CarModel.Curvature(r.Angle, run.Car.Wheelbase, track) * Dt;
                    x += r.Speed * Math.Sin(h) * Dt;
                    z += r.Speed * Math.Cos(h) * Dt;
                }
                var side = (x - x1) * Math.Cos(h1) - (z - z1) * Math.Sin(h1);
                sum += side * side;
                n++;
            }
        return Math.Sqrt(sum / Math.Max(1, n));
    }
    var car = group.First().Car;
    var fit = (Track: 0f, Rms: Error(0f));
    for (var track = 0.5f; track <= 3.0001f; track += 0.01f)
    {
        var rms = Error(track);
        if (rms < fit.Rms) fit = (track, rms);
    }
    tracks[car.Name] = fit.Track;
    Console.WriteLine($"Front track for {car.Name}: fitted {fit.Track:0.00} m (turns {CarModel.YawGain(car.Wheelbase, fit.Track):0.00} as sharply as a bicycle), open loop 1 s RMS {fit.Rms:0.00} m; " +
                      $"a plain bicycle {Error(0f):0.00} m; the spec's {car.TrackWidth:0.00} m {Error(car.TrackWidth):0.00} m.");
}

// 3. Closed loop on rig cases that passed: nwh1's driver for the spike's runs, Core's PursuitDriver (steering for the
// car's track, as NwhDrive does since 0.1.55) for the planner's runs, each on the line the rig built.
var closed = new Dictionary<string, List<double>>();
var exits = new Dictionary<string, List<double>>();
foreach (var run in runs.Where(r => r.Fixture != null && r.Verdict == "pass" && (r.Version.EndsWith("nwh1", StringComparison.Ordinal) || r.Plan != null)))
{
    var f = run.Fixture!;
    var nwh1 = run.Plan == null;
    var spec = run.Car;
    int entry = f.Route[f.EntryIndex], exit = f.Route[f.EntryIndex + 1];
    var least = Math.Max(spec.ArcMinRadius() * TurnArc.RadiusSlack, TurnArc.MinDriveRadius);
    Func<int, int, Polyline?> arc = nwh1
        ? (run.Arcs > 0 ? (e, x) => TurnArc.Fit(graph, e, x, run.MinRadius) : (e, x) => null)
        : (e, x) => e != entry || x != exit ? null
            : run.Plan == "arc" ? TurnArc.Fit(graph, e, x, spec.ArcMinRadius())
            : run.Plan == "tight arc" ? TurnArc.Fit(graph, e, x, spec.ArcMinRadius(), least) : null;
    var plan = DrivePlan.Build(graph, f.Route, run.Car, arc);
    var line = plan.Line(graph);
    var driver = nwh1 ? null : plan.Driver(line, spec.Wheelbase, spec.FrontAhead, spec.TrackWidth);
    // The driver as it drove these runs: no feed-forward before 0.1.65.
    if (driver != null) driver.FeedForward = string.CompareOrdinal(run.Version, "0.1.65") >= 0;
    // The recorded speed by distance along the line, and the car's lateral offset there.
    var byAlong = run.Rows.Where(r => r.Speed > 0.05f).Select(r => (r.Along, r.Speed, r.Lateral)).ToList();
    if (byAlong.Count < 10) continue;
    // The line rebuilt here must be the one the car drove: the recorded rear axle, located on it, at the recorded
    // distance along (a lane graph dumped by another game build infers other vias).
    var mismatch = 0.0;
    var checkedRows = 0;
    var from = 0f;
    for (var k = 0; k < run.Rows.Count; k += 10)
    {
        if (run.Pose(k) is not var (px, pz, _)) continue;
        var (at, _) = line.Locate((float)px, (float)pz, Math.Max(0f, from - 5f), 30f);
        from = at;
        mismatch = Math.Max(mismatch, Math.Abs(at - run.Rows[k].Along));
        checkedRows++;
    }
    if (checkedRows == 0 || mismatch > 3) { Console.WriteLine($"Closed loop {run.Name} ({run.Case}): left out, its line isn't the one rebuilt here ({mismatch:0.0} m apart)."); continue; }
    var start = graph.Position(plan.Path[0]);
    var next = line.Line.At(1f);
    // The model's track: the prefab's (as the planner uses it), the fitted one with TRACK=fit, none with BICYCLE=1.
    var modelTrack = Environment.GetEnvironmentVariable("BICYCLE") == "1" ? 0f : Environment.GetEnvironmentVariable("TRACK") == "fit" ? tracks[run.Car.Name] : spec.TrackWidth;
    var car = new CarModel(run.Car.Wheelbase, run.Car.LockAt, start.X, start.Z, Math.Atan2(next.X - start.X, next.Z - start.Z))
        { Smooth = best.Smooth, Rate = best.Rate, TrackWidth = modelTrack, YawFactor = float.TryParse(Environment.GetEnvironmentVariable("YAW"), NumberStyles.Float, CultureInfo.InvariantCulture, out var yf) ? yf : 1f };
    var uTurn = run.Fixture!.Kind == DrivenRides.Core.Testing.CaseKind.UTurn;
    var startHeading = car.Heading;
    var exitPhase = false;
    double exitWorst = 0;
    var along = 0f;
    double worst = 0, worstAt = 0, widestModel = 0, widestCar = 0;
    var lastAlong = byAlong[^1].Along;
    for (var step = 0; step < 20000 && along < lastAlong - 0.5f; step++)
    {
        along = line.Line.Project((float)car.X, (float)car.Z, along);
        var (speed, recordedLateral) = At(byAlong, along);
        var here = line.Line.At(along);
        var lateral = (car.X - here.X) * Math.Cos(car.Heading) - (car.Z - here.Z) * Math.Sin(car.Heading);
        var difference = Math.Abs(lateral - recordedLateral);
        // From 10 m on: the first metres from a standstill are on the handbrake and the clutch, not the steering.
        if (along > 10f && difference > worst) { worst = difference; worstAt = along; }
        if (uTurn && !exitPhase && Math.Abs(DriveLine.Wrap(car.Heading - startHeading - Math.PI)) < 20 * Math.PI / 180) exitPhase = true;
        if (exitPhase) exitWorst = Math.Max(exitWorst, difference);
        if (Environment.GetEnvironmentVariable("SERIES") == run.Name && step % 25 == 0)
            Console.WriteLine($"    {along,6:0.0} m: speed {speed:0.0}, model {lateral,6:0.00}, car {recordedLateral,6:0.00}");
        widestModel = Math.Max(widestModel, Math.Abs(lateral));
        widestCar = Math.Max(widestCar, Math.Abs(recordedLateral));
        double steer;
        car.Speed = Math.Max(0.3f, speed);
        if (driver != null) steer = driver.Step(car.Input(Dt)).SteerDegrees;
        else
        {
            // nwh1's steering: pure pursuit from the rear axle, max(wheelbase + 1.5, 1.5 + 0.45 v) ahead.
            var look = Math.Max(run.Car.Wheelbase + 1.5f, 1.5f + 0.45f * speed);
            var target = line.Line.At(along + look);
            double tx = target.X - car.X, tz = target.Z - car.Z;
            var alpha = DriveLine.Wrap(Math.Atan2(tx, tz) - car.Heading);
            steer = Math.Atan(run.Car.Wheelbase * 2 * Math.Sin(alpha) / Math.Max(1, Math.Sqrt(tx * tx + tz * tz))) * 180 / Math.PI;
        }
        car.Step(new DriveOutput { SteerDegrees = (float)steer, Desired = (float)car.Speed }, Dt);
    }
    if (!closed.TryGetValue(run.Car.Name, out var list)) closed[run.Car.Name] = list = new List<double>();
    list.Add(worst);
    if (uTurn)
    {
        if (!exits.TryGetValue(run.Car.Name, out var e)) exits[run.Car.Name] = e = new List<double>();
        e.Add(exitWorst);
    }
    if (Environment.GetEnvironmentVariable("VERBOSE") == "1" || worst > 0.3)
        Console.WriteLine($"Closed loop {run.Name} ({run.Case}, {run.Car.Name}, {run.Plan ?? "nwh1"}): model off the car's lateral by up to {worst:0.00} m (at {worstAt:0} m along); widest off the line: model {widestModel:0.00} m, car {widestCar:0.00} m.");
}
foreach (var (name, list) in exits)
    Console.WriteLine($"U-turn exits, {name}, {list.Count} passed runs: model off the car's lateral {Spread(list)}.");
foreach (var (name, list) in closed)
    Console.WriteLine($"Closed loop, {name}, {list.Count} passed runs: {Spread(list)}. Margin to use: {list.Max():0.00} m{(list.Max() > 0.5 ? " (over 0.5 m: the plan says wider margins, not more model)" : "")}.");

static (float Speed, float Lateral) At(List<(float Along, float Speed, float Lateral)> rows, float along)
{
    if (along <= rows[0].Along) return (rows[0].Speed, rows[0].Lateral);
    for (var i = 1; i < rows.Count; i++)
        if (rows[i].Along >= along)
        {
            var span = rows[i].Along - rows[i - 1].Along;
            var f = span > 1e-4f ? (along - rows[i - 1].Along) / span : 1f;
            return (rows[i - 1].Speed + (rows[i].Speed - rows[i - 1].Speed) * f, rows[i - 1].Lateral + (rows[i].Lateral - rows[i - 1].Lateral) * f);
        }
    return (rows[^1].Speed, rows[^1].Lateral);
}

static string Spread(List<double> values)
{
    if (values.Count == 0) return "-";
    var v = values.OrderBy(x => x).ToList();
    return $"median {v[v.Count / 2]:0.00} m, 95% {v[(int)(v.Count * 0.95)]:0.00} m, worst {v[^1]:0.00} m";
}

sealed class Run
{
    public string Name = "", Case = "", Version = "", Verdict = "";
    // The planner's form for the manoeuvre (0.1.55 on: arc, tight arc, vias); null for the spike's runs.
    public string? Plan;
    public CarSpec Car = null!;
    public DrivenRides.Core.Testing.Fixture? Fixture;
    public int Arcs, Offset;
    public float MinRadius;
    public readonly List<(float T, float Along, float Lateral, float Speed, float Steer, float Input, float Angle)> Rows = new();
    private readonly List<(double X, double Z, double Heading)> poses = new();

    public (double X, double Z, double Heading)? Pose(int row)
    {
        var i = row + Offset;
        return i >= 0 && i < poses.Count ? poses[i] : null;
    }

    public static Run? Load(string tracePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(tracePath))!;
        var name = Path.GetFileName(tracePath).Replace("-nwh.csv", "");
        var samplesPath = Path.Combine(folder, name + "-samples.csv");
        var resultsPath = Directory.GetFiles(folder, "results-*.jsonl").FirstOrDefault();
        if (!File.Exists(samplesPath) || resultsPath == null) return null;
        JsonElement? line = null;
        foreach (var l in File.ReadLines(resultsPath))
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            var e = JsonDocument.Parse(l).RootElement;
            if (e.GetProperty("run").GetString() == name) { line = e; break; }
        }
        if (line is not JsonElement r) return null;
        var run = new Run
        {
            Name = name, Case = r.GetProperty("case").GetString()!, Version = r.GetProperty("version").GetString()!,
            Car = Cars.Get(r.GetProperty("car").GetString()!) ?? throw new InvalidDataException("no measured car " + r.GetProperty("car").GetString()),
            Arcs = r.TryGetProperty("arcs", out var a) ? a.GetInt32() : 0, MinRadius = r.TryGetProperty("minRadius", out var m) ? m.GetSingle() : 0f,
            Verdict = r.GetProperty("verdict").GetString() ?? "", Plan = r.TryGetProperty("plan", out var p) ? p.GetString() : null,
        };
        if (r.TryGetProperty("fixture", out var fx) && fx.ValueKind == JsonValueKind.Object)
            run.Fixture = new DrivenRides.Core.Testing.Fixture
            {
                Id = fx.GetProperty("Id").GetString()!, Route = fx.GetProperty("Route").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                EntryIndex = fx.GetProperty("EntryIndex").GetInt32(), ExitIndex = fx.GetProperty("ExitIndex").GetInt32(),
                Kind = Enum.Parse<DrivenRides.Core.Testing.CaseKind>(fx.GetProperty("Kind").GetString()!),
                Manoeuvre = Enum.Parse<DrivenRides.Core.Testing.CaseKind>(fx.GetProperty("Manoeuvre").GetString()!),
            };
        float F(string v) => float.Parse(v, CultureInfo.InvariantCulture);
        foreach (var l in File.ReadLines(tracePath).Skip(1))
        {
            var c = l.Split(',');
            if (c.Length < 8) continue;
            run.Rows.Add((F(c[0]), F(c[1]), F(c[2]), F(c[3]), F(c[5]), F(c[6]), F(c[7])));
        }
        var speeds = new List<float>();
        foreach (var l in File.ReadLines(samplesPath).Where(l => l.StartsWith("S,")))
        {
            var c = l.Split(',');
            // The front axle and the heading; the rear axle is a wheelbase behind.
            double h = F(c[9]) * Math.PI / 180;
            run.poses.Add((F(c[6]) - run.Car.Wheelbase * Math.Sin(h), F(c[8]) - run.Car.Wheelbase * Math.Cos(h), h));
            speeds.Add(F(c[10]));
        }
        // Both are written each physics step, but may start a step or two apart: the offset where the speeds match.
        var bestError = double.MaxValue;
        for (var o = -5; o <= 5; o++)
        {
            double sum = 0;
            var n = 0;
            for (var i = 0; i < run.Rows.Count; i++)
                if (i + o >= 0 && i + o < speeds.Count) { sum += Math.Abs(Math.Abs(run.Rows[i].Speed) - speeds[i + o]); n++; }
            if (n > 0 && sum / n < bestError) { bestError = sum / n; run.Offset = o; }
        }
        return run;
    }
}
