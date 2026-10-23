using System;
using System.Collections.Generic;
using DrivenRides.Core.Drive;

namespace DrivenRides.Core.Routing
{
    public readonly struct DepartureEntry
    {
        public readonly int From, To;
        public readonly Vec3 Position;
        public readonly float Heading, Distance;
        public DepartureEntry(int from, int to, Vec3 position, float heading, float distance)
        { From = from; To = to; Position = position; Heading = heading; Distance = distance; }
    }

    // A parked heading is not a road direction. Project onto directed road segments, including long ones,
    // then check each proposed entry with the separate local planner before moving the car.
    public static class DepartureEntryPicker
    {
        public static List<DepartureEntry> Pick(LaneGraph graph, Vec3 from, CarSpec car, int vehicleType,
            Func<int, bool>? disabled = null, Func<int, int, bool>? blocked = null, float radius = 90f)
        {
            var candidates = new List<DepartureEntry>();
            for (var a = 0; a < graph.Count; a++)
            {
                if (graph.IsIntersection(a) || !graph.Allows(a, vehicleType) || disabled?.Invoke(a) == true) continue;
                var p = graph.Position(a);
                if (Math.Abs(p.Y - from.Y) > 1.5f) continue;
                foreach (var b in graph.Successors(a))
                {
                    if (!graph.Allows(b, vehicleType) || disabled?.Invoke(b) == true || blocked?.Invoke(a, b) == true) continue;
                    var q = graph.Position(b);
                    if (Math.Abs(q.Y - from.Y) > 1.5f) continue;
                    var length = Vec3.HorizontalDistance(p, q);
                    if (length < 1) continue;
                    var dx = (q.X - p.X) / length; var dz = (q.Z - p.Z) / length;
                    var along = Math.Max(0, Math.Min(length, (from.X - p.X) * dx + (from.Z - p.Z) * dz));
                    foreach (var advance in new[] { 0f, 6f, 12f, 24f, 36f, 48f })
                    {
                        var at = Math.Min(length - (graph.IsIntersection(b) ? car.FrontAhead + 3 : 0.5f), along + advance);
                        if (at < 0.5f) continue;
                        var pos = new Vec3(p.X + dx * at, p.Y + (q.Y - p.Y) * at / length, p.Z + dz * at);
                        var distance = Vec3.HorizontalDistance(pos, from);
                        if (distance > radius) continue;
                        var heading = (float)Math.Atan2(dx, dz);
                        var duplicate = false;
                        foreach (var entry in candidates)
                            if (Math.Abs(entry.Heading - heading) < 0.05f && Vec3.HorizontalDistance(entry.Position, pos) < 3f)
                            { duplicate = true; break; }
                        if (!duplicate) candidates.Add(new DepartureEntry(a, b, pos, heading, distance));
                    }
                }
            }
            candidates.Sort((a, b) => { var order = a.Distance.CompareTo(b.Distance); return order != 0 ? order : a.From.CompareTo(b.From); });
            return candidates;
        }
    }
}
