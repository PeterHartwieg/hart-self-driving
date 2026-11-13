#nullable enable
using System;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace DrivenRides
{
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
        private LineRenderer? lineRenderer;
        private Material? material;
        private GameObject? lineObject;
        private bool failed;
        private bool rejected;
        private float nearRefreshAt;

        private void OnEnable() => instance = this;
        internal static void HideActive() { if (instance != null) instance.Hide(); }
        internal static void DestroyActive()
        {
            if (instance == null) return;
            instance.Hide();
            Object.Destroy(instance);
            instance = null;
        }

        private void LateUpdate()
        {
            if (failed) return;
            try
            {
                var ride = Ride.Active;
                if (!Options.RouteLine || ride == null || !ride.TryRouteLine(out var line, out var along, out var vehicle, out var headCarPosition))
                {
                    Hide();
                    source = null;
                    return;
                }
                Show(line, along, vehicle, headCarPosition);
            }
            catch (Exception ex)
            {
                Hide();
                failed = true;
                Log.Error("Route line disabled for this city: " + ex.Message);
            }
        }

        internal void Show(Polyline line, float along, Transform vehicle, Vec3? headCarPosition = null)
        {
            if (!ReferenceEquals(source, line))
            {
                Hide();
                source = line;
                try { samples.Reset(line); }
                catch (ArgumentException ex)
                {
                    rejected = true;
                    Log.Info("Route line hidden for this plan: " + ex.Message);
                    return;
                }
                rejected = false;
                if (grounded.Length < samples.Count)
                {
                    var capacity = Math.Max(samples.Count, Math.Max(256, grounded.Length * 2));
                    grounded = new Vector3[capacity];
                    upload = new Vector3[capacity + 1];
                }
                probed = 0;
                nearRefreshAt = 0f;
            }
            if (rejected) return;
            var next = samples.FirstAfter(Mathf.Max(0f, along));
            if (next >= samples.Count) { Hide(); return; }
            probed = Math.Max(probed, Math.Max(0, next - 1));
            var end = Math.Min(samples.Count, probed + ProbesPerFrame);
            if (end > probed) first = -1;
            for (; probed < end; probed++)
            {
                var point = samples.Point(probed);
                var p = new Vector3(point.X, point.Y, point.Z);
                p.y = (NwhPlanner.RoadAt(p, vehicle) ?? p.y) + Lift;
                grounded[probed] = p;
            }
            if (Time.unscaledTime >= nearRefreshAt)
            {
                nearRefreshAt = Time.unscaledTime + 0.5f;
                var nearEnd = Math.Min(probed, next + 32);
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
            var r = lineRenderer!;
            var at = RouteLinePath.Head(line, along, headCarPosition);
            var head = new Vector3(at.X, 0f, at.Z);
            var previous = Math.Max(0, next - 1);
            var span = samples.Distance(next) - samples.Distance(previous);
            var fraction = span > 0f ? Mathf.Clamp01((along - samples.Distance(previous)) / span) : 0f;
            head.y = Mathf.Lerp(grounded[previous].y, grounded[next].y, fraction) - Lift;
            head.y = (NwhPlanner.RoadAt(head, vehicle) ?? head.y) + Lift;
            if (first != next)
            {
                upload[0] = head;
                var count = probed - next;
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
            if (lineRenderer != null) return;
            var shader = Shader.Find("HDRP/Unlit");
            if (shader == null) throw new InvalidOperationException("HDRP/Unlit shader unavailable");
            material = new Material(shader) { name = "DrivenRides.RouteBlue", hideFlags = HideFlags.DontSave };
            material.SetColor("_UnlitColor", new Color(47f / 255f, 128f / 255f, 1f, 0.82f));
            material.SetFloat("_SurfaceType", 1f);
            material.SetFloat("_BlendMode", 0f);
            material.SetFloat("_DoubleSidedEnable", 1f);
            material.SetFloat("_TransparentZWrite", 0f);
            material.SetFloat("_ZTestTransparent", (float)CompareFunction.LessEqual);
            if (!HDMaterial.ValidateMaterial(material)) throw new InvalidOperationException("HDRP route material validation failed");
            Log.Info($"Route line material: shader {material.shader.name}, render queue {material.renderQueue}.");
            lineObject = new GameObject("DrivenRides.RouteLine") { hideFlags = HideFlags.DontSave };
            lineObject.transform.SetParent(transform, false);
            lineObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            lineRenderer = lineObject.AddComponent<LineRenderer>();
            lineRenderer.enabled = false;
            lineRenderer.sharedMaterial = material;
            lineRenderer.useWorldSpace = true;
            lineRenderer.alignment = LineAlignment.TransformZ;
            lineRenderer.widthMultiplier = 0.4f;
            lineRenderer.startColor = lineRenderer.endColor = new Color32(0x2F, 0x80, 0xFF, 210);
            lineRenderer.numCornerVertices = 2;
            lineRenderer.numCapVertices = 2;
            lineRenderer.shadowCastingMode = ShadowCastingMode.Off;
            lineRenderer.receiveShadows = false;
            lineRenderer.lightProbeUsage = LightProbeUsage.Off;
            lineRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        private void Hide()
        {
            if (lineRenderer != null) lineRenderer.enabled = false;
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
