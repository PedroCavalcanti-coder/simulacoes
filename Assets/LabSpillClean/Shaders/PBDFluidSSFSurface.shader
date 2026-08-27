Shader "PBDFluid/SSFSurface"
{
    Properties
    {
        // Preenchidas por renderer via MaterialPropertyBlock. Permanecem fora da
        // aparencia editavel, mas precisam ser propriedades locais para que cada
        // liquido leia seus proprios buffers SSF em cenas multi-liquido.
        [HideInInspector] _PBDFluidSurfaceDepth ("SSF Depth", 2D) = "black" {}
        [HideInInspector] _PBDFluidSurfaceNormal ("SSF Normal", 2D) = "black" {}
        [HideInInspector] _DensityThreshold ("Collective density threshold", Float) = 0.12
        [HideInInspector] _EdgeSoftness ("Collective edge softness", Float) = 1.25

        [Header(Cores do liquido)]
        _ShallowColor ("Cor rasa / bordas", Color) = (0.55, 0.82, 0.88, 1)
        _DeepColor ("Cor profunda / corpo", Color) = (0.02, 0.16, 0.32, 1)
        _AbsorptionColor ("Cor de transmissao", Color) = (0.24, 0.76, 0.72, 1)

        [Header(Opacidade e volume)]
        _Opacity ("Opacidade de composicao", Range(0, 1)) = 0.9
        _LightTransmission ("Luz atravessando o liquido", Range(0, 2)) = 0.65
        _Absorption ("Absorcao pela espessura", Range(0, 5)) = 1.4
        _Turbidity ("Turbidez", Range(0, 1)) = 0.18
        _IOR ("Indice de refracao", Range(1, 2.5)) = 1.333

        [Header(Brilho e iluminacao)]
        _ReflectionColor ("Cor do reflexo", Color) = (1, 1, 1, 1)
        _ReflectionStrength ("Brilho de Fresnel", Range(0, 2)) = 0.8
        _SpecularIntensity ("Reflexo das luzes", Range(0, 3)) = 1
        _FresnelPower ("Fresnel", Range(0.5, 8)) = 3
        _Smoothness ("Suavidade", Range(0, 1)) = 0.92
        [HDR] _EmissionColor ("Cor de emissao", Color) = (0, 0, 0, 1)
        _EmissionStrength ("Forca da emissao", Range(0, 10)) = 0
        [Toggle] _ReceiveShadows ("Receber sombras", Float) = 1

        [Header(Sombra projetada por particulas)]
        [Toggle] _CastShadows ("Projetar sombra", Float) = 1
        _ShadowOpacity ("Quanto bloqueia a luz", Range(0, 1)) = 0.45
        _ShadowParticleScale ("Continuidade da sombra", Range(0.5, 3)) = 2

        [Header(Outline)]
        [Toggle] _OutlineEnabled ("Ativar outline", Float) = 0
        _OutlineColor ("Cor do outline", Color) = (0.02, 0.18, 0.35, 0.9)
        _OutlineThickness ("Espessura (pixels SSF)", Range(0.5, 8)) = 1.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "RenderType"="Transparent"
            "Queue"="Transparent"
        }

        Pass
        {
            Name "SSFForward"
            Tags { "LightMode"="UniversalForward" }

            // O proxy so delimita pixels. Renderizar a face de saida evita
            // aplicar alpha duas vezes quando frente e fundo do cubo sobrepoem.
            Cull Front
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D_X_FLOAT(_PBDFluidSurfaceDepth);
            TEXTURE2D_X(_PBDFluidSurfaceNormal);

            float4 _PBDFluidTexelSize;
            float4x4 _PBDFluidInvProjection;
            float4x4 _PBDFluidCameraToWorld;
            float3 _PBDFluidRenderCameraPosition;
            float _PBDFluidNormalRadius;
            float _PBDFluidWorldRadius;

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _AbsorptionColor;
                half4 _ReflectionColor;
                half4 _EmissionColor;
                half4 _OutlineColor;
                float _Absorption;
                float _Turbidity;
                float _IOR;
                float _FresnelPower;
                float _Smoothness;
                float _EmissionStrength;
                float _Opacity;
                float _LightTransmission;
                float _ReflectionStrength;
                float _SpecularIntensity;
                float _ReceiveShadows;
                float _CastShadows;
                float _ShadowOpacity;
                float _ShadowParticleScale;
                float _OutlineEnabled;
                float _OutlineThickness;
                float _DensityThreshold;
                float _EdgeSoftness;
            CBUFFER_END

            static const float FAR_EYE = 1e5;

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS);
                return output;
            }

            bool IsFluid(float eyeDepth)
            {
                return eyeDepth > 1e-4 && eyeDepth < FAR_EYE;
            }

            float SampleRawDepth(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(
                    _PBDFluidSurfaceDepth, sampler_LinearClamp, saturate(uv), 0).r;
            }

            float SampleSurfaceDepth(float2 uv, float fallbackEye)
            {
                float eyeDepth = SampleRawDepth(uv);
                return IsFluid(eyeDepth) ? eyeDepth : fallbackEye;
            }

            float OutlineTap(float neighborDepth, float centerValid)
            {
                float neighborValid = IsFluid(neighborDepth) ? 1.0 : 0.0;
                return abs(neighborValid - centerValid);
            }

            float OutlineMask(float2 uv, float centerDepth)
            {
                float centerValid = IsFluid(centerDepth) ? 1.0 : 0.0;
                float thickness = max(_OutlineThickness, 0.5);
                float2 px = _PBDFluidTexelSize.xy * thickness;
                float2 diagonal = px * 0.70710678;

                float edge = 0.0;
                edge = max(edge, OutlineTap(SampleRawDepth(uv + float2( px.x, 0.0)), centerValid));
                edge = max(edge, OutlineTap(SampleRawDepth(uv + float2(-px.x, 0.0)), centerValid));
                edge = max(edge, OutlineTap(SampleRawDepth(uv + float2(0.0,  px.y)), centerValid));
                edge = max(edge, OutlineTap(SampleRawDepth(uv + float2(0.0, -px.y)), centerValid));
                edge = max(edge, OutlineTap(SampleRawDepth(uv + float2( diagonal.x,  diagonal.y)), centerValid));
                edge = max(edge, OutlineTap(SampleRawDepth(uv + float2(-diagonal.x,  diagonal.y)), centerValid));
                edge = max(edge, OutlineTap(SampleRawDepth(uv + float2( diagonal.x, -diagonal.y)), centerValid));
                edge = max(edge, OutlineTap(SampleRawDepth(uv + float2(-diagonal.x, -diagonal.y)), centerValid));
                return edge;
            }

            float3 ReconstructViewPosition(float2 uv, float eyeDepth)
            {
                float2 ndc = uv * 2.0 - 1.0;
                float4 rayH = mul(_PBDFluidInvProjection, float4(ndc, 1.0, 1.0));
                float3 ray = rayH.xyz / max(abs(rayH.w), 1e-6);
                return ray * (eyeDepth / max(-ray.z, 1e-5));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Os buffers SSF sao screen-space e pertencem a uma camera
                // especifica. Em Play a Scene View nao reconstrui buffers
                // proprios; sem esta guarda ela reutilizava os da Main Camera
                // e desenhava um retangulo fantasma no editor.
                float3 cameraDelta = _WorldSpaceCameraPos.xyz -
                    _PBDFluidRenderCameraPosition;
                clip(0.0625 - dot(cameraDelta, cameraDelta));

                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float2 fluidUV = UnityStereoTransformScreenSpaceTex(screenUV);
                float eyeDepth = SampleRawDepth(fluidUV);
                float outline = _OutlineEnabled > 0.5 ? OutlineMask(fluidUV, eyeDepth) : 0.0;

                // Fora da superficie ainda podemos desenhar a metade externa do
                // outline. Sem outline, o proxy continua totalmente invisivel.
                if (!IsFluid(eyeDepth))
                {
                    clip(outline - 1e-3);
                    return half4(_OutlineColor.rgb, saturate(_OutlineColor.a * outline));
                }

                half4 encodedNormal = SAMPLE_TEXTURE2D_X_LOD(
                    _PBDFluidSurfaceNormal, sampler_LinearClamp, fluidUV, 0);
                float thicknessWS = max((float)encodedNormal.a, 0.0);
                float diameterWS = max(_PBDFluidWorldRadius * 2.0, 1e-5);
                float layerDensity = thicknessWS / diameterWS;
                float edgeFeather = max(fwidth(layerDensity) *
                    max(_EdgeSoftness, 0.5), 1e-4);
                float coverage = smoothstep(
                    max(_DensityThreshold, 0.0) - edgeFeather,
                    max(_DensityThreshold, 0.0) + edgeFeather,
                    layerDensity);
                if (coverage <= 1e-3)
                {
                    clip(outline - 1e-3);
                    return half4(_OutlineColor.rgb, saturate(_OutlineColor.a * outline));
                }

                float3 normalWS = normalize(encodedNormal.xyz * 2.0 - 1.0);
                float3 positionVS = ReconstructViewPosition(screenUV, eyeDepth);
                float3 positionWS = mul(_PBDFluidCameraToWorld, float4(positionVS, 1.0)).xyz;
                float3 viewWS = normalize(_WorldSpaceCameraPos.xyz - positionWS);
                float ndv = saturate(dot(normalWS, viewWS));
                float safeIor = max(_IOR, 1.0001);
                float f0 = pow((safeIor - 1.0) / (safeIor + 1.0), 2.0);
                float fresnel = saturate(f0 + (1.0 - f0) *
                    pow(saturate(1.0 - ndv), max(_FresnelPower, 0.5)));
                // A aparencia de volume vem da espessura acumulada de todas as
                // particulas no raio, nao da curvatura de cada splat isolado.
                float body = saturate(1.0 - exp(-layerDensity * 0.7));
                float opticalDepth = lerp(0.04, 0.80, body);
                float absorb = 1.0 - exp(-max(_Absorption, 0.0) * opticalDepth);
                float3 liquidColor = lerp(_ShallowColor.rgb, _DeepColor.rgb,
                    saturate(body * (0.45 + absorb * 0.35)));
                liquidColor *= lerp(1.0.xxx, max(_AbsorptionColor.rgb, 0.01.xxx), absorb * 0.45);
                liquidColor = lerp(liquidColor, _ShallowColor.rgb,
                    saturate(_Turbidity) * body * 0.35);

                float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float shadow = _ReceiveShadows > 0.5 ? mainLight.shadowAttenuation : 1.0;
                float attenuation = mainLight.distanceAttenuation * shadow;
                float ndl = saturate(dot(normalWS, mainLight.direction));
                float diffuse = (0.28 + 0.72 * ndl) * attenuation;
                float3 ambient = max(SampleSH(normalWS), 0.0.xxx) * liquidColor;

                float3 halfVector = normalize(mainLight.direction + viewWS);
                // Um lobo largo produz o brilho branco da referencia sem
                // reflection probe, copia da camera ou uma segunda camada.
                float specularPower = lerp(8.0, 72.0, saturate(_Smoothness));
                float specular = pow(saturate(dot(normalWS, halfVector)), specularPower) *
                    lerp(0.45, 1.0, saturate(_Smoothness)) * attenuation *
                    max(_SpecularIntensity, 0.0);
                float backLight = pow(saturate(dot(-normalWS, mainLight.direction)), 2.0) *
                    attenuation * saturate(_LightTransmission * 0.5);

                float3 color = liquidColor *
                    (0.2.xxx + mainLight.color * diffuse * 0.8) + ambient * 0.55;
                color += mainLight.color * specular;
                color += mainLight.color * liquidColor * backLight * (1.0 - absorb * 0.6);
                color += _EmissionColor.rgb * max(_EmissionStrength, 0.0) *
                    (0.4 + body * 0.6);

                // Fresnel apenas colore a borda. Nao consulta cubemap nem a cor
                // da camera, portanto nao transforma a frente em uma janela.
                float reflectionWeight = saturate(_ReflectionStrength * fresnel * 0.55);
                color = lerp(color, _ReflectionColor.rgb, reflectionWeight);

                // Opacidade e literal. Os materiais do parque usam 1 por padrao,
                // mas o painel ainda pode reduzir este valor deliberadamente.
                float alpha = saturate(_Opacity * coverage);
                float outlineBlend = saturate(outline * _OutlineColor.a);
                color = lerp(color, _OutlineColor.rgb, outlineBlend);
                alpha = max(alpha, outlineBlend);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
