using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// Ambient sparkle motes drifting on standing water: small glinting points scattered across
    /// the surface, independent of ripples or impacts. This is the "make the water a little
    /// fantastical" layer — real sinks don't sparkle, stylised ones do.
    ///
    /// The emitter is a child of the water surface, so it rises with the level for free. This
    /// component only toggles emission on and off with the water and reports whether particles
    /// are currently warranted, so nothing burns particle budget on a dry basin.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LiquidSurfaceSparkles : MonoBehaviour
    {
        [SerializeField] LiquidSurface surface;

        [SerializeField] ParticleSystem sparkles;

        [Tooltip("Below this much liquid, sparkles stop; there is nothing to glint on a dry basin.")]
        [SerializeField, Min(0f)] float minimumContentsML = 5f;

        [Tooltip("Motes per second on a full-size basin at the high quality tier.")]
        [SerializeField, Min(0f)] float baseRatePerSecond = 5f;

        bool active;

        void OnDisable()
        {
            SetActive(false);
        }

        void LateUpdate()
        {
            if (surface == null || sparkles == null)
                return;

            SetActive(surface.ContentsML > minimumContentsML);
        }

        void SetActive(bool value)
        {
            if (active == value)
                return;

            active = value;

            var emission = sparkles.emission;
            if (value)
            {
                float budget = LiquidFXRuntime.ParticleBudget / 48f;
                emission.rateOverTime = baseRatePerSecond * budget;
                emission.enabled = true;
                if (!sparkles.isPlaying)
                    sparkles.Play(false);
            }
            else
            {
                emission.enabled = false;
                sparkles.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }
}
