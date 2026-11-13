#nullable enable
using UnityEngine;

namespace DrivenRides
{
    internal sealed class RideAnchor : MonoBehaviour
    {
        public Transform? Target;
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
