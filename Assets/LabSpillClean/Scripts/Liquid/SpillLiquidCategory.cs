// Copia de Assets/LiquidFX (pasta de exemplo, somente-leitura). Tipos e namespace
// renomeados para conviver com o original no mesmo projeto. Ver PLANO-REFORMA.md, tarefa 2.0.
using UnityEngine;

namespace LabSpill
{
    /// <summary>
    /// A family of liquids that mix with each other. Two liquids of the same category poured
    /// together become one blended layer; two liquids of different categories stratify, heavier
    /// at the bottom.
    ///
    /// The category deliberately carries no density, unlike the LiquidFX original it was copied
    /// from. LiquidVolumePro merges two layers only when <c>miscible</c> is set and their density
    /// is bit-for-bit equal, which forces density to double as the mixing key and makes those two
    /// properties impossible to set independently: water and ethanol have to mix (same family) but
    /// stack differently against oil (0.79 floats, 1.00 sinks). So every layer this project writes
    /// goes in with <c>miscible = false</c>, LiquidVolumePro never groups anything on its own, and
    /// <see cref="SpillFlaskVolume"/> does the merging by category itself. That leaves density free
    /// to mean only what it says, once per liquid - see <see cref="SpillLiquidDefinition"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "Lab Spill/Liquid Category", fileName = "Cat_")]
    public sealed class SpillLiquidCategory : ScriptableObject
    {
        [SerializeField] string displayName = "New Category";

        [Tooltip("Inspector/gizmo colour only. Does not affect rendering.")]
        [SerializeField] Color editorTint = Color.cyan;

        public string DisplayName => displayName;
        public Color EditorTint => editorTint;
    }
}
