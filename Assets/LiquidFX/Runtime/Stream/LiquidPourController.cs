using LiquidVolumeFX;
using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// Ties one source of liquid to whatever is underneath it.
    ///
    /// The important property of this component is that volume is conserved without any physics:
    /// every frame it removes millilitres from the source, puts them in a flight queue with the
    /// real fall time, and credits them to the receiver when they land. Particles are decoration
    /// on top of that ledger, never the mechanism, which is what the LiquidVolumePro pouring demo
    /// gets wrong for mobile (it counts OnParticleCollision callbacks).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LiquidPourController : MonoBehaviour
    {
        public enum SourceMode
        {
            /// <summary>Flow comes from tilting a flask past its spill angle.</summary>
            FlaskTilt,

            /// <summary>Flow comes from a valve opening, with a supply that never runs out.</summary>
            Valve
        }

        [Header("Source")]
        [SerializeField] SourceMode sourceMode = SourceMode.FlaskTilt;

        [SerializeField] FlaskVolume sourceFlask;

        [Tooltip("Where the liquid leaves. For a faucet this is the tip of the spout.")]
        [SerializeField] Transform lip;

        [Header("Valve Source")]
        [Tooltip("Faucet power, 0 (closed) to 10 (wide open). Matches the 0-10 dial from the earlier prototype.")]
        [SerializeField, Range(0f, 10f)] float intensity;

        [SerializeField, Min(1f)] float valveMaxFlowMLPerSecond = 130f;

        [Tooltip("Preferred over valveLiquidColor when set - lets a faucet pour a real cataloged liquid.")]
        [SerializeField] LiquidDefinition valveLiquid;

        [SerializeField] Color valveLiquidColor = new Color(0.62f, 0.86f, 0.95f, 1f);

        [Header("Exit")]
        [Tooltip("Direction the liquid leaves in, in the lip's local space.")]
        [SerializeField] Vector3 exitDirectionLocal = Vector3.down;

        [Tooltip("How much the lip's own facing pushes the stream sideways. Gravity owns the rest.")]
        [SerializeField, Range(0f, 1f)] float lipSidewaysInfluence = 0.25f;

        [SerializeField, Min(0f)] float minimumExitSpeed = 0.25f;

        [SerializeField, Min(0f)] float maximumExitSpeed = 1.15f;

        [Header("Visuals")]
        [SerializeField] LiquidStreamRibbon ribbon;

        [SerializeField] LiquidImpactFX impactFX;

        [Tooltip("Emitted for a moment after the flow closes, so the lip finishes dripping.")]
        [SerializeField] ParticleSystem lipDrips;

        [SerializeField, Min(0f)] float dripSeconds = 1.1f;

        [Header("Receiving")]
        [Tooltip("Leave empty to pick the receiver automatically from whatever is under the stream.")]
        [SerializeField] MonoBehaviour explicitReceiver;

        [Tooltip("How often the receiver under the stream is re-evaluated, in seconds.")]
        [SerializeField, Min(0f)] float receiverRefreshInterval = 0.15f;

        [Tooltip("Surfaces a missed stream can puddle on.")]
        [SerializeField] LayerMask spillMask = ~0;

        readonly LiquidFlightQueue flight = new LiquidFlightQueue(48);

        ILiquidContainer receiver;
        ILiquidContainer explicitReceiverCache;
        float receiverRefreshTimer;
        float dripTimer;
        bool wasFlowing;

        public float FlowMLPerSecond { get; private set; }
        public float InFlightML => flight.InFlightML;
        public ILiquidContainer Receiver => receiver;
        public bool IsFlowing => FlowMLPerSecond > 0.01f;

        /// <summary>Faucet power, 0 (closed) to 10 (wide open). Only used in <see cref="SourceMode.Valve"/>.</summary>
        public float Intensity
        {
            get => intensity;
            set => intensity = Mathf.Clamp(value, 0f, 10f);
        }

        /// <summary>Same control normalised to 0..1, kept for callers written against the older API.</summary>
        public float ValveOpen
        {
            get => intensity * 0.1f;
            set => intensity = Mathf.Clamp01(value) * 10f;
        }

        void OnEnable()
        {
            flight.Clear();
            ResolveExplicitReceiver();
        }

        void OnDisable()
        {
            // Whatever was still falling never lands: hand it to the receiver so no volume vanishes.
            float stranded = flight.DrainAll();
            if (stranded > 0f && receiver != null)
                receiver.AddML(stranded, LiquidColor);

            if (ribbon != null)
                ribbon.Clear();
            if (impactFX != null)
                impactFX.SetContinuous(transform.position, Vector3.up, 0f, 0f, Color.white);
        }

        void OnValidate()
        {
            maximumExitSpeed = Mathf.Max(maximumExitSpeed, minimumExitSpeed);
            ResolveExplicitReceiver();
        }

        Transform Lip => lip != null ? lip : (sourceFlask != null ? sourceFlask.Lip : transform);

        Color LiquidColor => sourceMode == SourceMode.FlaskTilt && sourceFlask != null
            ? TemperFlaskColor(sourceFlask.LiquidColor)
            : (valveLiquid != null ? TemperFlaskColor(valveLiquid.Color) : valveLiquidColor);

        /// <summary>
        /// The cataloged liquid currently leaving the lip, if any. Null for a single-mode flask or
        /// a valve with no LiquidDefinition assigned - both fall back to the flat colour path,
        /// exactly as before this identity existed.
        /// </summary>
        LiquidDefinition CurrentLiquid => sourceMode == SourceMode.FlaskTilt && sourceFlask != null
            ? sourceFlask.TopLiquid
            : valveLiquid;

        /// <summary>
        /// LiquidVolumePro flasks express colour the way a volumetric absorption material does:
        /// alpha is how strongly the liquid tints light passing through it, not how opaque a flat
        /// surface is. A near-water flask can have a fully saturated hue at alpha 0.1 and still
        /// look almost clear once rendered. The stream ribbon, impact particles and floor puddle
        /// all treat colour as a flat multiply instead, so passing that hue through unmodified
        /// paints them in solid dye. Blending toward white by the flask's own alpha converts
        /// "how strong is the tint" into "how much of the pure hue survives" once, here, so every
        /// consumer downstream gets a colour that already looks like the right amount of liquid
        /// rather than each shader inventing its own fudge factor.
        /// </summary>
        static Color TemperFlaskColor(Color raw)
        {
            float strength = Mathf.Clamp01(raw.a);
            return Color.Lerp(Color.white, new Color(raw.r, raw.g, raw.b, 1f), strength);
        }

        float MaximumFlow => sourceMode == SourceMode.FlaskTilt && sourceFlask != null
            ? sourceFlask.MaxFlowMLPerSecond
            : valveMaxFlowMLPerSecond;

        void Update()
        {
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
                return;

            FlowMLPerSecond = EvaluateFlow();
            float normalisedFlow = Mathf.Clamp01(FlowMLPerSecond / Mathf.Max(1f, MaximumFlow));

            Transform lipTransform = Lip;
            Vector3 exitDirection = ResolveExitDirection(lipTransform);

            float exitSpeed = Mathf.Lerp(minimumExitSpeed, maximumExitSpeed, normalisedFlow);
            Vector3 exitVelocity = exitDirection * exitSpeed;

            // Receiver first: the ribbon needs to know which plane to land on this frame, not the
            // one it landed on last frame, or it lags behind a rising water level.
            RefreshReceiver(deltaTime);
            DriveRibbon(lipTransform.position, exitVelocity, normalisedFlow);
            MoveLiquid(deltaTime);
            DriveImpact(normalisedFlow);
            DriveDrips(deltaTime);

            wasFlowing = IsFlowing;
        }

        // ------------------------------------------------------------------ flow

        float EvaluateFlow()
        {
            if (sourceMode == SourceMode.Valve)
                return valveMaxFlowMLPerSecond * (Mathf.Clamp(intensity, 0f, 10f) * 0.1f);

            return sourceFlask != null ? sourceFlask.EvaluateTiltFlowMLPerSecond() : 0f;
        }

        void DriveRibbon(Vector3 lipPosition, Vector3 exitVelocity, float normalisedFlow)
        {
            if (ribbon == null)
                return;

            if (receiver != null)
                ribbon.SetTargetPlane(receiver.SurfaceWorldY);
            else
                ribbon.ClearTargetPlane();

            if (IsFlowing)
                ribbon.SetFlow(lipPosition, exitVelocity, FlowMLPerSecond, LiquidColor);
            else
                ribbon.Stop();
        }

        // ------------------------------------------------------------------ receiver

        void ResolveExplicitReceiver()
        {
            explicitReceiverCache = explicitReceiver as ILiquidContainer;
            if (explicitReceiver != null && explicitReceiverCache == null)
                Debug.LogWarning($"{name}: explicit receiver does not implement ILiquidContainer.", this);
        }

        void RefreshReceiver(float deltaTime)
        {
            if (explicitReceiverCache != null)
            {
                receiver = explicitReceiverCache;
                return;
            }

            receiverRefreshTimer -= deltaTime;
            if (receiverRefreshTimer > 0f && receiver != null)
                return;

            receiverRefreshTimer = receiverRefreshInterval;

            Vector3 probe = ribbon != null && ribbon.IsVisible ? ribbon.ImpactPointWorld : Lip.position;
            probe.y = Lip.position.y;
            receiver = LiquidContainerRegistry.FindReceiverUnder(probe, Lip.position.y, sourceFlask);
        }

        // ------------------------------------------------------------------ volume ledger

        void MoveLiquid(float deltaTime)
        {
            float requested = FlowMLPerSecond * deltaTime;
            float travelSeconds = EstimateTravelSeconds();

            // 1. take liquid out of the source, putting it in the air with the real travel time.
            if (sourceMode == SourceMode.FlaskTilt && sourceFlask != null && sourceFlask.IsLayered)
            {
                // A layered flask can straddle two liquids within one frame's worth of volume (the
                // top layer runs out mid-request): loop so each liquid gets its own flight packet
                // instead of the transition being silently rounded into whichever happened first.
                float remaining = requested;
                int guard = LiquidVolume.MAX_LAYERS;
                while (remaining > 0.0001f && guard-- > 0)
                {
                    float got = sourceFlask.RemoveTopML(remaining, out LiquidDefinition liquid);
                    if (got <= 0f)
                        break;
                    flight.Enqueue(got, Time.time + travelSeconds, liquid);
                    remaining -= got;
                }
            }
            else
            {
                float leaving = requested;
                if (sourceMode == SourceMode.FlaskTilt && sourceFlask != null)
                    leaving = sourceFlask.RemoveML(requested);

                if (leaving > 0f)
                    flight.Enqueue(leaving, Time.time + travelSeconds, CurrentLiquid);
            }

            // 2. credit whatever has landed - a single frame can land more than one packet.
            while (flight.TryDequeueArrived(Time.time, out float landedML, out LiquidDefinition landedLiquid))
            {
                Color color = landedLiquid != null ? TemperFlaskColor(landedLiquid.Color) : LiquidColor;

                float accepted = landedLiquid != null && receiver is FlaskVolume flaskReceiver && flaskReceiver.IsLayered
                    ? flaskReceiver.AddLayeredML(landedML, landedLiquid)
                    : receiver?.AddML(landedML, color) ?? 0f;

                float overflow = landedML - accepted;
                if (overflow > 0.0001f)
                    SpillOverflow(overflow, color);
            }
        }

        /// <summary>
        /// Fall time from the lip down to the receiving surface. Solved from the geometry rather
        /// than read back from the ribbon, so it is correct on the very first frame of a pour
        /// instead of lagging one frame behind the mesh.
        /// </summary>
        /// <summary>
        /// Which way the liquid leaves the lip.
        ///
        /// Taking the lip's local down straight from the transform works for a faucet, whose spout
        /// never rotates, but it aims a tilted flask sideways: at 70 degrees of tilt the "down" of
        /// the lip is nearly horizontal, and a 0.5 m/s sideways launch drifts the stream 7 cm over
        /// a 0.1 m fall, which is wider than a 250 mL beaker's mouth. A real pour is gravity
        /// dominated within a centimetre of the lip, so only the horizontal part of the lip's
        /// facing survives, and only at a fraction of its strength.
        /// </summary>
        Vector3 ResolveExitDirection(Transform lipTransform)
        {
            Vector3 lipFacing = lipTransform.TransformDirection(exitDirectionLocal);
            if (lipFacing.sqrMagnitude < 0.0001f)
                return Vector3.down;

            lipFacing.Normalize();
            Vector3 sideways = Vector3.ProjectOnPlane(lipFacing, Vector3.up) * lipSidewaysInfluence;
            Vector3 direction = Vector3.down + sideways;
            return direction.sqrMagnitude < 0.0001f ? Vector3.down : direction.normalized;
        }

        float EstimateTravelSeconds()
        {
            Transform lipTransform = Lip;
            float targetY = receiver?.SurfaceWorldY
                ?? (ribbon != null && ribbon.IsVisible ? ribbon.ImpactPointWorld.y : lipTransform.position.y);

            float drop = lipTransform.position.y - targetY;
            if (drop <= 0f)
                return 0f;

            float exitVerticalSpeed = ResolveExitDirection(lipTransform).y
                * Mathf.Lerp(minimumExitSpeed, maximumExitSpeed, Mathf.Clamp01(FlowMLPerSecond / Mathf.Max(1f, MaximumFlow)));

            return LiquidFXRuntime.FallTime(drop, exitVerticalSpeed);
        }

        void SpillOverflow(float millilitres, Color color)
        {
            Vector3 point = ribbon != null && ribbon.IsVisible ? ribbon.ImpactPointWorld : Lip.position;
            Vector3 normal = Vector3.up;

            // Find the solid surface the overflow runs down onto.
            if (Physics.Raycast(point + Vector3.up * 0.05f, Vector3.down, out RaycastHit hit, 3f, spillMask, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                normal = hit.normal;
            }

            LiquidSpillManager.Spill(point, normal, millilitres, color);
        }

        // ------------------------------------------------------------------ impact

        void DriveImpact(float normalisedFlow)
        {
            if (ribbon == null)
                return;

            bool landed = IsFlowing && ribbon.IsVisible && ribbon.HeadHasLanded;

            if (impactFX != null)
            {
                if (landed)
                {
                    impactFX.SetContinuous(
                        ribbon.ImpactPointWorld,
                        Vector3.up,
                        normalisedFlow,
                        ribbon.ImpactSpeed,
                        LiquidColor);
                }
                else
                {
                    impactFX.SetContinuous(ribbon.ImpactPointWorld, Vector3.up, 0f, 0f, LiquidColor);
                }
            }

            if (receiver is LiquidSurface surface)
            {
                if (landed)
                {
                    float radius = Mathf.Max(0.012f, ribbon.ImpactRadius * 3.5f);
                    surface.SetContinuousRippleWorld(ribbon.ImpactPointWorld, normalisedFlow, radius);
                }
                else
                {
                    surface.ClearContinuousRipple();
                }
            }
        }

        void DriveDrips(float deltaTime)
        {
            if (lipDrips == null)
                return;

            if (wasFlowing && !IsFlowing)
                dripTimer = dripSeconds;

            if (dripTimer <= 0f)
            {
                if (lipDrips.isEmitting)
                    lipDrips.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                return;
            }

            dripTimer -= deltaTime;
            lipDrips.transform.position = Lip.position;
            if (!lipDrips.isPlaying)
                lipDrips.Play(false);

            var emission = lipDrips.emission;
            emission.enabled = true;
            emission.rateOverTime = Mathf.Lerp(0f, 6f, Mathf.Clamp01(dripTimer / Mathf.Max(0.01f, dripSeconds)));
        }
    }
}
