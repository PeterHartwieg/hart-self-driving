namespace DrivenRides.Core.Drive
{
    // Where a car stands and what it is doing: its rear axle, its heading (radians, as atan2(dx, dz)), its speed along
    // the heading (m/s, negative backwards) and its wheel angle (degrees, positive to the right). The planner joins a
    // new line from it (Feasibility.Join).
    public struct CarPose
    {
        public float X, Z, Heading, Speed, Wheel;

        public CarPose(float x, float z, float heading, float speed = 0f, float wheel = 0f)
        {
            X = x; Z = z; Heading = heading; Speed = speed; Wheel = wheel;
        }
    }
}
