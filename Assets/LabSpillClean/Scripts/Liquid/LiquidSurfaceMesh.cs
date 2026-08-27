using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LabLiquidVR
{
    /// <summary>
    /// Intersects the cavity with the simulated surface plane to measure the
    /// wave centre and contact radius. The visible liquid remains the original
    /// closed cavity mesh; its backfaces provide the interior surface without a
    /// separate geometric cap.
    /// </summary>
    sealed class LiquidSurfaceMesh : IDisposable
    {
        const float PointMergeEpsilon = 0.00025f;

        readonly List<Vector3> m_intersections = new List<Vector3>(256);
        readonly List<PlanarPoint> m_planarPoints = new List<PlanarPoint>(256);
        readonly List<PlanarPoint> m_hull = new List<PlanarPoint>(128);
        readonly List<Vector3> m_vertices = new List<Vector3>(4096);
        readonly List<Vector3> m_normals = new List<Vector3>(4096);
        readonly List<Vector2> m_uvs = new List<Vector2>(4096);
        readonly List<int> m_triangles = new List<int>(16384);
        readonly List<Vector3> m_combinedVertices = new List<Vector3>(2048);
        readonly List<Vector3> m_combinedNormals = new List<Vector3>(2048);
        readonly List<Vector2> m_combinedUvs = new List<Vector2>(2048);
        readonly List<Color32> m_combinedColours = new List<Color32>(2048);
        readonly List<int> m_combinedTriangles = new List<int>(4096);
        float[] m_cumulative = new float[256];

        Mesh m_mesh;
        MeshFilter m_filter;
        MeshRenderer m_renderer;
        Material[] m_originalMaterials;
        Material m_layerMaterial;
        Mesh m_sourceMesh;
        Vector3[] m_sourceVertices;
        Vector3[] m_sourceNormals;
        Vector2[] m_sourceUvs;
        int[] m_sourceTriangles;
        bool m_reportedUnreadableMesh;
        bool m_hasBuiltSurface;
        Vector3 m_lastLocalPlaneNormal;
        float m_lastLocalPlaneDistance;
        int m_lastAngularSegments;
        int m_lastRadialRings;
        float m_lastSkirtDepth;
        Vector3 m_surfaceCenterLocal;

        public Vector3 SurfaceCenterWorld { get; private set; }
        public float SurfaceInnerRadiusWorld { get; private set; }
        public Mesh BodyMesh => m_mesh;

        struct PlanarPoint
        {
            public Vector2 plane;
            public Vector3 world;

            public PlanarPoint(Vector2 plane, Vector3 world)
            {
                this.plane = plane;
                this.world = world;
            }
        }

        public void Ensure(Transform owner, MeshRenderer renderer, Material material)
        {
            if (m_mesh == null)
            {
                m_filter = owner.GetComponent<MeshFilter>();
                m_mesh = new Mesh
                {
                    name = owner.name + " Liquid Body With Interior Surface",
                    hideFlags = HideFlags.DontSave
                };
            }
            m_renderer = renderer;
            EnsureSurfaceLayerMaterial(material);
        }

        void EnsureSurfaceLayerMaterial(Material material)
        {
            if (m_renderer == null || material == null) return;
            Material[] current = m_renderer.sharedMaterials;
            if (current.Length == 1 && current[0] == material)
            {
                m_layerMaterial = material;
                return;
            }
            if (m_originalMaterials == null) m_originalMaterials = current;
            m_layerMaterial = material;
            m_renderer.sharedMaterials = new[] { material };
        }

        public void SetVisible(bool visible) { }

        public void Rebuild(
            Mesh cavity,
            Transform owner,
            Plane surfacePlane,
            LiquidConfig config,
            bool visible)
        {
            if (m_mesh == null || cavity == null)
            {
                SetVisible(false);
                return;
            }

            if (m_sourceMesh != cavity || m_sourceVertices == null || m_sourceTriangles == null)
            {
                try
                {
                    m_sourceVertices = cavity.vertices;
                    m_sourceNormals = cavity.normals;
                    m_sourceUvs = cavity.uv;
                    m_sourceTriangles = cavity.triangles;
                    m_sourceMesh = cavity;
                    m_hasBuiltSurface = false;
                }
                catch (UnityException)
                {
                    if (!m_reportedUnreadableMesh)
                    {
                        Debug.LogWarning($"[{owner.name}] A malha da cavidade precisa de Read/Write Enabled para gerar a superficie do liquido.");
                        m_reportedUnreadableMesh = true;
                    }
                    SetVisible(false);
                    return;
                }
            }

            int angularSegments = config != null ? config.surfaceAngularSegments : 128;
            int radialRings = config != null ? config.surfaceRadialRings : 16;
            float skirtDepth = config != null ? config.surfaceSkirtDepth : 0.035f;

            // A malha vive no espaco local do frasco. Translacao pura do objeto nao
            // altera a intersecao; portanto nao precisa refazer hull e upload a cada
            // frame. Ondas continuam animadas inteiramente no shader.
            Vector3 localPlaneNormal = owner.InverseTransformDirection(surfacePlane.normal).normalized;
            Vector3 planePointWorld = -surfacePlane.normal * surfacePlane.distance;
            Vector3 planePointLocal = owner.InverseTransformPoint(planePointWorld);
            float localPlaneDistance = -Vector3.Dot(localPlaneNormal, planePointLocal);
            bool sameGeometry = m_hasBuiltSurface
                && m_filter != null
                && m_filter.sharedMesh == m_mesh
                && m_lastAngularSegments == angularSegments
                && m_lastRadialRings == radialRings
                && Mathf.Abs(m_lastSkirtDepth - skirtDepth) < 0.00001f
                && Vector3.Angle(m_lastLocalPlaneNormal, localPlaneNormal) < 0.12f
                && Mathf.Abs(m_lastLocalPlaneDistance - localPlaneDistance) < 0.000002f;
            if (sameGeometry)
            {
                SurfaceCenterWorld = owner.TransformPoint(m_surfaceCenterLocal);
                SetVisible(visible);
                return;
            }

            m_intersections.Clear();
            Matrix4x4 localToWorld = owner.localToWorldMatrix;
            float scale = Mathf.Max(owner.lossyScale.magnitude, 0.001f);
            float planeEpsilon = 0.00002f * scale;

            for (int i = 0; i + 2 < m_sourceTriangles.Length; i += 3)
            {
                Vector3 a = localToWorld.MultiplyPoint3x4(m_sourceVertices[m_sourceTriangles[i]]);
                Vector3 b = localToWorld.MultiplyPoint3x4(m_sourceVertices[m_sourceTriangles[i + 1]]);
                Vector3 c = localToWorld.MultiplyPoint3x4(m_sourceVertices[m_sourceTriangles[i + 2]]);
                AddTrianglePlaneIntersections(a, b, c, surfacePlane, planeEpsilon);
            }

            if (m_intersections.Count < 3 || !BuildHull(surfacePlane.normal))
            {
                if (m_filter != null) m_filter.sharedMesh = m_sourceMesh;
                SetVisible(false);
                return;
            }

            if (!BuildRadialGrid(owner, surfacePlane.normal, angularSegments, radialRings, skirtDepth)) return;
            UploadCombinedMesh();
            m_hasBuiltSurface = true;
            m_lastLocalPlaneNormal = localPlaneNormal;
            m_lastLocalPlaneDistance = localPlaneDistance;
            m_lastAngularSegments = angularSegments;
            m_lastRadialRings = radialRings;
            m_lastSkirtDepth = skirtDepth;
            EnsureSurfaceLayerMaterial(m_layerMaterial);
            SetVisible(visible);
        }

        void AddTrianglePlaneIntersections(Vector3 a, Vector3 b, Vector3 c, Plane plane, float epsilon)
        {
            float da = plane.GetDistanceToPoint(a);
            float db = plane.GetDistanceToPoint(b);
            float dc = plane.GetDistanceToPoint(c);
            AddEdgeIntersection(a, b, da, db, epsilon);
            AddEdgeIntersection(b, c, db, dc, epsilon);
            AddEdgeIntersection(c, a, dc, da, epsilon);
        }

        void AddEdgeIntersection(Vector3 a, Vector3 b, float da, float db, float epsilon)
        {
            bool aOnPlane = Mathf.Abs(da) <= epsilon;
            bool bOnPlane = Mathf.Abs(db) <= epsilon;
            if (aOnPlane) AddUniqueIntersection(a);
            if (bOnPlane) AddUniqueIntersection(b);
            if (aOnPlane || bOnPlane || (da < 0f) == (db < 0f)) return;

            float t = da / (da - db);
            AddUniqueIntersection(Vector3.LerpUnclamped(a, b, t));
        }

        void AddUniqueIntersection(Vector3 point)
        {
            float epsilonSquared = PointMergeEpsilon * PointMergeEpsilon;
            for (int i = 0; i < m_intersections.Count; i++)
                if ((m_intersections[i] - point).sqrMagnitude <= epsilonSquared)
                    return;
            m_intersections.Add(point);
        }

        bool BuildHull(Vector3 normal)
        {
            Vector3 axisU = Mathf.Abs(normal.y) < 0.9f
                ? Vector3.Cross(normal, Vector3.up).normalized
                : Vector3.Cross(normal, Vector3.right).normalized;
            Vector3 axisV = Vector3.Cross(normal, axisU).normalized;

            m_planarPoints.Clear();
            for (int i = 0; i < m_intersections.Count; i++)
            {
                Vector3 point = m_intersections[i];
                m_planarPoints.Add(new PlanarPoint(
                    new Vector2(Vector3.Dot(point, axisU), Vector3.Dot(point, axisV)), point));
            }
            m_planarPoints.Sort((left, right) =>
            {
                int x = left.plane.x.CompareTo(right.plane.x);
                return x != 0 ? x : left.plane.y.CompareTo(right.plane.y);
            });

            m_hull.Clear();
            for (int i = 0; i < m_planarPoints.Count; i++)
            {
                PlanarPoint point = m_planarPoints[i];
                while (m_hull.Count >= 2 && Cross(m_hull[m_hull.Count - 2], m_hull[m_hull.Count - 1], point) <= 0f)
                    m_hull.RemoveAt(m_hull.Count - 1);
                m_hull.Add(point);
            }
            int lowerCount = m_hull.Count;
            for (int i = m_planarPoints.Count - 2; i >= 0; i--)
            {
                PlanarPoint point = m_planarPoints[i];
                while (m_hull.Count > lowerCount && Cross(m_hull[m_hull.Count - 2], m_hull[m_hull.Count - 1], point) <= 0f)
                    m_hull.RemoveAt(m_hull.Count - 1);
                m_hull.Add(point);
            }
            if (m_hull.Count > 1) m_hull.RemoveAt(m_hull.Count - 1);
            return m_hull.Count >= 3;
        }

        static float Cross(PlanarPoint origin, PlanarPoint a, PlanarPoint b)
        {
            Vector2 oa = a.plane - origin.plane;
            Vector2 ob = b.plane - origin.plane;
            return oa.x * ob.y - oa.y * ob.x;
        }

        bool BuildRadialGrid(
            Transform owner,
            Vector3 worldNormal,
            int angularSegments,
            int radialRings,
            float skirtDepthWorld)
        {
            angularSegments = Mathf.Clamp(angularSegments, 24, 256);
            radialRings = Mathf.Clamp(radialRings, 3, 32);
            Vector3 center = Vector3.zero;
            for (int i = 0; i < m_hull.Count; i++) center += m_hull[i].world;
            center /= m_hull.Count;

            SurfaceCenterWorld = center;
            SurfaceInnerRadiusWorld = float.PositiveInfinity;
            for (int i = 0; i < m_hull.Count; i++)
                SurfaceInnerRadiusWorld = Mathf.Min(
                    SurfaceInnerRadiusWorld,
                    Vector3.Distance(center, m_hull[i].world));
            if (!float.IsFinite(SurfaceInnerRadiusWorld)) SurfaceInnerRadiusWorld = 0f;

            if (m_cumulative.Length < m_hull.Count + 1)
                Array.Resize(ref m_cumulative, Mathf.NextPowerOfTwo(m_hull.Count + 1));
            m_cumulative[0] = 0f;
            for (int i = 0; i < m_hull.Count; i++)
                m_cumulative[i + 1] = m_cumulative[i] + Vector3.Distance(m_hull[i].world, m_hull[(i + 1) % m_hull.Count].world);
            float perimeter = m_cumulative[m_hull.Count];
            if (perimeter <= 1e-5f) return false;

            Matrix4x4 worldToLocal = owner.worldToLocalMatrix;
            m_surfaceCenterLocal = worldToLocal.MultiplyPoint3x4(center);
            Vector3 localNormal = owner.InverseTransformDirection(worldNormal).normalized;
            m_vertices.Clear();
            m_normals.Clear();
            m_uvs.Clear();
            m_triangles.Clear();
            m_vertices.Add(worldToLocal.MultiplyPoint3x4(center));
            m_normals.Add(localNormal);
            m_uvs.Add(Vector2.zero);

            for (int ring = 1; ring <= radialRings; ring++)
            {
                float radius01 = ring / (float)radialRings;
                for (int segment = 0; segment < angularSegments; segment++)
                {
                    Vector3 boundary = SampleHull(m_cumulative, perimeter * segment / angularSegments);
                    Vector3 worldPoint = Vector3.LerpUnclamped(center, boundary, radius01);
                    m_vertices.Add(worldToLocal.MultiplyPoint3x4(worldPoint));
                    m_normals.Add(localNormal);
                    m_uvs.Add(new Vector2(radius01, segment / (float)angularSegments));
                }
            }

            // Dados legados da antiga superficie geometrica. Nao sao enviados ao
            // MeshRenderer; somente centro e raio calculados acima sao usados.
            int skirtStart = m_vertices.Count;
            for (int segment = 0; segment < angularSegments; segment++)
            {
                Vector3 boundary = SampleHull(m_cumulative, perimeter * segment / angularSegments);
                Vector3 skirtPoint = boundary - worldNormal * skirtDepthWorld;
                Vector3 wallNormalWorld = Vector3.ProjectOnPlane(boundary - center, worldNormal).normalized;
                m_vertices.Add(worldToLocal.MultiplyPoint3x4(skirtPoint));
                m_normals.Add(owner.InverseTransformDirection(wallNormalWorld).normalized);
                m_uvs.Add(new Vector2(1.01f, segment / (float)angularSegments));
            }

            for (int segment = 0; segment < angularSegments; segment++)
            {
                int next = (segment + 1) % angularSegments;
                m_triangles.Add(0);
                m_triangles.Add(1 + next);
                m_triangles.Add(1 + segment);
            }
            for (int ring = 1; ring < radialRings; ring++)
            {
                int innerStart = 1 + (ring - 1) * angularSegments;
                int outerStart = 1 + ring * angularSegments;
                for (int segment = 0; segment < angularSegments; segment++)
                {
                    int next = (segment + 1) % angularSegments;
                    int inner = innerStart + segment;
                    int innerNext = innerStart + next;
                    int outer = outerStart + segment;
                    int outerNext = outerStart + next;
                    m_triangles.Add(inner); m_triangles.Add(outerNext); m_triangles.Add(outer);
                    m_triangles.Add(inner); m_triangles.Add(innerNext); m_triangles.Add(outerNext);
                }
            }
            int outerStartIndex = 1 + (radialRings - 1) * angularSegments;
            for (int segment = 0; segment < angularSegments; segment++)
            {
                int next = (segment + 1) % angularSegments;
                int top = outerStartIndex + segment;
                int topNext = outerStartIndex + next;
                int bottom = skirtStart + segment;
                int bottomNext = skirtStart + next;
                m_triangles.Add(top); m_triangles.Add(bottom); m_triangles.Add(bottomNext);
                m_triangles.Add(top); m_triangles.Add(bottomNext); m_triangles.Add(topNext);
            }

            return true;
        }

        void UploadCombinedMesh()
        {
            int sourceVertexCount = m_sourceVertices.Length;
            int totalVertexCount = sourceVertexCount;
            m_combinedVertices.Clear();
            m_combinedNormals.Clear();
            m_combinedUvs.Clear();
            m_combinedColours.Clear();
            m_combinedTriangles.Clear();

            for (int i = 0; i < sourceVertexCount; i++)
            {
                m_combinedVertices.Add(m_sourceVertices[i]);
                m_combinedNormals.Add(m_sourceNormals != null && i < m_sourceNormals.Length
                    ? m_sourceNormals[i]
                    : Vector3.up);
                m_combinedUvs.Add(m_sourceUvs != null && i < m_sourceUvs.Length
                    ? m_sourceUvs[i]
                    : Vector2.zero);
                // Alpha zero identifies body vertices in the shader.
                m_combinedColours.Add(new Color32(255, 255, 255, 0));
            }
            m_mesh.Clear(false);
            m_mesh.indexFormat = totalVertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m_mesh.SetVertices(m_combinedVertices);
            m_mesh.SetNormals(m_combinedNormals);
            m_mesh.SetUVs(0, m_combinedUvs);
            m_mesh.SetColors(m_combinedColours);
            m_mesh.subMeshCount = 1;
            m_mesh.SetTriangles(m_sourceTriangles, 0, false);
            // Mantém os mesmos bounds da cavidade para o cálculo de volume e
            // iluminação não mudar quando a tampa é incorporada.
            m_mesh.bounds = m_sourceMesh.bounds;
            if (m_filter != null && m_filter.sharedMesh != m_mesh)
                m_filter.sharedMesh = m_mesh;
        }

        Vector3 SampleHull(float[] cumulative, float distance)
        {
            for (int i = 0; i < m_hull.Count; i++)
            {
                if (distance > cumulative[i + 1]) continue;
                float length = cumulative[i + 1] - cumulative[i];
                float t = length > 1e-6f ? (distance - cumulative[i]) / length : 0f;
                return Vector3.LerpUnclamped(m_hull[i].world, m_hull[(i + 1) % m_hull.Count].world, t);
            }
            return m_hull[0].world;
        }

        public void Dispose()
        {
            if (m_filter != null && m_filter.sharedMesh == m_mesh && m_sourceMesh != null)
                m_filter.sharedMesh = m_sourceMesh;
            if (m_renderer != null && m_originalMaterials != null)
                m_renderer.sharedMaterials = m_originalMaterials;
            if (m_mesh != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(m_mesh);
                else UnityEngine.Object.DestroyImmediate(m_mesh);
            }
            m_mesh = null;
            m_filter = null;
            m_renderer = null;
            m_originalMaterials = null;
            m_layerMaterial = null;
            m_hasBuiltSurface = false;
        }
    }
}
