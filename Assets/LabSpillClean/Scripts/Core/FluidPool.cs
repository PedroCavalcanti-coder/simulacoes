using System;
using System.Collections.Generic;
using UnityEngine;

namespace PBDFluid
{
    /// <summary>
    /// Um pool de particulas de capacidade FIXA.
    ///
    /// O que veio antes (FluidBody) crescia e encolhia os ComputeBuffers conforme o
    /// liquido era emitido: cada lote custava dez GetData sincronos e dez realocacoes,
    /// quarenta vezes por segundo enquanto se derramava, e o solver inteiro tinha de
    /// ser reconstruido junto porque a contagem mudava. Era essa a origem do engasgo
    /// no derrame, nao a simulacao em si.
    ///
    /// Aqui todos os buffers nascem com <see cref="Capacity"/> elementos e nunca mais
    /// mudam de tamanho. Um slot nao usado nao e ausencia de memoria: e uma particula
    /// morta, estacionada no cemiterio e ignorada por todos os kernels. Nascer e morrer
    /// viram escrita em slot, e a unica contabilidade na CPU e a lista de slots livres.
    /// </summary>
    public sealed class FluidPool : IDisposable
    {
        const int THREADS = 128;

        /// <summary>Numero de slots. Nunca muda depois do construtor.</summary>
        public int Capacity { get; }

        /// <summary>Quantos slots estao vivos agora, mantido pela lista livre.</summary>
        public int AliveCount => Capacity - m_freeSlots.Count;

        public float Density { get; }
        public float Viscosity { get; set; }
        public float Dampning { get; set; }
        public float ParticleRadius { get; }
        public float ParticleDiameter => ParticleRadius * 2f;
        public float ParticleMass { get; set; }
        public float ParticleVolume { get; }

        public ComputeBuffer Positions { get; private set; }
        public ComputeBuffer[] Predicted { get; private set; }
        public ComputeBuffer[] Velocities { get; private set; }
        public ComputeBuffer Densities { get; private set; }
        public ComputeBuffer Pressures { get; private set; }
        public ComputeBuffer States { get; private set; }
        public ComputeBuffer SubstanceIds { get; private set; }
        public ComputeBuffer DeathTimes { get; private set; }

        /// <summary>
        /// Slots que morreram no passo: (slot, porto+1). Buffer de tipo Append; a
        /// contagem sai por <see cref="ComputeBuffer.CopyCount"/>.
        /// </summary>
        public ComputeBuffer Deaths { get; private set; }

        // Ordem descendente na pilha para que os primeiros Pop devolvam 0, 1, 2...:
        // slots ocupados ficam agrupados no inicio do buffer, o que ajuda a coerencia
        // de cache dos kernels enquanto ha pouco liquido em cena.
        readonly Stack<int> m_freeSlots;

        public FluidPool(int capacity, float radius, float density)
        {
            Capacity = Mathf.Max(THREADS, capacity);
            if (Capacity % THREADS != 0)
                Capacity += THREADS - Capacity % THREADS;

            Density = density;
            Viscosity = 0.002f;
            Dampning = 0f;
            ParticleRadius = radius;
            ParticleVolume = 4f / 3f * Mathf.PI * Mathf.Pow(radius, 3f);
            ParticleMass = ParticleVolume * Density;

            const int float4Stride = 4 * sizeof(float);
            Positions = new ComputeBuffer(Capacity, float4Stride);
            Predicted = new[]
            {
                new ComputeBuffer(Capacity, float4Stride),
                new ComputeBuffer(Capacity, float4Stride)
            };
            Velocities = new[]
            {
                new ComputeBuffer(Capacity, float4Stride),
                new ComputeBuffer(Capacity, float4Stride)
            };
            Densities = new ComputeBuffer(Capacity, sizeof(float));
            Pressures = new ComputeBuffer(Capacity, sizeof(float));
            States = new ComputeBuffer(Capacity, sizeof(float));
            SubstanceIds = new ComputeBuffer(Capacity, sizeof(uint));
            DeathTimes = new ComputeBuffer(Capacity, sizeof(float));
            Deaths = new ComputeBuffer(Capacity, 2 * sizeof(uint), ComputeBufferType.Append);

            m_freeSlots = new Stack<int>(Capacity);
            for (int i = Capacity - 1; i >= 0; i--)
                m_freeSlots.Push(i);
        }

        /// <summary>
        /// Estaciona todo mundo morto no cemiterio. Chamado uma vez, depois que o
        /// dominio e conhecido: antes disso nao ha um lugar seguro para o cemiterio.
        /// </summary>
        public void ParkAll(Vector3 graveyard)
        {
            var positions = new Vector4[Capacity];
            var dead = new float[Capacity];
            var hidden = new uint[Capacity];
            var deathTimes = new float[Capacity];
            var parked = new Vector4(graveyard.x, graveyard.y, graveyard.z, 0f);
            for (int i = 0; i < Capacity; i++)
            {
                positions[i] = parked;
                dead[i] = 1f;
                hidden[i] = uint.MaxValue;
                deathTimes[i] = float.MaxValue;
            }

            Positions.SetData(positions);
            Predicted[0].SetData(positions);
            Predicted[1].SetData(positions);
            States.SetData(dead);
            SubstanceIds.SetData(hidden);
            DeathTimes.SetData(deathTimes);

            var zero4 = new Vector4[Capacity];
            Velocities[0].SetData(zero4);
            Velocities[1].SetData(zero4);
            var zero1 = new float[Capacity];
            Densities.SetData(zero1);
            Pressures.SetData(zero1);
        }

        /// <summary>Reserva um slot livre. False quando o pool esta cheio.</summary>
        public bool TryAllocate(out int slot)
        {
            if (m_freeSlots.Count == 0)
            {
                slot = -1;
                return false;
            }

            slot = m_freeSlots.Pop();
            return true;
        }

        /// <summary>
        /// Devolve um slot que a GPU relatou como morto. A GPU ja o estacionou e o
        /// escondeu do renderer; aqui so a contabilidade da CPU e atualizada.
        /// </summary>
        public void ReleaseSlot(int slot)
        {
            if (slot >= 0 && slot < Capacity)
                m_freeSlots.Push(slot);
        }

        public void Dispose()
        {
            Positions?.Release(); Positions = null;
            if (Predicted != null)
            {
                Predicted[0]?.Release();
                Predicted[1]?.Release();
                Predicted = null;
            }
            if (Velocities != null)
            {
                Velocities[0]?.Release();
                Velocities[1]?.Release();
                Velocities = null;
            }
            Densities?.Release(); Densities = null;
            Pressures?.Release(); Pressures = null;
            States?.Release(); States = null;
            SubstanceIds?.Release(); SubstanceIds = null;
            DeathTimes?.Release(); DeathTimes = null;
            Deaths?.Release(); Deaths = null;
        }
    }
}
