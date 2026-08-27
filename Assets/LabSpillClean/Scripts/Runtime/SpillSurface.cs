using UnityEngine;

namespace LabSpill
{
    public enum SpillSurfaceKind { Bench, Ground }

    [DisallowMultipleComponent, RequireComponent(typeof(Collider))]
    public sealed class SpillSurface : MonoBehaviour
    {
        public SpillSurfaceKind kind;
        public Collider Collider => GetComponent<Collider>();
    }
}
