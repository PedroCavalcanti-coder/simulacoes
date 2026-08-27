using System;
using System.Collections.Generic;
using LabLiquidVR;
using LabSpill.Rendering;
using PBDFluid;
using UnityEngine;
using UnityEngine.Rendering;

namespace LabSpill
{
    /// <summary>
    /// Dono do fluido em particulas: um pool de capacidade fixa, o solver PBD e a
    /// publicacao das superficies para a Renderer Feature.
    ///
    /// A versao anterior tratava o conjunto de particulas como uma lista que crescia:
    /// emitir realocava dez ComputeBuffers e reconstruia o solver, quarenta vezes por
    /// segundo; a vida das particulas era decidida por um GetData sincrono a cada 0,1 s;
    /// e cada morte custava um SetData de um unico elemento. Aqui nada disso existe.
    /// A CPU so escolhe slots livres e le, de forma assincrona, a lista de mortes que a
    /// GPU produziu.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpillFluidWorld : MonoBehaviour
    {
        [Serializable]
        sealed class Liquid
        {
            public string key;
            public string name;
            public Material material;
            public LiquidConfig config;
        }

        sealed class SurfaceView
        {
            public readonly SpillRenderBridge.Entry entry = new SpillRenderBridge.Entry();
            public MeshRenderer renderer;
        }

        struct PendingReadback
        {
            public AsyncGPUReadbackRequest count;
            public AsyncGPUReadbackRequest deaths;
        }

        [Header("Configuracao unica")]
        public SpillVisualSettings settings;

        [Tooltip("Material base PBDFluid/SSFSurface. O JSON do frasco aplica as cores.")]
        public Material surfaceMaterialTemplate;

        [Header("Dominio")]
        [Tooltip("Envolve todos os colisores da cena no Start e sobe um pouco, para o " +
            "liquido ter espaco acima da bancada.")]
        public bool autoFitDomain = true;

        [Tooltip("Volume simulado, relativo a este objeto, quando autoFitDomain esta " +
            "desligado. Fora dele o hash da GPU grampeia as posicoes.")]
        public Bounds simulationBounds = new Bounds(Vector3.zero, new Vector3(6f, 4f, 6f));

        readonly List<Liquid> m_liquids = new List<Liquid>();
        readonly List<SurfaceView> m_views = new List<SurfaceView>();
        readonly List<int> m_liveParticlesPerLiquid = new List<int>();
        readonly Queue<PendingReadback> m_pending = new Queue<PendingReadback>(4);

        FluidPool m_pool;
        FluidSolver m_solver;
        SpillColliderProvider m_colliders;
        Bounds m_domain;
        Vector3 m_graveyard;

        // Substancia por slot. A GPU esconde o slot morto pondo 0xFFFFFFFF no
        // SubstanceIds, mas a CPU precisa lembrar o que havia ali para creditar o
        // volume no frasco quando a morte for uma captura.
        uint[] m_slotSubstance;

        FluidSolver.SpawnGPU[] m_spawnScratch;
        int m_spawnCount;

        // Discos de emissao em pacote hexagonal, recalculados so quando o raio do
        // gargalo muda. Substituem a rejeicao aleatoria O(n^2) que a emissao usava.
        readonly List<Vector2> m_emissionDisc = new List<Vector2>(32);
        float m_emissionDiscRadius = -1f;
        int m_emissionDiscCursor;

        SpillLiquidContainer[] m_receivers;
        FluidSolver.PortGPU[] m_ports = new FluidSolver.PortGPU[8];
        SpillLiquidContainer[] m_portOwners = new SpillLiquidContainer[8];
        int m_portCount;
        float m_sceneRefreshTimer;
        bool m_deathsRequested;

        static readonly int SurfaceDepth = Shader.PropertyToID("_PBDFluidSurfaceDepth");
        static readonly int SurfaceNormal = Shader.PropertyToID("_PBDFluidSurfaceNormal");
        static readonly int DensityThreshold = Shader.PropertyToID("_DensityThreshold");
        static readonly int EdgeSoftness = Shader.PropertyToID("_EdgeSoftness");

        public SpillVisualSettings Settings => settings;
        public int ParticleCount => m_pool != null ? m_pool.AliveCount : 0;
        public int LiquidCount => m_liquids.Count;
        public Bounds SimulationBounds => m_domain;

        // Contadores de diagnostico. Sem eles "parece igual" nao e' verificavel:
        // o pool, o nascimento na GPU e a morte assincrona sao invisiveis por
        // construcao, e a unica forma de saber se estao funcionando e medir.
        public int Capacity => m_pool != null ? m_pool.Capacity : 0;
        public int ColliderCount => m_colliders != null ? m_colliders.Count : 0;
        public int PortCount => m_portCount;
        public int SpawnedTotal { get; private set; }
        public int DiedByAge { get; private set; }
        public int DiedByCapture { get; private set; }

        void Start()
        {
            if (settings == null)
            {
                Debug.LogError("[LabSpill] SpillVisualSettings nao atribuido.", this);
                enabled = false;
                return;
            }

            m_domain = autoFitDomain
                ? FitDomainToScene()
                : new Bounds(transform.position + simulationBounds.center, simulationBounds.size);
            m_graveyard = m_domain.min + Vector3.down * 50f;

            // O grid hash ordena Capacity elementos com bitonic sort, que tem teto proprio.
            int capacity = Mathf.Min(settings.maxParticles, BitonicSort.MAX_ELEMENTS);
            m_pool = new FluidPool(capacity, settings.PhysicalRadius, 1000f);
            m_pool.ParticleMass *= settings.massScale;
            m_pool.Viscosity = settings.viscosity;
            m_pool.ParkAll(m_graveyard);

            m_slotSubstance = new uint[m_pool.Capacity];
            for (int i = 0; i < m_slotSubstance.Length; i++) m_slotSubstance[i] = uint.MaxValue;

            m_spawnScratch = new FluidSolver.SpawnGPU[Mathf.Max(128, settings.maxParticlesPerFrame)];

            m_solver = new FluidSolver(m_pool, m_domain, m_spawnScratch.Length)
            {
                Graveyard = m_graveyard,
                PortEntryDepth = settings.PhysicalRadius * 2f
            };
            ApplyTunables();

            m_colliders = new SpillColliderProvider(settings.maxColliders);
            RefreshSceneCache();
            UploadColliders(force: true);
        }

        void Update()
        {
            if (m_pool == null) return;

            m_sceneRefreshTimer += Time.deltaTime;
            if (m_sceneRefreshTimer >= 0.5f)
            {
                m_sceneRefreshTimer = 0f;
                RefreshSceneCache();
            }

            UploadColliders(force: false);
            DrainReadbacks();
        }

        void FixedUpdate()
        {
            if (m_solver == null) return;

            ApplyTunables();
            FlushSpawns();
            UploadPorts();

            // O contador so e zerado depois que o conteudo anterior ja foi PEDIDO. Zerar
            // incondicionalmente perderia as mortes de um passo cuja leitura nao coube na
            // fila, e slot cuja morte ninguem leu nunca volta para a lista livre: o pool
            // secaria aos poucos ate o derrame parar.
            if (m_deathsRequested)
            {
                m_solver.ResetDeaths();
                m_deathsRequested = false;
            }

            int steps = Mathf.Clamp(settings.maxPhysicsStepsPerFrame, 1, 4);
            float dt = Time.fixedDeltaTime / steps;
            for (int i = 0; i < steps; i++)
                m_solver.StepPhysics(dt, Time.time);

            RequestDeathReadback();
        }

        void LateUpdate() => PublishSurfaces();

        /// <summary>
        /// Copia do asset os parametros que se calibram no olho. Roda todo passo de
        /// proposito: lidos so uma vez no Start, calibrar coesao ou viscosidade exigia
        /// sair do Play, mudar, voltar - e comparar de memoria, que e justamente como
        /// nao se calibra nada.
        /// </summary>
        void ApplyTunables()
        {
            m_solver.SolverIterations = settings.solverIterations;
            m_solver.ConstraintIterations = settings.constraintIterations;
            m_solver.RestDamping = settings.restDamping;
            m_solver.Cohesion = settings.cohesion;
            m_pool.Viscosity = settings.viscosity;
        }

        // ------------------------------------------------------------------ liquidos

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

            m_liquids.Add(new Liquid
            {
                key = key,
                name = config.liquidName,
                material = material,
                config = LiquidConfig.Copy(config)
            });
            m_liveParticlesPerLiquid.Add(0);
            return m_liquids.Count - 1;
        }

        // ------------------------------------------------------------------ emissao

        /// <summary>
        /// Reserva slots e monta as particulas de um jato. Devolve quantas foram aceitas;
        /// o emissor devolve ao frasco o volume que sobrou.
        ///
        /// A distribuicao e deterministica: as particulas ocupam um disco hexagonal na
        /// boca e se espalham ao longo da distancia que o jato percorreu desde a ultima
        /// emissao. Nao ha mais sorteio com rejeicao, que antes gastava ate 288 tentativas
        /// por particula e ainda assim podia empilhar duas no mesmo ponto.
        /// </summary>
        public int QueueJet(Vector3 origin, Vector3 velocity, int count, int liquidIndex,
            float neckRadius, Vector3 mouthNormal)
        {
            if (m_pool == null || liquidIndex < 0 || liquidIndex >= m_liquids.Count || count <= 0)
                return 0;

            int room = Mathf.Min(count, m_spawnScratch.Length - m_spawnCount);
            if (room <= 0) return 0;

            Vector3 direction = velocity.sqrMagnitude > 1e-6f ? velocity.normalized : Vector3.down;
            Vector3 normal = mouthNormal.sqrMagnitude > 1e-6f ? mouthNormal.normalized : -direction;
            Vector3 reference = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.92f
                ? Vector3.right : Vector3.up;
            Vector3 tangent = Vector3.Cross(normal, reference).normalized;
            Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;

            float radius = settings.PhysicalRadius;
            float usableRadius = Mathf.Max(0f, neckRadius - radius);
            BuildEmissionDisc(usableRadius, radius);

            // Comprimento de jato que a boca "varreu" desde o ultimo frame. Espalhar as
            // particulas por ele e o que produz um filete continuo em vez de um bloco
            // de particulas nascendo sobrepostas no mesmo ponto.
            float travel = Mathf.Max(radius * 2f, velocity.magnitude * Time.deltaTime);
            float now = Time.time;

            int accepted = 0;
            for (int i = 0; i < room; i++)
            {
                if (!m_pool.TryAllocate(out int slot)) break;

                Vector2 disc = m_emissionDisc[m_emissionDiscCursor % m_emissionDisc.Count];
                m_emissionDiscCursor++;

                float along = (i + 0.5f) / room * travel;
                Vector3 position = origin
                    + tangent * disc.x + bitangent * disc.y
                    + direction * along;

                m_slotSubstance[slot] = (uint)liquidIndex;
                m_spawnScratch[m_spawnCount++] = new FluidSolver.SpawnGPU(
                    position,
                    velocity,
                    now + UnityEngine.Random.Range(
                        settings.particleLifetimeMin, settings.particleLifetimeMax),
                    (uint)liquidIndex,
                    (uint)slot);
                m_liveParticlesPerLiquid[liquidIndex]++;
                SpawnedTotal++;
                accepted++;
            }

            return accepted;
        }

        void BuildEmissionDisc(float usableRadius, float particleRadius)
        {
            if (Mathf.Abs(usableRadius - m_emissionDiscRadius) < 1e-5f && m_emissionDisc.Count > 0)
                return;

            m_emissionDiscRadius = usableRadius;
            m_emissionDisc.Clear();
            m_emissionDisc.Add(Vector2.zero);

            float spacing = particleRadius * 2f;
            for (int ring = 1; ring * spacing <= usableRadius; ring++)
            {
                int points = ring * 6;
                float ringRadius = ring * spacing;
                for (int p = 0; p < points; p++)
                {
                    float angle = p / (float)points * Mathf.PI * 2f;
                    m_emissionDisc.Add(new Vector2(
                        Mathf.Cos(angle) * ringRadius, Mathf.Sin(angle) * ringRadius));
                }
            }
        }

        void FlushSpawns()
        {
            if (m_spawnCount == 0) return;
            m_solver.Spawn(m_spawnScratch, m_spawnCount);
            m_spawnCount = 0;
        }

        // ------------------------------------------------------------------ mortes

        void RequestDeathReadback()
        {
            // Sem pedido novo enquanto a fila estiver cheia. Como o reset depende deste
            // sinalizador, pular aqui apenas adia: as mortes se acumulam no mesmo buffer
            // (que tem Capacity entradas, e nenhuma particula morre duas vezes) e a
            // proxima leitura leva todas.
            if (m_pending.Count >= 8) return;

            m_pending.Enqueue(new PendingReadback
            {
                count = AsyncGPUReadback.Request(m_solver.CopyDeathCount()),
                deaths = AsyncGPUReadback.Request(m_pool.Deaths)
            });
            m_deathsRequested = true;
        }

        void DrainReadbacks()
        {
            while (m_pending.Count > 0)
            {
                PendingReadback pending = m_pending.Peek();
                if (!pending.count.done || !pending.deaths.done) return;
                m_pending.Dequeue();

                if (pending.count.hasError || pending.deaths.hasError) continue;

                int dead = (int)pending.count.GetData<uint>()[0];
                if (dead <= 0) continue;

                var records = pending.deaths.GetData<uint>();
                int available = records.Length / 2;
                dead = Mathf.Min(dead, available);

                for (int i = 0; i < dead; i++)
                {
                    int slot = (int)records[i * 2];
                    uint portPlusOne = records[i * 2 + 1];
                    ReleaseParticle(slot, portPlusOne);
                }
            }
        }

        void ReleaseParticle(int slot, uint portPlusOne)
        {
            if (slot < 0 || slot >= m_slotSubstance.Length) return;

            uint substance = m_slotSubstance[slot];
            if (substance == uint.MaxValue) return;   // ja devolvido por uma leitura anterior

            m_slotSubstance[slot] = uint.MaxValue;
            m_pool.ReleaseSlot(slot);
            if (substance < m_liveParticlesPerLiquid.Count)
                m_liveParticlesPerLiquid[(int)substance] =
                    Mathf.Max(0, m_liveParticlesPerLiquid[(int)substance] - 1);

            if (portPlusOne == 0)
            {
                DiedByAge++;
                return;
            }

            DiedByCapture++;
            int portIndex = (int)portPlusOne - 1;
            if (portIndex < 0 || portIndex >= m_portCount) return;

            SpillLiquidContainer receiver = m_portOwners[portIndex];
            if (receiver == null || substance >= m_liquids.Count) return;

            receiver.ReceiveLiquid(m_liquids[(int)substance].config, settings.millilitersPerParticle);
        }

        // ------------------------------------------------------------------ cena

        /// <summary>
        /// Envolve todos os colisores da cena. Substitui o calculo antigo, que somava os
        /// colisores marcados como bancada ou chao - marcadores que deixaram de existir
        /// quando a particula passou a colidir com tudo.
        /// </summary>
        Bounds FitDomainToScene()
        {
            var colliders = FindObjectsByType<Collider>(FindObjectsInactive.Exclude);
            bool any = false;
            Bounds result = new Bounds(transform.position, Vector3.one);

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider col = colliders[i];
                if (col == null || !col.enabled || col.isTrigger) continue;

                if (!any) { result = col.bounds; any = true; }
                else result.Encapsulate(col.bounds);
            }

            if (!any)
                return new Bounds(transform.position + simulationBounds.center, simulationBounds.size);

            // Folga lateral para respingo, e mais no topo: e para onde o jato sobe quando
            // se despeja de um frasco erguido acima da bancada.
            result.Expand(new Vector3(0.5f, 0.5f, 0.5f));
            result.Encapsulate(new Vector3(result.center.x, result.max.y + 1f, result.center.z));
            return result;
        }

        void RefreshSceneCache()
        {
            m_receivers = FindObjectsByType<SpillLiquidContainer>(FindObjectsInactive.Exclude);
        }

        void UploadColliders(bool force)
        {
            if (m_colliders.Refresh(m_domain, settings.colliderRefreshInterval, force))
                m_solver.SetColliders(m_colliders.Array, m_colliders.Count);
        }

        void UploadPorts()
        {
            m_portCount = 0;
            if (m_receivers == null) return;

            float radius = settings.PhysicalRadius;
            float visualPad = Mathf.Max(0f, radius * settings.visualRadiusScale - radius);

            for (int i = 0; i < m_receivers.Length; i++)
            {
                SpillLiquidContainer candidate = m_receivers[i];
                if (candidate == null || !candidate.isActiveAndEnabled) continue;

                // Frasco cheio nao vira porto: sem isso a particula sumiria na boca sem
                // virar volume nenhum.
                if (candidate.currentVolumeML >= candidate.capacityML -
                    settings.millilitersPerParticle * 0.5f) continue;

                if (!candidate.TryGetOpening(out Vector3 center, out Vector3 normal, out float openingRadius))
                    continue;
                if (Vector3.Dot(normal, Vector3.up) < 0.55f) continue;

                if (m_portCount >= m_ports.Length)
                {
                    System.Array.Resize(ref m_ports, m_ports.Length * 2);
                    System.Array.Resize(ref m_portOwners, m_portOwners.Length * 2);
                }

                m_ports[m_portCount] = new FluidSolver.PortGPU
                {
                    center = center,
                    normal = normal,
                    // O SSF desenha a gota maior que o raio fisico. A captura acompanha
                    // essa silhueta: se a gota visual entrou no gargalo, o volume entra.
                    radius = openingRadius + visualPad,
                    captureDepth = Mathf.Max(0.08f, openingRadius * 2.5f)
                };
                m_portOwners[m_portCount] = candidate;
                m_portCount++;
            }

            m_solver.SetPorts(m_ports, m_portCount);
        }

        // ------------------------------------------------------------------ render

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

                bool hasParticles = m_pool != null && m_liveParticlesPerLiquid[i] > 0;
                view.renderer.enabled = hasParticles;
                view.entry.Positions = m_pool?.Positions;
                view.entry.SubstanceIds = m_pool?.SubstanceIds;
                view.entry.SubstanceIndex = i;
                view.entry.FilterBySubstance = true;
                // Slot morto carrega SubstanceIds 0xFFFFFFFF, que nunca casa com o
                // indice de nenhum liquido: o vertex shader do SSF descarta sozinho.
                view.entry.Count = hasParticles ? m_pool.Capacity : 0;
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
            m_pool?.Dispose();
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.35f, 0.8f, 1f, 0.5f);
            Gizmos.DrawWireCube(transform.position + simulationBounds.center, simulationBounds.size);
        }
    }
}
