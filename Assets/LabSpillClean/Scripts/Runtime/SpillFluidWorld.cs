using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LabLiquidVR;
using LabSpill.Rendering;
using PBDFluid;
using UnityEngine;
using UnityEngine.Rendering;

namespace LabSpill
{
    [DisallowMultipleComponent]
    public sealed class SpillFluidWorld : MonoBehaviour
    {
        [Serializable]
        sealed class Liquid
        {
            public string key;
            public string name;
            public Color particleColor;
            public Material material;
            public LiquidConfig config;
        }

        readonly struct ReceiverLiquidKey : IEquatable<ReceiverLiquidKey>
        {
            public readonly SpillLiquidContainer receiver;
            public readonly int liquidIndex;
            public ReceiverLiquidKey(SpillLiquidContainer receiver, int liquidIndex)
            {
                this.receiver = receiver;
                this.liquidIndex = liquidIndex;
            }
            public bool Equals(ReceiverLiquidKey other) =>
                receiver == other.receiver && liquidIndex == other.liquidIndex;
            public override bool Equals(object obj) => obj is ReceiverLiquidKey other && Equals(other);
            public override int GetHashCode() =>
                ((receiver != null ? receiver.GetHashCode() : 0) * 397) ^ liquidIndex;
        }

        sealed class SurfaceView
        {
            public readonly SpillRenderBridge.Entry entry = new SpillRenderBridge.Entry();
            public MeshRenderer renderer;
        }

        struct RecentSpawn
        {
            public Vector3 position;
            public float expiresAt;
        }

        [Header("Configuracao unica")]
        public SpillVisualSettings settings;
        [Tooltip("Material base PBDFluid/SSFSurface. O JSON do frasco aplica as cores.")]
        public Material surfaceMaterialTemplate;

        readonly List<Liquid> m_liquids = new List<Liquid>();
        readonly List<SurfaceView> m_views = new List<SurfaceView>();
        readonly List<Vector3> m_queuePositions = new List<Vector3>();
        readonly List<Vector3> m_queueVelocities = new List<Vector3>();
        readonly List<Color> m_queueColors = new List<Color>();
        readonly List<uint> m_queueSubstances = new List<uint>();
        readonly List<RecentSpawn> m_recentSpawns = new List<RecentSpawn>();
        readonly List<float> m_deathAt = new List<float>();
        readonly List<Vector3> m_previousLifecyclePositions = new List<Vector3>();
        readonly Dictionary<ReceiverLiquidKey, int> m_receiverAdds =
            new Dictionary<ReceiverLiquidKey, int>();
        readonly List<uint> m_particleSubstances = new List<uint>();
        readonly List<int> m_liveParticlesPerLiquid = new List<int>();
        readonly List<FluidSolver.ColliderGPU> m_surfaceColliders =
            new List<FluidSolver.ColliderGPU>();

        FluidBody m_fluid;
        FluidBoundary m_boundary;
        FluidSolver m_solver;
        Bounds m_domain;
        SpillSurface[] m_surfaces;
        SpillLiquidContainer[] m_receivers;
        float m_flushTimer;
        float m_lifecycleTimer;
        float m_compactTimer;
        int m_liveParticleCount;
        Vector4[] m_positions;
        float[] m_states;
        float[] m_stateScratch;
        readonly Vector4[] m_deadPosition = new Vector4[1];
        readonly float[] m_deadState = { 1f };

        static readonly int SurfaceDepth = Shader.PropertyToID("_PBDFluidSurfaceDepth");
        static readonly int SurfaceNormal = Shader.PropertyToID("_PBDFluidSurfaceNormal");
        static readonly int DensityThreshold = Shader.PropertyToID("_DensityThreshold");
        static readonly int EdgeSoftness = Shader.PropertyToID("_EdgeSoftness");

        public SpillVisualSettings Settings => settings;
        public int ParticleCount => m_fluid != null ? m_fluid.NumParticles : 0;
        public int LiquidCount => m_liquids.Count;
        public ComputeBuffer PositionsBuffer => m_fluid != null ? m_fluid.Positions : null;
        public ComputeBuffer StatesBuffer => m_fluid != null ? m_fluid.States : null;
        public Bounds SimulationBounds => m_domain;

        void Start()
        {
            if (settings == null)
            {
                Debug.LogError("[LabSpill] SpillVisualSettings nao atribuido.", this);
                enabled = false;
                return;
            }
            Build();
        }

        void Build()
        {
            RefreshSceneCache();
            m_domain = CalculateDomain();
            float radius = settings.PhysicalRadius;
            float diameter = radius * 2f;
            Vector3 graveyard = m_domain.min + Vector3.down * 50f;

            var boundarySource = new ParticlesFromList(diameter,
                new[] { m_domain.min - Vector3.one * diameter });
            m_boundary = new FluidBoundary(boundarySource, radius, 1000f,
                Matrix4x4.identity, false);
            m_boundary.Bounds = m_domain;

            var source = new ParticlesFromList(diameter, new[] { graveyard });
            m_fluid = new FluidBody(source, radius, 1000f, Matrix4x4.identity);
            m_fluid.Bounds = m_domain;
            m_fluid.ParticleMass *= settings.massScale;
            m_fluid.Colors.SetData(new[] { Vector4.zero });
            m_fluid.SubstanceIds.SetData(new[] { uint.MaxValue });
            m_fluid.States.SetData(new[] { 1f });
            m_liveParticleCount = 0;
            for (int i = 0; i < m_liveParticlesPerLiquid.Count; i++)
                m_liveParticlesPerLiquid[i] = 0;
            m_deathAt.Clear();
            m_deathAt.Add(-2f);
            m_particleSubstances.Clear();
            m_particleSubstances.Add(uint.MaxValue);
            m_previousLifecyclePositions.Clear();
            m_previousLifecyclePositions.Add(graveyard);

            CreateSolver(graveyard);
            UploadSurfaceColliders();
        }

        void CreateSolver(Vector3 graveyard)
        {
            m_solver?.Dispose();
            m_solver = new FluidSolver(m_fluid, m_boundary)
            {
                Graveyard = graveyard,
                SolverIterations = settings.solverIterations,
                ConstraintIterations = settings.constraintIterations,
                RestDamping = settings.restDamping
            };
            m_fluid.Viscosity = settings.viscosity;
        }

        Bounds CalculateDomain()
        {
            bool hasBounds = false;
            Bounds result = new Bounds(transform.position, Vector3.one);
            for (int i = 0; i < m_surfaces.Length; i++)
            {
                Collider col = m_surfaces[i] != null ? m_surfaces[i].Collider : null;
                if (col == null) continue;
                if (!hasBounds) { result = col.bounds; hasBounds = true; }
                else result.Encapsulate(col.bounds);
            }
            for (int i = 0; i < m_receivers.Length; i++)
            {
                Renderer r = m_receivers[i] != null ? m_receivers[i].GetComponent<Renderer>() : null;
                if (r == null) continue;
                if (!hasBounds) { result = r.bounds; hasBounds = true; }
                else result.Encapsulate(r.bounds);
            }
            result.Expand(new Vector3(0.5f, 0.5f, 0.5f));
            return result;
        }

        void RefreshSceneCache()
        {
            m_surfaces = FindObjectsByType<SpillSurface>(FindObjectsInactive.Exclude);
            m_receivers = FindObjectsByType<SpillLiquidContainer>(FindObjectsInactive.Exclude);
        }

        void UploadSurfaceColliders()
        {
            m_surfaceColliders.Clear();
            for (int i = 0; i < m_surfaces.Length; i++)
            {
                SpillSurface surface = m_surfaces[i];
                Collider col = surface != null ? surface.Collider : null;
                if (col == null || !col.enabled) continue;
                m_surfaceColliders.Add(ColliderToGpu(col));
            }
            m_solver.SetColliders(m_surfaceColliders.ToArray());
        }

        void Update()
        {
            if (m_fluid == null) return;
            m_flushTimer += Time.deltaTime;
            if (m_queuePositions.Count > 0 &&
                (settings.emissionFlushInterval <= 0f ||
                 m_flushTimer >= settings.emissionFlushInterval))
                FlushQueuedEmissions();

            UpdateLifecycle();
            m_compactTimer += Time.deltaTime;
            if (m_compactTimer >= 1f)
            {
                m_compactTimer = 0f;
                CompactDead();
            }
        }

        void FixedUpdate()
        {
            if (m_solver == null) return;
            int steps = Mathf.Clamp(settings.maxPhysicsStepsPerFrame, 1, 4);
            float dt = Time.fixedDeltaTime / steps;
            for (int i = 0; i < steps; i++) m_solver.StepPhysics(dt);
        }

        void LateUpdate() => PublishSurfaces();

        public int RegisterLiquid(string key, LiquidConfig config, Material template)
        {
            for (int i = 0; i < m_liquids.Count; i++)
                if (m_liquids[i].key == key) return i;
            if (config == null) return -1;

            Material source = template != null ? template : surfaceMaterialTemplate;
            Shader fallback = source == null ? Shader.Find("PBDFluid/SSFSurface") : null;
            if (source == null && fallback == null) return -1;
            Material material = source != null ? new Material(source) : new Material(fallback);
            material.name = "Spill - " + config.liquidName;
            material.hideFlags = HideFlags.HideAndDontSave;
            ApplyConfig(material, config);
            if (material.HasProperty(DensityThreshold))
                material.SetFloat(DensityThreshold, settings.densityThreshold);
            if (material.HasProperty(EdgeSoftness))
                material.SetFloat(EdgeSoftness, settings.edgeSoftness);

            Color particle = config.bodyColor;
            particle.a = 1f;
            m_liquids.Add(new Liquid
            {
                key = key,
                name = config.liquidName,
                particleColor = particle,
                material = material,
                config = LiquidConfig.Copy(config)
            });
            m_liveParticlesPerLiquid.Add(0);
            return m_liquids.Count - 1;
        }

        public int QueueJet(Vector3 origin, Vector3 velocity, int count, int liquidIndex,
            float neckRadius, Vector3 mouthNormal)
        {
            if (m_fluid == null || liquidIndex < 0 || liquidIndex >= m_liquids.Count || count <= 0)
                return 0;
            int capacity = Mathf.Min(settings.maxParticles, BitonicSort.MAX_ELEMENTS);
            int available = capacity - m_fluid.NumParticles - m_boundary.NumParticles -
                m_queuePositions.Count;
            int requested = Mathf.Clamp(count, 0, Mathf.Max(0, available));
            if (requested == 0) return 0;

            Vector3 direction = velocity.sqrMagnitude > 1e-6f ? velocity.normalized : Vector3.down;
            Vector3 normal = mouthNormal.sqrMagnitude > 1e-6f ? mouthNormal.normalized : -direction;
            Vector3 reference = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.92f
                ? Vector3.right : Vector3.up;
            Vector3 tangent = Vector3.Cross(normal, reference).normalized;
            Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;
            float physicalRadius = settings.PhysicalRadius;
            float spacing = physicalRadius * 2.1f;
            float usableRadius = Mathf.Max(0f, neckRadius - physicalRadius);
            float now = Time.time;
            for (int i = m_recentSpawns.Count - 1; i >= 0; i--)
                if (m_recentSpawns[i].expiresAt <= now) m_recentSpawns.RemoveAt(i);

            int accepted = 0;
            for (int i = 0; i < requested; i++)
            {
                Vector3 candidate = origin;
                bool found = false;
                for (int layer = 0; layer < 12 && !found; layer++)
                {
                    for (int attempt = 0; attempt < 24; attempt++)
                    {
                        float radial = usableRadius * Mathf.Sqrt(UnityEngine.Random.value);
                        float angle = UnityEngine.Random.value * Mathf.PI * 2f;
                        Vector3 disk = tangent * (Mathf.Cos(angle) * radial) +
                            bitangent * (Mathf.Sin(angle) * radial);
                        candidate = origin + disk + direction * (layer * spacing);
                        if (IsSeparated(candidate, spacing)) { found = true; break; }
                    }
                }
                // Se o disco estiver lotado, prolonga o jato em camadas. Nunca
                // aceita a ultima tentativa aleatoria se ela estiver sobreposta.
                for (int layer = 12; layer < 64 && !found; layer++)
                {
                    candidate = origin + direction * (layer * spacing);
                    found = IsSeparated(candidate, spacing);
                }
                if (!found) break;
                m_queuePositions.Add(candidate);
                m_queueVelocities.Add(velocity);
                m_queueColors.Add(m_liquids[liquidIndex].particleColor);
                m_queueSubstances.Add((uint)liquidIndex);
                m_recentSpawns.Add(new RecentSpawn
                {
                    position = candidate,
                    expiresAt = now + 0.15f
                });
                accepted++;
            }
            return accepted;
        }

        bool IsSeparated(Vector3 candidate, float spacing)
        {
            float minSq = spacing * spacing;
            for (int i = 0; i < m_queuePositions.Count; i++)
                if ((m_queuePositions[i] - candidate).sqrMagnitude < minSq) return false;
            for (int i = 0; i < m_recentSpawns.Count; i++)
                if ((m_recentSpawns[i].position - candidate).sqrMagnitude < minSq) return false;
            return true;
        }

        public bool FlushQueuedEmissions()
        {
            if (m_queuePositions.Count == 0 || m_fluid == null) return false;
            int added = m_queuePositions.Count;
            m_fluid.Append(m_queuePositions.ToArray(), m_queueVelocities.ToArray(),
                m_queueColors.ToArray(), m_queueSubstances.ToArray());
            for (int i = 0; i < added; i++)
            {
                m_previousLifecyclePositions.Add(m_queuePositions[i]);
                uint substance = m_queueSubstances[i];
                m_particleSubstances.Add(substance);
                if (substance < m_liveParticlesPerLiquid.Count)
                    m_liveParticlesPerLiquid[(int)substance]++;
            }
            m_queuePositions.Clear();
            m_queueVelocities.Clear();
            m_queueColors.Clear();
            m_queueSubstances.Clear();
            for (int i = 0; i < added; i++) m_deathAt.Add(-1f);
            m_liveParticleCount += added;
            m_flushTimer = 0f;
            Vector3 graveyard = m_solver.Graveyard;
            CreateSolver(graveyard);
            UploadSurfaceColliders();
            return true;
        }

        void UpdateLifecycle()
        {
            m_lifecycleTimer += Time.deltaTime;
            if (m_lifecycleTimer < 0.1f || m_fluid == null) return;
            m_lifecycleTimer = 0f;
            RefreshSceneCache();

            int count = m_fluid.NumParticles;
            EnsureArray(ref m_positions, count);
            EnsureArray(ref m_states, count);
            while (m_deathAt.Count < count) m_deathAt.Add(-1f);
            m_fluid.Positions.GetData(m_positions);
            m_fluid.States.GetData(m_states);
            m_liveParticleCount = 0;
            for (int i = 0; i < m_liveParticlesPerLiquid.Count; i++)
                m_liveParticlesPerLiquid[i] = 0;
            for (int i = 0; i < count; i++)
                if (m_states[i] <= 0.5f)
                {
                    m_liveParticleCount++;
                    if (i < m_particleSubstances.Count)
                    {
                        uint substance = m_particleSubstances[i];
                        if (substance < m_liveParticlesPerLiquid.Count)
                            m_liveParticlesPerLiquid[(int)substance]++;
                    }
                }
            while (m_previousLifecyclePositions.Count < count)
                m_previousLifecyclePositions.Add(m_positions[m_previousLifecyclePositions.Count]);
            m_receiverAdds.Clear();
            float now = Time.time;

            for (int i = 0; i < count; i++)
            {
                if (m_states[i] > 0.5f) continue;
                Vector3 position = m_positions[i];
                Vector3 previous = m_previousLifecyclePositions[i];
                SpillLiquidContainer receiver;
                if (TryFindReceiver(previous, position, out receiver))
                {
                    int liquidIndex = i < m_particleSubstances.Count
                        ? (int)m_particleSubstances[i]
                        : -1;
                    ReceiverLiquidKey key = new ReceiverLiquidKey(receiver, liquidIndex);
                    int received;
                    m_receiverAdds.TryGetValue(key, out received);
                    m_receiverAdds[key] = received + 1;
                    MarkDead(i);
                    continue;
                }
                m_previousLifecyclePositions[i] = position;
                if (m_deathAt[i] >= 0f)
                {
                    if (now >= m_deathAt[i]) MarkDead(i);
                    continue;
                }
                if (IsOnBench(position))
                    m_deathAt[i] = now + UnityEngine.Random.Range(
                        settings.benchLifetimeMin, settings.benchLifetimeMax);
            }

            foreach (var pair in m_receiverAdds)
                if (pair.Key.receiver != null && pair.Key.liquidIndex >= 0 &&
                    pair.Key.liquidIndex < m_liquids.Count)
                    pair.Key.receiver.ReceiveLiquid(
                        m_liquids[pair.Key.liquidIndex].config,
                        pair.Value * settings.millilitersPerParticle);
        }

        bool TryFindReceiver(Vector3 previous, Vector3 position,
            out SpillLiquidContainer receiver)
        {
            receiver = null;
            for (int i = 0; i < m_receivers.Length; i++)
            {
                SpillLiquidContainer candidate = m_receivers[i];
                if (candidate == null || !candidate.isActiveAndEnabled ||
                    candidate.currentVolumeML >= candidate.capacityML -
                    settings.millilitersPerParticle * 0.5f) continue;
                Vector3 center, normal;
                float openingRadius;
                if (!candidate.TryGetOpening(out center, out normal, out openingRadius) ||
                    Vector3.Dot(normal, Vector3.up) < 0.55f) continue;
                Vector3 delta = position - center;
                float axial = Vector3.Dot(delta, normal);
                Vector3 radial = delta - normal * axial;
                float captureDepth = Mathf.Max(0.08f, openingRadius * 2.5f);
                // O SSF desenha a gota maior que o raio PBD. A captura acompanha
                // essa silhueta: se a gota visual entrou no gargalo, o volume entra.
                float physicalRadius = settings.PhysicalRadius;
                float visualRadius = physicalRadius * settings.visualRadiusScale;
                float usable = Mathf.Max(0f,
                    openingRadius + Mathf.Max(0f, visualRadius - physicalRadius));
                if (radial.sqrMagnitude <= usable * usable &&
                    axial <= physicalRadius * 2f && axial >= -captureDepth)
                {
                    receiver = candidate;
                    return true;
                }

                // A leitura de vida ocorre a cada 0,1 s. Um jato rapido pode
                // atravessar todo o gargalo entre duas leituras; cruza-se o
                // segmento anterior/atual com o plano circular da abertura.
                float previousAxial = Vector3.Dot(previous - center, normal);
                float denominator = previousAxial - axial;
                if (previousAxial >= 0f && axial <= 0f &&
                    Mathf.Abs(denominator) > 1e-6f)
                {
                    float t = Mathf.Clamp01(previousAxial / denominator);
                    Vector3 crossing = Vector3.Lerp(previous, position, t) - center;
                    Vector3 crossingRadial = crossing - normal * Vector3.Dot(crossing, normal);
                    if (crossingRadial.sqrMagnitude <= usable * usable)
                    {
                        receiver = candidate;
                        return true;
                    }
                }
            }
            return false;
        }

        bool IsOnBench(Vector3 position)
        {
            for (int i = 0; i < m_surfaces.Length; i++)
            {
                SpillSurface surface = m_surfaces[i];
                if (surface == null || surface.kind != SpillSurfaceKind.Bench) continue;
                Bounds b = surface.Collider.bounds;
                float r = settings.PhysicalRadius;
                if (position.x < b.min.x - r || position.x > b.max.x + r ||
                    position.z < b.min.z - r || position.z > b.max.z + r) continue;
                if (position.y >= b.max.y - r * 3f &&
                    position.y <= b.max.y + Mathf.Max(0.08f, r * 12f)) return true;
            }
            return false;
        }

        void MarkDead(int index)
        {
            Vector3 g = m_solver.Graveyard;
            m_deadPosition[0] = new Vector4(g.x, g.y, g.z, 0f);
            m_fluid.Positions.SetData(m_deadPosition, 0, index, 1);
            m_fluid.States.SetData(m_deadState, 0, index, 1);
            if (index < m_states.Length) m_states[index] = 1f;
            m_liveParticleCount = Mathf.Max(0, m_liveParticleCount - 1);
            if (index < m_particleSubstances.Count)
            {
                uint substance = m_particleSubstances[index];
                if (substance < m_liveParticlesPerLiquid.Count)
                    m_liveParticlesPerLiquid[(int)substance] = Mathf.Max(0,
                        m_liveParticlesPerLiquid[(int)substance] - 1);
            }
            m_deathAt[index] = -2f;
            if (index < m_previousLifecyclePositions.Count)
                m_previousLifecyclePositions[index] = g;
        }

        void CompactDead()
        {
            int count = m_fluid.NumParticles;
            EnsureArray(ref m_stateScratch, count);
            m_fluid.States.GetData(m_stateScratch);
            int dead = 0;
            for (int i = 0; i < count; i++) if (m_stateScratch[i] > 0.5f) dead++;
            int alive = count - dead;
            m_liveParticleCount = alive;
            if (dead == 0 || (alive > 0 && dead < 32) || (alive == 0 && count == 1)) return;
            var survivingTimes = new List<float>(alive);
            var survivingPositions = new List<Vector3>(alive);
            var survivingSubstances = new List<uint>(alive);
            for (int i = 0; i < count; i++)
                if (m_stateScratch[i] <= 0.5f)
                {
                    survivingTimes.Add(i < m_deathAt.Count ? m_deathAt[i] : -1f);
                    survivingPositions.Add(i < m_previousLifecyclePositions.Count
                        ? m_previousLifecyclePositions[i] : Vector3.zero);
                    survivingSubstances.Add(i < m_particleSubstances.Count
                        ? m_particleSubstances[i] : uint.MaxValue);
                }
            Vector3 graveyard = m_solver.Graveyard;
            if (!m_fluid.CompactDead()) return;
            m_deathAt.Clear();
            m_previousLifecyclePositions.Clear();
            m_particleSubstances.Clear();
            if (alive == 0)
            {
                // ComputeBuffers nao aceitam tamanho zero. Mantemos uma unica
                // sentinela morta, fora da cena, sem consumir capacidade util.
                m_deathAt.Add(-2f);
                m_previousLifecyclePositions.Add(graveyard);
                m_particleSubstances.Add(uint.MaxValue);
            }
            else
            {
                m_deathAt.AddRange(survivingTimes);
                m_previousLifecyclePositions.AddRange(survivingPositions);
                m_particleSubstances.AddRange(survivingSubstances);
            }
            CreateSolver(graveyard);
            UploadSurfaceColliders();
        }

        void PublishSurfaces()
        {
            while (m_views.Count < m_liquids.Count)
                m_views.Add(new SurfaceView());
            for (int i = 0; i < m_views.Count; i++)
            {
                SurfaceView view = m_views[i];
                Liquid liquid = m_liquids[i];
                EnsureProxy(view, liquid);
                view.renderer.transform.SetPositionAndRotation(m_domain.center, Quaternion.identity);
                view.renderer.transform.localScale = m_domain.size;
                view.renderer.sharedMaterial = liquid.material;
                bool hasLiquidParticles = m_fluid != null &&
                    i < m_liveParticlesPerLiquid.Count &&
                    m_liveParticlesPerLiquid[i] > 0;
                view.renderer.enabled = hasLiquidParticles;
                view.entry.Positions = m_fluid != null ? m_fluid.Positions : null;
                view.entry.SubstanceIds = m_fluid != null ? m_fluid.SubstanceIds : null;
                view.entry.SubstanceIndex = i;
                view.entry.FilterBySubstance = true;
                // Count zero impede que a Renderer Feature agende depth/blur/normal
                // para a sentinela ou para um lote completamente morto.
                view.entry.Count = hasLiquidParticles ? m_fluid.NumParticles : 0;
                view.entry.Radius = settings.PhysicalRadius;
                view.entry.WorldBounds = m_domain;
                view.entry.SurfaceRenderer = view.renderer;
                if (view.entry.SurfaceProperties == null)
                    view.entry.SurfaceProperties = new MaterialPropertyBlock();
                view.entry.SurfaceProperties.Clear();
                if (view.entry.SurfaceDepth != null)
                    view.entry.SurfaceProperties.SetTexture(SurfaceDepth, view.entry.SurfaceDepth);
                if (view.entry.SurfaceNormal != null)
                    view.entry.SurfaceProperties.SetTexture(SurfaceNormal, view.entry.SurfaceNormal);
                view.renderer.SetPropertyBlock(view.entry.SurfaceProperties);
                SpillRenderBridge.Register(view.entry);
            }
        }

        void EnsureProxy(SurfaceView view, Liquid liquid)
        {
            if (view.renderer != null) return;
            GameObject proxy = GameObject.CreatePrimitive(PrimitiveType.Cube);
            proxy.name = "Spill Surface - " + liquid.name;
            Collider col = proxy.GetComponent<Collider>();
            if (col != null) Destroy(col);
            proxy.transform.SetParent(transform, true);
            view.renderer = proxy.GetComponent<MeshRenderer>();
            view.renderer.shadowCastingMode = ShadowCastingMode.Off;
            view.renderer.receiveShadows = true;
        }

        static void ApplyConfig(Material material, LiquidConfig config)
        {
            SetColor(material, "_ShallowColor", config.surfaceColor);
            SetColor(material, "_DeepColor", config.deepColor);
            SetColor(material, "_AbsorptionColor", config.absorptionColor);
            SetColor(material, "_ReflectionColor", config.surfaceColor);
            SetColor(material, "_EmissionColor", config.emissionColor);
            SetColor(material, "_FoamColor", config.foamColor);
            SetFloat(material, "_Opacity", config.surfaceOpacity);
            SetFloat(material, "_LightTransmission", config.lightTransmission * 2f);
            SetFloat(material, "_Absorption", Mathf.Clamp(config.absorptionDensity, 0f, 5f));
            SetFloat(material, "_Turbidity", config.turbidity);
            SetFloat(material, "_IOR", config.indexOfRefraction);
            SetFloat(material, "_Smoothness", config.smoothness);
            SetFloat(material, "_EmissionStrength", config.emissionStrength);
        }

        static void SetColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }

        static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        static FluidSolver.ColliderGPU ColliderToGpu(Collider col)
        {
            Transform tr = col.transform;
            Vector3 scale = tr.lossyScale;
            Vector3 abs = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            var gpu = new FluidSolver.ColliderGPU
            {
                axisX = tr.right,
                axisY = tr.up,
                axisZ = tr.forward,
                velocity = Vector3.zero
            };
            if (col is SphereCollider sphere)
            {
                gpu.type = 0;
                gpu.center = tr.TransformPoint(sphere.center);
                gpu.radius = sphere.radius * Mathf.Max(abs.x, Mathf.Max(abs.y, abs.z));
            }
            else if (col is CapsuleCollider capsule)
            {
                gpu.type = 2;
                gpu.center = tr.TransformPoint(capsule.center);
                if (capsule.direction == 0)
                {
                    gpu.axisY = tr.right; gpu.axisX = tr.up; gpu.axisZ = tr.forward;
                }
                else if (capsule.direction == 2)
                {
                    gpu.axisY = tr.forward; gpu.axisX = tr.right; gpu.axisZ = tr.up;
                }
                float axisScale = capsule.direction == 0 ? abs.x :
                    capsule.direction == 1 ? abs.y : abs.z;
                float radialScale = capsule.direction == 0 ? Mathf.Max(abs.y, abs.z) :
                    capsule.direction == 1 ? Mathf.Max(abs.x, abs.z) : Mathf.Max(abs.x, abs.y);
                gpu.radius = capsule.radius * radialScale;
                gpu.halfExt = new Vector3(0f,
                    Mathf.Max(0f, capsule.height * 0.5f * axisScale - gpu.radius), 0f);
            }
            else
            {
                BoxCollider box = col as BoxCollider;
                gpu.type = 1;
                if (box != null)
                {
                    gpu.center = tr.TransformPoint(box.center);
                    gpu.halfExt = Vector3.Scale(box.size, abs) * 0.5f;
                }
                else
                {
                    gpu.center = col.bounds.center;
                    gpu.axisX = Vector3.right;
                    gpu.axisY = Vector3.up;
                    gpu.axisZ = Vector3.forward;
                    gpu.halfExt = col.bounds.extents;
                }
            }
            return gpu;
        }

        static void EnsureArray<T>(ref T[] array, int count)
        {
            if (array == null || array.Length != count) array = new T[count];
        }

        void OnDestroy()
        {
            for (int i = 0; i < m_views.Count; i++)
            {
                SpillRenderBridge.Unregister(m_views[i].entry);
                if (m_views[i].renderer != null) Destroy(m_views[i].renderer.gameObject);
            }
            for (int i = 0; i < m_liquids.Count; i++)
                if (m_liquids[i].material != null) Destroy(m_liquids[i].material);
            m_solver?.Dispose();
            m_fluid?.Dispose();
            m_boundary?.Dispose();
        }
    }
}
