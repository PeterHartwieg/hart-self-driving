using System.Collections.Generic;

namespace DrivenRides.Core.Drive
{

    public struct PathMark
    {
        public int Position, Waypoint;
        public float Along;
        public bool Stop, GiveWay, Enter, Exit;
        public int Junction, Road;
        public bool? Free;
    }

    public struct OtherCar
    {
        public int Id;
        public float X, Z, Heading, Vx, Vz;
        public float Length, Width;
        public float TriggerLength;
        public int Target;
        public bool StoppedForUs;
        // It follows a slower car or is overtaking (Gley's Follow and Overtake actions): it may change lanes into a
        // lane beside it at its next waypoint. Gley checks that lane only for its own cars' targets, so it doesn't
        // see the ride car there (WaypointManager.IsTargetFree; T3 20261013-211205, ride 18).
        public bool MayChangeLane;

        public float Speed => (float)System.Math.Sqrt(Vx * Vx + Vz * Vz);
    }

    public struct RideBody
    {
        public float Wheelbase, FrontAhead, RearBehind, HalfWidth;
        public float Length => FrontAhead + RearBehind;
    }

    public interface ILiveFlags
    {
        bool Stop(int waypoint);
        bool GiveWayEntry(int waypoint, out int junction, out int road);
    }

    public struct TrafficSnapshot
    {
        public float Time;
        public float RearX, RearZ, Heading, Speed, Wheel;
        public IReadOnlyList<PathMark> Marks;
        public IReadOnlyList<OtherCar> Cars;
        public ILiveFlags Flags;
        public float? GapBehind;
        public float? GapAhead;
        public float? PlannerHoldAlong;
    }

    public enum JunctionEventKind { Enter, Leave }

    public struct JunctionEvent
    {
        public JunctionEventKind Kind;
        public int Junction;
        public override string ToString() => $"{Kind} {Junction}";
    }

    public sealed class TrafficDecision
    {
        public float? StopAlong;
        public int StopPosition = -1;
        public bool StopIsRed;
        public float? YieldAlong;
        public int YieldFor = -1;
        public float YieldWay;
        // Where and when the conflict is, for the trace.
        // Kept as numbers and written out only when read (the adapter reads it when the reason changes): no string is
        // built each physics step (review round 1, O7).
        public string YieldNote => Note.ToString();
        public YieldNote Note;
        public float? GapAhead;
        public int GapFor = -1;
        public float GapSpeed;
        public float? MaxSpeed;
        public bool NoseIn;
        public int ApproachFor = -1;
        public float ApproachAlong;
        public int PassRounds;
        public bool Unsettled;
        public readonly List<int> EmergencyFor = new List<int>();
        public bool BackOff;
        public int BackOffFrom = -1;
        public readonly List<JunctionEvent> Events = new List<JunctionEvent>();
        public int BlockedBy = -1;
        public string BlockedWhy = "";
        public string Why = "";

        public void Clear()
        {
            StopAlong = YieldAlong = GapAhead = MaxSpeed = null;
            GapSpeed = 0f;
            StopPosition = YieldFor = GapFor = BackOffFrom = BlockedBy = -1;
            BlockedWhy = "";
            StopIsRed = BackOff = NoseIn = Unsettled = false;
            PassRounds = 0;
            ApproachFor = -1;
            Events.Clear();
            EmergencyFor.Clear();
            Why = "";
            Note = default;
        }
    }

    public enum YieldNoteKind { None, Conflict, Still, KeepClear, Approach }

    public struct YieldNote
    {
        public YieldNoteKind Kind;
        public int Pose, Car;
        public float On, From, To, Early, Late, Speed;
        public bool Same, HeadOn;

        public override string ToString() => Kind switch
        {
            YieldNoteKind.Conflict => $"pose {Pose} ({On:0} m on) u {From:0.0}..{To:0.0}, us {Early:0.0}..{Late:0.0} s, it {Speed:0.0} m/s{(Same ? " same way" : HeadOn ? " head-on" : "")}",
            YieldNoteKind.Still => "still giving way",
            YieldNoteKind.KeepClear => $"keeping clear of car {Car}'s way",
            YieldNoteKind.Approach => $"keeping the option to stop short of car {Car}'s way",
            _ => "",
        };
    }
}
