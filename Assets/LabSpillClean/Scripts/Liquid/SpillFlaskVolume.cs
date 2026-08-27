// Copia de Assets/LiquidFX (pasta de exemplo, somente-leitura). Tipos e namespace
// renomeados para conviver com o original no mesmo projeto. Ver PLANO-REFORMA.md, tarefa 2.0.
using System.Collections.Generic;
using LiquidVolumeFX;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace LabSpill
{
    /// <summary>
    /// Gives a LiquidVolumePro flask a real volume in millilitres.
    ///
    /// Two independent modes, chosen by <c>Volume.detail.isMultiple()</c>:
    ///
    /// - Single (the LVP "Default"/"Simple" details): one level, one blended colour. This is the
    ///   original behaviour, untouched, and what every flask in the project uses today.
    ///
    /// - Layered (LVP "Multiple" details): a stack of named <see cref="SpillLiquidDefinition"/> liquids,
    ///   authored once in <see cref="initialContents"/> and baked into LiquidVolumePro's own
    ///   <c>liquidLayers</c> array. Pouring drains the exposed top liquid first, colour and all;
    ///   receiving stacks a new layer on top, or merges into an existing one of the same category
    ///   (LiquidVolumePro's own miscibility rule - see SpillLiquidCategory). See
    ///   Assets/LiquidFX/SPEC-Camadas.md for the full design rationale.
    /// </summary>
    // ExecuteAlways so the layered state is rebuilt in the editor too. Without it the runtime
    // fields (layeredMode, the slot table, the millilitre total) are only ever populated on a real
    // OnEnable, so after leaving play mode the inspector reports an empty, non-layered flask while
    // the LiquidVolume next to it is still visibly full - the component and the thing it drives
    // disagreeing is exactly the kind of thing that sends someone hunting for a bug that is not there.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LiquidVolume))]
    public sealed class SpillFlaskVolume : MonoBehaviour, ISpillLiquidContainer
    {
        [System.Serializable]
        public struct LayerCharge
        {
            public SpillLiquidDefinition liquid;
            [Min(0f)] public float millilitres;
        }

        [Header("Calibration")]
        [SerializeField, Min(1f)] float capacityML = 250f;

        [Tooltip("Normalised level that corresponds to an empty flask. Most LVP meshes never reach 0. Single mode only.")]
        [SerializeField, Range(0f, 0.4f)] float emptyLevel = 0.02f;

        [Tooltip("Normalised level that corresponds to a full flask, just under the neck. Single mode only.")]
        [SerializeField, Range(0.5f, 1f)] float fullLevel = 0.92f;

        [Header("Layered Contents")]
        [Tooltip("Populate this to switch the flask into layered mode: it will be baked into " +
            "LiquidVolumePro's own layer stack (switching Volume.detail to a Multiple variant if " +
            "needed) the moment this changes, and again on enable. Order in this list is cosmetic " +
            "only - the real stacking order is decided by each liquid's category density.")]
        [SerializeField] List<LayerCharge> initialContents = new List<LayerCharge>();

        [Tooltip("Normalised level that corresponds to a full flask, in layered mode.")]
        [SerializeField, Range(0.5f, 1f)] float layeredFullLevel = 0.92f;

        [Header("Pouring Geometry")]
        [Tooltip("Point the liquid leaves from when the flask is tilted. Usually an empty child on the spout.")]
        [SerializeField] Transform lip;

        [Tooltip("Tilt in degrees from upright where liquid starts to run out.")]
        [SerializeField, Range(5f, 120f)] float spillTiltDegrees = 42f;

        [Tooltip("Tilt in degrees where the flask pours at full rate.")]
        [SerializeField, Range(10f, 180f)] float fullTiltDegrees = 110f;

        [Tooltip("Millilitres per second at full tilt with a full flask.")]
        [SerializeField, Min(1f)] float maxFlowMLPerSecond = 120f;

        [Header("Receiving")]
        [Tooltip("Radius of the mouth. A stream landing inside this radius counts as going in.")]
        [SerializeField, Min(0.005f)] float portRadius = 0.045f;

        [Tooltip("Local offset of the mouth from the flask origin.")]
        [SerializeField] Vector3 portLocalOffset = new Vector3(0f, 0.12f, 0f);

        LiquidVolume liquidVolume;
        float contentsML;
        bool initialised;
        bool layeredMode;

        // Parallel to Volume.liquidLayers: which SpillLiquidDefinition (if any) occupies each slot.
        // Kept in a separate array rather than derived from layer colour because two different
        // liquids of the same category can share an identical rendered colour.
        SpillLiquidDefinition[] slotLiquid;

        const float AmountEpsilon = 0.00001f;

        bool warnedAboutUntrackedAdd;
        bool warnedAboutLayerOverflow;

        public Transform Transform => transform;
        public LiquidVolume Volume => liquidVolume != null ? liquidVolume : liquidVolume = GetComponent<LiquidVolume>();
        public Transform Lip => lip != null ? lip : transform;
        public float CapacityML => capacityML;
        public float ContentsML => layeredMode ? LayeredContentsML() : contentsML;
        public float FreeML => Mathf.Max(0f, capacityML - ContentsML);
        public float MaxFlowMLPerSecond => maxFlowMLPerSecond;

        /// <summary>True once this flask has been baked into LiquidVolumePro's layer stack.</summary>
        public bool IsLayered => layeredMode;

        /// <summary>The liquid currently exposed at the surface - what a tilt pours out. Null in single mode.</summary>
        public SpillLiquidDefinition TopLiquid
        {
            get
            {
                int slot = TopSlotIndex();
                return slot >= 0 ? slotLiquid[slot] : null;
            }
        }

        public Color LiquidColor => layeredMode ? TopLayerColor() : (Volume != null ? Volume.liquidColor1 : Color.white);

        public Vector3 PortCentreWorld => transform.TransformPoint(portLocalOffset);
        public float PortRadius => portRadius * MaxAbsoluteScale(transform.lossyScale);

        public Vector3 SurfaceCentreWorld
        {
            get
            {
                Vector3 centre = transform.position;
                centre.y = SurfaceWorldY;
                return centre;
            }
        }

        public float SurfaceWorldY => Volume != null ? Volume.liquidSurfaceYPosition : transform.position.y;

        /// <summary>Angle between the flask up axis and world up, in degrees.</summary>
        public float TiltDegrees => Vector3.Angle(transform.up, Vector3.up);

        void OnEnable()
        {
            Initialise();
            SpillContainerRegistry.Register(this);
        }

        void OnDisable()
        {
            SpillContainerRegistry.Unregister(this);
        }

        void OnValidate()
        {
            fullLevel = Mathf.Max(fullLevel, emptyLevel + 0.05f);
            fullTiltDegrees = Mathf.Max(fullTiltDegrees, spillTiltDegrees + 5f);

            if (initialised && !layeredMode)
                PushLevelToShader();

#if UNITY_EDITOR
            // initialContents.Count > 0 is the only trigger: a flask nobody has touched the
            // layered fields on must never be silently switched to Multiple detail underneath it.
            if (initialContents.Count > 0)
            {
                EditorApplication.delayCall += () =>
                {
                    if (this != null)
                        BakeInitialContents();
                };
            }
#endif
        }

        void Initialise()
        {
            if (Volume == null)
                return;

            if (!initialised)
            {
                layeredMode = Volume.detail.isMultiple();
                if (layeredMode)
                {
                    EnsureLayeredArrays();

                    // Only rebake when this component actually owns the contents. A Multiple-detail
                    // flask with hand-authored layers and an empty list is someone else's setup -
                    // baking an empty list over it would silently wipe the flask.
                    if (initialContents.Count > 0)
                        BakeInitialContents();
                    else
                        AdoptExistingLayers();
                }
                else
                {
                    // Adopt whatever the artist authored in the inspector as the starting volume.
                    contentsML = NormalisedToML(Volume.level);
                }

                initialised = true;
            }

            if (!layeredMode)
                PushLevelToShader();
        }

        // ------------------------------------------------------------------ ISpillLiquidContainer

        public bool IsAbovePort(Vector3 worldPoint)
        {
            Vector3 centre = PortCentreWorld;
            float dx = worldPoint.x - centre.x;
            float dz = worldPoint.z - centre.z;
            float radius = PortRadius;
            return dx * dx + dz * dz <= radius * radius;
        }

        public float AddML(float millilitres, Color color)
        {
            if (millilitres <= 0f)
                return 0f;

            Initialise();

            if (!layeredMode)
            {
                float accepted = Mathf.Min(millilitres, FreeML);
                if (accepted <= 0f)
                    return 0f;

                BlendColor(color, accepted);
                contentsML += accepted;
                PushLevelToShader();
                return accepted;
            }

            // No SpillLiquidDefinition identity on this path (see AddLayeredML for the real transfer).
            // The only sane thing to do without fabricating a category is merge into whatever
            // liquid is already exposed on top; with nothing to merge into, the volume is dropped
            // rather than guessing at a density.
            int top = TopSlotIndex();
            if (top < 0)
            {
                if (!warnedAboutUntrackedAdd)
                {
                    warnedAboutUntrackedAdd = true;
                    Debug.LogWarning($"{name}: received {millilitres:0.#} mL with no SpillLiquidDefinition and no " +
                        "existing layer to merge into, so it was dropped. Pour from a layered SpillFlaskVolume " +
                        "(which calls AddLayeredML) instead of a plain colour source.", this);
                }
                return 0f;
            }

            float acceptedLayered = Mathf.Min(millilitres, FreeML);
            if (acceptedLayered <= 0f)
                return 0f;

            LiquidVolume.LiquidLayer[] layers = Volume.liquidLayers;
            layers[top].amount += MLToAmount(acceptedLayered, layers[top].density);
            Volume.UpdateLayers(true);
            return acceptedLayered;
        }

        /// <summary>
        /// Adds an identified liquid. Stirs it into the layer of the same mixing family when there
        /// is one, otherwise stacks a new layer. This is the entry point a layer-aware pour should
        /// call instead of <see cref="AddML"/>.
        /// </summary>
        public float AddLayeredML(float millilitres, SpillLiquidDefinition liquidToAdd)
        {
            if (millilitres <= 0f)
                return 0f;

            if (liquidToAdd == null || !layeredMode)
                return AddML(millilitres, liquidToAdd != null ? liquidToAdd.Color : Color.white);

            Initialise();
            EnsureLayeredArrays();
            return AddLayeredCore(millilitres, liquidToAdd);
        }

        /// <summary>
        /// The body of <see cref="AddLayeredML"/> without the Initialise call, so
        /// <see cref="BakeInitialContents"/> can reuse it. Baking runs from inside Initialise
        /// itself, and calling back into it there would recurse forever.
        /// </summary>
        float AddLayeredCore(float millilitres, SpillLiquidDefinition liquidToAdd)
        {
            float accepted = Mathf.Min(millilitres, FreeML);
            if (accepted <= 0f)
                return 0f;

            LiquidVolume.LiquidLayer[] layers = Volume.liquidLayers;

            // 1. Something of the same mixing family is already here - stir the two together into
            //    that one slot. This is the step LiquidVolumePro would do itself for miscible
            //    layers of equal density; doing it here is what lets density keep meaning stacking
            //    order alone (see SpillLiquidCategory).
            int compatible = FindSlotWithCategory(liquidToAdd.Category);
            if (compatible >= 0)
            {
                float existingML = AmountToML(layers[compatible].amount, layers[compatible].density);
                liquidToAdd.BlendInto(ref layers[compatible], existingML, accepted);
                layers[compatible].amount = MLToAmount(existingML + accepted, layers[compatible].density);

                // The blend has no identity of its own, so the slot reports whichever of its
                // ingredients now holds the most volume. That is what colours the next pour.
                if (accepted > existingML)
                    slotLiquid[compatible] = liquidToAdd;
                layers[compatible].layerName = slotLiquid[compatible].DisplayName;

                Volume.UpdateLayers(true);
                return accepted;
            }

            // 2. A free slot - occupy it.
            int free = FindFreeSlot();
            if (free >= 0)
            {
                slotLiquid[free] = liquidToAdd;
                liquidToAdd.ApplyTo(ref layers[free]);
                layers[free].amount = MLToAmount(accepted, liquidToAdd.Density);
                Volume.UpdateLayers(true);
                return accepted;
            }

            // 3. Every slot full. Fold the incoming volume into whichever existing slot has the
            //    least volume, keeping THAT slot's identity - the incoming trace is what disappears,
            //    not what was already tracked. Volume is conserved either way.
            int smallest = 0;
            for (int i = 1; i < layers.Length; i++)
                if (layers[i].amount < layers[smallest].amount)
                    smallest = i;

            if (!warnedAboutLayerOverflow)
            {
                warnedAboutLayerOverflow = true;
                Debug.LogWarning($"{name}: all {layers.Length} liquid layer slots are full; merging " +
                    $"{accepted:0.#} mL of '{liquidToAdd.DisplayName}' into slot {smallest} " +
                    $"('{(slotLiquid[smallest] != null ? slotLiquid[smallest].DisplayName : "empty")}') " +
                    "instead of tracking it separately.", this);
            }

            layers[smallest].amount += MLToAmount(accepted, layers[smallest].density);
            Volume.UpdateLayers(true);
            return accepted;
        }

        public float RemoveML(float millilitres)
        {
            if (millilitres <= 0f)
                return 0f;

            Initialise();

            if (!layeredMode)
            {
                float removed = Mathf.Min(millilitres, contentsML);
                if (removed <= 0f)
                    return 0f;

                contentsML -= removed;
                PushLevelToShader();
                return removed;
            }

            float remaining = millilitres;
            float removedTotal = 0f;
            int guard = LiquidVolume.MAX_LAYERS;
            while (remaining > 0.0001f && guard-- > 0)
            {
                float got = RemoveTopML(remaining, out _);
                if (got <= 0f)
                    break;
                removedTotal += got;
                remaining -= got;
            }
            return removedTotal;
        }

        /// <summary>
        /// Removes up to <paramref name="millilitres"/> from the layer exposed at the top of the
        /// stack, never reaching into the layer below in one call - the caller loops if it needs
        /// more than one layer's worth. Returns the millilitres actually removed and the liquid
        /// that identifies that layer, for colouring the stream.
        ///
        /// A layer here is already a finished mixture: liquids of one family are stirred together
        /// into a single slot on the way in (see AddLayeredML), so there is no group of sibling
        /// layers to drain proportionally.
        /// </summary>
        public float RemoveTopML(float millilitres, out SpillLiquidDefinition liquid)
        {
            liquid = null;
            if (millilitres <= 0f || !layeredMode || Volume == null)
                return 0f;

            EnsureLayeredArrays();
            LiquidVolume.LiquidLayer[] layers = Volume.liquidLayers;
            if (layers == null)
                return 0f;

            int top = TopSlotIndex();
            if (top < 0)
                return 0f;

            float density = layers[top].density;
            float removedAmount = Mathf.Min(MLToAmount(millilitres, density), layers[top].amount);
            if (removedAmount <= 0f)
                return 0f;

            liquid = slotLiquid[top];
            layers[top].amount -= removedAmount;
            if (layers[top].amount <= AmountEpsilon)
                ReleaseSlot(top);

            Volume.UpdateLayers(true);
            return AmountToML(removedAmount, density);
        }

        // ------------------------------------------------------------------ layered helpers

        void EnsureLayeredArrays()
        {
            if (slotLiquid == null || slotLiquid.Length != LiquidVolume.MAX_LAYERS)
                slotLiquid = new SpillLiquidDefinition[LiquidVolume.MAX_LAYERS];
        }

        /// <summary>
        /// Writes <see cref="initialContents"/> into LiquidVolumePro's layer stack, switching
        /// <c>Volume.detail</c> to a Multiple variant if it is not already one. Idempotent - safe
        /// to call repeatedly (an editor "Bake Now" button, OnValidate, or Initialise all call this).
        /// </summary>
        public void BakeInitialContents()
        {
            if (Volume == null)
                return;

            EnsureLayeredArrays();

            DETAIL current = Volume.detail;
            DETAIL target = current == DETAIL.Simple || current == DETAIL.Default ? DETAIL.Multiple
                : current == DETAIL.SimpleNoFlask || current == DETAIL.DefaultNoFlask ? DETAIL.MultipleNoFlask
                : current;
            if (Volume.detail != target)
                Volume.detail = target;

            layeredMode = Volume.detail.isMultiple();
            if (!layeredMode)
                return;

            // Allocate exactly MAX_LAYERS slots once. LiquidVolume.UpdateLayersNow() resets any
            // slot at/after the array's previous length to random SetDefaults() values the moment
            // the array grows, so growing it more than this one time must never happen.
            LiquidVolume.LiquidLayer[] layers = Volume.liquidLayers;
            if (layers == null || layers.Length != LiquidVolume.MAX_LAYERS)
            {
                layers = new LiquidVolume.LiquidLayer[LiquidVolume.MAX_LAYERS];
                Volume.liquidLayers = layers;
                layers = Volume.liquidLayers;
            }

            for (int i = 0; i < layers.Length; i++)
            {
                slotLiquid[i] = null;
                layers[i].amount = 0f;
                layers[i].miscible = false;
                layers[i].density = FreeSlotDensity(i);
            }

            // Routed through AddLayeredML rather than written slot by slot, so authoring two
            // charges of the same family in the list produces the same single blended layer that
            // pouring them one into the other would.
            for (int i = 0; i < initialContents.Count; i++)
            {
                LayerCharge charge = initialContents[i];
                if (charge.liquid == null || charge.millilitres <= 0f)
                    continue;

                AddLayeredCore(charge.millilitres, charge.liquid);
            }

            Volume.UpdateLayers(true);
        }

        /// <summary>
        /// Takes ownership of layers that were authored straight onto the LiquidVolume, with no
        /// <see cref="SpillLiquidDefinition"/> behind them. They stay renderable and pourable - the slot
        /// table just has no liquid identity to report for them, so a pour from such a flask
        /// carries colour but no catalogued liquid.
        /// </summary>
        void AdoptExistingLayers()
        {
            LiquidVolume.LiquidLayer[] layers = Volume.liquidLayers;
            if (layers == null)
                return;

            for (int i = 0; i < layers.Length && i < slotLiquid.Length; i++)
                slotLiquid[i] = null;
        }

        void ReleaseSlot(int index)
        {
            slotLiquid[index] = null;
            LiquidVolume.LiquidLayer[] layers = Volume.liquidLayers;
            layers[index].amount = 0f;
            layers[index].miscible = false;
            layers[index].density = FreeSlotDensity(index);
        }

        /// <summary>
        /// Occupied slot holding a liquid of the same mixing family, or -1. A null category only
        /// matches another null category, so uncatalogued liquids never silently merge into a
        /// catalogued one.
        /// </summary>
        int FindSlotWithCategory(SpillLiquidCategory category)
        {
            LiquidVolume.LiquidLayer[] layers = Volume.liquidLayers;
            for (int i = 0; i < slotLiquid.Length; i++)
            {
                if (slotLiquid[i] == null || layers[i].amount <= AmountEpsilon)
                    continue;
                if (slotLiquid[i].Category == category)
                    return i;
            }
            return -1;
        }

        int FindFreeSlot()
        {
            for (int i = 0; i < slotLiquid.Length; i++)
                if (slotLiquid[i] == null)
                    return i;
            return -1;
        }

        /// <summary>Index of the occupied slot with the lowest density (LiquidVolumePro floats
        /// lower density on top - see LiquidVolume.UpdateLayersNow), or -1 if the flask is empty.</summary>
        int TopSlotIndex()
        {
            if (!layeredMode || Volume == null || Volume.liquidLayers == null || slotLiquid == null)
                return -1;

            LiquidVolume.LiquidLayer[] layers = Volume.liquidLayers;
            int best = -1;
            float bestDensity = float.MaxValue;
            for (int i = 0; i < layers.Length; i++)
            {
                if (slotLiquid[i] == null || layers[i].amount <= AmountEpsilon)
                    continue;
                if (layers[i].density < bestDensity)
                {
                    bestDensity = layers[i].density;
                    best = i;
                }
            }
            return best;
        }

        Color TopLayerColor()
        {
            int slot = TopSlotIndex();
            if (slot < 0)
                return Color.white;

            LiquidVolume.LiquidLayer layer = Volume.liquidLayers[slot];
            // currentColor is what LiquidVolumePro is actually rendering right now (it eases
            // toward mixedColor over adjustmentSpeed rather than snapping), so reading it keeps
            // the stream/splash colour in sync with what is on screen instead of jumping ahead to
            // a blend that has not finished animating in yet.
            if (layer.currentColor != default)
                return layer.currentColor;
            if (layer.mixedColor.a > 0.0001f)
                return layer.mixedColor;
            return layer.color;
        }

        float LayeredContentsML()
        {
            if (Volume == null || Volume.liquidLayers == null || slotLiquid == null)
                return 0f;

            LiquidVolume.LiquidLayer[] layers = Volume.liquidLayers;
            float total = 0f;
            for (int i = 0; i < layers.Length; i++)
                if (slotLiquid[i] != null)
                    total += AmountToML(layers[i].amount, layers[i].density);
            return total;
        }

        static float FreeSlotDensity(int slotIndex) => 1000f + slotIndex;

        /// <summary>mL -> LiquidVolumePro "amount" units (amount/density is what contributes to
        /// fill level, so amount = mL * density * fullLevel / capacityML).</summary>
        float MLToAmount(float millilitres, float density) => millilitres * density * layeredFullLevel / capacityML;

        float AmountToML(float amount, float density) => density > 0f ? amount * capacityML / (density * layeredFullLevel) : 0f;

        // ------------------------------------------------------------------ helpers

        /// <summary>Sets the contents directly, in millilitres. Clamped to the capacity. Single mode only.</summary>
        public void SetContentsML(float millilitres)
        {
            Initialise();
            if (layeredMode)
            {
                Debug.LogWarning($"{name}: SetContentsML has no effect in layered mode - use AddLayeredML/RemoveTopML.", this);
                return;
            }

            contentsML = Mathf.Clamp(millilitres, 0f, capacityML);
            PushLevelToShader();
        }

        /// <summary>
        /// Flow rate produced by the current tilt. Zero while upright, ramping up past the spill
        /// angle, and fading out as the liquid runs low.
        /// </summary>
        public float EvaluateTiltFlowMLPerSecond()
        {
            float contents = ContentsML;
            if (contents <= 0f)
                return 0f;

            float tilt = TiltDegrees;
            if (tilt <= spillTiltDegrees)
                return 0f;

            float tiltFactor = Mathf.Clamp01(Mathf.InverseLerp(spillTiltDegrees, fullTiltDegrees, tilt));
            tiltFactor = tiltFactor * tiltFactor * (3f - 2f * tiltFactor);

            // The head of liquid above the lip drives the speed. Nearly empty flasks dribble.
            float head = Mathf.Clamp01(contents / Mathf.Max(1f, capacityML * 0.35f));
            return maxFlowMLPerSecond * tiltFactor * Mathf.Sqrt(head);
        }

        float NormalisedToML(float level)
        {
            float normalised = Mathf.InverseLerp(emptyLevel, fullLevel, level);
            return Mathf.Clamp01(normalised) * capacityML;
        }

        float MLToNormalised(float millilitres)
        {
            float normalised = capacityML <= 0f ? 0f : Mathf.Clamp01(millilitres / capacityML);
            return Mathf.Lerp(emptyLevel, fullLevel, normalised);
        }

        void PushLevelToShader()
        {
            if (Volume == null || layeredMode)
                return;

            // LiquidVolume.level early-outs on an unchanged value, so this is cheap per frame.
            // In layered mode the setter refuses the write anyway (level is derived from the
            // layers) and logs a warning every time - the layeredMode guard above is load-bearing.
            Volume.level = MLToNormalised(contentsML);
        }

        void BlendColor(Color incoming, float incomingML)
        {
            if (Volume == null || incomingML <= 0f)
                return;

            float total = contentsML + incomingML;
            if (total <= 0f)
                return;

            float weight = incomingML / total;
            if (weight <= 0.001f)
                return;

            Volume.liquidColor1 = Color.Lerp(Volume.liquidColor1, incoming, weight);
            Volume.liquidColor2 = Color.Lerp(Volume.liquidColor2, incoming * 0.75f, weight);
        }

        static float MaxAbsoluteScale(Vector3 scale)
        {
            return Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)));
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.85f, 1f, 0.9f);
            Vector3 centre = PortCentreWorld;
            float radius = PortRadius;
            const int steps = 24;
            Vector3 previous = centre + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= steps; i++)
            {
                float angle = i / (float)steps * Mathf.PI * 2f;
                Vector3 next = centre + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                Gizmos.DrawLine(previous, next);
                previous = next;
            }

            if (lip != null)
            {
                Gizmos.color = new Color(1f, 0.75f, 0.2f, 0.9f);
                Gizmos.DrawSphere(lip.position, 0.008f);
            }
        }
    }
}
