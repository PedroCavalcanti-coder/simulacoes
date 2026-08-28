using LabSpill;
using LiquidVolumeFX;
using UnityEngine;

namespace LabLiquidVR
{
    /// <summary>
    /// Converte volume que sai de um frasco em particulas no mundo.
    ///
    /// Atende os dois frascos enquanto a cena esta sendo migrada. Com
    /// <see cref="flask"/> preenchido usa o caminho novo, dirigido por este proprio
    /// Update a partir do bico geometrico do LiquidVolumePro; sem ele continua sendo
    /// chamado pelo <see cref="SpillLiquidContainer"/> antigo. A metade antiga sai
    /// quando o ultimo frasco for migrado.
    /// </summary>
    [DefaultExecutionOrder(-100), DisallowMultipleComponent]
    public sealed class SpillPourEmitter : MonoBehaviour
    {
        [Tooltip("Frasco novo (LiquidVolumePro). Preenchido = caminho novo.")]
        public SpillFlaskVolume flask;

        [Tooltip("Frasco antigo. Usado apenas enquanto este frasco nao foi migrado.")]
        public SpillLiquidContainer source;

        public SpillFluidWorld world;
        [Tooltip("Template SSF. Cor e propriedades sao sobrescritas pelo liquido.")]
        public Material surfaceMaterialTemplate;

        float m_pendingMl;
        int m_liquidIndex = -1;
        string m_registeredKey;

        public int LiquidIndex => m_liquidIndex;
        public float PendingMilliliters => m_pendingMl;

        void Awake() => EnsureRegistered();
        void OnEnable() => EnsureRegistered();
        void OnDisable() => CancelPendingPour();

        void EnsureReferences()
        {
            if (flask == null) flask = GetComponentInChildren<SpillFlaskVolume>(true);
            if (source == null && flask == null)
                source = GetComponentInChildren<SpillLiquidContainer>(true);
            if (world == null) world = FindAnyObjectByType<SpillFluidWorld>();
        }

        void EnsureRegistered()
        {
            EnsureReferences();
            if (flask != null) return;   // o caminho novo registra por definicao, ao derramar

            LiquidConfig config = source != null ? source.PourConfig : null;
            if (source == null || world == null || config == null) return;
            Color32 colour = config.bodyColor;
            string key = config.category + "." + colour.r + "." + colour.g + "." +
                colour.b + "." + Mathf.RoundToInt(config.densityKgPerLiter * 100f);
            if (m_liquidIndex >= 0 && m_registeredKey == key) return;
            m_liquidIndex = world.RegisterLiquid(key, config, surfaceMaterialTemplate);
            m_registeredKey = key;
        }

        void Update()
        {
            // O caminho antigo e dirigido pelo Update do proprio container, que chama
            // TryEmitPour. O novo nao tem quem o chame, entao se dirige aqui.
            if (flask != null) PourFromFlask();
        }

        /// <summary>
        /// Derrama pelo bico do LiquidVolumePro. Debita do frasco ANTES de emitir e
        /// devolve o que o pool nao aceitou: como o pool tem capacidade fixa, emitir
        /// primeiro e debitar o aceito depois faria mL sumir num derrame com a cena
        /// cheia de liquido.
        /// </summary>
        void PourFromFlask()
        {
            EnsureReferences();
            if (world == null) return;

            LiquidVolume volume = flask.Volume;
            if (volume == null) return;
            if (!volume.GetSpillPoint(out Vector3 spillPosition, out float _)) return;

            float rate = flask.EvaluateTiltFlowMLPerSecond();
            if (rate <= 0f)
            {
                m_pendingMl = 0f;
                return;
            }

            float mlPerParticle = world.Settings.millilitersPerParticle;
            m_pendingMl = Mathf.Min(flask.ContentsML, m_pendingMl + rate * Time.deltaTime);

            // A tolerancia e em mL, somada antes da divisao: um residuo de ponto
            // flutuante ficaria eternamente abaixo de uma particula inteira.
            int particles = Mathf.Min(
                Mathf.FloorToInt((m_pendingMl + 0.0001f) / mlPerParticle),
                world.Settings.maxParticlesPerFrame);
            if (particles <= 0) return;

            float removed = flask.RemoveTopML(particles * mlPerParticle,
                out SpillLiquidDefinition top);
            if (removed <= 0f || top == null)
            {
                m_pendingMl = 0f;
                return;
            }

            int index = world.RegisterLiquid(top, surfaceMaterialTemplate);
            if (index < 0)
            {
                flask.AddLayeredML(removed, top);
                return;
            }

            float head = Mathf.Max(0.001f, volume.liquidSurfaceYPosition - spillPosition.y);
            float speed = Mathf.Sqrt(2f * 9.81f * head) * 0.6f;

            // O liquido escorre para fora da borda, nao para cima: direcao horizontal
            // saindo do eixo do frasco, com queda somada por cima.
            Vector3 outward = spillPosition - flask.transform.position;
            outward.y = 0f;
            outward = outward.sqrMagnitude > 1e-6f
                ? outward.normalized
                : flask.transform.up;

            Rigidbody body = GetComponentInParent<Rigidbody>();
            Vector3 vessel = body != null ? body.GetPointVelocity(spillPosition) : Vector3.zero;
            Vector3 velocity = vessel + outward * speed + Vector3.down * (speed * 0.5f + 0.2f);

            // O jato nasce como um filete na borda, nao ocupando a boca inteira.
            float radius = Mathf.Max(world.Settings.PhysicalRadius, flask.PortRadius * 0.3f);

            int accepted = world.QueueJet(
                spillPosition, velocity,
                Mathf.RoundToInt(removed / mlPerParticle),
                index, radius, flask.transform.up);

            float emitted = accepted * mlPerParticle;
            if (emitted < removed - 0.0001f)
                flask.AddLayeredML(removed - emitted, top);   // pool cheio: devolve

            m_pendingMl = Mathf.Max(0f, m_pendingMl - emitted);
        }

        public float TryEmitPour(float requestedMl, Vector3 mouthCenter,
            Vector3 mouthNormal, Vector3 velocity)
        {
            EnsureRegistered();
            if (requestedMl <= 0f || m_liquidIndex < 0 || world == null) return 0f;

            float mlPerParticle = world.Settings.millilitersPerParticle;
            float availableInContainer = Mathf.Max(0f, source.currentVolumeML);
            float requested = Mathf.Clamp(requestedMl, 0f, availableInContainer);
            if (availableInContainer < mlPerParticle - 0.0001f)
            {
                m_pendingMl = 0f;
                return availableInContainer;
            }
            m_pendingMl = Mathf.Min(availableInContainer, m_pendingMl + requested);
            int particles = Mathf.Min(
                // A tolerancia e medida em mL. Somar depois da divisao fazia
                // Um residuo de ponto flutuante ficar eternamente abaixo de uma particula inteira.
                Mathf.FloorToInt((m_pendingMl + 0.0001f) / mlPerParticle),
                world.Settings.maxParticlesPerFrame);

            // Debita o residuo final sem criar uma particula parcial. Assim toda
            // particula existente representa sempre exatamente o mesmo volume.
            if (particles <= 0)
            {
                return 0f;
            }

            float physicalRadius = world.Settings.PhysicalRadius;
            float radius = Mathf.Max(physicalRadius, source.SpoutRadiusWorld);
            int accepted = world.QueueJet(
                mouthCenter + mouthNormal.normalized * physicalRadius * 2f,
                velocity,
                particles,
                m_liquidIndex,
                radius,
                mouthNormal);

            float emitted = accepted * mlPerParticle;
            float debited = Mathf.Min(availableInContainer, emitted);
            m_pendingMl = Mathf.Max(0f, m_pendingMl - debited);
            return debited;
        }

        public void CancelPendingPour() => m_pendingMl = 0f;
    }
}
