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
        // False once the ride car is no longer the ride's: the anchor then keeps its last pose, so the hidden player
        // never jumps along with a pooled car Gley has moved elsewhere.
        public System.Func<bool>? Valid;

        private void Update() => Follow();

        private void LateUpdate() => Follow();

        private void Follow()
        {
            if (Target == null || !Target.gameObject.activeInHierarchy || (Valid != null && !Valid())) return;
            transform.SetPositionAndRotation(Target.position, Target.rotation);
        }
    }
}
