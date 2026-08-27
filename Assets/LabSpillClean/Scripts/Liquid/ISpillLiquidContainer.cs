// Copia de Assets/LiquidFX (pasta de exemplo, somente-leitura). Tipos e namespace
// renomeados para conviver com o original no mesmo projeto. Ver PLANO-REFORMA.md, tarefa 2.0.
using UnityEngine;

namespace LabSpill
{
    /// <summary>
    /// Anything that can hold liquid measured in millilitres. Today that is only
    /// <see cref="SpillFlaskVolume"/>, a flask driven by LiquidVolumePro. The transfer system
    /// only ever talks to containers through this interface, so adding another kind of vessel
    /// later costs no change in the pouring code.
    /// </summary>
    public interface ISpillLiquidContainer
    {
        Transform Transform { get; }

        float CapacityML { get; }

        float ContentsML { get; }

        float FreeML { get; }

        Color LiquidColor { get; }

        /// <summary>World position of the centre of the liquid surface (or of the floor when empty).</summary>
        Vector3 SurfaceCentreWorld { get; }

        /// <summary>World height of the liquid surface. Rises as the container fills.</summary>
        float SurfaceWorldY { get; }

        /// <summary>True when the world point is above the opening, i.e. a stream aimed here lands inside.</summary>
        bool IsAbovePort(Vector3 worldPoint);

        /// <summary>Adds liquid. Returns how much was actually accepted; the remainder overflows.</summary>
        float AddML(float millilitres, Color color);

        /// <summary>Removes liquid. Returns how much was actually available.</summary>
        float RemoveML(float millilitres);
    }
}
