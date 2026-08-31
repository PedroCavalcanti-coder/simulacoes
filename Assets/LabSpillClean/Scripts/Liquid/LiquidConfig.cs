using System;
using UnityEngine;

namespace LabLiquidVR
{
    /// <summary>
    /// Configuracao compartilhada pelo corpo, pela superficie e pelos efeitos
    /// termicos de um liquido. O asset (<see cref="LiquidConfigAsset"/>) pode ser
    /// trocado por recipiente sem que o Fogareiro ou o shader precisem conhecer
    /// valores duplicados.
    /// </summary>
    [Serializable]
    public sealed class LiquidConfig
    {
        public string liquidName = "Liquido padrao";

        [Header("Fisica")]
        [Tooltip("Liquidos da mesma categoria sao misciveis.")]
        public string category = "aquoso";
        [Tooltip("Densidade em kg/L. Fases menos densas ficam acima.")]
        [Min(0.01f)] public float densityKgPerLiter = 1f;
        [Min(0.2f)] public float viscosity = 1f;
        public float boilingPointC = 100f;

        [Header("Visual")]
        public Color surfaceColor = new Color(0.72f, 0.96f, 0.90f, 0.90f);
        public Color bodyColor = new Color(0.16f, 0.68f, 0.69f, 0.68f);
        public Color deepColor = new Color(0.035f, 0.25f, 0.29f, 1f);
        public Color absorptionColor = new Color(0.24f, 0.76f, 0.72f, 1f);
        public Color foamColor = new Color(0.86f, 0.98f, 1f, 0.90f);
        public Color emissionColor = new Color(0.08f, 0.90f, 0.62f, 1f);
        public Color bubbleColor = new Color(0.78f, 0.96f, 1f, 0.48f);
        public Color vaporColor = new Color(0.94f, 0.98f, 1f, 0.18f);
        [Range(0f, 12f)] public float absorptionDensity = 2.6f;
        [Range(0f, 1f)] public float transparency = 0.48f;
        [Range(0f, 1f)] public float lightTransmission = 0.72f;
        [Range(0f, 1f)] public float surfaceOpacity = 0.82f;
        [Range(0f, 1f)] public float turbidity = 0.18f;
        [Range(0f, 1f)] public float smoothness = 0.82f;
        [Min(1.0001f)] public float indexOfRefraction = 1.333f;
        [Min(0f)] public float emissionStrength;

        [Header("Ondas")]
        [Range(0f, 0.12f)] public float maximumWaveHeight = 0.04f;
        [Range(0f, 0.12f)] public float maximumVerticalWaveHeight = 0.034f;
        [Range(0.1f, 40f)] public float waveFrequency = 10.5f;
        [Range(0f, 1f)] public float waveIrregularity = 0.68f;
        [Range(0f, 1f)] public float waveDetail = 0.86f;
        [Range(0f, 0.02f)] public float microWaveAmplitude = 0.0015f;
        [Range(0f, 8f)] public float microWaveSpeed = 0.42f;

        [Header("Camada de superficie")]
        [Range(24, 256)] public int surfaceAngularSegments = 128;
        [Range(3, 32)] public int surfaceRadialRings = 16;
        [Range(0.001f, 0.12f)] public float surfaceSkirtDepth = 0.035f;

        [Header("Bolhas")]
        [Range(8, 512)] public int bubbleMaxParticles = 160;
        [Min(0f)] public float bubbleRateAtBoiling = 4f;
        [Min(0f)] public float bubbleRateAtMaximum = 48f;
        [Min(0.001f)] public float bubbleRiseSpeedAtBoiling = 0.055f;
        [Min(0.001f)] public float bubbleRiseSpeedAtMaximum = 0.14f;
        [Range(0.001f, 0.25f)] public float bubbleMinSizeAtBoiling = 0.012f;
        [Range(0.001f, 0.25f)] public float bubbleMaxSizeAtBoiling = 0.027f;
        [Range(0.001f, 0.25f)] public float bubbleMinSizeAtMaximum = 0.028f;
        [Range(0.001f, 0.25f)] public float bubbleMaxSizeAtMaximum = 0.075f;
        [Range(0f, 0.5f)] public float bubbleInstabilityAtBoiling = 0.008f;
        [Range(0f, 0.5f)] public float bubbleInstabilityAtMaximum = 0.11f;
        [Range(0.005f, 0.45f)] public float bubbleEmitterRadius = 0.09f;
        [Range(0f, 0.25f)] public float bubbleBottomOffset = 0.045f;
        [Range(0.5f, 1f)] public float bubbleWallInset = 0.88f;
        [Range(0f, 1f)] public float bubbleCollisionBounciness = 0.18f;

        [Header("Vapor")]
        [Min(0f)] public float steamRateAtMaximum = 5f;
        [Range(0f, 1f)] public float steamStartIntensity = 0.65f;

        /// <summary>
        /// Copia os valores do asset (ou os defaults desta classe, se o asset estiver
        /// vazio) para uma instancia nova e independente. Uma copia, e nao a referencia
        /// direta a <c>source.data</c>: o valor devolvido pode acabar dentro de uma
        /// mistura (<see cref="LiquidConfig.Mix"/>) que muda com o tempo, e isso nunca
        /// pode voltar a escrever no asset compartilhado entre frascos.
        /// </summary>
        public static LiquidConfig Load(LiquidConfigAsset source)
        {
            LiquidConfig config = source != null && source.data != null
                ? Copy(source.data)
                : new LiquidConfig();
            config.Sanitize();
            return config;
        }

        public void Sanitize()
        {
            if (string.IsNullOrWhiteSpace(category)) category = "aquoso";
            densityKgPerLiter = Mathf.Max(0.01f, densityKgPerLiter);
            viscosity = Mathf.Max(0.2f, viscosity);
            indexOfRefraction = Mathf.Max(1.0001f, indexOfRefraction);
            absorptionDensity = Mathf.Clamp(absorptionDensity, 0f, 12f);
            transparency = Mathf.Clamp01(transparency);
            lightTransmission = Mathf.Clamp01(lightTransmission);
            surfaceOpacity = Mathf.Clamp01(surfaceOpacity);
            turbidity = Mathf.Clamp01(turbidity);
            smoothness = Mathf.Clamp01(smoothness);
            emissionStrength = Mathf.Max(0f, emissionStrength);
            maximumWaveHeight = Mathf.Clamp(maximumWaveHeight, 0f, 0.12f);
            maximumVerticalWaveHeight = Mathf.Clamp(maximumVerticalWaveHeight, 0f, 0.12f);
            waveFrequency = Mathf.Clamp(waveFrequency, 0.1f, 40f);
            waveIrregularity = Mathf.Clamp01(waveIrregularity);
            waveDetail = Mathf.Clamp01(waveDetail);
            microWaveAmplitude = Mathf.Clamp(microWaveAmplitude, 0f, 0.02f);
            microWaveSpeed = Mathf.Clamp(microWaveSpeed, 0f, 8f);
            surfaceAngularSegments = Mathf.Clamp(surfaceAngularSegments, 24, 256);
            surfaceRadialRings = Mathf.Clamp(surfaceRadialRings, 3, 32);
            surfaceSkirtDepth = Mathf.Clamp(surfaceSkirtDepth, 0.001f, 0.12f);
            bubbleMaxParticles = Mathf.Clamp(bubbleMaxParticles, 8, 512);
            bubbleRateAtBoiling = Mathf.Max(0f, bubbleRateAtBoiling);
            bubbleRateAtMaximum = Mathf.Max(bubbleRateAtBoiling, bubbleRateAtMaximum);
            bubbleRiseSpeedAtBoiling = Mathf.Max(0.001f, bubbleRiseSpeedAtBoiling);
            bubbleRiseSpeedAtMaximum = Mathf.Max(bubbleRiseSpeedAtBoiling, bubbleRiseSpeedAtMaximum);
            bubbleMinSizeAtBoiling = Mathf.Clamp(bubbleMinSizeAtBoiling, 0.001f, 0.25f);
            bubbleMaxSizeAtBoiling = Mathf.Max(bubbleMinSizeAtBoiling, bubbleMaxSizeAtBoiling);
            bubbleMinSizeAtMaximum = Mathf.Clamp(bubbleMinSizeAtMaximum, 0.001f, 0.25f);
            bubbleMaxSizeAtMaximum = Mathf.Max(bubbleMinSizeAtMaximum, bubbleMaxSizeAtMaximum);
            bubbleInstabilityAtBoiling = Mathf.Max(0f, bubbleInstabilityAtBoiling);
            bubbleInstabilityAtMaximum = Mathf.Max(bubbleInstabilityAtBoiling, bubbleInstabilityAtMaximum);
            bubbleEmitterRadius = Mathf.Clamp(bubbleEmitterRadius, 0.005f, 0.45f);
            bubbleBottomOffset = Mathf.Clamp(bubbleBottomOffset, 0f, 0.25f);
            bubbleWallInset = Mathf.Clamp(bubbleWallInset, 0.5f, 1f);
            bubbleCollisionBounciness = Mathf.Clamp01(bubbleCollisionBounciness);
            steamRateAtMaximum = Mathf.Max(0f, steamRateAtMaximum);
            steamStartIntensity = Mathf.Clamp01(steamStartIntensity);
        }

        public static LiquidConfig Copy(LiquidConfig source)
        {
            if (source == null) return new LiquidConfig();
            LiquidConfig copy = JsonUtility.FromJson<LiquidConfig>(JsonUtility.ToJson(source));
            copy.Sanitize();
            return copy;
        }

        public static LiquidConfig Mix(LiquidConfig a, float volumeA,
            LiquidConfig b, float volumeB)
        {
            if (a == null) return Copy(b);
            if (b == null) return Copy(a);
            float total = Mathf.Max(0.0001f, volumeA + volumeB);
            float t = Mathf.Clamp01(volumeB / total);
            LiquidConfig result = Copy(a);
            result.liquidName = a.liquidName + " + " + b.liquidName;
            result.category = a.category;
            result.densityKgPerLiter = Mathf.Lerp(a.densityKgPerLiter, b.densityKgPerLiter, t);
            result.viscosity = Mathf.Lerp(a.viscosity, b.viscosity, t);
            result.boilingPointC = Mathf.Lerp(a.boilingPointC, b.boilingPointC, t);
            result.surfaceColor = Color.Lerp(a.surfaceColor, b.surfaceColor, t);
            result.bodyColor = Color.Lerp(a.bodyColor, b.bodyColor, t);
            result.deepColor = Color.Lerp(a.deepColor, b.deepColor, t);
            result.absorptionColor = Color.Lerp(a.absorptionColor, b.absorptionColor, t);
            result.foamColor = Color.Lerp(a.foamColor, b.foamColor, t);
            result.emissionColor = Color.Lerp(a.emissionColor, b.emissionColor, t);
            result.bubbleColor = Color.Lerp(a.bubbleColor, b.bubbleColor, t);
            result.vaporColor = Color.Lerp(a.vaporColor, b.vaporColor, t);
            result.absorptionDensity = Mathf.Lerp(a.absorptionDensity, b.absorptionDensity, t);
            result.transparency = Mathf.Lerp(a.transparency, b.transparency, t);
            result.lightTransmission = Mathf.Lerp(a.lightTransmission, b.lightTransmission, t);
            result.surfaceOpacity = Mathf.Lerp(a.surfaceOpacity, b.surfaceOpacity, t);
            result.turbidity = Mathf.Lerp(a.turbidity, b.turbidity, t);
            result.smoothness = Mathf.Lerp(a.smoothness, b.smoothness, t);
            result.indexOfRefraction = Mathf.Lerp(a.indexOfRefraction, b.indexOfRefraction, t);
            result.emissionStrength = Mathf.Lerp(a.emissionStrength, b.emissionStrength, t);
            result.Sanitize();
            return result;
        }
    }
}
