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
        // Out of a U-turn (the heading within 20 degrees of the exit lane's): how far the rear axle ran past the line on
        // the outside, and how far it then swung back past it on the inside (Peter: "the second half overshoots and
        // then a correction goes back the other way").
        public float ExitOvershoot, ExitSwingBack;
        public Polyline? Arc;
        public JudgeResult? Judge;
        public int Steps;

        // Whether another verdict drives the step the same way: the same form and, on an arc, the same curve within
        // ShapeTolerance at its ends and middle and in length. A check run again builds a new arc object for the same
        // curve (review round 2: comparing the objects replanned the ride at every added turn).
        public bool SameShape(Verdict other)
        {
            if (Feasible != other.Feasible || Form != other.Form) return false;
            if (Arc == null || other.Arc == null) return Arc == null && other.Arc == null;
            if (Math.Abs(Arc.Length - other.Arc.Length) > ShapeTolerance) return false;
            foreach (var f in new[] { 0f, 0.5f, 1f })
                if (Vec3.HorizontalDistance(Arc.At(Arc.Length * f), other.Arc.At(other.Arc.Length * f)) > ShapeTolerance) return false;
            return true;
        }

        public const float ShapeTolerance = 0.25f;

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
        // larger on every side. T2 (session 20261013-182118, 134 passed runs of the limo, the Dash and the taxi,
        // Core's driver): the model's rear axle came within 0.5 m of the car's everywhere, 0.35 m in 95% of runs; the
        // worst were the swings out of U-turns, which the model makes a little narrower than the car.
        public const float DefaultMargin = 0.5f;
        // The rig fails a run whose body box, 0.1 m larger, overlaps anything (near-miss).
        public const float NearMiss = 0.1f;
        // A step with no case to drive it on (Catalogue.ForStep: a U-turn whose far side doesn't leave the junction)
        // can't be checked, so it isn't driven (review round 1: it was taken as feasible unchecked).
        public const string NoCase = "no case to check it on";
        // The world callback names what it found with this in front when it can move away (a parked car): a
        // manoeuvre failed by it is checked again later, one failed by the built world is not (NwhPlanner.CauseOf).
        public const string Movable = "movable: ";
        // Settled in its lane: the rear axle this close to the line and the wheel this straight.
        private const float SettledOff = 0.2f;
        private const float SettledWheel = 5f;
        private const float StepSeconds = 0.02f;
        // The world is checked every so many metres, from this far before the manoeuvre's entry on.
        private const float WorldEvery = 0.5f;
        private const float WorldFrom = 10f;
        // Once the rear axle is past the manoeuvre's exit waypoint, it keeps within this of its line: back in the
        // target lane, not in the next one.
        public const float OffLaneLimit = 1.5f;

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
        // The ground's height under a point (the game casts a ray down onto the road, the pavement), or null; null
        // for no ground (offline). With it, the wheels must stay on the carriageway: NWH's wheels ride on ray casts,
        // so a wheel up on the pavement touches no collider (main's 0.1.56 rig kerb check, Peter, turn test 0.1.55).
        public Func<Vec3, float?>? GroundAt { get; set; }
        // Whether a point is pedestrian ground (the judge's PavementAt); null offline, where the judge's geometric
        // kerb estimate stands in.
        public Func<Vec3, bool>? PavementAt { get; set; }
        // U-turns through Gley's vias instead of a driver's curve (TurnArc.FitUTurn), for T1's comparison only.
        public bool UTurnVias { get; set; }
        // As the rig's kerb check: a wheel this much above the lowest is on a kerb (a road's camber across the car is
        // a few centimetres, a kerb about 15), here at two world samples in a row (1 m).
        public const float KerbRise = 0.1f;
        private const int KerbSamples = 2;
        public CarSpec Car => car;
        public int Simulations { get; private set; }

        // world(box): the name of what the body box overlaps, or null; null for no world (offline: only the judge's
        // pavement rule stands in for it). limitKmh(waypoint): the road's limit, 0 for none.
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

        // Each check below is also a piece of work that yields as it goes (review round 3: a planning job's time
        // budget couldn't stop a simulation, the slow part, once started): after each look at the world and every
        // YieldSteps simulation steps. The plain calls run the work to its end.
        public const int YieldSteps = 50;

        private static void Run(IEnumerable<object?> work)
        {
            foreach (var _ in work) { }
        }

        // The arc an added turn is driven along, if the car can drive one cleanly; null: through its vias.
        public Polyline? Arc(int entry, int exit)
        {
            Run(ArcWork(entry, exit));
            return KnownArc(entry, exit, out var arc) ? arc : null;
        }

        // What Arc gives, if it is known without a simulation.
        private bool KnownArc(int entry, int exit, out Polyline? arc)
        {
            arc = null;
            if (graph.IsUTurn(entry, exit) && UTurnVias) return true;
            if (!arcs.TryGetValue((entry, exit), out var v)) return false;
            arc = v.Feasible ? v.Arc : null;
            return true;
        }

        private IEnumerable<object?> ArcWork(int entry, int exit) =>
            graph.IsUTurn(entry, exit) ? (UTurnVias ? Array.Empty<object?>() : UTurnWork(entry, exit)) : TurnArcWork(entry, exit);

        // Whether the car drives an added turn cleanly through its vias to the aim (PathExpander's clear).
        public bool Clear(int entry, int exit, int aim)
        {
            Run(ViaWork(entry, exit, aim));
            return vias[(entry, exit, aim)].Feasible;
        }

        // One added step (a missing turn or a U-turn) in the form the planner gives it: an arc that works, else the
        // vias with the aim PathExpander picks. Not feasible: the planner blocks the step for this car.
        public Verdict Step(int entry, int exit)
        {
            Run(StepWork(entry, exit));
            return steps[(entry, exit)];
        }

        public IEnumerable<object?> StepWork(int entry, int exit)
        {
            if (steps.ContainsKey((entry, exit))) yield break;
            Verdict verdict;
            if (graph.IsUTurn(entry, exit))
            {
                if (UTurnVias)
                {
                    foreach (var y in ViaWork(entry, exit, exit)) yield return y;
                    verdict = vias[(entry, exit, exit)];
                }
                else
                {
                    foreach (var y in UTurnWork(entry, exit)) yield return y;
                    verdict = arcs[(entry, exit)];
                }
            }
            else
            {
                foreach (var y in TurnArcWork(entry, exit)) yield return y;
                verdict = arcs[(entry, exit)];
                if (!verdict.Feasible)
                {
                    var fixture = Catalogue.ForStep(graph, entry, exit, options);
                    var aim = exit;
                    if (fixture != null)
                    {
                        // The aim PathExpander picks, its questions answered one simulation at a time: each pass stops
                        // at the first unknown answer, which is then worked out (the same answers, in the same order, as
                        // asking each in turn).
                        // Each pass works out one more answer, so this ends (review round 4: a limit could end it
                        // with answers missing).
                        while (true)
                        {
                            int? need = null;
                            var expanded = PathExpander.Expand(graph, fixture.Route, (e, x, a) =>
                            {
                                if (e != entry || x != exit) return true;
                                if (need != null) return false;
                                if (vias.TryGetValue((e, x, a), out var v)) return v.Feasible;
                                need = a;
                                return false;
                            });
                            if (need is int a0) { foreach (var y in ViaWork(entry, exit, a0)) yield return y; continue; }
                            if (expanded.Aims.TryGetValue((entry, exit), out var picked)) aim = picked.Aim;
                            break;
                        }
                    }
                    foreach (var y in ViaWork(entry, exit, aim)) yield return y;
                    var through = vias[(entry, exit, aim)];
                    // A failed arc's reason is kept beside the vias' one, for the log.
                    verdict = through.Feasible || verdict.Reason.Length == 0 ? through : through.With("; arc: " + verdict.Reason);
                }
            }
            steps[(entry, exit)] = verdict;
        }

        // The checks a plan of a route needs (DrivePlan.Build asks Arc and Clear through PathExpander), worked out
        // ahead as yielding work, so the Build that follows finds them all known. The same answers in the same order:
        // each pass stops at the first unknown one.
        // Each pass works out one more answer, so it ends with all of them known (review round 4: after 64 passes it
        // stopped, and Build ran the rest at once).
        public IEnumerable<object?> PrepareWork(IReadOnlyList<int> route)
        {
            while (true)
            {
                (int, int)? needArc = null;
                (int, int, int)? needVia = null;
                PathExpander.Expand(graph, route,
                    (e, x, a) =>
                    {
                        if (needArc != null || needVia != null) return false;
                        if (vias.TryGetValue((e, x, a), out var v)) return v.Feasible;
                        needVia = (e, x, a);
                        return false;
                    },
                    (e, x) =>
                    {
                        if (needArc != null || needVia != null) return false;
                        if (KnownArc(e, x, out var arc)) return arc != null;
                        needArc = (e, x);
                        return false;
                    });
                if (needArc is var (ae, ax)) { foreach (var y in ArcWork(ae, ax)) yield return y; }
                else if (needVia is var (ve, vx, va)) { foreach (var y in ViaWork(ve, vx, va)) yield return y; }
                else yield break;
            }
        }

        // Whether a step's verdict is known (cached): one forgotten (Forget) is checked afresh when next asked.
        public bool Knows(int entry, int exit) => steps.ContainsKey((entry, exit));

        // Forgets what is known about a step, so it is checked again: the world near it may have loaded since (the
        // game parks cars only near the player, and they are part of the world check).
        public void Forget(int entry, int exit)
        {
            steps.Remove((entry, exit));
            arcs.Remove((entry, exit));
            var stale = new List<(int, int, int)>();
            foreach (var key in vias.Keys) if (key.Item1 == entry && key.Item2 == exit) stale.Add(key);
            foreach (var key in stale) vias.Remove(key);
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

        private IEnumerable<object?> TurnArcWork(int entry, int exit)
        {
            if (arcs.ContainsKey((entry, exit))) yield break;
            Verdict verdict;
            var fixture = Catalogue.ForStep(graph, entry, exit, options);
            var widest = TurnArc.Fit(graph, entry, exit, arcMinRadius);
            if (widest == null) verdict = new Verdict { Form = "arc", Reason = "no arc fits the junction" };
            else if (fixture == null) verdict = new Verdict { Form = "arc", Reason = NoCase };
            else
            {
                verdict = new Verdict { Form = "arc" };
                foreach (var y in DriveArcWork(fixture, entry, exit, widest, verdict)) yield return y;
                // The tightest arc the car can follow keeps further from the inside corner than a wide one.
                var least = Math.Max(arcMinRadius * TurnArc.RadiusSlack, TurnArc.MinDriveRadius);
                if (!verdict.Feasible && widest.Radius > least + 1f && TurnArc.Fit(graph, entry, exit, arcMinRadius, least) is Polyline tight)
                {
                    var second = new Verdict { Form = "tight arc" };
                    foreach (var y in DriveArcWork(fixture, entry, exit, tight, second)) yield return y;
                    if (!second.Feasible) second.Reason += "; widest arc: " + verdict.Reason;
                    verdict = second;
                }
            }
            arcs[(entry, exit)] = verdict;
        }

        // A U-turn on a driver's curve (TurnArc.FitUTurn) at the car's tightest followable circle at the slowest corner
        // speed; too narrow for it, the car goes round the block.
        private IEnumerable<object?> UTurnWork(int entry, int exit)
        {
            if (arcs.ContainsKey((entry, exit))) yield break;
            Verdict verdict;
            var fixture = Catalogue.ForStep(graph, entry, exit, options);
            var minRadius = car.RadiusAt(PursuitDriver.MinCornerMs);
            var line = TurnArc.FitUTurn(graph, entry, exit, minRadius, out var why);
            if (line == null)
                verdict = new Verdict { Form = "u-curve", Reason = FormattableString.Invariant($"no U-turn curve, {graph.UTurnWidth(entry, exit):0.0} m across: {why}") };
            else if (fixture == null) verdict = new Verdict { Form = "u-curve", Reason = NoCase };
            else
            {
                verdict = new Verdict { Form = "u-curve" };
                foreach (var y in DriveArcWork(fixture, entry, exit, line, verdict)) yield return y;
            }
            arcs[(entry, exit)] = verdict;
        }

        private IEnumerable<object?> DriveArcWork(Fixture fixture, int entry, int exit, Polyline arc, Verdict verdict)
        {
            var plan = DrivePlan.Build(graph, fixture.Route, car, (e, x) => e == entry && x == exit ? arc : null, null, limitKmh);
            foreach (var y in DriveWork(fixture, plan, verdict)) yield return y;
            verdict.Arc = arc;
        }

        private IEnumerable<object?> ViaWork(int entry, int exit, int aim)
        {
            if (vias.ContainsKey((entry, exit, aim))) yield break;
            var fixture = Catalogue.ForStep(graph, entry, exit, options);
            var verdict = new Verdict { Form = "vias" };
            if (fixture == null) verdict.Reason = NoCase;
            else
            {
                // The aim given, if the case's run-out has it; else the one PathExpander picks without a check.
                var plan = DrivePlan.Build(graph, fixture.Route, car, (e, x) => null, (e, x, a) => e != entry || x != exit || a == aim, limitKmh);
                foreach (var y in DriveWork(fixture, plan, verdict)) yield return y;
            }
            vias[(entry, exit, aim)] = verdict;
        }

        // The case driven on the plan's line from a standstill at its first waypoint, as the rig loads a car, until
        // the judge passes its last gate or fails it.
        public Verdict Drive(Fixture fixture, DrivePlan plan, string form)
        {
            var verdict = new Verdict { Form = form };
            Run(DriveWork(fixture, plan, verdict));
            return verdict;
        }

        // Drive as work: fills `verdict` (its Form given).
        private IEnumerable<object?> DriveWork(Fixture fixture, DrivePlan plan, Verdict verdict)
        {
            Simulations++;
            var line = plan.Line(graph);
            var driver = plan.Driver(line, car.Wheelbase, car.FrontAhead, car.TrackWidth);
            var geometry = CaseGeometry.Build(graph, fixture);
            var judge = new Judge(graph, geometry, fixture) { PavementAt = PavementAt };
            var path = plan.Path;
            var start = graph.Position(path[0]);
            var next = line.Line.At(1f);
            var startHeading = Math.Atan2(next.X - start.X, next.Z - start.Z);
            var model = car.Model(start.X, start.Z, startHeading);
            var exitPhase = false;
            float entryAlong = 0f, exitAlong = float.MaxValue;
            for (var k = 0; k < path.Count; k++) if (path[k] == fixture.Route[fixture.EntryIndex]) { entryAlong = line.AlongOf[k]; break; }
            for (var k = path.Count - 1; k >= 0; k--) if (path[k] == fixture.Route[fixture.ExitIndex]) { exitAlong = line.AlongOf[k]; break; }
            // The margin covers the model's error in a manoeuvre; settled in a straight lane, the model and the car keep
            // to it within centimetres, so there the body counts as it is (an outer lane 1.36 m from a kerb wall is a
            // normal lane, T3 session 20261013-191420: 'Curb1').
            var turningHalf = new Vec3(car.BodyHalf.X + Margin, car.BodyHalf.Y, car.BodyHalf.Z + Margin);
            var settledHalf = car.BodyHalf;
            var length = Route.Fixed(graph, fixture.Route).LengthMetres;
            var limit = 30.0 + length / 4.0;
            var lastWorld = double.NegativeInfinity;
            var kerbSamples = 0;
            var travelled = 0.0;
            var time = 0.0;
            var timedOut = true;
            for (var step = 1; time < limit; step++)
            {
                var o = driver.Step(model.Input(StepSeconds));
                double x0 = model.X, z0 = model.Z, h0 = model.Heading;
                model.Step(o, StepSeconds);
                time += StepSeconds;
                travelled += CornerTravel(x0, z0, h0, model);
                var ground = line.Line.At(driver.Along).Y;
                var pose = new Pose((float)model.X, ground, (float)model.Z, (float)model.Heading);
                var off = Math.Abs(o.Lateral);
                if (fixture.Manoeuvre == CaseKind.UTurn && !judge.Failed)
                {
                    var exitHeading = Math.Atan2(-Math.Sin(startHeading), -Math.Cos(startHeading));
                    if (!exitPhase && Math.Abs(DriveLine.Wrap(model.Heading - exitHeading)) < 20 * Math.PI / 180) exitPhase = true;
                    if (exitPhase)
                    {
                        // A left U-turn's outside is the line's right (positive).
                        if (o.Lateral > verdict.ExitOvershoot) { verdict.ExitOvershoot = o.Lateral; verdict.ExitSwingBack = 0f; }
                        else if (verdict.ExitOvershoot > 0f && -o.Lateral > verdict.ExitSwingBack) verdict.ExitSwingBack = -o.Lateral;
                    }
                }
                if (driver.Along > entryAlong - WorldFrom && off > verdict.WorstOff && !judge.Failed) { verdict.WorstOff = off; verdict.Worst = pose; }
                var settled = off < SettledOff && Math.Abs(model.Steer) < SettledWheel;
                var half = settled ? settledHalf : turningHalf;
                var worldHalf = new Vec3(half.X + NearMiss, half.Y, half.Z + NearMiss);
                var footprint = Footprint(pose, half);
                judge.Add(new Sample
                {
                    Step = step, Time = time, Centre = Local(pose, car.BodyCentre), FrontAxle = Local(pose, new Vec3(0f, 0f, car.Wheelbase)),
                    HeadingDegrees = (float)(model.Heading * 180 / Math.PI), Footprint = footprint, SpeedMs = (float)Math.Abs(model.Speed),
                });
                var looked = false;
                if (driver.Along > entryAlong - WorldFrom && travelled - lastWorld >= WorldEvery)
                {
                    lastWorld = travelled;
                    looked = true;
                    var hit = world?.Invoke(new BodyBox(Local(pose, car.BodyCentre), worldHalf, pose.Heading));
                    if (hit != null && !judge.Failed)
                    {
                        judge.Contact(step, hit);
                        verdict.Hit = hit;
                    }
                    if (GroundAt != null && !judge.Failed && WheelOnKerb(pose, ref kerbSamples, settled) is string wheel)
                    {
                        judge.Reject(step, "kerb", wheel);
                        verdict.Hit = wheel;
                    }
                }
                if (!judge.Failed && driver.Along >= exitAlong && off > OffLaneLimit)
                {
                    var what = FormattableString.Invariant($"rear axle {off:0.00} m off the line past the exit");
                    judge.Reject(step, "off-lane", what);
                    verdict.Hit = what;
                }
                if (judge.Failed && verdict.Steps == 0) { verdict.Steps = step; verdict.Worst = pose; verdict.WorstOff = off; }
                if (judge.Failed || judge.Finished) { timedOut = false; break; }
                if (o.Along + car.FrontAhead >= line.Line.Length - 1.5f && Math.Abs(model.Speed) < 0.3) { timedOut = false; break; }
                if (looked || step % YieldSteps == 0) yield return null;
            }
            var result = judge.Result(timedOut);
            verdict.Judge = result;
            verdict.Feasible = result.Verdict == "pass";
            if (!verdict.Feasible) verdict.Reason = (result.Reasons.Count > 0 ? string.Join(" ", result.Reasons) : result.Verdict) + (verdict.Hit != null ? " (" + verdict.Hit + ")" : "");
        }

        // The four wheels at a pose, the margin further out each side; one higher than the lowest by KerbRise, for
        // KerbSamples samples in a row, is on a kerb.
        // settled: the car keeps to a straight lane within centimetres, so its wheels count where they are (review
        // round 1: the margin put a settled car's wheels on the kerb beside an outer lane).
        private float TrackHalf => car.TrackWidth > 0f ? car.TrackWidth / 2f : Math.Max(0.5f, car.BodyHalf.X - 0.25f);

        // The ground under the four wheels where they are (rear left, rear right, front left, front right), or null.
        private Vec3[]? Track(Pose pose)
        {
            var half = TrackHalf;
            var at = new[]
            {
                Local(pose, new Vec3(-half, 0f, 0f)), Local(pose, new Vec3(half, 0f, 0f)),
                Local(pose, new Vec3(-half, 0f, car.Wheelbase)), Local(pose, new Vec3(half, 0f, car.Wheelbase)),
            };
            for (var i = 0; i < 4; i++)
            {
                if (!(GroundAt!(at[i]) is float h)) return null;
                at[i] = new Vec3(at[i].X, h, at[i].Z);
            }
            return at;
        }

        // Both wheels of one side up (a car run wide onto the pavement): the car at a pose (its rear axle) leans
        // beyond the road's cross-fall by more than KerbRise. Measured where the wheels really are, not the margin
        // wider (review round 3). What, or null; null too without GroundAt or a lane near.
        public string? Leaning(Pose pose)
        {
            if (GroundAt == null || !(WheelPlane.CrossFall(graph, Local(pose, car.BodyCentre), pose.Heading, GroundAt) is float fall) || !(Track(pose) is Vec3[] real)) return null;
            var roll = WheelPlane.Roll(real, fall * 2f * TrackHalf);
            return Math.Abs(roll) > KerbRise
                ? FormattableString.Invariant($"kerb: the {(roll > 0 ? "left" : "right")} wheels {Math.Abs(roll):0.00} m above the {(roll > 0 ? "right" : "left")} ones beyond the road's cross-fall at ({pose.X:0.0}, {pose.Z:0.0})")
                : null;
        }

        private string? WheelOnKerb(Pose pose, ref int samples, bool settled)
        {
            var half = TrackHalf + (settled ? 0f : Margin);
            var wheels = new[]
            {
                Local(pose, new Vec3(-half, 0f, 0f)), Local(pose, new Vec3(half, 0f, 0f)),
                Local(pose, new Vec3(-half, 0f, car.Wheelbase)), Local(pose, new Vec3(half, 0f, car.Wheelbase)),
            };
            // Off the plane through the other three wheels (WheelPlane, as the rig's kerb check): a slope tilts all four.
            var ground = new Vec3[4];
            for (var i = 0; i < 4; i++)
            {
                if (!(GroundAt!(wheels[i]) is float h)) { samples = 0; return null; }
                ground[i] = new Vec3(wheels[i].X, h, wheels[i].Z);
            }
            string? up = null;
            for (var i = 0; i < 4 && up == null; i++)
                if (WheelPlane.Rise(ground, i) is float rise && rise > KerbRise)
                    up = FormattableString.Invariant($"kerb: a {(i < 2 ? "rear" : "front")} wheel {rise:0.00} m above the plane of the others at ({wheels[i].X:0.0}, {wheels[i].Z:0.0})");
            up ??= Leaning(pose);
            samples = up != null ? samples + 1 : 0;
            return samples >= KerbSamples ? up : null;
        }

        // A stretch of a planned line driven by the model (review round 1: the rounded corners and the sideways moves
        // the plan puts on lane steps were never checked): the car starts on the line 25 m before `from` at the
        // plan's speed there and drives to `to`; from `from` on, its body every WorldEvery metres against the world,
        // its corners against the pavement and its wheels against kerbs, as a manoeuvre's check. What it meets and
        // where, or null.
        public string? Sweep(DrivePlan plan, DriveLine line, float from, float to)
        {
            var result = new SweepResult();
            Run(SweepWork(plan, line, from, to, result));
            return result.What;
        }

        public IEnumerable<object?> SweepWork(DrivePlan plan, DriveLine line, float from, float to, SweepResult result)
        {
            var driver = plan.Driver(line, car.Wheelbase, car.FrontAhead, car.TrackWidth);
            var start = Math.Max(0f, from - 25f);
            Vec3 p0 = line.Line.At(start), p1 = line.Line.At(start + 1f);
            var model = car.Model(p0.X, p0.Z, Math.Atan2(p1.X - p0.X, p1.Z - p0.Z));
            driver.StartAt(p0.X, p0.Z);
            model.Speed = driver.PlannedSpeed(start, car.TopKmh / 3.6f);
            return FollowWork(driver, model, line, from, to, result);
        }

        // What a sweep or a join met (null: nothing), and the rear axle's last distance from the line.
        public sealed class SweepResult
        {
            public string? What;
            public float Lateral;
        }

        // The drive from where the car stands onto a new line (review round 2: a new way on was taken up when the line
        // passed near the car, never driven from the car's pose): the model starts at the car's rear axle, heading,
        // speed and wheel angle and follows the line for JoinMetres, its body, corners and wheels checked as a
        // sweep's, and must have settled within JoinSettled of the line by then. What it meets, or null.
        public string? Join(DrivePlan plan, DriveLine line, CarPose pose)
        {
            var result = new SweepResult();
            Run(JoinWork(plan, line, pose, result));
            return result.What;
        }

        public IEnumerable<object?> JoinWork(DrivePlan plan, DriveLine line, CarPose pose, SweepResult result)
        {
            Simulations++;
            var driver = plan.Driver(line, car.Wheelbase, car.FrontAhead, car.TrackWidth);
            driver.StartAt(pose.X, pose.Z);
            var model = car.Model(pose.X, pose.Z, pose.Heading, pose.Speed);
            model.SetWheel(pose.Wheel);
            var from = driver.Along;
            var to = Math.Min(from + JoinMetres, line.Line.Length - car.FrontAhead - 2f);
            foreach (var y in FollowWork(driver, model, line, from, to, result)) yield return y;
            if (result.What != null) result.What = "joining the new line: " + result.What;
            else if (Math.Abs(result.Lateral) > JoinSettled)
                result.What = FormattableString.Invariant($"joining the new line: still {result.Lateral:+0.00;-0.00} m off it after {to - from:0} m");
        }

        // Where the car will be in `seconds`, driving the plan it drives now at no more than its present speed (it
        // may slow for the line's bends): the pose a way on's join is checked from while the car drives on (review
        // round 4: checked from the pose when the check began, the join was taken up from wherever the car was by
        // the time it ended).
        public CarPose Predict(DrivePlan plan, DriveLine line, CarPose now, float seconds)
        {
            var driver = plan.Driver(line, car.Wheelbase, car.FrontAhead, car.TrackWidth);
            driver.StartAt(now.X, now.Z);
            var model = car.Model(now.X, now.Z, now.Heading, now.Speed);
            model.SetWheel(now.Wheel);
            var cap = Math.Max(1f, now.Speed);
            for (var t = 0f; t < seconds; t += StepSeconds)
            {
                var input = model.Input(StepSeconds);
                input.MaxSpeed = cap;
                model.Step(driver.Step(input), StepSeconds);
            }
            return new CarPose((float)model.X, (float)model.Z, (float)model.Heading, (float)model.Speed, (float)model.Steer);
        }

        public const float JoinMetres = 30f;
        public const float JoinSettled = 0.5f;

        // The model driven along a line from `from` to `to`, checked from the start: its body against the world, its
        // corners against the pavement, its wheels against kerbs, each time a corner of the body has moved WorldEvery
        // metres. What it meets, or null; the rear axle's last distance from the line.
        private IEnumerable<object?> FollowWork(PursuitDriver driver, CarModel model, DriveLine line, float from, float to, SweepResult result)
        {
            var turningHalf = new Vec3(car.BodyHalf.X + Margin, car.BodyHalf.Y, car.BodyHalf.Z + Margin);
            var lastCheck = double.NegativeInfinity;
            var travelled = 0.0;
            var kerbSamples = 0;
            var step = 0;
            for (var time = 0f; time < 60f && driver.Along < Math.Min(to, line.Line.Length - car.FrontAhead - 2f); time += StepSeconds)
            {
                var o = driver.Step(model.Input(StepSeconds));
                double x0 = model.X, z0 = model.Z, h0 = model.Heading;
                model.Step(o, StepSeconds);
                travelled += CornerTravel(x0, z0, h0, model);
                result.Lateral = o.Lateral;
                if (driver.Along < from || travelled - lastCheck < WorldEvery)
                {
                    if (++step % YieldSteps == 0) yield return null;
                    continue;
                }
                lastCheck = travelled;
                var pose = new Pose((float)model.X, line.Line.At(driver.Along).Y, (float)model.Z, (float)model.Heading);
                var settled = Math.Abs(o.Lateral) < SettledOff && Math.Abs(model.Steer) < SettledWheel;
                var half = settled ? car.BodyHalf : turningHalf;
                var footprint = Footprint(pose, half);
                if (world?.Invoke(new BodyBox(Local(pose, car.BodyCentre), new Vec3(half.X + NearMiss, half.Y, half.Z + NearMiss), pose.Heading)) is string hit)
                {
                    result.What = FormattableString.Invariant($"{hit} at {driver.Along:0} m");
                    yield break;
                }
                if (PavementAt != null && Pavement.CornerOnPavement(graph, footprint, Local(pose, car.BodyCentre), PavementAt) is string pavement)
                {
                    result.What = pavement;
                    yield break;
                }
                if (GroundAt != null && WheelOnKerb(pose, ref kerbSamples, settled) is string kerb)
                {
                    result.What = kerb;
                    yield break;
                }
                yield return null;
            }
        }

        // How far the body's farthest corner moved in a step, at most: the rear axle's move and the turn times the
        // corner's reach from it (review round 2: sampled by the rear axle's travel, a corner swinging round a slow
        // turn moved several times as far between samples).
        private double CornerTravel(double x0, double z0, double h0, CarModel model)
        {
            var reach = Math.Sqrt(Math.Pow(Math.Abs(car.BodyCentre.X) + car.BodyHalf.X, 2) + Math.Pow(Math.Abs(car.BodyCentre.Z) + car.BodyHalf.Z, 2));
            return Math.Sqrt((model.X - x0) * (model.X - x0) + (model.Z - z0) * (model.Z - z0)) + Math.Abs(DriveLine.Wrap(model.Heading - h0)) * reach;
        }

        // The line a plan gives, checked as it will be driven (review rounds 1 and 2): each rounded corner and each
        // sideways move driven by the model over its stretch (Sweep). A rounded corner that fails is left sharp, and
        // the sharp corner is then swept on the final line too; a move that fails is dropped, and as the lane without
        // it is the one found blocked, its step fails. Each failure goes in `failed` (the step to block, and why),
        // and a plan with any failure must not be driven. Yields after each sweep, so a caller can spread the work.
        public IEnumerable<object?> Validate(DrivePlan plan, List<(int From, int To, string What)> failed, List<string>? notes = null)
        {
            var sharp = new HashSet<int>();
            var settled = false;
            for (var round = 0; round < ValidateRounds && !settled; round++)
            {
                var line = plan.Line(graph);
                var changed = false;
                foreach (var k in new List<int>(plan.Fillets))
                {
                    if (k <= 0 || k >= line.AlongOf.Length) continue;
                    var swept = new SweepResult();
                    foreach (var y in SweepWork(plan, line, line.AlongOf[k] - 10f, line.AlongOf[k] + 10f, swept)) yield return y;
                    var what = swept.What;
                    if (what == null) continue;
                    plan.Fillets.Remove(k);
                    sharp.Add(k);
                    notes?.Add($"corner at {plan.Path[k]} left sharp ({what})");
                    changed = true;
                }
                foreach (var shift in new List<(int Position, float From, float To, float Offset)>(plan.Shifts))
                {
                    var start = line.AlongOf[shift.Position];
                    var swept = new SweepResult();
                    foreach (var y in SweepWork(plan, line, start + shift.From - 10f, start + shift.To + 10f, swept)) yield return y;
                    var what = swept.What;
                    if (what == null) continue;
                    plan.Shifts.Remove(shift);
                    failed.Add((plan.Path[shift.Position], plan.Path[shift.Position + 1], "no way past within the lane: " + what));
                    changed = true;
                }
                settled = !changed;
            }
            var final = plan.Line(graph);
            foreach (var k in sharp)
            {
                var swept = new SweepResult();
                foreach (var y in SweepWork(plan, final, final.AlongOf[k] - 10f, final.AlongOf[k] + 10f, swept)) yield return y;
                var what = swept.What;
                if (what != null && k + 1 < plan.Path.Count) failed.Add((plan.Path[k], plan.Path[k + 1], $"corner at {plan.Path[k]}, neither rounded nor sharp: {what}"));
            }
            if (!settled)
                foreach (var shift in plan.Shifts)
                    failed.Add((plan.Path[shift.Position], plan.Path[shift.Position + 1], "the line didn't settle in " + ValidateRounds + " rounds"));
        }

        public const int ValidateRounds = 6;

        // Validate run to its end: the steps that failed (none: the plan may be driven).
        public List<(int From, int To, string What)> ValidateNow(DrivePlan plan, List<string>? notes = null)
        {
            var failed = new List<(int From, int To, string What)>();
            foreach (var _ in Validate(plan, failed, notes)) { }
            return failed;
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

        private static bool Has(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }
    }
}
