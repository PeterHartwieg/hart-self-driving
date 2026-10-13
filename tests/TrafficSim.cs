using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;

namespace DrivenRides.Core.Tests;

internal sealed class SimResult
{
    public string? Failure;
    public float Time;
    public bool Arrived;
    // A dump's run that left the recorded area (TrafficSim) rather than reaching the end of the line.
    public bool LeftArea;
    public float LongestStall;
    public int BackOffs;
    // The driver found the way blocked by a car that won't move (TrafficDriver.Stuck): the planner's to go round.
    public string? Blocked;
    public readonly HashSet<int> GaveWayTo = new();
    public readonly StringBuilder Trace = new();
    public override string ToString() => Failure ?? (Arrived ? (LeftArea ? $"left the recorded area at {Time:0.0} s" : $"arrived at {Time:0.0} s") : Blocked != null ? $"blocked at {Time:0.0} s: {Blocked}" : "not arrived");
}

// T0's simulation (docs/implementation-plan.md, "Tests"): the ride car is Core's CarModel driven by PursuitDriver
// and TrafficDriver, exactly as NwhDrive drives the NWH car; the traffic cars behave as Gley's do, as far as the ride
// car is concerned: they follow their lanes at the road's limit, stop at `stop` waypoints they can stop for, wait
// at a priority junction's give-way line until it is their road's turn, and see the ride car only in a short front
// trigger (VehicleComponent.OnTriggerEnter: the player's layer means StopInDistance), so one cut in on at speed hits
// it. A car the ride car is ahead of in the same direction follows it (as Gley's do each other).
// Invariants: no overlap of the ride car's body with a traffic car's; no standstill over 30 s except at a red;
// never past a `stop` waypoint while it is set, unless it was set when the car could no longer stop at HardDecel.
internal sealed class TrafficSim : ILiveFlags
{
    public const float Dt = 0.02f;
    public const float StallLimit = 30f;
    public const int DecideEvery = 2;
    public static float TraceEvery = float.TryParse(Environment.GetEnvironmentVariable("FT_TRAFFIC_TRACE_EVERY"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : 0.5f;
    private readonly TrafficScenario sc;
    private readonly LaneGraph graph;
    private readonly bool[] stop;
    private readonly Dictionary<int, Junction> junctions = new();
    private readonly List<Car> cars = new();
    private readonly DriveLine line;
    private readonly PursuitDriver driver;
    private readonly CarModel ride;
    private readonly TrafficDriver traffic;
    private readonly RideBody body;
    private readonly int[] path;
    private readonly List<PathMark> marks = new();
    private readonly List<OtherCar> near = new();
    private readonly TrafficDecision decision = new();
    private readonly bool trace;
    private float time;

    public TrafficSim(TrafficScenario scenario, bool trace = false)
    {
        sc = scenario;
        this.trace = trace;
        graph = sc.BuildGraph();
        stop = sc.Waypoints.Select(w => w.Stop).ToArray();
        replayEnd = sc.History.Count > 0 ? sc.History[^1].T : 0f;
        foreach (var t in sc.Timelines) if (t.At.Count > 0) replayEnd = Math.Max(replayEnd, t.At[^1]);
        foreach (var j in sc.Junctions) junctions[j.Id] = new Junction(j, stop, sc, graph, replayEnd);
        foreach (var c in sc.Cars) cars.Add(new Car(c));
        var r = sc.Ride;
        path = r.Path;
        var points = path.Select(i => graph.Position(i)).ToList();
        var arcs = r.Arcs.Select(a => (a.Entry, new Polyline(a.Points) { Radius = a.Radius })).ToList();
        line = DriveLine.Build(points, arcs);
        body = new RideBody { Wheelbase = r.Wheelbase, FrontAhead = r.FrontAhead, RearBehind = r.RearBehind, HalfWidth = r.HalfWidth };
        driver = new PursuitDriver(line, r.Wheelbase, r.FrontAhead, r.TopMs);
        foreach (var (from, to, ms) in r.Slow) driver.Slow(from, to, ms);
        ride = new CarModel(r.Wheelbase, r.Lock, r.X, r.Z, r.Heading, r.Speed);
        driver.StartAt(r.X, r.Z);
        traffic = new TrafficDriver(graph, line, driver, body);
        // Time enough for the whole line at 3 m/s on average, lights included (a dump runs 400 m on from its window).
        seconds = Math.Max(sc.Seconds, 30f + line.Line.Length / 3f);
    }

    private readonly float seconds;
    // A dump's lights are replayed as the game showed them up to here, then cycled (Junction).
    private readonly float replayEnd;

    public bool Stop(int waypoint) => waypoint >= 0 && waypoint < stop.Length && stop[waypoint];

    public bool GiveWayEntry(int waypoint, out int junction, out int road)
    {
        junction = road = -1;
        if (waypoint < 0 || waypoint >= sc.Waypoints.Count) return false;
        var w = sc.Waypoints[waypoint];
        if (!w.GiveWay || !w.Enter || w.Junction < 0) return false;
        junction = w.Junction;
        road = w.Road;
        return true;
    }

    public SimResult Run()
    {
        var result = new SimResult();
        var stoppable = new Dictionary<int, bool>();
        float stall = 0f, nextTrace = 0f;
        var backing = false;
        var prevFront = driver.Along + body.FrontAhead;
        for (var step = 0; time < seconds; step++)
        {
            time = step * Dt;
            foreach (var j in junctions.Values) j.Update(time);
            if (time <= replayEnd) foreach (var t in sc.Timelines)
            {
                var value = stop[t.Waypoint];
                for (var k = 0; k < t.At.Count && t.At[k] <= time; k++) value = t.Stop[k];
                stop[t.Waypoint] = value;
            }
            var rideBox = traffic.BodyAt((float)ride.X, (float)ride.Z, (float)ride.Heading);
            foreach (var c in cars) c.Step(this, rideBox, (float)ride.Heading, time);

            // The ride car, as NwhDrive drives it.
            var front = driver.Along + body.FrontAhead;
            marks.Clear();
            for (var k = 0; k < path.Length; k++)
            {
                var along = line.AlongOf[k];
                if (along < driver.Along - body.RearBehind - 10f || along > front + 80f) continue;
                var w = sc.Waypoints[path[k]];
                var m = new PathMark
                {
                    Position = k, Waypoint = path[k], Along = along, Stop = stop[path[k]], GiveWay = w.GiveWay, Enter = w.Enter, Exit = w.Exit,
                    Junction = w.Junction, Road = w.Road,
                };
                if (traffic.Rules.ShouldAsk(m, front) && junctions.TryGetValue(w.Junction, out var jn) && jn.IsPriority) m.Free = jn.Free(path[k], -1);
                marks.Add(m);
            }
            near.Clear();
            foreach (var c in cars)
            {
                if (!c.Active) continue;
                if ((c.X - ride.X) * (c.X - ride.X) + (c.Z - ride.Z) * (c.Z - ride.Z) > TrafficDriver.SeeMetres * TrafficDriver.SeeMetres) continue;
                near.Add(c.AsOther());
            }
            var snapshot = new TrafficSnapshot
            {
                Time = time, RearX = (float)ride.X, RearZ = (float)ride.Z, Heading = (float)ride.Heading, Speed = (float)ride.Speed,
                Marks = marks, Cars = near, Flags = this,
            };
            // NwhDrive decides every physics step; the simulation every other one (25 a second), to keep the soak
            // short. Junction events are never lost: a step without a decision keeps the last one's answers only.
            if (step % DecideEvery == 0) traffic.Decide(snapshot, decision);
            else decision.Events.Clear();
            foreach (var e in decision.Events)
                if (junctions.TryGetValue(e.Junction, out var jn))
                {
                    if (e.Kind == JunctionEventKind.Enter) jn.Inside.Add(-1);
                    else jn.Inside.Remove(-1);
                }
            if (decision.BlockedBy >= 0)
            {
                result.Blocked = decision.BlockedWhy;
                return Finish(result);
            }
            DriveOutput o;
            if (decision.BackOff)
            {
                if (!backing) result.BackOffs++;
                o = new DriveOutput { SteerDegrees = 0f, Desired = -TrafficDriver.BackOffMs };
                ride.Step(o, Dt);
                driver.BackTo((float)ride.X, (float)ride.Z);
            }
            else
            {
                var input = ride.Input(Dt);
                input.StopAlong = decision.StopAlong;
                input.YieldAlong = decision.YieldAlong;
                input.GapAhead = decision.GapAhead;
                input.GapSpeed = decision.GapSpeed;
                input.MaxSpeed = decision.MaxSpeed;
                o = driver.Step(input);
                ride.Step(o, Dt);
            }
            backing = decision.BackOff;
            if (decision.YieldFor >= 0) result.GaveWayTo.Add(decision.YieldFor);

            // Invariants.
            rideBox = traffic.BodyAt((float)ride.X, (float)ride.Z, (float)ride.Heading);
            foreach (var c in cars)
            {
                if (!c.Active || !c.Body.Overlaps(rideBox)) continue;
                result.Failure = $"overlap with car {c.Spec.Id} at {time:0.00} s (ride {ride.Speed:0.0} m/s, car {c.V:0.0} m/s, {decision.Why})";
                return Finish(result);
            }
            front = driver.Along + body.FrontAhead;
            for (var k = 0; k < path.Length; k++)
            {
                var along = line.AlongOf[k];
                if (!stop[path[k]]) { stoppable.Remove(k); continue; }
                if (!stoppable.ContainsKey(k) && along > prevFront)
                    // With a tenth of a second to see it (the game's lights change between physics steps too).
                    stoppable[k] = along - prevFront - ride.Speed * 0.1 > ride.Speed * ride.Speed / (2 * PursuitDriver.HardDecel);
                if (prevFront < along && front >= along && stoppable.TryGetValue(k, out var could) && could)
                {
                    result.Failure = $"past the stop at path position {k} while set, at {time:0.00} s";
                    return Finish(result);
                }
            }
            prevFront = front;
            if (driver.Along + body.FrontAhead >= line.Line.Length - 1.5f && Math.Abs(ride.Speed) < 0.3)
            {
                result.Arrived = true;
                return Finish(result);
            }
            // A dump covers 170 m round where the ride car was; beyond that its graph is cut and its traffic
            // jams where the game's wouldn't. Leaving the area ends the run as a pass.
            if (sc.History.Count > 0 && (ride.X - sc.Ride.X) * (ride.X - sc.Ride.X) + (ride.Z - sc.Ride.Z) * (ride.Z - sc.Ride.Z) > 150 * 150)
            {
                result.Arrived = true;
                result.LeftArea = true;
                return Finish(result);
            }
            var atRed = decision.StopIsRed && decision.StopAlong - front < 8f;
            stall = Math.Abs(ride.Speed) < 0.3 && !decision.BackOff && !atRed ? stall + Dt : 0f;
            result.LongestStall = Math.Max(result.LongestStall, stall);
            if (stall > StallLimit)
            {
                result.Failure = $"standing {StallLimit:0} s at {driver.Along:0.0} m along ({decision.Why}) at {time:0.0} s";
                return Finish(result);
            }
            if (trace && time >= nextTrace)
            {
                nextTrace = time + TraceEvery;
                result.Trace.Append($"{time,6:0.0} ({ride.X:0.0},{ride.Z:0.0}) along {driver.Along,6:0.0} v {ride.Speed,5:0.0} stop {decision.StopAlong,6:0.0} give {decision.YieldAlong,6:0.0} gap {decision.GapAhead,5:0.0} {decision.Why} {decision.YieldNote} inside {traffic.Rules.Inside.Count}");
                if (decision.GapFor >= 0)
                {
                    var g = cars.First(c => c.Spec.Id == decision.GapFor);
                    result.Trace.Append($" [ride h {ride.Heading * 57.3:0} car {g.Spec.Id} at ({g.X:0.00},{g.Z:0.00}) h {g.Heading * 57.3:0} L {g.Spec.Length:0.0}]");
                }
                foreach (var c in cars.Where(c => c.Active).OrderBy(c => (c.X - ride.X) * (c.X - ride.X) + (c.Z - ride.Z) * (c.Z - ride.Z))) result.Trace.Append($" | {c.Spec.Id} ({c.X:0},{c.Z:0}) {c.V:0.0}{(c.ForUs ? " us" : "")}{(c.V < 0.5f && c.Why != "" ? " " + c.Why : "")}");
                result.Trace.Append('\n');
            }
        }
        result.Failure = $"not there in {seconds:0} s ({driver.Along:0} of {line.Line.Length:0} m)";
        return Finish(result);
    }

    private SimResult Finish(SimResult r)
    {
        r.Time = time;
        return r;
    }

    private sealed class Junction
    {
        private readonly ScenarioJunction spec;
        private readonly bool[] stop;
        private readonly Dictionary<int, int> roadOf = new();
        public readonly HashSet<int> Inside = new();
        private int current, pending = -1;
        private bool amber;
        private float since;

        private readonly List<int[]> roads;
        private readonly float handOver;
        private bool cycling;

        public Junction(ScenarioJunction spec, bool[] stop, TrafficScenario sc, LaneGraph graph, float replayEnd)
        {
            this.spec = spec;
            this.stop = stop;
            roads = spec.Roads;
            // A replayed light (a dump) without its roads: from the road each stop waypoint enters by (Gley's
            // stopWaypoints group, in the dump as "r"; both ways of a street share one), else by the way they face.
            if (spec.Kind == "replay" && roads.Count == 0)
            {
                roads = new List<int[]>();
                var byRoad = new SortedDictionary<int, List<int>>();
                for (var w = 0; w < sc.Waypoints.Count; w++)
                {
                    var p = sc.Waypoints[w];
                    if (p.Junction != spec.Id || !p.Enter || p.Road < 0) continue;
                    if (!byRoad.TryGetValue(p.Road, out var list)) byRoad[p.Road] = list = new List<int>();
                    list.Add(w);
                }
                foreach (var list in byRoad.Values) roads.Add(list.ToArray());
            }
            if (spec.Kind == "replay" && roads.Count == 0)
            {
                var groups = new List<(double Heading, List<int> Ways)>();
                for (var w = 0; w < sc.Waypoints.Count; w++)
                {
                    var p = sc.Waypoints[w];
                    if (p.Junction != spec.Id || !p.Enter || graph.Successors(w).Count == 0) continue;
                    var n = graph.Position(graph.Successors(w)[0]);
                    var h = Math.Atan2(n.X - p.X, n.Z - p.Z);
                    var group = groups.FindIndex(g => Math.Abs(DriveLine.Wrap(g.Heading - h)) < 0.5);
                    if (group < 0) groups.Add((h, new List<int> { w }));
                    else groups[group].Ways.Add(w);
                }
                foreach (var g in groups) roads.Add(g.Ways.ToArray());
            }
            for (var r = 0; r < roads.Count; r++) foreach (var w in roads[r]) roadOf[w] = r;
            current = spec.FirstRoad;
            since = -spec.Offset;
            handOver = replayEnd;
            if (IsLights) Apply();
        }

        public bool IsLights => spec.Kind == "lights" || (cycling && roads.Count > 0);
        public bool IsPriority => spec.Kind == "priority";

        // Gley's light cycle: green, then amber until it has lasted Yellow and nobody is inside, then the next road.
        public void Update(float t)
        {
            if (spec.Kind == "replay" && !cycling && t > handOver && roads.Count > 0)
            {
                // From where the game left it: the road with green goes on, as if its green had just begun.
                cycling = true;
                current = Math.Max(0, roads.FindIndex(r => r.All(w => !stop[w])));
                amber = false;
                since = t;
                Apply();
            }
            if (!IsLights) return;
            if (!amber && t - since > Math.Max(1f, spec.Green)) { amber = true; since = t; Apply(); }
            // Amber is held while anyone is inside, as in Gley, but not past 10 s here: the simulation's traffic cars can
            // jam in a box where the game's don't, and a light held for good would stand everyone still.
            else if (amber && t - since > spec.Yellow && (Inside.Count == 0 || t - since > spec.Yellow + 10f))
            {
                current = (current + 1) % roads.Count;
                amber = false;
                since = t;
                Apply();
            }
        }

        private void Apply()
        {
            for (var r = 0; r < roads.Count; r++) foreach (var w in roads[r]) stop[w] = r != current || amber;
        }

        // Round robin by road with at most a few cars inside (Gley's PriorityIntersection, in outline): the current
        // road goes; another road asking queues a change, made once the junction is empty.
        public bool Free(int waypoint, int who)
        {
            if (!roadOf.TryGetValue(waypoint, out var road)) return true;
            if (pending >= 0 && Inside.Count == 0) { current = pending; pending = -1; }
            if (road == current && pending < 0) return Inside.Count <= 3;
            if (road != current && pending < 0) pending = road;
            return false;
        }
    }

    private sealed class Car
    {
        public readonly ScenarioCar Spec;
        public bool Active, Gone;
        private readonly List<int> route = new();
        private readonly List<Vec3> pts = new();
        private readonly List<float> at = new();
        private readonly List<int> routeOf = new();
        private readonly List<float> corner = new();
        public float U, V;
        public float X, Z, Heading;
        public bool ForUs;
        public string Why = "";
        private int target = -1;
        private readonly HashSet<int> ranThrough = new(), granted = new(), passed = new();
        private float blockedByCar;
        private float ignoreCarsUntil;
        private readonly Box[] ahead = new Box[48];
        public Box Body;

        public Car(ScenarioCar spec) => Spec = spec;

        public OtherCar AsOther() => new()
        {
            Id = Spec.Id, X = X, Z = Z, Heading = Heading, Vx = V * (float)Math.Sin(Heading), Vz = V * (float)Math.Cos(Heading),
            Length = Spec.Length, Width = Spec.Width, TriggerLength = Spec.Trigger, Target = target, StoppedForUs = ForUs,
        };

        private float L => Spec.Length;

        private void Spawn(TrafficSim sim)
        {
            var g = sim.graph;
            route.AddRange(Spec.Route);
            if (Spec.HasPose)
            {
                float hx = (float)Math.Sin(Spec.Heading), hz = (float)Math.Cos(Spec.Heading);
                AddPoint(new Vec3(Spec.X - hx * L, 0, Spec.Z - hz * L), -1);
                AddPoint(new Vec3(Spec.X, 0, Spec.Z), -1);
                for (var k = 0; k < route.Count; k++)
                {
                    var p = g.Position(route[k]);
                    if ((p.X - Spec.X) * hx + (p.Z - Spec.Z) * hz > L / 2f + 0.5f || routeOf.Count > 2) AddPoint(p, k);
                }
                U = L;
                V = Spec.Speed;
            }
            else
            {
                var a = g.Position(route[0]);
                var b = g.Position(route[1]);
                var d = Vec3.HorizontalDistance(a, b);
                AddPoint(new Vec3(a.X - (b.X - a.X) / d * (L + 2f), 0, a.Z - (b.Z - a.Z) / d * (L + 2f)), -1);
                for (var k = 0; k < route.Count; k++) AddPoint(g.Position(route[k]), k);
                U = L + 2f - L / 2f;
                V = g.SpeedMs(route[0]) * Spec.Pace;
            }
            Active = true;
            Place();
        }

        private void AddPoint(Vec3 p, int routeIndex)
        {
            if (pts.Count > 0)
            {
                var d = Vec3.HorizontalDistance(pts[^1], p);
                if (d < 0.05f) return;
                at.Add(at[^1] + d);
            }
            else at.Add(0f);
            pts.Add(p);
            routeOf.Add(routeIndex);
            corner.Add(float.MaxValue);
            var n = pts.Count;
            if (n >= 3)
            {
                Vec3 a = pts[n - 3], b = pts[n - 2], c = pts[n - 1];
                var turn = Math.Abs(DriveLine.Wrap(Math.Atan2(c.X - b.X, c.Z - b.Z) - Math.Atan2(b.X - a.X, b.Z - a.Z)));
                var span = (Vec3.HorizontalDistance(a, b) + Vec3.HorizontalDistance(b, c)) / 2f;
                corner[n - 2] = turn > 0.02 ? (float)Math.Sqrt(3.0 * span / turn) : float.MaxValue;
            }
        }

        private Vec3 PointAt(float u)
        {
            if (u <= 0f) return pts[0];
            var from = 1;
            while (from + 8 < pts.Count && at[from + 8] < u) from += 8;
            for (var i = from; i < pts.Count; i++)
            {
                if (at[i] < u) continue;
                var f = (u - at[i - 1]) / Math.Max(1e-4f, at[i] - at[i - 1]);
                return pts[i - 1] + (pts[i] - pts[i - 1]) * f;
            }
            return pts[^1];
        }

        private void Place()
        {
            var f = PointAt(U + L / 2f);
            var r = PointAt(U - L / 2f);
            X = (f.X + r.X) / 2f;
            Z = (f.Z + r.Z) / 2f;
            if (Vec3.HorizontalDistance(f, r) > 0.1f) Heading = (float)Math.Atan2(f.X - r.X, f.Z - r.Z);
            Body = new Box(X, Z, Heading, Spec.Width / 2f, L / 2f);
        }

        private static float Approach(float speed, float metres, float decel) => metres <= 0f ? speed : (float)Math.Sqrt(speed * speed + 2 * decel * metres);

        public void Step(TrafficSim sim, in Box rideBox, float rideHeading, float t)
        {
            if (Gone) return;
            if (!Active)
            {
                if (t < Spec.Spawn) return;
                Spawn(sim);
                // Not into anything: wait for room, as Gley's spawner does. A car with a pose (a dump) is where it was.
                var clear = Spec.HasPose || !Body.Overlaps(rideBox.Grown(1f, 2f)) && sim.cars.All(o => o == this || !o.Active || !o.Body.Overlaps(Body.Grown(1f, 2f)));
                if (!clear) { Active = false; route.Clear(); pts.Clear(); at.Clear(); routeOf.Clear(); corner.Clear(); return; }
            }
            var g = sim.graph;
            var front = U + L / 2f;
            // Far behind the ride car and going away: it can't matter any more.
            float rx = (float)sim.ride.X - X, rz = (float)sim.ride.Z - Z;
            if (rx * rx + rz * rz > 220f * 220f && rx * (float)Math.Sin(Heading) + rz * (float)Math.Cos(Heading) < 0f)
            {
                Gone = true;
                Active = false;
                foreach (var j in sim.junctions.Values) j.Inside.Remove(Spec.Id);
                return;
            }
            // Long enough ahead: its lane's first successor.
            while (at[^1] - front < 60f)
            {
                var next = g.Successors(route[^1]);
                if (next.Count == 0) break;
                route.Add(next[0]);
                AddPoint(g.Position(next[0]), route.Count - 1);
            }
            target = -1;
            for (var i = 0; i < pts.Count; i++) if (routeOf[i] >= 0 && at[i] > front + 0.3f) { target = route[routeOf[i]]; break; }
            // A lane that ends (Gley gives such a waypoint giveWay): the car stands at its end for good, as Gley's
            // did in T3 (20261013-191420, ride 1: 300 s at a lane end in the ride car's way).
            var last = sim.sc.Waypoints[route[^1]];
            var laneEnd = g.Successors(route[^1]).Count == 0 && last.GiveWay && !last.InJunction;
            if (laneEnd && target < 0) target = route[^1];
            if (!laneEnd && target < 0 && front >= at[^1] - 0.5f)
            {
                Gone = true;
                Active = false;
                foreach (var j in sim.junctions.Values) j.Inside.Remove(Spec.Id);
                return;
            }

            var limit = (target >= 0 ? g.SpeedMs(target) : LaneGraph.DefaultSpeedMs) * Spec.Pace;
            Why = "";
            var brake = 4f;
            if (laneEnd)
            {
                var room = at[^1] - front;
                limit = Math.Min(limit, room < 0.5f ? 0f : Approach(0f, room - 0.5f, 4f));
                if (limit < 0.5f) Why = "lane end";
            }
            for (var i = 0; i < pts.Count; i++)
            {
                var room = at[i] - front;
                if (room < -0.5f) continue;
                if (room > 50f) break;
                if (corner[i] < float.MaxValue) limit = Math.Min(limit, Approach(corner[i], room, 3f));
                if (routeOf[i] < 0) continue;
                var ri = routeOf[i];
                var w = route[ri];
                var spec = sim.sc.Waypoints[w];
                // Membership, at the front.
                if (room <= 0f && passed.Add(ri) && spec.Junction >= 0 && sim.junctions.TryGetValue(spec.Junction, out var jn))
                {
                    if (spec.Enter) jn.Inside.Add(Spec.Id);
                    if (spec.Exit) jn.Inside.Remove(Spec.Id);
                }
                if (room <= 0f) continue;
                if (sim.stop[w] && !ranThrough.Contains(ri))
                {
                    if (V * V / (2 * room) > 6f) ranThrough.Add(ri);
                    else if (Approach(0f, room - 0.5f, 4f) < limit) { limit = Approach(0f, room - 0.5f, 4f); Why = "red"; }
                }
                if (spec.GiveWay && spec.Enter && !granted.Contains(ri) && sim.junctions.TryGetValue(spec.Junction, out var pj) && pj.IsPriority)
                {
                    if (room < 2.5f && pj.Free(w, Spec.Id)) granted.Add(ri);
                    else limit = Math.Min(limit, Approach(0f, room - 0.3f, 3f));
                }
            }

            // What is ahead of its front: the trigger stops it for anything, the follow distance only for cars it
            // follows (traffic cars, and the ride car once it is ahead in the same direction). Gley's cars react to
            // the player's car through the trigger alone; following it is the simulation's own, so it doesn't
            // rear-end a ride car it has been behind for a while (the game's cars may: T3 logs who moved).
            ForUs = false;
            // Its way ahead, in boxes a metre long along its path (Gley turns its trigger with the wheels).
            var reach = Spec.Trigger + V * 1.5f + 4f;
            var n = 0;
            var seg = 1;
            while (seg < pts.Count - 1 && at[seg] < front) seg++;
            for (var d = 0.5f; d <= reach && n < ahead.Length; d += 1f)
            {
                var u = front + d;
                while (seg < pts.Count - 1 && at[seg] < u) seg++;
                Vec3 a = pts[seg - 1], b = pts[seg];
                var f = Math.Max(0f, Math.Min(1f, (u - at[seg - 1]) / Math.Max(1e-4f, at[seg] - at[seg - 1])));
                ahead[n++] = new Box(a.X + (b.X - a.X) * f, a.Z + (b.Z - a.Z) * f, (float)Math.Atan2(b.X - a.X, b.Z - a.Z), Spec.Width / 2f, 0.5f);
            }
            var stopNow = false;
            var stoppedByCar = -1;
            var near2 = (reach + L + 6f) * (reach + L + 6f);
            void Look(in Box other, bool isRide, float otherHeading, int otherId)
            {
                if ((other.X - X) * (other.X - X) + (other.Z - Z) * (other.Z - Z) > near2) return;
                var gap = -1f;
                if (other.Overlaps(Body.Grown(0f, 0.2f)) && (other.X - X) * (float)Math.Sin(Heading) + (other.Z - Z) * (float)Math.Cos(Heading) > 0f) gap = 0f;
                else for (var k = 0; k < n; k++) if (ahead[k].Overlaps(other)) { gap = k; break; }
                if (gap < 0f) return;
                if (gap < Spec.Trigger)
                {
                    Why = isRide ? "trigger ride" : $"trigger {otherId}";
                    stopNow = true;
                    if (isRide) ForUs = true;
                    else stoppedByCar = otherId;
                    return;
                }
                // Following stops it 1.5 m short, inside its trigger (2 m or more): in Gley whatever holds a car
                // back from the ride car is in its obstacleList.
                var same = Math.Cos(otherHeading - Heading) > 0.7;
                if ((!isRide || same) && Approach(0f, gap - 1.5f, 4f) < limit) { limit = Approach(0f, gap - 1.5f, 4f); Why = isRide ? "follow ride" : $"follow {otherId}"; }
            }
            Look(rideBox, true, rideHeading, -1);
            if (t >= ignoreCarsUntil)
                foreach (var o in sim.cars) if (o != this && o.Active) Look(o.Body, false, o.Heading, o.Spec.Id);
            // Two traffic cars nose to nose: in Gley one of them backs or swings out after a while; here the lower id
            // stops looking at cars for a moment (the ride car is never ignored).
            blockedByCar = stoppedByCar >= 0 && V < 0.3f ? blockedByCar + Dt : 0f;
            // A jam among traffic cars (a dump's graph is cut at its edges, so its cars can meet where the game's
            // wouldn't): the one waiting longest on a standing car moves on through it after a while.
            var blocker = stoppedByCar >= 0 ? sim.cars.FirstOrDefault(o => o.Spec.Id == stoppedByCar) : null;
            if (blockedByCar > 3f && blocker != null && blocker.V < 0.3f && (blocker.blockedByCar > 0f || blocker.Why != "red") && (blocker.blockedByCar < blockedByCar || Spec.Id < stoppedByCar)) ignoreCarsUntil = t + 2f;
            if (stopNow) { limit = 0f; brake = 8f; }
            V += Math.Max(-brake * Dt, Math.Min(2f * Dt, limit - V));
            V = Math.Max(0f, V);
            U += V * Dt;
            Place();
        }
    }
}
