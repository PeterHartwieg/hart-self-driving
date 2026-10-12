using System;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    public readonly struct Gate
    {
        private readonly float halfWidth, maxDy;
        public Gate(string name, Vec3 point, float dirX, float dirZ, float halfWidth = 4f, float maxDy = 2f)
        {
            float length = (float)Math.Sqrt(dirX * dirX + dirZ * dirZ);
            if (!(length > 0) || float.IsInfinity(length)) throw new ArgumentException("A gate needs a direction.");
            Name = name;
            Point = point;
            DirX = dirX / length;
            DirZ = dirZ / length;
            this.halfWidth = halfWidth;
            this.maxDy = maxDy;
        }
        public string Name { get; }
        public Vec3 Point { get; }
        public float DirX { get; }
        public float DirZ { get; }
        public float Lateral(Vec3 p) => (p.X - Point.X) * DirZ - (p.Z - Point.Z) * DirX;
        internal float Along(Vec3 p) => (p.X - Point.X) * DirX + (p.Z - Point.Z) * DirZ;
        public bool Crossed(Vec3 from, Vec3 to) => Crossing(from, to, out _);

        internal bool Crossing(Vec3 from, Vec3 to, out float fraction)
        {
            float a = Along(from), b = Along(to);
            fraction = 0;
            if (a > 0 || b < 0 || b <= a) return false;
            fraction = -a / (b - a);
            Vec3 crossing = from + (to - from) * fraction;
            return Math.Abs(Lateral(crossing)) <= halfWidth && Math.Abs(crossing.Y - Point.Y) <= maxDy;
        }
    }
}
