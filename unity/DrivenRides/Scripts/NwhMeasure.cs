#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using GleyTrafficSystem;
using GleyUrbanAssets;
using HarmonyLib;
using UnityEngine;

namespace DrivenRides
{
    // Phase 1 of NWH driving (docs/implementation-plan.md, "Phases"): the five things the traffic layers rely on,
    // measured in one go with the rig's `measure` command, one log line each. Nothing here changes the game for longer
    // than the light test, which counts a car with index -1 in one intersection and takes it out again.
    internal static class NwhMeasure
    {
        private static readonly FieldInfo? TrafficVehiclesField = AccessTools.Field(typeof(TrafficManager), "trafficVehicles");
        private static readonly FieldInfo? IntersectionManagerField = AccessTools.Field(typeof(TrafficManager), "intersectionManager");
        private static readonly FieldInfo? ActiveIntersectionsField = AccessTools.Field(typeof(IntersectionManager), "activeIntersections");
        private static readonly FieldInfo? AssociatedField = AccessTools.Field(typeof(Waypoint), "associatedIntersection");
        private static readonly FieldInfo? CarsInField = AccessTools.Field(typeof(GenericIntersection), "carsInIntersection");
        private static readonly FieldInfo? CurrentRoadField = AccessTools.Field(typeof(TrafficLightsIntersection), "currentRoad");
        private static readonly FieldInfo? YellowField = AccessTools.Field(typeof(TrafficLightsIntersection), "yellowLight");
        private static readonly FieldInfo? ObstacleListField = AccessTools.Field(typeof(VehicleComponent), "obstacleList");

        public static IEnumerator Run(Vector3 near)
        {
            Log.Info("NWH measure: start.");
            Try("layers", Layers);
            Try("give way", () => GiveWay(near));
            Try("obstacle lists", ObstacleLists);
            var cost = Cost();
            while (cost.MoveNext()) yield return cost.Current;
            var light = Light(near);
            while (true)
            {
                object? step;
                try { if (!light.MoveNext()) break; step = light.Current; }
                catch (Exception ex) { Log.Error("NWH measure, light: " + ex); break; }
                yield return step;
            }
            Log.Info("NWH measure: done.");
        }

        private static void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Error($"NWH measure, {what}: {ex}"); }
        }

        private static List<VehicleComponent> Pool() =>
            (TrafficVehiclesField?.GetValue(TrafficManager.Instance) as TrafficVehicles)?.GetVehicleList() ?? new List<VehicleComponent>();

        // 1. Which of the layers that matter collide with each other (Physics.GetIgnoreLayerCollision).
        private static void Layers()
        {
            var gley = Pool().FirstOrDefault(v => v != null)?.gameObject.layer ?? -1;
            var names = new List<(string, int)>();
            foreach (var n in new[] { "Default", "Vehicles", "PlayerVehicles", "ParkedVehicles", "Human", "Player", "Roads", "Obstacles" })
            {
                var l = LayerMask.NameToLayer(n);
                if (l >= 0) names.Add((n, l));
            }
            if (gley >= 0 && names.All(p => p.Item2 != gley)) names.Add((LayerMask.LayerToName(gley) + " (Gley cars)", gley));
            var rows = new List<string>();
            foreach (var (a, la) in names)
                rows.Add(a + ": " + string.Join(" ", names.Select(p => (Physics.GetIgnoreLayerCollision(la, p.Item2) ? "-" : "x") + p.Item1)));
            Log.Info($"NWH measure, layers (x collides, - ignores; Gley cars on {(gley >= 0 ? LayerMask.LayerToName(gley) : "?")}): " + string.Join(" | ", rows));
        }

        private static IIntersection? IntersectionOf(Waypoint w) => AssociatedField?.GetValue(w) as IIntersection;

        // 2. Give-way waypoints: whether CanChange() answers from outside Gley, on the nearest few.
        private static void GiveWay(Vector3 near)
        {
            var all = GleyGraph.Waypoints;
            if (all == null) { Log.Info("NWH measure, give way: no waypoints."); return; }
            var found = all.Where(w => w != null && w.giveWay && IntersectionOf(w) != null).OrderBy(w => (w.position - near).sqrMagnitude).Take(5).ToList();
            var parts = found.Select(w =>
            {
                try { return $"{w.listIndex} at {(w.position - near).magnitude:0} m, {IntersectionOf(w)!.GetType().Name}: {w.CanChange()}"; }
                catch (Exception ex) { return $"{w.listIndex}: {ex.GetType().Name} {ex.Message}"; }
            });
            Log.Info($"NWH measure, give way: {all.Count(w => w != null && w.giveWay)} give-way waypoints, {all.Count(w => w != null && w.giveWay && IntersectionOf(w) != null)} in an intersection; nearest: {string.Join("; ", parts)}.");
        }

        // 3. Whether a Gley car's obstacleList (what its front trigger holds) is readable, and what is in them now.
        private static void ObstacleLists()
        {
            var active = Pool().Where(v => v != null && v.gameObject.activeInHierarchy).ToList();
            var withList = 0;
            var samples = new List<string>();
            foreach (var v in active)
            {
                if (!(ObstacleListField?.GetValue(v) is List<Collider> list)) continue;
                withList++;
                if (list.Count > 0 && samples.Count < 5)
                    samples.Add($"{v.name} ({v.GetCurrentAction()}): {string.Join(", ", list.Where(c => c != null).Select(c => $"{c.name} [{LayerMask.LayerToName(c.gameObject.layer)}]"))}");
            }
            Log.Info($"NWH measure, obstacle lists: {withList} of {active.Count} active cars readable; holding something: {(samples.Count > 0 ? string.Join("; ", samples) : "none")}.");
        }

        // 4. The cost of reading every active Gley car within 60 m each physics step: position, velocity, target, action,
        // obstacle list. 250 steps.
        private static IEnumerator Cost()
        {
            var times = new List<double>();
            var counts = new List<int>();
            var watch = new Stopwatch();
            for (var step = 0; step < 250; step++)
            {
                yield return new WaitForFixedUpdate();
                try
                {
                    watch.Restart();
                    var near = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                    var n = 0;
                    var sum = 0f;
                    foreach (var v in Pool())
                    {
                        if (v == null || !v.gameObject.activeInHierarchy || v.rb == null) continue;
                        var p = v.rb.position;
                        if ((p - near).sqrMagnitude > 3600f) continue;
                        var vel = v.rb.velocity;
                        var index = v.GetIndex();
                        var target = index >= 0 ? TrafficManager.Instance.GetTargetWaypoint(index) : null;
                        var action = v.GetCurrentAction();
                        var list = ObstacleListField?.GetValue(v) as List<Collider>;
                        sum += vel.x + (target != null ? target.listIndex : 0) + (int)action + (list?.Count ?? 0);
                        n++;
                    }
                    watch.Stop();
                    times.Add(watch.Elapsed.TotalMilliseconds * 1000.0);
                    counts.Add(n);
                }
                catch (Exception ex) { Log.Error("NWH measure, cost: " + ex.Message); yield break; }
            }
            times.Sort();
            Log.Info($"NWH measure, cost per physics step: median {times[times.Count / 2]:0} µs, 95% {times[times.Count * 95 / 100]:0} µs, max {times[times.Count - 1]:0} µs, for {counts.Average():0.0} cars within 60 m (of {Pool().Count(v => v != null && v.gameObject.activeInHierarchy)} active).");
        }

        // 5. Whether counting a car with index -1 in the nearest active traffic light holds it on yellow, and whether
        // taking it out lets the light go on.
        private static IEnumerator Light(Vector3 near)
        {
            var manager = IntersectionManagerField?.GetValue(TrafficManager.Instance);
            var active = manager != null ? ActiveIntersectionsField?.GetValue(manager) as List<GenericIntersection> : null;
            var all = GleyGraph.Waypoints;
            TrafficLightsIntersection? light = null;
            var best = float.MaxValue;
            if (active != null && all != null)
                foreach (var i in active.OfType<TrafficLightsIntersection>())
                {
                    var first = i.stopWaypoints?.SelectMany(s => s.roadWaypoints).FirstOrDefault() ?? -1;
                    if (first < 0 || first >= all.Length || all[first] == null) continue;
                    var d = (all[first].position - near).sqrMagnitude;
                    if (d < best) { best = d; light = i; }
                }
            if (light == null) { Log.Info($"NWH measure, light: no active traffic light ({active?.Count ?? -1} active intersections)."); yield break; }
            int Road() => CurrentRoadField?.GetValue(light) is int r ? r : -1;
            bool Yellow() => YellowField?.GetValue(light) is bool y && y;
            int Inside() => (CarsInField?.GetValue(light) as List<int>)?.Count ?? -1;
            Log.Info($"NWH measure, light: {light.name} at {Mathf.Sqrt(best):0} m, green {light.greenLightTime:0.0} s, yellow {light.yellowLightTime:0.0} s, road {Road()}, yellow {Yellow()}, cars inside {Inside()}.");
            ((IIntersection)light).VehicleEnter(-1);
            var held = Time.realtimeSinceStartup;
            var startRoad = Road();
            var changed = false;
            var sawYellow = false;
            var hold = light.greenLightTime + light.yellowLightTime + 10f;
            while (Time.realtimeSinceStartup - held < hold)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                sawYellow |= Yellow();
                if (Road() != startRoad) { changed = true; break; }
            }
            Log.Info($"NWH measure, light with -1 inside for {Time.realtimeSinceStartup - held:0} s: {(changed ? "it changed road anyway" : sawYellow ? "held on yellow" : "never reached yellow")} (cars inside {Inside()}).");
            ((IIntersection)light).VehicleLeft(-1);
            var left = Time.realtimeSinceStartup;
            var road = Road();
            while (Time.realtimeSinceStartup - left < light.yellowLightTime + light.greenLightTime + 5f)
            {
                yield return new WaitForSecondsRealtime(0.25f);
                if (Road() != road) break;
            }
            Log.Info($"NWH measure, light after -1 left: {(Road() != road ? $"changed road {Time.realtimeSinceStartup - left:0.0} s later" : "did not change")} (cars inside {Inside()}).");
        }
    }
}
