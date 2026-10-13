using System;
using System.Collections.Generic;
using System.Globalization;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // What the feasibility check needs of a car driven by its own physics, measured from its drivable prefab (the game
    // side reads it from the prefab asset, NwhShape): the wheelbase, the lock at a speed, and the body box. Lengths are
    // from the rear axle on the ground: x to the right, y up, z forward.
    public sealed class CarSpec
    {
        public string Name { get; }
        public float Wheelbase { get; }
        // The lock in degrees at a speed in m/s (NWH: maximumSteerAngle x speedSensitiveSteeringCurve at speed / 50).
        public Func<float, float> LockAt { get; }
        public Vec3 BodyCentre { get; }
        public Vec3 BodyHalf { get; }
        // The vehicle type's top speed, km/h.
        public float TopKmh { get; }
        // The front wheels' track with NWH's Ackermann, 0 without (CarModel.TrackWidth).
        public float TrackWidth { get; }

        // How fast the wheel turns, degrees a second (NWH's degreesPerSecondLimit: 140 on the cars measured so far).
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

        // The front bumper, metres ahead of the rear axle.
        public float FrontAhead => BodyCentre.Z + BodyHalf.Z;
        public float Length => BodyHalf.Z * 2f;
        // A long car takes U-turns at a crawl (the rig: 15 km/h from 5.5 m; at 25 a 6.7 m car went wide into the kerb).
        public bool Long => Length >= 5.5f;

        // How sharply the car turns against a bicycle with its wheelbase (NWH's Ackermann).
        public float YawGain => CarModel.YawGain(Wheelbase, TrackWidth);

        // The car's tightest circle at a speed, measured at the rear axle.
        public float RadiusAt(float speedMs) => (float)(1 / (YawFactor * CarModel.Curvature(Math.Max(5f, LockAt(speedMs)), Wheelbase, TrackWidth)));

        // How sharply the car turns against the model at the same wheel angle (CarModel.YawFactor): 0.97 replays the
        // T2 runs of the limo, the Dash and the taxi best (U-turn exits within 0.21 to 0.39 m, every run within 0.41 m;
        // tools/calibrate with YAW=).
        public const float DefaultYawFactor = 0.97f;
        public float YawFactor { get; set; } = DefaultYawFactor;

        // The model of this car at a pose.
        public CarModel Model(double x, double z, double heading, double speed = 0) => new CarModel(Wheelbase, LockAt, x, z, heading, speed) { TrackWidth = TrackWidth, Rate = WheelRate, YawFactor = YawFactor };

        // The car's circle at the speed it takes the tightest arc a junction may get for it: TurnArc.Fit makes an arc
        // RadiusSlack wider than this (and never under MinDriveRadius), and the driver takes an arc at its corner speed
        // (PursuitDriver.Lateral), where NWH has already narrowed the lock. The spike's rig used the lock at 10 km/h,
        // and the limo met a 6.5 m arc near full lock at 17 km/h (turn-4644-8640).
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
            Math.Min(TopKmh / 3.6f, Math.Max(PursuitDriver.MinCornerMs, (float)Math.Sqrt(PursuitDriver.Lateral * radius)));

        // A lock curve through measured points (km/h, degrees), straight between them and flat past the ends.
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

        // For the tools: "name;wheelbase;top km/h;body centre x,y,z;body half x,y,z;km/h=lock,km/h=lock,...;track width[;wheel rate]",
        // lengths from the rear axle, as the game log's "NWH shape" line gives them.
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
