using System.Collections.Generic;
using LabLiquidVR;
using PBDFluid;
using UnityEngine;

namespace LabSpill
{
    /// <summary>
    /// Reune os colisores da cena para o solver.
    ///
    /// Antes so existiam os colisores marcados com um componente proprio, divididos em
    /// bancada (colide) e chao (mata). Agora a regra e uma so: a particula colide com
    /// tudo que o Unity ja conhece como colisor dentro do dominio, e morre por tempo de
    /// vida, em qualquer lugar.
    /// </summary>
    public sealed class SpillColliderProvider
    {
        readonly List<FluidSolver.ColliderGPU> m_colliders = new List<FluidSolver.ColliderGPU>(64);
        readonly Collider[] m_overlap;
        readonly HashSet<int> m_warnedMeshes = new HashSet<int>();

        float m_nextRefresh;

        public IReadOnlyList<FluidSolver.ColliderGPU> Colliders => m_colliders;
        public FluidSolver.ColliderGPU[] Array { get; private set; } = new FluidSolver.ColliderGPU[0];
        public int Count => m_colliders.Count;

        public SpillColliderProvider(int maxColliders)
        {
            m_overlap = new Collider[Mathf.Max(8, maxColliders)];
        }

        /// <summary>Forca a proxima chamada de <see cref="Refresh"/> a reunir de novo.</summary>
        public void SetDirty() => m_nextRefresh = 0f;

        /// <summary>
        /// Reune os colisores se o intervalo tiver passado. Retorna true quando a lista
        /// mudou e precisa ser reenviada para a GPU.
        /// </summary>
        public bool Refresh(Bounds domain, float interval, bool force = false)
        {
            if (!force && Time.time < m_nextRefresh)
                return false;

            m_nextRefresh = Time.time + Mathf.Max(0.05f, interval);
            m_colliders.Clear();

            int found = Physics.OverlapBoxNonAlloc(
                domain.center, domain.extents, m_overlap, Quaternion.identity,
                ~0, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < found; i++)
            {
                Collider col = m_overlap[i];
                if (col == null || !col.enabled || !col.gameObject.activeInHierarchy)
                    continue;
                if (IsExcluded(col))
                    continue;

                m_colliders.Add(ToGpu(col));
            }

            if (Array.Length < m_colliders.Count)
                Array = new FluidSolver.ColliderGPU[Mathf.NextPowerOfTwo(m_colliders.Count)];
            m_colliders.CopyTo(Array);
            return true;
        }

        /// <summary>
        /// Vidraria nao colide. Todo frasco da cena tem um BoxCollider em volta, e
        /// desde que a particula passou a colidir com tudo essa caixa viraria tampa: a
        /// gota mirada na boca bateria no topo e nunca chegaria ao porto que a converte
        /// em volume. Em troca, a gota que erra atravessa o corpo do frasco - artefato
        /// pequeno e local, contra a perda da funcao principal.
        ///
        /// O reconhecimento e automatico, por presenca do componente de recipiente, em
        /// vez de depender de um marcador posto a mao em cada frasco: frasco novo ja
        /// nasce certo, e nao ha marcador para alguem esquecer. O marcador
        /// <see cref="SpillColliderExclude"/> continua existindo para os outros casos.
        ///
        /// Procura nas duas direcoes porque o colisor fica no frasco e o recipiente no
        /// filho que desenha o liquido.
        /// </summary>
        static bool IsExcluded(Collider col)
        {
            if (col.GetComponentInParent<SpillColliderExclude>() != null) return true;
            if (col.GetComponentInParent<SpillLiquidContainer>() != null) return true;
            return col.GetComponentInChildren<SpillLiquidContainer>(true) != null;
        }

        FluidSolver.ColliderGPU ToGpu(Collider col)
        {
            Transform tr = col.transform;
            Vector3 scale = tr.lossyScale;
            Vector3 abs = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));

            var body = col.attachedRigidbody;
            var gpu = new FluidSolver.ColliderGPU
            {
                axisX = tr.right,
                axisY = tr.up,
                axisZ = tr.forward,
                velocity = body != null ? body.linearVelocity : Vector3.zero
            };

            if (col is SphereCollider sphere)
            {
                gpu.type = 0;
                gpu.center = tr.TransformPoint(sphere.center);
                gpu.radius = sphere.radius * Mathf.Max(abs.x, Mathf.Max(abs.y, abs.z));
                return gpu;
            }

            if (col is CapsuleCollider capsule)
            {
                gpu.type = 2;
                gpu.center = tr.TransformPoint(capsule.center);
                if (capsule.direction == 0)
                {
                    gpu.axisY = tr.right; gpu.axisX = tr.up; gpu.axisZ = tr.forward;
                }
                else if (capsule.direction == 2)
                {
                    gpu.axisY = tr.forward; gpu.axisX = tr.right; gpu.axisZ = tr.up;
                }
                float axisScale = capsule.direction == 0 ? abs.x
                    : capsule.direction == 1 ? abs.y : abs.z;
                float radialScale = capsule.direction == 0 ? Mathf.Max(abs.y, abs.z)
                    : capsule.direction == 1 ? Mathf.Max(abs.x, abs.z) : Mathf.Max(abs.x, abs.y);
                gpu.radius = capsule.radius * radialScale;
                gpu.halfExt = new Vector3(
                    0f, Mathf.Max(0f, capsule.height * 0.5f * axisScale - gpu.radius), 0f);
                return gpu;
            }

            gpu.type = 1;
            if (col is BoxCollider box)
            {
                gpu.center = tr.TransformPoint(box.center);
                gpu.halfExt = Vector3.Scale(box.size, abs) * 0.5f;
                return gpu;
            }

            // Qualquer outra coisa (mesh, terreno) vira a caixa alinhada aos eixos do
            // proprio bounds. Aproximado de proposito: a cena nao tem nenhum caso
            // desses hoje, e inventar decomposicao de mesh antes de existir um objeto
            // que precise dela seria codigo sem uso a manter. O aviso sai uma vez por
            // objeto, para a aproximacao nunca ser silenciosa.
            int id = col.GetInstanceID();
            if (m_warnedMeshes.Add(id))
                Debug.LogWarning(
                    $"[LabSpill] '{col.name}' e um {col.GetType().Name} e sera aproximado pela " +
                    "caixa do bounds. Se a forma importar, troque por primitivas ou marque com " +
                    "SpillColliderExclude.", col);

            Bounds bounds = col.bounds;
            gpu.center = bounds.center;
            gpu.axisX = Vector3.right;
            gpu.axisY = Vector3.up;
            gpu.axisZ = Vector3.forward;
            gpu.halfExt = bounds.extents;
            return gpu;
        }
    }
}
