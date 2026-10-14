using System;
using System.Collections.Generic;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Testing
{
    public sealed class CaseGeometry
    {
        private CaseGeometry(List<Gate> gates, Gate? turnGate, List<Vec3> route, List<Vec3> expanded)
        {
            Gates = gates.AsReadOnly();
            TurnGate = turnGate;
            RoutePolyline = route.AsReadOnly();
            ExpandedPolyline = expanded.AsReadOnly();
        }
        public IReadOnlyList<Gate> Gates { get; }
        public Gate? TurnGate { get; }
        public IReadOnlyList<Vec3> RoutePolyline { get; }
        public IReadOnlyList<Vec3> ExpandedPolyline { get; }

        public static CaseGeometry Build(LaneGraph graph, Fixture fixture)
        {
            if (fixture.Route.Count < 2) throw new ArgumentException("A fixture needs two waypoints.", nameof(fixture));
            var route = new List<Vec3>();
            foreach (int w in fixture.Route) route.Add(graph.Position(w));
            var expanded = new List<Vec3>();
            foreach (int w in PathExpander.Expand(graph, fixture.Route).Path) expanded.Add(graph.Position(w));
            Gate At(string name, int index)
            {
                Geometry.Heading(graph, fixture.Route, index, out float dx, out float dz);
                return new Gate(name, route[index], dx, dz);
            }
            // A case that ends at its exit (a ride: the route's last waypoint) has no run-out to put the last gate in;
            // its exit and last gates go 16 m and 8 m before the route's end, where the car arrives in its lane and
            // stops (the NWH driver stops its front 1.5 m short of the end). With the exit gate on the last waypoint
            // and the last one before it, a clean NWH arrival failed missed-gate:exit (T3, session 20261013-191420).
            if (fixture.ExitIndex >= route.Count - 1 && fixture.Manoeuvre == CaseKind.Ride)
            {
                var total = 0f;
                for (int k = 1; k < route.Count; k++) total += Vec3.HorizontalDistance(route[k - 1], route[k]);
                // The ride is judged on to its arrival (Judge.Arrive) against the line's direction where the car
                // stops: the gates only mark the way there, so a bend at the end is driven and judged too (review
                // round 1; T3 20261013-211205 failed rides 9 to 18 where a gate square to a chord of a terminal bend met
                // the car at 33 degrees).
                if (total > 24f)
                    return new CaseGeometry(new List<Gate> { At("entry", fixture.EntryIndex), GateAt("exit", route, total - 16f), GateAt("last", route, total - 8f) },
                        null, route, expanded);
            }
            var gates = new List<Gate> { At("entry", fixture.EntryIndex), At("exit", fixture.ExitIndex) };
            Gate last = At("last", route.Count - 2);
            float length = 0;
            for (int k = fixture.ExitIndex; k + 1 < route.Count; k++)
            {
                float segment = Vec3.HorizontalDistance(route[k], route[k + 1]);
                if (segment > 0 && length + segment >= 30f)
                {
                    Vec3 point = route[k] + (route[k + 1] - route[k]) * ((30f - length) / segment);
                    Geometry.Direction(route[k], route[k + 1], out float dx, out float dz);
                    last = new Gate("last", point, dx, dz);
                    break;
                }
                length += segment;
            }
            gates.Add(last);
            Gate? turnGate = null;
            if (fixture.Manoeuvre == CaseKind.UTurn && fixture.TurnGateWaypoint >= 0)
            {
                Geometry.Heading(graph, fixture.Route, fixture.EntryIndex, out float dx, out float dz);
                turnGate = new Gate("turn", graph.Position(fixture.TurnGateWaypoint), dx, dz);
            }
            return new CaseGeometry(gates, turnGate, route, expanded);
        }

        // A gate across the route this far along it, square to its segment there.
        private static Gate GateAt(string name, List<Vec3> route, float along)
        {
            float length = 0;
            for (int k = 0; k + 1 < route.Count; k++)
            {
                float segment = Vec3.HorizontalDistance(route[k], route[k + 1]);
                if (segment > 0 && length + segment >= along)
                {
                    Vec3 point = route[k] + (route[k + 1] - route[k]) * ((along - length) / segment);
                    Geometry.Direction(route[k], route[k + 1], out float dx, out float dz);
                    return new Gate(name, point, dx, dz);
                }
                length += segment;
            }
            Geometry.Direction(route[route.Count - 2], route[route.Count - 1], out float ex, out float ez);
            return new Gate(name, route[route.Count - 1], ex, ez);
        }
    }
}
