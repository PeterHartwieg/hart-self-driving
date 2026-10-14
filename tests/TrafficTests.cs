using DrivenRides.Core.Routing;
using System.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DrivenRides.Core.Testing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

public sealed class TrafficTests
{
    private readonly ITestOutputHelper output;
    public TrafficTests(ITestOutputHelper output) => this.output = output;

    internal static SimResult Run(TrafficScenario s, bool trace = false) => new TrafficSim(s, trace).Run();

    [Theory]
    [InlineData("lights")]
    [InlineData("priority")]
    [InlineData("merge")]
    [InlineData("uturn")]
    [InlineData("headon")]
    [InlineData("pullaway")]
    public void Soak(string template)
    {
        var seeds = int.TryParse(Environment.GetEnvironmentVariable("FT_TRAFFIC_SEEDS"), out var n) ? n : 500;
        var failures = new ConcurrentBag<(int Seed, string Why)>();
        var watch = Stopwatch.StartNew();
        var arrived = 0;
        var forced = 0;
        var marginal = 0;
        var marginalUnderHalf = 0;
        var late = 0;
        var again = 0;
        var harder = 0;
        var newHazards = 0;
        var newHazardRuns = 0;
        var exhausted = 0;
        var unsettled = 0;
        var roundsMost = new int[1];
        var invariantFailures = new ConcurrentBag<string>();
        var optionLost = new ConcurrentBag<string>();
        var lostKinds = new Dictionary<string, int>();
        var notEmergency = new ConcurrentBag<string>();
        var times = new List<float>();
        var forcedSeeds = new ConcurrentBag<(int Seed, string What)>();
        Parallel.For(0, seeds, seed =>
        {
            var r = Run(TrafficTemplates.Build(template, seed));
            System.Threading.Interlocked.Add(ref forced, r.ForcedCommits);
            System.Threading.Interlocked.Add(ref marginal, r.MarginalCommits);
            if (r.MarginalLeast < 0.5f) System.Threading.Interlocked.Increment(ref marginalUnderHalf);
            System.Threading.Interlocked.Add(ref late, r.ApproachesLate);
            System.Threading.Interlocked.Add(ref again, r.ApproachesAgain);
            if (r.ForcedUnderFloor > 0) invariantFailures.Add($"seed {seed}: a forced commit under the floor");
            if (r.NotEmergency != null) notEmergency.Add($"seed {seed}: {r.NotEmergency}");
            if (r.LostKind != null) lock (lostKinds) lostKinds[r.LostKind] = lostKinds.TryGetValue(r.LostKind, out var lk) ? lk + 1 : 1;
            if (r.OptionLost > 0f) optionLost.Add($"seed {seed}: the stopping option lost (short of the way by {r.OptionLost:0.00} m at HardDecel)");
            System.Threading.Interlocked.Add(ref harder, r.HarderThanDecel);
            System.Threading.Interlocked.Add(ref newHazards, r.NewHazards);
            if (r.NewHazards > 0) System.Threading.Interlocked.Increment(ref newHazardRuns);
            System.Threading.Interlocked.Add(ref exhausted, r.PassExhausted);
            System.Threading.Interlocked.Add(ref unsettled, r.PassUnsettled);
            lock (roundsMost) roundsMost[0] = Math.Max(roundsMost[0], r.PassRoundsMost);
            if (r.Arrived && r.Failure == null) lock (times) times.Add(r.Time);
            if (r.ForcedCommits > 0) forcedSeeds.Add((seed, r.Forced[0]));
            if (r.Failure != null || r.Blocked != null) failures.Add((seed, r.Failure ?? "blocked: " + r.Blocked));
            else System.Threading.Interlocked.Increment(ref arrived);
        });
        output.WriteLine($"{template}: {seeds} seeds in {watch.ElapsedMilliseconds} ms, {failures.Count} failed, {forced} forced commits in {forcedSeeds.Count} seeds, {marginal} marginal ({marginalUnderHalf} runs under 0.5 s to spare), {late} stops imposed late, {again} imposed again within 1 s, {harder} decisions braking past Decel, final pass up to {roundsMost[0]} rounds ({exhausted} out of rounds, {unsettled} unsettled), mean arrival {(times.Count > 0 ? times.Average() : 0f):0.00} s, {optionLost.Count} runs losing the stopping option avoidably, {newHazardRuns} runs with a stop made unmakeable by news ({newHazards})");
        foreach (var (seed, why) in failures.OrderBy(f => f.Seed).Take(20)) output.WriteLine($"  seed {seed}: {why}");
        foreach (var (seed, what) in forcedSeeds.OrderBy(f => f.Seed).Take(10)) output.WriteLine($"  forced, seed {seed}: {what}");
        foreach (var f in invariantFailures.Take(10)) output.WriteLine("  invariant, " + f);
        foreach (var f in optionLost.OrderBy(f => f).Take(10)) output.WriteLine("  " + f);
        output.WriteLine($"  {notEmergency.Count} runs with a stop it could no longer make not from the emergency rule");
        foreach (var f in notEmergency.OrderBy(f => f).Take(5)) output.WriteLine("    " + f);
        output.WriteLine("  lost by kind: " + string.Join(", ", lostKinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")));
        // Contacts and red runs are failures above; these are the others (review round 6, decision a), and the final
        // pass settling (round 7, BB4).
        Assert.Empty(failures);
        Assert.Empty(invariantFailures);
        Assert.True(unsettled == 0, $"{unsettled} decisions whose final pass didn't settle");
        // Every stop the car could no longer make came from the emergency rule (review round 8, CC3).
        Assert.Empty(notEmergency);
        // A ratchet from 0.1.79-traffic16 (review round 6; round 7 added the last two; round 8 counts only avoidable
        // losses): runs with a forced commit, reduced-headway continuations, runs that lost the stopping option for a
        // car with no news of it (CC4: meant to be a hard zero; not yet, see the review notes), and stops imposed again
        // within a second (flicker, BB1). Per template, only ever lowered.
        if (Ratchet.TryGetValue(template, out var most))
        {
            Assert.True(forcedSeeds.Count <= most.Forced, $"{forcedSeeds.Count} runs with a forced commit, the ratchet is {most.Forced}");
            Assert.True(marginal <= most.Marginal, $"{marginal} marginal continuations, the ratchet is {most.Marginal}");
            Assert.True(optionLost.Count <= most.OptionLost, $"{optionLost.Count} runs lost the stopping option, the ratchet is {most.OptionLost}");
            Assert.True(again <= most.Again, $"{again} stops imposed again within 1 s, the ratchet is {most.Again}");
        }
        // Forced commits are the emergency rule's: at most 2 % of runs. Review round 4 (X4) asked for 1 %; with the
        // cautious judgement trusting the cars' real stops and give-ways (round 5, Y5) priority has 1.2 %, which the
        // reviewers leave to in-game evidence rather than more tuning in simulation.
        Assert.True(forcedSeeds.Count <= seeds / 50, $"{forcedSeeds.Count} of {seeds} runs had a forced commit");
    }

    // FT_TRAFFIC_TRACE=lights:12 prints one seed's run, every half second; FT_TRAFFIC_TRACE=file:<name>.json a dump's.
    private static readonly Dictionary<string, (int Forced, int Marginal, int OptionLost, int Again)> Ratchet = new()
    {
        ["lights"] = (2, 0, 25, 47), ["priority"] = (2, 2, 95, 3), ["merge"] = (0, 0, 77, 9), ["uturn"] = (0, 9, 9, 25),
        ["headon"] = (0, 0, 20, 0), ["pullaway"] = (1, 9, 7, 0),
    };

    [Fact]
    public void TraceOneSeed()
    {
        var which = Environment.GetEnvironmentVariable("FT_TRAFFIC_TRACE");
        if (string.IsNullOrEmpty(which)) return;
        var parts = which.Split(':');
        var scenario = parts[0] == "file"
            ? TrafficScenarioJson.Load(System.IO.File.ReadAllText(System.IO.Path.Combine(TrafficScenarioJson.Folder()!, parts[1])))
            : TrafficTemplates.Build(parts[0], int.Parse(parts[1]));
        var r = Run(scenario, true);
        output.WriteLine(r.ToString());
        foreach (var f in r.Forced) output.WriteLine("forced: " + f);
        output.WriteLine($"option lost by {r.OptionLost:0.00} m, forced under floor {r.ForcedUnderFloor}, late {r.ApproachesLate}, again {r.ApproachesAgain}, harder than Decel {r.HarderThanDecel}");
        output.WriteLine(r.Trace.ToString());
    }

    // The NWH spike's three traffic failures (docs/nwh-drive-spike.md, "Traffic on").

    [Fact]
    public void SpikeUTurnAcrossOncomingOnASharedGreen()
    {
        var r = Run(TrafficTemplates.SpikeUTurnSharedGreen());
        Assert.True(r.Failure == null, r.Failure);
        Assert.True(r.Arrived);
        // It waited for the oncoming car instead of turning in front of it.
        Assert.Contains(1, r.GaveWayTo);
    }

    [Fact]
    public void SpikeGoesOnceTheOncomingCarsHavePassed()
    {
        var r = Run(TrafficTemplates.SpikeWaitAfterOncoming());
        Assert.True(r.Failure == null, r.Failure);
        // The last oncoming car is past the turn at about 11 s; the turn and 50 m on take under 8 s more.
        Assert.True(r.Time < 20f, $"arrived at {r.Time:0.0} s");
    }

    [Fact]
    public void SpikeNoseToNoseWithACarThatWaitsForIt()
    {
        var r = Run(TrafficTemplates.SpikeNoseToNose());
        Assert.True(r.Failure == null, r.Failure);
        Assert.True(r.BackOffs >= 1);
    }

    // A scenario survives the JSON round trip unchanged: it runs to the same end.
    [Fact]
    public void ScenarioJsonRoundTrip()
    {
        foreach (var s in new[] { TrafficTemplates.Build("lights", 7), TrafficTemplates.SpikeNoseToNose() })
        {
            var again = TrafficScenarioJson.Load(s.ToJson());
            Assert.Equal(s.Waypoints.Count, again.Waypoints.Count);
            Assert.Equal(s.Cars.Count, again.Cars.Count);
            var a = Run(s);
            var b = Run(again);
            // Written at round-trip precision: the very same run.
            Assert.Equal(a.ToString(), b.ToString());
            Assert.Equal(a.Time, b.Time);
        }
    }

    // Every scenario in tests/traffic-scenarios/ (dumps from the game's failed traffic runs, turned into tests): no
    // contact, no red run, no standstill over 30 s, and resolved: the ride car arrives, leaves the recorded area, or
    // finds its way blocked by a car that won't move (the planner's to go round). No exemptions (review round 1).
    [Fact]
    public void DumpedScenarios()
    {
        var folder = TrafficScenarioJson.Folder();
        Assert.NotNull(folder);
        foreach (var file in System.IO.Directory.GetFiles(folder!, "*.json"))
        {
            var r = Run(TrafficScenarioJson.Load(System.IO.File.ReadAllText(file)));
            output.WriteLine($"{System.IO.Path.GetFileName(file)}: {r}");
            Assert.True(r.Failure == null, $"{System.IO.Path.GetFileName(file)}: {r.Failure}");
            Assert.True(r.Arrived || r.Blocked != null, $"{System.IO.Path.GetFileName(file)}: {r}");
        }
    }

    // Dumps the simulation can't resolve yet, kept visible: tests/traffic-scenarios/pending/ (each one's note says why).
    // Each has a replay test of the decision it took in the game.
    [Fact(Skip = "pending: tests/traffic-scenarios/pending/ (ride 17: a merge into a stream with no gap and no car slow enough to nose in on; see its note)")]
    public void PendingScenarios()
    {
        foreach (var file in System.IO.Directory.GetFiles(System.IO.Path.Combine(TrafficScenarioJson.Folder()!, "pending"), "*.json"))
        {
            var r = Run(TrafficScenarioJson.Load(System.IO.File.ReadAllText(file)));
            Assert.True(r.Failure == null && (r.Arrived || r.Blocked != null), $"{System.IO.Path.GetFileName(file)}: {r}");
        }
    }

    // T3 20261013-213209, ride 17 (62 s at a merge): fed the recorded frames, the ride car waits at the merge point
    // (where it gives way is never short of its front: it has come up as far as it may) and only ever for a car that
    // is moving and coming at the merge (none standing, none past where the ride car waits). Why it doesn't
    // resolve in these frames is in the dump's note (review round 2, U4): no car in them is slow enough to nose in
    // on, and the 10 s kept hold no gap.
    [Fact]
    public void Ride17WaitsAtTheMergeForCarsThatAreComing()
    {
        var s = TrafficScenarioJson.Load(System.IO.File.ReadAllText(System.IO.Path.Combine(TrafficScenarioJson.Folder()!, "pending", "t3d-213209-ride17-stall.json")));
        var lines = ReplayDecisions(s);
        var waits = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var y = System.Text.RegularExpressions.Regex.Match(lines[i], @"along +(-?[\d,.]+) yield +(-?[\d,.]+)");
            if (y.Success)
            {
                waits++;
                var along = float.Parse(y.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
                var yield = float.Parse(y.Groups[2].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(yield >= along + s.Ride.FrontAhead - 0.5f, $"frame {i}: gives way short of where it stands: {lines[i]}");
            }
            var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"for +(\d+)");
            if (!m.Success) continue;
            var id = int.Parse(m.Groups[1].Value);
            var car = s.History[i].Cars.FirstOrDefault(c => c.Id == id);
            Assert.True(car.Id == id, $"frame {i}: gives way to car {id}, which isn't there");
            var speed = Math.Sqrt(car.Vx * car.Vx + car.Vz * car.Vz);
            Assert.True(speed > DrivenRides.Core.Drive.TrafficDriver.StandingMs, $"frame {i}: gives way to car {id} standing ({speed:0.0} m/s): {lines[i]}");
            // Coming at the merge (review round 3, V5): the ride car's front, where it waits, is ahead of the car's rear
            // along the way the car is going, so the car has yet to pass it.
            var f = s.History[i];
            double fx = f.RideX + Math.Sin(f.RideHeading) * s.Ride.FrontAhead, fz = f.RideZ + Math.Cos(f.RideHeading) * s.Ride.FrontAhead;
            var length = s.Cars.First(c => c.Id == id).Length;
            double rx = car.X - Math.Sin(car.Heading) * length / 2, rz = car.Z - Math.Cos(car.Heading) * length / 2;
            var coming = ((fx - rx) * car.Vx + (fz - rz) * car.Vz) / speed;
            // Give or take half a sweep step: the sweep counts the body between two poses too (review round 7), so a
            // car whose rear has just gone past the front is still in the way of the half step ahead of it.
            Assert.True(coming > -DrivenRides.Core.Drive.TrafficDriver.SweepStep / 2f, $"frame {i}: gives way to car {id}, past the merge by {-coming:0.0} m: {lines[i]}");
        }
        Assert.True(waits > lines.Count / 2, $"gives way in only {waits} of {lines.Count} frames");
    }

    // Review round 2 (U4): nosing in resolves a merge into a crawling stream: the ride car gets in front of a car that
    // stops for it in its trigger, without a contact, where waiting for a gap would stall.
    [Fact]
    public void NosesIntoACrawlingStream()
    {
        var failed = new List<string>();
        var nosed = 0;
        const int seeds = 20;
        for (var seed = 0; seed < seeds; seed++)
        {
            var r = Run(TrafficTemplates.Crawl(seed));
            if (r.Failure != null || !r.Arrived) failed.Add($"seed {seed}: {r}");
            if (r.NoseIns > 0) nosed++;
        }
        output.WriteLine($"crawl: {seeds} seeds, {nosed} nosed in, {failed.Count} failed");
        foreach (var f in failed) output.WriteLine("  " + f);
        Assert.Empty(failed);
        Assert.True(nosed >= seeds * 3 / 4, $"only {nosed} of {seeds} nosed in");
    }

    // A dump's recorded frames fed to TrafficDriver as they were seen in the game (no simulation): what it decided
    // at each frame. FT_TRAFFIC_REPLAY=<file>.json prints it.
    internal static List<string> ReplayDecisions(TrafficScenario s)
    {
        var graph = s.BuildGraph();
        var r = s.Ride;
        var line = r.BuildLine(graph, out _);
        var driver = r.BuildDriver(line);
        var traffic = new DrivenRides.Core.Drive.TrafficDriver(graph, line, driver, new DrivenRides.Core.Drive.RideBody { Wheelbase = r.Wheelbase, FrontAhead = r.FrontAhead, RearBehind = r.RearBehind, HalfWidth = r.HalfWidth });
        var flags = new ReplayFlags(s);
        var d = new DrivenRides.Core.Drive.TrafficDecision();
        var lines = new List<string>();
        var sizes = s.Cars.ToDictionary(c => c.Id);
        foreach (var f in s.History)
        {
            driver.StartAt(f.RideX, f.RideZ);
            var cars = f.Cars.Where(c => sizes.ContainsKey(c.Id)).Select(c => new DrivenRides.Core.Drive.OtherCar
            {
                Id = c.Id, X = c.X, Z = c.Z, Heading = c.Heading, Vx = c.Vx, Vz = c.Vz, Length = sizes[c.Id].Length, Width = sizes[c.Id].Width,
                TriggerLength = sizes[c.Id].Trigger, Target = c.Target, StoppedForUs = c.ForUs, MayChangeLane = c.Action == "Follow" || c.Action == "Overtake",
            }).ToList();
            traffic.Decide(new DrivenRides.Core.Drive.TrafficSnapshot
            {
                Time = f.T, RearX = f.RideX, RearZ = f.RideZ, Heading = f.RideHeading, Speed = f.RideSpeed,
                Marks = Array.Empty<DrivenRides.Core.Drive.PathMark>(), Cars = cars, Flags = flags,
            }, d);
            lines.Add($"{f.T,5:0.0} v {f.RideSpeed,4:0.0} along {driver.Along,6:0.0} yield {d.YieldAlong,6:0.0} for {d.YieldFor,3} gap {d.GapAhead,5:0.0} {d.Why} {d.YieldNote}{(d.ForcedCommit ? $" | forced {d.ForcedFor} with {d.ForcedMargin:0.00} s" : "")}");
        }
        return lines;
    }

    private sealed class ReplayFlags : DrivenRides.Core.Drive.ILiveFlags
    {
        private readonly TrafficScenario s;
        public ReplayFlags(TrafficScenario s) => this.s = s;
        public bool Stop(int w) => w >= 0 && w < s.Waypoints.Count && s.Waypoints[w].Stop;
        public bool GiveWayEntry(int w, out int junction, out int road) { junction = road = -1; return false; }
    }

    [Fact]
    public void ReplayOneDump()
    {
        var file = Environment.GetEnvironmentVariable("FT_TRAFFIC_REPLAY");
        if (string.IsNullOrEmpty(file)) return;
        foreach (var l in ReplayDecisions(TrafficScenarioJson.Load(System.IO.File.ReadAllText(System.IO.Path.Combine(TrafficScenarioJson.Folder()!, file))))) output.WriteLine(l);
    }

    // T3 20261013-211205, ride 9: a truck (11 m) coming up on the ride car's left as it turned left across its lane.
    // Fed the recorded frames, the ride car must give way to it at every frame from the first until the contact, or,
    // where the recorded car drove on past the stop it was given (a replay doesn't obey the decisions) and can no
    // longer stop short of the truck's way, the emergency rule decides: a forced commit with the floor to spare
    // (review round 6: before, the cautious stop was kept for a car past that point, which stops it in the truck's
    // way where standing isn't safe).
    [Fact]
    public void Ride9KeepsGivingWayToTheTruck()
    {
        var s = TrafficScenarioJson.Load(System.IO.File.ReadAllText(System.IO.Path.Combine(TrafficScenarioJson.Folder()!, "t3c-211205-ride9-contact-1.json")));
        var lines = ReplayDecisions(s);
        var first = lines.FindIndex(l => l.Contains("for   0"));
        Assert.True(first >= 0, string.Join("\n", lines));
        var dropped = lines.Skip(first).Where(l => !l.Contains("for   0") && !l.Contains("| forced 0 ")).ToList();
        foreach (var l in lines.Skip(first)) output.WriteLine(l);
        Assert.True(dropped.Count == 0, $"dropped car 0 at:\n{string.Join("\n", dropped.Take(5))}");
    }

    // T3 20261013-211205, ride 18: a car in the lane beside, following a slower car, overtook into the ride car's
    // lane while alongside (Gley's lane change checks only its own cars' targets). From when it starts to overtake
    // (Overtake, 1 s before it moves over) to the contact, the ride car must be giving way to it.
    [Fact]
    public void Ride18GivesWayToACarOvertakingIntoItsLane()
    {
        var s = TrafficScenarioJson.Load(System.IO.File.ReadAllText(System.IO.Path.Combine(TrafficScenarioJson.Folder()!, "t3c-211205-ride18-contact-1.json")));
        var lines = ReplayDecisions(s);
        var overtaking = s.History.FindIndex(f => f.Cars.Any(c => c.Id == 20 && c.Action == "Overtake"));
        Assert.True(overtaking >= 0);
        var after = lines.Skip(overtaking).ToList();
        foreach (var l in after) output.WriteLine(l);
        Assert.All(after, l => Assert.Contains("for  20", l));
    }

    [Fact]
    public void AmberThroughThenHeldShortWaitsForTheGreen()
    {
        var r = Run(TrafficTemplates.AmberThroughThenHeldShort(), true);
        output.WriteLine(r.Trace.ToString());
        Assert.True(r.Failure == null, r.Failure);
        Assert.True(r.Time > 30f, $"arrived at {r.Time:0.0} s, before the green");
    }


    // Review round 1 (S10): a dump carries the calibrated car and the line as driven, and the simulation and the
    // replay use them rather than a bicycle on a line rebuilt from the waypoints.
    [Fact]
    public void ScenarioCarriesTheCarAndTheDrivenLine()
    {
        var s = TrafficTemplates.Build("uturn", 3);
        var graph = s.BuildGraph();
        var built = s.Ride.BuildLine(graph, out var along);
        // A line moved 0.6 m to the side: as a shifted line would be.
        s.Ride.Line = built.Line.Points.Select(p => new Vec3(p.X + 0.6f, p.Y, p.Z)).ToList();
        s.Ride.LineAlong = along.ToArray();
        s.Ride.LockCurve = new() { (0f, 40f), (10f, 30f), (20f, 20f) };
        s.Ride.TrackWidth = 1.6f;
        s.Ride.YawFactor = 0.97f;
        s.Ride.WheelRate = 140f;
        s.Ride.Steer = -12f;
        var again = TrafficScenarioJson.Load(s.ToJson()).Ride;
        Assert.Equal(s.Ride.Line.Count, again.Line.Count);
        Assert.Equal(s.Ride.LineAlong.Length, again.LineAlong.Length);
        Assert.InRange(again.LockAt(5f), 34.99f, 35.01f);
        Assert.Equal(1.6f, again.TrackWidth, 3);
        Assert.Equal(140f, again.WheelRate, 3);
        var car = again.BuildCar();
        Assert.Equal(-12.0, car.Steer, 3);
        Assert.Equal(0.97f, car.YawFactor, 3);
        Assert.Equal(1.6f, car.TrackWidth, 3);
        // The line used is the driven one (0.6 m over), not the waypoints'.
        var line = again.BuildLine(graph, out var lineAlong);
        Assert.Equal(again.LineAlong, lineAlong);
        Assert.InRange(line.Line.Points[0].X - built.Line.Points[0].X, 0.59f, 0.61f);
    }
}
