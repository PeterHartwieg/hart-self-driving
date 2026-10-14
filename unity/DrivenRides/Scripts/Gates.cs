using System;
using System.Collections.Generic;
using DrivenRides.Core.Routing;
using HarmonyLib;
using UnityEngine;

namespace DrivenRides
{
    // The gates a car can drive through (the game's FenceDoor with acceptsVehicles; a gated Hamptons community's is a
    // HamptonsPrivateFenceDoor). The game opens one when any collider enters its FenceDoorTrigger, unless IsLocked():
    // a community's gate is locked unless the player owns or rents a building with unlocksHamptonsPrivateFence and the
    // same privateFenceIndex (the check TaxiSystem.TravelTo makes before a taxi ride, CanEnterPrivateFence). So the ride
    // car opens an unlocked gate itself, as the player's car does, and the planner treats its doors as open; a locked
    // gate's lanes are not driven, and a ride whose way leads through one ends before it (NwhPlanner).
    internal static class Gates
    {
        private static readonly System.Reflection.MethodInfo? IsLockedMethod = AccessTools.Method(typeof(FenceDoor), "IsLocked");

        // One gate as the planner sees it, with its door colliders.
        internal sealed class Found
        {
            public RoadGate Gate = null!;
            public FenceDoor Door = null!;
            public readonly HashSet<Collider> DoorColliders = new HashSet<Collider>();
            public List<(int From, int To)> Steps = new List<(int, int)>();
        }

        private static List<Found> current = new List<Found>();
        private static readonly Dictionary<Collider, Found> byCollider = new Dictionary<Collider, Found>();
        private static readonly Dictionary<(int, int), Found> byStep = new Dictionary<(int, int), Found>();

        public static IReadOnlyList<Found> Current => current;

        // Read again: the gates in the loaded city and whether each is locked now (the player may have bought a house).
        public static bool Read { get; private set; }

        public static void Refresh(LaneGraph graph)
        {
            Read = true;
            current = new List<Found>();
            byCollider.Clear();
            byStep.Clear();
            FenceDoor[] all;
            try { all = UnityEngine.Object.FindObjectsOfType<FenceDoor>(); }
            catch (Exception ex) { Log.Error("Gates: " + ex.Message); return; }
            foreach (var door in all)
            {
                if (door == null || !door.acceptsVehicles || door.IsForSale() || door.doors == null) continue;
                var found = new Found { Door = door };
                Bounds? box = null;
                void Grow(Bounds b) { if (box is Bounds have) { have.Encapsulate(b); box = have; } else box = b; }
                foreach (var leaf in door.doors)
                {
                    if (leaf == null) continue;
                    foreach (var c in leaf.GetComponentsInChildren<Collider>())
                        if (!c.isTrigger) { found.DoorColliders.Add(c); Grow(c.bounds); }
                }
                foreach (var trigger in door.GetComponentsInChildren<FenceDoorTrigger>())
                    if (trigger.GetComponent<Collider>() is Collider t) Grow(t.bounds);
                if (!(box is Bounds b0)) continue;
                bool locked;
                try { locked = IsLockedMethod != null && (bool)IsLockedMethod.Invoke(door, null); }
                catch (Exception ex) { Log.Error("Gates: IsLocked: " + ex.Message); locked = true; }
                found.Gate = new RoadGate { MinX = b0.min.x, MaxX = b0.max.x, MinZ = b0.min.z, MaxZ = b0.max.z, Y = b0.min.y, Locked = locked, Name = door.name };
                found.Steps = GateCrossing.Steps(graph, found.Gate);
                if (found.Steps.Count == 0) continue;
                current.Add(found);
                Log.Info($"Gate '{door.name}' ({door.GetType().Name}): {(locked ? "shut for the player" : "opens")}, doors and trigger over x {b0.min.x:0.0}..{b0.max.x:0.0}, z {b0.min.z:0.0}..{b0.max.z:0.0}, {found.Steps.Count} lane steps through it.");
                foreach (var c in found.DoorColliders) byCollider[c] = found;
                foreach (var s in found.Steps) byStep[s] = found;
            }
            var locked0 = 0;
            foreach (var f in current) if (f.Gate.Locked) locked0++;
            Log.Info($"Gates: {current.Count} across lanes, {locked0} locked for the player.");
        }

        // A door of a gate that opens for this ride: the planner counts it as open (out of the way).
        public static bool IsOpenDoor(Collider c) => byCollider.TryGetValue(c, out var f) && !f.Gate.Locked;

        // The gate a lane step goes through, or null.
        public static Found? On(int from, int to) => byStep.TryGetValue((from, to), out var f) ? f : null;

        public static bool LockedStep(int from, int to) => On(from, to) is Found f && f.Gate.Locked;
    }
}
