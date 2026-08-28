Shader "Hidden/PBDFluid/SSFBlur"
{
    Properties
    {
        [HideInInspector] _BlitTexture ("Source", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float2 _Direction;
            float4 _TexelSize;
            float _Radius;
            float _DepthFalloff;
            float _SurfaceTension;
            float _PBDFluidWorldRadius;
            float _PBDFluidProjectionScaleY;
            float _PBDFluidOrthographic;

            static const float FAR_EYE = 1e5;

            bool IsFluid(float eye)
            {
                return eye > 1e-4 && eye < FAR_EYE;
            }

            float ProjectedFilterRadius(float eyeDepth)
            {
                // Converte o raio real da particula para pixels. Assim o filtro
                // cobre aproximadamente uma particula tanto perto quanto longe
                // da camera sem aumentar o numero de amostras.
                float depthScale = lerp(max(eyeDepth, 1e-4), 1.0,
                    saturate(_PBDFluidOrthographic));
                float projectedRadius = max(_PBDFluidWorldRadius, 1e-5) *
                    max(_PBDFluidProjectionScaleY, 1e-4) * 0.5 * _TexelSize.w / depthScale;
                return clamp(projectedRadius * max(_Radius, 0.25), 1.0, 96.0);
            }

            // A substancia acompanha a profundidade em vez de ser filtrada: ela e um
            // indice, e a media de dois indices nao e um indice. Cada pixel fica com a
            // substancia da amostra que forneceu a profundidade mais proxima, entao a
            // identidade se espalha exatamente ate onde a superficie se espalha.
            float4 Frag(Varyings input) : SV_Target0
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord.xy;
                float4 centerData = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_PointClamp, uv, _BlitMipLevel);
                float center = centerData.r;
                float substance = centerData.b;

                // Somente pixels vazios proximos da silhueta procuram uma semente.
                // A maior parte do buffer vazio encerra aqui com cinco leituras,
                // em vez das 66 leituras usadas pelo filtro anterior.
                if (!IsFluid(center))
                {
                    float seed = FAR_EYE;
                    float seedSubstance = 0.0;
                    [unroll] for (int i = 1; i <= 4; i++)
                    {
                        float4 s0 = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp,
                            uv + _Direction * _TexelSize.xy * i, _BlitMipLevel);
                        float4 s1 = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp,
                            uv - _Direction * _TexelSize.xy * i, _BlitMipLevel);
                        if (IsFluid(s0.r) && s0.r < seed) { seed = s0.r; seedSubstance = s0.b; }
                        if (IsFluid(s1.r) && s1.r < seed) { seed = s1.r; seedSubstance = s1.b; }
                    }
                    if (!IsFluid(seed)) return float4(0.0, 0.0, 0.0, 0.0);
                    center = seed;
                    substance = seedSubstance;
                }

                float stepPixels = ProjectedFilterRadius(center) * 0.25;
                float depths[9];
                float thicknesses[9];
                float nearest = center;
                [unroll] for (int i = -4; i <= 4; i++)
                {
                    float4 sampleData = SAMPLE_TEXTURE2D_X_LOD(
                        _BlitTexture, sampler_PointClamp,
                        uv + _Direction * _TexelSize.xy * (i * stepPixels),
                        _BlitMipLevel);
                    float d = sampleData.r;
                    depths[i + 4] = d;
                    thicknesses[i + 4] = max(sampleData.g, 0.0);
                    if (IsFluid(d) && d < nearest) { nearest = d; substance = sampleData.b; }
                }

                // Aproxima o envelope frontal da massa e limita a correcao a
                // menos de um raio real, evitando paredes planas artificiais.
                float tension = saturate(_SurfaceTension);
                float reference = lerp(center, nearest, tension);
                float maxPull = max(_PBDFluidWorldRadius * 1.05, 1e-4);
                reference = max(reference, center - maxPull);

                float weightedDepth = 0;
                float weightSum = 0;
                float weightedThickness = 0;
                float thicknessWeightSum = 0;
                float worldRadius = max(_PBDFluidWorldRadius, 1e-5);
                float falloff = max(_DepthFalloff, 0.1);

                // Nove taps fixos: o alcance muda com a projecao, o custo nao.
                [unroll] for (int i = -4; i <= 4; i++)
                {
                    float d = depths[i + 4];
                    float tap = (float)i;
                    float spatial = rcp(1.0 + 0.22 * tap * tap);
                    // A espessura usa o kernel espacial completo, incluindo
                    // zeros. Isso cria a queda continua da isosuperficie nas
                    // bordas e une as contribuicoes das particulas vizinhas.
                    weightedThickness += thicknesses[i + 4] * spatial;
                    thicknessWeightSum += spatial;

                    if (!IsFluid(d)) continue;
                    float depthDelta = (d - reference) * falloff / worldRadius;
                    float rangeWeight = rcp(1.0 + depthDelta * depthDelta);
                    float weight = spatial * rangeWeight;
                    weightedDepth += d * weight;
                    weightSum += weight;
                }

                float smoothed = weightSum > 1e-5 ? weightedDepth / weightSum : reference;
                float smoothedThickness = weightedThickness /
                    max(thicknessWeightSum, 1e-5);
                float smoothedEye = lerp(smoothed, min(smoothed, reference), tension * 0.8);
                return float4(smoothedEye, smoothedThickness, substance, 0.0);
            }
            ENDHLSL
        }
    }
}
