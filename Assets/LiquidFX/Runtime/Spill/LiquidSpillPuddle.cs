using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// Liquid that missed the target and ended up on a surface.
    ///
    /// The radius comes from the actual volume it received divided by a film thickness, so
    /// spilling 20 mL makes a small puddle and spilling 400 mL makes a large one. When the flow
    /// stops the puddle waits, then dries from the rim inward and switches itself off; the
    /// renderer, the material block and the component all go quiet, which is the point: a spill
    /// that happened two minutes ago must not cost anything.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class LiquidSpillPuddle : MonoBehaviour
    {
        static readonly int GrowthId = Shader.PropertyToID("_Growth");
        static readonly int DrynessId = Shader.PropertyToID("_Dryness");
        static readonly int ColorId = Shader.PropertyToID("_BaseColor");
        static readonly int SeedId = Shader.PropertyToID("_Seed");

        [Header("Shape")]
        [Tooltip("Thickness of the liquid film in metres. Water beading on a smooth bench sits around 2 mm.")]
        [SerializeField, Min(0.0002f)] float filmThickness = 0.002f;

        [Tooltip("Lift above the surface it spilled on, in metres. The puddle sits exactly on the " +
            "floor's own collider hit point otherwise, and two coplanar surfaces z-fight into a " +
            "flickering mess instead of one clean puddle.")]
        [SerializeField, Min(0.0005f)] float surfaceLift = 0.003f;

        [SerializeField, Min(0.01f)] float maximumRadius = 0.45f;

        [Tooltip("Seconds for the visible edge to catch up with the volume already received.")]
        [SerializeField, Min(0.01f)] float spreadSeconds = 0.55f;

        [Header("Drying")]
        [Tooltip("Seconds of no new liquid before the puddle starts to dry.")]
        [SerializeField, Min(0f)] float dryDelay = 2.5f;

        [Tooltip("Seconds the drying animation takes.")]
        [SerializeField, Min(0.1f)] float drySeconds = 6f;

        [Header("Appearance")]
        [SerializeField] Color liquidColor = new Color(0.55f, 0.82f, 0.9f, 0.85f);

        MeshRenderer puddleRenderer;
        MaterialPropertyBlock properties;

        float contentsML;
        float targetRadius;
        float currentRadius;
        float idleTimer;
        float dryness;
        float seed;
        bool live;

        public bool IsLive => live;
        public float ContentsML => contentsML;

        void OnEnable()
        {
            Cache();
            seed = Random.value * 100f;
            Apply();
        }

        void OnDisable()
        {
            live = false;
        }

        /// <summary>Resets the puddle for reuse from a pool.</summary>
        public void ResetPuddle(Vector3 worldPosition, Vector3 surfaceNormal, Color color)
        {
            Cache();
            Vector3 normal = surfaceNormal.sqrMagnitude > 0.0001f ? surfaceNormal.normalized : Vector3.up;
            transform.position = worldPosition + normal * surfaceLift;
            transform.rotation = Quaternion.FromToRotation(Vector3.up, normal);

            liquidColor = color;
            contentsML = 0f;
            currentRadius = 0f;
            targetRadius = 0f;
            dryness = 0f;
            idleTimer = 0f;
            seed = Random.value * 100f;
            live = true;
            enabled = true;
            if (puddleRenderer != null)
                puddleRenderer.enabled = true;

            Apply();
        }

        /// <summary>Pours liquid onto the puddle. Call every frame while the spill is running.</summary>
        public void AddML(float millilitres)
        {
            if (millilitres <= 0f)
                return;

            live = true;
            enabled = true;
            if (puddleRenderer != null && !puddleRenderer.enabled)
                puddleRenderer.enabled = true;

            contentsML += millilitres;
            idleTimer = 0f;
            dryness = 0f;

            // volume = area * thickness  ->  radius = sqrt(volume / (pi * thickness))
            float volumeM3 = LiquidFXRuntime.MillilitresToCubicMetres(contentsML);
            targetRadius = Mathf.Min(maximumRadius, Mathf.Sqrt(volumeM3 / (Mathf.PI * filmThickness)));
        }

        void LateUpdate()
        {
            if (!live)
                return;

            float deltaTime = Application.isPlaying ? Time.deltaTime : 0f;
            if (deltaTime <= 0f)
            {
                Apply();
                return;
            }

            currentRadius = Mathf.MoveTowards(
                currentRadius,
                targetRadius,
                Mathf.Max(0.001f, maximumRadius / spreadSeconds) * deltaTime);

            idleTimer += deltaTime;
            if (idleTimer > dryDelay)
                dryness = Mathf.Clamp01(dryness + deltaTime / drySeconds);

            Apply();

            if (dryness >= 1f)
                Retire();
        }

        void Retire()
        {
            live = false;
            contentsML = 0f;
            currentRadius = 0f;
            targetRadius = 0f;
            if (puddleRenderer != null)
                puddleRenderer.enabled = false;

            // Nothing left to animate: stop taking a slot in the update list.
            enabled = false;
        }

        void Cache()
        {
            if (puddleRenderer == null)
                puddleRenderer = GetComponent<MeshRenderer>();
            properties ??= new MaterialPropertyBlock();
            if (puddleRenderer != null)
            {
                puddleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                puddleRenderer.receiveShadows = false;
            }
        }

        void Apply()
        {
            Cache();
            if (puddleRenderer == null)
                return;

            float diameter = Mathf.Max(0.001f, currentRadius * 2f);
            Vector3 scale = transform.localScale;
            if (!Mathf.Approximately(scale.x, diameter) || !Mathf.Approximately(scale.z, diameter))
                transform.localScale = new Vector3(diameter, 1f, diameter);

            puddleRenderer.GetPropertyBlock(properties);
            properties.SetFloat(GrowthId, targetRadius <= 0.0001f ? 0f : Mathf.Clamp01(currentRadius / targetRadius));
            properties.SetFloat(DrynessId, dryness);
            properties.SetFloat(SeedId, seed);
            properties.SetColor(ColorId, liquidColor);
            puddleRenderer.SetPropertyBlock(properties);
        }
    }
}
