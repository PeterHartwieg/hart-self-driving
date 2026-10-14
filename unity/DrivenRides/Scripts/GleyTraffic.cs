#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using DrivenRides.Core.Drive;
using GleyTrafficSystem;
using GleyUrbanAssets;
using HarmonyLib;
using UnityEngine;

namespace DrivenRides
{
    // What Core's traffic decisions (TrafficDriver) read from Gley, and the two things they ask of it
    // (docs/implementation-plan.md, "The four layers"): the active traffic cars near the ride car with their target
    // waypoints and whether the ride car is what holds them back (VehicleComponent.obstacleList, private); the live
    // flags of any waypoint; the intersection a waypoint belongs to (Waypoint.associatedIntersection, private) and the
    // road it enters by; Waypoint.CanChange(); IIntersection.VehicleEnter(-1) and VehicleLeft(-1).
    // Phase 1 (13 October 2026, 0.1.54): obstacleList readable on every active car; reading every car within 60 m
    // each physics step took 30 µs (median), 257 µs at most; VehicleEnter(-1) holds a light on amber and
    // VehicleLeft(-1) lets it change 0.3 s later; CanChange() answers from outside.
    internal sealed class GleyTraffic : ILiveFlags
    {
        private static readonly FieldInfo? TrafficVehiclesField = AccessTools.Field(typeof(TrafficManager), "trafficVehicles");
        private static readonly FieldInfo? ObstacleListField = AccessTools.Field(typeof(VehicleComponent), "obstacleList");
        private static readonly FieldInfo? IntersectionField = AccessTools.Field(typeof(Waypoint), "associatedIntersection");
        private static bool reported;

        // Each intersection the ride meets gets a number for Core; the same object keeps its number.
        private readonly Dictionary<IIntersection, int> ids = new Dictionary<IIntersection, int>();
        private readonly List<IIntersection> byId = new List<IIntersection>();
        private readonly Dictionary<int, (int Junction, int Road)> cache = new Dictionary<int, (int, int)>();
        private readonly HashSet<int> entered = new HashSet<int>();
        private readonly HashSet<Collider> rideColliders = new HashSet<Collider>();

        public readonly List<VehicleComponent> Components = new List<VehicleComponent>();

        public GleyTraffic(GameObject rideRoot)
        {
            foreach (var c in rideRoot.GetComponentsInChildren<Collider>(true)) rideColliders.Add(c);
            if (reported) return;
            reported = true;
            Log.Info($"Traffic reading: trafficVehicles {(TrafficVehiclesField != null ? "found" : "MISSING")}, obstacleList {(ObstacleListField != null ? "found" : "MISSING")}, " +
                     $"associatedIntersection {(IntersectionField != null ? "found" : "MISSING")}.");
        }

        public static List<VehicleComponent>? Pool() =>
            TrafficManager.HasInstance ? (TrafficVehiclesField?.GetValue(TrafficManager.Instance) as TrafficVehicles)?.GetVehicleList() : null;

        // Every active Gley car within `radius` of `near`, as Core sees it. Components[i] is the car of into[i].
        public void ReadCars(Vector3 near, float radius, List<OtherCar> into)
        {
            into.Clear();
            Components.Clear();
            var pool = Pool();
            if (pool == null) return;
            var manager = TrafficManager.Instance;
            foreach (var v in pool)
            {
                if (v == null || !v.gameObject.activeInHierarchy || v.rb == null || v.frontTrigger == null) continue;
                var front = v.frontTrigger.position;
                var d = front - near;
                d.y = 0f;
                if (d.sqrMagnitude > radius * radius) continue;
                var forward = Vector3.ProjectOnPlane(v.transform.forward, Vector3.up).normalized;
                var length = v.length > 0.5f ? v.length : 4.5f;
                var middle = front - forward * (length / 2f);
                var vel = v.rb.velocity;
                var target = -1;
                try { target = manager.GetTargetWaypoint(v.GetIndex())?.listIndex ?? -1; }
                catch (Exception) { }
                into.Add(new OtherCar
                {
                    Id = v.GetIndex(), X = middle.x, Z = middle.z, Heading = Mathf.Atan2(forward.x, forward.z), Vx = vel.x, Vz = vel.z,
                    Length = length, Width = v.colliderWidth > 0.5f ? v.colliderWidth : 1.9f, TriggerLength = TriggerLength(v),
                    Target = target, StoppedForUs = HoldsUs(v), MayChangeLane = MayChangeLane(v),
                });
                Components.Add(v);
            }
        }

        private static bool MayChangeLane(VehicleComponent v)
        {
            var action = v.GetCurrentAction();
            return action == SpecialDriveActionTypes.Follow || action == SpecialDriveActionTypes.Overtake;
        }

        // Whether the ride car's collider is in this car's obstacleList: what its front trigger stops it for.
        private bool HoldsUs(VehicleComponent v)
        {
            if (!(ObstacleListField?.GetValue(v) is List<Collider> list)) return false;
            for (var i = 0; i < list.Count; i++) if (list[i] != null && rideColliders.Contains(list[i])) return true;
            return false;
        }

        // How far the trigger box reaches ahead of the front (the box is frontTrigger's first child).
        private static readonly Dictionary<GameObject, float> triggerLengths = new Dictionary<GameObject, float>();

        private static float TriggerLength(VehicleComponent v)
        {
            var key = v.gameObject;
            if (triggerLengths.TryGetValue(key, out var known)) return known;
            var length = 3f;
            try
            {
                if (v.frontTrigger.childCount > 0 && v.frontTrigger.GetChild(0).GetComponent<BoxCollider>() is BoxCollider box)
                {
                    var far = box.transform.TransformPoint(box.center + Vector3.forward * box.size.z / 2f);
                    length = Mathf.Max(0.5f, Vector3.Dot(far - v.frontTrigger.position, v.frontTrigger.forward));
                }
            }
            catch (Exception) { }
            triggerLengths[key] = length;
            return length;
        }

        public static Waypoint? At(int waypoint)
        {
            var all = GleyGraph.Waypoints;
            return all != null && waypoint >= 0 && waypoint < all.Length ? all[waypoint] : null;
        }

        public bool Stop(int waypoint) => At(waypoint)?.stop ?? false;

        public bool GiveWayEntry(int waypoint, out int junction, out int road)
        {
            junction = road = -1;
            var w = At(waypoint);
            if (w == null || !w.giveWay || !w.enter) return false;
            (junction, road) = JunctionOf(waypoint);
            return junction >= 0;
        }

        // The intersection a waypoint belongs to, numbered for Core, and the road it enters by (its group in the
        // intersection's enter or stop waypoints), -1 when there is none.
        public (int Junction, int Road) JunctionOf(int waypoint)
        {
            if (cache.TryGetValue(waypoint, out var known)) return known;
            var result = (-1, -1);
            var w = At(waypoint);
            if (w != null && IntersectionField?.GetValue(w) is IIntersection x)
            {
                if (!ids.TryGetValue(x, out var id))
                {
                    id = byId.Count;
                    ids[x] = id;
                    byId.Add(x);
                }
                result = (id, RoadIn(x, waypoint));
            }
            cache[waypoint] = result;
            return result;
        }

        private static int RoadIn(IIntersection x, int waypoint)
        {
            List<IntersectionStopWaypointsIndex>? roads = x is PriorityIntersection p ? p.enterWaypoints : x is TrafficLightsIntersection l ? l.stopWaypoints : null;
            if (roads == null) return -1;
            for (var r = 0; r < roads.Count; r++) if (roads[r]?.roadWaypoints != null && roads[r].roadWaypoints.Contains(waypoint)) return r;
            return -1;
        }

        public IIntersection? Intersection(int junction) => junction >= 0 && junction < byId.Count ? byId[junction] : null;

        // Gley's own answer for a give-way waypoint at an intersection. Asking counts as a request for the road, as a
        // waiting Gley car's does (PriorityIntersection.IsPathFree).
        public static bool? CanChange(int waypoint)
        {
            var w = At(waypoint);
            if (w == null || !w.IsInIntersection()) return null;
            try { return w.CanChange(); }
            catch (Exception ex) { Log.Error("CanChange: " + ex.Message); return null; }
        }

        // Counts the ride car in, or out of, an intersection, once each way.
        public void Apply(JunctionEvent e)
        {
            var x = Intersection(e.Junction);
            if (x == null) return;
            try
            {
                if (e.Kind == JunctionEventKind.Enter) { if (entered.Add(e.Junction)) x.VehicleEnter(-1); }
                else if (entered.Remove(e.Junction)) x.VehicleLeft(-1);
            }
            catch (Exception ex) { Log.Error($"Junction {e}: {ex.Message}"); }
        }

        // Out of every intersection: at the end of the drive and on any exit.
        public void LeaveAll()
        {
            foreach (var j in new List<int>(entered)) Apply(new JunctionEvent { Kind = JunctionEventKind.Leave, Junction = j });
        }

        public int Inside => entered.Count;
    }
}
