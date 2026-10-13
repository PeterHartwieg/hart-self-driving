using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;

namespace DrivenRides.Core.Drive
{
    // The car's body box at one pose of a simulated drive: its centre in the world, its half size (the margins added)
    // and its heading (radians, as atan2(dx, dz)).
    public readonly struct BodyBox
    {
        public readonly Vec3 Centre, Half;
        public readonly float Heading;

        public BodyBox(Vec3 centre, Vec3 half, float heading) { Centre = centre; Half = half; Heading = heading; }
    }

    public sealed class Verdict
    {
        public bool Feasible;
        // How the manoeuvre was driven: "arc", "tight arc", "vias", or "route" for a whole case.
        public string Form = "";
        // Why not, empty when feasible: the judge's reasons, and what the body touched.
        public string Reason = "";
        public string? Hit;
        // The rear axle's pose where it failed (or, for a pass, furthest from the line), and that distance.
        public Pose Worst;
        public float WorstOff;
        public Polyline? Arc;
        public JudgeResult? Judge;
        public int Steps;

        // A copy with more said about why not.
        public Verdict With(string reason)
        {
            var copy = (Verdict)MemberwiseClone();
            copy.Reason += reason;
            return copy;
        }

        public override string ToString() => Feasible
            ? FormattableString.Invariant($"feasible ({Form}, rear axle up to {WorstOff:0.00} m off the line)")
            : FormattableString.Invariant($"not feasible ({Form}): {Reason} at ({Worst.X:0.0}, {Worst.Z:0.0}) heading {Worst.Heading * 180 / Math.PI:0}");
    }

    // What a car driven by its own physics can drive (docs/implementation-plan.md, "What the car can drive"): Core's
    // PursuitDriver drives CarModel along the line the ride would give it, through the rig's own case for that step
    // (Catalogue.ForStep), and the rig's judge (Core.Testing.Judge) judges the track: the lane, the far side early,
    // the exit, and contact by the body box against the world through the adapter's callback. So the planner asks the
    // question the rig asks: would this manoeuvre fail? An added turn is tried on its widest arc, then on the tightest
    // the car can follow, then through its vias; a U-turn through its vias only, and a car that can't make it goes
    // round the block. Verdicts are kept for the session (the world and the car don't change).
    public sealed class Feasibility
    {
        // The model's error against the rig's traces (tools/calibrate): the footprint and the body box are this much
        // larger on every side.
        public const float DefaultMargin = 0.2f;
        // The rig fails a run whose body box, 0.1 m larger, overlaps anything (near-miss).
        public const float NearMiss = 0.1f;
        private const float StepSeconds = 0.02f;
        // The world is checked every so many metres, from this far before the manoeuvre's entry on.
        private const float WorldEvery = 0.5f;
        private const float WorldFrom = 10f;
        // Offline only (no world callback): the kerb is half a lane past the outermost lane of a carriageway (the lane
        // graph's lanes are mostly 4 m apart), and a footprint more than 0.3 m past it (as the judge's intrusion) is
        // on the pavement, where the poles and hydrants the rig's cars hit stand. A rough stand-in: it misses the
        // corners inside junctions, where most of the rig's U-turn contacts were.
        public const float KerbOffset = 2f;
        private const float KerbTolerance = 0.3f;

        private readonly LaneGraph graph;
        private readonly CarSpec car;
        private readonly Func<BodyBox, string?>? world;
        private readonly Func<int, float>? limitKmh;
        private readonly CatalogueOptions options;
        private readonly float arcMinRadius;
        private readonly Dictionary<(int, int), Verdict> arcs = new Dictionary<(int, int), Verdict>();
        private readonly Dictionary<(int, int, int), Verdict> vias = new Dictionary<(int, int, int), Verdict>();
        private readonly Dictionary<(int, int), Verdict> steps = new Dictionary<(int, int), Verdict>();

        public float Margin { get; }
        public CarSpec Car => car;
        public int Simulations { get; private set; }

        // world(box): the name of what the body box overlaps, or null; null for no world (offline: the kerb estimate
        // stands in for it). limitKmh(waypoint): the road's limit, 0 for none.
        public Feasibility(LaneGraph graph, CarSpec car, Func<BodyBox, string?>? world = null, float margin = DefaultMargin,
            Func<int, float>? limitKmh = null, CatalogueOptions? options = null)
        {
            this.graph = graph;
            this.car = car;
            this.world = world;
            this.limitKmh = limitKmh;
            this.options = options ?? new CatalogueOptions();
            Margin = Math.Max(0f, margin);
            arcMinRadius = car.ArcMinRadius();
        }

        // The arc an added turn is driven along, if the car can drive one cleanly; null: through its vias.
        public Polyline? Arc(int entry, int exit)
        {
            var v = ArcVerdict(entry, exit);
            return v.Feasible ? v.Arc : null;
        }

        // Whether the car drives an added turn cleanly through its vias to the aim (PathExpander's clear).
        public bool Clear(int entry, int exit, int aim) => ViaVerdict(entry, exit, aim).Feasible;

        // One added step (a missing turn or a U-turn) in the form the planner gives it: an arc that works, else the
        // vias with the aim PathExpander picks. Not feasible: the planner blocks the step for this car.
        public Verdict Step(int entry, int exit)
        {
            if (steps.TryGetValue((entry, exit), out var known)) return known;
            Verdict verdict;
            if (graph.UTurn(entry) == exit) verdict = ViaVerdict(entry, exit, exit);
            else
            {
                verdict = ArcVerdict(entry, exit);
                if (!verdict.Feasible)
                {
                    var fixture = Catalogue.ForStep(graph, entry, exit, options);
                    var aim = exit;
                    if (fixture != null)
                    {
                        var expanded = PathExpander.Expand(graph, fixture.Route, (e, x, a) => e != entry || x != exit || ViaVerdict(e, x, a).Feasible);
                        if (expanded.Aims.TryGetValue((entry, exit), out var picked)) aim = picked.Aim;
                    }
                    var through = ViaVerdict(entry, exit, aim);
                    // A failed arc's reason is kept beside the vias' one, for the log.
                    verdict = through.Feasible || verdict.Reason.Length == 0 ? through : through.With("; arc: " + verdict.Reason);
                }
            }
            steps[(entry, exit)] = verdict;
            return verdict;
        }

        // A whole catalogue case as the planner would drive it (tools/rejudge's T1): a turn or U-turn case is its step;
        // any other case is driven with each added step in its planned form.
        public Verdict Case(Fixture fixture)
        {
            if ((fixture.Kind == CaseKind.Turn || fixture.Kind == CaseKind.UTurn) && fixture.Path == null)
                return Step(fixture.Route[fixture.EntryIndex], fixture.Route[fixture.EntryIndex + 1]);
            var plan = fixture.Path != null ? DrivePlan.Fixed(fixture.Path, fixture.SlowPath, car.TopKmh) : DrivePlan.Build(graph, fixture.Route, car, Arc, Clear, limitKmh);
            return Drive(fixture, plan, "route");
        }

        private Verdict ArcVerdict(int entry, int exit)
        {
            if (arcs.TryGetValue((entry, exit), out var known)) return known;
            Verdict verdict;
            var fixture = Catalogue.ForStep(graph, entry, exit, options);
            var widest = TurnArc.Fit(graph, entry, exit, arcMinRadius);
            if (widest == null) verdict = new Verdict { Form = "arc", Reason = "no arc fits the junction" };
            else if (fixture == null) verdict = new Verdict { Feasible = true, Form = "arc", Arc = widest };
            else
            {
                verdict = DriveArc(fixture, entry, exit, widest, "arc");
                // The tightest arc the car can follow keeps further from the inside corner than a wide one.
                var least = Math.Max(arcMinRadius * TurnArc.RadiusSlack, TurnArc.MinDriveRadius);
                if (!verdict.Feasible && widest.Radius > least + 1f && TurnArc.Fit(graph, entry, exit, arcMinRadius, least) is Polyline tight)
                {
                    var second = DriveArc(fixture, entry, exit, tight, "tight arc");
                    if (!second.Feasible) second.Reason += "; widest arc: " + verdict.Reason;
                    verdict = second;
                }
            }
            arcs[(entry, exit)] = verdict;
            return verdict;
        }

        private Verdict DriveArc(Fixture fixture, int entry, int exit, Polyline arc, string form)
        {
            var plan = DrivePlan.Build(graph, fixture.Route, car, (e, x) => e == entry && x == exit ? arc : null, null, limitKmh);
            var verdict = Drive(fixture, plan, form);
            verdict.Arc = arc;
            return verdict;
        }

        private Verdict ViaVerdict(int entry, int exit, int aim)
        {
            if (vias.TryGetValue((entry, exit, aim), out var known)) return known;
            var fixture = Catalogue.ForStep(graph, entry, exit, options);
            Verdict verdict;
            if (fixture == null) verdict = new Verdict { Feasible = true, Form = "vias" };
            else
            {
                // The aim given, if the case's run-out has it; else the one PathExpander picks without a check.
                var plan = DrivePlan.Build(graph, fixture.Route, car, (e, x) => null, (e, x, a) => e != entry || x != exit || a == aim, limitKmh);
                verdict = Drive(fixture, plan, "vias");
            }
            vias[(entry, exit, aim)] = verdict;
            return verdict;
        }

        // The case driven on the plan's line from a standstill at its first waypoint, as the rig loads a car, until
        // the judge passes its last gate or fails it.
        public Verdict Drive(Fixture fixture, DrivePlan plan, string form)
        {
            Simulations++;
            var line = plan.Line(graph);
            var driver = plan.Driver(line, car.Wheelbase, car.FrontAhead, car.TrackWidth);
            var geometry = CaseGeometry.Build(graph, fixture);
            var judge = new Judge(graph, geometry, fixture);
            var kerbs = world == null ? Kerbs(fixture) : null;
            var path = plan.Path;
            var start = graph.Position(path[0]);
            var next = line.Line.At(1f);
            var model = car.Model(start.X, start.Z, Math.Atan2(next.X - start.X, next.Z - start.Z));
            var entryAlong = 0f;
            for (var k = 0; k < path.Count; k++) if (path[k] == fixture.Route[fixture.EntryIndex]) { entryAlong = line.AlongOf[k]; break; }
            var half = new Vec3(car.BodyHalf.X + Margin, car.BodyHalf.Y, car.BodyHalf.Z + Margin);
            var worldHalf = new Vec3(half.X + NearMiss, half.Y, half.Z + NearMiss);
            var length = Route.Fixed(graph, fixture.Route).LengthMetres;
            var limit = 30.0 + length / 4.0;
            var verdict = new Verdict { Form = form };
            var lastWorld = double.NegativeInfinity;
            var travelled = 0.0;
            var time = 0.0;
            var timedOut = true;
            for (var step = 1; time < limit; step++)
            {
                var o = driver.Step(model.Input(StepSeconds));
                double x0 = model.X, z0 = model.Z;
                model.Step(o, StepSeconds);
                time += StepSeconds;
                travelled += Math.Sqrt((model.X - x0) * (model.X - x0) + (model.Z - z0) * (model.Z - z0));
                var ground = line.Line.At(driver.Along).Y;
                var pose = new Pose((float)model.X, ground, (float)model.Z, (float)model.Heading);
                var off = Math.Abs(o.Lateral);
                if (driver.Along > entryAlong - WorldFrom && off > verdict.WorstOff && !judge.Failed) { verdict.WorstOff = off; verdict.Worst = pose; }
                var footprint = Footprint(pose, half);
                judge.Add(new Sample
                {
                    Step = step, Time = time, Centre = Local(pose, car.BodyCentre), FrontAxle = Local(pose, new Vec3(0f, 0f, car.Wheelbase)),
                    HeadingDegrees = (float)(model.Heading * 180 / Math.PI), Footprint = footprint, SpeedMs = (float)Math.Abs(model.Speed),
                });
                if (driver.Along > entryAlong - WorldFrom && travelled - lastWorld >= WorldEvery)
                {
                    lastWorld = travelled;
                    string? hit = null;
                    if (world != null) hit = world(new BodyBox(Local(pose, car.BodyCentre), worldHalf, pose.Heading));
                    else if (kerbs != null) hit = Kerb(kerbs, footprint);
                    if (hit != null && !judge.Failed)
                    {
                        judge.Contact(step, hit);
                        verdict.Hit = hit;
                    }
                }
                if (judge.Failed && verdict.Steps == 0) { verdict.Steps = step; verdict.Worst = pose; verdict.WorstOff = off; }
                if (judge.Failed || judge.Finished) { timedOut = false; break; }
                if (o.Along + car.FrontAhead >= line.Line.Length - 1.5f && Math.Abs(model.Speed) < 0.3) { timedOut = false; break; }
            }
            var result = judge.Result(timedOut);
            verdict.Judge = result;
            verdict.Feasible = result.Verdict == "pass";
            if (!verdict.Feasible) verdict.Reason = (result.Reasons.Count > 0 ? string.Join(" ", result.Reasons) : result.Verdict) + (verdict.Hit != null ? " (" + verdict.Hit + ")" : "");
            return verdict;
        }

        // A point given in the car's own space (from the rear axle on the ground) at a pose.
        private static Vec3 Local(Pose pose, Vec3 p)
        {
            float s = (float)Math.Sin(pose.Heading), c = (float)Math.Cos(pose.Heading);
            return new Vec3(pose.X + p.X * c + p.Z * s, pose.Y + p.Y, pose.Z - p.X * s + p.Z * c);
        }

        // The footprint as the rig's TestCar gives it: four corners, then four edge midpoints, at the bottom of the box.
        private Vec3[] Footprint(Pose pose, Vec3 half)
        {
            var c = car.BodyCentre;
            var bottom = c.Y - car.BodyHalf.Y;
            float x = half.X, z = half.Z;
            return new[]
            {
                Local(pose, new Vec3(c.X - x, bottom, c.Z + z)), Local(pose, new Vec3(c.X + x, bottom, c.Z + z)),
                Local(pose, new Vec3(c.X + x, bottom, c.Z - z)), Local(pose, new Vec3(c.X - x, bottom, c.Z - z)),
                Local(pose, new Vec3(c.X, bottom, c.Z + z)), Local(pose, new Vec3(c.X + x, bottom, c.Z)),
                Local(pose, new Vec3(c.X, bottom, c.Z - z)), Local(pose, new Vec3(c.X - x, bottom, c.Z)),
            };
        }

        // The case's plain lane stretches, for the offline kerb estimate.
        private List<LaneStretch> Kerbs(Fixture fixture)
        {
            var result = new List<LaneStretch>();
            var route = fixture.Route;
            for (var k = 0; k + 1 < route.Count; k++)
            {
                int a = route[k], b = route[k + 1];
                if (graph.IsIntersection(a) || graph.IsIntersection(b) || !Has(graph.Successors(a), b)) continue;
                var stretch = new LaneStretch(graph, graph.Position(a), graph.Position(b), k);
                stretch.Min = -1f;
                stretch.Max = stretch.Length + 1f;
                result.Add(stretch);
            }
            return result;
        }

        // A footprint point past the kerb of the stretch it stands beside (the nearest one along whose length it is):
        // past the outermost lane on the right by half a lane, where no oncoming lane is. Only the nearest, since a
        // U-turn's run-out runs back beside its run-up; only the right, since a U-turn crosses the median's side at its
        // opening, and a divided street's carriageways are too far apart for either to see the other's lanes.
        private static string? Kerb(List<LaneStretch> kerbs, Vec3[] footprint)
        {
            foreach (var p in footprint)
            {
                LaneStretch? nearest = null;
                var nearestSide = float.MaxValue;
                foreach (var s in kerbs)
                {
                    var along = s.Along(p);
                    if (along < s.Min || along > s.Max || Math.Abs(p.Y - s.Height(along)) > 2f) continue;
                    var distance = Math.Abs(s.Side(p) - (s.OwnLeft + s.OwnRight) / 2f);
                    if (distance < nearestSide) { nearestSide = distance; nearest = s; }
                }
                if (nearest == null || !float.IsPositiveInfinity(nearest.Right)) continue;
                var past = nearest.Side(p) - nearest.OwnRight - KerbOffset;
                if (past > KerbTolerance)
                    return FormattableString.Invariant($"kerb (estimate) right of route segment {nearest.Index}, {past:0.00} m past it at ({p.X:0.0}, {p.Z:0.0})");
            }
            return null;
        }

        private static bool Has(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }
    }
}
