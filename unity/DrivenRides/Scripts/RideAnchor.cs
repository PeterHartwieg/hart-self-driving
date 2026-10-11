#nullable enable
using UnityEngine;

namespace DrivenRides
{
    // The object the player is attached to during a ride. It copies the ride car's pose, so a pooled car that
    // Gley deactivates or reuses never takes the player with it. It copies in Update and again in LateUpdate,
    // because the camera brain also runs in LateUpdate in no fixed order.
    internal sealed class RideAnchor : MonoBehaviour
    {
        public Transform? Target;

        private void Update() => Follow();

        private void LateUpdate() => Follow();

        private void Follow()
        {
            if (Target == null || !Target.gameObject.activeInHierarchy) return;
            transform.SetPositionAndRotation(Target.position, Target.rotation);
        }
    }
}
