#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BigAmbitions.DayNightCycle;
using Buildings;
using DG.Tweening;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using GleyTrafficSystem;
using HarmonyLib;
using Helpers;
using Player.HUD.SmartphoneUI;
using PlayerActivity;
using UI;
using Unity.Collections;
using UnityEngine;
using UnityEngine.AI;
using Vehicles.Taxis;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    internal sealed partial class Ride
    {

        private bool OnBridge()
        {
            var here = Body.position;
            bridges ??= Object.FindObjectsOfType<Streets.BridgeTriggerController>();
            foreach (var bridge in bridges)
            {
                if (bridge == null) continue;
                if (skipped.Contains(bridge)) continue;
                var trigger = bridge.GetComponent<Collider>();
                if (trigger == null || !trigger.bounds.Contains(here)) continue;
                skipped.Add(bridge);
                // A test never crosses a bridge (its cases stay clear of the triggers); if one does, it ends here.
                if (kind == Kind.Test) { EndWith(Outcome.Vanilla, "bridge"); return true; }
                if (!(SkipTargetsField?.GetValue(bridge) is Transform[] targets) || targets.Length == 0) { Event("Bridge: no skip targets."); return false; }
                Transform? best = null;
                var bestAngle = float.MaxValue;
                foreach (var t in targets)
                {
                    if (t == null || Vector3.Dot(t.position - here, Body.forward) < 0f) continue;
                    var angle = Mathf.Abs(Mathf.DeltaAngle(Body.eulerAngles.y, t.eulerAngles.y));
                    if (angle < bestAngle) { bestAngle = angle; best = t; }
                }
                if (best == null) { Event("Bridge: no skip target ahead."); return false; }
                var ahead = new List<Transform>();
                foreach (var t in targets)
                    if (t != null && Vector3.Dot(t.position - here, Body.forward) >= 0f && Vector3.Dot(t.forward, Body.forward) > 0f) ahead.Add(t);
                var all = GleyGraph.Waypoints!;
                var at = -1;
                var passes = false;
                for (var k = progress!.NextIndex; k + 1 < path.Count && at < 0; k++)
                {
                    var p = all[path[k]].position;
                    var near = false;
                    foreach (var t in ahead) if (Vector3.Distance(p, t.position) < Bridges.LandWithin) { near = true; break; }
                    if (!near) continue;
                    passes = true;
                    if (Vector3.Distance(here, p) >= MinBridgeSkipMetres) at = k;
                }
                if (!passes) { Event("Bridge: the route doesn't pass the far side; driving on."); return false; }
                if (at < 0) return false;
                Event("Bridge: targets ahead " + string.Join(", ", System.Linq.Enumerable.Select(ahead, t => $"({t.position.x:0.0}, {t.position.y:0.0}, {t.position.z:0.0})")) + ".");
                bridgeSkip = (best, at);
                phase = Phase.Bridging;
                return true;
            }
            return false;
        }

        private bool LandNwh(int at, out Vector3 landedAt, out string why)
        {
            landedAt = Body.position;
            why = "";
            var graph = GleyGraph.Graph;
            if (graph == null || nwhPlanner == null || nwhDrive == null) { why = "no planner"; return false; }
            var w = route.Waypoints;
            var r = -1;
            for (var k = at; k < path.Count && r < 0; k++) r = IndexOf(w, path[k]);
            if (r < 0 || r + 1 >= w.Count) { why = "no route waypoint past the bridge"; return false; }
            var rest = new List<int>();
            for (var k = r; k < w.Count; k++) rest.Add(w[k]);
            var plan = nwhPlanner.Plan(graph, rest, out var failed);
            if (plan == null) { why = failed.Count > 0 ? $"step {failed[0].From} -> {failed[0].To} fails as driven" : "no plan"; return false; }
            var all = GleyGraph.Waypoints!;
            var ignore = nwh!.Car.Root.transform;
            var land = all[rest[0]].position;
            var ahead = all[rest[1]].position;
            land.y = NwhPlanner.RoadAt(land, ignore) ?? land.y;
            ahead.y = NwhPlanner.RoadAt(ahead, ignore) ?? ahead.y;
            var heading = graph.PlainHeading(rest[0], out var hx, out var hz) ? new Vector3(hx, 0f, hz) : ahead - land;
            heading.y = 0f;
            var next = ahead - land;
            var run = new Vector2(next.x, next.z).magnitude;
            if (heading.sqrMagnitude > 0.01f && run > 0.5f) heading = heading.normalized + Vector3.up * (next.y / run);
            var rotation = heading.sqrMagnitude > 0.01f ? Quaternion.LookRotation(heading) : Body.rotation;
            NwhRides.PlaceRearAxle(nwh!.Car, land, rotation);
            landedAt = land;
            alongOffset += progress!.AlongMetres;
            route = Route.Fixed(graph, rest);
            nwhTaken = plan;
            path = Expand(route);
            progress = new RouteProgress(Points(path));
            IndexRoute();
            lastAlong = alongOffset;
            nwhDrive.DriveOn(nwhPlan ?? plan);
            Event($"Bridge: the car landed on waypoint {rest[0]}; {rest.Count} route waypoints on.");
            return true;
        }

        private IEnumerator SkipBridge()
        {
            var (target, at) = bridgeSkip!.Value;
            bridgeSkip = null;
            var all = GleyGraph.Waypoints!;
            var from = Body.position;
            var to = all[path[at]].position;
            Event($"Bridge: skipping {Vector3.Distance(from, to):0} m to waypoint {path[at]}.");
            faded = true;
            var fade = UiFader.Fade();
            while (fade.MoveNext()) yield return fade.Current;
            if (cancelled) yield break;

            if (nwh != null)
            {
                var ownOnBridge = kind == Kind.OwnCar ? OwnCarDrive.Car : null;
                if (ownOnBridge != null && !OwnCarTaxi.HasFuelForBridge(ownOnBridge, Vector3.Distance(from, to)))
                {
                    Notice.Text("drivenrides:notice_bridge_fuel");
                    Cancel("not enough fuel for the bridge");
                    yield break;
                }
                if (!LandNwh(at, out var landedAt, out var why)) { EndWith(Outcome.Vanilla, "bridge: " + why); yield break; }
                if (ownOnBridge != null) OwnCarTaxi.ChargeBridgeFuel(ownOnBridge, Vector3.Distance(from, landedAt));
                to = landedAt;
                if (bridges != null)
                    foreach (var other in bridges)
                        if (other != null && Vector3.Distance(other.transform.position, Body.position) < 300f) skipped.Add(other);
                yield return null;
            }
            else
            {
                var ahead = all[path[at + 1]].position - to;
                var rotation = ahead.sqrMagnitude > 0.01f ? Quaternion.LookRotation(ahead) : target.rotation;
                if (car.rb != null)
                {
                    car.rb.velocity = Vector3.zero;
                    car.rb.angularVelocity = Vector3.zero;
                    car.rb.position = to + Vector3.up * 0.3f;
                    car.rb.rotation = rotation;
                }
                car.transform.SetPositionAndRotation(to + Vector3.up * 0.3f, rotation);
                if (bridges != null)
                    foreach (var other in bridges)
                        if (other != null && Vector3.Distance(other.transform.position, to) < 300f) skipped.Add(other);
                alongOffset += progress!.AlongMetres;
                var rest = path.GetRange(at, path.Count - at);
                var shifted = new HashSet<int>();
                foreach (var k in slowPath) if (k >= at) shifted.Add(k - at);
                slowPath.Clear();
                slowPath.UnionWith(shifted);
                shifted.Clear();
                foreach (var k in uTurnPath) if (k >= at) shifted.Add(k - at);
                uTurnPath.Clear();
                uTurnPath.UnionWith(shifted);
                path = rest;
                owned.Clear();
                for (var k = 1; k < path.Count; k++) owned.Add(path[k]);
                progress = new RouteProgress(Points(path));
                IndexRoute();
                lastAlong = alongOffset;
                Go();
                yield return null;
            }

            var minutes = Vector3.Distance(from, to) * 0.04f * PlayerPrefSettings.GameSpeed;
            timeMachineEnded = false;
            Action ended = () => timeMachineEnded = true;
            GlobalEvents.onTimeMachineEnded += ended;
            try
            {
                Timestamp goal = TimeHelper.Now();
                goal.AddMinutes(minutes);
                ownTimeMachine = true;
                try { InstanceBehavior<UIs>.Instance.timeMachine.StartTimeMachine(goal, disableCancel: true); }
                finally { ownTimeMachine = false; }
                while (!timeMachineEnded && !cancelled) yield return null;
            }
            finally { GlobalEvents.onTimeMachineEnded -= ended; }
            if (cancelled) yield break;

            var unfade = UiFader.UnFade();
            while (unfade.MoveNext()) yield return unfade.Current;
            faded = false;
            watch!.Waiting(unpausedSeconds);
            phase = Phase.Driving;
        }
    }
}
