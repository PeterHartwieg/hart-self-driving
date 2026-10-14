using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;
using HarmonyLib;
using UnityEngine;

namespace DrivenRides
{
    // The stretches of lane a ride never drives: the bridge skip (Ride.OnBridge, as the game's SkipBridgeHelper) moves
    // the car from a bridge trigger to the trigger's far-side target facing its way. The planner leaves their steps
    // unchecked (0.1.90: a 1.6 km taxi ride across the bridge got no plan, its bridge lanes blocked on
    // 'BridgeMiddleRoadHiderColliders', solid colliders across the bridge's middle that the skip jumps). Worked out from
    // the same triggers and targets the ride uses: from each lane waypoint inside a trigger along the lane to the first
    // waypoint within LandWithin of a target facing the lane's way, at least MinSkipMetres on (Ride's own limits).
    internal static class Bridges
    {
        private static readonly System.Reflection.FieldInfo? SkipTargetsField = AccessTools.Field(typeof(Streets.BridgeTriggerController), "skipTargets");
        public const float LandWithin = 40f;
        public const float MinSkipMetres = 200f;
        private const float MaxSkipMetres = 4000f;
        private static HashSet<int>? skipped;
        private static object? builtFor;

        // Whether the step from this waypoint on is jumped by a bridge skip.
        public static bool Skips(int waypoint) => Skipped().Contains(waypoint);

        private static HashSet<int> Skipped()
        {
            var graph = GleyGraph.Graph;
            if (skipped != null && ReferenceEquals(builtFor, graph)) return skipped;
            skipped = new HashSet<int>();
            builtFor = graph;
            if (graph == null) return skipped;
            try
            {
                foreach (var bridge in UnityEngine.Object.FindObjectsOfType<Streets.BridgeTriggerController>(true))
                {
                    if (bridge == null || !(bridge.GetComponent<Collider>() is Collider trigger)) continue;
                    if (!(SkipTargetsField?.GetValue(bridge) is Transform[] targets) || targets.Length == 0) continue;
                    var box = trigger.bounds;
                    foreach (var w in graph.Near(GleyGraph.ToVec(box.center), box.extents.magnitude + 5f))
                        if (box.Contains(ToVector(graph.Position(w)))) Follow(graph, w, targets);
                }
            }
            catch (Exception ex) { Log.Error("Bridges: " + ex.Message); }
            Log.Info($"Bridges: {skipped.Count} lane waypoints on bridge skips, left out of the planner's checks.");
            return skipped;
        }

        // Along the lane from an entry waypoint (the first successor each time) to where the skip lands.
        private static void Follow(LaneGraph graph, int entry, Transform[] targets)
        {
            var chain = new List<int> { entry };
            var gone = 0f;
            var at = entry;
            while (gone < MaxSkipMetres)
            {
                var next = graph.Successors(at);
                if (next.Count == 0) return;
                var n = next[0];
                var a = graph.Position(at);
                var b = graph.Position(n);
                gone += Vec3.HorizontalDistance(a, b);
                if (gone >= MinSkipMetres)
                    foreach (var t in targets)
                    {
                        if (t == null) continue;
                        var heading = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
                        if (Vector3.Distance(ToVector(b), t.position) < LandWithin && Vector3.Dot(heading, t.forward) > 0f)
                        {
                            foreach (var w in chain) skipped!.Add(w);
                            return;
                        }
                    }
                chain.Add(n);
                at = n;
            }
        }

        private static Vector3 ToVector(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
