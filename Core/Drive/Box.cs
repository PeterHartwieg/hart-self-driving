using System;

namespace DrivenRides.Core.Drive
{
    public readonly struct Box
    {
        public readonly float X, Z, Sin, Cos, HalfWidth, HalfLength;

        public Box(float x, float z, float heading, float halfWidth, float halfLength)
        {
            X = x;
            Z = z;
            Sin = (float)Math.Sin(heading);
            Cos = (float)Math.Cos(heading);
            HalfWidth = halfWidth;
            HalfLength = halfLength;
        }

        private Box(float x, float z, float sin, float cos, float halfWidth, float halfLength)
        {
            X = x; Z = z; Sin = sin; Cos = cos; HalfWidth = halfWidth; HalfLength = halfLength;
        }

        public static Box Along(float ax, float az, float bx, float bz, float halfWidth)
        {
            float dx = bx - ax, dz = bz - az;
            var length = (float)Math.Sqrt(dx * dx + dz * dz);
            var sin = length > 1e-4f ? dx / length : 0f;
            var cos = length > 1e-4f ? dz / length : 1f;
            return new Box((ax + bx) / 2f, (az + bz) / 2f, sin, cos, halfWidth, length / 2f);
        }

        public Box Grown(float sideways, float lengthways) => new Box(X, Z, Sin, Cos, HalfWidth + sideways, HalfLength + lengthways);

        public float ExtentX => Math.Abs(Sin) * HalfLength + Math.Abs(Cos) * HalfWidth;
        public float ExtentZ => Math.Abs(Cos) * HalfLength + Math.Abs(Sin) * HalfWidth;

        public bool BoundsOverlap(in Box o) => Math.Abs(X - o.X) <= ExtentX + o.ExtentX && Math.Abs(Z - o.Z) <= ExtentZ + o.ExtentZ;

        public bool Overlaps(in Box o)
        {
            if (!BoundsOverlap(o)) return false;
            float dx = o.X - X, dz = o.Z - Z;
            return !Separated(dx, dz, Sin, Cos, o) && !Separated(dx, dz, Cos, -Sin, o)
                && !Separated(dx, dz, o.Sin, o.Cos, o) && !Separated(dx, dz, o.Cos, -o.Sin, o);
        }

        private bool Separated(float dx, float dz, float ax, float az, in Box o)
        {
            var distance = Math.Abs(dx * ax + dz * az);
            return distance > RadiusOn(ax, az) + o.RadiusOn(ax, az);
        }

        public float RadiusOn(float ax, float az) => HalfLength * Math.Abs(Sin * ax + Cos * az) + HalfWidth * Math.Abs(Cos * ax - Sin * az);

        public (float From, float To) SpanOf(in Box o)
        {
            var middle = (o.X - X) * Sin + (o.Z - Z) * Cos;
            var r = o.RadiusOn(Sin, Cos);
            var from = Math.Max(-HalfLength, middle - r) + HalfLength;
            var to = Math.Min(HalfLength, middle + r) + HalfLength;
            return (from, to);
        }
    }
}
