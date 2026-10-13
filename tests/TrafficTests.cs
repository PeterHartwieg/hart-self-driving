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
    public void Soak(string template)
    {
        var seeds = int.TryParse(Environment.GetEnvironmentVariable("FT_TRAFFIC_SEEDS"), out var n) ? n : 500;
        var failures = new ConcurrentBag<(int Seed, string Why)>();
        var watch = Stopwatch.StartNew();
        var arrived = 0;
        Parallel.For(0, seeds, seed =>
        {
            var r = Run(TrafficTemplates.Build(template, seed));
            if (r.Failure != null || r.Blocked != null) failures.Add((seed, r.Failure ?? "blocked: " + r.Blocked));
            else System.Threading.Interlocked.Increment(ref arrived);
        });
        output.WriteLine($"{template}: {seeds} seeds in {watch.ElapsedMilliseconds} ms, {failures.Count} failed");
        foreach (var (seed, why) in failures.OrderBy(f => f.Seed).Take(20)) output.WriteLine($"  seed {seed}: {why}");
        Assert.Empty(failures);
    }

    // FT_TRAFFIC_TRACE=lights:12 prints one seed's run, every half second; FT_TRAFFIC_TRACE=file:<name>.json a dump's.
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
            Assert.Equal(a.ToString(), b.ToString());
            Assert.InRange(b.Time, a.Time - 0.05f, a.Time + 0.05f);
        }
    }

    // Every scenario in tests/traffic-scenarios/ (dumps from the game's failed traffic runs, turned into tests).
    [Fact]
    public void DumpedScenarios()
    {
        var folder = TrafficScenarioJson.Folder();
        Assert.NotNull(folder);
        var files = System.IO.Directory.GetFiles(folder!, "*.json");
        foreach (var file in files)
        {
            var r = Run(TrafficScenarioJson.Load(System.IO.File.ReadAllText(file)));
            output.WriteLine($"{System.IO.Path.GetFileName(file)}: {r}");
            Assert.True(r.Failure == null, $"{System.IO.Path.GetFileName(file)}: {r.Failure}");
        }
    }

    // A dump's recorded frames fed to TrafficDriver as they were seen in the game (no simulation): what it decided
    // at each frame. FT_TRAFFIC_REPLAY=<file>.json prints it.
    internal static List<string> ReplayDecisions(TrafficScenario s)
    {
        var graph = s.BuildGraph();
        var r = s.Ride;
        var line = DrivenRides.Core.Drive.DriveLine.Build(r.Path.Select(i => graph.Position(i)).ToList(), r.Arcs.Select(a => (a.Entry, new DrivenRides.Core.Ride.Polyline(a.Points) { Radius = a.Radius })).ToList());
        var driver = new DrivenRides.Core.Drive.PursuitDriver(line, r.Wheelbase, r.FrontAhead, r.TopMs);
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
            lines.Add($"{f.T,5:0.0} v {f.RideSpeed,4:0.0} along {driver.Along,6:0.0} yield {d.YieldAlong,6:0.0} for {d.YieldFor,3} gap {d.GapAhead,5:0.0} {d.Why} {d.YieldNote}");
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
    // Fed the recorded frames, the ride car must give way to it at every frame from the first until the contact.
    [Fact]
    public void Ride9KeepsGivingWayToTheTruck()
    {
        var s = TrafficScenarioJson.Load(System.IO.File.ReadAllText(System.IO.Path.Combine(TrafficScenarioJson.Folder()!, "t3c-211205-ride9-contact-1.json")));
        var lines = ReplayDecisions(s);
        var first = lines.FindIndex(l => l.Contains("for   0"));
        Assert.True(first >= 0, string.Join("\n", lines));
        var dropped = lines.Skip(first).Where(l => !l.Contains("for   0")).ToList();
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
}
