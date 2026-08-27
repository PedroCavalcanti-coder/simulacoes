using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// A rectangular body of standing liquid: a sink basin, a tray, a tank.
    /// Two things separate it from the earlier prototype surface:
    ///
    /// 1. The level is driven by a real volume in millilitres divided by the basin area, so a
    ///    faucet running at 90 mL/s raises it at a rate you can predict, and a drain lowers it.
    /// 2. Ripples are expressed in metres relative to the basin centre instead of UV space, so a
    ///    4 cm impact ring is 4 cm wide no matter how the quad is scaled.
    ///
    /// The mesh is a flat grid in the XZ plane spanning -0.5..0.5, scaled by the transform.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class LiquidSurface : MonoBehaviour, ILiquidContainer
    {
        const int MaxRipples = 6;

        static readonly int RippleCountId = Shader.PropertyToID("_RippleCount");
        static readonly int RippleDataId = Shader.PropertyToID("_RippleData");
        static readonly int RippleParamsId = Shader.PropertyToID("_RippleParams");
        static readonly int ContinuousRippleId = Shader.PropertyToID("_ContinuousRipple");
        static readonly int SurfaceSizeId = Shader.PropertyToID("_SurfaceSize");
        static readonly int RippleSpeedId = Shader.PropertyToID("_RippleSpeed");
        static readonly int RippleWavelengthId = Shader.PropertyToID("_RippleWavelength");
        static readonly int RippleSpatialDecayId = Shader.PropertyToID("_RippleSpatialDecay");
        static readonly int RippleTimeDecayId = Shader.PropertyToID("_RippleTimeDecay");
        static readonly int RippleLifetimeId = Shader.PropertyToID("_RippleLifetime");
        static readonly int RippleAmplitudeId = Shader.PropertyToID("_RippleAmplitude");
        static readonly int TintId = Shader.PropertyToID("_LiquidTint");

        [Header("Basin Geometry (world space)")]
        [Tooltip("Height of the inner floor of the basin. The surface can never go below it.")]
        [SerializeField] float basinFloorY = 0.79f;

        [Tooltip("Height of the rim. Anything above this overflows.")]
        [SerializeField] float basinRimY = 1.02f;

        [Header("Contents")]
        [SerializeField, Min(0f)] float startingContentsML = 4000f;

        [Tooltip("Drain rate while the plug is open.")]
        [SerializeField, Min(0f)] float drainMLPerSecond = 900f;

        [SerializeField] bool drainOpen;

        [Header("Ripples (metres, seconds)")]
        [Tooltip("Speed of the expanding wave front.")]
        [SerializeField, Range(0.05f, 3f)] float rippleSpeed = 0.55f;

        [Tooltip("Distance between two crests.")]
        [SerializeField, Range(0.005f, 0.4f)] float rippleWavelength = 0.055f;

        [Tooltip("How quickly the wave dies behind the front, per metre.")]
        [SerializeField, Range(0.5f, 40f)] float rippleSpatialDecay = 9f;

        [Tooltip("How quickly the wave dies with age, per second.")]
        [SerializeField, Range(0.1f, 8f)] float rippleTimeDecay = 1.35f;

        [SerializeField, Range(0.25f, 6f)] float rippleLifetime = 2.4f;

        [Tooltip("Crest height of a unit strength impact.")]
        [SerializeField, Range(0.0005f, 0.05f)] float rippleAmplitude = 0.0035f;

        [Header("Appearance")]
        [SerializeField] Color liquidTint = new Color(0.35f, 0.72f, 0.78f, 1f);

        [Tooltip("Hides the renderer once the basin is essentially dry, to save fill rate.")]
        [SerializeField, Min(0f)] float hideBelowML = 1f;

        readonly Vector4[] rippleData = new Vector4[MaxRipples];
        readonly Vector4[] rippleParams = new Vector4[MaxRipples];

        MeshRenderer surfaceRenderer;
        MaterialPropertyBlock properties;
        Vector4 continuousRipple;
        float contentsML;
        int rippleCount;
        int nextRippleIndex;
        bool initialised;
        bool stateDirty = true;

        public Transform Transform => transform;
        public Color LiquidColor => liquidTint;
        public float ContentsML => contentsML;
        public float FreeML => Mathf.Max(0f, CapacityML - contentsML);
        public bool DrainOpen { get => drainOpen; set => drainOpen = value; }
        public float BasinFloorY => basinFloorY;
        public float BasinRimY => basinRimY;

        /// <summary>Inner width and depth of the basin in metres, taken from the transform scale.</summary>
        public Vector2 BasinSize
        {
            get
            {
                Vector3 scale = transform.lossyScale;
                return new Vector2(Mathf.Max(0.001f, Mathf.Abs(scale.x)), Mathf.Max(0.001f, Mathf.Abs(scale.z)));
            }
        }

        public float BasinAreaM2
        {
            get
            {
                Vector2 size = BasinSize;
                return size.x * size.y;
            }
        }

        public float CapacityML =>
            LiquidFXRuntime.CubicMetresToMillilitres(BasinAreaM2 * Mathf.Max(0f, basinRimY - basinFloorY));

        public float SurfaceWorldY
        {
            get
            {
                float heightM = LiquidFXRuntime.MillilitresToCubicMetres(contentsML) / Mathf.Max(0.0001f, BasinAreaM2);
                return Mathf.Min(basinRimY, basinFloorY + heightM);
            }
        }

        public Vector3 SurfaceCentreWorld
        {
            get
            {
                Vector3 centre = transform.position;
                centre.y = SurfaceWorldY;
                return centre;
            }
        }

        void OnEnable()
        {
            Cache();
            if (!initialised)
            {
                contentsML = Mathf.Clamp(startingContentsML, 0f, CapacityML);
                initialised = true;
            }
            stateDirty = true;
            ApplyHeight();
            PushState();
            LiquidContainerRegistry.Register(this);
        }

        void OnDisable()
        {
            LiquidContainerRegistry.Unregister(this);
            rippleCount = 0;
            nextRippleIndex = 0;
            continuousRipple = Vector4.zero;
            stateDirty = true;
        }

        void OnValidate()
        {
            basinRimY = Mathf.Max(basinRimY, basinFloorY + 0.01f);
            Cache();
            if (!Application.isPlaying)
                contentsML = Mathf.Clamp(startingContentsML, 0f, CapacityML);
            stateDirty = true;
            ApplyHeight();
            PushState();
        }

        /// <summary>
        /// Sets the basin's floor and rim height directly. Used by the Scene-view box handle
        /// (<c>LiquidSurfaceEditor</c>) so dragging the gizmo edits the same numbers the floor/rim
        /// fields expose in the Inspector, instead of a separate shadow representation.
        /// </summary>
        public void SetBasinHeights(float floorY, float rimY)
        {
            basinFloorY = floorY;
            basinRimY = Mathf.Max(rimY, floorY + 0.01f);
            if (!Application.isPlaying)
                contentsML = Mathf.Clamp(startingContentsML, 0f, CapacityML);
            stateDirty = true;
            ApplyHeight();
        }

        void LateUpdate()
        {
            if (drainOpen && contentsML > 0f)
            {
                contentsML = Mathf.Max(0f, contentsML - drainMLPerSecond * Time.deltaTime);
                ApplyHeight();
            }

            ExpireRipples();

            if (stateDirty)
                PushState();
        }

        // ------------------------------------------------------------------ ILiquidContainer

        public bool IsAbovePort(Vector3 worldPoint)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            return local.x >= -0.5f && local.x <= 0.5f && local.z >= -0.5f && local.z <= 0.5f;
        }

        public float AddML(float millilitres, Color color)
        {
            if (millilitres <= 0f)
                return 0f;

            float accepted = Mathf.Min(millilitres, FreeML);
            if (accepted <= 0f)
                return 0f;

            float total = contentsML + accepted;
            if (total > 0f)
            {
                liquidTint = Color.Lerp(liquidTint, color, Mathf.Clamp01(accepted / total) * 0.35f);
                stateDirty = true;
            }

            contentsML = total;
            ApplyHeight();
            return accepted;
        }

        public float RemoveML(float millilitres)
        {
            if (millilitres <= 0f)
                return 0f;

            float removed = Mathf.Min(millilitres, contentsML);
            contentsML -= removed;
            ApplyHeight();
            return removed;
        }

        public void SetContentsML(float millilitres)
        {
            contentsML = Mathf.Clamp(millilitres, 0f, CapacityML);
            initialised = true;
            ApplyHeight();
        }

        // ------------------------------------------------------------------ ripples

        /// <summary>
        /// One-shot circular ripple. <paramref name="radius"/> is the initial cavity radius in
        /// metres, <paramref name="strength"/> scales the crest height.
        /// </summary>
        public void AddImpulseWorld(Vector3 worldPosition, float strength = 1f, float radius = 0.03f)
        {
            if (!Application.isPlaying || contentsML <= hideBelowML)
                return;

            if (!TryWorldToLocalMetres(worldPosition, out Vector2 local))
                return;

            rippleData[nextRippleIndex] = new Vector4(local.x, local.y, Time.time, Mathf.Max(0.01f, strength));
            rippleParams[nextRippleIndex] = new Vector4(Mathf.Max(0.002f, radius), 0f, 0f, 0f);
            nextRippleIndex = (nextRippleIndex + 1) % MaxRipples;
            rippleCount = Mathf.Min(rippleCount + 1, MaxRipples);
            stateDirty = true;
        }

        /// <summary>Standing disturbance under a continuous stream. Call every frame while pouring.</summary>
        public void SetContinuousRippleWorld(Vector3 worldPosition, float intensity, float radius = 0.03f)
        {
            if (!Application.isPlaying || intensity <= 0.001f || !TryWorldToLocalMetres(worldPosition, out Vector2 local))
            {
                ClearContinuousRipple();
                return;
            }

            var next = new Vector4(local.x, local.y, Mathf.Clamp01(intensity), Mathf.Max(0.004f, radius));
            if (next != continuousRipple)
            {
                continuousRipple = next;
                stateDirty = true;
            }
        }

        public void ClearContinuousRipple()
        {
            if (continuousRipple.z <= 0f)
                return;

            continuousRipple.z = 0f;
            stateDirty = true;
        }

        /// <summary>Drops the ripple count back to zero once every entry has expired.</summary>
        void ExpireRipples()
        {
            if (rippleCount == 0)
                return;

            float now = Time.time;
            for (int i = 0; i < rippleCount; i++)
            {
                if (now - rippleData[i].z <= rippleLifetime)
                    return;
            }

            rippleCount = 0;
            nextRippleIndex = 0;
            stateDirty = true;
        }

        bool TryWorldToLocalMetres(Vector3 worldPosition, out Vector2 localMetres)
        {
            Vector3 local = transform.InverseTransformPoint(worldPosition);
            Vector2 size = BasinSize;
            localMetres = new Vector2(local.x * size.x, local.z * size.y);
            return local.x >= -0.65f && local.x <= 0.65f && local.z >= -0.65f && local.z <= 0.65f;
        }

        // ------------------------------------------------------------------ plumbing

        void Cache()
        {
            if (surfaceRenderer == null)
                surfaceRenderer = GetComponent<MeshRenderer>();
            properties ??= new MaterialPropertyBlock();
        }

        void ApplyHeight()
        {
            Vector3 position = transform.position;
            float target = SurfaceWorldY;
            if (!Mathf.Approximately(position.y, target))
            {
                position.y = target;
                transform.position = position;
            }

            Cache();
            if (surfaceRenderer != null)
            {
                bool visible = contentsML > hideBelowML;
                if (surfaceRenderer.enabled != visible)
                    surfaceRenderer.enabled = visible;
            }
        }

        void PushState()
        {
            Cache();
            if (surfaceRenderer == null)
                return;

            stateDirty = false;
            Vector2 size = BasinSize;

            surfaceRenderer.GetPropertyBlock(properties);
            properties.SetInteger(RippleCountId, rippleCount);
            properties.SetVectorArray(RippleDataId, rippleData);
            properties.SetVectorArray(RippleParamsId, rippleParams);
            properties.SetVector(ContinuousRippleId, continuousRipple);
            properties.SetVector(SurfaceSizeId, new Vector4(size.x, size.y, 0f, 0f));
            properties.SetFloat(RippleSpeedId, rippleSpeed);
            properties.SetFloat(RippleWavelengthId, rippleWavelength);
            properties.SetFloat(RippleSpatialDecayId, rippleSpatialDecay);
            properties.SetFloat(RippleTimeDecayId, rippleTimeDecay);
            properties.SetFloat(RippleLifetimeId, rippleLifetime);
            properties.SetFloat(RippleAmplitudeId, rippleAmplitude);
            properties.SetColor(TintId, liquidTint);
            surfaceRenderer.SetPropertyBlock(properties);
        }

        [ContextMenu("Test Centre Impulse")]
        void TestCentreImpulse() => AddImpulseWorld(SurfaceCentreWorld, 1.4f, 0.035f);

        void OnDrawGizmosSelected()
        {
            Vector2 size = BasinSize;
            Vector3 centre = transform.position;
            centre.y = basinFloorY;
            Gizmos.color = new Color(0.4f, 0.6f, 1f, 0.5f);
            Gizmos.DrawWireCube(centre, new Vector3(size.x, 0.001f, size.y));
            centre.y = basinRimY;
            Gizmos.color = new Color(1f, 0.4f, 0.3f, 0.5f);
            Gizmos.DrawWireCube(centre, new Vector3(size.x, 0.001f, size.y));
        }
    }
}
