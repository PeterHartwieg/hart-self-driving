using System;

namespace DrivenRides.Core.Drive
{
    // A kinematic model of a car driven by its own physics (NWH): a bicycle about the rear axle, a wheel that follows
    // the asked-for angle smoothed over about 0.1 s and turned at most 180 degrees a second, held to the lock at the
    // present speed, and a speed that follows the driver's wish at up to 3 m/s² either way (pedals not modelled). Used
    // by the planner's feasibility check and the Core tests, and calibrated against the rig's -nwh.csv traces.
    public sealed class CarModel
    {
        public const float SmoothSeconds = 0.1f;
        public const float WheelRate = 180f;
        public const float Accel = 3f;

        private readonly float wheelbase;
        private readonly Func<float, float> lockAt;
        private double smoothed;

        // The rear axle and heading (radians, as atan2(dx, dz)), the wheel (degrees, positive to the right), the speed.
        public double X, Z, Heading, Steer, Speed;

        // lockAt: the lock in degrees at a speed in m/s (NWH: maximumSteerAngle × speedSensitiveSteeringCurve).
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

        // What the driver is told about the car this step.
        public DriveInput Input(float dt) => new DriveInput
        {
            RearX = (float)X, RearZ = (float)Z, Heading = (float)Heading, SpeedMs = (float)Speed, Dt = dt, WheelDegrees = (float)Steer,
        };

        // One physics step with the driver's output.
        public void Step(DriveOutput o, float dt)
        {
            var lockNow = lockAt((float)Math.Abs(Speed));
            var want = Math.Max(-lockNow, Math.Min(lockNow, o.SteerDegrees));
            smoothed += (want - smoothed) * Math.Min(1, dt / SmoothSeconds);
            Steer += Math.Max(-WheelRate * dt, Math.Min(WheelRate * dt, smoothed - Steer));
            Speed += Math.Max(-Accel * dt, Math.Min(Accel * dt, o.Desired - Speed));
            Heading += Speed * Math.Tan(Steer * Math.PI / 180) / wheelbase * dt;
            X += Speed * Math.Sin(Heading) * dt;
            Z += Speed * Math.Cos(Heading) * dt;
        }
    }
}
