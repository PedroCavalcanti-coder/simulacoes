using System.Collections.Generic;
using UnityEngine;

namespace LabLiquidVR
{
    [System.Serializable]
    public struct SpillGraduationPoint
    {
        [Min(0f)] public float milliliters;
        [Range(0f, 1f)] public float normalizedHeight;

        public SpillGraduationPoint(float milliliters, float normalizedHeight)
        {
            this.milliliters = milliliters;
            this.normalizedHeight = normalizedHeight;
        }
    }

    [System.Serializable]
    public struct InitialLiquidPortion
    {
        public LiquidConfigAsset configFile;
        [Min(0f)] public float milliliters;
    }

    // ------------------------------------------------------------------------
    //  LiquidContainer  (L1)
    //  Recipiente com liquido de nivel FAKE. Voxeliza a cavidade (LiquidVolumeBaker)
    //  e dirige o Liquid.shader (Triple Axis, ja URP) por VOLUME em mL — nao por
    //  fracao de altura. Superficie = plano horizontal-mundo no percentil de volume;
    //  conserva mL ao inclinar. Slosh = superficie tipo PENDULO: fica perpendicular
    //  a gravidade APARENTE (g + aceleracao do frasco), com mola-amortecida -> responde
    //  a movimento rapido, ondula, e assenta sozinho. Baker so define a ALTURA (volume).
    //
    //  Vai no GameObject do LIQUIDO (MeshRenderer com material Liquid.shader +
    //  MeshFilter com a mesh da cavidade). Mesh precisa de Read/Write ON.
    // ------------------------------------------------------------------------
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public sealed class SpillLiquidContainer : MonoBehaviour
    {
        const float EmptyVolumeEpsilonML = 0.0001f;

        [Header("Cavidade / volume")]
        [Tooltip("Mesh da cavidade. Vazio = usa a MeshFilter deste objeto.")]
        public Mesh cavityMesh;
        [Range(8, 64)] public int voxelResolution = 32;
        [Min(0.01f)] public float capacityML = 250f;
        [Min(0f)]    public float currentVolumeML = 100f;

        [Header("Arquivo do liquido")]
        [Tooltip("Asset compartilhado por corpo, interior, ondas, bolhas e vapor. Vazio usa os defaults de LiquidConfig.")]
        public LiquidConfigAsset liquidConfigFile;

        [Header("Mistura e separacao")]
        [Tooltip("Tempo aproximado para fases imisciveis trocarem de posicao.")]
        [Min(0.1f)] public float densitySeparationSeconds = 4f;
        [Tooltip("Composicao inicial opcional. Vazio usa currentVolumeML e liquidConfigFile.")]
        public List<InitialLiquidPortion> initialComposition = new List<InitialLiquidPortion>();

        [Header("Temperatura")]
        [Tooltip("Temperatura atual do liquido em graus Celsius.")]
        public float currentTemperatureC = 22f;

        [Header("Calibracao pelas graduacoes")]
        [Tooltip("Usa os pontos abaixo para alinhar o nivel visual às marcas reais do frasco.")]
        public bool useGraduationCalibration = true;
        [Tooltip("Cada ponto associa um volume em mL a uma altura entre 0 (fundo) e 1 (topo). A ordem nao importa.")]
        public List<SpillGraduationPoint> graduationCalibration = new List<SpillGraduationPoint>
        {
            new SpillGraduationPoint(0f, 0f),
            new SpillGraduationPoint(250f, 1f)
        };

        [Header("Derramar")]
        [Tooltip("Vazão (mL/s) com a superfície bem acima do bico.")]
        public float pourRateMLperSec = 40f;

        [Header("Slosh / ondas")]
        [Tooltip("Quanto a superfície inclina no movimento (0..1.5). 1 = físico (pêndulo).")]
        [Range(0f, 1.5f)] public float sloshStrength = 0.8f;
        [Tooltip("Rigidez da mola da superfície: baixo = mais balança/lag; alto = rígido.")]
        public float sloshStiffness = 30f;
        [Tooltip("Amortecimento: quão rápido a superfície assenta.")]
        public float sloshDamping = 3.5f;
        [Tooltip("Ondas + espuma por agitação.")]
        public float waveResponse = 5f;
        [Tooltip("Sensibilidade das ondas a aceleracao, giro e deslocamento rapido.")]
        [Range(0.1f, 5f)] public float motionSensitivity = 1.8f;
        [Tooltip("Resposta radial quando o frasco sobe ou desce.")]
        [Range(0f, 5f)] public float verticalWaveResponse = 2.2f;
        [Tooltip("Rapidez com que novas ondas ganham energia.")]
        [Range(0.1f, 30f)] public float waveAttack = 14f;
        [Tooltip("Rapidez com que as ondas perdem energia. Menor = duram mais.")]
        [Range(0.1f, 10f)] public float waveDecay = 1.8f;

        public float FillFraction => capacityML > 1e-4f ? Mathf.Clamp01(currentVolumeML / capacityML) : 0f;
        public float CalibratedHeight01 => EvaluateCalibratedHeight(currentVolumeML);
        public bool  WouldPour { get; private set; }
        public LiquidConfig Config
        {
            get
            {
                EnsureLiquidConfig();
                LiquidPhase top = m_composition != null ? m_composition.TopPhase : null;
                return top != null ? top.appearance : m_config;
            }
        }
        public LiquidConfig PourConfig => Config;
        public float PlaneWorldY { get; private set; }
        public float SpoutWorldY { get; private set; }
        public Vector3 SpoutWorldPosition { get; private set; }
        public Vector3 SpoutWorldCenter { get; private set; }
        public float SpoutRadiusWorld { get; private set; }
        public Plane SurfacePlane { get; private set; } = new Plane(Vector3.up, Vector3.zero);
        public Vector3 ContainerUpWorld => m_baker != null && m_baker.IsBaked
            ? transform.TransformDirection(m_baker.LocalUp).normalized
            : transform.up;
        public float BoilingPointC => Config != null ? Config.boilingPointC : 100f;
        public Color BubbleColor => Config != null
            ? Config.bubbleColor
            : new Color(0.78f, 0.96f, 1f, 0.48f);
        public Color VaporColor => Config != null
            ? Config.vaporColor
            : new Color(1f, 1f, 1f, 0.25f);
        public float BoilingIntensity01 { get; private set; }
        public bool TryGetOpening(out Vector3 center, out Vector3 normal, out float radius)
        {
            EnsureInit();
            if (m_baker == null || !m_baker.IsBaked)
            {
                center = transform.position;
                normal = transform.up;
                radius = 0f;
                return false;
            }

            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            center = m_baker.RimWorldCenter(localToWorld);
            normal = transform.TransformDirection(m_baker.LocalUp).normalized;
            radius = m_baker.RimWorldRadius(localToWorld);
            return radius > 0f;
        }

        MeshRenderer m_renderer;
        Material m_mat;
        LiquidVolumeBaker m_baker;
        LiquidSurfaceMesh m_surfaceMesh;
        LiquidConfig m_config;
        LiquidConfigAsset m_loadedConfigFile;
        bool m_configAppliedToMaterial;
        LiquidComposition m_composition;

        sealed class LayerVisual
        {
            public GameObject gameObject;
            public MeshRenderer renderer;
            public MeshFilter filter;
            public Material material;
            public LiquidConfig appliedConfig;
        }

        readonly List<LayerVisual> m_layerVisuals = new List<LayerVisual>(4);
        readonly List<LiquidPhase> m_renderOrder = new List<LiquidPhase>(4);

        // slosh: superficie = pendulo (mola-amortecida) que segue a gravidade APARENTE
        // (gravidade + aceleracao do frasco). Responde a movimento rapido, assenta sozinho.
        Vector3 m_prevPos, m_prevVel, m_velocity, m_accel, m_angVel;
        Quaternion m_prevRot;
        Vector2 m_tilt, m_tiltVel;    // inclinacao (x,z) do normal da superficie
        float m_bob, m_bobVel;        // ondulacao vertical (subir/descer)
        float m_wavesMult = 1f;
        float m_foam;
        Vector2 m_waveDirection = Vector2.right;
        float m_wavePhase;
        float m_waveAmplitude;
        float m_verticalWaveAmplitude;

        // cache do plano
        float m_cachedPlaneY; float m_lastFrac = -1f; Quaternion m_lastRot; bool m_planeDirty = true; float m_lastPosY;
        bool m_hasPouredSinceFilled;
        SpillPourEmitter m_pbdPourEmitter;

        void Awake()
        {
            EnsureInit();
            m_prevPos = transform.position;
            m_prevRot = transform.rotation;
            m_lastRot = transform.rotation;
            m_lastPosY = transform.position.y;
        }

        void OnValidate()
        {
            capacityML = Mathf.Max(0.01f, capacityML);
            currentVolumeML = Mathf.Clamp(currentVolumeML, 0f, capacityML);
            m_planeDirty = true;
            m_config = null;
            m_loadedConfigFile = null;
            m_configAppliedToMaterial = false;
            if (m_renderer == null) m_renderer = GetComponent<MeshRenderer>();
            // Alterar Renderer.enabled dentro de OnValidate dispara callbacks de
            // visibilidade enquanto o Unity ainda verifica a consistencia do objeto.
            // Awake/Update e SetVolumeML ja mantem o estado visual sincronizado.
        }

        void EnsureLiquidConfig()
        {
            if (m_config != null && m_loadedConfigFile == liquidConfigFile) return;
            m_config = LiquidConfig.Load(liquidConfigFile);
            m_loadedConfigFile = liquidConfigFile;
            m_configAppliedToMaterial = false;
        }

        void ApplyLiquidConfigToMaterial()
        {
            if (m_mat == null || m_config == null || m_configAppliedToMaterial) return;
            SetMaterialColor("_SurfaceColor", m_config.surfaceColor);
            SetMaterialColor("_BodyColor", m_config.bodyColor);
            SetMaterialColor("_DeepColor", m_config.deepColor);
            SetMaterialColor("_AbsorptionColor", m_config.absorptionColor);
            SetMaterialColor("_FoamColor", m_config.foamColor);
            SetMaterialColor("_EmissionColor", m_config.emissionColor);
            SetMaterialFloat("_AbsorptionDensity", m_config.absorptionDensity);
            SetMaterialFloat("_Transparency", m_config.transparency);
            SetMaterialFloat("_TransmissionStrength", m_config.lightTransmission);
            SetMaterialFloat("_SurfaceOpacity", m_config.surfaceOpacity);
            SetMaterialFloat("_Turbidity", m_config.turbidity);
            SetMaterialFloat("_Smoothness", m_config.smoothness);
            SetMaterialFloat("_IOR", m_config.indexOfRefraction);
            SetMaterialFloat("_EmissionStrength", m_config.emissionStrength);
            SetMaterialFloat("_MaximumWaveHeight", m_config.maximumWaveHeight);
            SetMaterialFloat("_MaximumVerticalWaveHeight", m_config.maximumVerticalWaveHeight);
            SetMaterialFloat("_WaveFrequency", m_config.waveFrequency);
            SetMaterialFloat("_WaveIrregularity", m_config.waveIrregularity);
            SetMaterialFloat("_WaveDetail", m_config.waveDetail);
            SetMaterialFloat("_MicroWaveAmplitude", m_config.microWaveAmplitude);
            SetMaterialFloat("_MicroWaveSpeed", m_config.microWaveSpeed);
            m_configAppliedToMaterial = true;
        }

        void SetMaterialColor(string propertyName, Color value)
        {
            if (m_mat.HasProperty(propertyName)) m_mat.SetColor(propertyName, value);
        }

        void SetMaterialFloat(string propertyName, float value)
        {
            if (m_mat.HasProperty(propertyName)) m_mat.SetFloat(propertyName, value);
        }

        void UpdateRendererVisibility()
        {
            bool visible = currentVolumeML > EmptyVolumeEpsilonML;
            if (m_renderer != null)
                m_renderer.enabled = visible;
            if (!visible)
                for (int i = 1; i < m_layerVisuals.Count; i++)
                    if (m_layerVisuals[i].renderer != null)
                        m_layerVisuals[i].renderer.enabled = false;
            m_surfaceMesh?.SetVisible(visible);
        }

        float EvaluateCalibratedHeight(float milliliters)
        {
            float linear = capacityML > 1e-4f ? Mathf.Clamp01(milliliters / capacityML) : 0f;
            if (!useGraduationCalibration || graduationCalibration == null || graduationCalibration.Count == 0)
                return linear;

            SpillGraduationPoint lower = default;
            SpillGraduationPoint upper = default;
            bool hasLower = false;
            bool hasUpper = false;
            for (int i = 0; i < graduationCalibration.Count; i++)
            {
                SpillGraduationPoint point = graduationCalibration[i];
                if (point.milliliters <= milliliters && (!hasLower || point.milliliters > lower.milliliters))
                {
                    lower = point;
                    hasLower = true;
                }
                if (point.milliliters >= milliliters && (!hasUpper || point.milliliters < upper.milliliters))
                {
                    upper = point;
                    hasUpper = true;
                }
            }

            if (!hasLower && !hasUpper) return linear;
            if (!hasLower) return Mathf.Clamp01(upper.normalizedHeight);
            if (!hasUpper) return Mathf.Clamp01(lower.normalizedHeight);
            if (Mathf.Abs(upper.milliliters - lower.milliliters) < 1e-4f)
                return Mathf.Clamp01(lower.normalizedHeight);

            float t = Mathf.InverseLerp(lower.milliliters, upper.milliliters, milliliters);
            return Mathf.Clamp01(Mathf.Lerp(lower.normalizedHeight, upper.normalizedHeight, t));
        }

        float EffectiveVolumeFraction()
        {
            if (!useGraduationCalibration || m_baker == null || !m_baker.IsBaked)
                return FillFraction;
            return m_baker.VolumeFractionAtNormalizedHeight(CalibratedHeight01);
        }

        float VolumeFractionForML(float milliliters)
        {
            float height = EvaluateCalibratedHeight(Mathf.Clamp(milliliters, 0f, capacityML));
            return m_baker != null && m_baker.IsBaked
                ? m_baker.VolumeFractionAtNormalizedHeight(height)
                : Mathf.Clamp01(milliliters / Mathf.Max(0.01f, capacityML));
        }

        // (re)inicializa refs + bake. Robusto a domain reload no Editor: Awake nao
        // re-roda mas campos privados viram null; Update chama isto p/ evitar NRE.
        void EnsureInit()
        {
            EnsureLiquidConfig();
            if (m_composition == null)
            {
                m_composition = new LiquidComposition();
                if (initialComposition != null && initialComposition.Count > 0)
                {
                    m_composition.Reset(m_config, 0f);
                    for (int i = 0; i < initialComposition.Count; i++)
                    {
                        InitialLiquidPortion portion = initialComposition[i];
                        if (portion.milliliters <= 0f) continue;
                        m_composition.Receive(
                            LiquidConfig.Load(portion.configFile),
                            portion.milliliters,
                            capacityML);
                    }
                    currentVolumeML = m_composition.TotalVolumeML;
                }
                else
                {
                    m_composition.Reset(m_config, currentVolumeML);
                }
            }
            if (m_renderer == null) m_renderer = GetComponent<MeshRenderer>();
            if (m_pbdPourEmitter == null) m_pbdPourEmitter = GetComponentInParent<SpillPourEmitter>();
            if (m_mat == null && m_renderer != null)
                m_mat = Application.isPlaying ? m_renderer.material : m_renderer.sharedMaterial;
            ApplyLiquidConfigToMaterial();
            if (cavityMesh == null) cavityMesh = GetComponent<MeshFilter>().sharedMesh;
            if ((m_baker == null || !m_baker.IsBaked) && cavityMesh != null)   // mesh nula (import) -> tenta no proximo frame
            {
                m_baker = new LiquidVolumeBaker();
                m_baker.Bake(cavityMesh, voxelResolution, transform.InverseTransformDirection(Vector3.up));
                m_planeDirty = true;
            }
            if (Application.isPlaying && m_mat != null && m_renderer != null)
            {
                m_surfaceMesh ??= new LiquidSurfaceMesh();
                m_surfaceMesh.Ensure(transform, m_renderer, m_mat);
                if (m_mat.HasProperty("_IsSurfaceMesh")) m_mat.SetFloat("_IsSurfaceMesh", 0f);
            }
        }

        float CurrentViscosity()
        {
            if (Config != null) return Mathf.Max(0.2f, Config.viscosity);
            if (m_mat != null && m_mat.HasProperty("_Viscosity"))
                return Mathf.Max(0.2f, m_mat.GetFloat("_Viscosity"));
            return 1f;
        }

        void Update()
        {
            EnsureInit();
            if (m_mat == null || m_baker == null || !m_baker.IsBaked) return;
            currentVolumeML = Mathf.Clamp(m_composition.TotalVolumeML, 0f, capacityML);
            UpdateRendererVisibility();
            if (currentVolumeML <= EmptyVolumeEpsilonML)
            {
                currentVolumeML = 0f;
                BoilingIntensity01 = 0f;
                WouldPour = false;
                m_hasPouredSinceFilled = false;
                m_pbdPourEmitter?.CancelPendingPour();
                m_mat.SetFloat("_Volume01", 0f);
                return;
            }
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            m_composition.UpdateSeparation(dt, densitySeparationSeconds);
            Bounds b = m_renderer.bounds;

            // ===== slosh: superficie = pendulo seguindo a gravidade APARENTE =====
            // gravidade aparente = g*up + aceleracao do frasco. A superficie fica
            // perpendicular a ela -> movimento rapido inclina/ondula, e assenta sozinho.
            const float G = 9.81f;
            Vector3 gApp = Vector3.up * G + m_accel;
            float denom = Mathf.Max(gApp.y, 1f);
            Vector2 targetTilt = new Vector2(gApp.x, gApp.z) / denom * sloshStrength;
            targetTilt += new Vector2(m_angVel.z, -m_angVel.x) * (sloshStrength * 0.15f);  // giro agita
            targetTilt = Vector2.ClampMagnitude(targetTilt, 0.7f);

            // Viscosidade aumenta o amortecimento e retarda a resposta sem mudar
            // o equilibrio final da superficie (que continua perpendicular a g).
            float viscosity = Mathf.Max(1f, CurrentViscosity());
            float viscousResponse = 1f + Mathf.Log10(viscosity);
            float effectiveStiffness = sloshStiffness / Mathf.Sqrt(viscousResponse);
            float effectiveDamping = sloshDamping * viscousResponse;

            // mola-amortecida persegue o alvo -> lag + oscilacao + assentamento
            Vector2 tiltAcc = (targetTilt - m_tilt) * effectiveStiffness - m_tiltVel * effectiveDamping;
            m_tiltVel += tiltAcc * dt;
            m_tilt += m_tiltVel * dt;

            // ondulacao vertical (subir/descer rapido)
            float bobAcc = -m_accel.y * (sloshStrength * 0.008f) - m_bob * effectiveStiffness - m_bobVel * effectiveDamping;
            m_bobVel += bobAcc * dt;
            m_bob = Mathf.Clamp(m_bob + m_bobVel * dt, -0.035f, 0.035f);

            // agitacao -> ondas (_WavesMult) + espuma (_Foam)
            float agitation = (m_tiltVel.magnitude + Mathf.Abs(m_bobVel) * 15f + m_angVel.magnitude) / viscousResponse;
            m_wavesMult = Mathf.Lerp(m_wavesMult, 1f + Mathf.Clamp(agitation * waveResponse, 0f, 5f), dt * 4f);
            float targetFoam = Mathf.Max(
                Mathf.Clamp01(agitation * 0.25f),
                BoilingIntensity01 * 0.32f);
            m_foam = Mathf.Clamp01(Mathf.Lerp(m_foam, targetFoam, dt * 3f));

            Vector3 surfNormal = new Vector3(m_tilt.x, 1f, m_tilt.y).normalized;

            // nivel por VOLUME (baker): conserva ao inclinar. Slosh so inclina a NORMAL.
            float frac = EffectiveVolumeFraction();
            Matrix4x4 l2w = transform.localToWorldMatrix;
            float posY = transform.position.y;
            if (m_planeDirty || Quaternion.Angle(transform.rotation, m_lastRot) > 0.4f || Mathf.Abs(frac - m_lastFrac) > 1e-4f)
            {
                m_cachedPlaneY = m_baker.PlaneYForVolume(l2w, frac);          // re-sort completo (girou/mudou mL)
                m_lastFrac = frac; m_lastRot = transform.rotation; m_planeDirty = false;
            }
            else
            {
                m_cachedPlaneY += posY - m_lastPosY;   // pura translacao: plano acompanha o Y (sem re-sort)
            }
            m_lastPosY = posY;
            PlaneWorldY = m_cachedPlaneY;

            // ponto do plano: nivel + bob (ondulacao visual). Pour usa m_cachedPlaneY (real).
            Vector3 planePoint = new Vector3(b.center.x, m_cachedPlaneY + m_bob, b.center.z);
            Plane plane = new Plane(surfNormal, planePoint);
            SurfacePlane = plane;

            // derrama? superficie acima do bico
            SpoutWorldY = m_baker.SpoutWorldY(l2w);
            SpoutWorldPosition = m_baker.SpoutWorldPoint(l2w);
            SpoutWorldCenter = m_baker.RimWorldCenter(l2w);
            SpoutRadiusWorld = m_baker.RimWorldRadius(l2w);
            bool drainingVoxelRemainder = m_hasPouredSinceFilled && frac <= 0f;
            WouldPour = currentVolumeML > EmptyVolumeEpsilonML &&
                (m_cachedPlaneY >= SpoutWorldY - 1e-3f || drainingVoxelRemainder);

            // O emissor PBD é o único caminho de derramamento. O débito só acontece
            // depois que o mundo aceita as partículas, mantendo mL e partículas iguais.
            bool usePbd = m_pbdPourEmitter != null && m_pbdPourEmitter.isActiveAndEnabled;
            if (WouldPour && usePbd)
            {
                Vector3 spout = m_baker.SpoutWorldPoint(l2w);
                float head = Mathf.Max(0.001f, m_cachedPlaneY - SpoutWorldY);           // altura acima do bico
                float visc = CurrentViscosity();
                float rate = pourRateMLperSec * Mathf.Clamp01(head / 0.02f) / Mathf.Sqrt(visc);  // mL/s
                float requestedMl = drainingVoxelRemainder
                    ? currentVolumeML
                    : Mathf.Min(rate * dt, currentVolumeML);

                float speed = Mathf.Sqrt(2f * 9.81f * head) * 0.6f;
                Rigidbody body = GetComponentInParent<Rigidbody>();
                Vector3 vesselVelocity = body != null ? body.GetPointVelocity(spout) : m_velocity;
                Vector3 mouthNormal = ContainerUpWorld;
                Vector3 pbdVelocity = vesselVelocity + mouthNormal * speed + Vector3.down * 0.2f;
                float emittedMl = m_pbdPourEmitter.TryEmitPour(
                    requestedMl,
                    SpoutWorldCenter,
                    mouthNormal,
                    pbdVelocity);

                if (emittedMl > 0f)
                {
                    m_composition.RemoveFromTop(emittedMl);
                    currentVolumeML = m_composition.TotalVolumeML;
                    if (currentVolumeML <= EmptyVolumeEpsilonML) currentVolumeML = 0f;
                    m_planeDirty = true;
                    m_hasPouredSinceFilled = true;
                    UpdateRendererVisibility();
                }
            }
            else
            {
                m_pbdPourEmitter?.CancelPendingPour();
            }

            // uniforms do Liquid.shader (Triple Axis)
            Vector4 planeVector = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
            m_mat.SetVector("_Plane", planeVector);
            m_mat.SetVector("_SurfacePlane", planeVector);
            m_mat.SetVector("_PlanePos", new Vector3(0f, frac, 0f));
            m_mat.SetFloat("_Volume01", CalibratedHeight01);
            m_mat.SetFloat("_BoundsL", b.min.y);
            m_mat.SetFloat("_BoundsH", b.max.y);
            m_mat.SetFloat("_BoundsX", b.size.x);
            m_mat.SetFloat("_BoundsZ", b.size.z);
            m_mat.SetFloat("_WavesMult", m_wavesMult);
            m_mat.SetFloat("_MeshScale", Mathf.Max(b.size.x, b.size.z));
            m_mat.SetFloat("_Foam", m_foam);

            float viscosity01 = Mathf.Clamp01(Mathf.Log10(Mathf.Max(1f, viscosity)) / 3.3f);

            // Suavizar a direcao impede a funcao inteira de onda de saltar
            // quando a aceleracao muda de sinal entre dois frames.
            Vector2 desiredDirection = new Vector2(m_accel.x, m_accel.z);
            if (desiredDirection.sqrMagnitude < 0.02f)
                desiredDirection = new Vector2(m_velocity.x, m_velocity.z);
            if (desiredDirection.sqrMagnitude < 0.002f) desiredDirection = m_tiltVel;
            if (desiredDirection.sqrMagnitude > 1e-6f)
            {
                desiredDirection.Normalize();
                if (Vector2.Dot(desiredDirection, m_waveDirection) < 0f)
                    desiredDirection = -desiredDirection;
                float directionBlend = 1f - Mathf.Exp(-dt * Mathf.Lerp(9f, 2f, viscosity01));
                m_waveDirection = Vector2.Lerp(m_waveDirection, desiredDirection, directionBlend).normalized;
            }

            float horizontalAcceleration = new Vector2(m_accel.x, m_accel.z).magnitude / G;
            float horizontalSpeed = new Vector2(m_velocity.x, m_velocity.z).magnitude;
            float excitation = horizontalAcceleration * 0.72f
                + horizontalSpeed * 0.12f
                + m_angVel.magnitude * 0.2f
                + m_tiltVel.magnitude * 0.45f
                + (m_wavesMult - 1f) * 0.14f;
            float targetWave = Mathf.Clamp01(excitation * motionSensitivity / viscousResponse);
            // Ebulição reutiliza a mesma onda criada por movimento rápido. Assim
            // o material frio continua calmo e o quente parece estar agitado.
            float boilingMotion = Mathf.SmoothStep(0f, 1f, BoilingIntensity01);
            targetWave = Mathf.Max(targetWave, boilingMotion * 0.85f);
            float waveRate = targetWave > m_waveAmplitude ? waveAttack : waveDecay / Mathf.Sqrt(viscousResponse);
            m_waveAmplitude = Mathf.Lerp(m_waveAmplitude, targetWave, 1f - Mathf.Exp(-dt * waveRate));

            float verticalExcitation = Mathf.Abs(m_accel.y) / G
                + Mathf.Abs(m_velocity.y) * 0.1f
                + Mathf.Abs(m_bobVel) * 24f;
            float targetVerticalWave = Mathf.Clamp01(verticalExcitation * verticalWaveResponse / viscousResponse);
            targetVerticalWave = Mathf.Max(targetVerticalWave, boilingMotion * 0.70f);
            float verticalRate = targetVerticalWave > m_verticalWaveAmplitude ? waveAttack * 1.2f : waveDecay * 0.8f;
            m_verticalWaveAmplitude = Mathf.Lerp(
                m_verticalWaveAmplitude,
                targetVerticalWave,
                1f - Mathf.Exp(-dt * verticalRate));

            // Fase acumulada permanece continua quando viscosidade e energia mudam.
            float phaseSpeed = Mathf.Lerp(5.5f, 0.55f, viscosity01)
                * (1f + m_waveAmplitude * 0.7f)
                * Mathf.Lerp(1f, 2.4f, boilingMotion);
            m_wavePhase = Mathf.Repeat(m_wavePhase + dt * phaseSpeed, Mathf.PI * 200f);
            m_mat.SetFloat("_WaveAmplitude", m_waveAmplitude);
            m_mat.SetFloat("_VerticalWaveAmplitude", m_verticalWaveAmplitude);
            m_mat.SetFloat("_WavePhase", m_wavePhase);
            m_mat.SetVector("_WaveDirection", new Vector4(m_waveDirection.x, m_waveDirection.y, 0f, 0f));
            m_mat.SetFloat("_Viscosity01", viscosity01);

            // Atualiza a superficie livre usada pelas ondas e pela ebulicao.
            m_surfaceMesh?.Rebuild(cavityMesh, transform, plane, m_config, true);
            Vector3 waveCenter = m_surfaceMesh != null && m_surfaceMesh.SurfaceInnerRadiusWorld > 1e-4f
                ? m_surfaceMesh.SurfaceCenterWorld
                : planePoint;
            float waveInnerRadius = m_surfaceMesh != null && m_surfaceMesh.SurfaceInnerRadiusWorld > 1e-4f
                ? m_surfaceMesh.SurfaceInnerRadiusWorld
                : Mathf.Max(0.001f, Mathf.Min(b.size.x, b.size.z) * 0.35f);
            m_mat.SetVector("_WaveOrigin", new Vector4(
                waveCenter.x,
                waveCenter.y,
                waveCenter.z,
                waveInnerRadius));
            UpdateLayerVisuals(l2w, b, surfNormal, waveCenter, waveInnerRadius);

        }

        void UpdateLayerVisuals(Matrix4x4 localToWorld, Bounds bounds,
            Vector3 surfaceNormal, Vector3 waveCenter, float waveInnerRadius)
        {
            m_renderOrder.Clear();
            for (int i = 0; i < m_composition.Phases.Count; i++)
                if (m_composition.Phases[i].volumeML > EmptyVolumeEpsilonML)
                    m_renderOrder.Add(m_composition.Phases[i]);
            m_renderOrder.Sort((a, b) => a.displayBottomML.CompareTo(b.displayBottomML));
            EnsureLayerVisualCount(m_renderOrder.Count);
            LiquidPhase freeSurface = m_composition.TopPhase;

            for (int i = 0; i < m_layerVisuals.Count; i++)
            {
                LayerVisual visual = m_layerVisuals[i];
                bool active = i < m_renderOrder.Count;
                visual.renderer.enabled = active;
                if (!active) continue;

                LiquidPhase phase = m_renderOrder[i];
                if (visual.appliedConfig != phase.appearance)
                {
                    ApplyConfigToMaterial(visual.material, phase.appearance);
                    visual.appliedConfig = phase.appearance;
                }

                float bottomML = Mathf.Clamp(phase.displayBottomML, 0f, capacityML);
                float topML = Mathf.Clamp(bottomML + phase.volumeML, 0f, capacityML);
                float bottomY = m_baker.PlaneYForVolume(localToWorld, VolumeFractionForML(bottomML));
                float topY = m_baker.PlaneYForVolume(localToWorld, VolumeFractionForML(topML));
                bool isFreeSurface = phase == freeSurface;
                Plane topPlane = new Plane(surfaceNormal,
                    new Vector3(bounds.center.x, topY + (isFreeSurface ? m_bob : 0f), bounds.center.z));
                Plane bottomPlane = new Plane(surfaceNormal,
                    new Vector3(bounds.center.x, bottomY, bounds.center.z));
                Material material = visual.material;
                material.renderQueue = 2990 + i;
                material.SetVector("_SurfacePlane", new Vector4(
                    topPlane.normal.x, topPlane.normal.y, topPlane.normal.z, topPlane.distance));
                material.SetVector("_LayerBottomPlane", new Vector4(
                    bottomPlane.normal.x, bottomPlane.normal.y, bottomPlane.normal.z, bottomPlane.distance));
                material.SetFloat("_HasLayerBottom", bottomML > EmptyVolumeEpsilonML ? 1f : 0f);
                material.SetFloat("_LayerWaveStrength", isFreeSurface ? 1f : 0f);
                material.SetFloat("_Volume01", Mathf.Clamp01(phase.volumeML / capacityML));
                material.SetFloat("_WaveAmplitude", isFreeSurface ? m_waveAmplitude : 0f);
                material.SetFloat("_VerticalWaveAmplitude", isFreeSurface ? m_verticalWaveAmplitude : 0f);
                material.SetFloat("_WavePhase", m_wavePhase);
                material.SetVector("_WaveDirection", new Vector4(
                    m_waveDirection.x, m_waveDirection.y, 0f, 0f));
                material.SetVector("_WaveOrigin", new Vector4(
                    waveCenter.x, waveCenter.y, waveCenter.z, waveInnerRadius));
                material.SetFloat("_Foam", isFreeSurface ? m_foam : 0f);
                material.SetFloat("_Viscosity01", Mathf.Clamp01(
                    Mathf.Log10(Mathf.Max(1f, phase.appearance.viscosity)) / 3.3f));
                material.SetFloat("_BoundsL", bounds.min.y);
                material.SetFloat("_BoundsH", bounds.max.y);
                material.SetFloat("_BoundsX", bounds.size.x);
                material.SetFloat("_BoundsZ", bounds.size.z);
                material.SetFloat("_MeshScale", Mathf.Max(bounds.size.x, bounds.size.z));
            }
        }

        void EnsureLayerVisualCount(int count)
        {
            if (count > 0 && m_layerVisuals.Count == 0)
                m_layerVisuals.Add(new LayerVisual
                {
                    renderer = m_renderer,
                    filter = GetComponent<MeshFilter>(),
                    material = m_mat
                });

            while (m_layerVisuals.Count < count)
            {
                GameObject layer = new GameObject("Liquid Phase " + m_layerVisuals.Count);
                layer.transform.SetParent(transform, false);
                layer.layer = gameObject.layer;
                MeshFilter filter = layer.AddComponent<MeshFilter>();
                filter.sharedMesh = m_surfaceMesh != null && m_surfaceMesh.BodyMesh != null
                    ? m_surfaceMesh.BodyMesh
                    : cavityMesh;
                MeshRenderer renderer = layer.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = true;
                Material material = new Material(m_mat)
                {
                    name = "Liquid Phase Runtime",
                    hideFlags = HideFlags.DontSave
                };
                renderer.sharedMaterial = material;
                m_layerVisuals.Add(new LayerVisual
                {
                    gameObject = layer,
                    renderer = renderer,
                    filter = filter,
                    material = material
                });
            }
        }

        static void ApplyConfigToMaterial(Material material, LiquidConfig config)
        {
            if (material == null || config == null) return;
            Set(material, "_SurfaceColor", config.surfaceColor);
            Set(material, "_BodyColor", config.bodyColor);
            Set(material, "_DeepColor", config.deepColor);
            Set(material, "_AbsorptionColor", config.absorptionColor);
            Set(material, "_FoamColor", config.foamColor);
            Set(material, "_EmissionColor", config.emissionColor);
            Set(material, "_AbsorptionDensity", config.absorptionDensity);
            Set(material, "_Transparency", config.transparency);
            Set(material, "_TransmissionStrength", config.lightTransmission);
            Set(material, "_SurfaceOpacity", config.surfaceOpacity);
            Set(material, "_Turbidity", config.turbidity);
            Set(material, "_Smoothness", config.smoothness);
            Set(material, "_IOR", config.indexOfRefraction);
            Set(material, "_EmissionStrength", config.emissionStrength);
            Set(material, "_MaximumWaveHeight", config.maximumWaveHeight);
            Set(material, "_MaximumVerticalWaveHeight", config.maximumVerticalWaveHeight);
            Set(material, "_WaveFrequency", config.waveFrequency);
            Set(material, "_WaveIrregularity", config.waveIrregularity);
            Set(material, "_WaveDetail", config.waveDetail);
            Set(material, "_MicroWaveAmplitude", config.microWaveAmplitude);
            Set(material, "_MicroWaveSpeed", config.microWaveSpeed);
        }

        static void Set(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }

        static void Set(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        void FixedUpdate()
        {
            float fdt = Mathf.Max(Time.fixedDeltaTime, 1e-4f);
            Vector3 vel = (transform.position - m_prevPos) / fdt;
            m_velocity = Vector3.Lerp(m_velocity, vel, 1f - Mathf.Exp(-fdt * 18f));
            Vector3 measuredAcceleration = (vel - m_prevVel) / fdt;
            measuredAcceleration = Vector3.ClampMagnitude(measuredAcceleration, 80f);
            m_accel = Vector3.Lerp(m_accel, measuredAcceleration, 1f - Mathf.Exp(-fdt * 22f));
            m_prevVel = vel;
            m_prevPos = transform.position;

            Quaternion dq = transform.rotation * Quaternion.Inverse(m_prevRot);
            m_prevRot = transform.rotation;
            dq.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            m_angVel = (Mathf.Deg2Rad * ang / fdt) * axis.normalized;
        }

        void OnDestroy()
        {
            m_surfaceMesh?.Dispose();
            m_surfaceMesh = null;
            for (int i = 1; i < m_layerVisuals.Count; i++)
            {
                if (m_layerVisuals[i].material != null) Destroy(m_layerVisuals[i].material);
                if (m_layerVisuals[i].gameObject != null) Destroy(m_layerVisuals[i].gameObject);
            }
            m_layerVisuals.Clear();
        }

        // ---- API (painel / teste) ----
        public void AddVolumeML(float ml) { SetVolumeML(currentVolumeML + ml); }
        public float ReceiveLiquid(LiquidConfig liquid, float milliliters)
        {
            EnsureInit();
            float accepted = m_composition.Receive(liquid, milliliters, capacityML);
            currentVolumeML = m_composition.TotalVolumeML;
            if (accepted > 0f)
            {
                m_hasPouredSinceFilled = false;
                m_planeDirty = true;
                UpdateRendererVisibility();
            }
            return accepted;
        }
        public void SetBoilingIntensity(float intensity01)
        {
            BoilingIntensity01 = Mathf.Clamp01(intensity01);
        }

        public void SetVolumeML(float ml)
        {
            float previous = currentVolumeML;
            EnsureInit();
            m_composition.SetTotal(ml, capacityML, m_config);
            currentVolumeML = m_composition.TotalVolumeML;
            if (currentVolumeML <= EmptyVolumeEpsilonML) currentVolumeML = 0f;
            if (currentVolumeML <= EmptyVolumeEpsilonML) BoilingIntensity01 = 0f;
            if (currentVolumeML > previous + EmptyVolumeEpsilonML)
            {
                m_hasPouredSinceFilled = false;
                m_pbdPourEmitter?.CancelPendingPour();
            }
            m_planeDirty = true;
            UpdateRendererVisibility();
        }

        public void SetLiquidConfig(LiquidConfigAsset config)
        {
            liquidConfigFile = config;
            m_config = null;
            m_loadedConfigFile = null;
            m_configAppliedToMaterial = false;
            m_composition = null;
            EnsureInit();
        }

        public Vector3 BubbleOriginWorld
        {
            get
            {
                EnsureInit();
                float offset = Config != null ? Config.bubbleBottomOffset : 0.045f;
                return m_baker != null && m_baker.IsBaked
                    ? m_baker.BottomWorldPoint(transform.localToWorldMatrix, offset)
                    : transform.position;
            }
        }

        public float DistanceToSurfaceAlong(Vector3 originWorld, Vector3 directionWorld)
        {
            float denominator = Vector3.Dot(SurfacePlane.normal, directionWorld.normalized);
            if (Mathf.Abs(denominator) < 1e-5f) return 0f;
            return Mathf.Max(0f, -SurfacePlane.GetDistanceToPoint(originWorld) / denominator);
        }

        public bool ConstrainBubble(
            ref Vector3 positionWorld,
            ref Vector3 velocityWorld,
            float radiusWorld)
        {
            EnsureInit();
            if (m_baker == null || !m_baker.IsBaked) return true;
            LiquidConfig config = Config;
            return m_baker.ConstrainBubble(
                transform.localToWorldMatrix,
                transform.worldToLocalMatrix,
                SurfacePlane,
                ref positionWorld,
                ref velocityWorld,
                radiusWorld,
                config != null ? config.bubbleWallInset : 0.88f,
                config != null ? config.bubbleCollisionBounciness : 0.18f);
        }

        [ContextMenu("Resetar calibracao de graduacao")]
        public void ResetGraduationCalibration()
        {
            graduationCalibration = new List<SpillGraduationPoint>
            {
                new SpillGraduationPoint(0f, 0f),
                new SpillGraduationPoint(capacityML, 1f)
            };
            m_planeDirty = true;
        }

    }
}
