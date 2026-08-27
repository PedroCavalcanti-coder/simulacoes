using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// A family of liquids that mix with each other and stack in a fixed order against every
    /// other family. LiquidVolumePro only merges two layers when their density is bit-for-bit
    /// equal (see LiquidVolume.UpdateLayersNow, the "density == groupDensity" check), so density
    /// cannot live on the individual liquid without breaking that comparison the moment two
    /// liquids of the same family are poured together at slightly different values. It has to
    /// live here, once per category, shared by every liquid that belongs to it.
    /// </summary>
    [CreateAssetMenu(menuName = "LiquidFX/Liquid Category", fileName = "Cat_")]
    public sealed class LiquidCategory : ScriptableObject
    {
        [SerializeField] string displayName = "New Category";

        [Tooltip("Stacking density. Lower floats on top. Also the mixing key: two liquids only " +
            "mix if their categories share this exact value, so no two categories may use the " +
            "same number.")]
        [SerializeField, Min(0.001f)] float stackDensity = 1f;

        [Tooltip("Inspector/gizmo colour only. Does not affect rendering.")]
        [SerializeField] Color editorTint = Color.cyan;

        public string DisplayName => displayName;
        public float StackDensity => Mathf.Max(0.001f, stackDensity);
        public Color EditorTint => editorTint;
    }
}
