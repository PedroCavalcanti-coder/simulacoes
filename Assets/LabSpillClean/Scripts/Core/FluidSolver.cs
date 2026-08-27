using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PBDFluid
{
    public sealed class FluidSolver : IDisposable
    {
        // colisores — primitivas analiticas, atualizadas por frame
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

        /// <summary>
        /// Boca de um recipiente. A particula que entra por ela e capturada dentro do
        /// proprio passo de fisica, e nao numa varredura da CPU: com isso a amostragem
        /// passa a ser por substep, e um jato rapido nao atravessa mais a boca inteira
        /// entre duas leituras.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PortGPU
        {
            public Vector3 center;
            public float radius;
            public Vector3 normal;
            public float captureDepth;
        }

        /// <summary>
        /// Uma particula a nascer. Preenchida pela CPU num buffer de tamanho fixo e
        /// gravada pelo kernel SpawnParticles. Layout identico ao struct do shader.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SpawnGPU
        {
            public Vector4 position;   // xyz = posicao, w = instante da morte
            public Vector4 velocity;   // xyz = velocidade, w livre
            public uint substance;
            public uint slot;
            public uint pad0;
            public uint pad1;

            public SpawnGPU(Vector3 position, Vector3 velocity, float deathTime,
                uint substance, uint slot)
            {
                this.position = new Vector4(position.x, position.y, position.z, deathTime);
                this.velocity = new Vector4(velocity.x, velocity.y, velocity.z, 0f);
                this.substance = substance;
                this.slot = slot;
                pad0 = 0;
                pad1 = 0;
            }
        }

        const int THREADS = 128;
        const int READ = 0;
        const int WRITE = 1;

        public int Groups { get; }
        public FluidPool Pool { get; }
        public GridHash Hash { get; }
        public SmoothingKernel Kernel { get; }

        public int SolverIterations { get; set; } = 2;
        public int ConstraintIterations { get; set; } = 2;

        public Vector3 Gravity = new Vector3(0f, -9.81f, 0f);
        public Vector3 Graveyard = new Vector3(0f, -500f, 0f);

        // 0..1: atrito com o colisor. Colisor MOVENDO arrasta o fluido ate' a vel dele; fluido
        // deslizando num colisor PARADO freia aos poucos. 0 = sem atrito.
        public float ColliderFriction = 0.4f;

        // --- estabilidade PBF (anti-"pipoca"), calibravel ao vivo ---
        public float CFL = 0.5f;              // desloc. max por substep = CFL * cellSize
        public float CorrectionFactor = 0.4f; // correcao max por iteracao = fator * raio
        public float Relaxation = 60f;        // epsilon CFM

        // --- AMORTECIMENTO ADAPTATIVO p/ REPOUSO (dissipacao viscosa) ---
        public float RestDamping = 2f;      // 1/s em repouso
        public float MoveDamping = 0.2f;    // 1/s em movimento (baixo = derrame vivo)
        public float DampRefSpeed = 0.35f;  // m/s onde ja conta como "rapido"

        /// <summary>Coesao entre vizinhos, em m/s². E o que mantem o jato coeso.</summary>
        public float Cohesion = 0.4f;

        /// <summary>Profundidade acima do plano da boca que ja conta como dentro.</summary>
        public float PortEntryDepth = 0.01f;

        readonly ComputeShader m_shader;
        readonly int m_predictPositionsKernel;
        readonly int m_computeDensityKernel;
        readonly int m_solveConstraintKernel;
        readonly int m_solveCollidersKernel;
        readonly int m_updateVelocitiesKernel;
        readonly int m_solveViscosityKernel;
        readonly int m_updatePositionsKernel;
        readonly int m_spawnParticlesKernel;

        ComputeBuffer m_colliderBuffer;
        int m_colliderCount;

        ComputeBuffer m_portBuffer;
        int m_portCount;

        readonly ComputeBuffer m_spawnBuffer;
        readonly ComputeBuffer m_deathCountBuffer;

        public FluidSolver(FluidPool pool, Bounds domain, int maxSpawnPerDispatch)
        {
            Pool = pool;

            float cellSize = pool.ParticleRadius * 4f;
            Hash = new GridHash(domain, pool.Capacity, cellSize);
            Kernel = new SmoothingKernel(cellSize);

            Groups = pool.Capacity / THREADS;
            if (pool.Capacity % THREADS != 0) Groups++;

            m_shader = Resources.Load("FluidSolver") as ComputeShader;
            m_predictPositionsKernel = m_shader.FindKernel("PredictPositions");
            m_computeDensityKernel = m_shader.FindKernel("ComputeDensity");
            m_solveConstraintKernel = m_shader.FindKernel("SolveConstraint");
            m_solveCollidersKernel = m_shader.FindKernel("SolveColliders");
            m_updateVelocitiesKernel = m_shader.FindKernel("UpdateVelocities");
            m_solveViscosityKernel = m_shader.FindKernel("SolveViscosity");
            m_updatePositionsKernel = m_shader.FindKernel("UpdatePositions");
            m_spawnParticlesKernel = m_shader.FindKernel("SpawnParticles");

            m_spawnBuffer = new ComputeBuffer(
                Mathf.Max(THREADS, maxSpawnPerDispatch), Marshal.SizeOf<SpawnGPU>());
            m_deathCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);

            // Criado ja no construtor mesmo sem portos: UpdatePositions declara o buffer
            // e roda todo passo, e o D3D reclama de um StructuredBuffer nao ligado ainda
            // que o laco de leitura nunca execute com PortCount = 0.
            m_portBuffer = new ComputeBuffer(8, Marshal.SizeOf<PortGPU>());
        }

        public void Dispose()
        {
            Hash.Dispose();
            m_colliderBuffer?.Release(); m_colliderBuffer = null;
            m_portBuffer?.Release(); m_portBuffer = null;
            m_spawnBuffer?.Release();
            m_deathCountBuffer?.Release();
        }

        // ------------------------------------------------------------------ nascimento

        /// <summary>
        /// Grava um lote de particulas novas nos slots que a CPU ja reservou. Um SetData
        /// num buffer de tamanho fixo e um Dispatch: nenhuma realocacao, nenhum readback.
        /// </summary>
        public void Spawn(SpawnGPU[] records, int count)
        {
            if (count <= 0) return;
            count = Mathf.Min(count, m_spawnBuffer.count);

            m_spawnBuffer.SetData(records, 0, 0, count);

            int kernel = m_spawnParticlesKernel;
            m_shader.SetInt("SpawnCount", count);
            m_shader.SetBuffer(kernel, "Spawns", m_spawnBuffer);
            m_shader.SetBuffer(kernel, "Positions", Pool.Positions);
            // A velocidade inicial tem de cair no buffer que o PredictPositions do
            // proximo passo vai LER, e nao no par dele.
            m_shader.SetBuffer(kernel, "SpawnVelocities", Pool.Velocities[READ]);
            m_shader.SetBuffer(kernel, "SubstanceIds", Pool.SubstanceIds);
            m_shader.SetBuffer(kernel, "DeathTimes", Pool.DeathTimes);
            m_shader.SetBuffer(kernel, "States", Pool.States);
            m_shader.SetBuffer(kernel, "Densities", Pool.Densities);
            m_shader.SetBuffer(kernel, "Pressures", Pool.Pressures);

            int groups = count / THREADS;
            if (count % THREADS != 0) groups++;
            m_shader.Dispatch(kernel, groups, 1, 1);
        }

        // ------------------------------------------------------------------ passo

        public void StepPhysics(float dt, float simTime)
        {
            if (dt <= 0.0) return;
            if (SolverIterations <= 0 || ConstraintIterations <= 0) return;

            dt /= SolverIterations;

            m_shader.SetInt("NumParticles", Pool.Capacity);
            m_shader.SetVector("Gravity", Gravity);
            m_shader.SetVector("Graveyard", Graveyard);
            m_shader.SetFloat("SimTime", simTime);
            m_shader.SetFloat("Dampning", Pool.Dampning);
            m_shader.SetFloat("DeltaTime", dt);
            m_shader.SetFloat("Density", Pool.Density);
            m_shader.SetFloat("Viscosity", Pool.Viscosity);
            m_shader.SetFloat("ParticleMass", Pool.ParticleMass);
            m_shader.SetFloat("Cohesion", Mathf.Max(0f, Cohesion));

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
            m_shader.SetFloat("MaxCorrection", CorrectionFactor * Pool.ParticleRadius);
            m_shader.SetFloat("Relaxation", Relaxation);

            m_shader.SetFloat("RestDamping", Mathf.Max(0f, RestDamping));
            m_shader.SetFloat("MoveDamping", Mathf.Max(0f, MoveDamping));
            m_shader.SetFloat("DampRefSpeed", Mathf.Max(1e-4f, DampRefSpeed));
            m_shader.SetFloat("PortEntryDepth", PortEntryDepth);

            for (int i = 0; i < SolverIterations; i++)
            {
                PredictPositions();
                Hash.Process(Pool.Predicted[READ], Pool.States);
                ConstrainPositions();
                SolveColliders();
                UpdateVelocities();
                SolveViscosity();
                UpdatePositions();
            }
        }

        void PredictPositions()
        {
            int kernel = m_predictPositionsKernel;

            m_shader.SetBuffer(kernel, "Positions", Pool.Positions);
            m_shader.SetBuffer(kernel, "PredictedWRITE", Pool.Predicted[WRITE]);
            m_shader.SetBuffer(kernel, "VelocitiesREAD", Pool.Velocities[READ]);
            m_shader.SetBuffer(kernel, "VelocitiesWRITE", Pool.Velocities[WRITE]);
            m_shader.SetBuffer(kernel, "States", Pool.States);

            m_shader.Dispatch(kernel, Groups, 1, 1);

            Swap(Pool.Predicted);
            Swap(Pool.Velocities);
        }

        void ConstrainPositions()
        {
            int computeKernel = m_computeDensityKernel;
            int solveKernel = m_solveConstraintKernel;

            m_shader.SetBuffer(computeKernel, "Densities", Pool.Densities);
            m_shader.SetBuffer(computeKernel, "Pressures", Pool.Pressures);
            m_shader.SetBuffer(computeKernel, "IndexMap", Hash.IndexMap);
            m_shader.SetBuffer(computeKernel, "Table", Hash.Table);

            m_shader.SetBuffer(solveKernel, "Pressures", Pool.Pressures);
            m_shader.SetBuffer(solveKernel, "IndexMap", Hash.IndexMap);
            m_shader.SetBuffer(solveKernel, "Table", Hash.Table);
            m_shader.SetBuffer(solveKernel, "States", Pool.States);

            for (int i = 0; i < ConstraintIterations; i++)
            {
                m_shader.SetBuffer(computeKernel, "PredictedREAD", Pool.Predicted[READ]);
                m_shader.Dispatch(computeKernel, Groups, 1, 1);

                m_shader.SetBuffer(solveKernel, "PredictedREAD", Pool.Predicted[READ]);
                m_shader.SetBuffer(solveKernel, "PredictedWRITE", Pool.Predicted[WRITE]);
                m_shader.Dispatch(solveKernel, Groups, 1, 1);

                Swap(Pool.Predicted);
            }
        }

        void UpdateVelocities()
        {
            int kernel = m_updateVelocitiesKernel;

            m_shader.SetBuffer(kernel, "Positions", Pool.Positions);
            m_shader.SetBuffer(kernel, "PredictedREAD", Pool.Predicted[READ]);
            m_shader.SetBuffer(kernel, "VelocitiesWRITE", Pool.Velocities[WRITE]);
            m_shader.SetBuffer(kernel, "States", Pool.States);

            m_shader.Dispatch(kernel, Groups, 1, 1);

            Swap(Pool.Velocities);
        }

        void SolveViscosity()
        {
            int kernel = m_solveViscosityKernel;

            m_shader.SetBuffer(kernel, "Densities", Pool.Densities);
            m_shader.SetBuffer(kernel, "IndexMap", Hash.IndexMap);
            m_shader.SetBuffer(kernel, "Table", Hash.Table);
            m_shader.SetBuffer(kernel, "PredictedREAD", Pool.Predicted[READ]);
            m_shader.SetBuffer(kernel, "VelocitiesREAD", Pool.Velocities[READ]);
            m_shader.SetBuffer(kernel, "VelocitiesWRITE", Pool.Velocities[WRITE]);
            m_shader.SetBuffer(kernel, "States", Pool.States);

            m_shader.Dispatch(kernel, Groups, 1, 1);

            Swap(Pool.Velocities);
        }

        void UpdatePositions()
        {
            int kernel = m_updatePositionsKernel;

            m_shader.SetInt("PortCount", m_portCount);
            if (m_portBuffer != null)
                m_shader.SetBuffer(kernel, "Ports", m_portBuffer);
            m_shader.SetBuffer(kernel, "Positions", Pool.Positions);
            m_shader.SetBuffer(kernel, "PredictedREAD", Pool.Predicted[READ]);
            m_shader.SetBuffer(kernel, "States", Pool.States);
            m_shader.SetBuffer(kernel, "SubstanceIds", Pool.SubstanceIds);
            m_shader.SetBuffer(kernel, "DeathTimes", Pool.DeathTimes);
            m_shader.SetBuffer(kernel, "Deaths", Pool.Deaths);

            m_shader.Dispatch(kernel, Groups, 1, 1);
        }

        // ------------------------------------------------------------------ colisores e portos

        public void SetColliders(ColliderGPU[] arr, int count)
        {
            m_colliderCount = arr == null ? 0 : Mathf.Min(count, arr.Length);
            if (m_colliderCount == 0) return;

            EnsureCapacity(ref m_colliderBuffer, m_colliderCount, Marshal.SizeOf<ColliderGPU>());
            m_colliderBuffer.SetData(arr, 0, 0, m_colliderCount);
        }

        public void SetPorts(PortGPU[] arr, int count)
        {
            m_portCount = arr == null ? 0 : Mathf.Min(count, arr.Length);
            if (m_portCount == 0) return;

            EnsureCapacity(ref m_portBuffer, m_portCount, Marshal.SizeOf<PortGPU>());
            m_portBuffer.SetData(arr, 0, 0, m_portCount);
        }

        void SolveColliders()
        {
            if (m_colliderBuffer == null || m_colliderCount == 0) return;

            int kernel = m_solveCollidersKernel;
            m_shader.SetInt("ColliderCount", m_colliderCount);
            m_shader.SetFloat("ColliderClearance", Pool.ParticleRadius);
            m_shader.SetFloat("ColliderFriction", Mathf.Clamp01(ColliderFriction));
            m_shader.SetBuffer(kernel, "Colliders", m_colliderBuffer);
            m_shader.SetBuffer(kernel, "PredictedREAD", Pool.Predicted[READ]);
            m_shader.SetBuffer(kernel, "PredictedWRITE", Pool.Predicted[WRITE]);
            m_shader.SetBuffer(kernel, "VelocitiesREAD", Pool.Velocities[READ]);
            m_shader.SetBuffer(kernel, "States", Pool.States);

            m_shader.Dispatch(kernel, Groups, 1, 1);

            Swap(Pool.Predicted);
        }

        // ------------------------------------------------------------------ mortes

        /// <summary>Zera o contador do buffer Append das mortes. Uma vez por frame, antes dos passos.</summary>
        public void ResetDeaths() => Pool.Deaths.SetCounterValue(0);

        /// <summary>
        /// Copia o contador do buffer Append para um buffer legivel e o devolve. O
        /// chamador dispara a leitura assincrona; nada aqui bloqueia a GPU.
        /// </summary>
        public ComputeBuffer CopyDeathCount()
        {
            ComputeBuffer.CopyCount(Pool.Deaths, m_deathCountBuffer, 0);
            return m_deathCountBuffer;
        }

        static void EnsureCapacity(ref ComputeBuffer buffer, int count, int stride)
        {
            if (buffer != null && buffer.count >= count) return;
            buffer?.Release();
            buffer = new ComputeBuffer(count, stride);
        }

        static void Swap(ComputeBuffer[] buffers)
        {
            ComputeBuffer tmp = buffers[0];
            buffers[0] = buffers[1];
            buffers[1] = tmp;
        }
    }
}
