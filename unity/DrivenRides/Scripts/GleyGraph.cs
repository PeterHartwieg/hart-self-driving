#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using DrivenRides.Core.Routing;
using GleyTrafficSystem;
using GleyUrbanAssets;
using UnityEngine;

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

        public static void Drop()
        {
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

        private static bool Ensure()
        {
            var current = CurrentSceneData.GetSceneInstance();
            if (current == null || current.allWaypoints == null || current.allWaypoints.Length == 0) return false;
            if (graph != null && ReferenceEquals(current, scene)) return true;

            var watch = Stopwatch.StartNew();
            var all = current.allWaypoints;
            var n = all.Length;
            var positions = new Vec3[n];
            var neighbors = new int[n][];
            var allowed = new int[n];
            var intersection = new bool[n];
            var speed = new float[n];
            var misplaced = 0;
            for (var i = 0; i < n; i++)
            {
                var w = all[i];
                if (w == null) { neighbors[i] = Array.Empty<int>(); continue; }
                if (w.listIndex != i) misplaced++;
                positions[i] = ToVec(w.position);
                neighbors[i] = w.neighbors != null ? w.neighbors.ToArray() : Array.Empty<int>();
                var mask = 0;
                if (w.allowedCars != null)
                    foreach (var type in w.allowedCars) mask |= 1 << (int)type;
                allowed[i] = mask;
                intersection[i] = !TrafficManager.IsNotIntersection(w);
                speed[i] = w.maxSpeed;
            }
            graph = new LaneGraph(positions, neighbors, allowed, intersection, speed);
            waypoints = all;
            scene = current;
            Log.Info($"Lane graph: {n} waypoints, {graph.DroppedEdges} bad edges dropped, {misplaced} with listIndex not their position, top speed {graph.TopSpeedMs * 3.6f:0} km/h, built in {watch.ElapsedMilliseconds} ms.");
            return true;
        }
    }
}
