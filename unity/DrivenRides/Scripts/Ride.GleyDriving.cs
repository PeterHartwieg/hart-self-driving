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
    // GleyDriving operations. Fields remain in Ride.cs to preserve initialization order.
    internal sealed partial class Ride
    {

        private void Depart()
        {
            if (nwh != null) { DepartNwh(); return; }
            if (!OwnsCar) { EndWith(Outcome.Vanilla, "the car was gone before departure"); return; }
            if (kind == Kind.Taxi) taxi!.DriveAway();
            owned.Clear();
            // A loaded car sits on its spawn waypoint; the route starts there.
            for (var i = onFirstWaypoint ? 1 : 0; i < path.Count; i++) owned.Add(path[i]);
            car.presetPath = owned;
            departed = true;
            SetTopSpeed();
            Go();
            progress = new RouteProgress(Points(path));
            IndexRoute();
            watch = new RideWatch(unpausedSeconds, route.DriveSeconds);
            // The car's own traffic music (AiCarMusic, a one-in-ten chance per car) played at full volume with the
            // camera on the car (spike 18). Muted for the ride.
            foreach (var music in car.GetComponentsInChildren<AiCarMusic>(true))
                if (music.musicPlayer != null && !music.musicPlayer.mute) { music.musicPlayer.mute = true; muted.Add(music.musicPlayer); }
            if (kind != Kind.Test)
            {
                if (TestMode.On)
                {
                    probe = car.gameObject.AddComponent<CollisionProbe>();
                    probe.Trace = trace;
                }
                SwitchCamera();
            }
            phase = Phase.Driving;
            Log.Trace($"Departed: {owned.Count} waypoints for Gley, light cycle {GreenLightTime()} s.");
        }

        private static List<Vec3> Points(List<int> waypoints)
        {
            var all = GleyGraph.Waypoints!;
            var points = new List<Vec3>(waypoints.Count);
            foreach (var i in waypoints) points.Add(GleyGraph.ToVec(all[i].position));
            return points;
        }

        // Puts each added turn's and U-turn's waypoints between its entry and exit (Core's PathExpander).
        private List<int> Expand(Route r)
        {
            var steering = carSteering;
            var graph = GleyGraph.Graph;
            // A way on the planner has just checked (NwhTake) is taken as it is; otherwise the route's plan, checked
            // as driven. One whose line fails leaves the steps blocked and the ride on the Gley expansion below.
            var plan = nwhTaken != null && nwhTaken.Path.Count > 0 && r.Waypoints.Count > 0 && nwhTaken.Path[0] == r.Waypoints[0] ? nwhTaken : null;
            nwhTaken = null;
            if (nwhPlanner != null && graph != null && plan == null)
            {
                plan = nwhPlanner.Plan(graph, r.Waypoints, out var failed);
                foreach (var (from, to, what) in failed) nwhPlanner.Block(from, to, NwhPlanner.CauseOf(graph, from, to, what));
                if (plan == null) Event($"NWH: the line fails as driven at step {failed[0].From} -> {failed[0].To}; the Gley expansion stands in.");
            }
            nwhPlan = plan;
            if (plan != null)
            {
                nwhWatch.Installed(plan);
                arcs.Clear();
                arcAt = 0;
                arcAlong = -1f;
                foreach (var (at, line) in plan.Arcs) arcs.Add((plan.Path[at], plan.Path[at + 1], line));
                slowPath.Clear();
                slowPath.UnionWith(plan.Expanded.SlowPath);
                uTurnPath.Clear();
                uTurnPath.UnionWith(plan.Expanded.UTurnPath);
                return plan.Path;
            }
            var expanded = PathExpander.Expand(graph, r.Waypoints, (e, x, a) => TurnClear(e, x, a, steering), (e, x) => ArcClear(e, x, steering));
            arcs.Clear();
            arcAt = 0;
            arcAlong = -1f;
            if (graph != null)
                foreach (var (entry, exit) in expanded.Arcs)
                    if (ClearArc(graph, entry, exit, steering) is Polyline line) arcs.Add((entry, exit, line));
            slowPath.Clear();
            slowPath.UnionWith(expanded.SlowPath);
            uTurnPath.Clear();
            uTurnPath.UnionWith(expanded.UTurnPath);
            return expanded.Path;
        }

        private static bool Contains(IReadOnlyList<int> list, int value)
        {
            for (var k = 0; k < list.Count; k++) if (list[k] == value) return true;
            return false;
        }

        // Takes one action off the car and reapplies the AI's top action to the driving state.
        private void ClearAction(SpecialDriveActionTypes action)
        {
            var ai = DrivingAIField?.GetValue(TrafficManager.Instance);
            if (ai == null || NewActionMethod == null || ApplyActionMethod == null || CurrentActionField == null) return;
            NewActionMethod.Invoke(ai, new object[] { carIndex, action, false });
            if (CurrentActionField.GetValue(ai) is SpecialDriveActionTypes[] current && carIndex < current.Length)
            {
                current[carIndex] = SpecialDriveActionTypes.Destroyed;
                ApplyActionMethod.Invoke(ai, new object[] { carIndex });
            }
        }

        private void Go()
        {
            var traffic = TrafficManager.Instance;
            var ai = DrivingAIField?.GetValue(traffic);
            if (ai == null || NewActionMethod == null || ApplyActionMethod == null || CurrentActionField == null)
            {
                Log.Error("Go: Gley's DrivingAI methods not found.");
                return;
            }
            var next = traffic.GetTargetWaypoint(carIndex);
            var atRed = Options.TrafficLights && next != null && next.stop;
            foreach (var action in new[] { SpecialDriveActionTypes.StopNow, SpecialDriveActionTypes.TempStop, SpecialDriveActionTypes.StopInPoint })
            {
                if (action == SpecialDriveActionTypes.StopInPoint && atRed) continue;
                NewActionMethod.Invoke(ai, new object[] { carIndex, action, false });
            }
            if (CurrentActionField.GetValue(ai) is SpecialDriveActionTypes[] current && carIndex < current.Length)
            {
                // A value the AI never picks, so ApplyAction sees a change and sends the driving state.
                current[carIndex] = SpecialDriveActionTypes.Destroyed;
                ApplyActionMethod.Invoke(ai, new object[] { carIndex });
            }
            traffic.VehicleUpdateWaypoint(car);
        }

        // Red lights are Gley's own: it stops a car whose target is a red stop line (StopInPoint) and lets it go on
        // green. For a car with a preset path it would also move the target past the stop line once the car is within
        // 5 m, stopped or not; the WaypointRequested prefix (HoldsAtRed) blocks that for the ride car. With the option
        // off, a car stopped at a red is sent on. A bump leaves a StopNow that Gley never clears for a car on a path,
        // so the car is sent on after a moment.
        private void HandleStops()
        {
            var traffic = TrafficManager.Instance;
            var next = traffic.GetTargetWaypoint(carIndex);
            var state = traffic.GetCurrentDrivingState(carIndex).Item2;
            var red = next != null && next.stop;
            if (red && state == SpecialDriveActionTypes.StopInPoint)
            {
                if (!Options.TrafficLights) { Go(); Event("Light: red, driving through (option off)."); return; }
                if (loggedRed != next!.listIndex) { loggedRed = next.listIndex; Log.Trace($"Light: waiting at the red at waypoint {next.listIndex}."); }
            }
            // Gley's give-way mode, set at a give-way waypoint, isn't taken off a car on a preset path: on spike 20 the
            // car crawled at 5 m/s through the Hamptons. Off again once the car's target isn't a give-way point.
            if (state == SpecialDriveActionTypes.GiveWay && next != null && !next.giveWay)
            {
                giveWaySeconds += Time.unscaledDeltaTime;
                if (giveWaySeconds >= 3f) { giveWaySeconds = 0f; ClearAction(SpecialDriveActionTypes.GiveWay); Event("Give way: off again."); }
            }
            else giveWaySeconds = 0f;
            if (state == SpecialDriveActionTypes.StopNow)
            {
                stopNowSeconds += Time.unscaledDeltaTime;
                if (stopNowSeconds >= BumpSeconds) { stopNowSeconds = 0f; Go(); Event("Bump: going again."); }
            }
            else stopNowSeconds = 0f;
        }

        // The WaypointRequested prefix: true while the ride car's target is a red stop line and lights are obeyed.
        public static bool HoldsAtRed(int vehicleIndex)
        {
            var ride = Active;
            if (ride == null || !ride.GleyDriven || ride.finished || ride.cancelled || !ride.departed || ride.carIndex != vehicleIndex || !Options.TrafficLights || !ride.OwnsCar) return false;
            var next = TrafficManager.Instance.GetTargetWaypoint(vehicleIndex);
            return next != null && next.stop;
        }

        // Changes the car's top speed in Gley when it differs from the last one set; Gley reads it at the next waypoint.
        private void ApplyTopSpeed(float speed)
        {
            if (Mathf.Approximately(speed, appliedTop)) return;
            try
            {
                var ai = DrivingAIField?.GetValue(TrafficManager.Instance);
                if (ai == null || SetMaxSpeedMethod == null) return;
                SetMaxSpeedMethod.Invoke(ai, new object[] { carIndex, speed });
                appliedTop = speed;
                if (speed < TopSpeed) trace.Note($"slow {speed:0} km/h for an added turn");
            }
            catch (Exception ex) { Log.Error("Top speed: " + ex.Message); }
        }

        private void SetTopSpeed()
        {
            try
            {
                var ai = DrivingAIField?.GetValue(TrafficManager.Instance);
                if (ai == null || SetMaxSpeedMethod == null) { Log.Error("Top speed: Gley's DrivingAI not found."); return; }
                var top = TopSpeed;
                SetMaxSpeedMethod.Invoke(ai, new object[] { carIndex, (float)top });
                appliedTop = top;
                // Gley sets the acceleration so a car reaches its rolled top speed in its model's accelerationTime
                // (10 s by default): slow off the line. The ride car reaches its top speed in AccelerationSeconds.
                if (PowerStepField?.GetValue(TrafficManager.Instance) is NativeArray<float> power && carIndex < power.Length)
                    power[carIndex] = top / 3.6f / (AccelerationSeconds / Time.fixedDeltaTime);
                if (SteeringStepField?.GetValue(TrafficManager.Instance) is NativeArray<float> steering && carIndex < steering.Length && steeringWas < 0f
                    && WheelDistanceField?.GetValue(TrafficManager.Instance) is NativeArray<float> wheels && carIndex < wheels.Length
                    && MaxSteerField?.GetValue(TrafficManager.Instance) is NativeArray<float> lockArray && carIndex < lockArray.Length)
                {
                    steeringWas = steering[carIndex];
                    wheelDistanceWas = wheels[carIndex];
                    maxSteerWas = lockArray[carIndex];
                    // The drivable car's wheelbase and lock (Steering), its wheel turned to full lock in a third of a
                    // second or more (SteeringFactor), as TurnDrive models it.
                    wheels[carIndex] = carSteering.Shape.WheelDistance;
                    lockArray[carIndex] = carSteering.Shape.MaxSteerDegrees;
                    steering[carIndex] = carSteering.Shape.MaxSteerDegrees * Time.fixedDeltaTime * carSteering.SteeringFactor;
                }
                Log.Trace($"Top speed {top} km/h ({(driverTopSpeed > 0 ? $"vehicle type {driverVehicleType}" : "traffic model")}; Gley model {car.minPossibleSpeed}-{car.maxPossibleSpeed}, rolled {car.GetMaxSpeed()}).");
            }
            catch (Exception ex) { Log.Error("Top speed: " + ex.Message); }
        }

        // Each physics step, before Gley's drive job: while the front axle is on an arc turn, Gley's target for the car
        // is the point ArcLookahead on along the arc. TrafficManager keeps the target positions in an array it only
        // rewrites when the car's waypoint changes, and DriveJob steers the front axle at it. After the arc the real
        // target goes back. Gley still pops the exit lane's waypoints as the car passes them.
        internal static void SteerArcs() => Active?.SteerArc();

        private void SteerArc()
        {
            if (!GleyDriven || !departed || finished || cancelled || arcAt >= arcs.Count || car == null || progress == null || !OwnsCar) return;
            var (entry, exit, line) = arcs[arcAt];
            // Stopping at a light or giving way, Gley brakes by the distance to its real target.
            if (TrafficManager.Instance.GetCurrentDrivingState(carIndex).Item2 != SpecialDriveActionTypes.Forward)
            {
                if (arcAlong >= 0f && !arcHeld) { arcHeld = true; RestoreTarget(); }
                return;
            }
            arcHeld = false;
            var front = car.GetFrontAxle().position;
            if (arcAlong < 0f)
            {
                // It starts with the front axle on the lead-in, by its lane, before Gley lets go of the entry.
                if (!routeIndex.TryGetValue(entry, out var k) || k > progress.NextIndex + 2) return;
                var start = line.At(0f);
                var on = line.At(1f);
                var dir = new Vector3(on.X - start.X, 0f, on.Z - start.Z).normalized;
                var offset = new Vector3(front.x - start.X, 0f, front.z - start.Z);
                var ahead = Vector3.Dot(offset, dir);
                if (ahead < 0f || ahead > TurnArc.LeadIn + 6f || (offset - dir * ahead).magnitude > 2.5f) return;
                arcAlong = 0f;
                Event($"Turn {entry} -> {exit}: steering along its {line.Length:0} m arc.");
            }
            var pivot = car.rb != null ? car.rb.worldCenterOfMass : car.transform.position;
            arcAlong = line.Project(pivot.x, pivot.z, arcAlong);
            var lookahead = ArcLookahead(carSteering);
            if (arcAlong + lookahead >= line.Length) { EndArc(); return; }
            var carrot = line.At(arcAlong + lookahead);
            SetTarget(new Vector3(carrot.X, carrot.Y, carrot.Z));
        }

        private void EndArc()
        {
            arcAt++;
            arcAlong = -1f;
            arcHeld = false;
            RestoreTarget();
        }

        private void RestoreTarget()
        {
            var target = TrafficManager.Instance.GetTargetWaypoint(carIndex);
            if (target != null) SetTarget(target.position);
        }

        internal static void TurnAboutMiddle() => Active?.TurnAboutCentre();

        private void TurnAboutCentre()
        {
            if (!GleyDriven || !departed || finished || cancelled || car == null || car.rb == null || !OwnsCar) return;
            if (!(RotationAngleField?.GetValue(TrafficManager.Instance) is NativeArray<float> angles) || carIndex >= angles.Length) return;
            var degrees = angles[carIndex];
            if (Mathf.Abs(degrees) < 0.0001f) return;
            var rb = car.rb;
            // From the centre of mass to the origin, flat; rotating the origin about the centre of mass by the step's
            // angle moves it by (R - 1) of that.
            var offset = rb.position - rb.worldCenterOfMass;
            offset.y = 0f;
            var moved = Quaternion.Euler(0f, degrees, 0f) * offset - offset;
            if (moved.sqrMagnitude > 0.04f) return;
            rb.position += moved;
        }

        private void SetTarget(Vector3 at)
        {
            try
            {
                var array = TargetPositionField?.GetValue(TrafficManager.Instance);
                if (array == null) return;
                if (targetSetter == null || float3Constructor == null)
                {
                    var type = array.GetType();
                    targetSetter = type.GetMethod("set_Item");
                    float3Constructor = type.GetGenericArguments()[0].GetConstructor(new[] { typeof(float), typeof(float), typeof(float) });
                }
                targetSetter!.Invoke(array, new[] { carIndex, float3Constructor!.Invoke(new object[] { at.x, at.y, at.z }) });
            }
            catch (Exception ex) { Log.Error("Arc target: " + ex.Message); arcAt = arcs.Count; arcAlong = -1f; }
        }

        private bool StepBlockedAhead()
        {
            if (Time.unscaledTime < nextNearCheck) return false;
            nextNearCheck = Time.unscaledTime + 0.5f;
            var graph = GleyGraph.Graph;
            if (graph == null) return false;
            var all = GleyGraph.Waypoints!;
            var here = car.transform.position;
            var w = route.Waypoints;
            for (var k = 0; k + 1 < w.Count; k++)
            {
                if (Contains(graph.Successors(w[k]), w[k + 1]) || checkedNear.Contains((w[k], w[k + 1]))) continue;
                if (!routeIndex.TryGetValue(w[k], out var at) || at < progress!.NextIndex - 1) continue;
                if (Vector3.Distance(here, all[w[k]].position) > 80f) continue;
                checkedNear.Add((w[k], w[k + 1]));
                // The next few waypoints too, so an added turn aims where the ride's path aims (PathExpander).
                var ahead = new List<int>();
                for (var q = k; q < w.Count && q <= k + 4; q++) ahead.Add(w[q]);
                var from = w[k];
                var to = w[k + 1];
                // The NWH check is asked afresh: the parked cars near the step are loaded now. A step still feasible in
                // another form (vias now, not the arc) changes the line, so the route is expanded again (review round 1).
                var before = nwhPlanner?.Check.Step(from, to);
                nwhPlanner?.Check.Forget(from, to);
                var blocked = (nwhPlanner != null ? nwhPlanner.BlockedSteps(graph, ahead, 0f) : FindBlockedSteps(graph, ahead, carSteering)).FindAll(b => b.From == from && b.To == to);
                if (blocked.Count == 0)
                {
                    var now = nwhPlanner?.Check.Step(from, to);
                    if (before != null && now != null && !now.SameShape(before))
                    {
                        Event($"Step {from} -> {to} ahead now driven {now.Form}; replanning.");
                        return true;
                    }
                    continue;
                }
                // A test drives its case as given and reports the block; the rides' blocked-step set stays as it was.
                if (kind == Kind.Test) { EndWith(Outcome.Vanilla, $"replanned: step {w[k]} -> {w[k + 1]} blocked by '{blocked[0].What}'"); return false; }
                if (nwhPlanner != null) nwhPlanner.Block(w[k], w[k + 1], NwhPlanner.CauseOf(graph, w[k], w[k + 1], blocked[0].What));
                else GleyGraph.BlockStep(w[k], w[k + 1]);
                Event($"Step {w[k]} -> {w[k + 1]} ahead blocked by '{blocked[0].What}'; replanning.");
                return true;
            }
            return false;
        }

        private void StopAtEnd()
        {
            TrafficManager.Instance.SetVehicleAction(car, SpecialDriveActionTypes.StopInPoint, true);
            // Cleared in place: presetPath stays this same non-null list, so Gley never despawns the car.
            owned.Clear();
            phase = Phase.Stopping;
            Event($"Stopping at the end of the route, {LeftMetres:0} m from its last waypoint.");
        }

        private void Restart()
        {
            Restarts++;
            Go();
            watch!.Restarted(unpausedSeconds);
            Event("Watchdog: restart (" + watch.LastReason + ").");
        }

        // Gley signals a red light by "stop" on the target waypoint and StopInPoint, the car in front by
        // StopInDistance. A waiting car isn't stuck, and a restart would clear those stops.
        private bool IsWaiting()
        {
            var traffic = TrafficManager.Instance;
            var next = traffic.GetTargetWaypoint(carIndex);
            var state = traffic.GetCurrentDrivingState(carIndex).Item2;
            return (next != null && next.stop) || state == SpecialDriveActionTypes.StopInPoint ||
                   state == SpecialDriveActionTypes.GiveWay || state == SpecialDriveActionTypes.StopInDistance;
        }

        // Starts a replan; StepReplan advances it over the next frames.
        private void Replan()
        {
            if (kind == Kind.Test) { EndWith(Outcome.Vanilla, "replanned: watchdog, " + (watch?.LastReason ?? "")); return; }
            var graph = GleyGraph.Graph;
            var next = TrafficManager.Instance.GetTargetWaypoint(carIndex);
            if (graph == null || next == null) { EndWith(Outcome.Vanilla, "replan had no start"); return; }
            replanJob = RoutePlanner.Begin(graph, new RouteRequest
            {
                Start = next.listIndex,
                Goals = goals,
                VehicleType = vehicleType,
                IsDisabled = GleyGraph.IsDisabled,
                IsBlockedStep = (a, b) => GleyGraph.IsBlockedStep(a, b) || (nwhPlanner != null ? nwhPlanner.IsBlocked(a, b) : TestedSteps.Blocks(a, b, carLength)),
                WideUTurns = nwhPlanner != null,
            });
            replanSeconds = 0f;
            Log.Trace("Watchdog: replanning (" + watch!.LastReason + ").");
        }

        private void StepReplan()
        {
            var job = replanJob!;
            job.Step(ReplanStep);
            replanSeconds += Time.unscaledDeltaTime;
            if (job.Status == PlanStatus.Running && replanSeconds < ReplanSecondsLimit) return;
            replanJob = null;
            if (job.Status != PlanStatus.Found || job.Result == null) { EndWith(Outcome.Vanilla, $"replan found no route ({job.Status})"); return; }
            alongOffset += progress!.AlongMetres;
            route = job.Result;
            path = Expand(route);
            owned.Clear();
            owned.AddRange(path);
            progress = new RouteProgress(Points(path));
            IndexRoute();
            // Gley takes the new list at its next waypoint request. At a light or in a queue a restart would clear
            // the stop, so it only happens when the car isn't waiting.
            if (IsWaiting()) watch!.Waiting(unpausedSeconds);
            else Restart();
            watch!.Replanned(unpausedSeconds);
            Log.Trace($"Watchdog: replanned, {route.Waypoints.Count} waypoints, {route.LengthMetres:0} m, {job.Expanded} expanded.");
        }

        private void IndexRoute()
        {
            routeIndex.Clear();
            for (var k = 0; k < path.Count; k++) routeIndex[path[k]] = k;
        }

        // The HasReachedOrPassedWaypoint prefix. Gley counts a waypoint as passed once it is behind the car's nose,
        // and pops every such waypoint off the path at once; in a U-turn or a lane change, more so at 3x, the car
        // faces sideways for a moment and Gley emptied the rest of the route (spike 8: stopped 272 m short after
        // hitting a wall). For the ride car a waypoint counts as passed only when the car is within 5 m of it or
        // the route progress, which follows the route and not the car's nose, is past it. Null for other cars.
        public static bool? HasPassed(int vehicleIndex, int waypointIndex, Vector3 waypoint, Vector3 carCentre)
        {
            var ride = Active;
            if (ride == null || !ride.GleyDriven || ride.finished || ride.cancelled || !ride.departed || ride.carIndex != vehicleIndex || ride.progress == null || !ride.OwnsCar) return null;
            if ((waypoint - carCentre).sqrMagnitude < 25f) return true;
            return ride.routeIndex.TryGetValue(waypointIndex, out var k) && k < ride.progress.NextIndex;
        }

        // The ride car's centre of mass, as Gley measures from it; null for other cars.
        public static Vector3? CarCentre(int vehicleIndex)
        {
            var ride = Active;
            if (ride == null || !ride.GleyDriven || ride.carIndex != vehicleIndex || ride.car == null || ride.car.rb == null) return null;
            return ride.car.rb.worldCenterOfMass;
        }
    }
}
