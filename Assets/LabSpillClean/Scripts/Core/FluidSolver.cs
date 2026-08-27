using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PBDFluid
{
    public partial class FluidSolver : IDisposable
    {
        // colisores por layer (C2) — primitivas analiticas, atualizadas por frame
        [StructLayout(LayoutKind.Sequential)]
        public struct ColliderGPU
        {
            public int type;               // 0 sphere, 1 box, 2 capsule, 3 plane
            public Vector3 center;         // mundo
            public Vector3 axisX, axisY, axisZ;
            public Vector3 halfExt;        // box: meias-ext; capsule: (_, meia-altura, _)
            public float radius;           // sphere/capsule
            public Vector3 velocity;       // vel. do colisor (mundo) — arrasta o fluido (atrito)
        }
        private ComputeBuffer m_colliderBuffer;
        private int m_colliderCount;

        // colisores de MORTE (kill-on-contact): tocar = particula some
        private ComputeBuffer m_killColliderBuffer;
        private int m_killColliderCount;

        // 0..1: atrito com o colisor. Colisor MOVENDO arrasta o fluido ate' a vel dele; fluido
        // deslizando num colisor PARADO freia aos poucos (desliza "levemente"). 0 = sem atrito.
        public float ColliderFriction = 0.4f;

        public Vector3 Graveyard = new Vector3(0, -500, 0);

        private const int THREADS = 128;
        private const int READ = 0;
        private const int WRITE = 1;

        public int Groups { get; private set; }

        public FluidBoundary Boundary { get; private set; }

        public FluidBody Body { get; private set; }

        public GridHash Hash { get; private set; }

        public int SolverIterations { get; set; }

        public int ConstraintIterations { get; set; }

        public Vector3 Gravity = new Vector3(0.0f, -9.81f, 0.0f);

        // --- estabilidade PBF (anti-"pipoca"), calibravel ao vivo ---
        public float CFL = 0.5f;              // desloc. max por substep = CFL * cellSize
        public float CorrectionFactor = 0.4f; // correcao max por iteracao = fator * raio
        public float Relaxation = 60f;        // epsilon CFM (era hardcoded 60 no shader)

        // --- AMORTECIMENTO ADAPTATIVO p/ REPOUSO (dissipacao viscosa) ---
        // Faz o liquido PARAR com o tempo e ficar em repouso ate' ser perturbado (Newton).
        // Forte devagar (assenta), fraco rapido (jato vivo). Ver shader UpdateVelocities.
        public float RestDamping = 2f;      // 1/s em repouso; preserva slosh sem jitter perpetuo
        public float MoveDamping = 0.2f;    // 1/s em movimento (baixo = derrame vivo)
        public float DampRefSpeed = 0.35f;  // m/s onde ja conta como "rapido"

        public SmoothingKernel Kernel { get; private set; }

        private ComputeShader m_shader;
        private readonly int m_predictPositionsKernel;
        private readonly int m_computeDensityKernel;
        private readonly int m_solveConstraintKernel;
        private readonly int m_solveCollidersKernel;
        private readonly int m_solveKillCollidersKernel;
        private readonly int m_updateVelocitiesKernel;
        private readonly int m_solveViscosityKernel;
        private readonly int m_updatePositionsKernel;

        public FluidSolver(FluidBody body, FluidBoundary boundary)
        {
            SolverIterations = 2;
            ConstraintIterations = 2;

            Body = body;
            Boundary = boundary;

            float cellSize = Body.ParticleRadius * 4.0f;
            int total = Body.NumParticles + Boundary.NumParticles;
            Hash = new GridHash(Boundary.Bounds, total, cellSize);
            Kernel = new SmoothingKernel(cellSize);

            int numParticles = Body.NumParticles;
            Groups = numParticles / THREADS;
            if (numParticles % THREADS != 0) Groups++;

            m_shader = Resources.Load("FluidSolver") as ComputeShader;
            m_predictPositionsKernel = m_shader.FindKernel("PredictPositions");
            m_computeDensityKernel = m_shader.FindKernel("ComputeDensity");
            m_solveConstraintKernel = m_shader.FindKernel("SolveConstraint");
            m_solveCollidersKernel = m_shader.FindKernel("SolveColliders");
            m_solveKillCollidersKernel = m_shader.FindKernel("SolveKillColliders");
            m_updateVelocitiesKernel = m_shader.FindKernel("UpdateVelocities");
            m_solveViscosityKernel = m_shader.FindKernel("SolveViscosity");
            m_updatePositionsKernel = m_shader.FindKernel("UpdatePositions");

        }

        public void Dispose()
        {
            Hash.Dispose();
            m_colliderBuffer?.Release(); m_colliderBuffer = null;
            m_killColliderBuffer?.Release(); m_killColliderBuffer = null;
        }

        public void StepPhysics(float dt)
        {

            if (dt <= 0.0) return;
            if (SolverIterations <= 0 || ConstraintIterations <= 0) return;

            dt /= SolverIterations;

            m_shader.SetInt("NumParticles", Body.NumParticles);
            m_shader.SetVector("Gravity", Gravity);
            m_shader.SetFloat("Dampning", Body.Dampning);
            m_shader.SetFloat("DeltaTime", dt);
            m_shader.SetFloat("Density", Body.Density);
            m_shader.SetFloat("Viscosity", Body.Viscosity);
            m_shader.SetFloat("ParticleMass", Body.ParticleMass);

            m_shader.SetFloat("KernelRadius", Kernel.Radius);
            m_shader.SetFloat("KernelRadius2", Kernel.Radius2);
            m_shader.SetFloat("Poly6Zero", Kernel.Poly6(Vector3.zero));
            m_shader.SetFloat("Poly6", Kernel.POLY6);
            m_shader.SetFloat("SpikyGrad", Kernel.SPIKY_GRAD);
            m_shader.SetFloat("ViscLap", Kernel.VISC_LAP);

            m_shader.SetFloat("HashScale", Hash.InvCellSize);
            m_shader.SetVector("HashSize", Hash.Bounds.size);
            m_shader.SetVector("HashTranslate", Hash.Bounds.min);

            m_shader.SetFloat("MaxSpeed", CFL * Kernel.Radius / Mathf.Max(dt, 1e-6f));
            m_shader.SetFloat("MaxCorrection", CorrectionFactor * Body.ParticleRadius);
            m_shader.SetFloat("Relaxation", Relaxation);

            // amortecimento adaptativo p/ repouso (Newton): dissipa a energia residual do solver
            m_shader.SetFloat("RestDamping", Mathf.Max(0f, RestDamping));
            m_shader.SetFloat("MoveDamping", Mathf.Max(0f, MoveDamping));
            m_shader.SetFloat("DampRefSpeed", Mathf.Max(1e-4f, DampRefSpeed));

            //Predicted and velocities use a double buffer as solver step
            //needs to read from many locations of buffer and write the result
            //in same pass. Could be removed if needed as long as buffer writes 
            //are atomic. Not sure if they are.

            for (int i = 0; i < SolverIterations; i++)
            {
                PredictPositions(dt);

                Hash.Process(Body.Predicted[READ], Boundary.Positions);

                ConstrainPositions();

                SolveColliders();

                UpdateVelocities(dt);

                SolveViscosity();

                UpdatePositions();
            }

            SolveKillColliders();   // tocar objeto de morte -> some

        }

        private void PredictPositions(float dt)
        {
            int kernel = m_predictPositionsKernel;

            m_shader.SetBuffer(kernel, "Positions", Body.Positions);
            m_shader.SetBuffer(kernel, "PredictedWRITE", Body.Predicted[WRITE]);
            m_shader.SetBuffer(kernel, "VelocitiesREAD", Body.Velocities[READ]);
            m_shader.SetBuffer(kernel, "VelocitiesWRITE", Body.Velocities[WRITE]);
            m_shader.SetBuffer(kernel, "States", Body.States);

            m_shader.Dispatch(kernel, Groups, 1, 1);

            Swap(Body.Predicted);
            Swap(Body.Velocities);
        }

        public void ConstrainPositions()
        {

            int computeKernel = m_computeDensityKernel;
            int solveKernel = m_solveConstraintKernel;

            m_shader.SetBuffer(computeKernel, "Densities", Body.Densities);
            m_shader.SetBuffer(computeKernel, "Pressures", Body.Pressures);
            m_shader.SetBuffer(computeKernel, "Boundary", Boundary.Positions);
            m_shader.SetBuffer(computeKernel, "IndexMap", Hash.IndexMap);
            m_shader.SetBuffer(computeKernel, "Table", Hash.Table);

            m_shader.SetBuffer(solveKernel, "Pressures", Body.Pressures);
            m_shader.SetBuffer(solveKernel, "Boundary", Boundary.Positions);
            m_shader.SetBuffer(solveKernel, "IndexMap", Hash.IndexMap);
            m_shader.SetBuffer(solveKernel, "Table", Hash.Table);
            m_shader.SetBuffer(solveKernel, "States", Body.States);

            for (int i = 0; i < ConstraintIterations; i++)
            {
                m_shader.SetBuffer(computeKernel, "PredictedREAD", Body.Predicted[READ]);
                m_shader.Dispatch(computeKernel, Groups, 1, 1);

                m_shader.SetBuffer(solveKernel, "PredictedREAD", Body.Predicted[READ]);
                m_shader.SetBuffer(solveKernel, "PredictedWRITE", Body.Predicted[WRITE]);
                m_shader.Dispatch(solveKernel, Groups, 1, 1);

                Swap(Body.Predicted);
            }
        }

        private void UpdateVelocities(float dt)
        {
            int kernel = m_updateVelocitiesKernel;

            m_shader.SetBuffer(kernel, "Positions", Body.Positions);
            m_shader.SetBuffer(kernel, "PredictedREAD", Body.Predicted[READ]);
            m_shader.SetBuffer(kernel, "VelocitiesWRITE", Body.Velocities[WRITE]);
            m_shader.SetBuffer(kernel, "States", Body.States);

            m_shader.Dispatch(kernel, Groups, 1, 1);

            Swap(Body.Velocities);
        }

        private void SolveViscosity()
        {
            int kernel = m_solveViscosityKernel;

            m_shader.SetBuffer(kernel, "Densities", Body.Densities);
            m_shader.SetBuffer(kernel, "Boundary", Boundary.Positions);
            m_shader.SetBuffer(kernel, "IndexMap", Hash.IndexMap);
            m_shader.SetBuffer(kernel, "Table", Hash.Table);

            m_shader.SetBuffer(kernel, "PredictedREAD", Body.Predicted[READ]);
            m_shader.SetBuffer(kernel, "VelocitiesREAD", Body.Velocities[READ]);
            m_shader.SetBuffer(kernel, "VelocitiesWRITE", Body.Velocities[WRITE]);
            m_shader.SetBuffer(kernel, "States", Body.States);

            m_shader.Dispatch(kernel, Groups, 1, 1);

            Swap(Body.Velocities);
        }

        // ---- colisores por layer (C2) ----
        public void SetColliders(ColliderGPU[] arr)
        {
            m_colliderCount = (arr == null) ? 0 : arr.Length;
            if (m_colliderCount == 0) return;
            if (m_colliderBuffer == null || m_colliderBuffer.count != m_colliderCount)
            {
                m_colliderBuffer?.Release();
                m_colliderBuffer = new ComputeBuffer(m_colliderCount, Marshal.SizeOf(typeof(ColliderGPU)));
            }
            m_colliderBuffer.SetData(arr);
        }

        private void SolveColliders()
        {
            if (m_colliderBuffer == null || m_colliderCount == 0) return;

            int kernel = m_solveCollidersKernel;
            m_shader.SetInt("ColliderCount", m_colliderCount);
            m_shader.SetFloat("ColliderClearance", Body.ParticleRadius);
            m_shader.SetFloat("ColliderFriction", Mathf.Clamp01(ColliderFriction));
            m_shader.SetBuffer(kernel, "Colliders", m_colliderBuffer);
            m_shader.SetBuffer(kernel, "PredictedREAD", Body.Predicted[READ]);
            m_shader.SetBuffer(kernel, "PredictedWRITE", Body.Predicted[WRITE]);
            m_shader.SetBuffer(kernel, "VelocitiesREAD", Body.Velocities[READ]); // p/ atrito self-limitado
            m_shader.SetBuffer(kernel, "States", Body.States);

            m_shader.Dispatch(kernel, Groups, 1, 1);

            Swap(Body.Predicted);
        }

        // ---- colisores de MORTE (kill-on-contact) ----
        public void SetKillColliders(ColliderGPU[] arr)
        {
            m_killColliderCount = (arr == null) ? 0 : arr.Length;
            if (m_killColliderCount == 0) return;
            if (m_killColliderBuffer == null || m_killColliderBuffer.count != m_killColliderCount)
            {
                m_killColliderBuffer?.Release();
                m_killColliderBuffer = new ComputeBuffer(m_killColliderCount, Marshal.SizeOf(typeof(ColliderGPU)));
            }
            m_killColliderBuffer.SetData(arr);
        }

        // roda nas posicoes FINAIS (1x/frame): particula tocando colisor de morte -> some.
        private void SolveKillColliders()
        {
            if (m_killColliderBuffer == null || m_killColliderCount == 0) return;

            int kernel = m_solveKillCollidersKernel;
            m_shader.SetInt("KillColliderCount", m_killColliderCount);
            m_shader.SetFloat("KillClearance", Body.ParticleRadius);
            m_shader.SetVector("Graveyard", Graveyard);
            m_shader.SetBuffer(kernel, "KillColliders", m_killColliderBuffer);
            m_shader.SetBuffer(kernel, "Positions", Body.Positions);
            m_shader.SetBuffer(kernel, "States", Body.States);

            m_shader.Dispatch(kernel, Groups, 1, 1);
        }

        private void UpdatePositions()
        {
            int kernel = m_updatePositionsKernel;

            m_shader.SetBuffer(kernel, "Positions", Body.Positions);
            m_shader.SetBuffer(kernel, "PredictedREAD", Body.Predicted[READ]);
            m_shader.SetBuffer(kernel, "States", Body.States);

            m_shader.Dispatch(kernel, Groups, 1, 1);
        }

        private void Swap(ComputeBuffer[] buffers)
        {
            ComputeBuffer tmp = buffers[0];
            buffers[0] = buffers[1];
            buffers[1] = tmp;
        }
    }

}
