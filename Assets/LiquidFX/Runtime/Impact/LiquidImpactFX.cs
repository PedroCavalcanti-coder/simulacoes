using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// Everything that happens where a stream lands: the crown of thin sheets, the sparse
    /// droplets that bounce off, the expanding surface ring and, on hard impacts, entrained
    /// bubbles under the surface.
    ///
    /// Emission rates are derived from the impact speed and the flow rate and then clamped
    /// against the global particle budget, so turning a faucet to maximum cannot blow the frame
    /// time on a phone.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LiquidImpactFX : MonoBehaviour
    {
        [Header("Systems")]
        [SerializeField] ParticleSystem crownSheets;
        [SerializeField] ParticleSystem droplets;
        [SerializeField] ParticleSystem surfaceRing;
        [SerializeField] ParticleSystem bubbles;
        [SerializeField] ParticleSystem splashBurst;

        [Header("Continuous Rates (at full intensity)")]
        [SerializeField, Min(0f)] float crownPerSecond = 26f;
        [SerializeField, Min(0f)] float dropletsPerSecond = 14f;
        [SerializeField, Min(0f)] float ringsPerSecond = 7f;
        [SerializeField, Min(0f)] float bubblesPerSecond = 10f;

        [Tooltip("Air only entrains into an actual body of liquid. Turn off for a stream landing " +
            "on a dry floor or bench, where bubbles rising out of nothing would look wrong.")]
        [SerializeField] bool includeBubbles = true;

        [Header("Thresholds")]
        [Tooltip("Impact speed below which nothing splashes; the liquid just merges in.")]
        [SerializeField, Min(0f)] float quietSpeed = 0.6f;

        [Tooltip("Impact speed that produces the full effect.")]
        [SerializeField, Min(0.1f)] float loudSpeed = 3.6f;

        [Tooltip("Bubbles only appear past this normalised strength.")]
        [SerializeField, Range(0f, 1f)] float bubbleThreshold = 0.45f;

        bool active;
        bool landedPrevious;

        void OnDisable()
        {
            SetContinuousActive(false);
        }

        /// <summary>
        /// Drives the continuous splash under a running stream.
        /// <paramref name="intensity"/> is the normalised flow (0..1),
        /// <paramref name="impactSpeed"/> is metres per second at the surface.
        /// </summary>
        public void SetContinuous(Vector3 worldPoint, Vector3 surfaceNormal, float intensity, float impactSpeed, Color color)
        {
            if (intensity <= 0.001f)
            {
                SetContinuousActive(false);
                landedPrevious = false;
                return;
            }

            transform.position = worldPoint;
            if (surfaceNormal.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(surfaceNormal);

            float strength = Mathf.Clamp01(Mathf.InverseLerp(quietSpeed, loudSpeed, impactSpeed)) * Mathf.Clamp01(intensity);
            float budget = LiquidFXRuntime.ParticleBudget / 48f;

            SetRate(crownSheets, crownPerSecond * strength * budget, color);
            SetRate(droplets, dropletsPerSecond * strength * budget, color);
            SetRate(surfaceRing, ringsPerSecond * Mathf.Clamp01(intensity) * budget, color);
            if (includeBubbles)
                SetRate(bubbles, strength > bubbleThreshold ? bubblesPerSecond * strength * budget : 0f, color);

            // The instant the stream first lands is its own moment - a quick, energetic pop before
            // the steady splash settles in - not just the continuous rate ramping up from zero.
            if (!landedPrevious)
                EmitLandingBurst(strength, budget, color);
            landedPrevious = true;

            SetContinuousActive(true);
        }

        void EmitLandingBurst(float strength, float budget, Color color)
        {
            if (strength <= 0.01f)
                return;

            Emit(crownSheets, Mathf.RoundToInt(10f * strength * budget), color);
            Emit(droplets, Mathf.RoundToInt(9f * strength * budget), color);
            Emit(splashBurst, Mathf.RoundToInt(Mathf.Lerp(1f, 3f, strength)), color);
        }

        /// <summary>One-off splash, for an object dropped into the liquid or a single drip landing.</summary>
        public void EmitBurst(Vector3 worldPoint, Vector3 surfaceNormal, float strength, Color color)
        {
            strength = Mathf.Clamp01(strength);
            if (strength <= 0.01f)
                return;

            transform.position = worldPoint;
            if (surfaceNormal.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(surfaceNormal);

            float budget = LiquidFXRuntime.ParticleBudget / 48f;
            Emit(crownSheets, Mathf.RoundToInt(6f * strength * budget), color);
            Emit(droplets, Mathf.RoundToInt(5f * strength * budget), color);
            Emit(surfaceRing, 1, color);
            Emit(splashBurst, 1, color);
            if (includeBubbles && strength > bubbleThreshold)
                Emit(bubbles, Mathf.RoundToInt(4f * strength * budget), color);
        }

        void SetContinuousActive(bool value)
        {
            if (active == value)
                return;

            active = value;
            Toggle(crownSheets, value);
            Toggle(droplets, value);
            Toggle(surfaceRing, value);
            if (includeBubbles)
                Toggle(bubbles, value);
        }

        void Toggle(ParticleSystem system, bool value)
        {
            if (system == null)
                return;

            var emission = system.emission;
            emission.enabled = value;

            if (value)
            {
                if (!system.isPlaying)
                    system.Play(false);
            }
            else
            {
                // Stop emitting but let the live particles finish their lifetime.
                system.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        void SetRate(ParticleSystem system, float ratePerSecond, Color color)
        {
            if (system == null)
                return;

            var emission = system.emission;
            emission.rateOverTime = Mathf.Max(0f, ratePerSecond);

            var main = system.main;
            Color tinted = color;
            tinted.a = main.startColor.color.a;
            main.startColor = tinted;
        }

        static void Emit(ParticleSystem system, int count, Color color)
        {
            if (system == null || count <= 0)
                return;

            var parameters = new ParticleSystem.EmitParams
            {
                applyShapeToPosition = true
            };
            Color tinted = color;
            tinted.a = system.main.startColor.color.a;
            parameters.startColor = tinted;
            system.Emit(parameters, count);
        }
    }
}
