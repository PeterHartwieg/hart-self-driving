using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;

namespace DrivenRides.Core.Tests;

// Reads a T0 scenario written by TrafficScenario.ToJson: a template's, or one the game dumped when a traffic run
// failed (NwhDrive). Drop a dump into tests/traffic-scenarios/ and it runs in every `dotnet test`.
internal static class TrafficScenarioJson
{
    public static TrafficScenario Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var s = new TrafficScenario
        {
            Name = Str(root, "name"),
            Note = Str(root, "note"),
            Seconds = root.TryGetProperty("seconds", out var sec) ? sec.GetSingle() : 60f,
        };
        foreach (var w in root.GetProperty("waypoints").EnumerateArray())
        {
            var p = w.GetProperty("p");
            var flags = w.TryGetProperty("f", out var f) ? f.GetString() ?? "" : "";
            s.Waypoints.Add(new ScenarioWaypoint
            {
                X = p[0].GetSingle(), Y = p[1].GetSingle(), Z = p[2].GetSingle(), Next = Ints(w.GetProperty("n")),
                InJunction = w.GetProperty("x").GetInt32() == 1, Kmh = w.GetProperty("s").GetSingle(),
                Stop = flags.Contains('S'), GiveWay = flags.Contains('G'), Enter = flags.Contains('E'), Exit = flags.Contains('X'),
                Junction = w.TryGetProperty("j", out var j) ? j.GetInt32() : -1, Road = w.TryGetProperty("r", out var r) ? r.GetInt32() : -1,
                Source = w.TryGetProperty("src", out var src) ? src.GetInt32() : -1,
            });
        }
        foreach (var x in root.GetProperty("junctions").EnumerateArray())
            s.Junctions.Add(new ScenarioJunction
            {
                Id = x.GetProperty("id").GetInt32(), Kind = Str(x, "kind"), Roads = x.GetProperty("roads").EnumerateArray().Select(Ints).ToList(),
                Green = x.GetProperty("green").GetSingle(), Yellow = x.GetProperty("yellow").GetSingle(), FirstRoad = x.GetProperty("first").GetInt32(),
                Offset = x.GetProperty("offset").GetSingle(),
            });
        var ride = root.GetProperty("ride");
        var pose = ride.GetProperty("pose");
        s.Ride = new ScenarioRide
        {
            Path = Ints(ride.GetProperty("path")), Wheelbase = ride.GetProperty("wheelbase").GetSingle(), Lock = ride.GetProperty("lock").GetSingle(),
            FrontAhead = ride.GetProperty("front").GetSingle(), RearBehind = ride.GetProperty("rear").GetSingle(), HalfWidth = ride.GetProperty("halfWidth").GetSingle(),
            TopMs = ride.GetProperty("top").GetSingle(), X = pose[0].GetSingle(), Z = pose[1].GetSingle(), Heading = pose[2].GetSingle(), Speed = pose[3].GetSingle(),
            Arcs = ride.GetProperty("arcs").EnumerateArray().Select(a => new ScenarioArc
            {
                Entry = a.GetProperty("entry").GetInt32(), Radius = a.GetProperty("radius").GetSingle(),
                Points = a.GetProperty("points").EnumerateArray().Select(q => new Vec3(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle())).ToList(),
            }).ToList(),
            Slow = ride.GetProperty("slow").EnumerateArray().Select(q => (q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle())).ToList(),
        };
        foreach (var c in root.GetProperty("cars").EnumerateArray())
        {
            var car = new ScenarioCar
            {
                Id = c.GetProperty("id").GetInt32(), Spawn = c.GetProperty("spawn").GetSingle(), Route = Ints(c.GetProperty("route")),
                Length = c.GetProperty("length").GetSingle(), Width = c.GetProperty("width").GetSingle(), Trigger = c.GetProperty("trigger").GetSingle(),
                Pace = c.GetProperty("pace").GetSingle(),
            };
            if (c.TryGetProperty("pose", out var cp))
            {
                car.HasPose = true;
                car.X = cp[0].GetSingle();
                car.Z = cp[1].GetSingle();
                car.Heading = cp[2].GetSingle();
                car.Speed = cp[3].GetSingle();
            }
            s.Cars.Add(car);
        }
        if (root.TryGetProperty("timelines", out var timelines))
            foreach (var t in timelines.EnumerateArray())
            {
                var line = new ScenarioTimeline { Waypoint = t.GetProperty("w").GetInt32() };
                line.At.AddRange(t.GetProperty("at").EnumerateArray().Select(e => e.GetSingle()));
                line.Stop.AddRange(t.GetProperty("stop").EnumerateArray().Select(e => e.GetInt32() == 1));
                s.Timelines.Add(line);
            }
        // The history is for reading; the simulation starts from the cars' poses.
        return s;
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
    private static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    public static string? Folder()
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(TrafficScenarioJson).Assembly.Location)!); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "traffic-scenarios");
            if (Directory.Exists(path)) return path;
        }
        return null;
    }
}
