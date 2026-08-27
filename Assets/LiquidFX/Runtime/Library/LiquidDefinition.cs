using LiquidVolumeFX;
using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// One named liquid, authored once and reused across every flask - the equivalent of an item
    /// asset for a liquid instead of hand-tuning ten fields per layer on every LiquidVolume in the
    /// scene. Density is deliberately not here: it belongs to <see cref="LiquidCategory"/>, since
    /// LiquidVolumePro only merges layers whose density is exactly equal, and category is what
    /// decides whether two liquids should merge.
    /// </summary>
    [CreateAssetMenu(menuName = "LiquidFX/Liquid", fileName = "Liq_")]
    public sealed class LiquidDefinition : ScriptableObject
    {
        [Header("Identity")]
        [SerializeField] string displayName = "New Liquid";
        [SerializeField] LiquidCategory category;

        [Header("Appearance (maps 1:1 to LiquidVolume.LiquidLayer)")]
        [Tooltip("Alpha is volumetric absorption strength, not surface opacity.")]
        [SerializeField] Color color = new Color(0.3f, 0.7f, 1f, 0.35f);
        [SerializeField] Color murkColor = Color.black;
        [SerializeField, Range(0f, 1f)] float murkiness = 0.4f;
        [SerializeField, Range(0.001f, 0.48f)] float scale = 0.3f;
        [SerializeField, Range(0f, 1f)] float viscosity = 1f;
        [SerializeField, Range(0f, 1f)] float bubblesOpacity = 0.5f;
        [SerializeField, Range(0.001f, 10f)] float adjustmentSpeed = 1f;

        public string DisplayName => displayName;
        public LiquidCategory Category => category;
        public Color Color => color;

        /// <summary>Stacking density, inherited from the category. 1 (water-like) if uncategorised.</summary>
        public float Density => category != null ? category.StackDensity : 1f;

        /// <summary>
        /// Writes this liquid's appearance into a LiquidVolumePro layer slot. Deliberately does not
        /// touch <c>amount</c> - volume is the caller's responsibility, this only sets what the
        /// liquid looks like.
        /// </summary>
        public void ApplyTo(ref LiquidVolume.LiquidLayer layer)
        {
            layer.density = Density;
            // Always true: miscibility is expressed entirely through density equality (see
            // LiquidCategory) rather than this flag, so two liquids of the same category always
            // merge and two liquids of different categories never can, regardless of this value.
            layer.miscible = true;
            layer.color = color;
            layer.murkColor = murkColor;
            layer.murkiness = murkiness;
            layer.scale = scale;
            layer.viscosity = viscosity;
            layer.bubblesOpacity = bubblesOpacity;
            layer.adjustmentSpeed = adjustmentSpeed;
            layer.layerName = displayName;
        }
    }
}
