using System;
using System.Collections.Generic;

namespace DrivenRides.Core.Drive
{
    public sealed class JunctionRules
    {
        public const float InsideLimit = 20f;
        public const float AskMetres = 6f;
        // The front stops this far short of a stop line's waypoint: the pedals don't stop it to the centimetre (in the
        // T3 traces of 14 October the front stood 0 to 0.5 m past where it was told to stop, 146 of 164 reds). Gley's
        // own cars stand with their front at the waypoint (399 stops in the dumps, within 2 cm); at 1 m the ride car
        // stood 1 to 1.5 m behind them (Peter: "not at the stop line but half a car in front").
        public const float ShortOfLine = 0.6f;

        private readonly HashSet<int> drivenThrough = new HashSet<int>();
        private readonly Dictionary<int, bool> redDecision = new Dictionary<int, bool>();
        private readonly HashSet<int> entered = new HashSet<int>();
        private readonly HashSet<int> left = new HashSet<int>();
        private readonly Dictionary<int, float> inside = new Dictionary<int, float>();
        private readonly Dictionary<int, int> roadHeld = new Dictionary<int, int>();

        public IReadOnlyDictionary<int, float> Inside => inside;

        public bool ShouldAsk(in PathMark m, float front) =>
            m.GiveWay && m.Enter && m.Junction >= 0 && !entered.Contains(m.Position) && m.Along >= front - 0.5f && m.Along - front <= AskMetres;

        public int RoadHeld(int junction) => roadHeld.TryGetValue(junction, out var road) ? road : -1;

        public float Overhang { get; set; }

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
                var room = m.Along - (front - Overhang);
                if (room < -0.3f || drivenThrough.Contains(m.Position)) continue;
                var tooLate = room > 0f && speed * speed / (2f * room) > PursuitDriver.HardDecel;
                if (m.Stop)
                {
                    if (!redDecision.TryGetValue(m.Position, out var through)) redDecision[m.Position] = through = room <= 0f || tooLate;
                    if (through && room > 0f && (speed < 1f || speed * speed / (2f * room) <= PursuitDriver.Decel))
                        redDecision[m.Position] = through = false;
                    if (through) continue;
                    d.StopAlong = m.Along - ShortOfLine + Overhang;
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
                    d.StopAlong = m.Along - ShortOfLine + Overhang;
                    d.StopPosition = m.Position;
                    return;
                }
            }
        }

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
