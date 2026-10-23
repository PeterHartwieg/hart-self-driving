#nullable enable
using System;
using DrivenRides.Core.Ride;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // One reusable renderer per loaded city, independent of pooled cars and ride coroutines.
    // Only a larger/replaced plan grows buffers. Normal frames update one vertex; crossing a sample uploads
    // the remaining cached suffix. Road probes are bounded, and never repeated for the full route per frame.
    internal sealed class RouteLineView : MonoBehaviour
    {
        private const float Lift = 0.06f;
        private const int ProbesPerFrame = 128;
        private static RouteLineView? instance;
        private readonly RouteLinePath samples = new RouteLinePath();
        private Polyline? source;
        private Vector3[] grounded = Array.Empty<Vector3>();
        private Vector3[] upload = Array.Empty<Vector3>();
        private int probed, first = -1;
        private LineRenderer? renderer;
        private Material? material;
        private GameObject? lineObject;
        private bool failed;
        private float nearRefreshAt;

        private void OnEnable() => instance = this;
        internal static void HideActive() { if (instance != null) instance.Hide(); }
        internal static void DisableActive()
        {
            if (instance == null) return;
            instance.Hide();
            instance.enabled = false;
        }

        private void LateUpdate()
        {
            if (failed) return;
            try
            {
                var ride = Ride.Active;
                if (!Options.RouteLine || ride == null || !ride.TryRouteLine(out var line, out var along, out var vehicle))
                {
                    Hide();
                    source = null;
                    return;
                }
                Show(line, along, vehicle);
            }
            catch (Exception ex)
            {
                // A cosmetic failure must never abort a paid ride or repeatedly throw each frame.
                Hide();
                failed = true;
                Log.Error("Route line disabled for this city: " + ex.Message);
            }
        }

        internal void Show(Polyline line, float along, Transform vehicle)
        {
            if (!ReferenceEquals(source, line))
            {
                Hide();
                samples.Reset(line);
                if (grounded.Length < samples.Count)
                {
                    var capacity = Math.Max(samples.Count, Math.Max(256, grounded.Length * 2));
                    grounded = new Vector3[capacity];
                    upload = new Vector3[capacity + 1];
                }
                source = line;
                probed = 0;
                nearRefreshAt = 0f;
            }
            var end = Math.Min(samples.Count, probed + ProbesPerFrame);
            for (; probed < end; probed++)
            {
                var point = samples.Point(probed);
                var p = new Vector3(point.X, point.Y, point.Z);
                p.y = (NwhPlanner.RoadAt(p, vehicle) ?? p.y) + Lift;
                grounded[probed] = p;
            }
            // No partly projected line or stale previous route during a reroute. A long route may take a few
            // frames to prepare. The plan's height is retained where the game's surface has no collider.
            if (probed < samples.Count) return;
            var next = samples.FirstAfter(Mathf.Max(0f, along));
            if (next >= samples.Count) { Hide(); return; }
            // Distant colliders can stream in after the route was prepared. Recheck only the next 64 m,
            // twice a second, so the visible nearby ribbon follows newly loaded road surfaces too.
            if (Time.unscaledTime >= nearRefreshAt)
            {
                nearRefreshAt = Time.unscaledTime + 0.5f;
                var nearEnd = Math.Min(samples.Count, next + 32);
                for (var i = Math.Max(0, next - 1); i < nearEnd; i++)
                {
                    var point = samples.Point(i);
                    var p = new Vector3(point.X, point.Y, point.Z);
                    var height = (NwhPlanner.RoadAt(p, vehicle) ?? p.y) + Lift;
                    if (Mathf.Abs(grounded[i].y - height) < 0.001f) continue;
                    grounded[i].y = height;
                    first = -1;
                }
            }
            EnsureRenderer();
            var r = renderer!;
            var head = vehicle.position;
            // Use the sampled road height as the ray's reference, not the vehicle's body pivot height.
            var previous = Math.Max(0, next - 1);
            var span = samples.Distance(next) - samples.Distance(previous);
            var fraction = span > 0f ? Mathf.Clamp01((along - samples.Distance(previous)) / span) : 0f;
            head.y = Mathf.Lerp(grounded[previous].y, grounded[next].y, fraction) - Lift;
            head.y = (NwhPlanner.RoadAt(head, vehicle) ?? head.y) + Lift;
            if (first != next)
            {
                upload[0] = head;
                var count = samples.Count - next;
                Array.Copy(grounded, next, upload, 1, count);
                r.positionCount = count + 1;
                r.SetPositions(upload);
                first = next;
            }
            else r.SetPosition(0, head);
            r.enabled = true;
        }

        private void EnsureRenderer()
        {
            if (renderer != null) return;
            var shader = Shader.Find("HDRP/Unlit");
            if (shader == null) throw new InvalidOperationException("HDRP/Unlit shader unavailable");
            material = new Material(shader) { name = "DrivenRides.RouteBlue", hideFlags = HideFlags.DontSave };
            // The game uses HDRP. Its unlit shader takes colour/alpha from the material, not line vertex colours.
            material.SetColor("_UnlitColor", new Color(47f / 255f, 128f / 255f, 1f, 0.82f));
            material.SetFloat("_SurfaceType", 1f);
            material.SetFloat("_BlendMode", 0f);
            material.SetFloat("_DoubleSidedEnable", 1f);
            material.SetFloat("_TransparentZWrite", 0f);
            material.SetFloat("_ZTestTransparent", (float)CompareFunction.LessEqual);
            if (!HDMaterial.ValidateMaterial(material)) throw new InvalidOperationException("HDRP route material validation failed");
            lineObject = new GameObject("DrivenRides.RouteLine") { hideFlags = HideFlags.DontSave };
            lineObject.transform.SetParent(transform, false);
            // TransformZ makes a road-facing ribbon, independent of camera pitch.
            lineObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            renderer = lineObject.AddComponent<LineRenderer>();
            renderer.enabled = false;
            renderer.sharedMaterial = material;
            renderer.useWorldSpace = true;
            renderer.alignment = LineAlignment.TransformZ;
            renderer.widthMultiplier = 0.4f;
            renderer.startColor = renderer.endColor = new Color32(0x2F, 0x80, 0xFF, 210);
            renderer.numCornerVertices = 2;
            renderer.numCapVertices = 2;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        private void Hide()
        {
            if (renderer != null) renderer.enabled = false;
            first = -1;
        }

        private void OnDisable()
        {
            Hide();
            source = null;
            if (instance == this) instance = null;
        }

        private void OnDestroy()
        {
            if (lineObject != null) Object.Destroy(lineObject);
            if (material != null) Object.Destroy(material);
        }
    }
}
