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
        [Tooltip("Escala dos buffers SSF. Subiu para 0.75 depois que os pipelines por " +
            "liquido viraram um so: parte do custo economizado foi gasta em borda.")]
        [Range(0.5f, 1f)] public float resolutionScale = 0.75f;
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
        [Tooltip("Emitir deixou de custar realocacao de buffer, entao o orcamento que " +
            "isso liberava cabe aqui.")]
        [Range(1, 3)] public int solverIterations = 2;
        [Range(1, 4)] public int constraintIterations = 3;
        [Range(1, 4)] public int maxPhysicsStepsPerFrame = 1;
        [Min(0f)] public float viscosity = 0.004f;
        [Min(0.1f)] public float massScale = 1f;
        [Min(0f)] public float restDamping = 2f;
        [Tooltip("Coesao entre vizinhos, em m/s2. E o que faz o jato sair como filete " +
            "continuo em vez de esferas soltas. Compare com a gravidade, 9.81: abaixo " +
            "de 1 o efeito nao aparece. 0 desliga.")]
        [Range(0f, 8f)] public float cohesion = 2f;

        [Header("Emissao e descarte")]
        [Range(1, 256)] public int maxParticlesPerFrame = 32;
        [Tooltip("Tempo de vida da particula. Vale em qualquer lugar: bancada, chao ou " +
            "no ar. Sorteado dentro da faixa para a poca nao sumir toda de uma vez.")]
        [Min(0f)] public float particleLifetimeMin = 8f;
        [Min(0f)] public float particleLifetimeMax = 20f;

        [Header("Colisao")]
        [Tooltip("Intervalo (s) entre varreduras dos colisores da cena.")]
        [Range(0.05f, 5f)] public float colliderRefreshInterval = 0.5f;
        [Tooltip("Teto de colisores enviados a GPU por vez.")]
        [Range(8, 256)] public int maxColliders = 64;

        void OnValidate()
        {
            millilitersPerParticle = Mathf.Max(0.001f, millilitersPerParticle);
            maxParticles = Mathf.Max(128, maxParticles);
            resolutionScale = Mathf.Clamp(resolutionScale, 0.5f, 1f);
            densityThreshold = Mathf.Clamp(densityThreshold, 0.005f, 0.5f);
            particleLifetimeMax = Mathf.Max(particleLifetimeMin, particleLifetimeMax);
        }
    }
}
