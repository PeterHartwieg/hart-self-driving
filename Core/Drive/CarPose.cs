namespace DrivenRides.Core.Drive
{
    // Where a car stands and what it is doing: its rear axle, its heading (radians, as atan2(dx, dz)), its speed along
    // the heading (m/s, negative backwards) and its wheel angle (degrees, positive to the right). The planner joins a
    // new line from it (Feasibility.Join).
    public struct CarPose
    {
        public float X, Z, Heading, Speed, Wheel;
        // What the driver is braking for, as it last drove (DriveInput's, on the line it drives): where the front must
        // stop (a red, the planner's hold) or give way, what stands ahead and how fast it moves on, a speed cap. Where
        // the car will be is foreseen with them (Feasibility.Predict; review round 5).
        public float? StopAlong, YieldAlong, GapAhead, MaxSpeed;
        public float GapSpeed;

        public CarPose(float x, float z, float heading, float speed = 0f, float wheel = 0f)
        {
            X = x; Z = z; Heading = heading; Speed = speed; Wheel = wheel;
            StopAlong = YieldAlong = GapAhead = MaxSpeed = null;
            GapSpeed = 0f;
        }
    }
}
