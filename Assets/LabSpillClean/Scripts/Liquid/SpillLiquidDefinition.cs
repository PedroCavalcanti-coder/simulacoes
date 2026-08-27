// Copia de Assets/LiquidFX (pasta de exemplo, somente-leitura). Tipos e namespace
// renomeados para conviver com o original no mesmo projeto. Ver PLANO-REFORMA.md, tarefa 2.0.
using LiquidVolumeFX;
using UnityEngine;

namespace LabSpill
{
    /// <summary>
    /// One named liquid, authored once and reused across every flask - the equivalent of an item
    /// asset for a liquid instead of hand-tuning ten fields per layer on every LiquidVolume in the
    /// scene. Density is deliberately not here: it belongs to <see cref="SpillLiquidCategory"/>, since
    /// LiquidVolumePro only merges layers whose density is exactly equal, and category is what
    /// decides whether two liquids should merge.
    /// </summary>
    [CreateAssetMenu(menuName = "Lab Spill/Liquid", fileName = "Liq_")]
    public sealed class SpillLiquidDefinition : ScriptableObject
    {
        [Header("Identity")]
        [SerializeField] string displayName = "New Liquid";

        [Tooltip("Mixing family. Two liquids of the same category blend into one layer; " +
            "different categories stratify by density.")]
        [SerializeField] SpillLiquidCategory category;

        [Tooltip("Real density in kg/L. Decides stacking order only - lighter floats. Mixing is " +
            "decided by category, so two liquids can share a family and still have their own " +
            "densities (ethanol 0.79 mixes with water 1.00 yet floats on oil 0.92).")]
        [SerializeField, Min(0.01f)] float densityKgPerLiter = 1f;

        [Header("Appearance (maps 1:1 to LiquidVolume.LiquidLayer)")]
        [Tooltip("Alpha is volumetric absorption strength, not surface opacity.")]
        [SerializeField] Color color = new Color(0.3f, 0.7f, 1f, 0.35f);
        [SerializeField] Color murkColor = Color.black;
        [SerializeField, Range(0f, 1f)] float murkiness = 0.4f;
        [SerializeField, Range(0.001f, 0.48f)] float scale = 0.3f;
        [SerializeField, Range(0f, 1f)] float viscosity = 1f;
        [SerializeField, Range(0f, 1f)] float bubblesOpacity = 0.5f;
        [SerializeField, Range(0.001f, 10f)] float adjustmentSpeed = 1f;

        // Campos abaixo nao existem no LiquidFX original: o LabSpillClean tambem precisa
        // descrever o comportamento termico e o jato de particulas do liquido, que o
        // LiquidVolume.LiquidLayer nao tem onde guardar. Ver PLANO-REFORMA.md, tarefa 2.2.

        [Header("Termico")]
        [Tooltip("Temperatura de ebulicao em graus Celsius.")]
        [SerializeField] float boilingPointC = 100f;
        [Tooltip("Cor do vapor emitido acima da boca do frasco.")]
        [SerializeField] Color vaporColor = new Color(0.94f, 0.98f, 1f, 0.18f);
        [Tooltip("Particulas de vapor por segundo com a ebulicao no maximo.")]
        [SerializeField, Min(0f)] float steamRateAtMaximum = 5f;
        [Tooltip("Intensidade de ebulicao (0..1) a partir da qual o vapor comeca a aparecer.")]
        [SerializeField, Range(0f, 1f)] float steamStartIntensity = 0.65f;

        [Header("Jato (SSF)")]
        [Tooltip("Cor das particulas que saem do bico. Alpha e ignorado.")]
        [SerializeField] Color streamColor = new Color(0.16f, 0.68f, 0.69f, 1f);
        [Tooltip("Viscosidade real em mPa.s (agua = 1, oleo ~ 60). Usada pela fisica do jato, " +
            "nao pelo render: o campo 'viscosity' acima e o 0..1 estetico do LiquidVolumePro.")]
        [SerializeField, Min(0.2f)] float physicalViscosity = 1f;

        public string DisplayName => displayName;
        public SpillLiquidCategory Category => category;
        public Color Color => color;

        public float BoilingPointC => boilingPointC;
        public Color VaporColor => vaporColor;
        public float SteamRateAtMaximum => steamRateAtMaximum;
        public float SteamStartIntensity => steamStartIntensity;
        public Color StreamColor => streamColor;
        public float PhysicalViscosity => Mathf.Max(0.2f, physicalViscosity);

        /// <summary>Stacking density in kg/L. Lighter liquids float.</summary>
        public float Density => Mathf.Max(0.01f, densityKgPerLiter);

        /// <summary>
        /// Writes this liquid's appearance into a LiquidVolumePro layer slot. Deliberately does not
        /// touch <c>amount</c> - volume is the caller's responsibility, this only sets what the
        /// liquid looks like.
        /// </summary>
        public void ApplyTo(ref LiquidVolume.LiquidLayer layer)
        {
            layer.density = Density;
            // Always false. LiquidVolumePro's own grouping merges layers whose density matches
            // exactly, which would tie mixing to stacking; SpillFlaskVolume merges by category
            // instead and hands LiquidVolumePro one already-blended layer per group, so its
            // grouping pass must stay switched off. See SpillLiquidCategory.
            layer.miscible = false;
            layer.color = color;
            layer.murkColor = murkColor;
            layer.murkiness = murkiness;
            layer.scale = scale;
            layer.viscosity = viscosity;
            layer.bubblesOpacity = bubblesOpacity;
            layer.adjustmentSpeed = adjustmentSpeed;
            layer.layerName = displayName;
        }

        /// <summary>
        /// Stirs this liquid into a layer that already holds <paramref name="existingML"/> of
        /// something else in the same category, as if <paramref name="incomingML"/> of it had been
        /// poured in. Every appearance field and the density blend by volume, which is the job
        /// LiquidVolumePro does for itself when it groups miscible layers - it cannot do it here
        /// because this project keeps its layers non-miscible so that density stays free to mean
        /// stacking order alone. See <see cref="SpillLiquidCategory"/>.
        /// </summary>
        public void BlendInto(ref LiquidVolume.LiquidLayer layer, float existingML, float incomingML)
        {
            float total = existingML + incomingML;
            if (total <= 0f)
                return;

            float w = Mathf.Clamp01(incomingML / total);
            layer.density = Mathf.Lerp(layer.density, Density, w);
            layer.color = Color.Lerp(layer.color, color, w);
            layer.murkColor = Color.Lerp(layer.murkColor, murkColor, w);
            layer.murkiness = Mathf.Lerp(layer.murkiness, murkiness, w);
            layer.scale = Mathf.Lerp(layer.scale, scale, w);
            layer.viscosity = Mathf.Lerp(layer.viscosity, viscosity, w);
            layer.bubblesOpacity = Mathf.Lerp(layer.bubblesOpacity, bubblesOpacity, w);
        }
    }
}
