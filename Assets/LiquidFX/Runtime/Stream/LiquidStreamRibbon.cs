using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// The falling stream, drawn as one procedural camera facing ribbon along the real ballistic
    /// curve. One mesh, one draw call, a couple of dozen vertices, no particle simulation.
    ///
    /// The width follows mass continuity (radius proportional to 1/sqrt(speed)), which is what
    /// makes a jet visibly thin out as it accelerates downward. Starting and stopping are
    /// animated by moving a head and a tail along the curve rather than fading the whole thing
    /// out, so a closed faucet breaks the stream instead of dissolving it.
    ///
    /// The mesh is rebuilt into preallocated arrays every frame while flowing: zero GC.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class LiquidStreamRibbon : MonoBehaviour
    {
        public enum StreamState
        {
            Idle,
            Flowing,
            Breaking
        }

        const int MaxSegments = 32;
        const float MinimumSpeed = 0.35f;

        static readonly int ColorId = Shader.PropertyToID("_BaseColor");

        [Header("Shape")]
        [Tooltip("Radius of the stream at the lip when the flow is at its reference rate.")]
        [SerializeField, Min(0.0005f)] float lipRadius = 0.006f;

        [Tooltip("Flow that produces exactly lipRadius at the lip.")]
        [SerializeField, Min(1f)] float referenceFlowMLPerSecond = 90f;

        [Tooltip("Extra width so a thin jet still covers at least one pixel on a phone screen.")]
        [SerializeField, Min(0f)] float minimumRadius = 0.0018f;

        [Tooltip("Global width multiplier for art direction.")]
        [SerializeField, Range(0.2f, 4f)] float widthMultiplier = 1.35f;

        [Tooltip("Longest fall the ribbon will ever draw, in metres.")]
        [SerializeField, Min(0.1f)] float maximumFallDistance = 4f;

        [Header("Break-up")]
        [Tooltip("Distance below the lip where the jet starts wobbling and necking.")]
        [SerializeField, Min(0f)] float breakupStartDistance = 0.18f;

        [Tooltip("Amplitude of the neck wobble, as a fraction of the local radius.")]
        [SerializeField, Range(0f, 1f)] float breakupStrength = 0.35f;

        [SerializeField, Range(0f, 40f)] float breakupFrequency = 16f;

        [SerializeField, Range(0f, 20f)] float breakupSpeed = 7f;

        [Header("Start / Stop")]
        [Tooltip("Seconds for the ribbon to reach full width when the flow opens.")]
        [SerializeField, Min(0.01f)] float openSeconds = 0.12f;

        [Tooltip("Extra speed given to the tail when the flow closes, so the stream visibly snaps.")]
        [SerializeField, Range(1f, 4f)] float tailSnapMultiplier = 1.45f;

        [Header("Surface Texture")]
        [SerializeField, Min(0.1f)] float uvTilingPerMetre = 3.2f;

        [SerializeField] float uvScrollSpeed = 1.6f;

        [Header("Fallback Collision")]
        [Tooltip("Used only when no target plane has been supplied, e.g. liquid falling on the floor.")]
        [SerializeField] LayerMask impactMask = ~0;

        [SerializeField, Min(0.02f)] float impactRefreshInterval = 0.1f;

        Mesh mesh;
        MeshFilter meshFilter;
        MeshRenderer meshRenderer;
        MaterialPropertyBlock properties;

        Vector3[] vertices;
        Vector2[] uvs;
        Color[] colors;
        int[] indices;

        readonly Vector3[] pathPoint = new Vector3[MaxSegments + 1];
        readonly float[] pathSpeed = new float[MaxSegments + 1];
        readonly float[] pathTime = new float[MaxSegments + 1];
        readonly float[] pathDistance = new float[MaxSegments + 1];
        int pathSamples;
        float pathLength;

        Vector3 origin;
        Vector3 velocity;
        float flowMLPerSecond;
        float targetFlowMLPerSecond;
        Color liquidColor = new Color(0.72f, 0.92f, 1f, 1f);

        bool hasTargetPlane;
        float targetPlaneY;

        float headDistance;
        float tailDistance;
        float impactRefreshTimer;
        float dormantTimer;
        int builtVertexCount;
        int builtIndexCount;

        StreamState state = StreamState.Idle;

        public StreamState State => state;
        public bool IsVisible => state != StreamState.Idle;

        /// <summary>World point where the stream currently lands.</summary>
        public Vector3 ImpactPointWorld { get; private set; }

        /// <summary>True once the head of the stream has actually reached the impact point.</summary>
        public bool HeadHasLanded => pathLength > 0f && headDistance >= pathLength - 0.0005f;

        /// <summary>Seconds a droplet needs to travel from the lip to the impact point.</summary>
        public float TravelSeconds { get; private set; }

        /// <summary>Speed of the liquid at the impact point, in metres per second.</summary>
        public float ImpactSpeed { get; private set; }

        /// <summary>Radius of the jet where it lands, in metres.</summary>
        public float ImpactRadius { get; private set; }

        void Awake()
        {
            EnsureBuffers();
        }

        void OnEnable()
        {
            EnsureBuffers();
            // The ribbon is authored directly in world space, so the transform must stay neutral.
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            transform.localScale = Vector3.one;
            if (meshRenderer != null)
                meshRenderer.enabled = state != StreamState.Idle;
        }

        void OnDisable()
        {
            state = StreamState.Idle;
            if (meshRenderer != null)
                meshRenderer.enabled = false;
        }

        void OnDestroy()
        {
            if (mesh != null)
            {
                if (Application.isPlaying)
                    Destroy(mesh);
                else
                    DestroyImmediate(mesh);
            }
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Drives the stream for this frame. Call every frame while pouring; call
        /// <see cref="Stop"/> when the flow closes.
        /// </summary>
        public void SetFlow(Vector3 lipWorldPosition, Vector3 exitVelocity, float millilitresPerSecond, Color color)
        {
            origin = lipWorldPosition;
            velocity = exitVelocity;
            targetFlowMLPerSecond = Mathf.Max(0f, millilitresPerSecond);
            liquidColor = color;

            if (targetFlowMLPerSecond <= 0.01f)
            {
                Stop();
                return;
            }

            if (state != StreamState.Flowing)
            {
                if (state == StreamState.Idle)
                {
                    headDistance = 0f;
                    tailDistance = 0f;
                    flowMLPerSecond = 0f;
                }
                state = StreamState.Flowing;
                dormantTimer = 0f;
            }
        }

        /// <summary>Closes the flow. The ribbon breaks and retracts before it disappears.</summary>
        public void Stop()
        {
            if (state == StreamState.Flowing)
                state = StreamState.Breaking;

            targetFlowMLPerSecond = 0f;
        }

        /// <summary>Hard reset with no break-up animation.</summary>
        public void Clear()
        {
            state = StreamState.Idle;
            flowMLPerSecond = 0f;
            targetFlowMLPerSecond = 0f;
            headDistance = 0f;
            tailDistance = 0f;
            pathLength = 0f;
            if (meshRenderer != null)
                meshRenderer.enabled = false;
        }

        /// <summary>
        /// Tells the ribbon exactly which horizontal plane it lands on. Solving against a plane is
        /// exact and free, and it keeps the stream glued to a liquid surface that is rising.
        /// </summary>
        public void SetTargetPlane(float worldY)
        {
            hasTargetPlane = true;
            targetPlaneY = worldY;
        }

        public void ClearTargetPlane()
        {
            hasTargetPlane = false;
        }

        // ------------------------------------------------------------------ frame

        void LateUpdate()
        {
            if (state == StreamState.Idle)
            {
                HandleDormancy();
                return;
            }

            float deltaTime = Application.isPlaying ? Time.deltaTime : 1f / 60f;

            SolvePath();
            AdvanceEnds(deltaTime);
            UpdateFlowRamp(deltaTime);

            if (tailDistance >= pathLength - 0.0005f && state == StreamState.Breaking)
            {
                Clear();
                return;
            }

            BuildRibbon();
        }

        void HandleDormancy()
        {
            if (mesh == null)
                return;

            dormantTimer += Application.isPlaying ? Time.deltaTime : 0f;
            if (dormantTimer > LiquidFXRuntime.DormantTimeout && mesh.vertexCount > 0)
            {
                // Release the vertex buffer so an idle faucet costs nothing but the component.
                mesh.Clear();
                builtVertexCount = 0;
                builtIndexCount = 0;
                dormantTimer = 0f;
            }
        }

        void UpdateFlowRamp(float deltaTime)
        {
            float rate = Mathf.Max(0.0001f, openSeconds);
            flowMLPerSecond = Mathf.MoveTowards(
                flowMLPerSecond,
                targetFlowMLPerSecond,
                Mathf.Max(referenceFlowMLPerSecond, targetFlowMLPerSecond) / rate * deltaTime);
        }

        void AdvanceEnds(float deltaTime)
        {
            if (pathLength <= 0f)
                return;

            float headSpeed = SpeedAtDistance(headDistance);
            headDistance = Mathf.Min(pathLength, headDistance + headSpeed * deltaTime);

            if (state == StreamState.Breaking)
            {
                float tailSpeed = SpeedAtDistance(tailDistance) * tailSnapMultiplier;
                tailDistance = Mathf.Min(pathLength, tailDistance + tailSpeed * deltaTime);
            }
            else
            {
                tailDistance = 0f;
            }
        }

        // ------------------------------------------------------------------ path

        void SolvePath()
        {
            int segments = Mathf.Clamp(LiquidFXRuntime.StreamSegments, 4, MaxSegments);
            float totalTime = ResolveImpactTime();
            TravelSeconds = totalTime;

            pathSamples = segments + 1;
            pathLength = 0f;
            Vector3 previous = origin;

            for (int i = 0; i < pathSamples; i++)
            {
                float t = totalTime * i / segments;
                Vector3 point = LiquidFXRuntime.BallisticPoint(origin, velocity, t);
                Vector3 tangent = LiquidFXRuntime.BallisticVelocity(velocity, t);

                if (i > 0)
                    pathLength += Vector3.Distance(previous, point);

                pathPoint[i] = point;
                pathTime[i] = t;
                pathSpeed[i] = Mathf.Max(MinimumSpeed, tangent.magnitude);
                pathDistance[i] = pathLength;
                previous = point;
            }

            ImpactPointWorld = pathPoint[pathSamples - 1];
            ImpactSpeed = pathSpeed[pathSamples - 1];
            ImpactRadius = RadiusAtSpeed(ImpactSpeed, targetFlowMLPerSecond);
        }

        /// <summary>
        /// Time until the stream hits something. A supplied target plane is solved analytically;
        /// otherwise a coarse raycast sweep runs on a timer instead of every frame.
        /// </summary>
        float ResolveImpactTime()
        {
            if (hasTargetPlane)
            {
                float drop = origin.y - targetPlaneY;
                if (drop <= 0f)
                    return 0.02f;

                return Mathf.Min(
                    LiquidFXRuntime.FallTime(drop, velocity.y),
                    LiquidFXRuntime.FallTime(maximumFallDistance, velocity.y));
            }

            float fallbackTime = LiquidFXRuntime.FallTime(maximumFallDistance, velocity.y);
            if (!Application.isPlaying)
                return fallbackTime;

            impactRefreshTimer -= Time.deltaTime;
            if (impactRefreshTimer > 0f && cachedImpactTime > 0f)
                return cachedImpactTime;

            impactRefreshTimer = impactRefreshInterval;
            cachedImpactTime = RaycastImpactTime(fallbackTime);
            return cachedImpactTime;
        }

        float cachedImpactTime;

        float RaycastImpactTime(float fallbackTime)
        {
            const int coarseSteps = 6;
            Vector3 previous = origin;
            for (int i = 1; i <= coarseSteps; i++)
            {
                float t = fallbackTime * i / coarseSteps;
                Vector3 point = LiquidFXRuntime.BallisticPoint(origin, velocity, t);
                Vector3 delta = point - previous;
                float distance = delta.magnitude;
                if (distance > 0.0001f
                    && Physics.Raycast(previous, delta / distance, out RaycastHit hit, distance, impactMask, QueryTriggerInteraction.Ignore))
                {
                    float previousTime = fallbackTime * (i - 1) / coarseSteps;
                    return Mathf.Lerp(previousTime, t, hit.distance / distance);
                }

                previous = point;
            }

            return fallbackTime;
        }

        float SpeedAtDistance(float distance)
        {
            if (pathSamples < 2)
                return MinimumSpeed;

            for (int i = 1; i < pathSamples; i++)
            {
                if (pathDistance[i] >= distance)
                {
                    float span = Mathf.Max(0.0001f, pathDistance[i] - pathDistance[i - 1]);
                    float k = Mathf.Clamp01((distance - pathDistance[i - 1]) / span);
                    return Mathf.Lerp(pathSpeed[i - 1], pathSpeed[i], k);
                }
            }

            return pathSpeed[pathSamples - 1];
        }

        void SampleAtDistance(float distance, out Vector3 position, out Vector3 tangent, out float speed)
        {
            distance = Mathf.Clamp(distance, 0f, pathLength);

            for (int i = 1; i < pathSamples; i++)
            {
                if (pathDistance[i] >= distance)
                {
                    float span = Mathf.Max(0.0001f, pathDistance[i] - pathDistance[i - 1]);
                    float k = Mathf.Clamp01((distance - pathDistance[i - 1]) / span);
                    position = Vector3.Lerp(pathPoint[i - 1], pathPoint[i], k);
                    tangent = (pathPoint[i] - pathPoint[i - 1]).normalized;
                    speed = Mathf.Lerp(pathSpeed[i - 1], pathSpeed[i], k);
                    return;
                }
            }

            position = pathPoint[pathSamples - 1];
            tangent = pathSamples > 1
                ? (pathPoint[pathSamples - 1] - pathPoint[pathSamples - 2]).normalized
                : Vector3.down;
            speed = pathSpeed[pathSamples - 1];
        }

        float RadiusAtSpeed(float speed, float flow)
        {
            if (flow <= 0.01f)
                return 0f;

            // Reference radius scaled by the ratio of the actual flow to the reference flow,
            // then thinned by mass continuity as the jet speeds up.
            float lipSpeed = Mathf.Max(MinimumSpeed, velocity.magnitude);
            float flowRatio = Mathf.Sqrt(flow / referenceFlowMLPerSecond);
            float continuity = Mathf.Sqrt(lipSpeed / Mathf.Max(MinimumSpeed, speed));
            return Mathf.Max(minimumRadius, lipRadius * flowRatio * continuity) * widthMultiplier;
        }

        // ------------------------------------------------------------------ mesh

        void BuildRibbon()
        {
            EnsureBuffers();

            int segments = Mathf.Clamp(LiquidFXRuntime.StreamSegments, 4, MaxSegments);
            float visibleStart = Mathf.Clamp(tailDistance, 0f, pathLength);
            float visibleEnd = Mathf.Clamp(headDistance, visibleStart, pathLength);
            float visibleSpan = visibleEnd - visibleStart;

            if (visibleSpan <= 0.0005f || flowMLPerSecond <= 0.01f)
            {
                if (meshRenderer != null)
                    meshRenderer.enabled = false;
                return;
            }

            Camera camera = ResolveCamera();
            Vector3 viewPosition = camera != null ? camera.transform.position : origin + Vector3.back * 5f;
            bool orthographic = camera != null && camera.orthographic;
            Vector3 viewForward = camera != null ? camera.transform.forward : Vector3.forward;

            float time = Application.isPlaying ? Time.time : 0f;
            float scroll = time * uvScrollSpeed;

            Vector3 minBounds = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 maxBounds = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i <= segments; i++)
            {
                float k = i / (float)segments;
                float distance = visibleStart + visibleSpan * k;
                SampleAtDistance(distance, out Vector3 position, out Vector3 tangent, out float speed);

                Vector3 toView = orthographic ? -viewForward : (viewPosition - position);
                Vector3 side = Vector3.Cross(tangent, toView);
                if (side.sqrMagnitude < 1e-8f)
                    side = Vector3.Cross(tangent, Vector3.forward);
                side = side.normalized;

                float radius = RadiusAtSpeed(speed, flowMLPerSecond);
                radius *= NeckModulation(distance, time);
                radius *= EndTaper(distance, visibleStart, visibleEnd);

                Vector3 left = position - side * radius;
                Vector3 right = position + side * radius;

                int baseIndex = i * 2;
                vertices[baseIndex] = left;
                vertices[baseIndex + 1] = right;

                float v = distance * uvTilingPerMetre - scroll;
                uvs[baseIndex] = new Vector2(0f, v);
                uvs[baseIndex + 1] = new Vector2(1f, v);

                float alpha = AlphaAtDistance(distance, visibleStart, visibleEnd, speed);
                Color vertexColor = liquidColor;
                vertexColor.a = alpha;
                colors[baseIndex] = vertexColor;
                colors[baseIndex + 1] = vertexColor;

                minBounds = Vector3.Min(minBounds, Vector3.Min(left, right));
                maxBounds = Vector3.Max(maxBounds, Vector3.Max(left, right));
            }

            int vertexCount = (segments + 1) * 2;
            int indexCount = segments * 6;

            // Only touch the index buffer when the segment count actually changes (quality tier
            // switch). Rewriting it every frame is pure waste on a mesh this small but constant.
            if (builtVertexCount != vertexCount)
            {
                mesh.Clear(true);
                builtVertexCount = vertexCount;
                builtIndexCount = 0;
            }

            mesh.SetVertices(vertices, 0, vertexCount);
            mesh.SetUVs(0, uvs, 0, vertexCount);
            mesh.SetColors(colors, 0, vertexCount);

            if (builtIndexCount != indexCount)
            {
                mesh.SetIndices(indices, 0, indexCount, MeshTopology.Triangles, 0, false);
                builtIndexCount = indexCount;
            }

            Vector3 centre = (minBounds + maxBounds) * 0.5f;
            Vector3 size = maxBounds - minBounds;
            size.x = Mathf.Max(size.x, 0.01f);
            size.y = Mathf.Max(size.y, 0.01f);
            size.z = Mathf.Max(size.z, 0.01f);
            mesh.bounds = new Bounds(centre, size);

            if (meshRenderer != null)
            {
                if (!meshRenderer.enabled)
                    meshRenderer.enabled = true;

                properties ??= new MaterialPropertyBlock();
                meshRenderer.GetPropertyBlock(properties);
                properties.SetColor(ColorId, liquidColor);
                meshRenderer.SetPropertyBlock(properties);
            }
        }

        /// <summary>Sinusoidal necking that travels down with the liquid, so the jet looks unstable.</summary>
        float NeckModulation(float distance, float time)
        {
            if (breakupStrength <= 0f || distance <= breakupStartDistance)
                return 1f;

            float ramp = Mathf.Clamp01((distance - breakupStartDistance) / 0.35f);
            float wave = Mathf.Sin(distance * breakupFrequency - time * breakupSpeed);
            float wave2 = Mathf.Sin(distance * breakupFrequency * 1.87f - time * breakupSpeed * 0.63f);
            return 1f + (wave * 0.6f + wave2 * 0.4f) * breakupStrength * ramp;
        }

        /// <summary>Rounds the leading edge and the retracting tail so neither ends in a hard cut.</summary>
        float EndTaper(float distance, float visibleStart, float visibleEnd)
        {
            const float taper = 0.035f;
            float fromHead = Mathf.Clamp01((visibleEnd - distance) / taper);
            float fromTail = Mathf.Clamp01((distance - visibleStart) / taper);

            // A jet leaving the lip is full width at the lip itself, not tapered.
            if (visibleStart <= 0.0005f)
                fromTail = 1f;

            // Once the head has landed there is no leading edge to round any more.
            if (visibleEnd >= pathLength - 0.0005f)
                fromHead = 1f;

            return Mathf.Sqrt(Mathf.Min(fromHead, fromTail));
        }

        float AlphaAtDistance(float distance, float visibleStart, float visibleEnd, float speed)
        {
            // Fast thin liquid reads as more transparent, which is what stretched water does.
            float thinning = Mathf.Clamp01(Mathf.Sqrt(Mathf.Max(MinimumSpeed, velocity.magnitude) / speed));
            float alpha = Mathf.Lerp(0.55f, 1f, thinning);

            const float fade = 0.05f;
            if (visibleEnd < pathLength - 0.0005f)
                alpha *= Mathf.Clamp01((visibleEnd - distance) / fade);
            if (visibleStart > 0.0005f)
                alpha *= Mathf.Clamp01((distance - visibleStart) / fade);

            return alpha * liquidColor.a;
        }

        Camera ResolveCamera()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView != null && sceneView.camera != null)
                    return sceneView.camera;
            }
#endif
            return Camera.main;
        }

        void EnsureBuffers()
        {
            if (meshFilter == null)
                meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null)
                meshRenderer = GetComponent<MeshRenderer>();

            if (vertices == null)
            {
                int vertexCount = (MaxSegments + 1) * 2;
                vertices = new Vector3[vertexCount];
                uvs = new Vector2[vertexCount];
                colors = new Color[vertexCount];
                indices = new int[MaxSegments * 6];
                for (int i = 0; i < MaxSegments; i++)
                {
                    int v = i * 2;
                    int t = i * 6;
                    indices[t] = v;
                    indices[t + 1] = v + 2;
                    indices[t + 2] = v + 1;
                    indices[t + 3] = v + 1;
                    indices[t + 4] = v + 2;
                    indices[t + 5] = v + 3;
                }
            }

            if (mesh == null)
            {
                mesh = new Mesh { name = "Liquid Stream Ribbon" };
                mesh.MarkDynamic();
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            }

            if (meshFilter != null && meshFilter.sharedMesh != mesh)
                meshFilter.sharedMesh = mesh;

            if (meshRenderer != null)
            {
                meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
                meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }
        }
    }
}
