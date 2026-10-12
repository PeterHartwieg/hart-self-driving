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
    }
}
