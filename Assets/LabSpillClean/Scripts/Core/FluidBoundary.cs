using System;
using UnityEngine;

namespace PBDFluid
{

    public partial class FluidBoundary : IDisposable
    {
        private const int THREADS = 128;

        public int NumParticles { get; private set; }

        public Bounds Bounds;

        public float ParticleRadius { get; private set; }

        public float ParticleDiameter { get { return ParticleRadius * 2.0f; } }

        public float Density { get; private set; }

        public ComputeBuffer Positions { get; private set; }

        public FluidBoundary(ParticleSource source, float radius, float density, Matrix4x4 RTS)
            : this(source, radius, density, RTS, true) { }

        // computePsi=false -> boundary INERTE (psi=0): nao contribui densidade SPH.
        // Usado quando o chao e' um COLLIDER (nao ghost particles). As particulas so
        // existem p/ satisfazer o minimo do bitonic sort. Pula CreateBoundryPsi (que
        // roda seu proprio bitonic e exigiria >= MIN_ELEMENTS boundary).
        public FluidBoundary(ParticleSource source, float radius, float density, Matrix4x4 RTS, bool computePsi)
        {
            NumParticles = source.NumParticles;
            ParticleRadius = radius;
            Density = density;

            CreateParticles(source, RTS);         // w (psi) fica 0
            if (computePsi) CreateBoundryPsi();    // senao: inerte
        }

        public void Dispose()
        {
            if(Positions != null)
            {
                Positions.Release();
                Positions = null;
            }

        }

        private void CreateParticles(ParticleSource source, Matrix4x4 RTS)
        {
            Vector4[] positions = new Vector4[NumParticles];

            float inf = float.PositiveInfinity;
            Vector3 min = new Vector3(inf, inf, inf);
            Vector3 max = new Vector3(-inf, -inf, -inf);

            for (int i = 0; i < NumParticles; i++)
            {
                Vector4 pos = RTS * source.Positions[i];
                positions[i] = pos;

                if (pos.x < min.x) min.x = pos.x;
                if (pos.y < min.y) min.y = pos.y;
                if (pos.z < min.z) min.z = pos.z;

                if (pos.x > max.x) max.x = pos.x;
                if (pos.y > max.y) max.y = pos.y;
                if (pos.z > max.z) max.z = pos.z;
            }

            min.x -= ParticleRadius;
            min.y -= ParticleRadius;
            min.z -= ParticleRadius;

            max.x += ParticleRadius;
            max.y += ParticleRadius;
            max.z += ParticleRadius;

            Bounds = new Bounds();
            Bounds.SetMinMax(min, max);

            Positions = new ComputeBuffer(NumParticles, 4 * sizeof(float));
            Positions.SetData(positions);

        }

        private void CreateBoundryPsi()
        {

            float cellSize = ParticleRadius * 4.0f;
            SmoothingKernel K = new SmoothingKernel(cellSize);

            GridHash grid = new GridHash(Bounds, NumParticles, cellSize);
            grid.Process(Positions);

            ComputeShader shader = Resources.Load("FluidBoundary") as ComputeShader;

            int kernel = shader.FindKernel("ComputePsi");

            shader.SetFloat("Density", Density);
            shader.SetFloat("KernelRadiuse", K.Radius);
            shader.SetFloat("KernelRadius2", K.Radius2);
            shader.SetFloat("Poly6", K.POLY6);
            shader.SetFloat("Poly6Zero", K.Poly6(Vector3.zero));
            shader.SetInt("NumParticles", NumParticles);

            shader.SetFloat("HashScale", grid.InvCellSize);
            shader.SetVector("HashSize", grid.Bounds.size);
            shader.SetVector("HashTranslate", grid.Bounds.min);
            shader.SetBuffer(kernel, "IndexMap", grid.IndexMap);
            shader.SetBuffer(kernel, "Table", grid.Table);

            shader.SetBuffer(kernel, "Boundary", Positions);

            int groups = NumParticles / THREADS;
            if (NumParticles % THREADS != 0) groups++;

            //Fills the boundarys psi array so the fluid can
            //collide against it smoothly. The original computes
            //the phi for each boundary particle based on the
            //density of the boundary but I find the fluid 
            //leaks out so Im just using a const value.

            shader.Dispatch(kernel, groups, 1, 1);

            grid.Dispose();

        }

    }

}
