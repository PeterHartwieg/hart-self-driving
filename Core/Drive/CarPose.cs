namespace DrivenRides.Core.Drive
{
    public struct CarPose
    {
        public float X, Z, Heading, Speed, Wheel;
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
