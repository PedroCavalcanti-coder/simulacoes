using System.Collections.Generic;
using UnityEngine;

namespace LabSpill.Rendering
{
    public static class SpillRenderBridge
    {
        public sealed class Entry
        {
            public ComputeBuffer Positions;
            public ComputeBuffer SubstanceIds;
            public int SubstanceIndex;
            public bool FilterBySubstance = true;
            public int Count;
            public float Radius;
            public Mesh ParticleMesh;
            public ComputeBuffer Args;
            public Bounds WorldBounds;
            public MeshRenderer SurfaceRenderer;
            public RenderTexture SurfaceDepth;
            public RenderTexture SurfaceNormal;
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
