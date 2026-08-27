using System.Collections.Generic;
using UnityEngine;

namespace PBDFluid
{
    // Fonte de particulas a partir de uma lista arbitraria de posicoes (mundo).
    // Usada pelo FluidLabManager p/ semear o liquido inicial dentro dos frascos.
    public class ParticlesFromList : ParticleSource
    {
        public ParticlesFromList(float spacing, IList<Vector3> pts) : base(spacing)
        {
            Positions = new List<Vector3>(pts);
        }
    }
}
