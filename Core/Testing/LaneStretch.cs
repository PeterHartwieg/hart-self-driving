using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    // One straight piece of a route's lane, with the boundaries a car on it keeps to: the midline to the nearest
    // oncoming lane on either side (Left, Right; infinite where there is none) and the outermost lanes of its own
    // direction (OwnLeft, OwnRight). The judge's intrusion check and the feasibility check's carriageway use it.
    internal sealed class LaneStretch
    {
        private readonly Vec3 start, end;
        private readonly float dx, dz;
        public readonly int Index;
        public readonly float Length;
        public float Min, Max;
        public float Left = float.NegativeInfinity, Right = float.PositiveInfinity;
        public readonly float OwnLeft, OwnRight;

        public LaneStretch(LaneGraph graph, Vec3 start, Vec3 end, int index)
        {
            this.start = start;
            this.end = end;
            Index = index;
            Geometry.Direction(start, end, out dx, out dz);
            Length = Vec3.HorizontalDistance(start, end);
            Max = Length;
            float left = float.NegativeInfinity, right = float.PositiveInfinity;
            var same = new List<float> { 0 };
            foreach (int w in graph.Near(start, 12f))
            {
                Vec3 p = graph.Position(w);
                if (Math.Abs(p.Y - start.Y) > 1f || !graph.PlainHeading(w, out float x, out float z)) continue;
                float dot = dx * x + dz * z, side = Side(p);
                if (dot > 0.5f) same.Add(side);
                // An oncoming waypoint less than 2.5 m to the side is no lane beside: the game reuses a lane both
                // ways in places, and a Hamptons lane road is too narrow for any car to keep to its half.
                else if (dot < -0.5f && Math.Abs(side) >= 2.5f)
                {
                    if (side < 0) left = Math.Max(left, side);
                    else if (side > 0) right = Math.Min(right, side);
                }
            }
            float ownLeft = 0, ownRight = 0;
            foreach (float side in same)
            {
                if (side < ownLeft && side > left) ownLeft = side;
                if (side > ownRight && side < right) ownRight = side;
            }
            OwnLeft = ownLeft;
            OwnRight = ownRight;
            if (!float.IsNegativeInfinity(left)) Left = (left + ownLeft) / 2;
            if (!float.IsPositiveInfinity(right)) Right = (right + ownRight) / 2;
        }

        public float Along(Vec3 p) => (p.X - start.X) * dx + (p.Z - start.Z) * dz;
        public float Side(Vec3 p) => (p.X - start.X) * dz - (p.Z - start.Z) * dx;
        public float Height(float along) => Length == 0 ? start.Y : start.Y + (end.Y - start.Y) * along / Length;
    }
}
