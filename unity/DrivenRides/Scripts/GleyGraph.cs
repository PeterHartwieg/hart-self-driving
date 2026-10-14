#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using DrivenRides.Core.Routing;
using GleyTrafficSystem;
using GleyUrbanAssets;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // A snapshot of Gley's waypoint graph for the route planner, built once per city scene. Live state
    // (temporaryDisabled) is read from the waypoints during each search, so the snapshot never goes stale.
    internal static class GleyGraph
    {
        private static CurrentSceneData? scene;
        private static LaneGraph? graph;
        private static Waypoint[]? waypoints;

        public static LaneGraph? Graph => Ensure() ? graph : null;
        public static Waypoint[]? Waypoints => Ensure() ? waypoints : null;

        // Steps found blocked in the world (a barricade, a median), kept for the session: they don't move.
        private static readonly HashSet<(int, int)> blockedSteps = new HashSet<(int, int)>();
        public static bool IsBlockedStep(int from, int to) => blockedSteps.Contains((from, to));
        public static void BlockStep(int from, int to) => blockedSteps.Add((from, to));

        public static void Drop()
        {
            blockedSteps.Clear();
            scene = null;
            graph = null;
            waypoints = null;
        }

        public static bool IsDisabled(int index)
        {
            var all = waypoints;
            return all == null || index < 0 || index >= all.Length || all[index] == null || all[index].temporaryDisabled;
        }

        public static Vec3 ToVec(Vector3 v) => new Vec3(v.x, v.y, v.z);

        // Test mode (TestMode) writes the lane graph once per graph size to the game's data folder, for route analysis away
        // from the game: <LocalLow>/Hovgaard Games/Big Ambitions/DrivenRides/lane-graph-<count>.json.
        public static string DataFolder => System.IO.Path.Combine(Application.persistentDataPath, "DrivenRides");

        private static void Dump(Waypoint[] all, int[][] otherLanes)
        {
            try
            {
                System.IO.Directory.CreateDirectory(DataFolder);
                var path = System.IO.Path.Combine(DataFolder, $"lane-graph-{all.Length}.json");
                if (System.IO.File.Exists(path)) return;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder("[\n");
                for (var i = 0; i < all.Length; i++)
                {
                    var w = all[i];
                    if (w == null) { sb.Append("null,\n"); continue; }
                    sb.Append("{\"p\":[").Append(w.position.x.ToString("0.##", inv)).Append(',').Append(w.position.y.ToString("0.##", inv)).Append(',').Append(w.position.z.ToString("0.##", inv))
                      .Append("],\"n\":[").Append(string.Join(",", w.neighbors ?? new List<int>()))
                      .Append("],\"o\":[").Append(string.Join(",", otherLanes[i]))
                      .Append("],\"a\":[").Append(string.Join(",", w.allowedAgents ?? new List<int>()))
                      .Append("],\"x\":").Append(TrafficManager.IsNotIntersection(w) ? 0 : 1)
                      .Append(",\"s\":").Append(w.maxSpeed)
                      .Append(i == all.Length - 1 ? "}\n" : "},\n");
                }
                sb.Append("]\n");
                System.IO.File.WriteAllText(path, sb.ToString());
                Log.Trace("Lane graph written to " + path + ".");
            }
            catch (Exception ex) { Log.Error("Lane graph dump: " + ex.Message); }
        }

        // One line per accepted ride (rides.jsonl beside the graph): start, goals and route, for the same analysis.
        public static void DumpRide(int start, IReadOnlyList<Goal> goals, IReadOnlyList<int> route, Vector3 destination)
        {
            try
            {
                System.IO.Directory.CreateDirectory(DataFolder);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var line = $"{{\"start\":{start},\"dest\":[{destination.x.ToString("0.##", inv)},{destination.y.ToString("0.##", inv)},{destination.z.ToString("0.##", inv)}]," +
                           $"\"goals\":[{string.Join(",", System.Linq.Enumerable.Select(goals, g => g.Waypoint))}],\"route\":[{string.Join(",", route)}]}}\n";
                System.IO.File.AppendAllText(System.IO.Path.Combine(DataFolder, "rides.jsonl"), line);
            }
            catch (Exception ex) { Log.Error("Ride dump: " + ex.Message); }
        }

        // Why a plan found no route: what the start leads to, and how much of the graph and how many goals are
        // reachable with no filters, with only the disabled filter, and with only the vehicle filter.
        public static string Diagnose(LaneGraph g, int start, IReadOnlyList<Goal> goals, int vehicleType)
        {
            var goalSet = new HashSet<int>();
            foreach (var goal in goals) goalSet.Add(goal.Waypoint);
            var disabled = 0;
            for (var i = 0; i < g.Count; i++) if (IsDisabled(i)) disabled++;
            string Reach(Func<int, bool> blocked, bool sideways = false)
            {
                var seen = new bool[g.Count];
                var queue = new Queue<int>();
                seen[start] = true;
                queue.Enqueue(start);
                int count = 0, goalsReached = 0;
                while (queue.Count > 0)
                {
                    var n = queue.Dequeue();
                    count++;
                    if (goalSet.Contains(n)) goalsReached++;
                    foreach (var m in g.Successors(n))
                        if (!seen[m] && !blocked(m)) { seen[m] = true; queue.Enqueue(m); }
                    if (sideways)
                        foreach (var m in g.Lateral(n))
                            if (!seen[m] && !blocked(m)) { seen[m] = true; queue.Enqueue(m); }
                }
                return $"{count} waypoints, {goalsReached}/{goalSet.Count} goals";
            }
            var successors = g.Successors(start);
            var p = g.Position(start);
            return $"start {start} at ({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}) (mask {MaskOf(g, start)}, {successors.Count} successors: {string.Join(" ", successors)}, {g.Lateral(start).Count} lane changes), vehicle type {vehicleType}, " +
                   $"{disabled} waypoints disabled; reachable along lanes: {Reach(_ => false)}; with lane changes: {Reach(_ => false, true)}; " +
                   $"disabled filter only: {Reach(IsDisabled, true)}";
        }

        private static string MaskOf(LaneGraph g, int i)
        {
            var bits = new List<string>();
            for (var t = 0; t < 4; t++) if (g.Allows(i, t)) bits.Add(t.ToString());
            return bits.Count == 0 ? "none" : string.Join("+", bits);
        }

        private static bool Ensure()
        {
            // Called every frame during a ride, so the scene object is cached. Gley's own GetSceneInstance runs
            // FindObjectsOfType over the whole scene each call (and creates an object if none is found).
            if (graph != null && scene != null) return true;
            var current = Object.FindObjectOfType<CurrentSceneData>();
            if (current == null || current.allWaypoints == null || current.allWaypoints.Length == 0) return false;
            if (graph != null && ReferenceEquals(current, scene)) return true;

            var watch = Stopwatch.StartNew();
            var all = current.allWaypoints;
            var n = all.Length;
            var positions = new Vec3[n];
            var neighbors = new int[n][];
            var otherLanes = new int[n][];
            var allowed = new int[n];
            var intersection = new bool[n];
            var speed = new float[n];
            var misplaced = 0;
            var noType = 0;
            var listsDiffer = 0;
            for (var i = 0; i < n; i++)
            {
                var w = all[i];
                if (w == null) { neighbors[i] = Array.Empty<int>(); otherLanes[i] = Array.Empty<int>(); continue; }
                if (w.listIndex != i) misplaced++;
                positions[i] = ToVec(w.position);
                neighbors[i] = w.neighbors != null ? w.neighbors.ToArray() : Array.Empty<int>();
                otherLanes[i] = w.otherLanes != null ? w.otherLanes.ToArray() : Array.Empty<int>();
                // Gley's AI reads allowedAgents; allowedCars is the same list as an enum. Both are read, in case the
                // scene data filled only one of them.
                var agents = 0;
                if (w.allowedAgents != null)
                    foreach (var type in w.allowedAgents) if (type >= 0 && type < 32) agents |= 1 << type;
                var cars = 0;
                if (w.allowedCars != null)
                    foreach (var type in w.allowedCars) cars |= 1 << (int)type;
                if (agents != cars) listsDiffer++;
                allowed[i] = agents | cars;
                if (allowed[i] == 0) noType++;
                intersection[i] = !TrafficManager.IsNotIntersection(w);
                speed[i] = w.maxSpeed;
            }
            graph = new LaneGraph(positions, neighbors, allowed, intersection, speed, otherLanes: otherLanes, inferLanes: true, inferTurns: true);
            waypoints = all;
            scene = current;
            Log.Trace($"Lane graph: {n} waypoints, {graph.DroppedEdges} bad edges dropped, {misplaced} with listIndex not their position, {noType} allowing no vehicle type, {listsDiffer} where allowedAgents and allowedCars differ, {graph.GivenLaneChanges} with Gley lane changes, {graph.InferredLaneChanges} with inferred ones, {graph.InferredTurns} junction entries with added turns, {graph.InferredUTurns} with U-turns, top speed {graph.TopSpeedMs * 3.6f:0} km/h, built in {watch.ElapsedMilliseconds} ms.");
            if (TestMode.On) Dump(all, otherLanes);
            return true;
        }
    }
}
