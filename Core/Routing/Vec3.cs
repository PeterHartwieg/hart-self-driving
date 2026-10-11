using System;

namespace DrivenRides.Core.Routing
{
    public readonly struct Vec3
    {
        public readonly float X, Y, Z;

        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static float Distance(Vec3 a, Vec3 b)
        {
            double x = (double)a.X - b.X, y = (double)a.Y - b.Y, z = (double)a.Z - b.Z;
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }

        public static float HorizontalDistance(Vec3 a, Vec3 b)
        {
            double x = (double)a.X - b.X, z = (double)a.Z - b.Z;
            return (float)Math.Sqrt(x * x + z * z);
        }

        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator *(Vec3 a, float scalar) => new Vec3(a.X * scalar, a.Y * scalar, a.Z * scalar);
        public static Vec3 operator *(float scalar, Vec3 a) => a * scalar;
    }
}
