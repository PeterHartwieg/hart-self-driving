using System;
using System.Collections.Generic;
using System.Globalization;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    public sealed class CarSpec
    {
        public string Name { get; }
        public float Wheelbase { get; }
        public Func<float, float> LockAt { get; }
        public Vec3 BodyCentre { get; }
        public Vec3 BodyHalf { get; }
        public float TopKmh { get; }
        public float TrackWidth { get; }

        public float WheelRate { get; }

        public CarSpec(string name, float wheelbase, Func<float, float> lockAt, Vec3 bodyCentre, Vec3 bodyHalf, float topKmh, float trackWidth,
            float wheelRate = CarModel.WheelRate)
        {
            WheelRate = wheelRate > 0f ? wheelRate : CarModel.WheelRate;
            TrackWidth = Math.Max(0f, trackWidth);
            Name = name;
            Wheelbase = Math.Max(1f, wheelbase);
            LockAt = lockAt;
            BodyCentre = bodyCentre;
            BodyHalf = bodyHalf;
            TopKmh = topKmh > 0f ? topKmh : 50f;
        }

        public float FrontAhead => BodyCentre.Z + BodyHalf.Z;
        public float Length => BodyHalf.Z * 2f;
        public bool Long => Length >= 5.5f;

        public float YawGain => CarModel.YawGain(Wheelbase, TrackWidth);

        public float RadiusAt(float speedMs) => (float)(1 / (YawFactor * CarModel.Curvature(Math.Max(5f, LockAt(speedMs)), Wheelbase, TrackWidth)));

        public const float DefaultYawFactor = 0.97f;
        public float YawFactor { get; set; } = DefaultYawFactor;

        public CarModel Model(double x, double z, double heading, double speed = 0) => new CarModel(Wheelbase, LockAt, x, z, heading, speed) { TrackWidth = TrackWidth, Rate = WheelRate, YawFactor = YawFactor };

        // Keep the smallest candidate arc at the established 4 m/s² reference pace. Raising the driver's preferred
        // corner speed must not make a previously drivable turn disappear: NWH narrows its steering lock at speed.
        // DrivePlan caps each selected arc at the speed that leaves RadiusSlack in reserve, so tight arcs can still
        // be taken slower while larger ones use PursuitDriver.Lateral. The 6 m/s² experiment otherwise changed the
        // Dash's minimum circle from 9.48 to 11.95 m and replaced an accepted arc with slower vias.
        private const float ArcReferenceLateral = 4f;
        public float ArcMinRadius()
        {
            var r = TurnArc.MinDriveRadius;
            for (var k = 0; k < 20; k++)
            {
                var need = Math.Max(TurnArc.MinDriveRadius, RadiusAt(CornerSpeed(r)) * TurnArc.RadiusSlack);
                if (need <= r + 0.01f) break;
                r = need;
            }
            return RadiusAt(CornerSpeed(r));
        }

        private float CornerSpeed(float radius) =>
            Math.Min(TopKmh / 3.6f, Math.Max(PursuitDriver.MinCornerMs, (float)Math.Sqrt(ArcReferenceLateral * radius)));

        public static Func<float, float> LockCurve(IReadOnlyList<(float Kmh, float Degrees)> points) => speedMs =>
        {
            var kmh = Math.Abs(speedMs) * 3.6f;
            if (kmh <= points[0].Kmh) return points[0].Degrees;
            for (var i = 1; i < points.Count; i++)
                if (kmh <= points[i].Kmh)
                {
                    var f = (kmh - points[i - 1].Kmh) / (points[i].Kmh - points[i - 1].Kmh);
                    return points[i - 1].Degrees + (points[i].Degrees - points[i - 1].Degrees) * f;
                }
            return points[points.Count - 1].Degrees;
        };

        public static CarSpec Parse(string text)
        {
            var parts = text.Split(';');
            if (parts.Length < 7) throw new FormatException("a car is name;wheelbase;top;cx,cy,cz;hx,hy,hz;kmh=deg,...;track");
            float F(string v) => float.Parse(v.Trim(), CultureInfo.InvariantCulture);
            Vec3 V(string v) { var c = v.Split(','); return new Vec3(F(c[0]), F(c[1]), F(c[2])); }
            var points = new List<(float, float)>();
            foreach (var p in parts[5].Split(','))
            {
                var kv = p.Split('=');
                points.Add((F(kv[0]), F(kv[1])));
            }
            points.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return new CarSpec(parts[0], F(parts[1]), LockCurve(points), V(parts[3]), V(parts[4]), F(parts[2]), F(parts[6]), parts.Length > 7 ? F(parts[7]) : CarModel.WheelRate);
        }

        public override string ToString() => FormattableString.Invariant(
            $"{Name}: wheelbase {Wheelbase:0.00} m, lock {LockAt(0f):0.0} ({LockAt(10f / 3.6f):0.0} at 10 km/h, {LockAt(25f / 3.6f):0.0} at 25), front {FrontAhead:0.00} m ahead of the rear axle, body {BodyHalf.X * 2:0.00} x {BodyHalf.Z * 2:0.00} m, track {TrackWidth:0.00} m (turns {YawGain:0.00} as sharply as a bicycle), arcs for a circle of {ArcMinRadius():0.00} m");
    }
}
