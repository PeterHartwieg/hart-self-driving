using System;

namespace DrivenRides.Core.Drive
{
    // A kinematic model of a car driven by its own physics (NWH): a bicycle about the rear axle, a wheel that follows
    // the asked-for angle smoothed over about 0.1 s and turned at most 180 degrees a second, held to the lock at the
    // present speed, and a speed that follows the driver's wish at up to 3 m/s² faster and 6 m/s² slower (pedals not
    // modelled; a car brakes much harder than it speeds up, and the driver's amber rule counts on 5 m/s²). Used
    // by the planner's feasibility check and the Core tests, and calibrated against the rig's -nwh.csv traces.
    public sealed class CarModel
    {
        public const float SmoothSeconds = 0.1f;
        public const float WheelRate = 180f;
        public const float Accel = 3f;
        public const float Brake = 6f;

        private readonly float wheelbase;
        private readonly Func<float, float> lockAt;
        public float Smooth = SmoothSeconds, Rate = WheelRate;
        public float TrackWidth;
        public float YawFactor = 1f;
        private double smoothed;

        public double X, Z, Heading, Steer, Speed;

        public CarModel(float wheelbase, Func<float, float> lockAt, double x, double z, double heading, double speed = 0)
        {
            this.wheelbase = Math.Max(1f, wheelbase);
            this.lockAt = lockAt;
            X = x;
            Z = z;
            Heading = heading;
            Speed = speed;
        }

        public CarModel(float wheelbase, float lockDegrees, double x, double z, double heading, double speed = 0)
            : this(wheelbase, _ => lockDegrees, x, z, heading, speed) { }

        public float Wheelbase => wheelbase;

        public void SetWheel(double degrees) { Steer = degrees; smoothed = degrees; }

        public DriveInput Input(float dt) => new DriveInput
        {
            RearX = (float)X, RearZ = (float)Z, Heading = (float)Heading, SpeedMs = (float)Speed, Dt = dt, WheelDegrees = (float)Steer,
        };

        public static double Curvature(double steerDegrees, float wheelbase, float trackWidth)
        {
            var t = Math.Tan(steerDegrees * Math.PI / 180);
            if (trackWidth <= 0f) return t / wheelbase;
            var a = trackWidth * t / (2 * wheelbase);
            return 2 * trackWidth * t / (wheelbase * wheelbase) / Math.Max(0.1, 1 - a * a);
        }

        public static float YawGain(float wheelbase, float trackWidth) => trackWidth > 0f ? 2f * trackWidth / wheelbase : 1f;

        public void Step(DriveOutput o, float dt)
        {
            var lockNow = lockAt((float)Math.Abs(Speed));
            var want = Math.Max(-lockNow, Math.Min(lockNow, o.SteerDegrees));
            smoothed += (want - smoothed) * (Smooth > 0f ? Math.Min(1, dt / Smooth) : 1);
            Steer += Math.Max(-Rate * dt, Math.Min(Rate * dt, smoothed - Steer));
            var change = o.Desired - Speed;
            var slowing = Speed > 0 ? change < 0 : Speed < 0 ? change > 0 : false;
            var limit = (slowing ? Brake : Accel) * dt;
            Speed += Math.Max(-limit, Math.Min(limit, change));
            Heading += YawFactor * Speed * Curvature(Steer, wheelbase, TrackWidth) * dt;
            X += Speed * Math.Sin(Heading) * dt;
            Z += Speed * Math.Cos(Heading) * dt;
        }
    }
}
