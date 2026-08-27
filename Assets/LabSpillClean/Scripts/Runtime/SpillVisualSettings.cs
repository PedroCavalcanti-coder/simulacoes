using UnityEngine;

namespace LabSpill
{
    /// <summary>
    /// Unica fonte de verdade do derramamento. Nenhum valor abaixo e'
    /// duplicado na Renderer Feature ou no componente da cena.
    /// </summary>
    [CreateAssetMenu(menuName = "Lab Spill/Settings", fileName = "SpillVisualSettings")]
    public sealed class SpillVisualSettings : ScriptableObject
    {
        [Header("Volume")]
        [Tooltip("Volume representado por cada particula. O raio fisico e derivado deste valor.")]
        [Min(0.001f)] public float millilitersPerParticle = 1f;
        [Min(128)] public int maxParticles = 6000;

        [Header("Tamanho")]
        [Tooltip("Multiplica somente o splat SSF. O raio fisico continua vindo dos mL por particula.")]
        [Range(1f, 4f)] public float visualRadiusScale = 3.5f;

        /// <summary>
        /// Raio de uma esfera com o volume configurado, assumindo a convencao
        /// Unity de uma unidade por metro. Assim 1 mL resulta em ~6,2 mm e a
        /// proporcao acompanha automaticamente o frasco em escala real.
        /// </summary>
        public float PhysicalRadius
        {
            get
            {
                float cubicMeters = Mathf.Max(0.001f, millilitersPerParticle) * 1e-6f;
                return Mathf.Pow(3f * cubicMeters / (4f * Mathf.PI), 1f / 3f);
            }
        }

        [Header("Superficie embaçada")]
        [Tooltip("Escala dos buffers SSF. 0.66 evita meia-resolucao serrilhada sem o custo de full HD.")]
        [Range(0.5f, 1f)] public float resolutionScale = 0.66f;
        [Range(0.25f, 4f)] public float blurRadius = 3.2f;
        [Range(1, 3)] public int blurIterations = 1;
        [Range(0.1f, 1.5f)] public float normalRadius = 1.5f;
        [Range(0.1f, 4f)] public float depthFalloff = 0.45f;
        [Range(0f, 1f)] public float surfaceTension = 0.55f;
        [Tooltip("Suavidade da borda no material SSF; nao altera o raio fisico.")]
        [Range(0.5f, 4f)] public float edgeSoftness = 2.2f;
        [Tooltip("Limiar da espessura acumulada, medido em diametros visuais.")]
        [Range(0.005f, 0.5f)] public float densityThreshold = 0.01f;

        [Header("PBD simplificado")]
        [Range(1, 3)] public int solverIterations = 1;
        [Range(1, 3)] public int constraintIterations = 1;
        [Range(1, 4)] public int maxPhysicsStepsPerFrame = 1;
        [Min(0f)] public float viscosity = 0.004f;
        [Min(0.1f)] public float massScale = 1f;
        [Min(0f)] public float restDamping = 2f;

        [Header("Emissao e descarte")]
        [Range(1, 256)] public int maxParticlesPerFrame = 32;
        [Range(0f, 0.1f)] public float emissionFlushInterval = 0.025f;
        [Min(0f)] public float benchLifetimeMin = 10f;
        [Min(0f)] public float benchLifetimeMax = 30f;

        void OnValidate()
        {
            millilitersPerParticle = Mathf.Max(0.001f, millilitersPerParticle);
            maxParticles = Mathf.Max(128, maxParticles);
            resolutionScale = Mathf.Clamp(resolutionScale, 0.5f, 1f);
            densityThreshold = Mathf.Clamp(densityThreshold, 0.005f, 0.5f);
            benchLifetimeMax = Mathf.Max(benchLifetimeMin, benchLifetimeMax);
        }
    }
}
