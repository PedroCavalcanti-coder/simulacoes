using LabSpill;
using UnityEngine;

namespace LabLiquidVR
{
    [DefaultExecutionOrder(-100), DisallowMultipleComponent]
    public sealed class SpillPourEmitter : MonoBehaviour
    {
        public SpillLiquidContainer source;
        public SpillFluidWorld world;
        [Tooltip("Template SSF. Cor e propriedades sao sobrescritas pelo JSON do liquido.")]
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
            if (source == null) source = GetComponentInChildren<SpillLiquidContainer>(true);
            if (world == null) world = FindAnyObjectByType<SpillFluidWorld>();
        }

        void EnsureRegistered()
        {
            EnsureReferences();
            LiquidConfig config = source != null ? source.PourConfig : null;
            if (source == null || world == null || config == null) return;
            Color32 colour = config.bodyColor;
            string key = config.category + "." + colour.r + "." + colour.g + "." +
                colour.b + "." + Mathf.RoundToInt(config.densityKgPerLiter * 100f);
            if (m_liquidIndex >= 0 && m_registeredKey == key) return;
            m_liquidIndex = world.RegisterLiquid(key, config, surfaceMaterialTemplate);
            m_registeredKey = key;
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
