using System.Globalization;
using System.Text.Json;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Testing;

// T1 (docs/implementation-plan.md, "Tests"): the feasibility check (Core.Drive.Feasibility) on every catalogue turn and
// U-turn for one car, against the dumped lane graph, with no world (the kerb estimate stands in for it). One line per
// case, and the cases whose verdict changed since a previous run: the regression gate between two builds.
//
//   feasible <car> [--ids a,b,...|@file] [--kinds turn,uturn] [--margin m] [--out file] [--previous file]
//            [--rig results.jsonl] [--wide] [--parking m]
//
// <car>: limo, mersaididash, taxi (Cars.cs) or name;wheelbase;top km/h;cx,cy,cz;hx,hy,hz;kmh=deg,... (CarSpec.Parse; the game
// log's "NWH shape" line). --rig: compare with a rig run's NWH results for that car; each case
// is also driven as that run drove it (the arc whenever one fitted the lock at 10 km/h, no check), which says how well
// the model matches NWH on the same line. A rig verdict "avoided" (the game's check, with the world, left the case out)
// counts as the rig saying no.
static class Feasible
{
    public static void Run(string graphPath, string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Opt("--parking") is string pk) Pavement.ParkingLane = float.Parse(pk, CultureInfo.InvariantCulture);
        var car = Cars.Get(args[0]) ?? throw new ArgumentException("no measured car " + args[0]);
        var margin = Opt("--margin") is string m ? float.Parse(m, CultureInfo.InvariantCulture) : Feasibility.DefaultMargin;
        var graph = GraphFile.Load(graphPath, out var limits);
        float Limit(int w) => limits[w];
        // --pedestrian: every point counts as the pedestrians' ground (the worst the game's NavMesh can say), so the
        // pavement rule is decided by what counts as carriageway (lanes and junction boxes) alone.
        var check = new Feasibility(graph, car, null, margin, Limit) { PavementAt = Array.IndexOf(args, "--pedestrian") >= 0 ? _ => true : null };
        Console.WriteLine($"{car}; margin {margin:0.00} m");

        var kinds = (Opt("--kinds") ?? "turn,uturn").Split(',').ToHashSet();
        var cases = Catalogue.Build(graph).Where(f => kinds.Contains(f.Kind.ToString().ToLowerInvariant())).ToList();
        if (Opt("--ids") is string ids)
        {
            var set = (ids.StartsWith('@') ? File.ReadAllText(ids[1..]) : ids).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            cases = cases.Where(f => set.Contains(f.Id)).ToList();
        }

        // The rig's verdicts for this car, and the arc radius it drove with.
        var rig = new Dictionary<string, (string Verdict, string Reasons, int Arcs)>();
        float? rigRadius = null;
        if (Opt("--rig") is string rigFile)
            foreach (var line in File.ReadLines(rigFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var run = JsonDocument.Parse(line).RootElement;
                if (!run.TryGetProperty("driver", out var d) || d.GetString() != "nwh") continue;
                if (!(run.GetProperty("car").GetString() ?? "").EndsWith(car.Name, StringComparison.Ordinal)) continue;
                var reasons = run.TryGetProperty("judge", out var j) ? string.Join(" ", j.GetProperty("Reasons").EnumerateArray().Select(x => x.GetString())) : "";
                rig[run.GetProperty("case").GetString()!] = (run.GetProperty("verdict").GetString()!, reasons, run.TryGetProperty("arcs", out var a) ? a.GetInt32() : 0);
                if (run.TryGetProperty("minRadius", out var r)) rigRadius = r.GetSingle();
            }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rows = new List<string>();
        int feasible = 0, agree = 0, compared = 0, asRigAgree = 0, asRigCompared = 0;
        var confusion = new Dictionary<string, int>();
        foreach (var f in cases)
        {
            var v = check.Case(f);
            if (v.Feasible) feasible++;
            var row = $"{f.Id}\t{car.Name}\t{(v.Feasible ? "feasible" : "blocked")}\t{v.Form}\t{(v.Feasible ? "" : v.Reason)}";
            rows.Add(row);
            var extra = "";
            if (rig.TryGetValue(f.Id, out var r))
            {
                // As the rig drove it: the arc if one fitted its radius, else the vias; no check of either.
                var arc = r.Arcs > 0 && rigRadius != null ? (Func<int, int, Polyline?>)((e, x) => TurnArc.Fit(graph, e, x, rigRadius.Value)) : (e, x) => null;
                var asRig = check.Drive(f, DrivePlan.Build(graph, f.Route, car, arc, null, Limit), "as rig");
                // An avoided case is one the game's check (with the world) left out: the rig says the car can't.
                var rigPass = r.Verdict == "pass";
                compared++;
                if (rigPass == v.Feasible) agree++;
                asRigCompared++;
                if (rigPass == asRig.Feasible) asRigAgree++;
                var key = $"{f.Kind} rig {r.Verdict}, check {(v.Feasible ? "feasible" : "blocked")}";
                confusion[key] = confusion.GetValueOrDefault(key) + 1;
                extra = $"\trig {r.Verdict} [{r.Reasons}]; as the rig drove it: {(asRig.Feasible ? "pass" : "fail [" + asRig.Reason + "]")}, off {asRig.WorstOff:0.00} m";
            }
            Console.WriteLine(row + extra);
        }
        Console.WriteLine($"\n{cases.Count} cases, {feasible} feasible, {cases.Count - feasible} blocked; {check.Simulations} simulations in {watch.ElapsedMilliseconds} ms.");
        if (compared > 0)
        {
            Console.WriteLine($"Against the rig ({compared} cases): the check agrees on {agree}; driven as the rig drove them, the model agrees on {asRigAgree} of {asRigCompared}.");
            foreach (var (key, n) in confusion.OrderBy(p => p.Key)) Console.WriteLine($"  {key}: {n}");
        }
        if (Array.IndexOf(args, "--wide") >= 0)
        {
            // Each inner U-turn against its approach's wider ones (LaneGraph.UTurnGroup): the widest the car can make,
            // as the planner's width cost picks it when the lane changes are free.
            int inner = 0, any = 0, viaWider = 0, widerChosen = 0, total = 0;
            var wideIds = new List<string>();
            foreach (var f in cases.Where(c => c.Kind == CaseKind.UTurn && c.Id.StartsWith("uturn-", StringComparison.Ordinal)))
            {
                total++;
                int entry = f.Route[f.EntryIndex], t = f.Route[f.EntryIndex + 1];
                var innerVerdict = check.Step(entry, t);
                var group = graph.UTurnGroup(entry);
                foreach (var pair in group) if (graph.IsWideUTurn(pair.Entry, pair.Target)) wideIds.Add($"uturnw-{pair.Entry}-{pair.Target}");
                (int E, int X, float Width)? pick = null;
                foreach (var pair in group)
                    if (check.Step(pair.Entry, pair.Target).Feasible) { pick = pair; break; }
                if (innerVerdict.Feasible) inner++;
                if (pick != null) any++;
                if (!innerVerdict.Feasible && pick != null) viaWider++;
                if (pick != null && (pick.Value.E != entry || pick.Value.X != t)) widerChosen++;
                var innerWidth = group.FirstOrDefault(p => p.Entry == entry && p.Target == t).Width;
                Console.WriteLine($"{f.Id}: inner ({innerWidth:0} m) {(innerVerdict.Feasible ? "feasible" : "blocked")}; {group.Count} pairs; " +
                                  (pick is var (e, x, w) ? $"widest feasible {e} -> {x}, {w:0} m{(e == entry && x == t ? " (the inner one)" : "")}" : "none feasible: round the block"));
            }
            Console.WriteLine($"\nU-turns ({total} approaches): inner one feasible {inner}; some pair feasible {any}; feasible only via a wider pair {viaWider}; a wider pair is the widest feasible {widerChosen}.");
            Console.WriteLine("Wider pairs (rig ids): " + string.Join(",", wideIds));
            // Every pair through Gley's vias (as before the driver's curve) against on the curve.
            var vias = new Feasibility(graph, car, null, margin, Limit) { UTurnVias = true };
            int both = 0, curveOnly = 0, viasOnly = 0, neither = 0;
            foreach (var f in cases.Where(c => c.Kind == CaseKind.UTurn && c.Id.StartsWith("uturn-", StringComparison.Ordinal)))
                foreach (var pair in graph.UTurnGroup(f.Route[f.EntryIndex]))
                {
                    var curve = check.Step(pair.Entry, pair.Target);
                    var old = vias.Step(pair.Entry, pair.Target);
                    if (curve.Feasible && old.Feasible) both++;
                    else if (curve.Feasible) curveOnly++;
                    else if (old.Feasible) viasOnly++;
                    else neither++;
                    if (curve.Feasible != old.Feasible)
                        Console.WriteLine($"  {pair.Entry} -> {pair.Target} ({pair.Width:0} m): vias {(old.Feasible ? "feasible" : "blocked")}, curve {(curve.Feasible ? "feasible" : "blocked: " + curve.Reason)}");
                }
            Console.WriteLine($"U-turn pairs, vias against the driver's curve: both feasible {both}, curve only {curveOnly}, vias only {viasOnly}, neither {neither}.");
            // Out of each U-turn the car drives (simulated): overshoot past the exit lane and the swing back.
            foreach (var (name, k) in new[] { ("vias", vias), ("curve", check) })
            {
                var exits = cases.Where(c => c.Kind == CaseKind.UTurn && c.Id.StartsWith("uturn-", StringComparison.Ordinal))
                    .SelectMany(f => graph.UTurnGroup(f.Route[f.EntryIndex])).Select(p => k.Step(p.Entry, p.Target)).Where(v => v.Feasible).ToList();
                if (exits.Count == 0) continue;
                Console.WriteLine($"Exits on {name} ({exits.Count} feasible pairs): overshoot over 0.3 m {exits.Count(v => v.ExitOvershoot > 0.3f)}, worst {exits.Max(v => v.ExitOvershoot):0.00} m; swing back over 0.3 m {exits.Count(v => v.ExitSwingBack > 0.3f)}, worst {exits.Max(v => v.ExitSwingBack):0.00} m.");
                if (name == "curve" && Array.IndexOf(args, "--exits") >= 0)
                    foreach (var v in exits.Where(v => v.ExitOvershoot > 0.3f || v.ExitSwingBack > 0.3f))
                        Console.WriteLine($"  radius {v.Arc?.Radius:0.0} m: overshoot {v.ExitOvershoot:0.00}, swing back {v.ExitSwingBack:0.00}");
            }
        }
        if (Opt("--out") is string outFile) File.WriteAllLines(outFile, rows);
        if (Opt("--previous") is string previous)
        {
            var before = File.ReadLines(previous).Select(l => l.Split('\t')).Where(c => c.Length >= 4).ToDictionary(c => c[0] + "\t" + c[1], c => string.Join("\t", c.Skip(2)));
            int changed = 0;
            foreach (var row in rows)
            {
                var c = row.Split('\t');
                var key = c[0] + "\t" + c[1];
                var now = string.Join("\t", c.Skip(2));
                if (!before.TryGetValue(key, out var old)) { Console.WriteLine($"new      {c[0]}: {now}"); changed++; }
                else if (old.Split('\t')[0] != c[2] || old.Split('\t')[1] != c[3]) { Console.WriteLine($"changed  {c[0]}: {old.Replace('\t', ' ')} -> {now.Replace('\t', ' ')}"); changed++; }
                before.Remove(key);
            }
            foreach (var gone in before.Keys) { Console.WriteLine($"gone     {gone.Split('\t')[0]}"); changed++; }
            Console.WriteLine($"{changed} verdicts or forms changed against {previous}.");
            if (changed > 0) Environment.ExitCode = 1;
        }
    }
}
