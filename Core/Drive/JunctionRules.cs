using System;
using System.Collections.Generic;

namespace DrivenRides.Core.Drive
{
    // Layer 1, rights of way from Gley's own data (docs/implementation-plan.md, "The four layers"):
    // - a waypoint with `stop` set (a red or amber light) is a stop line, unless it was first seen too late to stop
    //   at HardDecel, which is driven through as on amber (NWH spike);
    // - a give-way waypoint at an intersection's entry (Gley's priority junctions, round robin by road) is a stop line
    //   until Waypoint.CanChange() says the way is free; the adapter asks only within AskMetres, so the car slows
    //   for it first, as Gley's own cars do;
    // - membership: the car is counted in an intersection (IIntersection.VehicleEnter(-1)) when its front passes an
    //   `enter` waypoint, and leaves it (VehicleLeft(-1)) when its rear passes an `exit` waypoint, after InsideLimit
    //   seconds at the latest, and on every exit of the drive (LeaveAll). A light holds amber while anyone is
    //   inside (TrafficLightsIntersection.UpdateIntersection), and a priority junction counts the car.
    public sealed class JunctionRules
    {
        public const float InsideLimit = 20f;
        public const float AskMetres = 6f;
        // The front stops this far short of a stop line's waypoint: the pedals don't stop it to the centimetre.
        public const float ShortOfLine = 1f;

        private readonly HashSet<int> drivenThrough = new HashSet<int>();
        // A red's decision, taken once when the car first sees it set: stop (false) or drive through as on amber
        // (true). Re-deciding each step dropped a red the car was braking for: a metre short of the line at 1 m/s
        // it "needed" more than HardDecel (T3 20261013-200946-0017: the front passed the line on red).
        private readonly Dictionary<int, bool> redDecision = new Dictionary<int, bool>();
        private readonly HashSet<int> entered = new HashSet<int>();
        private readonly HashSet<int> left = new HashSet<int>();
        private readonly Dictionary<int, float> inside = new Dictionary<int, float>();
        // The road the car holds at a junction: the one it entered by, or the one Gley last said was free for it.
        private readonly Dictionary<int, int> roadHeld = new Dictionary<int, int>();

        public IReadOnlyDictionary<int, float> Inside => inside;

        // Whether the adapter should ask Gley about this mark now (Waypoint.CanChange()). Asking has an effect: a
        // priority junction counts it as a request for the car's road, as it does for a waiting Gley car.
        public bool ShouldAsk(in PathMark m, float front) =>
            m.GiveWay && m.Enter && m.Junction >= 0 && !entered.Contains(m.Position) && m.Along >= front - 0.5f && m.Along - front <= AskMetres;

        // The road this car holds at a junction, or -1. Other roads' cars stop at their give-way line meanwhile.
        public int RoadHeld(int junction) => roadHeld.TryGetValue(junction, out var road) ? road : -1;

        public void Step(IReadOnlyList<PathMark> marks, float front, float rear, float speed, float time, TrafficDecision d)
        {
            for (var k = 0; k < marks.Count; k++)
            {
                var m = marks[k];
                if (m.Junction < 0) continue;
                if (m.Enter && m.Along <= front && entered.Add(m.Position) && !inside.ContainsKey(m.Junction))
                {
                    inside[m.Junction] = time;
                    if (m.Road >= 0) roadHeld[m.Junction] = m.Road;
                    d.Events.Add(new JunctionEvent { Kind = JunctionEventKind.Enter, Junction = m.Junction });
                }
                if (m.Exit && m.Along <= rear && left.Add(m.Position)) Leave(m.Junction, d.Events);
            }
            // At most InsideLimit seconds, so a junction can't stick on amber.
            List<int>? overdue = null;
            foreach (var pair in inside) if (time - pair.Value > InsideLimit) (overdue ??= new List<int>()).Add(pair.Key);
            if (overdue != null) foreach (var j in overdue) Leave(j, d.Events);

            for (var k = 0; k < marks.Count; k++)
            {
                var m = marks[k];
                if (!m.Stop) redDecision.Remove(m.Position);
                var room = m.Along - front;
                if (room < -0.3f || drivenThrough.Contains(m.Position)) continue;
                var tooLate = room > 0f && speed * speed / (2f * room) > PursuitDriver.HardDecel;
                if (m.Stop)
                {
                    if (!redDecision.TryGetValue(m.Position, out var through)) redDecision[m.Position] = through = room <= 0f || tooLate;
                    if (through) continue;
                    d.StopAlong = m.Along - ShortOfLine;
                    d.StopPosition = m.Position;
                    d.StopIsRed = true;
                    return;
                }
                if (m.GiveWay && m.Enter && m.Junction >= 0 && !entered.Contains(m.Position))
                {
                    if (m.Free == true)
                    {
                        if (m.Road >= 0) roadHeld[m.Junction] = m.Road;
                        continue;
                    }
                    if (m.Free == false)
                    {
                        if (!inside.ContainsKey(m.Junction)) roadHeld.Remove(m.Junction);
                        if (tooLate) { drivenThrough.Add(m.Position); continue; }
                    }
                    d.StopAlong = m.Along - ShortOfLine;
                    d.StopPosition = m.Position;
                    return;
                }
            }
        }

        // Every junction the car is counted in, left: at the end of the drive, after a teleport, on any exit.
        public void LeaveAll(List<JunctionEvent> events)
        {
            var all = new List<int>(inside.Keys);
            foreach (var j in all) Leave(j, events);
        }

        private void Leave(int junction, List<JunctionEvent> events)
        {
            roadHeld.Remove(junction);
            if (!inside.Remove(junction)) return;
            events.Add(new JunctionEvent { Kind = JunctionEventKind.Leave, Junction = junction });
        }
    }
}
