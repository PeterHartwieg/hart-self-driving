using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;

namespace DrivenRides.Core.Drive
{
    public readonly struct BodyBox
    {
        public readonly Vec3 Centre, Half;
        public readonly float Heading;

        public BodyBox(Vec3 centre, Vec3 half, float heading) { Centre = centre; Half = half; Heading = heading; }
    }

    public sealed class Verdict
    {
        public bool Feasible;
        public string Form = "";
        public string Reason = "";
        public string? Hit;
        public Pose Worst;
        public float WorstOff;
        public float ExitOvershoot, ExitSwingBack;
        public Polyline? Arc;
        public JudgeResult? Judge;
        public int Steps;

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

    public sealed class Feasibility
    {
        public const float DefaultMargin = 0.5f;
        public const float NearMiss = 0.1f;
        // A step with no case to drive it on (Catalogue.ForStep: a U-turn whose far side doesn't leave the junction)
        // can't be checked, so it isn't driven (review round 1: it was taken as feasible unchecked).
        public const string NoCase = "no case to check it on";
        public const string Movable = "movable: ";
        public const float LaneMargin = 0.2f;
        public const float SettledOff = 0.2f;
        public const float SettledWheel = 5f;
        private const float StepSeconds = 0.02f;
        private const float WorldEvery = 0.5f;
        private const float WorldFrom = 10f;
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
        public Func<Vec3, float?>? GroundAt { get; set; }
        // Whether a point is pedestrian ground (the judge's PavementAt); null offline, where the judge's geometric
        // kerb estimate stands in.
        public Func<Vec3, bool>? PavementAt { get; set; }
        // U-turns through Gley's vias instead of a driver's curve (TurnArc.FitUTurn), for T1's comparison only.
        public bool UTurnVias { get; set; }
        public const float KerbRise = 0.1f;
        // A kerb is at most this high: a wheel higher above the plane of the others is the ground probe meeting
        // something else (a bridge deck, a hider collider; 0.1.90 read 0.96 to 0.99 m by a bridge with the car
        // settled on its line). A wall that high is the body's to meet.
        public const float KerbMax = 0.35f;
        private const int KerbSamples = 2;
        public CarSpec Car => car;
        public int Simulations { get; private set; }

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

        public const int YieldSteps = 50;

        private static void Run(IEnumerable<object?> work)
        {
            foreach (var _ in work) { }
        }

        public Polyline? Arc(int entry, int exit)
        {
            Run(ArcWork(entry, exit));
            return KnownArc(entry, exit, out var arc) ? arc : null;
        }

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

        public bool Clear(int entry, int exit, int aim)
        {
            Run(ViaWork(entry, exit, aim));
            return vias[(entry, exit, aim)].Feasible;
        }

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
                    verdict = through.Feasible || verdict.Reason.Length == 0 ? through : through.With("; arc: " + verdict.Reason);
                }
            }
            steps[(entry, exit)] = verdict;
        }

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

        public bool Knows(int entry, int exit) => steps.ContainsKey((entry, exit));

        public void Forget(int entry, int exit)
        {
            steps.Remove((entry, exit));
            arcs.Remove((entry, exit));
            var stale = new List<(int, int, int)>();
            foreach (var key in vias.Keys) if (key.Item1 == entry && key.Item2 == exit) stale.Add(key);
            foreach (var key in stale) vias.Remove(key);
        }

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
            Verdict? verdict = null;
            var fixture = Catalogue.ForStep(graph, entry, exit, options);
            var radii = TurnArc.Radii(graph, entry, exit, arcMinRadius, car.BodyHalf.X);
            var least = Math.Max(arcMinRadius * TurnArc.RadiusSlack, TurnArc.MinDriveRadius);
            if (radii.Count == 0) verdict = new Verdict { Form = "arc", Reason = "no arc fits the junction and passes its middle on the near side" };
            else if (fixture == null) verdict = new Verdict { Form = "arc", Reason = NoCase };
            else
            {
                var failed = new List<string>();
                foreach (var r in radii)
                {
                    if (!(TurnArc.Fit(graph, entry, exit, arcMinRadius, r) is Polyline arc)) continue;
                    var tried = new Verdict { Form = r <= least + 0.01f ? "tight arc" : "arc" };
                    foreach (var y in DriveArcWork(fixture, entry, exit, arc, tried)) yield return y;
                    if (tried.Feasible) { verdict = tried; break; }
                    failed.Add(FormattableString.Invariant($"{r:0.0} m: {tried.Reason}"));
                    verdict = tried;
                }
                if (verdict == null) verdict = new Verdict { Form = "arc", Reason = "no arc fits the junction" };
                else if (!verdict.Feasible) verdict.Reason = string.Join("; ", failed);
            }
            arcs[(entry, exit)] = verdict;
        }

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
                var plan = DrivePlan.Build(graph, fixture.Route, car, (e, x) => null, (e, x, a) => e != entry || x != exit || a == aim, limitKmh);
                foreach (var y in DriveWork(fixture, plan, verdict)) yield return y;
            }
            vias[(entry, exit, aim)] = verdict;
        }

        public Verdict Drive(Fixture fixture, DrivePlan plan, string form)
        {
            var verdict = new Verdict { Form = form };
            Run(DriveWork(fixture, plan, verdict));
            return verdict;
        }

        private IEnumerable<object?> DriveWork(Fixture fixture, DrivePlan plan, Verdict verdict)
        {
            Simulations++;
            var line = plan.Line(graph);
            var driver = plan.Driver(line, car.Wheelbase, car.FrontAhead, car.TrackWidth);
            var geometry = CaseGeometry.Build(graph, fixture);
            var judge = new Judge(graph, geometry, fixture) { PavementAt = PavementAt, OnPavement = (_, what) => verdict.Hit ??= what };
            var path = plan.Path;
            var start = graph.Position(path[0]);
            var next = line.Line.At(1f);
            var startHeading = Math.Atan2(next.X - start.X, next.Z - start.Z);
            var model = car.Model(start.X, start.Z, startHeading);
            var exitPhase = false;
            float entryAlong = 0f, exitAlong = float.MaxValue;
            for (var k = 0; k < path.Count; k++) if (path[k] == fixture.Route[fixture.EntryIndex]) { entryAlong = line.AlongOf[k]; break; }
            for (var k = path.Count - 1; k >= 0; k--) if (path[k] == fixture.Route[fixture.ExitIndex]) { exitAlong = line.AlongOf[k]; break; }
            var turningHalf = new Vec3(car.BodyHalf.X + Margin, car.BodyHalf.Y, car.BodyHalf.Z + Margin);
            var settledHalf = car.BodyHalf;
            var length = Route.Fixed(graph, fixture.Route).LengthMetres;
            var limit = 30.0 + length / 4.0;
            var lastWorld = double.NegativeInfinity;
            var kerbSamples = 0;
            var travelled = 0.0;
            var time = 0.0;
            var timedOut = true;
            var ways = new List<(float X, float Z)>();
            if (fixture.Manoeuvre == CaseKind.Turn && verdict.Form.Contains("arc"))
            {
                if (graph.PlainHeading(fixture.Route[fixture.EntryIndex], out var hx, out var hz)) ways.Add((hx, hz));
                if (fixture.EntryIndex + 1 < fixture.Route.Count && graph.PlainHeading(fixture.Route[fixture.EntryIndex + 1], out var gx, out var gz)) ways.Add((gx, gz));
            }
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
                        verdict.Hit = hit + Measured(settled, o.Lateral, model.Steer, Margin);
                    }
                    if (ways.Count == 2 && !judge.Failed && driver.Along > entryAlong - WorldFrom && driver.Along < exitAlong)
                    {
                        var centre = Local(pose, car.BodyCentre);
                        for (var c = 0; c < 4; c++)
                        {
                            var corner = footprint[c];
                            float dx = centre.X - corner.X, dz = centre.Z - corner.Z, l = (float)Math.Sqrt(dx * dx + dz * dz);
                            if (l > Judge.PavementInset) corner = new Vec3(corner.X + dx / l * Judge.PavementInset, corner.Y, corner.Z + dz / l * Judge.PavementInset);
                            if (!Pavement.OnAnotherWaysLane(graph, corner, ways)) continue;
                            var what = FormattableString.Invariant($"a corner over another way's lane outside the junction at ({corner.X:0.0}, {corner.Z:0.0})");
                            judge.Reject(step, "other-way", what);
                            verdict.Hit = what;
                            break;
                        }
                    }
                    if (GroundAt != null && !judge.Failed && WheelOnKerb(pose, ref kerbSamples, settled, Margin) is string wheel)
                    {
                        judge.Reject(step, "kerb", wheel);
                        verdict.Hit = wheel + Measured(settled, o.Lateral, model.Steer, Margin);
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

        private float TrackHalf => car.TrackWidth > 0f ? car.TrackWidth / 2f : Math.Max(0.5f, car.BodyHalf.X - 0.25f);

        private string? WheelOnKerb(Pose pose, ref int samples, bool settled, float margin)
        {
            var half = TrackHalf + (settled ? 0f : margin);
            var wheels = new[]
            {
                Local(pose, new Vec3(-half, 0f, 0f)), Local(pose, new Vec3(half, 0f, 0f)),
                Local(pose, new Vec3(-half, 0f, car.Wheelbase)), Local(pose, new Vec3(half, 0f, car.Wheelbase)),
            };
            var ground = new Vec3[4];
            for (var i = 0; i < 4; i++)
            {
                if (!(GroundAt!(wheels[i]) is float h)) { samples = 0; return null; }
                ground[i] = new Vec3(wheels[i].X, h, wheels[i].Z);
            }
            string? up = null;
            for (var i = 0; i < 4 && up == null; i++)
                if (WheelPlane.Rise(ground, i) is float rise && rise > KerbRise && rise <= KerbMax)
                    up = FormattableString.Invariant($"kerb: a {(i < 2 ? "rear" : "front")} wheel {rise:0.00} m above the plane of the others at ({wheels[i].X:0.0}, {wheels[i].Z:0.0})");
            samples = up != null ? samples + 1 : 0;
            return samples >= KerbSamples ? up : null;
        }

        public string? Sweep(DrivePlan plan, DriveLine line, float from, float to)
        {
            var result = new SweepResult();
            Run(SweepWork(plan, line, from, to, result));
            return result.What;
        }

        public IEnumerable<object?> SweepWork(DrivePlan plan, DriveLine line, float from, float to, SweepResult result, float margin = LaneMargin)
        {
            var driver = plan.Driver(line, car.Wheelbase, car.FrontAhead, car.TrackWidth);
            var start = Math.Max(0f, from - 25f);
            Vec3 p0 = line.Line.At(start), p1 = line.Line.At(start + 1f);
            var model = car.Model(p0.X, p0.Z, Math.Atan2(p1.X - p0.X, p1.Z - p0.Z));
            driver.StartAt(p0.X, p0.Z);
            model.Speed = driver.PlannedSpeed(start, car.TopKmh / 3.6f);
            // Where the plan starts the car stands already: what its body touches there is no reason to refuse the way
            // on (0.1.100: a taxi standing by a kerb corner piece, 'Corner2' "at 0 m", got its first step blocked, and
            // with it every route). Checked once the car has moved a body length on.
            from = Math.Max(from, car.Length);
            if (from >= to) return Array.Empty<object?>();
            return FollowWork(driver, model, line, from, to, result, margin);
        }

        public sealed class SweepResult
        {
            public string? What;
            public float Lateral;
        }

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
            var to = JoinEnd(line, from);
            foreach (var y in FollowWork(driver, model, line, Math.Min(from + car.Length, to), to, result, Margin)) yield return y;
            if (result.What != null) result.What = "joining the new line: " + result.What;
            else if (Math.Abs(result.Lateral) > JoinSettled)
                result.What = FormattableString.Invariant($"joining the new line: still {result.Lateral:+0.00;-0.00} m off it after {to - from:0} m");
        }

        private float JoinEnd(DriveLine line, float from)
        {
            var last = line.Line.Length - car.FrontAhead - 2f;
            var to = from + JoinMetres;
            while (to < from + JoinMaxMetres && to < last && !Straight(line, to)) to += 1f;
            return Math.Min(to, last);
        }

        private static bool Straight(DriveLine line, float at)
        {
            var first = HeadingAt(line, at - JoinStraight);
            for (var a = at - JoinStraight + 1f; a <= at; a += 1f)
                if (Math.Abs(DriveLine.Wrap(HeadingAt(line, a) - first)) > JoinStraightDegrees * Math.PI / 180) return false;
            return true;
        }

        private static double HeadingAt(DriveLine line, float at)
        {
            Vec3 a = line.Line.At(Math.Max(0f, at)), b = line.Line.At(Math.Max(0f, at) + 0.5f);
            return Math.Atan2(b.X - a.X, b.Z - a.Z);
        }

        public const float JoinStraight = 8f, JoinStraightDegrees = 2f, JoinMaxMetres = 100f;

        public CarPose Predict(DrivePlan plan, DriveLine line, CarPose now, float seconds)
        {
            var driver = plan.Driver(line, car.Wheelbase, car.FrontAhead, car.TrackWidth);
            driver.StartAt(now.X, now.Z);
            var model = car.Model(now.X, now.Z, now.Heading, now.Speed);
            model.SetWheel(now.Wheel);
            float? obstacle = now.GapAhead is float gap ? driver.Along + car.FrontAhead + gap : (float?)null;
            for (var t = 0f; t < seconds; t += StepSeconds)
            {
                var input = model.Input(StepSeconds);
                input.StopAlong = now.StopAlong;
                input.YieldAlong = now.YieldAlong;
                input.MaxSpeed = now.MaxSpeed;
                if (obstacle is float o)
                {
                    input.GapAhead = Math.Max(0f, o + now.GapSpeed * t - (driver.Along + car.FrontAhead));
                    input.GapSpeed = now.GapSpeed;
                }
                model.Step(driver.Step(input), StepSeconds);
            }
            return new CarPose((float)model.X, (float)model.Z, (float)model.Heading, (float)model.Speed, (float)model.Steer);
        }

        public const float JoinMetres = 30f;
        public const float JoinSettled = 0.5f;

        private IEnumerable<object?> FollowWork(PursuitDriver driver, CarModel model, DriveLine line, float from, float to, SweepResult result, float margin)
        {
            var turningHalf = new Vec3(car.BodyHalf.X + margin, car.BodyHalf.Y, car.BodyHalf.Z + margin);
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
                    result.What = FormattableString.Invariant($"{hit} at {driver.Along:0} m") + Measured(settled, o.Lateral, model.Steer, margin);
                    yield break;
                }
                if (PavementAt != null && Pavement.CornerOnPavement(graph, footprint, Local(pose, car.BodyCentre), PavementAt) is string pavement)
                {
                    result.What = pavement + Measured(settled, o.Lateral, model.Steer, margin);
                    yield break;
                }
                if (GroundAt != null && WheelOnKerb(pose, ref kerbSamples, settled, margin) is string kerb)
                {
                    result.What = kerb + Measured(settled, o.Lateral, model.Steer, margin);
                    yield break;
                }
                yield return null;
            }
        }

        private static string Measured(bool settled, float lateral, double wheel, float margin) => FormattableString.Invariant(
            $" [rear axle {lateral:+0.00;-0.00} m off the line, wheel {wheel:0} deg, {(settled ? "settled: no margin" : $"margin {margin:0.00} m")}]");

        private double CornerTravel(double x0, double z0, double h0, CarModel model)
        {
            var reach = Math.Sqrt(Math.Pow(Math.Abs(car.BodyCentre.X) + car.BodyHalf.X, 2) + Math.Pow(Math.Abs(car.BodyCentre.Z) + car.BodyHalf.Z, 2));
            return Math.Sqrt((model.X - x0) * (model.X - x0) + (model.Z - z0) * (model.Z - z0)) + Math.Abs(DriveLine.Wrap(model.Heading - h0)) * reach;
        }

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
                if (swept.What == null || k + 1 >= plan.Path.Count) continue;
                var slow = new List<(int, float)>();
                for (var q = Math.Max(0, k - 1); q <= k + 1 && q < plan.Path.Count; q++) slow.Add((q, SqueezeKmh));
                plan.Slow.AddRange(slow);
                var squeezed = new SweepResult();
                foreach (var y in SweepWork(plan, final, final.AlongOf[k] - 10f, final.AlongOf[k] + 10f, squeezed, 0f)) yield return y;
                if (squeezed.What == null)
                {
                    notes?.Add($"corner at {plan.Path[k]} driven at {SqueezeKmh:0} km/h on the lane's line ({swept.What})");
                    continue;
                }
                foreach (var e in slow) plan.Slow.Remove(e);
                failed.Add((plan.Path[k], plan.Path[k + 1], $"corner at {plan.Path[k]}, rounded, sharp or at {SqueezeKmh:0} km/h: {squeezed.What}"));
            }
            if (!settled)
                foreach (var shift in plan.Shifts)
                    failed.Add((plan.Path[shift.Position], plan.Path[shift.Position + 1], "the line didn't settle in " + ValidateRounds + " rounds"));
        }

        public const int ValidateRounds = 6;
        public const float SqueezeKmh = 10f;

        public List<(int From, int To, string What)> ValidateNow(DrivePlan plan, List<string>? notes = null)
        {
            var failed = new List<(int From, int To, string What)>();
            foreach (var _ in Validate(plan, failed, notes)) { }
            return failed;
        }

        private static Vec3 Local(Pose pose, Vec3 p)
        {
            float s = (float)Math.Sin(pose.Heading), c = (float)Math.Cos(pose.Heading);
            return new Vec3(pose.X + p.X * c + p.Z * s, pose.Y + p.Y, pose.Z - p.X * s + p.Z * c);
        }

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
