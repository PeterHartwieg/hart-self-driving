#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using GleyTrafficSystem;
using HarmonyLib;
using Helpers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // The traffic car that drives the player's own car (docs/own-car-self-drive.md). Gley can only drive cars in its
    // pool, so the ride borrows one: the car's own traffic twin if the pool has an idle one, else the idle pooled car
    // closest in length, dressed as the player's car. The dress is a copy of the player car's meshes, with no
    // colliders or scripts, on the borrowed car; the borrowed car's own meshes are hidden, and the copied wheels turn
    // and steer with the borrowed car's wheels. Undress before the car goes back to the pool.
    internal sealed class StandIn : MonoBehaviour
    {
        private static readonly FieldInfo? TrafficVehiclesField = AccessTools.Field(typeof(TrafficManager), "trafficVehicles");
        // StringIdExtensions.GetIdWithoutType, as PrivateDriverHelpers.GetAiVehiclePrefab names the twin; that helper
        // logs an error for a car without one (a modded car), so the lookup is done here.
        private static readonly MethodInfo? GetIdWithoutType = AccessTools.Method(AccessTools.TypeByName("StringIdExtensions"), "GetIdWithoutType", new[] { typeof(string) });
        private static readonly FieldInfo? DriverField = AccessTools.Field(typeof(CarController), "driverAppearanceSetter");

        private readonly List<Renderer> hidden = new List<Renderer>();
        private GameObject? body;
        // Each copied wheel turns with a borrowed one. Gley sets a wheel's local rotation to EulerXYZ(spin, steer, 0)
        // (UpdateWheelJob.cs:67-71), so its parent is the rest frame and steer and spin can be read back each frame.
        private sealed class CopiedWheel
        {
            public Transform Holder = null!;
            public Transform Donor = null!;
            public Transform DonorParent = null!;
            public Quaternion HolderRest;
            // The copied wheel's radius: it spins with the distance the car moves, not with the borrowed wheel, whose
            // turn per frame at 3x speed can pass half a turn and so can't be read back.
            public float Radius;
            public float Spin;
        }

        private readonly List<CopiedWheel> wheels = new List<CopiedWheel>();
        private Vector3 lastPosition;
        private bool lastPositionSet;

        // Where the dressed car's own root sits (the body copy is lined up on the borrowed car by its middle, not its
        // root): the arrival puts the real car here.
        public Vector3 BodyPosition => body != null ? body.transform.position : transform.position;

        // The prefab to borrow, and whether it needs dressing. Null when the pool has no idle car at all.
        public static (VehicleComponent Prefab, bool Dress)? Pick(CarController real, out string why)
        {
            why = "";
            var twin = TwinPrefab(real);
            if (twin != null && IdleCount(twin) > 0) { why = $"its own traffic twin {twin.name}"; return (twin, false); }
            if (!(TrafficVehiclesField?.GetValue(TrafficManager.Instance) is TrafficVehicles vehicles)) { why = "no traffic pool"; return null; }
            var length = BodyBounds(real.transform, real.GetComponentsInChildren<Renderer>()).size.z;
            VehicleComponent? best = null;
            var bestDiff = float.MaxValue;
            var seen = new HashSet<GameObject>();
            foreach (var v in vehicles.GetVehicleList())
            {
                if (v == null || v.prefab == null || v.gameObject.activeInHierarchy || !seen.Add(v.prefab)) continue;
                // Only cars stand in: a truck or bus keeps to its own lanes; a taxi stays hailable and runs its own stops.
                if (v.vehicleType != VehicleTypes.Car || v.prefab.GetComponent<TaxiController>() != null) continue;
                var diff = Mathf.Abs(v.length - length);
                if (diff < bestDiff) { bestDiff = diff; best = v.prefab.GetComponent<VehicleComponent>(); }
            }
            if (best == null) { why = "no idle car in the traffic pool"; return null; }
            why = $"{best.name} dressed as the car ({length:0.0} m, {bestDiff:0.0} m off)" + (twin == null ? ", the car has no traffic twin" : ", its twin is busy");
            return (best, true);
        }

        // The car's own traffic model (Vehicles/<id>), as the private driver uses; a modded car has none.
        private static VehicleComponent? TwinPrefab(CarController real)
        {
            if (real.vehicleType == null) return null;
            var name = real.vehicleType.vehicleTypeName ?? "";
            var id = GetIdWithoutType?.Invoke(null, new object[] { name }) as string ?? name.Substring(name.IndexOf('_') + 1);
            var asset = PrefabHelper.LoadPrefabAssetByName("Vehicles/" + id);
            return asset != null ? asset.GetComponent<VehicleComponent>() : null;
        }

        private static int IdleCount(VehicleComponent prefab)
        {
            if (!(TrafficVehiclesField?.GetValue(TrafficManager.Instance) is TrafficVehicles vehicles)) return 0;
            var n = 0;
            foreach (var v in vehicles.GetVehicleList())
                if (v != null && v.prefab == prefab.gameObject && !v.gameObject.activeInHierarchy) n++;
            return n;
        }

        public static StandIn Dress(VehicleComponent donor, CarController real)
        {
            var standIn = donor.gameObject.AddComponent<StandIn>();
            try { standIn.Build(donor, real); }
            catch { standIn.Undress(); throw; }
            return standIn;
        }

        private void Build(VehicleComponent donor, CarController real)
        {
            var donorRoot = donor.transform;
            var realRoot = real.transform;
            var donorWheels = new List<Transform>();
            var donorRadius = new Dictionary<Transform, float>();
            foreach (var w in donor.allWheels)
                if (w?.wheelGraphics != null) { donorWheels.Add(w.wheelGraphics); donorRadius[w.wheelGraphics] = w.wheelRadius; }

            var realWheels = new List<Transform>();
            // The wheel controller's frame is the wheel's rest: NWH sets the visual to the controller's rotation times
            // steering, camber and spin (WheelController.cs:937-951).
            var restOf = new Dictionary<Transform, Transform>();
            var radiusOf = new Dictionary<Transform, float>();
            var realWheelParts = new HashSet<Transform>();
            if (real.vehicleController?.powertrain?.wheels != null)
                foreach (var w in real.vehicleController.powertrain.wheels)
                {
                    var visual = w?.wheelUAPI?.WheelVisual;
                    if (visual == null) continue;
                    realWheels.Add(visual.transform);
                    restOf[visual.transform] = w!.wheelUAPI.transform;
                    radiusOf[visual.transform] = w.wheelUAPI.Radius;
                    foreach (var t in visual.GetComponentsInChildren<Transform>(true)) realWheelParts.Add(t);
                    var fixedPart = w!.wheelUAPI.NonRotatingVisual;
                    if (fixedPart != null) foreach (var t in fixedPart.GetComponentsInChildren<Transform>(true)) realWheelParts.Add(t);
                }

            // The player's figure and the hired driver's sit in the car while the player is in it; neither is copied.
            var leaveOut = new List<Transform>();
            var figure = InstanceBehavior<GameManager>.Instance?.playerController?.Character;
            if (figure != null) leaveOut.Add(figure.transform);
            if (DriverField?.GetValue(real) is Component driver && driver != null) leaveOut.Add(driver.transform);
            var realRenderers = VisibleRenderers(real.gameObject);
            realRenderers.RemoveAll(r => leaveOut.Exists(t => r.transform.IsChildOf(t)));
            var donorRenderers = donor.GetComponentsInChildren<Renderer>(true);
            // Line the body copy up on the borrowed car: the same ground line and the same middle.
            var realBounds = BodyBounds(realRoot, realRenderers);
            var donorBounds = BodyBounds(donorRoot, donorRenderers);
            var shift = new Vector3(donorBounds.center.x - realBounds.center.x, donorBounds.min.y - realBounds.min.y, donorBounds.center.z - realBounds.center.z);

            body = new GameObject("DrivenRides.Body");
            body.transform.SetParent(donorRoot, false);
            body.transform.localPosition = shift;
            body.transform.localScale = Divide(realRoot.lossyScale, donorRoot.lossyScale);
            body.layer = donor.gameObject.layer;

            // The car's wheels: a holder each at the real wheel's place, turned each frame as the nearest borrowed wheel.
            var holders = new Dictionary<Transform, Transform>();
            foreach (var wheel in realWheels)
            {
                var holder = new GameObject("DrivenRides.Wheel").transform;
                holder.SetParent(body.transform, false);
                holder.localPosition = realRoot.InverseTransformPoint(wheel.position);
                // At rest, not as the wheel stands now (it may be steered or part way round).
                holder.localRotation = Quaternion.Inverse(realRoot.rotation) * (restOf.TryGetValue(wheel, out var rest) ? rest.rotation : wheel.rotation);
                holder.localScale = Divide(wheel.lossyScale, realRoot.lossyScale);
                holders[wheel] = holder;
                var donorWheel = NearestSameCorner(donorRoot, donorWheels, holder.localPosition + shift, donorBounds.center);
                if (donorWheel != null && donorWheel.parent != null)
                {
                    var realRadius = radiusOf.TryGetValue(wheel, out var rr) && rr > 0.01f ? rr
                        : donorRadius.TryGetValue(donorWheel, out var dr) && dr > 0.01f ? dr : 0.33f;
                    wheels.Add(new CopiedWheel { Holder = holder, Donor = donorWheel, DonorParent = donorWheel.parent, HolderRest = holder.localRotation, Radius = realRadius });
                }
            }

            var copied = 0;
            foreach (var r in realRenderers)
            {
                if (!(r is MeshRenderer) || !r.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null) continue;
                Transform parent = body.transform;
                var wheelOf = WheelOf(r.transform, realWheels, realWheelParts);
                Matrix4x4 local;
                if (wheelOf != null && holders.TryGetValue(wheelOf, out var holder))
                {
                    parent = holder;
                    local = wheelOf.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    // Relative to the wheel visual, whose pose the holder copies.
                }
                else local = realRoot.worldToLocalMatrix * r.transform.localToWorldMatrix;
                var part = new GameObject("DrivenRides.Part");
                part.layer = donor.gameObject.layer;
                part.transform.SetParent(parent, false);
                part.transform.localPosition = local.GetColumn(3);
                part.transform.localRotation = local.rotation;
                part.transform.localScale = local.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var copy = part.AddComponent<MeshRenderer>();
                copy.sharedMaterials = r.sharedMaterials;
                copy.shadowCastingMode = r.shadowCastingMode;
                // The car's colour and dirt are property blocks on its body meshes (CarFeatures.SetColor).
                if (r.HasPropertyBlock())
                {
                    var block = new MaterialPropertyBlock();
                    r.GetPropertyBlock(block);
                    copy.SetPropertyBlock(block);
                    for (var m = 0; m < r.sharedMaterials.Length; m++)
                    {
                        r.GetPropertyBlock(block, m);
                        if (!block.isEmpty) copy.SetPropertyBlock(block, m);
                    }
                }
                copied++;
            }

            foreach (var r in donorRenderers)
            {
                if (r == null || !r.enabled || r.transform.IsChildOf(body.transform)) continue;
                r.enabled = false;
                hidden.Add(r);
            }
            Log.Info($"Stand-in dressed: {copied} parts copied, {realWheels.Count} wheels ({wheels.Count} turning), {hidden.Count} of the borrowed car's parts hidden.");
        }

        private void LateUpdate()
        {
            var root = transform.rotation;
            // Metres rolled forward since the last frame (backwards is negative).
            var rolled = lastPositionSet ? Vector3.Dot(transform.position - lastPosition, transform.forward) : 0f;
            lastPosition = transform.position;
            lastPositionSet = true;
            foreach (var w in wheels)
            {
                if (w.Holder == null || w.Donor == null || w.DonorParent == null) continue;
                var local = w.Donor.localRotation;
                // Rx(spin) leaves the axle alone, so the axle gives the steering.
                var axle = local * Vector3.right;
                var steer = Mathf.Atan2(-axle.z, axle.x) * Mathf.Rad2Deg;
                w.Spin = (w.Spin + rolled / w.Radius * Mathf.Rad2Deg) % 360f;
                var turn = Quaternion.AngleAxis(steer, Vector3.up) * Quaternion.AngleAxis(w.Spin, Vector3.right);
                var parent = Quaternion.Inverse(root) * w.DonorParent.rotation;
                w.Holder.localRotation = parent * turn * Quaternion.Inverse(parent) * w.HolderRest;
            }
        }

        // Gley may take the car back mid-ride (removed, or cleared off the road); it must not come back dressed.
        private void OnDisable() => Undress();

        // Back to the pool as it came. Safe to call twice.
        public void Undress()
        {
            foreach (var r in hidden) if (r != null) r.enabled = true;
            hidden.Clear();
            wheels.Clear();
            if (body != null) Object.Destroy(body);
            body = null;
            Object.Destroy(this);
        }

        private static Vector3 Divide(Vector3 a, Vector3 b) =>
            new Vector3(b.x != 0f ? a.x / b.x : a.x, b.y != 0f ? a.y / b.y : a.y, b.z != 0f ? a.z / b.z : a.z);

        // Renderers that draw now: active, enabled, and of the nearest level of detail only.
        private static List<Renderer> VisibleRenderers(GameObject root)
        {
            var skip = new HashSet<Renderer>();
            foreach (var group in root.GetComponentsInChildren<LODGroup>())
            {
                var lods = group.GetLODs();
                for (var k = 1; k < lods.Length; k++) foreach (var r in lods[k].renderers) if (r != null) skip.Add(r);
            }
            var result = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                if (r.enabled && !skip.Contains(r) && !(r is ParticleSystemRenderer) && !(r is TrailRenderer) && !(r is LineRenderer)) result.Add(r);
            return result;
        }

        // The renderers' box in the root's own frame.
        private static Bounds BodyBounds(Transform root, IEnumerable<Renderer> renderers)
        {
            var any = false;
            var bounds = new Bounds();
            foreach (var r in renderers)
            {
                if (r == null || r is ParticleSystemRenderer) continue;
                var b = r.localBounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = root.InverseTransformPoint(r.transform.TransformPoint(corner));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            return bounds;
        }

        private static Transform? WheelOf(Transform part, List<Transform> wheels, HashSet<Transform> wheelParts)
        {
            if (!wheelParts.Contains(part)) return null;
            foreach (var w in wheels) if (part == w || part.IsChildOf(w)) return w;
            // A brake calliper (non-rotating) belongs to no turning wheel: it stays on the body.
            return null;
        }

        // The borrowed wheel on the same corner (left or right, front or back) nearest to a place in the car's frame.
        private static Transform? NearestSameCorner(Transform root, List<Transform> donorWheels, Vector3 local, Vector3 middle)
        {
            Transform? best = null;
            var bestDistance = float.MaxValue;
            foreach (var w in donorWheels)
            {
                var p = root.InverseTransformPoint(w.position);
                if (Mathf.Sign(p.x - middle.x) != Mathf.Sign(local.x - middle.x) || Mathf.Sign(p.z - middle.z) != Mathf.Sign(local.z - middle.z)) continue;
                var d = (p - local).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = w; }
            }
            return best;
        }
    }
}
