#nullable enable
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Routing;
using HarmonyLib;
using Helpers;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DrivenRides
{
    // Road destinations come from the street's parking strips. Generated spaces are checked only on arrival,
    // after the game has populated the area. This adapter does not run the local manoeuvre planner.
    internal static class ParkingNavigation
    {
        private static readonly FieldInfo? SideBySide = AccessTools.Field(typeof(ParkingLaneGenerator), "sideBySideParkingSpots");
        private static readonly FieldInfo? Curved = AccessTools.Field(typeof(ParkingLaneGenerator), "curvedLane");

        public static bool OrdinaryParallel(ParkingLaneGenerator lane) => lane != null && !lane.isHandicapParking
            && !lane.designatedDeliveryVanSpot && SideBySide?.GetValue(lane) is bool sideBySide && !sideBySide;

        public static List<Goal> Goals(Vector3 door, CarSpec car, int vehicleType)
        {
            var strips = new List<ParkingStrip>();
            // Inactive street sections count too (the Hamptons, for example). Reading geometry must not spawn
            // parked cars or regenerate gaps at the remote destination.
            foreach (var lane in Object.FindObjectsOfType<ParkingLaneGenerator>(true))
            {
                if (!OrdinaryParallel(lane)) continue;
                var curved = Curved?.GetValue(lane) is bool curve && curve;
                foreach (var box in lane.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (!box.enabled || !box.isTrigger || (LayerHelper.parkingAreaLayerMask & (1 << box.gameObject.layer)) == 0
                        || box.GetComponentInParent<ParkingLaneGenerator>() != lane || (curved && box.transform == lane.transform)) continue;
                    var t = box.transform; var scale = t.lossyScale;
                    var centre = t.TransformPoint(box.center);
                    var halfLength = box.size.z * Mathf.Abs(scale.z) / 2;
                    if (Vector3.Distance(centre, door) > halfLength + 155) continue;
                    strips.Add(new ParkingStrip(GleyGraph.ToVec(centre), t.eulerAngles.y * Mathf.Deg2Rad,
                        box.size.x * Mathf.Abs(scale.x) / 2, halfLength));
                }
            }
            var goals = GleyGraph.Graph != null ? ParkingGoalPicker.Pick(GleyGraph.Graph, strips, GleyGraph.ToVec(door), car, vehicleType) : new List<Goal>();
            Log.Trace($"Parking navigation: {strips.Count} parallel strips, {goals.Count} lane approaches near the destination.");
            return goals;
        }

        public static bool BesideSpace(NwhCar car, CarSpec spec, Vector3 position, Quaternion rotation)
        {
            var graph = GleyGraph.Graph;
            if (graph == null) return false;
            var rear = position + rotation * new Vector3(0, 0, car.RearZ);
            var forward = rotation * Vector3.forward;
            var delta = car.RearAxle - rear;
            var ahead = Vector3.Dot(delta, forward);
            return ahead >= -8 && ahead <= 16 && Vector3.Dot(car.Root.transform.forward, forward) > 0.98f
                && ParkingGoalPicker.Adjacent(graph, new ParkingStrip(GleyGraph.ToVec(rear), rotation.eulerAngles.y * Mathf.Deg2Rad,
                    spec.BodyHalf.X + 0.2f, spec.Length / 2 + 1), GleyGraph.ToVec(car.RearAxle));
        }

        public static List<Goal> SpaceGoals(NwhCar car, CarSpec spec, Vector3 position, Quaternion rotation, int vehicleType)
        {
            var rear = position + rotation * new Vector3(0, 0, car.RearZ);
            return ParkingGoalPicker.ForSpace(GleyGraph.Graph!, GleyGraph.ToVec(rear), rotation.eulerAngles.y * Mathf.Deg2Rad, spec, vehicleType);
        }

        internal sealed class Choice { public AutoParkSpot? Spot; }

        // Pick using directed road travel plus walking cost, not proximity across a one-way street. A reachable
        // space beside the car takes priority; no road journey is needed for that local handoff.
        public static IEnumerator ChooseSpace(CarController owner, List<AutoParkSpot> spaces, Vector3? door, Choice choice)
        {
            var graph = GleyGraph.Graph;
            var type = OwnCarTaxi.DrivableType(owner);
            var planner = type != null ? NwhPlanner.For(type, owner.vehicleType.maxSpeed) : null;
            var car = NwhCar.Wrap(owner.vehicleController, owner.gameObject, type ?? "", out _);
            var prefab = OwnCarTaxi.PlanPrefab(owner, out _);
            if (graph == null || planner == null || car == null || prefab == null) yield break;
            var goals = new Dictionary<int, (Goal Goal, AutoParkSpot Spot)>();
            var best = float.MaxValue;
            var processed = 0;
            foreach (var spot in spaces)
            {
                if (spot == null) continue;
                var p = spot.transform.position; var r = spot.transform.parent.rotation;
                var walk = door is Vector3 target ? Vector3.Distance(p, target) / 1.4f : 0f;
                var direct = BesideSpace(car, planner.Car, p, r);
                if (direct)
                {
                    var score = walk + Vector3.Distance(car.RearAxle, p) / 0.8f;
                    if (score < best) { best = score; choice.Spot = spot; }
                }
                foreach (var goal in SpaceGoals(car, planner.Car, p, r, (int)prefab.vehicleType))
                {
                    var cost = walk + goal.WalkSeconds;
                    if (!goals.TryGetValue(goal.Waypoint, out var old) || cost < old.Goal.WalkSeconds)
                        goals[goal.Waypoint] = (new Goal(goal.Waypoint, cost), spot);
                }
                // A dense area can contain many gaps; do not do every graph query in one rendered frame.
                if (++processed % 8 == 0) yield return null;
                if (owner == null || !owner.controlledByPlayer || Ride.Active?.ParkingSkipped == true) yield break;
            }
            if (choice.Spot != null) { Log.Trace("Parking navigation: a free space beside the current lane."); yield break; }
            if (goals.Count == 0) yield break;
            var start = OwnCarTaxi.StartUnder(graph, owner, -1, w => !graph.IsIntersection(w), out var offset);
            if (start < 0 || Mathf.Abs(offset) > 1.5f) yield break;
            var list = new List<Goal>();
            foreach (var pair in goals.Values) list.Add(pair.Goal);
            var job = RoutePlanner.Begin(graph, new RouteRequest { Start = start, Goals = list, VehicleType = (int)prefab.vehicleType,
                WideUTurns = true, IsDisabled = GleyGraph.IsDisabled,
                IsBlockedStep = (a, b) => Gates.LockedStep(a, b) || GleyGraph.IsBlockedStep(a, b) || planner.IsBlocked(a, b) });
            var total = Stopwatch.StartNew();
            while (job.Status == PlanStatus.Running && total.Elapsed.TotalSeconds < 2)
            {
                var frame = Stopwatch.StartNew();
                job.Step(1500, () => frame.Elapsed.TotalMilliseconds >= 3);
                yield return null;
                if (owner == null || !owner.controlledByPlayer || Ride.Active?.ParkingSkipped == true) yield break;
            }
            if (job.Status == PlanStatus.Found && job.Result != null)
            {
                choice.Spot = goals[list[job.Result.GoalIndex].Waypoint].Spot;
                Log.Trace($"Parking navigation: free space reached by {job.Result.LengthMetres:0} m of directed road travel.");
            }
        }
    }
}
