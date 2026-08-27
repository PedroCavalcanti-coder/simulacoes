using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// Anything that can hold liquid measured in millilitres: a flask driven by LiquidVolumePro,
    /// a sink basin driven by <see cref="LiquidSurface"/>, or a puddle on the floor.
    /// The transfer system only ever talks to containers through this interface, so a pour from
    /// a beaker into a sink is the same code path as a pour from a flask into a flask.
    /// </summary>
    public interface ILiquidContainer
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
