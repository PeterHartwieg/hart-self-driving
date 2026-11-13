using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Drive
{
    // Recovery operations. Fields remain in TrafficDriver.cs to preserve initialization order.
    public sealed partial class TrafficDriver
    {
        // Keep the first matching observation, as the recovery paths did before sharing this lookup.
        private static OtherCar? FindCar(IReadOnlyList<OtherCar> cars, int id)
        {
            for (var i = 0; i < cars.Count; i++) if (cars[i].Id == id) return cars[i];
            return null;
        }

        private void Back(in TrafficSnapshot s, TrafficDecision d, int candidate)
        {
            if (backingFrom < 0 && candidate >= 0 && HasRoomBehind(s))
            {
                backingFrom = candidate;
                backStartX = s.RearX;
                backStartZ = s.RearZ;
            }
            if (backingFrom < 0) return;
            var from = FindCar(s.Cars, backingFrom);
            var backed = (float)Math.Sqrt((s.RearX - backStartX) * (s.RearX - backStartX) + (s.RearZ - backStartZ) * (s.RearZ - backStartZ));
            var keep = from is OtherCar && !moving.Contains(backingFrom) && backed < MaxBackOff && HasRoomBehind(s);
            if (keep)
            {
                var o2 = from!.Value;
                var p = poses[0];
                keep = backed < MinBackOff || p.Overlaps(BodyOf(o2).Grown(BackOffClear, BackOffClear)) || p.Overlaps(TriggerOf(o2).Grown(BackOffClear, BackOffClear))
                    || InTurnedTrigger(o2, s);
            }
            if (!keep) { backingFrom = -1; return; }
            d.BackOff = true;
            d.BackOffFrom = backingFrom;
        }


        private void LaneBack(in TrafficSnapshot s, TrafficDecision d, bool standing)
        {
            if (laneBackFrom < 0)
            {
                if (!standing || !HasRoomBehindLane(s)) return;
                for (var i = 0; i < s.Cars.Count && laneBackFrom < 0; i++)
                {
                    var c = s.Cars[i];
                    if (!moving.Contains(c.Id) || heldFor.Contains(c.Id)) continue;
                    corridor.Build(graph, c, s.Flags, MaxHorizon, SweepMetres, roadHeld, SpeedingUp(c.Id), LeaderOf(c, s.Cars), Cruising(c.Id));
                    if (!corridor.Covers(poses[0].Grown(SideMargin, EndMargin), out var ua, out _, out var sin, out var cos) || ua <= 0f) continue;
                    if (Math.Abs(sin * poses[0].Sin + cos * poses[0].Cos) > 0.64f) continue;
                    if (StaysBehind(c, 0f, ua, null, null, 0, false)) continue;
                    var backOutMetres = BackOutDistance();
                    if (backOutMetres < 0f) continue;
                    if (backOutMetres / BackOffMs + 1f >= corridor.Reach(ua)) continue;
                    if (!PathBehindFree(s, backOutMetres + BackOffClear)) continue;
                    laneBackFrom = c.Id;
                    laneBackX = s.RearX;
                    laneBackZ = s.RearZ;
                }
                if (laneBackFrom < 0) return;
            }
            var from = FindCar(s.Cars, laneBackFrom);
            var backed = (float)Math.Sqrt((s.RearX - laneBackX) * (s.RearX - laneBackX) + (s.RearZ - laneBackZ) * (s.RearZ - laneBackZ));
            var keep = from is OtherCar && backed < MaxBackOff && HasRoomBehindLane(s);
            if (keep)
            {
                var c = from!.Value;
                corridor.Build(graph, c, s.Flags, MaxHorizon, SweepMetres, roadHeld, SpeedingUp(c.Id), LeaderOf(c, s.Cars), Cruising(c.Id));
                keep = corridor.Covers(poses[0].Grown(SideMargin + BackOffClear, EndMargin + BackOffClear), out var ua, out _, out _, out _) && ua > 0f;
            }
            if (!keep) { laneBackFrom = -1; return; }
            d.BackOff = true;
            d.BackOffFrom = laneBackFrom;
        }

        private void StandOff(in TrafficSnapshot s, TrafficDecision d, bool standing, float along)
        {
            if (standing) { if (rideStoodSince < 0f) rideStoodSince = now; }
            else if (!standOffBacking) rideStoodSince = -1f;
            standOffBacking = false;
            Forget(standOffGiven);
            if (standOffFrom < 0)
            {
                if (!standing || rideStoodSince < 0f || now - rideStoodSince < StandOffSeconds) return;
                for (var i = 0; i < s.Cars.Count && standOffFrom < 0; i++)
                {
                    var c = s.Cars[i];
                    if (!c.StoppedForUs || moving.Contains(c.Id)) continue;
                    if (standOffGiven.TryGetValue(c.Id, out var was) && (c.X - was.X) * (c.X - was.X) + (c.Z - was.Z) * (c.Z - was.Z) < StandOffMoved * StandOffMoved) continue;
                    if (!standingSince.TryGetValue(c.Id, out var since) || now - since < StandOffSeconds) continue;
                    if (!Before(c, poses[0])) continue;
                    var way = FirstZoneTouch(BodyOf(c), TriggerOf(c), 0.3f, 0);
                    if (way < 0 || rearAt[way] - along > StandOffRoom) continue;
                    standOffFrom = c.Id;
                    standOffX = s.RearX;
                    standOffZ = s.RearZ;
                    standOffSince = now;
                    standOffGiven[c.Id] = (c.X, c.Z);
                }
                if (standOffFrom < 0) return;
            }
            var from = FindCar(s.Cars, standOffFrom);
            if (!(from is OtherCar o) || now - standOffSince > StandOffHold) { standOffFrom = -1; return; }
            if (o.StoppedForUs) standOffWaited = now;
            var waits = now - standOffWaited < StandOffClear;
            var touch = FirstTouch(BodyOf(o), GroundMargin, 0);
            if (!waits && (touch < 0 || rearAt[touch] - along > StandOffRoom)) { standOffFrom = -1; return; }
            var backed = (float)Math.Sqrt((s.RearX - standOffX) * (s.RearX - standOffX) + (s.RearZ - standOffZ) * (s.RearZ - standOffZ));
            corridor.Build(graph, o, s.Flags, MaxHorizon, SweepMetres, roadHeld, true, LeaderOf(o, s.Cars));
            var inWay = corridor.Covers(poses[0].Grown(SideMargin + BackOffClear, EndMargin + BackOffClear), out var ua, out _, out _, out _) && ua > 0f;
            if ((waits || inWay) && backed < MaxBackOff && PathBehindFree(s, SweepStep))
            {
                d.BackOff = true;
                d.BackOffFrom = o.Id;
                standOffBacking = true;
                return;
            }
            Gap(d, 0, along, o.Id);
        }

        private float BackOutDistance()
        {
            var p = poses[0];
            var heading = (float)Math.Atan2(p.Sin, p.Cos);
            for (var back = 0.25f; back <= MaxBackOff; back += 0.25f)
            {
                var b = new Box(p.X - p.Sin * back, p.Z - p.Cos * back, heading, p.HalfWidth, p.HalfLength).Grown(SideMargin + BackOffClear, EndMargin + BackOffClear);
                if (!corridor.Covers(b, out var ua, out _, out _, out _) || ua <= 0f) return back;
            }
            return -1f;
        }

        private bool PathBehindFree(in TrafficSnapshot s, float metres)
        {
            if (s.GapBehind is float g && g < metres) return false;
            var p = poses[0];
            var heading = (float)Math.Atan2(p.Sin, p.Cos);
            for (var i = 0; i < s.Cars.Count; i++)
            {
                var other = BodyOf(s.Cars[i]).Grown(GroundMargin, GroundMargin);
                for (var back = SweepStep / 2f; back <= metres; back += SweepStep / 2f)
                    if (new Box(p.X - p.Sin * back, p.Z - p.Cos * back, heading, p.HalfWidth, p.HalfLength).Overlaps(other)) return false;
            }
            return true;
        }

        private bool HasRoomBehindLane(in TrafficSnapshot s)
        {
            var keep = backingFrom;
            backingFrom = laneBackFrom;
            var room = HasRoomBehind(s);
            backingFrom = keep;
            return room;
        }

        private bool InTurnedTrigger(in OtherCar c, in TrafficSnapshot s)
        {
            corridor.Build(graph, c, s.Flags, 1f, c.TriggerLength + BackOffClear + 2f, roadHeld);
            return corridor.Covers(poses[0].Grown(BackOffClear, BackOffClear), out var ua, out _, out _, out _) && ua <= c.TriggerLength + BackOffClear;
        }

        private bool HasRoomBehind(in TrafficSnapshot s)
        {
            if (s.GapBehind is float g && g < RoomBehind) return false;
            float hx = poses[0].Sin, hz = poses[0].Cos;
            var behind = new Box(poses[0].X - hx * RoomBehind, poses[0].Z - hz * RoomBehind, (float)Math.Atan2(hx, hz), poses[0].HalfWidth, poses[0].HalfLength);
            for (var i = 0; i < s.Cars.Count; i++)
            {
                var c = s.Cars[i];
                if (c.Id == backingFrom) continue;
                var other = BodyOf(c);
                if ((c.X - poses[0].X) * hx + (c.Z - poses[0].Z) * hz > 0f) continue;
                if (behind.Overlaps(other)) return false;
            }
            return true;
        }

        private void Stuck(in TrafficSnapshot s, TrafficDecision d, bool standing)
        {
            if (!standing || !(d.GapAhead is float gap) || gap > PursuitDriver.StopGap + 2f || d.GapFor < 0 || heldFor.Contains(d.GapFor)) return;
            for (var i = 0; i < s.Cars.Count; i++)
            {
                var c = s.Cars[i];
                if (c.Id != d.GapFor || moving.Contains(c.Id) || !standingSince.TryGetValue(c.Id, out var since)) continue;
                var laneEnd = c.Target >= 0 && c.Target < graph.Count && graph.Successors(c.Target).Count == 0;
                if (laneEnd && s.Time - since > 3f) d.BlockedBy = c.Id;
                else if (s.Time - since > StuckSeconds) d.BlockedBy = c.Id;
                if (d.BlockedBy >= 0) d.BlockedWhy = laneEnd ? $"traffic car {c.Id} stands at a lane end in the way" : $"traffic car {c.Id} hasn't moved for {StuckSeconds:0} s";
                return;
            }
        }
    }
}
