#nullable enable
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace DrivenRides
{
    // Spike builds: what the ride car does, for finding where it goes wrong away from the game. Four samples a
    // second to <LocalLow>/.../DrivenRides/ride-trace.csv (the latest ride only), and every collision with
    // something that isn't a vehicle to the log.
    internal sealed class RideTrace
    {
        private readonly StringBuilder lines = new StringBuilder("t,x,z,heading,speed,target,state,along,left,path\n");
        private float next;
        private float time;

        public void Sample(Vector3 position, float heading, float speed, int target, string state, double along, double left, int pathLeft)
        {
            time += Time.unscaledDeltaTime;
            if (time < next) return;
            next = time + 0.25f;
            var inv = CultureInfo.InvariantCulture;
            lines.Append(time.ToString("0.00", inv)).Append(',').Append(position.x.ToString("0.0", inv)).Append(',').Append(position.z.ToString("0.0", inv))
                 .Append(',').Append(heading.ToString("0", inv)).Append(',').Append(speed.ToString("0.0", inv)).Append(',').Append(target)
                 .Append(',').Append(state).Append(',').Append(along.ToString("0", inv)).Append(',').Append(left.ToString("0", inv)).Append(',').Append(pathLeft).Append('\n');
        }

        public void Note(string text) => lines.Append("# ").Append(time.ToString("0.00", CultureInfo.InvariantCulture)).Append(' ').Append(text).Append('\n');

        // A ride's trace goes to ride-trace.csv (the latest only); a test ride's to the file it names.
        public void Save(string? file = null)
        {
            try
            {
                var path = file ?? Path.Combine(GleyGraph.DataFolder, "ride-trace.csv");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, lines.ToString());
            }
            catch (System.Exception ex) { Log.Error("Ride trace: " + ex.Message); }
        }
    }

    // On the ride car while it carries the player: logs what it hits, at most once a second.
    internal sealed class CollisionProbe : MonoBehaviour
    {
        public RideTrace? Trace;
        private float quietUntil;

        private void OnCollisionEnter(Collision collision)
        {
            if (Time.unscaledTime < quietUntil || collision.collider == null) return;
            quietUntil = Time.unscaledTime + 1f;
            var body = collision.collider.attachedRigidbody;
            var vehicle = body != null ? body.GetComponent<GleyTrafficSystem.VehicleComponent>() : null;
            var what = vehicle != null ? $"vehicle '{body!.name}'" : $"'{collision.collider.name}' (layer {LayerMask.LayerToName(collision.collider.gameObject.layer)})";
            var text = $"Collision with {what} at {transform.position}, {collision.relativeVelocity.magnitude:0.0} m/s.";
            Log.Info(text);
            Trace?.Note(text);
        }
    }
}
