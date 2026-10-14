#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Buildings;
using GleyTrafficSystem;
using HarmonyLib;
using Helpers;
using NWH.Common.Vehicles;
using UnityEngine;
using Vehicles.VehicleTypes;
using NwhController = NWH.VehiclePhysics2.VehicleController;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // A drivable car dressed as a traffic car that has no drivable twin (Peter, 13 October 2026: the taxi gets a
    // drivable model, built as a re-skin). The reverse of StandIn's dress: the drivable car closest in size is the
    // donor and keeps its physics, colliders and wheels; its renderers are hidden, and a visual-only copy of the
    // traffic model (no colliders, no scripts) sits on it, with the traffic wheels on NWH's wheel visuals so they spin,
    // steer and ride the suspension. The prefabs can't be read offline, so everything is measured here and logged.
    internal static class NwhDress
    {
        // A car's size in its own frame, from its meshes and its wheels.
        public sealed class Shape
        {
            public float Length, Width, Height, Wheelbase, Track, WheelRadius;
            public int Wheels;

            public override string ToString() =>
                $"{Length:0.00} x {Width:0.00} x {Height:0.00} m, wheelbase {Wheelbase:0.00}, track {Track:0.00}, {Wheels} wheels of r {WheelRadius:0.00}";
        }

        public sealed class Donor
        {
            public string TypeName = "";
            public Shape Drivable = null!;
            public Shape Look = null!;
            public float Score;
        }

        private static readonly System.Reflection.FieldInfo? TypesField = AccessTools.Field(typeof(VehicleTypeHelper), "VehicleTypes");
        private static readonly System.Reflection.FieldInfo? TrafficVehiclesField = AccessTools.Field(typeof(TrafficManager), "trafficVehicles");
        private static readonly System.Reflection.FieldInfo? EngineSourceField = AccessTools.Field(typeof(EngineSoundComponent), "audioSource");
        private static readonly System.Reflection.FieldInfo? MasterVolumeField = AccessTools.Field(typeof(TrafficVehicles), "masterVolume");
        // By traffic prefab name; null when no drivable car fits.
        private static readonly Dictionary<string, Donor?> donors = new Dictionary<string, Donor?>();
        private static readonly HashSet<string> partsLogged = new HashSet<string>();

        // The wheels are placed on NWH's; a difference over this is reported as a mismatch.
        private const float Tolerance = 0.15f;

        // The drivable car closest in size to a traffic prefab, picked once per prefab. Its physics, steering and body
        // box are what the driver and the planner use.
        public static Donor? DonorFor(GameObject? trafficPrefab, out string why)
        {
            why = "";
            if (trafficPrefab == null) { why = "no traffic prefab"; return null; }
            var key = trafficPrefab.name;
            if (donors.TryGetValue(key, out var cached))
            {
                if (cached == null) why = "no drivable car to dress as " + key;
                return cached;
            }
            Donor? best = null;
            try
            {
                var watch = Stopwatch.StartNew();
                var look = MeasureTraffic(trafficPrefab);
                if (look == null) { why = key + " has no Gley VehicleComponent or no meshes"; Log.Info("Dress donor for " + key + ": " + why + "."); donors[key] = null; return null; }
                var candidates = new List<Donor>();
                if (TypesField?.GetValue(null) is Dictionary<string, VehicleType> types)
                    foreach (var type in types.Values)
                    {
                        if (type == null || string.IsNullOrEmpty(type.vehicleTypeName)) continue;
                        var asset = PrefabHelper.LoadPrefabAssetByName("Vehicles/PlayerVehicles/" + NwhCar.Id(type.vehicleTypeName));
                        if (asset == null || asset.GetComponentInChildren<NwhController>(true) == null) continue;
                        var shape = MeasureDrivable(asset);
                        if (shape == null || candidates.Any(c => c.TypeName == type.vehicleTypeName)) continue;
                        candidates.Add(new Donor { TypeName = type.vehicleTypeName, Drivable = shape, Look = look, Score = Score(shape, look) });
                    }
                // Same number of wheels if any has it (a four-wheeled taxi on a four-wheeled car).
                var same = candidates.Where(c => c.Drivable.Wheels == look.Wheels).ToList();
                best = (same.Count > 0 ? same : candidates).OrderBy(c => c.Score).FirstOrDefault();
                Log.Info($"Dress donor for {key} ({look}): {(best != null ? NwhCar.Id(best.TypeName) : "none")}, {watch.ElapsedMilliseconds} ms. Candidates by score: " +
                         string.Join("; ", candidates.OrderBy(c => c.Score).Select(c => $"{NwhCar.Id(c.TypeName)} {c.Score:0.00} ({c.Drivable})")) + ".");
                if (best == null) why = "no drivable car to dress as " + key;
            }
            catch (Exception ex)
            {
                why = "donor: " + ex.Message;
                Log.Error("Dress donor for " + key + ": " + ex);
            }
            donors[key] = best;
            return best;
        }

        // The wheels count most: they are put on the donor's, so a wheelbase or track difference shows at the arches.
        private static float Score(Shape drivable, Shape look) =>
            2f * Mathf.Abs(drivable.Wheelbase - look.Wheelbase) + 1.5f * Mathf.Abs(drivable.Track - look.Track) +
            Mathf.Abs(drivable.Length - look.Length) + Mathf.Abs(drivable.Width - look.Width) +
            0.5f * Mathf.Abs(drivable.Height - look.Height) + Mathf.Abs(drivable.WheelRadius - look.WheelRadius);

        private static Shape? MeasureDrivable(GameObject asset)
        {
            var root = asset.transform;
            var centres = new List<Vector3>();
            var radius = 0f;
            foreach (var wheel in asset.GetComponentsInChildren<WheelUAPI>(true))
            {
                centres.Add(NwhCentre(root, wheel));
                radius += wheel.Radius;
            }
            var body = MeshBounds(root, asset.GetComponentsInChildren<Renderer>(true), null);
            if (centres.Count < 2 || body == null) return null;
            return ShapeOf(body.Value, centres, radius / centres.Count);
        }

        private static Shape? MeasureTraffic(GameObject prefab)
        {
            var gley = prefab.GetComponent<VehicleComponent>();
            if (gley == null) return null;
            var root = prefab.transform;
            var centres = new List<Vector3>();
            var radius = 0f;
            if (gley.allWheels != null)
                foreach (var w in gley.allWheels)
                {
                    if (w?.wheelGraphics == null) continue;
                    centres.Add(root.InverseTransformPoint(w.wheelGraphics.position));
                    radius += TrafficRadius(w.wheelGraphics, w.wheelRadius);
                }
            var body = MeshBounds(root, prefab.GetComponentsInChildren<Renderer>(true), gley.shadowHolder);
            if (body == null) return null;
            if (centres.Count == 0) return new Shape { Length = body.Value.size.z, Width = body.Value.size.x, Height = body.Value.size.y };
            return ShapeOf(body.Value, centres, radius / centres.Count);
        }

        private static Shape ShapeOf(Bounds body, List<Vector3> wheels, float radius) => new Shape
        {
            Length = body.size.z, Width = body.size.x, Height = body.size.y,
            Wheelbase = wheels.Max(c => c.z) - wheels.Min(c => c.z), Track = wheels.Max(c => c.x) - wheels.Min(c => c.x),
            Wheels = wheels.Count, WheelRadius = radius,
        };

        // An NWH wheel's centre in the car's frame: the wheel controller's x and z (NWH keeps the visual under it,
        // Wheel.Initialize) and the visual's height as the car was built.
        private static Vector3 NwhCentre(Transform root, WheelUAPI wheel)
        {
            var at = root.InverseTransformPoint(wheel.transform.position);
            var visual = wheel.WheelVisual;
            if (visual != null) at.y = root.InverseTransformPoint(visual.transform.position).y;
            return at;
        }

        // Gley's own radius for a traffic wheel, or its meshes' if that is unset.
        private static float TrafficRadius(Transform wheel, float gleyRadius)
        {
            if (gleyRadius > 0.05f) return gleyRadius;
            var b = MeshBounds(wheel, wheel.GetComponentsInChildren<Renderer>(true), null);
            return b != null ? Mathf.Max(b.Value.extents.y, b.Value.extents.z) : 0.33f;
        }

        // Builds the dress on a stripped NWH car that hasn't woken yet. Null when done, else why not.
        public static string? Dress(NwhCar car, GameObject trafficPrefab, Donor donor)
        {
            var watch = Stopwatch.StartNew();
            var root = car.Root.transform;
            var holder = new GameObject("DrivenRides.LookHolder");
            holder.SetActive(false);
            try
            {
                var copy = Object.Instantiate(trafficPrefab, holder.transform, false);
                copy.name = "DrivenRides.Look." + trafficPrefab.name;
                var gley = copy.GetComponent<VehicleComponent>();
                if (gley == null) return "the traffic prefab has no Gley VehicleComponent";
                // Read before the scripts go: Instantiate points the copy's references at the copy's own parts.
                var trafficWheels = new List<(Transform Graphics, float Radius)>();
                if (gley.allWheels != null)
                    foreach (var w in gley.allWheels)
                        if (w?.wheelGraphics != null) trafficWheels.Add((w.wheelGraphics, TrafficRadius(w.wheelGraphics, w.wheelRadius)));
                var shadow = gley.shadowHolder;
                var gleyLights = copy.GetComponent<VehicleLightsComponent>();
                var lightsRenderer = gleyLights != null ? gleyLights.meshRenderer : null;
                var lightsSettings = gleyLights != null
                    ? new DressLights.Settings { BlinkerSpeed = gleyLights.blinkerSpeed, BlinkerOffset = UnityEngine.Random.Range(gleyLights.blinkerOffset.x, gleyLights.blinkerOffset.y), Brake = gleyLights.brakeLightBrightness, Reverse = gleyLights.reverseLightBrightness }
                    : null;
                // The engine sound: Gley's clip on the traffic model's own AudioSource, which stays on the copy.
                var gleyEngine = copy.GetComponent<EngineSoundComponent>();
                var engineSource = gleyEngine != null ? EngineSourceField?.GetValue(gleyEngine) as AudioSource ?? gleyEngine.GetComponent<AudioSource>() : null;
                var engineSettings = gleyEngine != null && engineSource != null && engineSource.clip != null
                    ? new DressEngine.Settings
                    {
                        MinPitch = gleyEngine.minPitch, MaxPitch = gleyEngine.maxPitch, MinVolume = gleyEngine.minVolume, MaxVolume = gleyEngine.maxVolume,
                        // Gley gives each car a top speed between these when it is spawned (VehicleComponent.ActivateVehicle).
                        TopKmh = (gley.minPossibleSpeed + gley.maxPossibleSpeed) / 2f,
                        Master = TrafficManager.HasInstance && TrafficVehiclesField?.GetValue(TrafficManager.Instance) is TrafficVehicles pool && MasterVolumeField?.GetValue(pool) is float master ? master : 1f,
                    }
                    : null;
                var stripped = StripToLook(copy, engineSettings != null ? engineSource : null);

                // The donor's own meshes are hidden; its colliders and wheel physics stay as they are.
                var hidden = 0;
                foreach (var r in car.Root.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled) { r.enabled = false; hidden++; }
                var lightSources = car.Root.GetComponentsInChildren<Light>(true).Length;

                copy.transform.SetParent(root, false);
                copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = Divide(trafficPrefab.transform.localScale, root.lossyScale);
                copy.transform.localPosition = Vector3.zero;
                copy.SetActive(true);
                var allRenderers = copy.GetComponentsInChildren<Renderer>(true);
                var lods = copy.GetComponentsInChildren<LODGroup>(true).Length;

                // Line the copy up on the donor's wheels: the axles' middle and the wheels' centre height.
                var nwhWheels = new List<(WheelUAPI Wheel, Transform Visual, Vector3 Centre)>();
                foreach (var w in car.Vehicle.powertrain.wheels)
                {
                    var visual = w?.wheelUAPI?.WheelVisual;
                    if (visual != null) nwhWheels.Add((w!.wheelUAPI, visual.transform, NwhCentre(root, w.wheelUAPI)));
                }
                var lookCentres = trafficWheels.Select(t => root.InverseTransformPoint(t.Graphics.position)).ToList();
                Vector3 shift;
                var lookMiddle = Middle(lookCentres);
                var nwhMiddle = Middle(nwhWheels.Select(n => n.Centre).ToList());
                if (lookMiddle != null && nwhMiddle != null) shift = nwhMiddle.Value - lookMiddle.Value;
                else
                {
                    // No wheels to go by: the body's middle on the donor's body box, its bottom on the ground under it.
                    var body = MeshBounds(root, allRenderers, shadow) ?? new Bounds();
                    var ground = car.BodyCentre.y - car.BodyHalf.y;
                    shift = new Vector3(car.BodyCentre.x - body.center.x, ground - body.min.y, car.BodyCentre.z - body.center.z);
                }
                copy.transform.localPosition = shift;

                // Each NWH wheel takes the traffic wheel on its corner. NWH turns the visual to its wheel controller's
                // rotation times steer and spin (WheelController.cs:937-953); Gley turns a wheel's graphics to its
                // parent's rotation times spin and steer (UpdateWheelJob.cs:67-71). So a holder under the visual gets
                // the traffic wheel's rest frame, and the wheel sits in it at rest.
                var mapping = new List<string>();
                var mismatches = new List<string>();
                var used = new HashSet<int>();
                var wheelRenderers = 0;
                foreach (var (wheel, visual, centre) in nwhWheels)
                {
                    var middle = nwhMiddle!.Value;
                    var corner = (centre.z >= middle.z ? "front" : "back") + "-" + (centre.x < middle.x ? "left" : "right");
                    var pick = -1;
                    var bestDistance = float.MaxValue;
                    for (var k = 0; k < trafficWheels.Count; k++)
                    {
                        if (used.Contains(k)) continue;
                        var at = lookCentres[k] + shift;
                        if (Mathf.Sign(at.x - middle.x) != Mathf.Sign(centre.x - middle.x) || Mathf.Sign(at.z - middle.z) != Mathf.Sign(centre.z - middle.z)) continue;
                        var d = (at - centre).sqrMagnitude;
                        if (d < bestDistance) { bestDistance = d; pick = k; }
                    }
                    if (pick < 0) { mapping.Add($"{corner} {visual.name}: none"); mismatches.Add("no traffic wheel for the " + corner); continue; }
                    used.Add(pick);
                    var (graphics, lookRadius) = trafficWheels[pick];
                    var placed = lookCentres[pick] + shift;
                    // The traffic wheel is scaled to the NWH wheel's radius so it touches the ground the physics uses.
                    var radius = wheel.Radius;
                    var wanted = lookRadius > 0.01f ? radius / lookRadius : 1f;
                    var factor = Mathf.Clamp(wanted, 0.8f, 1.25f);
                    // Out along the axle to the traffic car's own track (an offset along the spin axis doesn't wobble).
                    var outward = Mathf.Clamp(placed.x - centre.x, -0.3f, 0.3f);
                    var restRotation = wheel.transform.rotation;
                    var wheelHolder = new GameObject("DrivenRides.LookWheel").transform;
                    wheelHolder.gameObject.layer = graphics.gameObject.layer;
                    wheelHolder.SetParent(visual, false);
                    wheelHolder.localRotation = Quaternion.Inverse(restRotation) * graphics.parent.rotation;
                    wheelHolder.localPosition = Divide(Quaternion.Inverse(restRotation) * (root.rotation * new Vector3(outward, 0f, 0f)), visual.lossyScale);
                    wheelHolder.localScale = Divide(graphics.parent.lossyScale * factor, visual.lossyScale);
                    wheelRenderers += graphics.GetComponentsInChildren<Renderer>(true).Length;
                    graphics.SetParent(wheelHolder, false);
                    graphics.localPosition = Vector3.zero;
                    graphics.localRotation = Quaternion.identity;
                    var dz = placed.z - centre.z;
                    var dy = placed.y - centre.y;
                    mapping.Add($"{corner} {graphics.name} r {lookRadius:0.00} on {visual.name} r {radius:0.00} (x{factor:0.00}, {outward:+0.00;-0.00} m out, {dz:+0.00;-0.00} m along" +
                                (Mathf.Abs(visual.lossyScale.x - 1f) > 0.01f || Mathf.Abs(visual.lossyScale.y - 1f) > 0.01f ? $", visual scale {visual.lossyScale}" : "") + ")");
                    if (Mathf.Abs(dz) > Tolerance) mismatches.Add($"{corner} wheel {dz:+0.00;-0.00} m along");
                    if (Mathf.Abs(dy) > Tolerance) mismatches.Add($"{corner} wheel {dy:+0.00;-0.00} m up");
                    if (Mathf.Abs(placed.x - centre.x) > 0.3f) mismatches.Add($"{corner} wheel {placed.x - centre.x:+0.00;-0.00} m out, held to 0.3");
                    if (!Mathf.Approximately(factor, wanted)) mismatches.Add($"{corner} wheel radius {lookRadius:0.00} against {radius:0.00}, scaled x{factor:0.00} not x{wanted:0.00}");
                }
                for (var k = 0; k < trafficWheels.Count; k++)
                    if (!used.Contains(k)) { mapping.Add($"{trafficWheels[k].Graphics.name} left on the body"); mismatches.Add(trafficWheels[k].Graphics.name + " has no NWH wheel"); }

                // The lights are one renderer with a property block (VehicleLightsComponent): kept, off but for the
                // brake and reverse lights.
                string lightsText;
                if (lightsRenderer != null && lightsSettings != null)
                {
                    var lights = car.Root.AddComponent<DressLights>();
                    lights.Begin(car.Vehicle, lightsRenderer, lightsSettings);
                    lightsText = $"'{lightsRenderer.name}', brake and reverse lights on, headlights and blinkers off";
                }
                else lightsText = "none found (Gley's VehicleLightsComponent " + (gleyLights == null ? "missing" : "has no renderer") + ")";

                // A dressed car sounds like the car it looks like (Peter, rig 0.1.55: "Taxi motor sound sounds like the
                // sports car"): the donor's NWH engine sounds go silent (a sound component without clips never starts,
                // SoundComponent.VC_Initialize) and the traffic model's clip plays as Gley plays it. The tyres,
                // suspension, crash and the rest stay the donor's.
                string engineText;
                if (engineSettings != null && engineSource != null)
                {
                    engineSource.playOnAwake = false;
                    engineSource.loop = true;
                    // As EngineSoundComponent.Awake does.
                    if (TrafficComponent.Instance != null && TrafficComponent.Instance.engineMixerGroup != null) engineSource.outputAudioMixerGroup = TrafficComponent.Instance.engineMixerGroup;
                    var muted = MuteNwhEngine(car.Vehicle);
                    car.Root.AddComponent<DressEngine>().Begin(car, engineSource, engineSettings);
                    var s = engineSettings;
                    engineText = $"the traffic model's '{engineSource.clip!.name}' (pitch {s.MinPitch:0.00} to {s.MaxPitch:0.00}, volume {s.MinVolume:0.00} to {s.MaxVolume:0.00} from 0 to {s.TopKmh:0} km/h, " +
                                 $"master {s.Master:0.00}, mixer {(engineSource.outputAudioMixerGroup != null ? engineSource.outputAudioMixerGroup.name : "none")}, doppler {engineSource.dopplerLevel:0.0}); " +
                                 $"the donor's {(muted.Length > 0 ? muted : "engine sounds were already empty")} muted";
                }
                else engineText = "the donor's NWH engine (the traffic model has " + (gleyEngine == null ? "no EngineSoundComponent" : "no engine clip") + ")";

                // Where the look sticks out of the donor's body box (the physics): parts there pass through walls.
                var lookBody = MeshBounds(root, copy.GetComponentsInChildren<Renderer>(true), shadow);
                var outside = "";
                if (lookBody != null)
                {
                    var b = lookBody.Value;
                    var boxMin = car.BodyCentre - car.BodyHalf;
                    var boxMax = car.BodyCentre + car.BodyHalf;
                    var front = b.max.z - boxMax.z;
                    var back = boxMin.z - b.min.z;
                    var sides = Mathf.Max(b.max.x - boxMax.x, boxMin.x - b.min.x);
                    var top = b.max.y - boxMax.y;
                    outside = $"front {front:+0.00;-0.00}, back {back:+0.00;-0.00}, sides {sides:+0.00;-0.00}, top {top:+0.00;-0.00} m";
                    if (front > Tolerance || back > Tolerance || sides > Tolerance) mismatches.Add("the look sticks out of the physics box (" + outside + ")");
                }
                var look = donor.Look;
                var drivable = donor.Drivable;
                if (Mathf.Abs(drivable.Wheelbase - look.Wheelbase) > Tolerance) mismatches.Add($"wheelbase {look.Wheelbase:0.00} on {drivable.Wheelbase:0.00}");
                if (Mathf.Abs(drivable.Length - look.Length) > 0.3f) mismatches.Add($"length {look.Length:0.00} on {drivable.Length:0.00}");

                if (partsLogged.Add(trafficPrefab.name))
                    Log.Info($"Dress parts of {trafficPrefab.name}: renderers {string.Join(", ", allRenderers.GroupBy(r => r.name).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key))}; " +
                             $"wheels {string.Join(", ", trafficWheels.Select(t => $"{t.Graphics.name} r {t.Radius:0.00}"))}; shadow {(shadow != null ? shadow.name : "none")}; took off {stripped}.");
                car.Look = trafficPrefab.name;
                Log.Info($"Dressed {NwhCar.Id(donor.TypeName)} as {trafficPrefab.name}: look {look}; donor {drivable} (length {drivable.Length - look.Length:+0.00;-0.00}, width {drivable.Width - look.Width:+0.00;-0.00}, " +
                         $"wheelbase {drivable.Wheelbase - look.Wheelbase:+0.00;-0.00}, track {drivable.Track - look.Track:+0.00;-0.00}, score {donor.Score:0.00}). " +
                         $"Copied {allRenderers.Length - wheelRenderers} body and {wheelRenderers} wheel renderers ({lods} LOD groups), body shifted {shift.x:0.00}, {shift.y:0.00}, {shift.z:0.00}; " +
                         $"hid {hidden} of the donor's ({lightSources} light sources left on it). Wheels: {string.Join("; ", mapping)}. Lights: {lightsText}. Engine: {engineText}. " +
                         $"Outside the physics box: {(outside.Length > 0 ? outside : "unmeasured")}. Mismatch: {(mismatches.Count > 0 ? string.Join("; ", mismatches) : "none over " + Tolerance + " m")}. {watch.ElapsedMilliseconds} ms.");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Dress " + trafficPrefab.name + ": " + ex);
                return "dress: " + ex.Message;
            }
            finally { Object.Destroy(holder); }
        }

        // The donor's engine, exhaust, turbo and gearbox whine sounds, emptied before the car wakes. Returns their names.
        private static string MuteNwhEngine(NwhController vehicle)
        {
            var sound = vehicle.soundManager;
            if (sound == null) return "";
            var muted = new List<string>();
            foreach (var c in new NWH.VehiclePhysics2.Sound.SoundComponents.SoundComponent[]
                     {
                         sound.engineRunningComponent, sound.engineStartStopComponent, sound.exhaustPopComponent,
                         sound.turboWhistleComponent, sound.turboFlutterComponent, sound.transmissionWhineComponent,
                     })
            {
                if (c?.clips == null || c.clips.Count == 0) continue;
                muted.Add($"{c.GetType().Name.Replace("Component", "")} ('{(c.Clip != null ? c.Clip.name : "")}')");
                c.clips = new List<AudioClip>();
            }
            return string.Join(", ", muted);
        }

        // Everything but the meshes goes (and keep, the engine's AudioSource): scripts (Gley's, the game's, the
        // lights' and the sound's) first, then colliders, the rigidbody, lights, audio and the rest. Some require
        // others, so a few passes.
        private static string StripToLook(GameObject copy, AudioSource? keep)
        {
            var gone = new SortedDictionary<string, int>();
            void Remove(Component c)
            {
                var name = c.GetType().Name;
                Object.DestroyImmediate(c);
                if (c == null) gone[name] = gone.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            for (var pass = 0; pass < 4; pass++)
            {
                var scripts = copy.GetComponentsInChildren<MonoBehaviour>(true).Where(b => b != null).Reverse().ToList();
                if (scripts.Count == 0) break;
                foreach (var b in scripts) Remove(b);
            }
            for (var pass = 0; pass < 4; pass++)
            {
                var rest = copy.GetComponentsInChildren<Component>(true).Where(c => c != null && !IsLook(c) && c != keep).Reverse().ToList();
                if (rest.Count == 0) break;
                // The rigidbody last: joints need it.
                foreach (var c in rest.Where(c => !(c is Rigidbody))) Remove(c);
                foreach (var c in rest.Where(c => c is Rigidbody)) if (c != null) Remove(c);
            }
            var left = copy.GetComponentsInChildren<Component>(true).Where(c => c != null && !IsLook(c) && c != keep).Select(c => c.GetType().Name).Distinct().ToList();
            return string.Join(", ", gone.Select(p => p.Value > 1 ? $"{p.Key} x{p.Value}" : p.Key)) + (left.Count > 0 ? "; LEFT " + string.Join(", ", left) : "");
        }

        private static bool IsLook(Component c) => c is Transform || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer || c is LODGroup;

        // The middle of the wheels: the axles' middle along the car, their mean across and in height.
        private static Vector3? Middle(List<Vector3> wheels)
        {
            if (wheels.Count == 0) return null;
            return new Vector3(wheels.Average(c => c.x), wheels.Average(c => c.y), (wheels.Max(c => c.z) + wheels.Min(c => c.z)) / 2f);
        }

        // The meshes' box in the root's frame, from the meshes themselves, so it works on a prefab asset and on an
        // inactive copy. Renderers under leaveOut (Gley's shadow quad) and switched-off parts are left out.
        private static Bounds? MeshBounds(Transform root, IEnumerable<Renderer> renderers, Transform? leaveOut)
        {
            var any = false;
            var bounds = new Bounds();
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled || !ActiveUnder(r.transform, root)) continue;
                if (leaveOut != null && r.transform.IsChildOf(leaveOut)) continue;
                Bounds b;
                if (r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null) b = filter.sharedMesh.bounds;
                else if (r is SkinnedMeshRenderer skinned) b = skinned.localBounds;
                else continue;
                for (var i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = root.InverseTransformPoint(r.transform.TransformPoint(corner));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            return any ? bounds : (Bounds?)null;
        }

        // Switched on below the root (the root itself may be switched off in a pooled prefab).
        private static bool ActiveUnder(Transform t, Transform root)
        {
            for (var at = t; at != null && at != root; at = at.parent)
                if (!at.gameObject.activeSelf) return false;
            return true;
        }

        private static Vector3 Divide(Vector3 a, Vector3 b) =>
            new Vector3(b.x != 0f ? a.x / b.x : a.x, b.y != 0f ? a.y / b.y : a.y, b.z != 0f ? a.z / b.z : a.z);

        // ---- Looking at one ----

        private static NwhCar? shown;

        // The traffic prefab named (a part of its name, or "taxi" for the hailable taxi) from Gley's pool.
        public static GameObject? TrafficPrefab(string which)
        {
            var pool = (TrafficVehiclesField?.GetValue(TrafficManager.Instance) as TrafficVehicles)?.GetVehicleList();
            if (pool == null) return null;
            var prefabs = pool.Where(v => v != null && v.prefab != null).Select(v => v.prefab).Distinct().ToList();
            if (which == "taxi") return prefabs.FirstOrDefault(p => p.GetComponent<TaxiController>() != null);
            return prefabs.FirstOrDefault(p => p.name.IndexOf(which, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Ctrl+Alt+Shift+L and the rig's "dress" command: a dressed car parked beside the player, held on its handbrake,
        // to look at. The one before goes.
        public static void SpawnBeside(string which)
        {
            Remove();
            if (!TrafficManager.HasInstance || !TrafficManager.IsInitialized) { Notice.Show("Traffic is not ready."); return; }
            if (BuildingManager.IsInsideBuilding) { Notice.Show("Go outside first."); return; }
            var prefab = TrafficPrefab(which);
            if (prefab == null) { Log.Info($"Dress: no traffic prefab '{which}' in the pool."); Notice.Show($"No traffic car '{which}'."); return; }
            var donor = DonorFor(prefab, out var why);
            if (donor == null) { Log.Info("Dress: " + why + "."); Notice.Show(why); return; }
            var player = InstanceBehavior<GameManager>.Instance.playerController.transform;
            var rotation = Quaternion.Euler(0f, player.eulerAngles.y, 0f);
            var size = new Vector3(Mathf.Max(donor.Look.Width, donor.Drivable.Width), Mathf.Max(donor.Look.Height, donor.Drivable.Height), Mathf.Max(donor.Look.Length, donor.Drivable.Length));
            Vector3? spot = null;
            foreach (var offset in new[] { Vector3.right * 4.5f, Vector3.left * 4.5f, Vector3.forward * 7f, Vector3.back * 7f })
            {
                var at = Ground(player.position + rotation * offset);
                if (at == null) continue;
                var centre = at.Value + Vector3.up * (size.y / 2f + 0.25f);
                var blocked = Physics.OverlapBox(centre, size / 2f + new Vector3(0.3f, 0f, 0.3f), rotation, ~0, QueryTriggerInteraction.Ignore)
                    .Any(c => !c.transform.IsChildOf(player));
                if (!blocked) { spot = at; break; }
            }
            if (spot == null) { Notice.Show("No free spot beside you."); Log.Info("Dress: no free spot beside the player."); return; }
            var car = NwhCar.SpawnDressed(prefab, spot.Value + Vector3.up * 0.4f, rotation, out why);
            if (car == null) { Log.Info("Dress: " + why + "."); Notice.Show("Dress failed: " + why); return; }
            // The spot is for the body's middle, not the prefab's origin.
            car.Root.transform.position -= rotation * new Vector3(car.BodyCentre.x, 0f, car.BodyCentre.z);
            Physics.SyncTransforms();
            car.Root.AddComponent<HoldStill>().Vehicle = car.Vehicle;
            shown = car;
            Log.Info($"Dress: {prefab.name} on {NwhCar.Id(car.TypeName)} beside the player at {car.Root.transform.position}.");
            Notice.Show($"{prefab.name} on {NwhCar.Id(car.TypeName)} beside you.");
        }

        public static void Remove()
        {
            if (shown?.Root != null) Object.Destroy(shown.Root);
            shown = null;
        }

        // The highest static surface under a place (a road, a pavement, the ground).
        private static Vector3? Ground(Vector3 near)
        {
            Vector3? best = null;
            foreach (var hit in Physics.RaycastAll(near + Vector3.up * 3f, Vector3.down, 12f, ~0, QueryTriggerInteraction.Ignore))
                if (hit.collider.attachedRigidbody == null && (best == null || hit.point.y > best.Value.y)) best = hit.point;
            return best;
        }

        // The car to look at stands on its handbrake (it has no driver; the engine is off).
        private sealed class HoldStill : MonoBehaviour
        {
            public NwhController? Vehicle;

            private void FixedUpdate()
            {
                if (Vehicle == null || !Vehicle.IsInitialized) return;
                Vehicle.input.Throttle = 0f;
                Vehicle.input.Brakes = 0f;
                Vehicle.input.Steering = 0f;
                Vehicle.input.Handbrake = 1f;
            }
        }
    }

    // The traffic model's lights on a dressed car: Gley's VehicleLightsComponent draws them with one renderer's
    // property block, which this sets from the NWH car's pedals and gear. Headlights and blinkers stay off.
    internal sealed class DressLights : MonoBehaviour
    {
        public sealed class Settings
        {
            public float BlinkerSpeed, BlinkerOffset, Brake, Reverse;
        }

        // The shader properties VehicleLightsComponent sets.
        private static readonly int IsBlinkerOn = Shader.PropertyToID("_IsBlinkerOn");
        private static readonly int IsLeftBlinkerOn = Shader.PropertyToID("_IsLeftBlinkerOn");
        private static readonly int IsRightBlinkerOn = Shader.PropertyToID("_IsRightBlinkerOn");
        private static readonly int BlinkerSpeed = Shader.PropertyToID("_BlinkerSpeed");
        private static readonly int BlinkerOffset = Shader.PropertyToID("_BlinkerOffset");
        private static readonly int FrontLight = Shader.PropertyToID("_FrontLightStrength");
        private static readonly int BrakeLight = Shader.PropertyToID("_BackLightStrength");
        private static readonly int ReverseLight = Shader.PropertyToID("_ReverseLightStrength");

        private NwhController? vehicle;
        private Renderer? lights;
        private Settings settings = new Settings();
        private MaterialPropertyBlock? block;
        private int state = -1;

        public void Begin(NwhController vehicle, Renderer lights, Settings settings)
        {
            this.vehicle = vehicle;
            this.lights = lights;
            this.settings = settings;
            block = new MaterialPropertyBlock();
            lights.GetPropertyBlock(block);
            block.SetFloat(BlinkerOffset, settings.BlinkerOffset);
            block.SetFloat(BlinkerSpeed, settings.BlinkerSpeed);
            block.SetInt(IsBlinkerOn, 0);
            block.SetInt(IsLeftBlinkerOn, 0);
            block.SetInt(IsRightBlinkerOn, 0);
            block.SetFloat(FrontLight, 0f);
            block.SetFloat(BrakeLight, 0f);
            block.SetFloat(ReverseLight, 0f);
            lights.SetPropertyBlock(block);
        }

        private void LateUpdate()
        {
            if (vehicle == null || lights == null || block == null || !vehicle.IsInitialized) return;
            var gear = vehicle.powertrain.transmission.Gear;
            // In reverse NWH's automatic gearbox swaps the pedals (swapInputInReverse): the throttle brakes.
            var braking = (gear < 0 ? vehicle.input.Throttle : vehicle.input.Brakes) > 0.05f;
            var now = (braking ? 1 : 0) | (gear < 0 ? 2 : 0);
            if (now == state) return;
            state = now;
            lights.GetPropertyBlock(block);
            block.SetFloat(BrakeLight, braking ? settings.Brake : 0f);
            block.SetFloat(ReverseLight, gear < 0 ? settings.Reverse : 0f);
            lights.SetPropertyBlock(block);
        }
    }
}

namespace DrivenRides
{
    // The traffic model's engine sound on a dressed car, as Gley plays it (EngineSoundComponent.UpdateEngineSound):
    // the car's speed over its top speed sets the volume and the pitch in a straight line. It plays while the NWH
    // engine runs. Speed, not engine revs: no gear changes in the sound, so it is the same at any rig speed.
    internal sealed class DressEngine : MonoBehaviour
    {
        public sealed class Settings
        {
            public float MinPitch, MaxPitch, MinVolume, MaxVolume, TopKmh, Master;
        }

        private NwhController? vehicle;
        private Rigidbody? body;
        private AudioSource? source;
        private Settings settings = new Settings();

        public void Begin(NwhCar car, AudioSource source, Settings settings)
        {
            vehicle = car.Vehicle;
            body = car.Body;
            this.source = source;
            this.settings = settings;
        }

        private void Update()
        {
            if (vehicle == null || body == null || source == null) return;
            if (!vehicle.IsInitialized || !vehicle.powertrain.engine.IsRunning)
            {
                if (source.isPlaying) source.Stop();
                return;
            }
            var share = body.velocity.magnitude * 3.6f / Mathf.Max(1f, settings.TopKmh);
            source.volume = (settings.MinVolume + (settings.MaxVolume - settings.MinVolume) * share) * settings.Master;
            source.pitch = settings.MinPitch + (settings.MaxPitch - settings.MinPitch) * share;
            if (!source.isPlaying) source.Play();
        }
    }
}
