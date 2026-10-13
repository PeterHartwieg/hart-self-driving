#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DrivenRides.Core.Routing;
using GleyTrafficSystem;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DrivenRides
{
    // Spike debug keys on Ctrl+Alt+Shift with letters the game doesn't bind (docs/implementation-plan.md).
    internal sealed class DebugKeys : MonoBehaviour
    {
        private static readonly (Key Key, string Label, Action Action)[] Keys =
        {
            (Key.G, "graph stats", GraphStats),
            (Key.X, "clear a stuck ride", Ride.DebugClear),
            (Key.K, "remove the ride car", Ride.DebugRemoveCar),
            (Key.V, "hospital now", Hospital),
            (Key.U, "throw in the ride's frame update", Ride.DebugThrow),
            (Key.J, "fake a save change", Ride.DebugFakeSaveChange),
            (Key.L, "park a dressed taxi beside you", () => NwhDress.SpawnBeside("taxi")),
        };

        private void OnEnable() =>
            Log.Info("Debug keys ready: " + string.Join(", ", Keys.Select(k => $"Ctrl+Alt+Shift+{k.Key} {k.Label}")) + ".");

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || SaveGameManager.Current == null || GameManager.ShouldBlockKeyboardShortcuts()) return;
            if (!keyboard.ctrlKey.isPressed || !keyboard.altKey.isPressed || !keyboard.shiftKey.isPressed) return;
            foreach (var k in Keys)
            {
                if (!keyboard[k.Key].wasPressedThisFrame) continue;
                Log.Info("Debug key: " + k.Label + ".");
                try { k.Action(); }
                catch (Exception ex) { Log.Error("Debug key " + k.Label + ": " + ex); }
            }
        }

        private static void Hospital()
        {
            var manager = InstanceBehavior<GameManager>.Instance;
            manager.StartCoroutine(manager.HospitalRespawn());
        }

        // Waypoint count, weak components, what the traffic pool holds, and planning times for ten routes.
        private static void GraphStats()
        {
            var graph = GleyGraph.Graph;
            if (graph == null) { Notice.Show("No lane graph yet."); return; }
            var component = new int[graph.Count];
            var sizes = new List<int>();
            for (var i = 0; i < graph.Count; i++)
            {
                if (component[i] != 0) continue;
                var id = sizes.Count + 1;
                var size = 0;
                var stack = new Stack<int>();
                stack.Push(i);
                component[i] = id;
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    size++;
                    foreach (var m in graph.Successors(n)) if (component[m] == 0) { component[m] = id; stack.Push(m); }
                    foreach (var m in graph.Predecessors(n)) if (component[m] == 0) { component[m] = id; stack.Push(m); }
                }
                sizes.Add(size);
            }
            sizes.Sort((a, b) => b.CompareTo(a));
            Log.Info($"Graph: {graph.Count} waypoints, {sizes.Count} weak components, largest {string.Join(", ", sizes.Take(5))}.");

            var pool = AccessPool();
            Log.Info("Traffic pool: " + (pool == null ? "unreadable" : string.Join(", ", pool
                .Where(v => v != null && v.prefab != null)
                .GroupBy(v => $"{v.prefab.name} ({v.vehicleType})")
                .Select(g => $"{g.Key} x{g.Count()} ({g.Count(v => v.gameObject.activeInHierarchy)} active)"))));

            var player = InstanceBehavior<GameManager>.Instance.playerController.transform.position;
            var start = graph.Nearest(GleyGraph.ToVec(player), 100f, i => graph.Successors(i).Count > 0);
            if (start < 0) { Log.Info("Planning test: no waypoint within 100 m of the player."); return; }
            var random = new System.Random(start);
            var tested = 0;
            for (var attempt = 0; attempt < 400 && tested < 10; attempt++)
            {
                var goal = random.Next(graph.Count);
                var distance = Vec3.HorizontalDistance(graph.Position(start), graph.Position(goal));
                if (distance < 1000f || distance > 4000f || component[goal] != component[start]) continue;
                tested++;
                var watch = Stopwatch.StartNew();
                var job = RoutePlanner.Plan(graph, new RouteRequest
                {
                    Start = start,
                    Goals = new[] { new Goal(goal, 0f) },
                    VehicleType = (int)VehicleTypes.Car,
                    IsDisabled = GleyGraph.IsDisabled,
                }, int.MaxValue);
                Log.Info($"Planning test {tested}: {distance:0} m straight, {job.Status}, {job.Result?.LengthMetres ?? 0:0} m route, {job.Expanded} expanded, {watch.ElapsedMilliseconds} ms.");
            }
            Notice.Show("Graph stats written to the log.");
        }

        private static List<VehicleComponent>? AccessPool()
        {
            var field = HarmonyLib.AccessTools.Field(typeof(TrafficManager), "trafficVehicles");
            return (field?.GetValue(TrafficManager.Instance) as TrafficVehicles)?.GetVehicleList();
        }
    }
}
