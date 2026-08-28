using System.Collections.Generic;
using LabLiquidVR;
using UnityEngine;
using UnityEngine.Rendering;

namespace LabSpill
{
    /// <summary>
    /// Fogareiro enxuto da cena limpa. A zona define quem aquece; toda a
    /// aparencia da ebulicao vem do LiquidConfig do proprio liquido.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpillBurner : MonoBehaviour
    {
        [Header("Estado")]
        [SerializeField] bool lit = true;
        [SerializeField] GameObject flame;
        [SerializeField] BoxCollider heatingZone;

        [Header("Temperatura")]
        [SerializeField, Min(0f)] float heatingRateCPerSecond = 12f;
        [SerializeField] float maximumTemperatureC = 130f;
        [SerializeField, Min(0f)] float coolingRateCPerSecond = 1.5f;
        [SerializeField] float ambientTemperatureC = 22f;

        [Header("Texturas dos efeitos")]
        [SerializeField] Texture2D steamTexture;

        public bool IsLit => lit;
        public int LiquidsInHeatZone => m_touching.Count;

        readonly Collider[] m_overlap = new Collider[64];
        readonly HashSet<SpillLiquidContainer> m_touching = new HashSet<SpillLiquidContainer>();
        readonly HashSet<SpillLiquidContainer> m_known = new HashSet<SpillLiquidContainer>();
        readonly Dictionary<SpillLiquidContainer, ThermalEffects> m_effects =
            new Dictionary<SpillLiquidContainer, ThermalEffects>();

        // Frascos novos. A temperatura mora aqui e nao no frasco porque calor e assunto
        // do fogareiro: o frasco nao precisa saber que existe fogo no mundo. As bolhas
        // sao as nativas do LiquidVolumePro, entao este caminho nao tem ParticleSystem
        // dentro do vidro nem colisao de bolha com a parede - some tudo o que a metade
        // antiga precisava para faze-las por conta propria.
        readonly HashSet<SpillFlaskVolume> m_flasksTouching = new HashSet<SpillFlaskVolume>();
        readonly Dictionary<SpillFlaskVolume, float> m_flaskTemperature =
            new Dictionary<SpillFlaskVolume, float>();
        readonly List<KeyValuePair<SpillFlaskVolume, float>> m_flaskScratch =
            new List<KeyValuePair<SpillFlaskVolume, float>>();

        Material m_bubbleMaterial;
        Material m_steamMaterial;

        sealed class ThermalEffects
        {
            public ParticleSystem bubbles;
            public ParticleSystem steam;
            public ParticleSystem.Particle[] bubbleBuffer;
            public int constraintTick;
        }

        void Reset() => FindReferences();

        void Awake()
        {
            FindReferences();
            if (heatingZone != null) heatingZone.isTrigger = true;
            ApplyState();
        }

        void OnValidate()
        {
            heatingRateCPerSecond = Mathf.Max(0f, heatingRateCPerSecond);
            coolingRateCPerSecond = Mathf.Max(0f, coolingRateCPerSecond);
            maximumTemperatureC = Mathf.Max(ambientTemperatureC, maximumTemperatureC);
            FindReferences();
            if (!Application.isPlaying) ApplyState();
        }

        public void SetLit(bool value)
        {
            lit = value;
            ApplyState();
        }

        void FindReferences()
        {
            if (flame == null)
            {
                Transform[] children = GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < children.Length; i++)
                {
                    string childName = children[i].name.ToLowerInvariant();
                    if (!childName.Contains("fire") && !childName.Contains("flame") &&
                        !childName.Contains("fogo")) continue;
                    flame = children[i].gameObject;
                    break;
                }
            }

            if (heatingZone == null)
            {
                BoxCollider[] colliders = GetComponentsInChildren<BoxCollider>(true);
                for (int i = 0; i < colliders.Length; i++)
                    if (colliders[i].isTrigger) { heatingZone = colliders[i]; break; }
                if (heatingZone == null && colliders.Length > 0) heatingZone = colliders[0];
            }
        }

        void ApplyState()
        {
            if (flame != null && flame != gameObject) flame.SetActive(lit);
        }

        void FixedUpdate()
        {
            FindLiquidsInZone();
            float dt = Time.fixedDeltaTime;
            UpdateFlasks(dt);
            foreach (SpillLiquidContainer liquid in m_known)
            {
                if (liquid == null) continue;
                bool heating = lit && m_touching.Contains(liquid) && liquid.currentVolumeML > 0.0001f;
                liquid.currentTemperatureC = Mathf.MoveTowards(
                    liquid.currentTemperatureC,
                    heating ? maximumTemperatureC : ambientTemperatureC,
                    (heating ? heatingRateCPerSecond : coolingRateCPerSecond) * dt);
                UpdateBoiling(liquid);
            }
        }

        void FindLiquidsInZone()
        {
            m_touching.Clear();
            m_flasksTouching.Clear();
            if (heatingZone == null || !heatingZone.enabled) return;

            Vector3 scale = heatingZone.transform.lossyScale;
            Vector3 halfExtents = Vector3.Scale(
                heatingZone.size * 0.5f,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            int count = Physics.OverlapBoxNonAlloc(
                heatingZone.transform.TransformPoint(heatingZone.center),
                halfExtents,
                m_overlap,
                heatingZone.transform.rotation,
                ~0,
                QueryTriggerInteraction.Collide);

            for (int i = 0; i < count; i++)
            {
                SpillFlaskVolume flask = ResolveFlask(m_overlap[i]);
                if (flask != null && flask.ContentsML > 0.0001f)
                {
                    m_flasksTouching.Add(flask);
                    if (!m_flaskTemperature.ContainsKey(flask))
                        m_flaskTemperature[flask] = ambientTemperatureC;
                    continue;
                }

                SpillLiquidContainer liquid = ResolveLiquid(m_overlap[i]);
                if (liquid == null || liquid.currentVolumeML <= 0.0001f) continue;
                m_touching.Add(liquid);
                m_known.Add(liquid);
            }
        }

        static SpillFlaskVolume ResolveFlask(Collider collider)
        {
            if (collider == null) return null;
            SpillFlaskVolume flask = collider.GetComponentInParent<SpillFlaskVolume>();
            if (flask != null) return flask;
            return collider.GetComponentInChildren<SpillFlaskVolume>();
        }

        /// <summary>
        /// Aquece, resfria e ferve os frascos novos. Bem mais curto que o caminho antigo
        /// porque a fervura visivel e nativa do LiquidVolumePro: aqui so se calcula a
        /// intensidade e se entrega ao frasco.
        /// </summary>
        void UpdateFlasks(float dt)
        {
            // Copia antes de percorrer: o corpo escreve no proprio dicionario.
            m_flaskScratch.Clear();
            foreach (var pair in m_flaskTemperature) m_flaskScratch.Add(pair);

            for (int i = 0; i < m_flaskScratch.Count; i++)
            {
                SpillFlaskVolume flask = m_flaskScratch[i].Key;
                if (flask == null)
                {
                    m_flaskTemperature.Remove(flask);
                    continue;
                }

                bool heating = lit && m_flasksTouching.Contains(flask) && flask.ContentsML > 0.0001f;
                float temperature = Mathf.MoveTowards(
                    m_flaskScratch[i].Value,
                    heating ? maximumTemperatureC : ambientTemperatureC,
                    (heating ? heatingRateCPerSecond : coolingRateCPerSecond) * dt);
                m_flaskTemperature[flask] = temperature;

                SpillLiquidDefinition top = flask.TopLiquid;
                float boilingPoint = top != null ? top.BoilingPointC : 100f;

                float intensity = 0f;
                if (flask.ContentsML > 0.0001f && temperature >= boilingPoint)
                    intensity = Mathf.Clamp01(
                        Mathf.InverseLerp(boilingPoint, maximumTemperatureC, temperature));

                flask.SetBoiling(intensity);
            }
        }

        static SpillLiquidContainer ResolveLiquid(Collider collider)
        {
            if (collider == null) return null;
            SpillLiquidContainer liquid = collider.GetComponentInParent<SpillLiquidContainer>();
            if (liquid != null) return liquid;
            liquid = collider.GetComponentInChildren<SpillLiquidContainer>();
            if (liquid != null) return liquid;
            return collider.transform.root.GetComponentInChildren<SpillLiquidContainer>();
        }

        void UpdateBoiling(SpillLiquidContainer liquid)
        {
            float intensity = 0f;
            if (liquid.currentVolumeML > 0.0001f &&
                liquid.currentTemperatureC >= liquid.BoilingPointC)
            {
                float progress = Mathf.InverseLerp(
                    liquid.BoilingPointC,
                    Mathf.Max(liquid.BoilingPointC + 0.01f, maximumTemperatureC),
                    liquid.currentTemperatureC);
                intensity = Mathf.Lerp(0.08f, 1f, progress);
            }

            liquid.SetBoilingIntensity(intensity);
            if (intensity <= 0f)
            {
                if (m_effects.TryGetValue(liquid, out ThermalEffects inactive))
                    StopEffects(inactive);
                return;
            }

            ThermalEffects effects = GetEffects(liquid);
            UpdateBubbles(liquid, effects.bubbles, intensity);
            ConstrainBubbles(liquid, effects);
            float steamStart = liquid.Config != null ? liquid.Config.steamStartIntensity : 0.65f;
            UpdateSteam(liquid, effects.steam,
                Mathf.InverseLerp(steamStart, 1f, intensity));
        }

        ThermalEffects GetEffects(SpillLiquidContainer liquid)
        {
            if (m_effects.TryGetValue(liquid, out ThermalEffects effects)) return effects;
            effects = new ThermalEffects
            {
                bubbles = CreateBubbles(liquid),
                steam = CreateSteam(liquid),
                bubbleBuffer = new ParticleSystem.Particle[Mathf.Max(
                    8, liquid.Config != null ? liquid.Config.bubbleMaxParticles : 100)]
            };
            m_effects.Add(liquid, effects);
            return effects;
        }

        ParticleSystem CreateBubbles(SpillLiquidContainer liquid)
        {
            ParticleSystem particles = CreateParticleSystem("Boiling Bubbles", liquid);
            LiquidConfig config = liquid.Config;
            var main = particles.main;
            main.maxParticles = config != null ? config.bubbleMaxParticles : 100;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startSpeed = 0f;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = liquid.BubbleColor;

            var emission = particles.emission;
            emission.rateOverTime = 0f;
            var shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radiusThickness = 1f;
            var noise = particles.noise;
            noise.enabled = true;
            noise.separateAxes = true;
            noise.frequency = 1.3f;
            noise.scrollSpeed = 0.2f;

            ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortingOrder = 8;
            renderer.sortingFudge = 2f;
            renderer.sharedMaterial = GetParticleMaterial(true);
            return particles;
        }

        ParticleSystem CreateSteam(SpillLiquidContainer liquid)
        {
            ParticleSystem particles = CreateParticleSystem("Boiling Steam", liquid);
            var main = particles.main;
            main.maxParticles = 70;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.2f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.055f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var emission = particles.emission;
            emission.rateOverTime = 0f;
            var shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.015f;
            var velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(0f);
            velocity.y = new ParticleSystem.MinMaxCurve(0.085f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f);
            var noise = particles.noise;
            noise.enabled = true;
            noise.separateAxes = true;
            noise.strengthX = 0.025f;
            noise.strengthY = 0.01f;
            noise.strengthZ = 0.025f;
            noise.frequency = 0.45f;
            noise.scrollSpeed = 0.12f;

            var size = particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.25f),
                new Keyframe(0.35f, 0.85f),
                new Keyframe(1f, 1.35f)));
            var color = particles.colorOverLifetime;
            color.enabled = true;
            Gradient fade = new Gradient();
            fade.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.42f),
                    new GradientAlphaKey(0f, 1f)
                });
            color.color = fade;

            ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortingOrder = 7;
            renderer.sortingFudge = 1f;
            renderer.sharedMaterial = GetParticleMaterial(false);
            return particles;
        }

        static ParticleSystem CreateParticleSystem(string effectName,
            SpillLiquidContainer liquid)
        {
            GameObject effectObject = new GameObject(effectName) { hideFlags = HideFlags.DontSave };
            Transform flask = liquid.transform.parent != null ? liquid.transform.parent : liquid.transform;
            effectObject.transform.SetParent(flask, false);
            Vector3 scale = flask.lossyScale;
            effectObject.transform.localScale = new Vector3(
                1f / Mathf.Max(Mathf.Abs(scale.x), 1e-4f),
                1f / Mathf.Max(Mathf.Abs(scale.y), 1e-4f),
                1f / Mathf.Max(Mathf.Abs(scale.z), 1e-4f));
            ParticleSystem particles = effectObject.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            return particles;
        }

        Material GetParticleMaterial(bool bubble)
        {
            Material existing = bubble ? m_bubbleMaterial : m_steamMaterial;
            if (existing != null) return existing;
            Shader shader = Shader.Find(bubble
                ? "LabSpill/Bubble Particle"
                : "LabSpill/Steam Particle");
            bool customShader = shader != null;
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null) return null;

            Material material = new Material(shader)
            {
                name = bubble ? "Spill Bubbles (Runtime)" : "Spill Steam (Runtime)",
                hideFlags = HideFlags.DontSave,
                renderQueue = (int)RenderQueue.Transparent + (bubble ? 100 : 0)
            };
            Texture texture = bubble ? null : steamTexture;
            if (texture != null)
            {
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            }
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", Color.white);
            if (material.HasProperty("_Intensity")) material.SetFloat("_Intensity", bubble ? 1.15f : 0.85f);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_ZTest")) material.SetFloat("_ZTest",
                (float)(bubble ? CompareFunction.Always : CompareFunction.LessEqual));
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (!customShader) material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            if (bubble) m_bubbleMaterial = material; else m_steamMaterial = material;
            return material;
        }

        static void UpdateBubbles(SpillLiquidContainer liquid,
            ParticleSystem particles, float intensity)
        {
            Renderer liquidRenderer = liquid.GetComponent<Renderer>();
            if (liquidRenderer == null) return;
            LiquidConfig config = liquid.Config;
            Bounds bounds = liquidRenderer.bounds;
            Vector3 origin = liquid.BubbleOriginWorld;
            Vector3 up = liquid.ContainerUpWorld;
            float travelHeight = liquid.DistanceToSurfaceAlong(origin, up);
            if (travelHeight <= 0.001f)
            {
                particles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                return;
            }

            float riseSpeed = Mathf.Lerp(
                config != null ? config.bubbleRiseSpeedAtBoiling : 0.055f,
                config != null ? config.bubbleRiseSpeedAtMaximum : 0.14f,
                intensity);
            particles.transform.SetPositionAndRotation(origin,
                Quaternion.FromToRotation(Vector3.forward, up));
            float flaskWidth = Mathf.Max(0.01f,
                Mathf.Min(bounds.size.x, Mathf.Min(bounds.size.y, bounds.size.z)));

            var shape = particles.shape;
            shape.radius = flaskWidth * (config != null ? config.bubbleEmitterRadius : 0.09f) *
                (config != null ? config.bubbleWallInset : 0.88f);
            var main = particles.main;
            main.maxParticles = config != null ? config.bubbleMaxParticles : 100;
            main.startColor = liquid.BubbleColor;
            main.startLifetime = new ParticleSystem.MinMaxCurve(
                travelHeight / riseSpeed * 1.05f,
                travelHeight / riseSpeed * 1.45f);
            main.startSize = new ParticleSystem.MinMaxCurve(
                flaskWidth * Mathf.Lerp(
                    config != null ? config.bubbleMinSizeAtBoiling : 0.012f,
                    config != null ? config.bubbleMinSizeAtMaximum : 0.028f,
                    intensity),
                flaskWidth * Mathf.Lerp(
                    config != null ? config.bubbleMaxSizeAtBoiling : 0.027f,
                    config != null ? config.bubbleMaxSizeAtMaximum : 0.075f,
                    intensity));
            var velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            // Unity exige o mesmo modo de curva nos tres eixos. Como Z usa
            // TwoConstants para variar a subida, X/Y tambem ficam em TwoConstants.
            velocity.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            velocity.y = new ParticleSystem.MinMaxCurve(0f, 0f);
            velocity.z = new ParticleSystem.MinMaxCurve(riseSpeed * 0.75f, riseSpeed * 1.15f);
            var noise = particles.noise;
            float instability = flaskWidth * Mathf.Lerp(
                config != null ? config.bubbleInstabilityAtBoiling : 0.008f,
                config != null ? config.bubbleInstabilityAtMaximum : 0.11f,
                intensity);
            noise.strengthX = instability;
            noise.strengthY = instability * 0.32f;
            noise.strengthZ = instability;
            noise.frequency = Mathf.Lerp(0.55f, 2.4f, intensity);
            noise.scrollSpeed = Mathf.Lerp(0.08f, 0.42f, intensity);
            var emission = particles.emission;
            emission.rateOverTime = Mathf.Lerp(
                config != null ? config.bubbleRateAtBoiling : 4f,
                config != null ? config.bubbleRateAtMaximum : 48f,
                intensity);
            particles.GetComponent<ParticleSystemRenderer>().localBounds = new Bounds(
                new Vector3(0f, 0f, travelHeight * 0.5f),
                new Vector3(flaskWidth, flaskWidth, travelHeight + flaskWidth));
            if (!particles.isPlaying) particles.Play();
        }

        static void ConstrainBubbles(SpillLiquidContainer liquid, ThermalEffects effects)
        {
            ParticleSystem particles = effects.bubbles;
            if (particles == null || particles.particleCount == 0 ||
                (++effects.constraintTick & 1) != 0) return;
            int required = Mathf.Max(particles.main.maxParticles, particles.particleCount);
            if (effects.bubbleBuffer.Length < required)
                effects.bubbleBuffer = new ParticleSystem.Particle[required];

            int count = particles.GetParticles(effects.bubbleBuffer);
            Transform simulation = particles.transform;
            float scale = Mathf.Max(Mathf.Abs(simulation.lossyScale.x),
                Mathf.Max(Mathf.Abs(simulation.lossyScale.y), Mathf.Abs(simulation.lossyScale.z)));
            for (int i = 0; i < count; i++)
            {
                ParticleSystem.Particle particle = effects.bubbleBuffer[i];
                Vector3 position = simulation.TransformPoint(particle.position);
                Vector3 velocity = simulation.TransformDirection(particle.velocity);
                float radius = particle.GetCurrentSize(particles) * scale * 0.5f;
                if (!liquid.ConstrainBubble(ref position, ref velocity, radius))
                    particle.remainingLifetime = 0f;
                else
                {
                    particle.position = simulation.InverseTransformPoint(position);
                    particle.velocity = simulation.InverseTransformDirection(velocity);
                }
                effects.bubbleBuffer[i] = particle;
            }
            particles.SetParticles(effects.bubbleBuffer, count);
        }

        static void UpdateSteam(SpillLiquidContainer liquid,
            ParticleSystem particles, float intensity)
        {
            if (intensity <= 0f)
            {
                if (particles.isEmitting)
                    particles.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                return;
            }

            Renderer liquidRenderer = liquid.GetComponent<Renderer>();
            if (liquid.TryGetOpening(out Vector3 center, out _, out _))
                particles.transform.position = center;
            else if (liquidRenderer != null)
                particles.transform.position = new Vector3(
                    liquidRenderer.bounds.center.x,
                    liquidRenderer.bounds.max.y,
                    liquidRenderer.bounds.center.z);

            Color vapor = liquid.VaporColor;
            vapor.a = Mathf.Lerp(0.04f, Mathf.Max(0.08f, vapor.a), intensity);
            var main = particles.main;
            main.startColor = vapor;
            float flaskWidth = liquidRenderer != null
                ? Mathf.Min(liquidRenderer.bounds.size.x, liquidRenderer.bounds.size.z)
                : 0.1f;
            main.startSize = new ParticleSystem.MinMaxCurve(flaskWidth * 0.18f, flaskWidth * 0.36f);
            var shape = particles.shape;
            shape.radius = flaskWidth * 0.11f;
            var emission = particles.emission;
            emission.rateOverTime = (liquid.Config != null
                ? liquid.Config.steamRateAtMaximum
                : 5f) * intensity;
            if (!particles.isPlaying) particles.Play();
        }

        static void StopEffects(ThermalEffects effects)
        {
            if (effects.bubbles != null &&
                (effects.bubbles.isEmitting || effects.bubbles.particleCount > 0))
                effects.bubbles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (effects.steam != null && effects.steam.isEmitting)
                effects.steam.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        }

        void OnDisable()
        {
            foreach (SpillLiquidContainer liquid in m_known)
                if (liquid != null) liquid.SetBoilingIntensity(0f);
            foreach (ThermalEffects effects in m_effects.Values) StopEffects(effects);
        }

        void OnDestroy()
        {
            foreach (ThermalEffects effects in m_effects.Values)
            {
                if (effects.bubbles != null) Destroy(effects.bubbles.gameObject);
                if (effects.steam != null) Destroy(effects.steam.gameObject);
            }
            if (m_bubbleMaterial != null) Destroy(m_bubbleMaterial);
            if (m_steamMaterial != null) Destroy(m_steamMaterial);
        }
    }
}
