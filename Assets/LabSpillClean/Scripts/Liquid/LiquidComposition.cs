using System;
using System.Collections.Generic;
using UnityEngine;

namespace LabLiquidVR
{
    [Serializable]
    public sealed class LiquidPhase
    {
        public LiquidConfig appearance;
        public string category;
        public float density;
        public float volumeML;
        public float displayBottomML;
    }

    sealed class LiquidComposition
    {
        readonly List<LiquidPhase> m_phases = new List<LiquidPhase>(4);
        readonly List<LiquidPhase> m_densityOrder = new List<LiquidPhase>(4);

        public IReadOnlyList<LiquidPhase> Phases => m_phases;
        public float TotalVolumeML { get; private set; }

        public void Reset(LiquidConfig config, float volumeML)
        {
            m_phases.Clear();
            TotalVolumeML = Mathf.Max(0f, volumeML);
            if (config == null || TotalVolumeML <= 0.0001f) return;
            m_phases.Add(new LiquidPhase
            {
                appearance = LiquidConfig.Copy(config),
                category = config.category,
                density = config.densityKgPerLiter,
                volumeML = TotalVolumeML,
                displayBottomML = 0f
            });
        }

        public float Receive(LiquidConfig incoming, float requestedML, float capacityML)
        {
            if (incoming == null || requestedML <= 0f) return 0f;
            float accepted = Mathf.Min(requestedML, Mathf.Max(0f, capacityML - TotalVolumeML));
            if (accepted <= 0f) return 0f;

            LiquidPhase compatible = null;
            for (int i = 0; i < m_phases.Count; i++)
                if (string.Equals(m_phases[i].category, incoming.category,
                    StringComparison.OrdinalIgnoreCase))
                {
                    compatible = m_phases[i];
                    break;
                }

            if (compatible != null)
            {
                float oldVolume = compatible.volumeML;
                compatible.appearance = LiquidConfig.Mix(
                    compatible.appearance, oldVolume, incoming, accepted);
                compatible.density = compatible.appearance.densityKgPerLiter;
                compatible.volumeML += accepted;
            }
            else
            {
                m_phases.Add(new LiquidPhase
                {
                    appearance = LiquidConfig.Copy(incoming),
                    category = incoming.category,
                    density = incoming.densityKgPerLiter,
                    volumeML = accepted,
                    // Liquido recebido nasce no topo e separa depois.
                    displayBottomML = TotalVolumeML
                });
            }

            TotalVolumeML += accepted;
            return accepted;
        }

        public float RemoveFromTop(float requestedML)
        {
            float remaining = Mathf.Min(Mathf.Max(0f, requestedML), TotalVolumeML);
            float removed = remaining;
            while (remaining > 0.0001f && m_phases.Count > 0)
            {
                LiquidPhase top = TopPhase;
                float take = Mathf.Min(remaining, top.volumeML);
                top.volumeML -= take;
                remaining -= take;
                TotalVolumeML -= take;
                if (top.volumeML <= 0.0001f) m_phases.Remove(top);
            }
            TotalVolumeML = Mathf.Max(0f, TotalVolumeML);
            return removed - remaining;
        }

        public void SetTotal(float targetML, float capacityML, LiquidConfig fallback)
        {
            targetML = Mathf.Clamp(targetML, 0f, capacityML);
            if (targetML > TotalVolumeML)
                Receive(TopPhase != null ? TopPhase.appearance : fallback,
                    targetML - TotalVolumeML, capacityML);
            else if (targetML < TotalVolumeML)
                RemoveFromTop(TotalVolumeML - targetML);
        }

        public void UpdateSeparation(float deltaTime, float fullSeparationSeconds)
        {
            m_densityOrder.Clear();
            m_densityOrder.AddRange(m_phases);
            m_densityOrder.Sort((a, b) => b.density.CompareTo(a.density));
            float bottom = 0f;
            float speed = Mathf.Max(1f, TotalVolumeML / Mathf.Max(0.1f, fullSeparationSeconds));
            for (int i = 0; i < m_densityOrder.Count; i++)
            {
                LiquidPhase phase = m_densityOrder[i];
                phase.displayBottomML = Mathf.MoveTowards(
                    phase.displayBottomML, bottom, speed * deltaTime);
                bottom += phase.volumeML;
            }
        }

        public LiquidPhase TopPhase
        {
            get
            {
                LiquidPhase top = null;
                float height = float.NegativeInfinity;
                for (int i = 0; i < m_phases.Count; i++)
                {
                    float candidate = m_phases[i].displayBottomML + m_phases[i].volumeML;
                    if (candidate <= height) continue;
                    height = candidate;
                    top = m_phases[i];
                }
                return top;
            }
        }
    }
}
