using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// Fixed pool of puddles. Nothing is instantiated after the warm-up, so a long session of
    /// clumsy pouring never grows the heap. Spills that land close to an existing puddle are
    /// merged into it instead of taking a new slot.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LiquidSpillManager : MonoBehaviour
    {
        [SerializeField] LiquidSpillPuddle puddlePrefab;

        [SerializeField, Range(1, 12)] int poolSize = 4;

        [Tooltip("Spills closer than this to a live puddle are merged into it.")]
        [SerializeField, Min(0.01f)] float mergeRadius = 0.35f;

        static LiquidSpillManager instance;

        LiquidSpillPuddle[] pool;
        int nextSlot;

        public static LiquidSpillManager Instance => instance;

        void OnEnable()
        {
            instance = this;
            Warmup();
        }

        void OnDisable()
        {
            if (instance == this)
                instance = null;
        }

        void Warmup()
        {
            if (pool != null || puddlePrefab == null)
                return;

            pool = new LiquidSpillPuddle[poolSize];
            for (int i = 0; i < poolSize; i++)
            {
                LiquidSpillPuddle puddle = Instantiate(puddlePrefab, transform);
                puddle.name = $"Puddle {i:00}";
                puddle.gameObject.SetActive(true);
                puddle.enabled = false;
                var puddleRenderer = puddle.GetComponent<MeshRenderer>();
                if (puddleRenderer != null)
                    puddleRenderer.enabled = false;
                pool[i] = puddle;
            }
        }

        /// <summary>Routes spilled liquid to a puddle. Safe to call every frame with a small amount.</summary>
        public static void Spill(Vector3 worldPoint, Vector3 surfaceNormal, float millilitres, Color color)
        {
            if (millilitres <= 0f)
                return;

            if (instance == null)
            {
                // Silently dropping volume here is how a scene ends up not conserving liquid and
                // nobody noticing for a week, so say it out loud, once.
                if (!warnedAboutMissingManager)
                {
                    warnedAboutMissingManager = true;
                    Debug.LogWarning(
                        "LiquidFX: liquid spilled but there is no LiquidSpillManager in the scene, " +
                        "so the volume is being discarded. Add one to see puddles.");
                }
                return;
            }

            instance.SpillInternal(worldPoint, surfaceNormal, millilitres, color);
        }

        static bool warnedAboutMissingManager;

        void SpillInternal(Vector3 worldPoint, Vector3 surfaceNormal, float millilitres, Color color)
        {
            Warmup();
            if (pool == null)
                return;

            float mergeSqr = mergeRadius * mergeRadius;
            for (int i = 0; i < pool.Length; i++)
            {
                LiquidSpillPuddle candidate = pool[i];
                if (candidate == null || !candidate.IsLive)
                    continue;

                if ((candidate.transform.position - worldPoint).sqrMagnitude <= mergeSqr)
                {
                    candidate.AddML(millilitres);
                    return;
                }
            }

            LiquidSpillPuddle free = FindFreeSlot();
            if (free == null)
                return;

            free.ResetPuddle(worldPoint, surfaceNormal, color);
            free.AddML(millilitres);
        }

        LiquidSpillPuddle FindFreeSlot()
        {
            for (int i = 0; i < pool.Length; i++)
            {
                LiquidSpillPuddle candidate = pool[(nextSlot + i) % pool.Length];
                if (candidate != null && !candidate.IsLive)
                {
                    nextSlot = (nextSlot + i + 1) % pool.Length;
                    return candidate;
                }
            }

            // Everything is live: recycle the oldest slot rather than allocating a new puddle.
            LiquidSpillPuddle recycled = pool[nextSlot];
            nextSlot = (nextSlot + 1) % pool.Length;
            return recycled;
        }
    }
}
