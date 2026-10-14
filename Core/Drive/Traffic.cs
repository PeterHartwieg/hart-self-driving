using System.Collections.Generic;

namespace DrivenRides.Core.Drive
{
    // What the ride car's traffic decisions read each physics step (docs/implementation-plan.md, "NWH driving"): the
    // path's waypoints ahead with Gley's live flags, the Gley cars near it, and Gley's flags for any waypoint, so a
    // traffic car's way ahead can be cut where it will stop. Everything is a snapshot; the decisions in
    // JunctionRules and TrafficDriver are worked out from it alone, plus a little memory each keeps explicitly.

    // A waypoint of the ride's path, with Gley's live flags (Waypoint.stop, giveWay, enter, exit).
    public struct PathMark
    {
        // Its index in the path and its waypoint.
        public int Position, Waypoint;
        // Metres along the DriveLine (DriveLine.AlongOf).
        public float Along;
        public bool Stop, GiveWay, Enter, Exit;
        // The adapter's number for the waypoint's intersection (Gley's associatedIntersection), -1 for none, and the
        // road it enters by (its group in the intersection's enter or stop waypoints), -1 if unknown.
        public int Junction, Road;
        // Gley's answer to Waypoint.CanChange() for a give-way waypoint the rules asked about (JunctionRules.ShouldAsk),
        // null when not asked.
        public bool? Free;
    }

    // A Gley car near the ride car.
    public struct OtherCar
    {
        public int Id;
        // The middle of its body and its heading (radians, as atan2(dx, dz)), its velocity (m/s, world).
        public float X, Z, Heading, Vx, Vz;
        public float Length, Width;
        // Its front trigger reaches this far ahead of its front (VehicleComponent.frontTrigger's box).
        public float TriggerLength;
        // Its target waypoint (TrafficManager.GetTargetWaypoint), -1 if unknown.
        public int Target;
        // It has the ride car's collider in its obstacleList: it stands, or brakes, because of the ride car.
        public bool StoppedForUs;
        // It follows a slower car or is overtaking (Gley's Follow and Overtake actions): it may change lanes into a
        // lane beside it at its next waypoint. Gley checks that lane only for its own cars' targets, so it doesn't
        // see the ride car there (WaypointManager.IsTargetFree; T3 20261013-211205, ride 18).
        public bool MayChangeLane;

        public float Speed => (float)System.Math.Sqrt(Vx * Vx + Vz * Vz);
    }

    // The ride car's body about its rear axle.
    public struct RideBody
    {
        public float Wheelbase, FrontAhead, RearBehind, HalfWidth;
        public float Length => FrontAhead + RearBehind;
    }

    // Gley's live flags for any waypoint, for the corridors of traffic cars.
    public interface ILiveFlags
    {
        bool Stop(int waypoint);
        // A give-way waypoint at an intersection's entry (a priority junction), with the intersection and the road it
        // enters by (-1 if unknown).
        bool GiveWayEntry(int waypoint, out int junction, out int road);
    }

    public struct TrafficSnapshot
    {
        public float Time;
        // The ride car: rear axle, heading (radians, as atan2(dx, dz)), signed speed along its heading.
        public float RearX, RearZ, Heading, Speed;
        // The path's waypoints from a little behind the rear to the stop-line horizon ahead, in path order.
        public IReadOnlyList<PathMark> Marks;
        public IReadOnlyList<OtherCar> Cars;
        public ILiveFlags Flags;
        // Metres free behind the rear bumper (parked cars, people), or null when nothing is near.
        public float? GapBehind;
        // Metres free ahead of the front bumper on the car's way (parked cars, people: what the adapter's own cast
        // sees), or null. Committing past a traffic car's way counts on it (review round 2, U1).
        public float? GapAhead;
        // Metres along the line where the front must stop for the planner (a step ahead it must not enter:
        // WayOnControl.HoldAlong), or null; set before Decide, a hold found this step included. A firm stop like the
        // stop line: committing past a traffic car's way counts on it (review round 3, R7 and V1).
        public float? PlannerHoldAlong;
    }

    public enum JunctionEventKind { Enter, Leave }

    // The adapter calls IIntersection.VehicleEnter(-1) or VehicleLeft(-1) for each.
    public struct JunctionEvent
    {
        public JunctionEventKind Kind;
        public int Junction;
        public override string ToString() => $"{Kind} {Junction}";
    }

    public sealed class TrafficDecision
    {
        // Layer 1: where the front must stop (a red, or a give-way that isn't free).
        public float? StopAlong;
        public int StopPosition = -1;
        public bool StopIsRed;
        // Layer 2: where the front gives way to a moving car, and which.
        public float? YieldAlong;
        public int YieldFor = -1;
        // Where and when the conflict is, for the trace.
        // Kept as numbers and written out only when read (the adapter reads it when the reason changes): no string is
        // built each physics step (review round 1, O7).
        public string YieldNote => Note.ToString();
        public YieldNote Note;
        // A traffic car in the way: metres from the front bumper to where the body would touch it, and which.
        public float? GapAhead;
        public int GapFor = -1;
        // How fast that car moves on along the ride car's way (m/s, 0 when it stands or comes the other way).
        public float GapSpeed;
        // Layer 3: creeping past a car that stands for the ride car.
        public float? MaxSpeed;
        // Nosing in at a creep in front of a crawling car that will stop for the ride car in its trigger.
        public bool NoseIn;
        // Gone on past a car's way only because stopping there would be hit and going on clears it by time (the
        // emergency rule, TrafficDriver.GoOn), and the car it was for; counted in T0 and T3 (review round 3, V2).
        public bool ForcedCommit;
        public int ForcedFor = -1;
        // The least time to spare that forced commit was judged with, seconds (review round 4, X4).
        public float ForcedMargin;
        // Gone on past a car's way committed to a moment ago with less than the headway to spare (no headway at all),
        // the car, and the time to spare (review round 5, Y1).
        public bool MarginalCommit;
        public int MarginalFor = -1;
        public float MarginalMargin;
        // The car whose way the front is to keep the option to stop short of (TrafficDriver.Approaches), or -1, and
        // where the front is to be able to stop.
        public int ApproachFor = -1;
        public float ApproachAlong;
        // The final pass (TrafficDriver): its rounds, and whether its last round still tightened the stops (then the
        // statistics above were judged once more on the trajectory as it ended; review round 7, BB4).
        public int PassRounds;
        public bool Unsettled;
        // The cars a stop was given for by the emergency rule (a stop the car can no longer make short of the car's
        // way, judged safe to stand in: TrafficDriver.GoOn, ApproachCautious; review round 8, CC3).
        public readonly List<int> EmergencyFor = new List<int>();
        // Layer 3: backing off along the line, away from a car standing nose to nose with the ride car.
        public bool BackOff;
        public int BackOffFrom = -1;
        public readonly List<JunctionEvent> Events = new List<JunctionEvent>();
        // A traffic car in the way that won't move (TrafficDriver.Stuck), or -1; the way is blocked.
        public int BlockedBy = -1;
        public string BlockedWhy = "";
        // One short line for the trace and the log.
        public string Why = "";

        public void Clear()
        {
            StopAlong = YieldAlong = GapAhead = MaxSpeed = null;
            GapSpeed = 0f;
            StopPosition = YieldFor = GapFor = BackOffFrom = BlockedBy = -1;
            BlockedWhy = "";
            StopIsRed = BackOff = NoseIn = ForcedCommit = MarginalCommit = Unsettled = false;
            PassRounds = 0;
            ForcedFor = ApproachFor = MarginalFor = -1;
            ForcedMargin = MarginalMargin = 0f;
            Events.Clear();
            EmergencyFor.Clear();
            Why = "";
            Note = default;
        }
    }

    public enum YieldNoteKind { None, Conflict, Still, KeepClear, Approach }

    // Where and when the conflict a yield is for lies, for the log and the trace.
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
