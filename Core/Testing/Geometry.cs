using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    internal static class Geometry
    {
        public static bool Direction(Vec3 a, Vec3 b, out float dx, out float dz)
        {
            float length = Vec3.HorizontalDistance(a, b);
            dx = length > 0 ? (b.X - a.X) / length : 0;
            dz = length > 0 ? (b.Z - a.Z) / length : 0;
            return length > 0;
        }

        public static float Angle(float ax, float az, float bx, float bz) =>
            (float)(Math.Acos(Math.Max(-1, Math.Min(1, ax * bx + az * bz))) * 180 / Math.PI);

        public static void Heading(LaneGraph graph, IReadOnlyList<int> route, int index, out float dx, out float dz)
        {
            int w = route[index];
            if (graph.PlainHeading(w, out dx, out dz)) return;
            if (index + 1 < route.Count && Direction(graph.Position(w), graph.Position(route[index + 1]), out dx, out dz)) return;
            if (index > 0 && Direction(graph.Position(route[index - 1]), graph.Position(w), out dx, out dz)) return;
            dz = 1;
        }

        // A fork is safe only when one branch continues the segment we arrived on.
        public static int Next(LaneGraph graph, int current, bool backwards, float dx, float dz)
        {
            var choices = backwards ? graph.Predecessors(current) : graph.Successors(current);
            if (choices.Count == 1) return choices[0];
            int best = -1;
            float alignment = 0.95f;
            bool tied = false;
            foreach (int w in choices)
            {
                Vec3 a = graph.Position(backwards ? w : current), b = graph.Position(backwards ? current : w);
                if (!Direction(a, b, out float x, out float z)) continue;
                float dot = dx * x + dz * z;
                if (dot > alignment) { alignment = dot; best = w; tied = false; }
                else if (best >= 0 && Math.Abs(dot - alignment) < 0.00001f) tied = true;
            }
            return tied ? -1 : best;
        }
    }
}
