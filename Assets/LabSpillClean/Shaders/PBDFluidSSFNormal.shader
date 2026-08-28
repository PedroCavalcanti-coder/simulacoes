Shader "Hidden/PBDFluid/SSFNormal"
{
    Properties
    {
        [HideInInspector] _BlitTexture ("Smoothed eye depth", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always Blend Off

        Pass
        {
            Name "ReconstructDepthNormal"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D_X_FLOAT(_PBDFluidSceneDepth);

            float4 _PBDFluidTexelSize;
            float4x4 _PBDFluidInvProjection;
            float4x4 _PBDFluidCameraToWorld;
            float _PBDFluidHasSceneDepth;
            float _PBDFluidNormalRadius;
            float _PBDFluidWorldRadius;
            float _PBDFluidProjectionScaleY;
            float _PBDFluidOrthographic;

            static const float FAR_EYE = 1e5;

            bool IsFluid(float eyeDepth)
            {
                return eyeDepth > 1e-4 && eyeDepth < FAR_EYE;
            }

            float SampleEye(float2 uv, float fallbackEye)
            {
                float eyeDepth = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_LinearClamp, saturate(uv), _BlitMipLevel).r;
                return IsFluid(eyeDepth) ? eyeDepth : fallbackEye;
            }

            float3 ReconstructViewPosition(float2 uv, float eyeDepth)
            {
                float2 ndc = uv * 2.0 - 1.0;
                float4 rayH = mul(_PBDFluidInvProjection, float4(ndc, 1.0, 1.0));
                float3 ray = rayH.xyz / max(abs(rayH.w), 1e-6);
                return ray * (eyeDepth / max(-ray.z, 1e-5));
            }

            struct FragmentOutput
            {
                float depth : SV_Target0;
                half4 normal : SV_Target1;
                // A substancia chega aqui ja espalhada pelo blur e so entao e gravada,
                // por isso ela cobre a mesma area que a superficie e nao sobra borda
                // com o valor de limpeza.
                float substance : SV_Target2;
            };

            FragmentOutput EmptyOutput()
            {
                FragmentOutput output;
                output.depth = 0.0;
                output.normal = half4(0.0, 0.0, 0.0, 0.0);
                output.substance = 0.0;
                return output;
            }

            FragmentOutput Frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord.xy;
                float4 surfaceData = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_LinearClamp, uv, _BlitMipLevel);
                float eyeDepth = surfaceData.r;
                float thicknessWS = max(surfaceData.g, 0.0);
                // Point sample no proprio pixel: um indice interpolado nao e um indice.
                float substance = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_PointClamp, uv, _BlitMipLevel).b;
                if (!IsFluid(eyeDepth)) return EmptyOutput();

                // A geometria opaca continua sendo dona do pixel quando esta
                // mais perto da camera que a superficie reconstruida.
                if (_PBDFluidHasSceneDepth > 0.5)
                {
                    float rawSceneDepth = SAMPLE_TEXTURE2D_X_LOD(
                        _PBDFluidSceneDepth, sampler_PointClamp, uv, 0).r;
                    float sceneEye = LinearEyeDepth(rawSceneDepth, _ZBufferParams);
                    // A profundidade filtrada de uma poca em contato pode ficar
                    // alguns milimetros atras do tampo. Aceitar uma fracao do
                    // raio visual evita o pontilhado por auto-oclusao sem deixar
                    // o liquido atravessar objetos espessos.
                    // O solver deixa o centro PBD parcialmente dentro do plano de
                    // apoio, enquanto o splat visual e bem maior que o raio fisico.
                    // Uma tolerancia de apenas alguns milimetros fazia a bancada
                    // recortar o interior da poca em faixas/pontos. Aceitar ate um
                    // raio e meio do splat preserva a superficie de contato, mas
                    // objetos claramente a frente continuam ocluindo o fluido.
                    float contactTolerance = max(
                        3e-3, _PBDFluidWorldRadius * 1.5);
                    if (sceneEye > 0.0 &&
                        eyeDepth >= sceneEye + contactTolerance)
                        return EmptyOutput();
                }

                float depthScale = lerp(max(eyeDepth, 1e-4), 1.0,
                    saturate(_PBDFluidOrthographic));
                float projectedRadius = max(_PBDFluidWorldRadius, 1e-5) *
                    max(_PBDFluidProjectionScaleY, 1e-4) * 0.5 *
                    _PBDFluidTexelSize.w / depthScale;
                float normalRadius = clamp(projectedRadius *
                    clamp(_PBDFluidNormalRadius, 0.1, 1.5), 1.0, 48.0);
                float2 texelX = float2(_PBDFluidTexelSize.x * normalRadius, 0.0);
                float2 texelY = float2(0.0, _PBDFluidTexelSize.y * normalRadius);
                float eyeL = SampleEye(uv - texelX, eyeDepth);
                float eyeR = SampleEye(uv + texelX, eyeDepth);
                float eyeD = SampleEye(uv - texelY, eyeDepth);
                float eyeU = SampleEye(uv + texelY, eyeDepth);

                float3 centerVS = ReconstructViewPosition(uv, eyeDepth);
                float3 leftVS = ReconstructViewPosition(uv - texelX, eyeL);
                float3 rightVS = ReconstructViewPosition(uv + texelX, eyeR);
                float3 downVS = ReconstructViewPosition(uv - texelY, eyeD);
                float3 upVS = ReconstructViewPosition(uv + texelY, eyeU);

                // Escolher a derivada unilateral menor evita que uma borda de
                // profundidade atravesse a normal e apareca como ruido claro.
                float3 dxForward = rightVS - centerVS;
                float3 dxBackward = centerVS - leftVS;
                float3 dyForward = upVS - centerVS;
                float3 dyBackward = centerVS - downVS;
                float3 dx = dot(dxForward, dxForward) < dot(dxBackward, dxBackward)
                    ? dxForward : dxBackward;
                float3 dy = dot(dyForward, dyForward) < dot(dyBackward, dyBackward)
                    ? dyForward : dyBackward;

                float3 normalVS = cross(dx, dy);
                float normalLengthSq = dot(normalVS, normalVS);
                normalVS = normalLengthSq > 1e-12
                    ? normalVS * rsqrt(normalLengthSq)
                    : float3(0.0, 0.0, 1.0);

                float3 viewVS = normalize(-centerVS);
                if (dot(normalVS, viewVS) < 0.0) normalVS = -normalVS;

                float3 normalWS = normalize(mul((float3x3)_PBDFluidCameraToWorld, normalVS));

                FragmentOutput output;
                output.depth = eyeDepth;
                output.substance = substance * (1.0 / 255.0);
                // Alpha transporta a espessura coletiva ate o material final.
                // O target RGBA16F preserva o valor em unidades de mundo.
                output.normal = half4(normalWS * 0.5 + 0.5, thicknessWS);
                return output;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
