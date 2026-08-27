using System.Collections.Generic;
using UnityEngine;

namespace LabLiquidVR
{
    // ------------------------------------------------------------------------
    //  LiquidVolumeBaker  (L1)
    //  Voxeliza o INTERIOR da mesh da cavidade (uma vez) e, em runtime, acha o
    //  plano horizontal-MUNDO cujo volume submerso = fracao alvo, p/ QUALQUER
    //  orientacao do frasco. Volume EXATO (percentil dos voxels) e conserva ao
    //  inclinar — resolve a limitacao do asset Triple Axis (fracao de altura).
    //
    //  Voxelizacao por COLUNA: p/ cada (x,z) lanca uma reta vertical local,
    //  coleta Ys de interseccao com os triangulos, ordena, pares = spans dentro.
    //  O(colunas x triangulos). Rapido. Mesh precisa ser READABLE (Read/Write ON
    //  no import) e FECHADA (glassware liquido normalmente e').
    // ------------------------------------------------------------------------
    public class LiquidVolumeBaker
    {
        public int Resolution { get; private set; }
        public bool IsBaked => m_centersLocal != null && m_centersLocal.Length > 0;
        public int OccupiedCount => m_centersLocal == null ? 0 : m_centersLocal.Length;

        // volume geometrico da cavidade em unidades-locais^3 (antes de escala)
        public float CavityVolumeLocal { get; private set; }

        Vector3[] m_centersLocal;     // centros dos voxels OCUPADOS (local)
        Vector3[] m_rimLocal;         // vertices do topo (~5%) = borda p/ derramar
        float[]   m_scratchWorldY;    // reuso p/ nao alocar por frame
        bool[] m_occupiedCells;
        Vector3 m_gridMinLocal;
        Vector3 m_cellSizeLocal;
        Vector3 m_bakeUpLocal = Vector3.up;
        Vector3 m_centerlineOriginLocal;
        Vector3 m_bottomCenterLocal;
        float m_minBakeProjection;
        float m_maxBakeProjection;

        public Vector3 LocalUp => m_bakeUpLocal;

        // -------- BAKE (chamar 1x; ~ms) --------
        // localUp = direcao "cima" da vidraria no LOCAL da mesh (rest-pose). Define
        // onde fica a abertura/bico. Default (0,0,0) => usa +Y. Robusto a mesh Z-up.
        public bool Bake(Mesh mesh, int resolution = 32, Vector3 localUp = default)
        {
            Resolution = Mathf.Clamp(resolution, 8, 64);
            m_centersLocal = null;
            m_occupiedCells = null;

            if (mesh == null) { Debug.LogError("[LiquidVolumeBaker] mesh nula."); return false; }
            Vector3[] verts;
            int[] tris;
            try { verts = mesh.vertices; tris = mesh.triangles; }
            catch { Debug.LogError($"[LiquidVolumeBaker] mesh '{mesh.name}' NAO e' readable. Liga Read/Write no import."); return false; }
            if (verts.Length == 0 || tris.Length == 0) { Debug.LogError("[LiquidVolumeBaker] mesh vazia."); return false; }

            // bounds local
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < verts.Length; i++) { min = Vector3.Min(min, verts[i]); max = Vector3.Max(max, verts[i]); }

            int N = Resolution;
            Vector3 size = max - min;
            Vector3 cell = new Vector3(size.x / N, size.y / N, size.z / N);
            if (cell.x <= 0 || cell.y <= 0 || cell.z <= 0) { Debug.LogError("[LiquidVolumeBaker] mesh degenerada (bounds 0)."); return false; }
            float cellVol = cell.x * cell.y * cell.z;

            var centers = new List<Vector3>(N * N * 4);
            m_gridMinLocal = min;
            m_cellSizeLocal = cell;
            m_occupiedCells = new bool[N * N * N];
            var ys = new List<float>(16);
            int triCount = tris.Length / 3;

            for (int ix = 0; ix < N; ix++)
            {
                float ox = min.x + (ix + 0.5f) * cell.x;
                for (int iz = 0; iz < N; iz++)
                {
                    float oz = min.z + (iz + 0.5f) * cell.z;
                    ys.Clear();

                    for (int t = 0; t < triCount; t++)
                    {
                        Vector3 a = verts[tris[t * 3]];
                        Vector3 b = verts[tris[t * 3 + 1]];
                        Vector3 c = verts[tris[t * 3 + 2]];
                        if (ColumnHitsTriangle(ox, oz, a, b, c, out float y)) ys.Add(y);
                    }
                    if (ys.Count < 2) continue;
                    ys.Sort();

                    // pares (entra, sai) = dentro
                    for (int k = 0; k + 1 < ys.Count; k += 2)
                    {
                        float y0 = ys[k], y1 = ys[k + 1];
                        // voxels cujo centro cai no span
                        int iy0 = Mathf.CeilToInt((y0 - min.y) / cell.y - 0.5f);
                        int iy1 = Mathf.FloorToInt((y1 - min.y) / cell.y - 0.5f);
                        iy0 = Mathf.Clamp(iy0, 0, N - 1);
                        iy1 = Mathf.Clamp(iy1, 0, N - 1);
                        for (int iy = iy0; iy <= iy1; iy++)
                        {
                            float cy = min.y + (iy + 0.5f) * cell.y;
                            if (cy >= y0 && cy <= y1)
                            {
                                centers.Add(new Vector3(ox, cy, oz));
                                m_occupiedCells[ix + N * (iy + N * iz)] = true;
                            }
                        }
                    }
                }
            }

            if (centers.Count == 0) { Debug.LogError("[LiquidVolumeBaker] 0 voxels dentro (mesh aberta/invertida?)."); return false; }

            m_centersLocal = centers.ToArray();
            m_scratchWorldY = new float[m_centersLocal.Length];
            CavityVolumeLocal = m_centersLocal.Length * cellVol;

            // borda: vertices no topo ~5% ao longo do eixo CIMA local (abertura do frasco).
            // Projecao em localUp -> serve p/ mesh Y-up, Z-up, qualquer orientacao.
            Vector3 up = localUp.sqrMagnitude > 1e-6f ? localUp.normalized : Vector3.up;
            m_bakeUpLocal = up;
            float maxProj = float.MinValue, minProj = float.MaxValue;
            Vector3 topVert = verts[0];
            for (int i = 0; i < verts.Length; i++)
            {
                float p = Vector3.Dot(verts[i], up);
                if (p > maxProj) { maxProj = p; topVert = verts[i]; }
                if (p < minProj) minProj = p;
            }
            m_minBakeProjection = minProj;
            m_maxBakeProjection = maxProj;

            // Centro do volume e centro inferior usados pelo emissor de bolhas.
            // Ambos ficam no espaco local, portanto acompanham qualquer rotacao
            // posterior do frasco sem voltar ao eixo vertical do mundo.
            m_centerlineOriginLocal = Vector3.zero;
            for (int i = 0; i < m_centersLocal.Length; i++)
                m_centerlineOriginLocal += m_centersLocal[i];
            m_centerlineOriginLocal /= m_centersLocal.Length;

            float bottomThreshold = minProj + (maxProj - minProj) * 0.08f;
            m_bottomCenterLocal = Vector3.zero;
            int bottomCount = 0;
            for (int i = 0; i < m_centersLocal.Length; i++)
            {
                if (Vector3.Dot(m_centersLocal[i], up) > bottomThreshold) continue;
                m_bottomCenterLocal += m_centersLocal[i];
                bottomCount++;
            }
            m_bottomCenterLocal = bottomCount > 0
                ? m_bottomCenterLocal / bottomCount
                : m_centerlineOriginLocal + up * (minProj - Vector3.Dot(m_centerlineOriginLocal, up));
            float rimThresh = maxProj - (maxProj - minProj) * 0.05f;
            var rim = new List<Vector3>(64);
            for (int i = 0; i < verts.Length; i++) if (Vector3.Dot(verts[i], up) >= rimThresh) rim.Add(verts[i]);
            m_rimLocal = rim.Count > 0 ? rim.ToArray() : new[] { topVert };

            Debug.Log($"[LiquidVolumeBaker] bake OK: {m_centersLocal.Length} voxels, res {N}, volLocal={CavityVolumeLocal:0.####}, rim={m_rimLocal.Length}.");
            return true;
        }

        // Converte uma altura calibrada (0 = fundo, 1 = topo) na fracao real
        // ocupada pelos voxels. A fracao resultante continua sendo conservada
        // por PlaneYForVolume quando o recipiente inclina.
        public float VolumeFractionAtNormalizedHeight(float normalizedHeight)
        {
            if (!IsBaked) return Mathf.Clamp01(normalizedHeight);

            float threshold = Mathf.Lerp(m_minBakeProjection, m_maxBakeProjection,
                Mathf.Clamp01(normalizedHeight));
            int below = 0;
            for (int i = 0; i < m_centersLocal.Length; i++)
            {
                if (Vector3.Dot(m_centersLocal[i], m_bakeUpLocal) <= threshold)
                    below++;
            }
            return (float)below / m_centersLocal.Length;
        }

        // reta vertical (ox,oz) x triangulo -> y (baricentrico no plano XZ)
        static bool ColumnHitsTriangle(float ox, float oz, Vector3 a, Vector3 b, Vector3 c, out float y)
        {
            y = 0f;
            float detT = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(detT) < 1e-12f) return false;   // triangulo vertical: ignora
            float l1 = ((b.z - c.z) * (ox - c.x) + (c.x - b.x) * (oz - c.z)) / detT;
            float l2 = ((c.z - a.z) * (ox - c.x) + (a.x - c.x) * (oz - c.z)) / detT;
            float l3 = 1f - l1 - l2;
            if (l1 < 0f || l2 < 0f || l3 < 0f) return false;
            y = l1 * a.y + l2 * b.y + l3 * c.y;
            return true;
        }

        // -------- RUNTIME --------
        // planoY (mundo) tal que 'fraction' (0..1) do volume fica ABAIXO, na
        // orientacao dada. Percentil dos voxels -> volume exato + conserva ao virar.
        public float PlaneYForVolume(Matrix4x4 localToWorld, float fraction)
        {
            if (!IsBaked) return localToWorld.GetColumn(3).y;
            int n = m_centersLocal.Length;
            fraction = Mathf.Clamp01(fraction);

            // worldY = linha Y da matriz . centro + translacao
            float m10 = localToWorld.m10, m11 = localToWorld.m11, m12 = localToWorld.m12, m13 = localToWorld.m13;
            for (int i = 0; i < n; i++)
            {
                Vector3 c = m_centersLocal[i];
                m_scratchWorldY[i] = m10 * c.x + m11 * c.y + m12 * c.z + m13;
            }
            System.Array.Sort(m_scratchWorldY);

            if (fraction <= 0f) return m_scratchWorldY[0] - 1e-4f;          // vazio: abaixo de tudo
            if (fraction >= 1f) return m_scratchWorldY[n - 1] + 1e-4f;      // cheio: acima de tudo
            int idx = Mathf.Clamp(Mathf.RoundToInt(fraction * (n - 1)), 0, n - 1);
            return m_scratchWorldY[idx];
        }

        // menor Y-mundo da borda = ponto por onde derrama quando inclina
        public float SpoutWorldY(Matrix4x4 localToWorld)
        {
            if (m_rimLocal == null) return localToWorld.GetColumn(3).y;
            float minY = float.MaxValue;
            for (int i = 0; i < m_rimLocal.Length; i++)
            {
                float wy = localToWorld.MultiplyPoint3x4(m_rimLocal[i]).y;
                if (wy < minY) minY = wy;
            }
            return minY;
        }

        // PONTO-mundo do bico (vertice da borda mais baixo) — onde nasce o jato
        public Vector3 SpoutWorldPoint(Matrix4x4 localToWorld)
        {
            Vector3 origin = localToWorld.GetColumn(3);
            if (m_rimLocal == null || m_rimLocal.Length == 0) return origin;
            float minY = float.MaxValue;
            Vector3 best = origin;
            for (int i = 0; i < m_rimLocal.Length; i++)
            {
                Vector3 w = localToWorld.MultiplyPoint3x4(m_rimLocal[i]);
                if (w.y < minY) { minY = w.y; best = w; }
            }
            return best;
        }

        public Vector3 RimWorldCenter(Matrix4x4 localToWorld)
        {
            Vector3 origin = localToWorld.GetColumn(3);
            if (m_rimLocal == null || m_rimLocal.Length == 0) return origin;
            Vector3 center = Vector3.zero;
            for (int i = 0; i < m_rimLocal.Length; i++)
                center += localToWorld.MultiplyPoint3x4(m_rimLocal[i]);
            return center / m_rimLocal.Length;
        }

        public float RimWorldRadius(Matrix4x4 localToWorld)
        {
            if (m_rimLocal == null || m_rimLocal.Length == 0) return 0f;
            Vector3 center = RimWorldCenter(localToWorld);
            Vector3 normal = localToWorld.MultiplyVector(m_bakeUpLocal).normalized;
            float radius = 0f;
            for (int i = 0; i < m_rimLocal.Length; i++)
            {
                Vector3 radial = Vector3.ProjectOnPlane(
                    localToWorld.MultiplyPoint3x4(m_rimLocal[i]) - center,
                    normal);
                radius += radial.magnitude;
            }
            return radius / m_rimLocal.Length;
        }

        /// <summary>
        /// Centro inferior da cavidade. O deslocamento e' uma fracao da altura
        /// local e evita que a primeira metade da bolha atravesse o fundo.
        /// </summary>
        public Vector3 BottomWorldPoint(Matrix4x4 localToWorld, float normalizedOffset)
        {
            float height = Mathf.Max(0f, m_maxBakeProjection - m_minBakeProjection);
            Vector3 local = m_bottomCenterLocal
                + m_bakeUpLocal * (height * Mathf.Clamp(normalizedOffset, 0f, 0.25f));
            return localToWorld.MultiplyPoint3x4(local);
        }

        /// <summary>
        /// Colisao volumetrica para particulas de bolha. A grade ocupada e' a
        /// mesma usada para conservar o volume: ao tocar a parede a particula
        /// e' projetada de volta ao voxel interno e sua velocidade e' refletida.
        /// Ao tocar a superficie livre ela estoura (retorna false).
        /// </summary>
        public bool ConstrainBubble(
            Matrix4x4 localToWorld,
            Matrix4x4 worldToLocal,
            Plane surfacePlane,
            ref Vector3 positionWorld,
            ref Vector3 velocityWorld,
            float radiusWorld,
            float wallInset,
            float bounciness)
        {
            if (!IsBaked || m_occupiedCells == null) return true;
            if (surfacePlane.GetDistanceToPoint(positionWorld) >= -Mathf.Max(0.0002f, radiusWorld * 0.55f))
                return false;

            Vector3 localPoint = worldToLocal.MultiplyPoint3x4(positionWorld);
            Vector3 axisPoint = PointOnCenterline(localPoint);
            Vector3 insetProbe = axisPoint + (localPoint - axisPoint) / Mathf.Clamp(wallInset, 0.5f, 1f);
            if (ContainsLocal(localPoint) && ContainsLocal(insetProbe)) return true;

            Vector3 nearest = m_centersLocal[0];
            float nearestDistance = float.MaxValue;
            Vector3 safeCell = new Vector3(
                Mathf.Max(m_cellSizeLocal.x, 1e-5f),
                Mathf.Max(m_cellSizeLocal.y, 1e-5f),
                Mathf.Max(m_cellSizeLocal.z, 1e-5f));
            for (int i = 0; i < m_centersLocal.Length; i++)
            {
                Vector3 delta = m_centersLocal[i] - localPoint;
                float normalizedDistance = delta.x * delta.x / (safeCell.x * safeCell.x)
                    + delta.y * delta.y / (safeCell.y * safeCell.y)
                    + delta.z * delta.z / (safeCell.z * safeCell.z);
                if (normalizedDistance >= nearestDistance) continue;
                nearestDistance = normalizedDistance;
                nearest = m_centersLocal[i];
            }

            Vector3 nearestAxis = PointOnCenterline(nearest);
            Vector3 correctedLocal = nearestAxis + (nearest - nearestAxis) * Mathf.Clamp(wallInset, 0.5f, 1f);
            Vector3 collisionNormalLocal = localPoint - correctedLocal;
            if (collisionNormalLocal.sqrMagnitude < 1e-8f)
                collisionNormalLocal = localPoint - nearestAxis;
            collisionNormalLocal.Normalize();

            Vector3 localVelocity = worldToLocal.MultiplyVector(velocityWorld);
            float outwardSpeed = Vector3.Dot(localVelocity, collisionNormalLocal);
            if (outwardSpeed > 0f)
                localVelocity -= collisionNormalLocal * outwardSpeed * (1f + Mathf.Clamp01(bounciness));

            positionWorld = localToWorld.MultiplyPoint3x4(correctedLocal);
            velocityWorld = localToWorld.MultiplyVector(localVelocity);
            return true;
        }

        Vector3 PointOnCenterline(Vector3 localPoint)
        {
            float along = Vector3.Dot(localPoint - m_centerlineOriginLocal, m_bakeUpLocal);
            return m_centerlineOriginLocal + m_bakeUpLocal * along;
        }

        bool ContainsLocal(Vector3 localPoint)
        {
            int ix = Mathf.FloorToInt((localPoint.x - m_gridMinLocal.x) / Mathf.Max(m_cellSizeLocal.x, 1e-6f));
            int iy = Mathf.FloorToInt((localPoint.y - m_gridMinLocal.y) / Mathf.Max(m_cellSizeLocal.y, 1e-6f));
            int iz = Mathf.FloorToInt((localPoint.z - m_gridMinLocal.z) / Mathf.Max(m_cellSizeLocal.z, 1e-6f));
            int n = Resolution;
            if (ix < 0 || iy < 0 || iz < 0 || ix >= n || iy >= n || iz >= n) return false;
            return m_occupiedCells[ix + n * (iy + n * iz)];
        }
    }
}
