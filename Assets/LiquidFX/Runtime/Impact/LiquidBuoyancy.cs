using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// Archimedes, not a spring: a Rigidbody sitting in a <see cref="LiquidSurface"/> gets pushed
    /// up by a force proportional to how much of it is under the surface. That single rule is
    /// what produces every behaviour density is supposed to give for free:
    ///
    /// - denser than the liquid: buoyancy never fully cancels gravity, sinks (fast if very dense,
    ///   slow if only a little denser).
    /// - about as dense as the liquid: settles and bobs near the surface.
    /// - much less dense: floats high, and if it is ever forced down to the basin floor the fully
    ///   submerged buoyant force is far larger than its weight, so releasing it there launches it
    ///   back out through the surface instead of drifting up gently.
    ///
    /// Submersion is approximated from the collider's own bounds (linear interpolation between
    /// "top at the surface" and "bottom at the surface"), which is cheap and looks right for the
    /// roughly convex shapes glassware and test props use. It is not a fluid simulation.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class LiquidBuoyancy : MonoBehaviour
    {
        [Tooltip("Leave empty to find a LiquidSurface on this object's parents automatically.")]
        [SerializeField] LiquidSurface surface;

        [SerializeField] Collider volumeCollider;

        [Header("Density (kg/m3 — water is 1000)")]
        [Tooltip("Under 1000 floats, over 1000 sinks. Far under 1000 pops out hard if released underwater.")]
        [SerializeField, Min(1f)] float density = 600f;

        [SerializeField, Min(1f)] float fluidDensity = 1000f;

        [Tooltip("Recomputes the Rigidbody's mass from density x volume, so the density slider is the single source of truth.")]
        [SerializeField] bool driveMass = true;

        [Header("Water Resistance")]
        [Tooltip("Extra drag applied only while submerged, so light objects settle instead of oscillating forever.")]
        [SerializeField, Min(0f)] float submergedLinearDrag = 1.4f;

        [SerializeField, Min(0f)] float submergedAngularDrag = 2f;

        [Tooltip("Caps the buoyant force so a very light object released at the bottom of a deep " +
            "basin pops out convincingly instead of leaving the water via an unplayable spike.")]
        [SerializeField, Min(1f)] float maxBuoyantAccelerationInGs = 6f;

        Rigidbody body;
        float volumeM3;

        public float Density
        {
            get => density;
            set
            {
                density = Mathf.Max(1f, value);
                RecalculateMass();
            }
        }

        void Reset()
        {
            volumeCollider = GetComponent<Collider>();
        }

        void OnEnable()
        {
            body = GetComponent<Rigidbody>();
            if (volumeCollider == null)
                volumeCollider = GetComponent<Collider>();
            if (surface == null)
                surface = GetComponentInParent<LiquidSurface>();

            RecalculateVolume();
            RecalculateMass();
        }

        void OnValidate()
        {
            if (volumeCollider == null)
                volumeCollider = GetComponent<Collider>();

            RecalculateVolume();
            RecalculateMass();
        }

        /// <summary>
        /// Volume from the collider's own bounds, treated as an ellipsoid inscribed in the box.
        /// A sphere collider's bounds are a cube around it, and an ellipsoid inscribed in that cube
        /// has exactly the sphere's volume, so this is exact for spheres and a reasonable estimate
        /// for anything else roughly convex.
        /// </summary>
        void RecalculateVolume()
        {
            if (volumeCollider == null)
            {
                volumeM3 = 0f;
                return;
            }

            Vector3 size = volumeCollider.bounds.size;
            volumeM3 = (4f / 3f) * Mathf.PI * (size.x * 0.5f) * (size.y * 0.5f) * (size.z * 0.5f);
        }

        void RecalculateMass()
        {
            if (!driveMass || body == null || volumeM3 <= 0f)
                return;

            body.mass = Mathf.Max(0.01f, density * volumeM3);
        }

        void FixedUpdate()
        {
            if (surface == null || body == null || volumeCollider == null || volumeM3 <= 0f)
                return;

            Bounds bounds = volumeCollider.bounds;
            float surfaceY = surface.SurfaceWorldY;

            float submergedFraction = Mathf.Clamp01((surfaceY - bounds.min.y) / Mathf.Max(0.0001f, bounds.size.y));
            if (submergedFraction <= 0f)
                return;

            float gravity = Mathf.Abs(Physics.gravity.y);

            float buoyantForce = fluidDensity * volumeM3 * submergedFraction * gravity;
            float maxForce = body.mass * gravity * maxBuoyantAccelerationInGs;
            buoyantForce = Mathf.Min(buoyantForce, maxForce);

            body.AddForce(Vector3.up * buoyantForce, ForceMode.Force);

            // Water resistance, scaled by how much of the object is actually in the water so it
            // blends smoothly through the surface rather than switching on with a jolt.
            body.AddForce(-body.linearVelocity * submergedLinearDrag * submergedFraction * body.mass, ForceMode.Force);
            body.AddTorque(-body.angularVelocity * submergedAngularDrag * submergedFraction * body.mass, ForceMode.Force);
        }
    }
}
