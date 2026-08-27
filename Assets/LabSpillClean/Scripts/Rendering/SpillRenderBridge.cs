using System.Collections.Generic;
using UnityEngine;

namespace LabSpill.Rendering
{
    /// <summary>
    /// Ponte entre a simulacao e a Renderer Feature. Publica os buffers; nunca opina
    /// sobre aparencia.
    ///
    /// Antes havia uma entrada por liquido, e cada uma disparava um pipeline SSF
    /// inteiro: dois draws instanciados, uma cadeia de blur, um passe de normal, duas
    /// RenderTextures persistentes e um shading de tela cheia. O custo era linear no
    /// numero de liquidos para desenhar o mesmo jato. Agora ha uma entrada so: o passe
    /// de profundidade grava tambem qual substancia venceu cada pixel, e o material
    /// escolhe a aparencia por esse indice. De quebra o resultado fica mais correto -
    /// dois liquidos sobrepostos deixam de aparecer um atraves do outro, porque quem
    /// esta na frente ganha o pixel.
    /// </summary>
    public static class SpillRenderBridge
    {
        /// <summary>
        /// Teto de substancias distintas numa cena. O material carrega a aparencia de
        /// todas em arrays deste tamanho.
        /// </summary>
        public const int MaxSubstances = 8;

        public sealed class Entry
        {
            public ComputeBuffer Positions;
            public ComputeBuffer SubstanceIds;

            /// <summary>Instancias a desenhar: a marca d'agua do pool, nao a capacidade.</summary>
            public int Count;
            public float Radius;
            public Bounds WorldBounds;

            public MeshRenderer SurfaceRenderer;
            public RenderTexture SurfaceDepth;
            public RenderTexture SurfaceNormal;

            /// <summary>Substancia vencedora por pixel, gravada junto da profundidade.</summary>
            public RenderTexture SurfaceSubstance;

            public MaterialPropertyBlock SurfaceProperties;
        }

        static readonly List<Entry> s_entries = new List<Entry>();
        public static List<Entry> Entries => s_entries;

        public static void Register(Entry entry)
        {
            if (entry != null && !s_entries.Contains(entry)) s_entries.Add(entry);
        }

        public static void Unregister(Entry entry)
        {
            if (entry != null) s_entries.Remove(entry);
        }
    }
}
