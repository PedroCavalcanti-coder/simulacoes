using System;
using UnityEngine;

namespace PBDFluid
{

    public partial class FluidBody : IDisposable
    {

        public int NumParticles { get; private set; }

        public Bounds Bounds;

        public float Density { get; set; }

        public float Viscosity { get; set; }

        public float Dampning { get; set; }

        public float ParticleRadius { get; private set; }

        public float ParticleDiameter { get { return ParticleRadius * 2.0f; } }

        public float ParticleMass { get; set; }

        public float ParticleVolume { get; private set; }

        public ComputeBuffer Pressures { get; private set; }

        public ComputeBuffer Densities { get; private set; }

        // estado por particula: 0 = viva, 1 = morta (respingo despawnado)
        public ComputeBuffer States { get; private set; }

        // cor por particula (substancia) — float4
        public ComputeBuffer Colors { get; private set; }

        // ID logico da substancia. A fisica continua compartilhada; o renderer
        // usa este buffer para reconstruir uma superficie/material por liquido.
        public ComputeBuffer SubstanceIds { get; private set; }

        public ComputeBuffer Positions { get; private set; }

        public ComputeBuffer[] Predicted { get; private set; }

        public ComputeBuffer[] Velocities { get; private set; }

        public FluidBody(ParticleSource source, float radius, float density, Matrix4x4 RTS)
        {
            NumParticles = source.NumParticles;
            Density = density;
            Viscosity = 0.002f;
            Dampning = 0.0f;

            ParticleRadius = radius;
            ParticleVolume = (4.0f / 3.0f) * Mathf.PI * Mathf.Pow(radius, 3);
            ParticleMass = ParticleVolume * Density;

            Densities = new ComputeBuffer(NumParticles, sizeof(float));
            Pressures = new ComputeBuffer(NumParticles, sizeof(float));
            States = new ComputeBuffer(NumParticles, sizeof(float));
            States.SetData(new float[NumParticles]); // 0 = ativa

            Colors = new ComputeBuffer(NumParticles, 4 * sizeof(float));
            var white = new Vector4[NumParticles];
            for (int i = 0; i < NumParticles; i++) white[i] = Vector4.one;
            Colors.SetData(white);

            SubstanceIds = new ComputeBuffer(NumParticles, sizeof(uint));
            SubstanceIds.SetData(new uint[NumParticles]);

            CreateParticles(source, RTS);
        }

        public void Dispose()
        {

            if (Positions != null)
            {
                Positions.Release();
                Positions = null;
            }

            if (Densities != null)
            {
                Densities.Release();
                Densities = null;
            }

            if (Pressures != null)
            {
                Pressures.Release();
                Pressures = null;
            }

            if (States != null)
            {
                States.Release();
                States = null;
            }

            if (Colors != null) { Colors.Release(); Colors = null; }
            if (SubstanceIds != null) { SubstanceIds.Release(); SubstanceIds = null; }
            CBUtility.Release(Predicted);
            CBUtility.Release(Velocities);
        }

        private void CreateParticles(ParticleSource source, Matrix4x4 RTS)
        {
            Vector4[] positions = new Vector4[NumParticles];
            Vector4[] predicted = new Vector4[NumParticles];
            Vector4[] velocities = new Vector4[NumParticles];

            float inf = float.PositiveInfinity;
            Vector3 min = new Vector3(inf, inf, inf);
            Vector3 max = new Vector3(-inf, -inf, -inf);

            for (int i = 0; i < NumParticles; i++)
            {
                Vector4 pos = RTS * source.Positions[i];
                positions[i] = pos;
                predicted[i] = pos;

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

            //Predicted and velocities use a double buffer as solver step
            //needs to read from many locations of buffer and write the result
            //in same pass. Could be removed if needed as long as buffer writes 
            //are atomic. Not sure if they are.

            Predicted = new ComputeBuffer[2];
            Predicted[0] = new ComputeBuffer(NumParticles, 4 * sizeof(float));
            Predicted[0].SetData(predicted);
            Predicted[1] = new ComputeBuffer(NumParticles, 4 * sizeof(float));
            Predicted[1].SetData(predicted);

            Velocities = new ComputeBuffer[2];
            Velocities[0] = new ComputeBuffer(NumParticles, 4 * sizeof(float));
            Velocities[0].SetData(velocities);
            Velocities[1] = new ComputeBuffer(NumParticles, 4 * sizeof(float));
            Velocities[1].SetData(velocities);
        }

        /// <summary>
        /// Adiciona particulas (cresce os buffers). Usado pelo emissor / botao
        /// "adicionar liquido". Apos chamar, recrie o FluidSolver (a contagem
        /// mudou -> hash/bitonic precisam ser refeitos).
        /// </summary>
        public void Append(Vector3[] pts, Vector3 initialVel) { Append(pts, initialVel, Color.white, 0); }

        public void Append(Vector3[] pts, Vector3 initialVel, Color col) { Append(pts, initialVel, col, 0); }

        public void Append(Vector3[] pts, Vector3 initialVel, Color col, uint substanceId)
        {
            if (pts == null || pts.Length == 0) return;

            var velocities = new Vector3[pts.Length];
            var colors = new Color[pts.Length];
            var substanceIds = new uint[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                velocities[i] = initialVel;
                colors[i] = col;
                substanceIds[i] = substanceId;
            }
            Append(pts, velocities, colors, substanceIds);
        }

        /// <summary>
        /// Adiciona um lote heterogeneo em uma unica realocacao. Cada particula
        /// pode ter velocidade, cor e substancia proprias; emissores simultaneos
        /// usam este caminho para pagar um unico readback/rebuild por intervalo.
        /// </summary>
        public void Append(Vector3[] pts, Vector3[] initialVelocities, Color[] colors, uint[] substanceIds)
        {
            if (pts == null || pts.Length == 0) return;
            int add = pts.Length;
            if (initialVelocities == null || initialVelocities.Length != add ||
                colors == null || colors.Length != add ||
                substanceIds == null || substanceIds.Length != add)
                throw new ArgumentException("Os metadados do lote devem ter o mesmo tamanho de pts.");

            int n0 = NumParticles;
            int n1 = n0 + add;

            // le o estado atual da GPU
            Vector4[] pos = new Vector4[n0]; Positions.GetData(pos);
            Vector4[] vel0 = new Vector4[n0]; Velocities[0].GetData(vel0);
            Vector4[] vel1 = new Vector4[n0]; Velocities[1].GetData(vel1);
            Vector4[] pre0 = new Vector4[n0]; Predicted[0].GetData(pre0);
            Vector4[] pre1 = new Vector4[n0]; Predicted[1].GetData(pre1);
            float[] dens = new float[n0]; Densities.GetData(dens);
            float[] pres = new float[n0]; Pressures.GetData(pres);
            float[] st = new float[n0]; States.GetData(st);
            Vector4[] cols = new Vector4[n0]; Colors.GetData(cols);
            uint[] ids = new uint[n0]; SubstanceIds.GetData(ids);

            // arrays novos (copia antigos + acrescenta)
            Vector4[] npos = new Vector4[n1]; System.Array.Copy(pos, npos, n0);
            Vector4[] nvel0 = new Vector4[n1]; System.Array.Copy(vel0, nvel0, n0);
            Vector4[] nvel1 = new Vector4[n1]; System.Array.Copy(vel1, nvel1, n0);
            Vector4[] npre0 = new Vector4[n1]; System.Array.Copy(pre0, npre0, n0);
            Vector4[] npre1 = new Vector4[n1]; System.Array.Copy(pre1, npre1, n0);
            float[] ndens = new float[n1]; System.Array.Copy(dens, ndens, n0);
            float[] npres = new float[n1]; System.Array.Copy(pres, npres, n0);
            float[] nst = new float[n1]; System.Array.Copy(st, nst, n0);
            Vector4[] ncols = new Vector4[n1]; System.Array.Copy(cols, ncols, n0);
            uint[] nids = new uint[n1]; System.Array.Copy(ids, nids, n0);

            for (int i = 0; i < add; i++)
            {
                Vector4 p = new Vector4(pts[i].x, pts[i].y, pts[i].z, 0);
                Vector3 velocity = initialVelocities[i];
                Vector4 v4 = new Vector4(velocity.x, velocity.y, velocity.z, 0);
                int j = n0 + i;
                npos[j] = p; npre0[j] = p; npre1[j] = p;
                nvel0[j] = v4; nvel1[j] = v4;
                ndens[j] = 0; npres[j] = 0; nst[j] = 0; // ativa
                ncols[j] = colors[i]; nids[j] = substanceIds[i];
                Bounds.Encapsulate(pts[i]);
            }

            // realoca
            Positions.Release(); Predicted[0].Release(); Predicted[1].Release();
            Velocities[0].Release(); Velocities[1].Release();
            Densities.Release(); Pressures.Release(); States.Release();
            Colors.Release(); SubstanceIds.Release();

            int s4 = 4 * sizeof(float), s1 = sizeof(float);
            Positions = new ComputeBuffer(n1, s4); Positions.SetData(npos);
            Predicted[0] = new ComputeBuffer(n1, s4); Predicted[0].SetData(npre0);
            Predicted[1] = new ComputeBuffer(n1, s4); Predicted[1].SetData(npre1);
            Velocities[0] = new ComputeBuffer(n1, s4); Velocities[0].SetData(nvel0);
            Velocities[1] = new ComputeBuffer(n1, s4); Velocities[1].SetData(nvel1);
            Densities = new ComputeBuffer(n1, s1); Densities.SetData(ndens);
            Pressures = new ComputeBuffer(n1, s1); Pressures.SetData(npres);
            States = new ComputeBuffer(n1, s1); States.SetData(nst);
            Colors = new ComputeBuffer(n1, s4); Colors.SetData(ncols);
            SubstanceIds = new ComputeBuffer(n1, sizeof(uint)); SubstanceIds.SetData(nids);

            NumParticles = n1;
        }

        /// <summary>
        /// Remove de verdade as particulas MORTAS (States>0.5): compacta as vivas p/ o
        /// inicio e ENCOLHE os buffers -> menos dispatch/sort/memoria. Retorna true se
        /// algo mudou (o caller PRECISA recriar o FluidSolver — a contagem mudou).
        /// </summary>
        public bool CompactDead()
        {
            int n0 = NumParticles;
            if (n0 == 0) return false;

            float[] st = new float[n0]; States.GetData(st);
            int alive = 0; for (int i = 0; i < n0; i++) if (st[i] <= 0.5f) alive++;
            if (alive == n0) return false;   // nada morto
            if (alive == 0 && n0 == 1) return false; // sentinela morta ja compactada
            int compactedCount = Mathf.Max(1, alive);

            Vector4[] pos = new Vector4[n0]; Positions.GetData(pos);
            Vector4[] vel0 = new Vector4[n0]; Velocities[0].GetData(vel0);
            Vector4[] vel1 = new Vector4[n0]; Velocities[1].GetData(vel1);
            Vector4[] pre0 = new Vector4[n0]; Predicted[0].GetData(pre0);
            Vector4[] pre1 = new Vector4[n0]; Predicted[1].GetData(pre1);
            Vector4[] cols = new Vector4[n0]; Colors.GetData(cols);
            uint[] ids = new uint[n0]; SubstanceIds.GetData(ids);

            var npos = new Vector4[compactedCount];
            var nvel0 = new Vector4[compactedCount];
            var nvel1 = new Vector4[compactedCount];
            var npre0 = new Vector4[compactedCount];
            var npre1 = new Vector4[compactedCount];
            var ncols = new Vector4[compactedCount];
            var nids = new uint[compactedCount];
            int k = 0;
            for (int i = 0; i < n0; i++)
            {
                if (st[i] > 0.5f) continue;
                npos[k] = pos[i]; nvel0[k] = vel0[i]; nvel1[k] = vel1[i];
                npre0[k] = pre0[i]; npre1[k] = pre1[i]; ncols[k] = cols[i]; nids[k] = ids[i]; k++;
            }
            if (alive == 0)
            {
                // O solver e o ComputeBuffer exigem pelo menos um elemento.
                // A posicao morta existente ja esta no cemiterio.
                npos[0] = pos[0];
                npre0[0] = pos[0];
                npre1[0] = pos[0];
                nids[0] = uint.MaxValue;
            }

            Positions.Release(); Velocities[0].Release(); Velocities[1].Release();
            Predicted[0].Release(); Predicted[1].Release(); Colors.Release(); SubstanceIds.Release();
            Densities.Release(); Pressures.Release(); States.Release();

            int s4 = 4 * sizeof(float), s1 = sizeof(float);
            Positions = new ComputeBuffer(compactedCount, s4); Positions.SetData(npos);
            Velocities[0] = new ComputeBuffer(compactedCount, s4); Velocities[0].SetData(nvel0);
            Velocities[1] = new ComputeBuffer(compactedCount, s4); Velocities[1].SetData(nvel1);
            Predicted[0] = new ComputeBuffer(compactedCount, s4); Predicted[0].SetData(npre0);
            Predicted[1] = new ComputeBuffer(compactedCount, s4); Predicted[1].SetData(npre1);
            Colors = new ComputeBuffer(compactedCount, s4); Colors.SetData(ncols);
            SubstanceIds = new ComputeBuffer(compactedCount, sizeof(uint)); SubstanceIds.SetData(nids);
            Densities = new ComputeBuffer(compactedCount, s1); Densities.SetData(new float[compactedCount]);
            Pressures = new ComputeBuffer(compactedCount, s1); Pressures.SetData(new float[compactedCount]);
            float[] compactedStates = new float[compactedCount];
            if (alive == 0) compactedStates[0] = 1f;
            States = new ComputeBuffer(compactedCount, s1); States.SetData(compactedStates);

            NumParticles = compactedCount;
            return true;
        }

    }


}
