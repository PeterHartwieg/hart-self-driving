using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    public struct Sample
    {
        public int Step;
        public double Time;
        public Vec3 Centre;
        public Vec3 FrontAxle;
        public float HeadingDegrees;
        public Vec3[] Footprint;
        public float SpeedMs;
        public bool Waiting;
        public float NearMiss;
    }

    public sealed class JudgeResult
    {
        public string Verdict = "";
        public List<string> Reasons = new List<string>();
        public List<string> GatesPassed = new List<string>();
        public List<string> Incidents = new List<string>();
        public float EntrySpeed, MaxIntrusion, NearMiss, PlainDeviationMax, LastGateLateral, LastGateHeadingError;
        public string? IntrusionDetail;
        // The farthest a corner on pedestrian ground went from every lane where a junction's box made it carriageway
        // (Pavement.CornerOnPavement): not a failure, the evidence for whether a box holds real pavement.
        public string? JunctionPavement;
        public float JunctionPavementMetres;

        public string ToJson() => new Json().Field("Verdict", Verdict).Field("Reasons", Reasons)
            .Field("GatesPassed", GatesPassed).Field("Incidents", Incidents).Field("EntrySpeed", EntrySpeed)
            .Field("MaxIntrusion", MaxIntrusion).Field("NearMiss", NearMiss).Field("PlainDeviationMax", PlainDeviationMax)
            .Field("LastGateLateral", LastGateLateral).Field("LastGateHeadingError", LastGateHeadingError)
            .Field("IntrusionDetail", IntrusionDetail).Field("JunctionPavement", JunctionPavement).ToString();
    }
}
