Shader "PBDFluid/SSFSurface"
{
    Properties
    {
        // Preenchidas pela Renderer Feature e pelo mundo. A aparencia de cada liquido
        // vive nos arrays _Substance*, indexados pelo id gravado no passe de
        // profundidade: um material so atende a cena inteira.
        [HideInInspector] _PBDFluidSurfaceDepth ("SSF Depth", 2D) = "black" {}
        [HideInInspector] _PBDFluidSurfaceNormal ("SSF Normal", 2D) = "black" {}
        [HideInInspector] _PBDFluidSurfaceSubstance ("SSF Substance", 2D) = "black" {}
        [HideInInspector] _DensityThreshold ("Collective density threshold", Float) = 0.12
        [HideInInspector] _EdgeSoftness ("Collective edge softness", Float) = 1.25

        [Header(Refracao)]
        _RefractionStrength ("Distorcao da cena atras", Range(0, 1)) = 0.35
        _RefractionFade ("Distancia onde a distorcao satura", Range(0.01, 1)) = 0.12

        [Header(Brilho e iluminacao)]
        _ReflectionStrength ("Brilho de Fresnel", Range(0, 2)) = 0.8
        _SpecularIntensity ("Reflexo das luzes", Range(0, 3)) = 1
        _FresnelPower ("Fresnel", Range(0.5, 8)) = 3
        [Toggle] _ReceiveShadows ("Receber sombras", Float) = 1

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
            // Declara _CameraOpaqueTexture e SampleSceneColor. Usar o include oficial
            // em vez de declarar o sampler a mao: e ele que trata XR e o downsampling
            // da textura opaca, que neste projeto esta em 2x.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            TEXTURE2D_X_FLOAT(_PBDFluidSurfaceDepth);
            TEXTURE2D_X(_PBDFluidSurfaceNormal);
            TEXTURE2D_X(_PBDFluidSurfaceSubstance);

            float4 _PBDFluidTexelSize;
            float4x4 _PBDFluidInvProjection;
            float4x4 _PBDFluidCameraToWorld;
            float3 _PBDFluidRenderCameraPosition;
            float _PBDFluidNormalRadius;
            float _PBDFluidWorldRadius;

            #define MAX_SUBSTANCES 8

            // Aparencia por substancia. Arrays em vez de propriedades soltas porque o
            // pipeline SSF foi unificado: um passe desenha todos os liquidos e grava
            // qual venceu cada pixel, e este material le a aparencia por esse indice.
            // Fora do CBUFFER de material: arrays nao cabem no bloco por instancia.
            // float4 e nao half4: SetVectorArray envia float4, e deixar os dois lados
            // com o mesmo tipo evita depender de como cada plataforma promove half
            // dentro de um array de constantes.
            float4 _SubstanceShallow[MAX_SUBSTANCES];
            float4 _SubstanceDeep[MAX_SUBSTANCES];
            float4 _SubstanceAbsorb[MAX_SUBSTANCES];
            float4 _SubstanceEmission[MAX_SUBSTANCES];
            // x absorcao  y turbidez  z ior  w suavidade
            float4 _SubstanceOptics[MAX_SUBSTANCES];
            // x opacidade  y transmissao  z forca de emissao  w reservado
            float4 _SubstanceSurface[MAX_SUBSTANCES];

            CBUFFER_START(UnityPerMaterial)
                half4 _OutlineColor;
                float _FresnelPower;
                float _ReflectionStrength;
                float _SpecularIntensity;
                float _ReceiveShadows;
                float _RefractionStrength;
                float _RefractionFade;
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

                // Os buffers SSF sao screen-space e pertencem a uma camera especifica.
                // Em Play a Scene View nao reconstroi buffers proprios; sem esta guarda
                // ela reutilizava os da Main Camera e desenhava um retangulo fantasma.
                float3 cameraDelta = _WorldSpaceCameraPos.xyz - _PBDFluidRenderCameraPosition;
                clip(0.0625 - dot(cameraDelta, cameraDelta));

                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float2 fluidUV = UnityStereoTransformScreenSpaceTex(screenUV);
                float eyeDepth = SampleRawDepth(fluidUV);
                float outline = _OutlineEnabled > 0.5 ? OutlineMask(fluidUV, eyeDepth) : 0.0;

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
                float edgeFeather = max(fwidth(layerDensity) * max(_EdgeSoftness, 0.5), 1e-4);
                float coverage = smoothstep(
                    max(_DensityThreshold, 0.0) - edgeFeather,
                    max(_DensityThreshold, 0.0) + edgeFeather,
                    layerDensity);
                if (coverage <= 1e-3)
                {
                    clip(outline - 1e-3);
                    return half4(_OutlineColor.rgb, saturate(_OutlineColor.a * outline));
                }

                // Qual liquido venceu este pixel. Amostrado em point do buffer NAO
                // borrado: interpolar um indice misturaria identidades e pintaria a
                // fronteira entre dois liquidos com a aparencia de um terceiro.
                float encodedSubstance = SAMPLE_TEXTURE2D_X_LOD(
                    _PBDFluidSurfaceSubstance, sampler_PointClamp, fluidUV, 0).r;
                int substance = clamp((int)round(encodedSubstance * 255.0), 0, MAX_SUBSTANCES - 1);

                float3 shallowColor = _SubstanceShallow[substance].rgb;
                float3 deepColor = _SubstanceDeep[substance].rgb;
                float3 absorbColor = max(_SubstanceAbsorb[substance].rgb, 0.01.xxx);
                float3 emissionColor = _SubstanceEmission[substance].rgb;
                float4 optics = _SubstanceOptics[substance];
                float4 surface = _SubstanceSurface[substance];
                float absorption = max(optics.x, 0.0);
                float turbidity = saturate(optics.y);
                float ior = max(optics.z, 1.0001);
                float smoothnessValue = saturate(optics.w);
                float opacity = saturate(surface.x);
                float transmission = max(surface.y, 0.0);
                float emissionStrength = max(surface.z, 0.0);

                float3 normalWS = normalize(encodedNormal.xyz * 2.0 - 1.0);
                float3 positionVS = ReconstructViewPosition(screenUV, eyeDepth);
                float3 positionWS = mul(_PBDFluidCameraToWorld, float4(positionVS, 1.0)).xyz;
                float3 viewWS = normalize(_WorldSpaceCameraPos.xyz - positionWS);
                float ndv = saturate(dot(normalWS, viewWS));
                float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
                float fresnel = saturate(f0 + (1.0 - f0) *
                    pow(saturate(1.0 - ndv), max(_FresnelPower, 0.5)));

                // --- REFRACAO -------------------------------------------------------
                // A cena atras e amostrada com deslocamento na direcao da normal em
                // espaco de tela. E o que separa agua de tinta: um filete fino deixa
                // ver o que esta atras, deformado, em vez de pintar por cima.
                float3 normalVS = mul((float3x3)UNITY_MATRIX_V, normalWS);
                float offsetFalloff = saturate(_RefractionFade / max(eyeDepth, 1e-3));
                float2 refractionOffset = normalVS.xy * _RefractionStrength *
                    offsetFalloff * (ior - 1.0);
                float2 refractedUV = saturate(screenUV + refractionOffset);
                float3 behind = SampleSceneColor(refractedUV);

                // --- BEER-LAMBERT ---------------------------------------------------
                // A luz que atravessa perde energia por cor conforme a espessura real
                // em metros. Filete fino fica quase claro; volume acumulado tinge.
                float pathLength = thicknessWS * (1.0 + turbidity * 2.0);
                float3 extinction = exp(-absorption * pathLength * (1.0.xxx - absorbColor));
                float3 transmitted = behind * extinction;

                // Cor propria do corpo, com a profundidade optica dando o gradiente
                // entre a borda rasa e o miolo fundo.
                float body = saturate(1.0 - exp(-layerDensity * 0.7));
                float3 bodyColor = lerp(shallowColor, deepColor, body);
                bodyColor = lerp(bodyColor, shallowColor, turbidity * body * 0.35);

                // Quanto mais espesso, menos a cena atras aparece e mais o corpo domina.
                float opaqueness = saturate(1.0 - exp(-absorption * pathLength * 1.5));
                float3 color = lerp(transmitted, bodyColor, opaqueness);

                float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float shadow = _ReceiveShadows > 0.5 ? mainLight.shadowAttenuation : 1.0;
                float attenuation = mainLight.distanceAttenuation * shadow;
                float ndl = saturate(dot(normalWS, mainLight.direction));

                // Iluminacao aplicada ao corpo, nao ao que se ve atraves: sombrear a
                // cena refratada a escureceria duas vezes.
                float3 ambient = max(SampleSH(normalWS), 0.0.xxx);
                color *= lerp(1.0.xxx, 0.35.xxx + mainLight.color * (0.28 + 0.72 * ndl) * 0.9 +
                    ambient * 0.35, opaqueness);

                float3 halfVector = normalize(mainLight.direction + viewWS);
                float specularPower = lerp(8.0, 72.0, smoothnessValue);
                float specular = pow(saturate(dot(normalWS, halfVector)), specularPower) *
                    lerp(0.45, 1.0, smoothnessValue) * attenuation *
                    max(_SpecularIntensity, 0.0);
                color += mainLight.color * specular;

                // Luz vinda de tras atravessando o liquido: o brilho de um filete
                // contra a janela.
                float backLight = pow(saturate(dot(-normalWS, mainLight.direction)), 2.0) *
                    attenuation * saturate(transmission * 0.5);
                color += mainLight.color * bodyColor * backLight * (1.0 - opaqueness * 0.6);

                color += emissionColor * emissionStrength * (0.4 + body * 0.6);

                // Fresnel clareia a borda de silhueta, onde a superficie fica de perfil.
                color = lerp(color, color + mainLight.color * 0.6,
                    saturate(_ReflectionStrength * fresnel * 0.45));

                // O alpha carrega quanto do pixel e liquido. A refracao ja trouxe a
                // cena atras, entao a opacidade nao precisa mais esconder o fundo:
                // basta ser alta o suficiente para o corpo aparecer.
                float alpha = saturate(coverage * lerp(0.65, 1.0, opaqueness) * opacity);
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
