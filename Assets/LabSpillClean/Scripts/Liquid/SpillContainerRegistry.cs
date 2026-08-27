// Copia de Assets/LiquidFX (pasta de exemplo, somente-leitura). Tipos e namespace
// renomeados para conviver com o original no mesmo projeto. Ver PLANO-REFORMA.md, tarefa 2.0.
using System.Collections.Generic;
using UnityEngine;

namespace LabSpill
{
    /// <summary>
    /// Every flask and basin registers itself here while enabled, so a stream can find out what
    /// it is landing in without a physics query. The list is tiny (a bench holds a handful of
    /// pieces of glassware) and the lookup runs at most once per frame per stream.
    /// </summary>
    public static class SpillContainerRegistry
    {
        static readonly List<ISpillLiquidContainer> containers = new List<ISpillLiquidContainer>(16);

        public static IReadOnlyList<ISpillLiquidContainer> All => containers;

        public static void Register(ISpillLiquidContainer container)
        {
            if (container == null || containers.Contains(container))
                return;

            containers.Add(container);
        }

        public static void Unregister(ISpillLiquidContainer container)
        {
            if (container == null)
                return;

            containers.Remove(container);
        }

        /// <summary>
        /// Highest container whose opening sits under <paramref name="worldPoint"/> and below
        /// <paramref name="fromY"/>. Highest wins so a beaker standing inside a sink catches the
        /// stream before the sink does.
        /// </summary>
        public static ISpillLiquidContainer FindReceiverUnder(Vector3 worldPoint, float fromY, ISpillLiquidContainer ignore = null)
        {
            ISpillLiquidContainer best = null;
            float bestY = float.MinValue;

            for (int i = 0; i < containers.Count; i++)
            {
                ISpillLiquidContainer candidate = containers[i];
                if (candidate == null || ReferenceEquals(candidate, ignore))
                    continue;

                Transform candidateTransform = candidate.Transform;
                if (candidateTransform == null || !candidateTransform.gameObject.activeInHierarchy)
                    continue;

                if (!candidate.IsAbovePort(worldPoint))
                    continue;

                float y = candidate.SurfaceWorldY;
                if (y > fromY)
                    continue;

                if (y > bestY)
                {
                    bestY = y;
                    best = candidate;
                }
            }

            return best;
        }
    }
}
