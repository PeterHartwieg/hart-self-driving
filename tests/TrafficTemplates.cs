using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;

namespace DrivenRides.Core.Tests;

internal sealed class ScenarioBuilder
{
    public readonly TrafficScenario S = new();

    public int Add(float x, float z, bool junction = false, float kmh = 50f)
    {
        S.Waypoints.Add(new ScenarioWaypoint { X = x, Z = z, InJunction = junction, Kmh = kmh });
        return S.Waypoints.Count - 1;
    }

    public void Link(int a, int b)
    {
        var w = S.Waypoints[a];
        if (!w.Next.Contains(b)) w.Next = w.Next.Append(b).ToArray();
    }

    public int[] Lane(float x0, float z0, float x1, float z1, float every = 5f, float kmh = 50f)
    {
        var length = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        var n = Math.Max(1, (int)Math.Round(length / every));
        var lane = new int[n + 1];
        for (var k = 0; k <= n; k++) lane[k] = Add(x0 + (x1 - x0) * k / n, z0 + (z1 - z0) * k / n, false, kmh);
        for (var k = 0; k < n; k++) Link(lane[k], lane[k + 1]);
        return lane;
    }

    private (float X, float Z) HeadingInto(int w)
    {
        var p = S.Waypoints[w];
        var before = S.Waypoints.FindIndex(o => o.Next.Contains(w));
        if (before < 0) { var n = S.Waypoints[p.Next[0]]; return Unit(n.X - p.X, n.Z - p.Z); }
        var b = S.Waypoints[before];
        return Unit(p.X - b.X, p.Z - b.Z);
    }

    private (float X, float Z) HeadingOut(int w)
    {
        var p = S.Waypoints[w];
        if (p.Next.Length == 0) return HeadingInto(w);
        var n = S.Waypoints[p.Next[0]];
        return Unit(n.X - p.X, n.Z - p.Z);
    }

    private static (float X, float Z) Unit(float x, float z)
    {
        var l = (float)Math.Sqrt(x * x + z * z);
        return (x / l, z / l);
    }

    public int[] Curve(int from, int to, float every = 2.5f, float kmh = 30f)
    {
        var a = S.Waypoints[from];
        var b = S.Waypoints[to];
        var (ax, az) = HeadingInto(from);
        var (bx, bz) = HeadingOut(to);
        var chord = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
        var turn = Math.Acos(Math.Max(-1, Math.Min(1, ax * bx + az * bz)));
        var k = chord * (float)(0.36 + 0.31 * turn / Math.PI);
        float p1x = a.X + ax * k, p1z = a.Z + az * k, p2x = b.X - bx * k, p2z = b.Z - bz * k;
        (float, float) At(float t)
        {
            var u = 1 - t;
            return (u * u * u * a.X + 3 * u * u * t * p1x + 3 * u * t * t * p2x + t * t * t * b.X,
                    u * u * u * a.Z + 3 * u * u * t * p1z + 3 * u * t * t * p2z + t * t * t * b.Z);
        }
        var length = 0f;
        var prev = (a.X, a.Z);
        for (var i = 1; i <= 50; i++) { var p = At(i / 50f); length += (float)Math.Sqrt((p.Item1 - prev.Item1) * (p.Item1 - prev.Item1) + (p.Item2 - prev.Item2) * (p.Item2 - prev.Item2)); prev = p; }
        var n = Math.Max(2, (int)Math.Round(length / every));
        var chain = new List<int>();
        var last = from;
        for (var i = 1; i < n; i++)
        {
            var (x, z) = At((float)i / n);
            var w = Add(x, z, true, kmh);
            Link(last, w);
            chain.Add(w);
            last = w;
        }
        Link(last, to);
        return chain.ToArray();
    }

    public void Flags(int w, bool stop = false, bool giveWay = false, bool enter = false, bool exit = false, int junction = -1, int road = -1)
    {
        var p = S.Waypoints[w];
        p.Stop |= stop;
        p.GiveWay |= giveWay;
        p.Enter |= enter;
        p.Exit |= exit;
        if (junction >= 0) p.Junction = junction;
        if (road >= 0) p.Road = road;
    }

    public void Ride(IEnumerable<int> path, RideCar car)
    {
        var p = path.ToArray();
        var a = S.Waypoints[p[0]];
        var b = S.Waypoints[p[1]];
        S.Ride = new ScenarioRide
        {
            Path = p, Wheelbase = car.Wheelbase, Lock = car.Lock, FrontAhead = car.FrontAhead, RearBehind = car.RearBehind, HalfWidth = car.HalfWidth,
            TopMs = car.TopMs, X = a.X, Z = a.Z, Heading = (float)Math.Atan2(b.X - a.X, b.Z - a.Z), Speed = car.StartMs,
        };
    }

    public const float GleyTrigger = 3.6f;

    public ScenarioCar Car(float spawn, IEnumerable<int> route, float pace = 1f, float length = 4.5f, float trigger = GleyTrigger)
    {
        var c = new ScenarioCar { Id = S.Cars.Count + 1, Spawn = spawn, Route = route.ToArray(), Pace = pace, Length = length, Trigger = trigger };
        S.Cars.Add(c);
        return c;
    }

    public ScenarioCar CarAt(int[] route, float metres, float pace = 1f, float speed = -1f)
    {
        var c = Car(0f, route, pace);
        var u = metres;
        for (var k = 1; k < route.Length; k++)
        {
            var a = S.Waypoints[route[k - 1]];
            var b = S.Waypoints[route[k]];
            var d = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            if (u > d) { u -= d; continue; }
            c.HasPose = true;
            c.X = a.X + (b.X - a.X) * u / d;
            c.Z = a.Z + (b.Z - a.Z) * u / d;
            c.Heading = (float)Math.Atan2(b.X - a.X, b.Z - a.Z);
            c.Speed = speed >= 0f ? speed : 13.9f * pace;
            c.Route = route.Skip(k).ToArray();
            return c;
        }
        throw new ArgumentException("route too short");
    }
}

internal sealed record RideCar(string Name, float Wheelbase, float Lock, float FrontAhead, float RearBehind, float HalfWidth, float TopMs = 13.9f, float StartMs = 8f)
{
    public static readonly RideCar Limo = new("limo", 3.75f, 37f, 4.85f, 1.25f, 1.0f);
    public static readonly RideCar Dash = new("mersaididash", 2.8f, 35f, 3.7f, 0.9f, 0.92f);
}

internal static class TrafficTemplates
{
    public static readonly string[] Names = { "lights", "priority", "merge", "uturn", "headon", "pullaway" };

    public static TrafficScenario Build(string name, int seed) => name switch
    {
        "lights" => FourWayLights(seed),
        "priority" => PriorityT(seed),
        "merge" => Merge(seed),
        "crawl" => Crawl(seed),
        "uturn" => UTurn(seed),
        "headon" => HeadOn(seed),
        "pullaway" => PullAway(seed),
        _ => throw new ArgumentException(name),
    };

    private static float Range(Random r, float a, float b) => a + (float)r.NextDouble() * (b - a);

    private static void Stream(ScenarioBuilder b, Random r, Func<int[]> route, float from, float until, float gapLo, float gapHi, float paceLo = 0.7f, float paceHi = 1f)
    {
        for (var t = from + Range(r, 0f, gapHi); t < until; t += Range(r, gapLo, gapHi))
            b.Car(t, route(), Range(r, paceLo, paceHi), Range(r, 3.8f, 5.5f), Range(r, 2f, 4f));
    }

    private sealed class Cross
    {
        public readonly int[][] In = new int[4][], Out = new int[4][];
        public readonly Dictionary<(int, int), int[]> Move = new();
    }

    private static Cross Crossroads(ScenarioBuilder b, float box, float reach, float half = 1.75f, bool uTurns = false)
    {
        var c = new Cross();
        (float X, float Z)[] dir = { (0, 1), (1, 0), (0, -1), (-1, 0) };
        for (var d = 0; d < 4; d++)
        {
            var (dx, dz) = dir[d];
            float hx = -dx, hz = -dz, rx = hz, rz = -hx;
            c.In[d] = b.Lane(dx * reach + rx * half, dz * reach + rz * half, dx * box + rx * half, dz * box + rz * half);
            float ox = dz, oz = -dx;
            c.Out[d] = b.Lane(dx * box + ox * half, dz * box + oz * half, dx * reach + ox * half, dz * reach + oz * half);
        }
        for (var from = 0; from < 4; from++)
            for (var to = 0; to < 4; to++)
            {
                if (to == from && !uTurns) continue;
                c.Move[(from, to)] = b.Curve(c.In[from][^1], c.Out[to][0]);
            }
        return c;
    }

    private static int[] Path(params IEnumerable<int>[] parts) => parts.SelectMany(p => p).ToArray();
    private static int[] Tail(int[] lane, float metres, float every = 5f) => lane.Skip(Math.Max(0, lane.Length - 1 - (int)Math.Round(metres / every))).ToArray();
    private static int[] Head(int[] lane, float metres, float every = 5f) => lane.Take(Math.Min(lane.Length, (int)Math.Round(metres / every) + 1)).ToArray();

    public static TrafficScenario FourWayLights(int seed)
    {
        var r = new Random(seed);
        var b = new ScenarioBuilder();
        var c = Crossroads(b, Range(r, 9f, 13f), 130f);
        var roads = new List<int[]> { new[] { c.In[0][^1], c.In[2][^1] }, new[] { c.In[1][^1], c.In[3][^1] } };
        b.S.Junctions.Add(new ScenarioJunction { Id = 0, Kind = "lights", Roads = roads, Green = Range(r, 8f, 14f), Yellow = Range(r, 1f, 3f), FirstRoad = r.Next(2), Offset = Range(r, 0f, 8f) });
        for (var d = 0; d < 4; d++)
        {
            b.Flags(c.In[d][^1], stop: true, enter: true, junction: 0, road: d % 2);
            b.Flags(c.Out[d][0], exit: true, junction: 0);
        }
        var car = r.Next(2) == 0 ? RideCar.Limo : RideCar.Dash;
        var to = r.NextDouble() < 0.6 ? 3 : r.Next(2) == 0 ? 0 : 1;
        b.Ride(Path(Tail(c.In[2], Range(r, 40f, 80f)), c.Move[(2, to)], Head(c.Out[to], 50f)), car with { StartMs = Range(r, 0f, 10f) });
        for (var d = 0; d < 4; d++)
        {
            var from = d;
            Stream(b, r, () => { var x = (from + 1 + r.Next(3)) % 4; return Path(c.In[from], c.Move[(from, x)], c.Out[x]); }, d == 2 ? 2f : -8f, 70f, 2.5f, 9f);
        }
        b.S.Name = $"lights-{seed}";
        b.S.Seconds = 90f;
        return b.S;
    }

    public static TrafficScenario PriorityT(int seed)
    {
        var r = new Random(seed);
        var b = new ScenarioBuilder();
        var c = Crossroads(b, Range(r, 9f, 13f), 120f);
        var roads = new List<int[]> { new[] { c.In[3][^1] }, new[] { c.In[1][^1] }, new[] { c.In[2][^1] } };
        b.S.Junctions.Add(new ScenarioJunction { Id = 0, Kind = "priority", Roads = roads });
        int[] ins = { 3, 1, 2 };
        for (var k = 0; k < 3; k++) b.Flags(c.In[ins[k]][^1], giveWay: true, enter: true, junction: 0, road: k);
        foreach (var d in new[] { 1, 2, 3 }) b.Flags(c.Out[d][0], exit: true, junction: 0);
        var car = r.Next(2) == 0 ? RideCar.Limo : RideCar.Dash;
        var choice = r.Next(4);
        var (rf, rt) = choice switch { 0 => (2, 3), 1 => (2, 1), 2 => (1, 2), _ => (3, 2) };
        b.Ride(Path(Tail(c.In[rf], Range(r, 40f, 80f)), c.Move[(rf, rt)], Head(c.Out[rt], 50f)), car with { StartMs = Range(r, 0f, 10f) });
        var exits = new Dictionary<int, int[]> { [3] = new[] { 1, 2 }, [1] = new[] { 3, 2 }, [2] = new[] { 1, 3 } };
        foreach (var from in ins)
        {
            var f = from;
            Stream(b, r, () => { var x = exits[f][r.Next(2)]; return Path(c.In[f], c.Move[(f, x)], c.Out[x]); }, f == rf ? 2f : -8f, 70f, 2.5f, 10f);
        }
        b.S.Name = $"priority-{seed}";
        b.S.Seconds = 90f;
        return b.S;
    }

    public static TrafficScenario Merge(int seed)
    {
        var r = new Random(seed);
        var b = new ScenarioBuilder();
        var main = b.Lane(-160f, 0f, 160f, 0f);
        var join = Array.FindIndex(main, w => Math.Abs(b.S.Waypoints[w].X) < 0.1f);
        var angle = Range(r, 12f, 30f) * (float)Math.PI / 180f;
        var slip = b.Lane(-90f * (float)Math.Cos(angle), -90f * (float)Math.Sin(angle), -12f * (float)Math.Cos(angle), -12f * (float)Math.Sin(angle));
        var merge = b.Curve(slip[^1], main[join], 2.5f, 50f);
        foreach (var w in merge) b.S.Waypoints[w].InJunction = false;
        var car = r.Next(2) == 0 ? RideCar.Limo : RideCar.Dash;
        b.Ride(Path(Tail(slip, Range(r, 40f, 75f)), merge, main.Skip(join).Take(15)), car with { StartMs = Range(r, 3f, 12f) });
        Stream(b, r, () => main, -10f, 70f, 1.5f, 7f, 0.6f, 1f);
        b.S.Name = $"merge-{seed}";
        b.S.Seconds = 90f;
        return b.S;
    }

    public static TrafficScenario Crawl(int seed)
    {
        var r = new Random(seed);
        var b = new ScenarioBuilder();
        var east = b.Lane(-160f, -1.75f, 160f, -1.75f, 4f);
        var join = Array.FindIndex(east, w => b.S.Waypoints[w].X >= 10f);
        var side = b.Lane(2.5f, -90f, 2.5f, -9f);
        var turn = b.Curve(side[^1], east[join], 2.5f, 25f);
        var car = r.Next(2) == 0 ? RideCar.Limo : RideCar.Dash;
        b.Ride(Path(Tail(side, Range(r, 30f, 60f)), turn, east.Skip(join).Take(15)), car with { StartMs = Range(r, 3f, 8f) });
        var pace = Range(r, 0.11f, 0.18f);
        var ms = 13.9f * pace;
        for (var x = 150f + 40f; x > 5f; x -= Range(r, 13.5f, 16.5f)) b.CarAt(east, x, pace, ms);
        for (var t = Range(r, 13.5f, 16.5f) / ms; t < 60f; t += Range(r, 13.5f, 16.5f) / ms) b.Car(t, east, pace, 4.5f, Range(r, 2.5f, 4f));
        b.S.Name = $"crawl-{seed}";
        b.S.Seconds = 90f;
        return b.S;
    }

    public static TrafficScenario UTurn(int seed)
    {
        var r = new Random(seed);
        var b = new ScenarioBuilder();
        var half = Range(r, 5.5f, 7f);
        var east = b.Lane(-160f, -half, 160f, -half);
        var west = b.Lane(160f, half, -160f, half);
        var at = Array.FindIndex(east, w => Math.Abs(b.S.Waypoints[w].X) < 0.1f);
        var back = Array.FindIndex(west, w => Math.Abs(b.S.Waypoints[w].X) < 0.1f);
        var turn = b.Curve(east[at], west[back], 2f, 20f);
        var car = r.Next(3) == 0 ? RideCar.Limo : RideCar.Dash;
        b.Ride(Path(east.Skip(at - (int)Range(r, 8f, 16f)).Take(at + 1 - (at - 16)).TakeWhile(w => b.S.Waypoints[w].X <= 0.1f), turn, west.Skip(back).Take(14)), car with { StartMs = Range(r, 0f, 10f) });
        for (var from = -12f; from < 70f; from += 20f + Range(r, 8f, 12f)) Stream(b, r, () => west, from, from + 20f, 2f, 12f, 0.6f, 1f);
        Stream(b, r, () => east, -12f, 70f, 3f, 10f, 0.6f, 1f);
        b.S.Name = $"uturn-{seed}";
        b.S.Seconds = 90f;
        return b.S;
    }

    public static TrafficScenario HeadOn(int seed)
    {
        var r = new Random(seed);
        var b = new ScenarioBuilder();
        var shared = Range(r, 15f, 35f);
        var eastIn = b.Lane(-140f, -1.75f, -shared / 2f - 6f, -1.75f);
        var eastShared = b.Lane(-shared / 2f, 0f, shared / 2f, 0f, 4f);
        var eastOut = b.Lane(shared / 2f + 6f, -1.75f, 140f, -1.75f);
        b.Link(eastIn[^1], eastShared[0]);
        b.Link(eastShared[^1], eastOut[0]);
        var westIn = b.Lane(140f, 1.75f, shared / 2f + 6f, 1.75f);
        var westShared = b.Lane(shared / 2f, 0f, -shared / 2f, 0f, 4f);
        var westOut = b.Lane(-shared / 2f - 6f, 1.75f, -140f, 1.75f);
        b.Link(westIn[^1], westShared[0]);
        b.Link(westShared[^1], westOut[0]);
        var car = r.Next(2) == 0 ? RideCar.Limo : RideCar.Dash;
        var start = Range(r, 30f, 100f);
        b.Ride(Path(Tail(eastIn, start), eastShared, Head(eastOut, 40f)), car with { StartMs = Range(r, 0f, 10f) });
        var westRoute = Path(westIn, westShared, westOut);
        var meet = (start + shared / 2f) / 9f;
        var n = 1 + r.Next(2);
        for (var k = 0; k < n; k++)
        {
            var pace = Range(r, 0.15f, 0.4f);
            var travel = (140f - shared / 2f + Range(r, -15f, 15f)) / (13.9f * pace);
            b.Car(Math.Max(0f, meet - travel) + k * Range(r, 2f, 5f), westRoute, pace, Range(r, 3.8f, 5.5f), Range(r, 2f, 4f));
        }
        b.S.Name = $"headon-{seed}";
        b.S.Seconds = 90f;
        return b.S;
    }

    public static TrafficScenario SpikeUTurnSharedGreen()
    {
        var b = new ScenarioBuilder();
        var eastIn = b.Lane(-150f, -6f, -10f, -6f);
        var eastOut = b.Lane(10f, -6f, 150f, -6f);
        var westIn = b.Lane(150f, 6f, 10f, 6f);
        var westOut = b.Lane(-10f, 6f, -150f, 6f);
        b.Curve(eastIn[^1], eastOut[0], 4f);
        b.Curve(westIn[^1], westOut[0], 4f);
        var northIn = b.Lane(1.75f, -120f, 1.75f, -12f);
        var northOut = b.Lane(1.75f, 12f, 1.75f, 120f);
        b.Curve(northIn[^1], northOut[0], 4f);
        var into = b.Lane(-10f, -6f, -4f, -6f, 3f).Skip(1).ToArray();
        b.Link(eastIn[^1], into[0]);
        foreach (var w in into) b.S.Waypoints[w].InJunction = true;
        var turn = b.Curve(into[^1], westOut[0], 2f, 20f);
        b.S.Junctions.Add(new ScenarioJunction { Id = 0, Kind = "lights", Roads = new() { new[] { eastIn[^1], westIn[^1] }, new[] { northIn[^1] } }, Green = 8f, Yellow = 1f, FirstRoad = 1, Offset = 5f });
        b.Flags(eastIn[^1], stop: true, enter: true, junction: 0, road: 0);
        b.Flags(westIn[^1], stop: true, enter: true, junction: 0, road: 0);
        b.Flags(northIn[^1], stop: true, enter: true, junction: 0, road: 1);
        b.Flags(eastOut[0], exit: true, junction: 0);
        b.Flags(westOut[0], exit: true, junction: 0);
        b.Flags(northOut[0], exit: true, junction: 0);
        b.Ride(Path(Tail(eastIn, 45f), into, turn, Head(westOut, 60f)), RideCar.Limo with { StartMs = 9f });
        b.CarAt(westIn, 52f, 0.95f, 13f);
        b.CarAt(westIn, 26f, 0.95f, 13f);
        b.CarAt(northIn, 60f, 0.9f, 12f);
        b.S.Name = "spike-uturn-shared-green";
        b.S.Note = "nwh5: U-turned across the oncoming lanes on a shared green and was hit at 13 m/s.";
        b.S.Seconds = 60f;
        return b.S;
    }

    public static TrafficScenario SpikeWaitAfterOncoming()
    {
        var b = new ScenarioBuilder();
        var c = Crossroads(b, 9f, 130f);
        var roads = new List<int[]> { new[] { c.In[0][^1], c.In[2][^1] }, new[] { c.In[1][^1], c.In[3][^1] } };
        b.S.Junctions.Add(new ScenarioJunction { Id = 0, Kind = "lights", Roads = roads, Green = 60f, Yellow = 3f, FirstRoad = 0 });
        for (var d = 0; d < 4; d++)
        {
            b.Flags(c.In[d][^1], stop: true, enter: true, junction: 0, road: d % 2);
            b.Flags(c.Out[d][0], exit: true, junction: 0);
        }
        b.Ride(Path(Tail(c.In[2], 40f), c.Move[(2, 3)], Head(c.Out[3], 50f)), RideCar.Limo with { StartMs = 6f });
        for (var k = 0; k < 3; k++) b.CarAt(Path(c.In[0], c.Move[(0, 2)], c.Out[2]), 70f + k * 25f, 0.9f, 12f);
        b.CarAt(Path(c.In[1], c.Move[(1, 3)], c.Out[3]), 118f, 1f, 0f);
        b.S.Name = "spike-wait-after-oncoming";
        b.S.Note = "nwh6: stood 9 s at the give-way after the oncoming cars had passed, then a traffic car stopped 2 m away and both waited for good.";
        b.S.Seconds = 40f;
        return b.S;
    }

    public static TrafficScenario SpikeNoseToNose()
    {
        var b = new ScenarioBuilder();
        var c = Crossroads(b, 9f, 120f);
        var ride = Path(Tail(c.In[2], 30f), c.Move[(2, 3)], Head(c.Out[3], 50f));
        b.Ride(ride, RideCar.Limo with { StartMs = 0f });
        var line = DrivenRides.Core.Drive.DriveLine.Build(ride.Select(i => new Vec3(b.S.Waypoints[i].X, 0, b.S.Waypoints[i].Z)).ToList(), Array.Empty<(int, DrivenRides.Core.Ride.Polyline)>());
        var p = line.Line.At(36f);
        var q = line.Line.At(37f);
        b.S.Ride.X = p.X;
        b.S.Ride.Z = p.Z;
        b.S.Ride.Heading = (float)Math.Atan2(q.X - p.X, q.Z - p.Z);
        float hx = (float)Math.Sin(b.S.Ride.Heading), hz = (float)Math.Cos(b.S.Ride.Heading);
        var frontX = p.X + hx * RideCar.Limo.FrontAhead;
        var frontZ = p.Z + hz * RideCar.Limo.FrontAhead;
        var other = b.Car(0f, Path(c.Move[(3, 1)], c.Out[1]), 1f, 4.6f, 3f);
        other.HasPose = true;
        other.Heading = (float)(Math.PI / 2);
        other.X = frontX - 0.3f - 2.3f - 0.9f;
        other.Z = frontZ + 0.8f;
        other.Speed = 0f;
        b.S.Name = "spike-nose-to-nose";
        b.S.Note = "nwh5/nwh6: touched a traffic car that stops for it at 1 m/s, then both stood for good.";
        b.S.Seconds = 40f;
        return b.S;
    }

    public static TrafficScenario AmberThroughThenHeldShort(float amberAt = 5.5f)
    {
        var b = new ScenarioBuilder();
        var lane = b.Lane(-200f, 0f, 200f, 0f, 2f);
        var line = Array.FindIndex(lane, w => Math.Abs(b.S.Waypoints[w].X) < 0.1f);
        var hold = Array.FindIndex(lane, w => Math.Abs(b.S.Waypoints[w].X - 6f) < 0.1f);
        b.Flags(lane[line], enter: true, junction: 0, road: 0);
        b.Flags(lane[hold], stop: true, enter: true, junction: 1, road: 0);
        b.S.Junctions.Add(new ScenarioJunction { Id = 0, Kind = "replay", Roads = new() { new[] { lane[line] } }, Green = 30f, Yellow = 1f });
        b.S.Junctions.Add(new ScenarioJunction { Id = 1, Kind = "replay", Roads = new() { new[] { lane[hold] } }, Green = 30f, Yellow = 1f });
        var red = new ScenarioTimeline { Waypoint = lane[line] };
        red.At.AddRange(new[] { amberAt, 30f });
        red.Stop.AddRange(new[] { true, false });
        var held = new ScenarioTimeline { Waypoint = lane[hold] };
        held.At.Add(15f);
        held.Stop.Add(false);
        b.S.Timelines.Add(red);
        b.S.Timelines.Add(held);
        b.Ride(lane.Skip(line - 30).Take(120), RideCar.Limo with { StartMs = 13.9f });
        b.CarAt(lane, 200f - 38f, 1f, 13.9f);
        b.S.Name = "amber-through-then-held-short";
        b.S.Seconds = 60f;
        return b.S;
    }

    public static TrafficScenario PullAway(int seed)
    {
        var r = new Random(seed);
        var b = new ScenarioBuilder();
        var east = b.Lane(-160f, -1.75f, 160f, -1.75f, 4f);
        var line = Array.FindIndex(east, w => b.S.Waypoints[w].X >= -40f + (float)r.NextDouble() * 16f);
        var join = Array.FindIndex(east, w => b.S.Waypoints[w].X >= 10f);
        b.Flags(east[line], enter: true, junction: 0, road: 0);
        b.S.Junctions.Add(new ScenarioJunction { Id = 0, Kind = "replay", Roads = new() { new[] { east[line] } }, Green = 60f, Yellow = 1f });
        var goAt = Range(r, 0f, 8f);
        var green = new ScenarioTimeline { Waypoint = east[line] };
        green.At.AddRange(new[] { 0f, goAt, goAt + 25f });
        green.Stop.AddRange(new[] { true, false, true });
        b.S.Timelines.Add(green);
        var side = b.Lane(2.5f, -90f, 2.5f, -9f);
        var turn = b.Curve(side[^1], east[join], 2.5f, 25f);
        var car = r.Next(2) == 0 ? RideCar.Limo : RideCar.Dash;
        b.Ride(Path(Tail(side, Range(r, 30f, 70f)), turn, east.Skip(join).Take(15)), car with { StartMs = Range(r, 0f, 9f) });
        var queue = 3 + r.Next(5);
        var lineX = b.S.Waypoints[east[line]].X;
        for (var k = 0; k < queue; k++)
        {
            var x = lineX - 3.5f - k * Range(r, 6.5f, 8f);
            b.CarAt(east, x + 160f, Range(r, 0.7f, 1f), 0f);
        }
        Stream(b, r, () => east, 4f, goAt + 25f, 3.5f, 10f);
        b.S.Name = $"pullaway-{seed}";
        b.S.Seconds = 90f;
        return b.S;
    }
}
