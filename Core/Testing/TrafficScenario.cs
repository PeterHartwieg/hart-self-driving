using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    // A T0 traffic scenario (docs/implementation-plan.md, "Tests"): a piece of lane graph with Gley's flags and
    // junctions, the ride car with its path, and traffic cars with their routes. The Core tests build them from
    // templates and run them in a kinematic simulation; the game writes one (NwhDrive) when a traffic run fails, with
    // the last seconds of the cars near the ride car, so the failure can be run offline (tests/traffic-scenarios/).
    public sealed class TrafficScenario
    {
        public string Name = "";
        public string Note = "";
        public float Seconds = 60f;
        public readonly List<ScenarioWaypoint> Waypoints = new List<ScenarioWaypoint>();
        public readonly List<ScenarioJunction> Junctions = new List<ScenarioJunction>();
        public ScenarioRide Ride = new ScenarioRide();
        public readonly List<ScenarioCar> Cars = new List<ScenarioCar>();
        // Stop flags over time, from the game, for waypoints whose junction has no simulated cycle.
        public readonly List<ScenarioTimeline> Timelines = new List<ScenarioTimeline>();
        // What the game saw, ten frames a second (for reading; the simulation starts from the cars' poses).
        public readonly List<ScenarioFrame> History = new List<ScenarioFrame>();

        public LaneGraph BuildGraph()
        {
            var n = Waypoints.Count;
            var positions = new Vec3[n];
            var next = new int[n][];
            var masks = new int[n];
            var junction = new bool[n];
            var kmh = new float[n];
            for (var i = 0; i < n; i++)
            {
                var w = Waypoints[i];
                positions[i] = new Vec3(w.X, w.Y, w.Z);
                next[i] = w.Next;
                masks[i] = 1;
                junction[i] = w.InJunction;
                kmh[i] = w.Kmh;
            }
            return new LaneGraph(positions, next, masks, junction, kmh);
        }

        public string ToJson()
        {
            var j = new StringBuilder();
            j.Append("{\n\"name\":").Append(Str(Name)).Append(",\n\"note\":").Append(Str(Note)).Append(",\n\"seconds\":").Append(F(Seconds)).Append(",\n\"waypoints\":[\n");
            for (var i = 0; i < Waypoints.Count; i++)
            {
                var w = Waypoints[i];
                j.Append("{\"p\":[").Append(F(w.X)).Append(',').Append(F(w.Y)).Append(',').Append(F(w.Z)).Append("],\"n\":").Append(Ints(w.Next))
                 .Append(",\"x\":").Append(w.InJunction ? 1 : 0).Append(",\"s\":").Append(F(w.Kmh));
                var flags = (w.Stop ? "S" : "") + (w.GiveWay ? "G" : "") + (w.Enter ? "E" : "") + (w.Exit ? "X" : "");
                if (flags.Length > 0) j.Append(",\"f\":").Append(Str(flags));
                if (w.Junction >= 0) j.Append(",\"j\":").Append(w.Junction);
                if (w.Road >= 0) j.Append(",\"r\":").Append(w.Road);
                if (w.Source >= 0) j.Append(",\"src\":").Append(w.Source);
                j.Append(i + 1 < Waypoints.Count ? "},\n" : "}\n");
            }
            j.Append("],\n\"junctions\":[");
            for (var i = 0; i < Junctions.Count; i++)
            {
                var x = Junctions[i];
                if (i > 0) j.Append(',');
                j.Append("\n{\"id\":").Append(x.Id).Append(",\"kind\":").Append(Str(x.Kind)).Append(",\"roads\":[");
                for (var r = 0; r < x.Roads.Count; r++) j.Append(r > 0 ? "," : "").Append(Ints(x.Roads[r]));
                j.Append("],\"green\":").Append(F(x.Green)).Append(",\"yellow\":").Append(F(x.Yellow)).Append(",\"first\":").Append(x.FirstRoad)
                 .Append(",\"offset\":").Append(F(x.Offset)).Append('}');
            }
            var ride = Ride;
            j.Append("],\n\"ride\":{\"path\":").Append(Ints(ride.Path)).Append(",\"arcs\":[");
            for (var i = 0; i < ride.Arcs.Count; i++)
            {
                var a = ride.Arcs[i];
                if (i > 0) j.Append(',');
                j.Append("{\"entry\":").Append(a.Entry).Append(",\"radius\":").Append(F(a.Radius)).Append(",\"points\":").Append(Points(a.Points)).Append('}');
            }
            j.Append("],\"wheelbase\":").Append(F(ride.Wheelbase)).Append(",\"lock\":").Append(F(ride.Lock)).Append(",\"front\":").Append(F(ride.FrontAhead))
             .Append(",\"rear\":").Append(F(ride.RearBehind)).Append(",\"halfWidth\":").Append(F(ride.HalfWidth)).Append(",\"top\":").Append(F(ride.TopMs))
             .Append(",\"pose\":[").Append(F(ride.X)).Append(',').Append(F(ride.Z)).Append(',').Append(F(ride.Heading)).Append(',').Append(F(ride.Speed)).Append("],\"slow\":[");
            for (var i = 0; i < ride.Slow.Count; i++)
                j.Append(i > 0 ? "," : "").Append('[').Append(F(ride.Slow[i].From)).Append(',').Append(F(ride.Slow[i].To)).Append(',').Append(F(ride.Slow[i].Ms)).Append(']');
            j.Append("]},\n\"cars\":[");
            for (var i = 0; i < Cars.Count; i++)
            {
                var c = Cars[i];
                j.Append(i > 0 ? ",\n" : "\n").Append("{\"id\":").Append(c.Id).Append(",\"spawn\":").Append(F(c.Spawn)).Append(",\"route\":").Append(Ints(c.Route));
                if (c.HasPose) j.Append(",\"pose\":[").Append(F(c.X)).Append(',').Append(F(c.Z)).Append(',').Append(F(c.Heading)).Append(',').Append(F(c.Speed)).Append(']');
                j.Append(",\"length\":").Append(F(c.Length)).Append(",\"width\":").Append(F(c.Width)).Append(",\"trigger\":").Append(F(c.Trigger)).Append(",\"pace\":").Append(F(c.Pace)).Append('}');
            }
            j.Append("],\n\"timelines\":[");
            for (var i = 0; i < Timelines.Count; i++)
            {
                var t = Timelines[i];
                j.Append(i > 0 ? ",\n" : "\n").Append("{\"w\":").Append(t.Waypoint).Append(",\"at\":[");
                for (var k = 0; k < t.At.Count; k++) j.Append(k > 0 ? "," : "").Append(F(t.At[k]));
                j.Append("],\"stop\":[");
                for (var k = 0; k < t.Stop.Count; k++) j.Append(k > 0 ? "," : "").Append(t.Stop[k] ? 1 : 0);
                j.Append("]}");
            }
            j.Append("],\n\"history\":[");
            for (var i = 0; i < History.Count; i++)
            {
                var h = History[i];
                j.Append(i > 0 ? ",\n" : "\n").Append("{\"t\":").Append(F(h.T)).Append(",\"ride\":[").Append(F(h.RideX)).Append(',').Append(F(h.RideZ)).Append(',')
                 .Append(F(h.RideHeading)).Append(',').Append(F(h.RideSpeed)).Append("],\"cars\":[");
                for (var k = 0; k < h.Cars.Count; k++)
                {
                    var c = h.Cars[k];
                    j.Append(k > 0 ? "," : "").Append('[').Append(c.Id).Append(',').Append(F(c.X)).Append(',').Append(F(c.Z)).Append(',').Append(F(c.Heading)).Append(',')
                     .Append(F(c.Vx)).Append(',').Append(F(c.Vz)).Append(',').Append(c.Target).Append(',').Append(c.ForUs ? 1 : 0).Append(',').Append(Str(c.Action ?? "")).Append(']');
                }
                j.Append("]}");
            }
            j.Append("]\n}\n");
            return j.ToString();
        }

        private static string F(float v) => float.IsNaN(v) || float.IsInfinity(v) ? "0" : Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
        private static string Ints(IEnumerable<int> values) => "[" + string.Join(",", values) + "]";

        private static string Points(IReadOnlyList<Vec3> points)
        {
            var b = new StringBuilder("[");
            for (var i = 0; i < points.Count; i++) b.Append(i > 0 ? "," : "").Append('[').Append(F(points[i].X)).Append(',').Append(F(points[i].Y)).Append(',').Append(F(points[i].Z)).Append(']');
            return b.Append(']').ToString();
        }

        private static string Str(string value)
        {
            var b = new StringBuilder("\"");
            foreach (var c in value)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }
    }

    public sealed class ScenarioWaypoint
    {
        public float X, Y, Z;
        public int[] Next = Array.Empty<int>();
        public bool InJunction;
        public float Kmh;
        // Gley's flags at the start (a simulated or replayed junction changes `Stop` as it runs).
        public bool Stop, GiveWay, Enter, Exit;
        public int Junction = -1, Road = -1;
        // Its index in Gley's waypoint list, for a dump from the game.
        public int Source = -1;
    }

    public sealed class ScenarioJunction
    {
        public int Id;
        // "lights": a cycle, one road green at a time, amber held while anyone is inside (Gley's
        // TrafficLightsIntersection); "priority": round robin by road, at most three inside (PriorityIntersection);
        // "replay": stop flags from Timelines.
        public string Kind = "lights";
        // Each road's entry waypoints (Gley's stopWaypoints or enterWaypoints).
        public List<int[]> Roads = new List<int[]>();
        public float Green = 12f, Yellow = 3f;
        public int FirstRoad;
        // Seconds of the first road's green already gone at the start.
        public float Offset;
    }

    public sealed class ScenarioArc
    {
        public int Entry;
        public float Radius;
        public List<Vec3> Points = new List<Vec3>();
    }

    public sealed class ScenarioRide
    {
        public int[] Path = Array.Empty<int>();
        public List<ScenarioArc> Arcs = new List<ScenarioArc>();
        public float Wheelbase = 2.8f, Lock = 35f, FrontAhead = 3.7f, RearBehind = 0.9f, HalfWidth = 0.92f, TopMs = 13.9f;
        // Rear axle, heading (radians, as atan2(dx, dz)), speed.
        public float X, Z, Heading, Speed;
        public List<(float From, float To, float Ms)> Slow = new List<(float, float, float)>();
    }

    public sealed class ScenarioCar
    {
        public int Id;
        public float Spawn;
        // Waypoints it drives through; past the last one it takes the first successor.
        public int[] Route = Array.Empty<int>();
        // Middle of its body, heading, speed; without a pose it starts at its route's first waypoint, heading to the second.
        public bool HasPose;
        public float X, Z, Heading, Speed;
        public float Length = 4.5f, Width = 1.9f, Trigger = 3f;
        // Its speed as a share of the road's limit.
        public float Pace = 1f;
    }

    public sealed class ScenarioTimeline
    {
        public int Waypoint;
        public List<float> At = new List<float>();
        public List<bool> Stop = new List<bool>();
    }

    public sealed class ScenarioFrame
    {
        public float T, RideX, RideZ, RideHeading, RideSpeed;
        public List<ScenarioCarState> Cars = new List<ScenarioCarState>();
    }

    public struct ScenarioCarState
    {
        public int Id, Target;
        public float X, Z, Heading, Vx, Vz;
        public bool ForUs;
        public string? Action;
    }
}
