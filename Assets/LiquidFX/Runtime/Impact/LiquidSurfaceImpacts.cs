using System.Collections.Generic;
using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// Turns anything that moves through the liquid surface into ripples and a splash.
    ///
    /// It works off a trigger volume instead of collisions so objects without a Rigidbody still
    /// register, and it rate-limits per collider so a slow moving hand does not fire six ripples
    /// a frame and burn the whole ripple budget.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    public sealed class LiquidSurfaceImpacts : MonoBehaviour
    {
        [SerializeField] LiquidSurface surface;

        [SerializeField] LiquidImpactFX impactFX;

        [Tooltip("Vertical band around the surface counted as a contact, in metres.")]
        [SerializeField, Min(0.001f)] float contactBand = 0.03f;

        [Tooltip("Minimum seconds between two ripples from the same collider.")]
        [SerializeField, Min(0.01f)] float perColliderCooldown = 0.22f;

        [Tooltip("Speed through the surface that produces a full strength splash.")]
        [SerializeField, Min(0.1f)] float loudSpeed = 2.2f;

        [SerializeField, Range(0f, 1f)] float minimumStrength = 0.15f;

        [Tooltip("Vertical speed below which a resting object touching the band does not ripple. " +
            "Without this floor, an object that merely sits in the contact band (a ball floating " +
            "half submerged) would re-trigger a ripple every cooldown forever.")]
        [SerializeField, Min(0f)] float minimumRippleSpeed = 0.08f;

        readonly Dictionary<int, TrackedCollider> tracked = new Dictionary<int, TrackedCollider>(16);
        readonly List<int> staleKeys = new List<int>(16);

        struct TrackedCollider
        {
            public Vector3 LastPosition;
            public float LastRippleTime;
            public float LastSeenTime;
            public bool WasTouching;
        }

        void Reset()
        {
            var trigger = GetComponent<Collider>();
            trigger.isTrigger = true;
        }

        void OnDisable()
        {
            tracked.Clear();
        }

        void LateUpdate()
        {
            if (tracked.Count == 0)
                return;

            // Drop colliders that left the volume without firing OnTriggerExit (destroyed, disabled).
            float now = Time.time;
            staleKeys.Clear();
            foreach (KeyValuePair<int, TrackedCollider> entry in tracked)
            {
                if (now - entry.Value.LastSeenTime > 0.5f)
                    staleKeys.Add(entry.Key);
            }

            for (int i = 0; i < staleKeys.Count; i++)
                tracked.Remove(staleKeys[i]);
        }

        void OnTriggerStay(Collider other)
        {
            if (surface == null || other == null)
                return;

            int key = other.GetInstanceID();
            Bounds bounds = other.bounds;
            Vector3 centre = bounds.center;
            float now = Time.time;

            tracked.TryGetValue(key, out TrackedCollider state);
            bool isNew = state.LastSeenTime <= 0f;

            float surfaceY = surface.SurfaceWorldY;
            bool touching = bounds.min.y <= surfaceY + contactBand && bounds.max.y >= surfaceY - contactBand;

            float verticalSpeed = 0f;
            if (!isNew)
                verticalSpeed = Mathf.Abs(centre.y - state.LastPosition.y) / Mathf.Max(0.0001f, Time.deltaTime);

            state.LastPosition = centre;
            state.LastSeenTime = now;

            // The entry edge has to be the touching transition itself, not "was the whole object
            // underwater": a floating ball never goes fully underwater (its top stays above the
            // band), so gating on submersion left crossedIn permanently true for anything resting
            // at the surface, which was the actual cause of the endless ripples — the speed gate
            // below never got a chance to apply because crossedIn kept bypassing it every frame.
            bool crossedIn = touching && !state.WasTouching;
            state.WasTouching = touching;

            // A ripple needs a reason: either the object just crossed into the band (a genuine
            // entry, always worth a splash) or it is actively moving through it fast enough to
            // disturb the surface. A resting object sitting in the band forever is neither, so it
            // goes quiet instead of re-triggering every cooldown.
            bool isGenuineContact = crossedIn || verticalSpeed >= minimumRippleSpeed;

            if (touching && isGenuineContact && now - state.LastRippleTime >= perColliderCooldown)
            {
                state.LastRippleTime = now;

                float radius = Mathf.Max(0.01f, Mathf.Max(bounds.extents.x, bounds.extents.z) * 0.85f);
                float strength = Mathf.Clamp01(verticalSpeed / loudSpeed);
                // Only a genuine entry gets the strength floor; ongoing contact is scored purely
                // by how fast the object is actually moving, so it fades out as motion settles.
                if (crossedIn)
                    strength = Mathf.Max(minimumStrength, strength);

                Vector3 contact = other.ClosestPoint(new Vector3(centre.x, surfaceY, centre.z));
                contact.y = surfaceY;

                surface.AddImpulseWorld(contact, strength * 1.6f, radius);

                if (impactFX != null && strength > minimumStrength + 0.05f)
                    impactFX.EmitBurst(contact, Vector3.up, strength, surface.LiquidColor);
            }

            tracked[key] = state;
        }

        void OnTriggerExit(Collider other)
        {
            if (other != null)
                tracked.Remove(other.GetInstanceID());
        }
    }
}
