using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    public enum CaseKind { Turn, UTurn, LaneChange, Ramp, Ride, Control }

    public sealed class Fixture
    {
        public string Id = "";
        public CaseKind Kind;
        public CaseKind Manoeuvre;
        public List<int> Route = new List<int>();
        public List<int>? Path;
        public List<int>? SlowPath;
        public int EntryIndex;
        public int ExitIndex;
        public int TurnGateWaypoint = -1;
        public float SharpnessDegrees;
        public string? PairId;
        public bool IsMutation;
        public string? ExpectedFailure;
        public int RideLine = -1;

        public string ToJson() => new Json().Field("Id", Id).Field("Kind", Kind.ToString())
            .Field("Manoeuvre", Manoeuvre.ToString()).Field("Route", Route).Field("Path", Path)
            .Field("SlowPath", SlowPath).Field("EntryIndex", EntryIndex).Field("ExitIndex", ExitIndex)
            .Field("TurnGateWaypoint", TurnGateWaypoint).Field("SharpnessDegrees", SharpnessDegrees)
            .Field("PairId", PairId).Field("IsMutation", IsMutation).Field("ExpectedFailure", ExpectedFailure)
            .Field("RideLine", RideLine).ToString();
    }

    public sealed class CatalogueOptions
    {
        // The car reaches its top speed in about 40 m (Ride.AccelerationSeconds), so 50 m of run-up is enough.
        public float RunUpMetres = 50f, RunOutMetres = 40f;
        // Plain lane changes kept: one in this many (1 keeps all).
        public int LaneChangeSample = 8;
        public Func<Vec3, bool>? NearBridge;
    }
}
