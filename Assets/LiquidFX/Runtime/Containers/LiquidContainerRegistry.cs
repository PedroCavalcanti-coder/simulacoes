using System.Collections.Generic;
using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// Every flask and basin registers itself here while enabled, so a stream can find out what
    /// it is landing in without a physics query. The list is tiny (a bench holds a handful of
    /// pieces of glassware) and the lookup runs at most once per frame per stream.
    /// </summary>
    public static class LiquidContainerRegistry
    {
        static readonly List<ILiquidContainer> containers = new List<ILiquidContainer>(16);

        public static IReadOnlyList<ILiquidContainer> All => containers;

        public static void Register(ILiquidContainer container)
        {
            if (container == null || containers.Contains(container))
                return;

            containers.Add(container);
        }

        public static void Unregister(ILiquidContainer container)
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
        public static ILiquidContainer FindReceiverUnder(Vector3 worldPoint, float fromY, ILiquidContainer ignore = null)
        {
            ILiquidContainer best = null;
            float bestY = float.MinValue;

            for (int i = 0; i < containers.Count; i++)
            {
                ILiquidContainer candidate = containers[i];
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
