using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Testing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// The slowdown dumps real rides write (NwhDrive.SlowdownDump): DR_CLASSIFY=<folder> replays each one's decisions on
// the recorded frames and says, for the stop that began the slowdown, which rule made it, for which car, and whether
// that car's recorded body came within NeededMetres of where the ride car's body would have been had it gone on at the
// speed it had (along its line, for LookSeconds, or until a second past when it would have met the car): needed, or needless (Peter, 14 October: "it gives way to other cars
// where nobody would usually give way").
public sealed class SlowdownClassifierTests
{
    public const float NeededMetres = 2f, LookSeconds = 3f;
    private readonly ITestOutputHelper output;
    public SlowdownClassifierTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void ClassifySlowdownDumps()
    {
        var folder = Environment.GetEnvironmentVariable("DR_CLASSIFY");
        if (string.IsNullOrEmpty(folder)) return;
        var counts = new Dictionary<string, int>();
        foreach (var file in System.IO.Directory.GetFiles(folder, "*.json").OrderBy(f => f))
        {
            string line;
            try { line = Classify(TrafficScenarioJson.Load(System.IO.File.ReadAllText(file)), out var key); counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1; }
            catch (Exception ex) { line = "failed: " + ex.Message; }
            output.WriteLine($"{System.IO.Path.GetFileName(file)}: {line}");
        }
        foreach (var kv in counts.OrderByDescending(k => k.Value)) output.WriteLine($"{kv.Value,4} {kv.Key}");
    }

    internal static string Classify(TrafficScenario s, out string key)
    {
        var graph = s.BuildGraph();
        var r = s.Ride;
        var line = r.BuildLine(graph, out _);
        var driver = r.BuildDriver(line);
        var body = new RideBody { Wheelbase = r.Wheelbase, FrontAhead = r.FrontAhead, RearBehind = r.RearBehind, HalfWidth = r.HalfWidth };
        var traffic = new TrafficDriver(graph, line, driver, body);
        var d = new TrafficDecision();
        var flags = new TrafficTests.ReplayFlags(s);
        var sizes = s.Cars.ToDictionary(c => c.Id);
        var frames = s.History;
        // The slowdown: the lowest speed, and the highest before it.
        var low = 0;
        for (var i = 0; i < frames.Count; i++) if (frames[i].RideSpeed < frames[low].RideSpeed) low = i;
        var peak = 0;
        for (var i = 0; i <= low; i++) if (frames[i].RideSpeed >= frames[peak].RideSpeed) peak = i;
        // The decisions from the start, and the first stop for a car from the peak on.
        int onset = -1, car = -1;
        string why = "", note = "";
        var kind = YieldNoteKind.None;
        var look = LookSeconds;
        var along0 = 0f;
        var held = new Dictionary<int, int>();
        var first = new Dictionary<int, (int Frame, string Why, string Note, YieldNoteKind Kind, float Along, float Late)>();
        for (var i = 0; i < frames.Count && i <= low; i++)
        {
            var f = frames[i];
            driver.StartAt(f.RideX, f.RideZ);
            var cars = f.Cars.Where(c => sizes.ContainsKey(c.Id)).Select(c => new OtherCar
            {
                Id = c.Id, X = c.X, Z = c.Z, Heading = c.Heading, Vx = c.Vx, Vz = c.Vz, Length = sizes[c.Id].Length, Width = sizes[c.Id].Width,
                TriggerLength = sizes[c.Id].Trigger, Target = c.Target, StoppedForUs = c.ForUs, MayChangeLane = c.Action == "Follow" || c.Action == "Overtake",
            }).ToList();
            traffic.Decide(new TrafficSnapshot { Time = f.T, RearX = f.RideX, RearZ = f.RideZ, Heading = f.RideHeading, Speed = f.RideSpeed, Marks = Array.Empty<PathMark>(), Cars = cars, Flags = flags }, d);
            if (i < peak) continue;
            var id = d.YieldFor >= 0 ? d.YieldFor : d.GapFor;
            if (id < 0) continue;
            // The car the stops were for most often from the highest speed to the lowest, from where it was first.
            held[id] = held.TryGetValue(id, out var n) ? n + 1 : 1;
            if (!first.ContainsKey(id)) first[id] = (i, d.Why, d.YieldNote, d.Note.Kind, driver.Along, d.Note.Late);
        }
        if (held.Count > 0)
        {
            car = held.OrderByDescending(h => h.Value).First().Key;
            var f0 = first[car];
            onset = f0.Frame; why = f0.Why; note = f0.Note; kind = f0.Kind; along0 = f0.Along;
            if (kind == YieldNoteKind.Conflict) look = Math.Min(8f, Math.Max(LookSeconds, f0.Late + 1f));
        }
        if (onset < 0) { key = "no stop for a car found"; return $"{key} ({s.Note})"; }
        var rule = kind switch
        {
            YieldNoteKind.Approach => "keeping the option",
            YieldNoteKind.KeepClear => "keep clear",
            YieldNoteKind.Conflict => "conflict",
            YieldNoteKind.Still => "still giving way",
            _ => why.StartsWith("behind") ? "behind" : "giving way",
        };
        // Gone on at the onset's speed along the line: the nearest the car's recorded body came to that body.
        var v = Math.Max(1f, frames[onset].RideSpeed);
        var nearest = float.MaxValue;
        for (var i = onset; i < frames.Count && frames[i].T - frames[onset].T <= look; i++)
        {
            var ci = frames[i].Cars.FindIndex(o => o.Id == car);
            if (ci < 0 || !sizes.ContainsKey(car)) continue;
            var c = frames[i].Cars[ci];
            var at = along0 + v * (frames[i].T - frames[onset].T);
            var p = line.Line.At(at);
            var q = line.Line.At(at + 0.5f);
            var ride = traffic.BodyAt(p.X, p.Z, (float)Math.Atan2(q.X - p.X, q.Z - p.Z));
            var other = new Box(c.X, c.Z, c.Heading, sizes[car].Width / 2f, sizes[car].Length / 2f);
            for (var g = 0f; g < Math.Min(nearest, 6f); g += 0.1f) if (ride.Grown(g, g).Overlaps(other)) { nearest = g; break; }
        }
        var needed = nearest < NeededMetres;
        key = $"{rule}, {(needed ? "needed" : "needless")}";
        return $"{key}: car {car} at {frames[onset].T:0.0} s, {v:0.0} m/s, nearest {(nearest < 6f ? nearest.ToString("0.0") + " m" : "over 6 m")}; {why} {note}";
    }

    private sealed class NoFlags : ILiveFlags
    {
        public bool Stop(int waypoint) => false;
        public bool GiveWayEntry(int waypoint, out int junction, out int road) { junction = -1; road = -1; return false; }
    }
}
