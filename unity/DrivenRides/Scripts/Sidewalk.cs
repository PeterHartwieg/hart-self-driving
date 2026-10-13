#nullable enable
using System;
using System.Linq;
using DrivenRides.Core.Routing;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace DrivenRides
{
    // Where the pavement is. Road and pavement are one flat ground collider with no height step (GroundPlane/Ground
    // in Manhattan, DrivingPlane/Ground in the Hamptons), and the NavMesh marks both as Walkable; only the filter the
    // game's pedestrians walk with (Helpers.NavMeshHelper.NpcNavMeshFilter) finds pavement and not road (rig command
    // measure ground, NwhMeasure.Ground, 13 October 2026: road -8..+2 m, pavement +3..+8.5 m from a lane centre in
    // Manhattan). The rig's judge and the planner's check ask it for each footprint corner.
    internal static class Sidewalk
    {
        private static NavMeshQueryFilter? filter;
        private static bool looked;
        // As the probe sampled: within 0.4 m of the point.
        private const float SampleMetres = 0.4f;

        public static bool Available => Filter != null;

        private static NavMeshQueryFilter? Filter
        {
            get
            {
                if (looked) return filter;
                looked = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? t = null;
                    try { t = asm.GetTypes().FirstOrDefault(x => x.Name == "NavMeshHelper"); } catch { }
                    if (t == null) continue;
                    var f = AccessTools.Field(t, "NpcNavMeshFilter");
                    var p = AccessTools.Property(t, "NpcNavMeshFilter");
                    try
                    {
                        if ((f != null ? f.GetValue(null) : p?.GetValue(null)) is NavMeshQueryFilter q) { filter = q; break; }
                    }
                    catch (Exception ex) { Log.Error("Pavement filter: " + ex.Message); }
                }
                Log.Info(filter != null ? $"Pavement: the pedestrians' NavMesh filter (areaMask {filter.Value.areaMask:X}, agent {filter.Value.agentTypeID})." : "Pavement: the pedestrians' NavMesh filter was not found; the kerb estimate stands in.");
                return filter;
            }
        }

        // Whether a point is pedestrian ground.
        public static bool At(Vec3 p) => Filter is NavMeshQueryFilter q && NavMesh.SamplePosition(new Vector3(p.X, p.Y, p.Z), out _, SampleMetres, q);
    }
}
