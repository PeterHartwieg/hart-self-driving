#nullable enable
using System.Collections.Generic;
using DrivenRides.Core.Testing;
using GleyTrafficSystem;
using Helpers;
using UnityEngine;

namespace DrivenRides
{
    // On a turn test's car while it drives (docs/turn-tests.md): each physics step it measures the car's body box,
    // front axle, speed and near misses for the judge, and reports hits. The first failure latches the car's outline
    // for the failure picture. Removed before the car goes back to Gley's pool.
    internal sealed class TestCar : MonoBehaviour
    {
        public Judge? Judge;
        public System.Func<bool>? Waiting;
        public int Step { get; private set; }
        public readonly List<Vector3> Track = new List<Vector3>();
        public Vector3[]? FailureOutline { get; private set; }
        public Vector3 FailureCentre { get; private set; }
        public bool PictureDue { get; set; }
        // Every sample and every hit, for judging the run again away from the game (tools/rejudge): S lines are
        // samples, C contacts, I incidents, O other collisions, R a replan.
        public readonly System.Text.StringBuilder Samples = new System.Text.StringBuilder();
        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        private VehicleComponent? car;
        private Rigidbody? body;
        // A car with no Gley component (an NWH-driven one, NwhCar): its front axle and its body box, set before the
        // judge starts.
        public System.Func<Vector3>? FrontAxleOf;
        private Vector3 boxCenter, boxHalf;
        // An NWH car's wheels, for the kerb check: they ride on ray casts, not colliders, so a wheel rolling up onto the
        // pavement touches nothing and the body may never hit the kerb (Peter, turn test 0.1.55: "quite a few are on
        // the sidewalk at the end", while the judge saw no contact).
        public NWH.Common.Vehicles.WheelUAPI[]? Wheels;
        private int[]? kerbSteps;
        private bool[]? kerbReported;
        private int rollSteps;
        private bool rollReported;
        // A wheel this far above the plane of the other three, for this many physics steps in a row, is on a kerb. A
        // road's camber across the car is a few centimetres; a kerb is about 15.
        private const float KerbRise = 0.1f;
        private const int KerbSteps = 5;
        private readonly Collider[] hits = new Collider[32];
        private static readonly float[] Inflations = { 0.3f, 0.2f, 0.1f };
        private float time;

        // Buildings and street props: what a car must never touch (the ride's own obstacle mask).
        public static readonly int ObstacleMask = LayerMask.GetMask("StreetProps") | LayerHelper.buildingsLayerMask;
        private static readonly int RoadsLayer = LayerMask.NameToLayer("Roads");

        private void Awake()
        {
            car = GetComponent<VehicleComponent>();
            body = car != null && car.rb != null ? car.rb : GetComponent<Rigidbody>();
            MeasureBody();
        }

        private void MeasureBody()
        {
            if (car != null) BodyBox(car, out boxCenter, out boxHalf);
        }

        // The body box in the car's own space, from its solid colliders; the triggers and Gley's sensors are left out.
        // Also for the ride's world check, on a pooled copy that may be inactive.
        public static void BodyBox(VehicleComponent car, out Vector3 centre, out Vector3 half)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            var found = false;
            var body = car.rb != null ? car.rb : car.GetComponent<Rigidbody>();
            var root = car.transform;
            foreach (var c in car.GetComponentsInChildren<Collider>(true))
            {
                if (c.isTrigger || c.GetComponentInParent<Rigidbody>(true) != body) continue;
                foreach (var corner in LocalCorners(c))
                {
                    var local = root.InverseTransformPoint(corner);
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    found = true;
                }
            }
            if (!found)
            {
                min = new Vector3(-car.colliderWidth / 2f, 0f, -car.length / 2f);
                max = new Vector3(car.colliderWidth / 2f, car.coliderHeight, car.length / 2f);
            }
            centre = (min + max) / 2f;
            half = (max - min) / 2f;
        }

        private static IEnumerable<Vector3> LocalCorners(Collider c)
        {
            Bounds b;
            if (c is BoxCollider box) b = new Bounds(box.center, box.size);
            else if (c is MeshCollider mesh && mesh.sharedMesh != null) b = mesh.sharedMesh.bounds;
            // World bounds are empty while the car is inactive.
            else if (!c.gameObject.activeInHierarchy) yield break;
            else { var w = c.bounds; for (var i = 0; i < 8; i++) yield return Corner(w.center, w.extents, i); yield break; }
            for (var i = 0; i < 8; i++) yield return c.transform.TransformPoint(Corner(b.center, b.extents, i));
        }

        private static Vector3 Corner(Vector3 center, Vector3 extents, int i) =>
            center + new Vector3((i & 1) == 0 ? -extents.x : extents.x, (i & 2) == 0 ? -extents.y : extents.y, (i & 4) == 0 ? -extents.z : extents.z);

        public void SetBody(Vector3 centre, Vector3 half) { boxCenter = centre; boxHalf = half; }

        public float HalfWidth => boxHalf.x;

        // The footprint at the bottom of the body box: four corners, then four edge midpoints.
        public Vector3[] Outline()
        {
            var t = transform;
            var bottom = boxCenter.y - boxHalf.y;
            var x = boxHalf.x;
            var z = boxHalf.z;
            var c = boxCenter;
            return new[]
            {
                t.TransformPoint(new Vector3(c.x - x, bottom, c.z + z)), t.TransformPoint(new Vector3(c.x + x, bottom, c.z + z)),
                t.TransformPoint(new Vector3(c.x + x, bottom, c.z - z)), t.TransformPoint(new Vector3(c.x - x, bottom, c.z - z)),
                t.TransformPoint(new Vector3(c.x, bottom, c.z + z)), t.TransformPoint(new Vector3(c.x + x, bottom, c.z)),
                t.TransformPoint(new Vector3(c.x, bottom, c.z - z)), t.TransformPoint(new Vector3(c.x - x, bottom, c.z)),
            };
        }

        private Vector3 FrontAxle()
        {
            if (FrontAxleOf != null) return FrontAxleOf();
            if (car != null && car.allWheels != null)
            {
                var sum = Vector3.zero;
                var n = 0;
                foreach (var wheel in car.allWheels)
                    if (wheel != null && wheel.wheelPosition == Wheel.WheelPosition.Front && wheel.wheelTransform != null) { sum += wheel.wheelTransform.position; n++; }
                if (n > 0) return sum / n;
            }
            return transform.TransformPoint(boxCenter + new Vector3(0f, 0f, boxHalf.z * 0.6f));
        }

        private float NearMiss()
        {
            var smallest = 0f;
            var centre = transform.TransformPoint(boxCenter);
            foreach (var inflate in Inflations)
            {
                var n = Physics.OverlapBoxNonAlloc(centre, boxHalf + new Vector3(inflate, 0f, inflate), hits, transform.rotation, ObstacleMask, QueryTriggerInteraction.Ignore);
                if (n == 0) break;
                smallest = inflate;
            }
            return smallest;
        }

        private void FixedUpdate()
        {
            if (Judge == null) return;
            if (Step == 0 && Sidewalk.Available)
            {
                // The pavement from the pedestrians' NavMesh (Sidewalk), recorded so tools/rejudge doesn't fall back
                // on the kerb estimate.
                Judge.PavementAt = Sidewalk.At;
                Judge.OnPavement = (step, what) => Samples.Append("P,").Append(step).Append(',').Append(what.Replace(',', ';')).Append('\n');
                Samples.Append("N,pavement from the pedestrians' NavMesh\n");
            }
            Step++;
            time += Time.fixedDeltaTime;
            var outline = Outline();
            var footprint = new Core.Routing.Vec3[outline.Length];
            for (var i = 0; i < outline.Length; i++) footprint[i] = GleyGraph.ToVec(outline[i]);
            var centre = transform.TransformPoint(boxCenter);
            var failedBefore = Judge.Failed;
            var sample = new Sample
            {
                Step = Step,
                Time = time,
                Centre = GleyGraph.ToVec(centre),
                FrontAxle = GleyGraph.ToVec(FrontAxle()),
                HeadingDegrees = transform.eulerAngles.y,
                Footprint = footprint,
                SpeedMs = body != null ? body.velocity.magnitude : 0f,
                Waiting = Waiting?.Invoke() ?? false,
                NearMiss = NearMiss(),
            };
            Judge.Add(sample);
            Kerb();
            if (Step % 5 == 0) Track.Add(centre);
            if (!failedBefore && Judge.Failed) Latch(outline, centre);
            Record(sample);
        }

        private void Kerb()
        {
            var wheels = Wheels;
            if (wheels == null || wheels.Length < 4 || Judge == null) return;
            kerbSteps ??= new int[wheels.Length];
            kerbReported ??= new bool[wheels.Length];
            foreach (var w in wheels) if (w == null || !w.IsGrounded) return;
            for (var i = 0; i < wheels.Length; i++)
            {
                // Off the plane through three other wheels: a slope or a ramp tilts all four together (turn test 0.1.59:
                // the rear wheels 0.49 m above the front ones on a Hamptons highway ramp read as a kerb).
                // The planner's check measures the same way (Core WheelPlane).
                var points = new Core.Routing.Vec3[wheels.Length];
                for (var j = 0; j < wheels.Length; j++) points[j] = GleyGraph.ToVec(wheels[j].HitPoint);
                if (!(Core.Drive.WheelPlane.Rise(points, i) is float rise)) continue;
                var w = wheels[i];
                kerbSteps[i] = rise > KerbRise ? kerbSteps[i] + 1 : 0;
                if (kerbSteps[i] < KerbSteps || kerbReported[i]) continue;
                kerbReported[i] = true;
                var what = $"kerb: wheel '{w.name}' {rise:0.00} m above the plane of the other wheels, on '{(w.HitCollider != null ? w.HitCollider.name : "?")}' ({(w.HitCollider != null ? LayerMask.LayerToName(w.HitCollider.gameObject.layer) : "?")}) at {w.HitPoint}";
                var failedBefore = Judge.Failed;
                Judge.Contact(Step, what);
                Samples.Append("C,").Append(Step).Append(',').Append(what.Replace(',', ';')).Append('\n');
                if (!failedBefore && Judge.Failed) Latch(Outline(), transform.TransformPoint(boxCenter));
            }
            Roll(wheels);
        }

        // Both wheels of one side up (a car run wide onto the pavement): each wheel lies in the plane of the other
        // three then, so the rise above misses it; the car leans beyond the road's own cross-fall (Core WheelPlane.Roll,
        // as the planner's check; review round 2).
        private void Roll(NWH.Common.Vehicles.WheelUAPI[] wheels)
        {
            var graph = GleyGraph.Graph;
            if (wheels.Length != 4 || graph == null || rollReported) return;
            // Rear left, rear right, front left, front right, by where each wheel sits on the car.
            var ordered = new Core.Routing.Vec3[4];
            var local = new Vector3[4];
            for (var i = 0; i < 4; i++) local[i] = transform.InverseTransformPoint(wheels[i].transform.position);
            var middleZ = (local[0].z + local[1].z + local[2].z + local[3].z) / 4f;
            var filled = 0;
            for (var i = 0; i < 4; i++)
            {
                var slot = (local[i].z > middleZ ? 2 : 0) + (local[i].x > 0f ? 1 : 0);
                ordered[slot] = GleyGraph.ToVec(wheels[i].HitPoint);
                filled |= 1 << slot;
            }
            if (filled != 15) return;
            var centre = transform.TransformPoint(boxCenter);
            var forward = transform.forward;
            var width = Core.Routing.Vec3.HorizontalDistance(ordered[0], ordered[1]);
            if (!(Core.Drive.WheelPlane.CrossFall(graph, GleyGraph.ToVec(centre), Mathf.Atan2(forward.x, forward.z), p => NwhPlanner.GroundBelow(p, transform)) is float fall)) return;
            var roll = Core.Drive.WheelPlane.Roll(ordered, fall * width);
            rollSteps = Mathf.Abs(roll) > KerbRise ? rollSteps + 1 : 0;
            if (rollSteps < KerbSteps) return;
            rollReported = true;
            var what = $"kerb: the {(roll > 0f ? "left" : "right")} wheels {Mathf.Abs(roll):0.00} m above the {(roll > 0f ? "right" : "left")} ones beyond the road's cross-fall at {centre}";
            var failedBefore = Judge!.Failed;
            Judge.Contact(Step, what);
            Samples.Append("C,").Append(Step).Append(',').Append(what.Replace(',', ';')).Append('\n');
            if (!failedBefore && Judge.Failed) Latch(Outline(), centre);
        }

        private void Record(Sample s)
        {
            var b = Samples;
            b.Append("S,").Append(s.Step).Append(',').Append(s.Time.ToString("0.###", Inv));
            Add(b, s.Centre);
            Add(b, s.FrontAxle);
            b.Append(',').Append(s.HeadingDegrees.ToString("0.#", Inv)).Append(',').Append(s.SpeedMs.ToString("0.##", Inv))
             .Append(',').Append(s.Waiting ? 1 : 0).Append(',').Append(s.NearMiss.ToString("0.#", Inv));
            foreach (var p in s.Footprint) Add(b, p);
            b.Append('\n');
        }

        private static void Add(System.Text.StringBuilder b, Core.Routing.Vec3 v) =>
            b.Append(',').Append(v.X.ToString("0.##", Inv)).Append(',').Append(v.Y.ToString("0.##", Inv)).Append(',').Append(v.Z.ToString("0.##", Inv));

        private void Latch(Vector3[] outline, Vector3 centre)
        {
            FailureOutline = outline;
            FailureCentre = centre;
            PictureDue = true;
        }

        private void OnCollisionEnter(Collision collision) => Hit(collision);

        private readonly HashSet<Collider> reported = new HashSet<Collider>();

        private void Hit(Collision collision)
        {
            if (Judge == null || collision.collider == null || !reported.Add(collision.collider)) return;
            var c = collision.collider;
            var layer = c.gameObject.layer;
            var what = $"'{c.name}' (layer {LayerMask.LayerToName(layer)}) at {collision.GetContact(0).point}, {collision.relativeVelocity.magnitude:0.0} m/s";
            var failedBefore = Judge.Failed;
            if ((ObstacleMask & (1 << layer)) != 0) { Judge.Contact(Step, what); Samples.Append("C,").Append(Step).Append(',').Append(what.Replace(',', ';')).Append('\n'); }
            else if (c.attachedRigidbody != null && c.attachedRigidbody.GetComponent<VehicleComponent>() != null) { Judge.Incident(Step, "vehicle " + what); Samples.Append("I,").Append(Step).Append(",vehicle ").Append(what.Replace(',', ';')).Append('\n'); }
            else if (LayerMask.LayerToName(layer) == "Human") { Judge.Incident(Step, "pedestrian " + what); Samples.Append("I,").Append(Step).Append(",pedestrian ").Append(what.Replace(',', ';')).Append('\n'); }
            // A road collider's upright face is a divider or a kerb wall built into the road (Peter: a car stuck on a
            // divider by Vantander Bank, turn test 0.1.41); its top is the road.
            else if (layer == RoadsLayer && Mathf.Abs(collision.GetContact(0).normal.y) < 0.5f) { Judge.Contact(Step, "road edge " + what); Samples.Append("C,").Append(Step).Append(",road edge ").Append(what.Replace(',', ';')).Append('\n'); }
            // Anything else (the road, terrain): recorded, not judged.
            else Samples.Append("O,").Append(Step).Append(',').Append(what.Replace(',', ';')).Append('\n');
            if (!failedBefore && Judge.Failed) Latch(Outline(), transform.TransformPoint(boxCenter));
        }
    }
}
