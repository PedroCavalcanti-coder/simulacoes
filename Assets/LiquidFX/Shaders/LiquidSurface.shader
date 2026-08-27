// Standing liquid: sink basin, tray, tank.
//
// What this fixes compared to the earlier prototype surface:
//  * it is genuinely transparent. Depth against the scene drives absorption and alpha, so the
//    drain, the basin floor and anything half submerged are visible through the water instead of
//    being hidden behind an opaque quad that only pretended to refract.
//  * refraction rejects foreground samples. Without that test, an object standing in front of
//    the water smears across the surface, which is the single most obvious "fake water" tell.
//  * contact foam comes from real geometry depth, not from the quad UV, so it hugs the basin
//    walls and every submerged object.
//  * ripples are metric. The C# side sends offsets and radii in metres, so a 4 cm ring is 4 cm
//    wide whatever the quad scale is, and the same numbers work on a sink and on a beaker.
//
// The mesh is expected to be a flat grid in the XZ plane spanning -0.5..0.5 with a transform
// scale of (width, 1, depth): the vertical scale must stay 1 so the displacement is in metres.

Shader "LiquidFX/Liquid Surface"
{
    Properties
    {
        _LiquidTint("Liquid Tint", Color) = (0.35, 0.72, 0.78, 1)
        _AbsorptionPerMetre("Absorption Per Metre (RGB)", Vector) = (2.4, 0.9, 0.6, 0)
        _FoamColor("Foam Color", Color) = (0.92, 0.97, 1, 1)
        _FoamDepth("Foam Contact Depth", Range(0.002, 0.3)) = 0.035
        _EdgeFadeDepth("Edge Fade Depth", Range(0.001, 0.2)) = 0.02
        _MaxOpacity("Maximum Opacity", Range(0.2, 1)) = 0.96

        _NormalMap("Micro Normal", 2D) = "bump" {}
        _NormalTiling("Micro Normal Tiling (per metre)", Float) = 4.0
        _NormalSpeed("Micro Normal Speed", Vector) = (0.03, 0.021, -0.024, 0.017)
        _MicroStrength("Micro Normal Strength", Range(0, 1)) = 0.12

        [Toggle(_REFRACTION_ON)] _Refraction("Refraction", Float) = 1
        _RefractionStrength("Refraction Strength", Range(0, 0.08)) = 0.022
        _ReflectionStrength("Reflection Strength", Range(0, 2)) = 1.0
        _ReflectionRoughness("Reflection Roughness", Range(0, 1)) = 0.06
        _FresnelPower("Fresnel Power", Range(1, 8)) = 4.6
        _SpecularPower("Specular Power", Range(8, 256)) = 96
        _SpecularStrength("Specular Strength", Range(0, 4)) = 1.4

        _RippleNormalStrength("Ripple Normal Strength", Range(0, 12)) = 3.2
        _RippleDisplacement("Ripple Vertex Displacement", Range(0, 2)) = 1.0
        _CrestFoam("Crest Foam", Range(0, 4)) = 0.9
        _RippleGlowColor("Ripple Glow Color", Color) = (0.75, 0.97, 1, 1)
        _RippleGlowStrength("Ripple Glow Strength", Range(0, 4)) = 1.4

        // Driven from LiquidSurface.cs through a MaterialPropertyBlock.
        _RippleAmplitude("Ripple Amplitude", Float) = 0.0035
        _RippleSpeed("Ripple Speed", Float) = 0.55
        _RippleWavelength("Ripple Wavelength", Float) = 0.055
        _RippleSpatialDecay("Ripple Spatial Decay", Float) = 9
        _RippleTimeDecay("Ripple Time Decay", Float) = 1.35
        _RippleLifetime("Ripple Lifetime", Float) = 2.4
        _SurfaceSize("Surface Size", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "LiquidSurfaceForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma shader_feature_local_fragment _REFRACTION_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "LiquidRipples.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float2 metres : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _LiquidTint;
                half4 _AbsorptionPerMetre;
                half4 _FoamColor;
                half _FoamDepth;
                half _EdgeFadeDepth;
                half _MaxOpacity;
                half _NormalTiling;
                float4 _NormalSpeed;
                half _MicroStrength;
                half _Refraction;
                half _RefractionStrength;
                half _ReflectionStrength;
                half _ReflectionRoughness;
                half _FresnelPower;
                half _SpecularPower;
                half _SpecularStrength;
                half _RippleNormalStrength;
                half _RippleDisplacement;
                half _CrestFoam;
                half4 _RippleGlowColor;
                half _RippleGlowStrength;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // Object space XZ spans -0.5..0.5; convert to metres on the basin.
                float2 metres = input.positionOS.xz * _SurfaceSize.xy;

                LiquidRippleSample ripple = EvaluateLiquidRipples(metres);
                float3 positionOS = input.positionOS.xyz;
                positionOS.y += ripple.height * _RippleDisplacement;

                VertexPositionInputs positions = GetVertexPositionInputs(positionOS);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.uv = input.uv;
                output.metres = metres;
                return output;
            }

            // Refraction that refuses to sample anything standing in front of the water.
            // Without this test, objects between the camera and the surface bleed into it.
            half3 SampleRefraction(float2 screenUV, float2 offset, float surfaceEye, out float refractedSceneEye)
            {
                float2 refractedUV = saturate(screenUV + offset);
                float refractedRaw = SampleSceneDepth(refractedUV);
                float refractedEye = LinearEyeDepth(refractedRaw, _ZBufferParams);

                if (refractedEye < surfaceEye)
                {
                    refractedUV = screenUV;
                    refractedRaw = SampleSceneDepth(refractedUV);
                    refractedEye = LinearEyeDepth(refractedRaw, _ZBufferParams);
                }

                refractedSceneEye = refractedEye;
                return SampleSceneColor(refractedUV);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // ---- micro detail -------------------------------------------------------
                float2 microUvA = input.metres * _NormalTiling + _Time.y * _NormalSpeed.xy;
                float2 microUvB = input.metres * (_NormalTiling * 1.31) + _Time.y * _NormalSpeed.zw;
                half2 microA = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, microUvA).rg * 2.0h - 1.0h;
                half2 microB = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, microUvB).rg * 2.0h - 1.0h;
                half2 micro = (microA + microB) * 0.5h;

                // Warping the ripple lookup with the micro normal keeps the rings from looking
                // like perfect compass circles.
                float2 metres = input.metres + micro * 0.004;
                LiquidRippleSample ripple = EvaluateLiquidRipples(metres);

                half3 normalOS = normalize(half3(
                    -ripple.slope.x * _RippleNormalStrength + micro.x * _MicroStrength,
                    1.0h,
                    -ripple.slope.y * _RippleNormalStrength + micro.y * _MicroStrength));
                half3 normalWS = normalize(TransformObjectToWorldNormal(normalOS));

                // ---- depth against the scene -------------------------------------------
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float surfaceEye = input.positionCS.w;
                float sceneRaw = SampleSceneDepth(screenUV);
                float sceneEye = LinearEyeDepth(sceneRaw, _ZBufferParams);
                float waterDepth = max(0.0, sceneEye - surfaceEye);

                half3 viewDirection = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half viewFacing = saturate(dot(normalWS, viewDirection));
                half fresnel = 0.02h + 0.98h * pow(1.0h - viewFacing, _FresnelPower);

                // ---- refraction ---------------------------------------------------------
                half3 refractedScene;
                float refractedSceneEye = sceneEye;
                #if defined(_REFRACTION_ON)
                    half2 viewNormal = TransformWorldToViewNormal(normalWS, false).xy;
                    half2 flatViewNormal = TransformWorldToViewNormal(
                        normalize(TransformObjectToWorldNormal(half3(0, 1, 0))), false).xy;
                    // Deep water can bend more; a 2 mm film cannot bend anything.
                    float bend = saturate(waterDepth * 6.0);
                    float2 refractionOffset = (viewNormal - flatViewNormal) * _RefractionStrength * bend;
                    refractedScene = SampleRefraction(screenUV, refractionOffset, surfaceEye, refractedSceneEye);
                #else
                    refractedScene = SampleSceneColor(screenUV);
                #endif

                float refractedDepth = max(0.0, refractedSceneEye - surfaceEye);

                // ---- absorption ---------------------------------------------------------
                // Beer-Lambert: the further light travels through the liquid, the more of it is
                // eaten, and the tint is what survives.
                half3 transmission = exp(-_AbsorptionPerMetre.rgb * refractedDepth);
                half3 body = refractedScene * transmission;
                body = lerp(_LiquidTint.rgb * (1.0h - transmission) + body, body, transmission);

                // ---- reflection ---------------------------------------------------------
                half3 reflectionDirection = reflect(-viewDirection, normalWS);
                half3 environment = GlossyEnvironmentReflection(
                    reflectionDirection, input.positionWS, _ReflectionRoughness, 1.0h, screenUV);

                half reflectionMask = saturate(fresnel * _ReflectionStrength);
                half3 color = lerp(body, environment, reflectionMask);

                // ---- foam ---------------------------------------------------------------
                // Real contact foam: thin water against basin walls and submerged objects.
                half contactFoam = 1.0h - saturate(waterDepth / max(_FoamDepth, 0.001h));
                contactFoam *= contactFoam;

                // Trace the whole wave crest, not just its exact peak pixel: a raw height ratio
                // only lights up right at the top of the curve, which reads as a faint dot rather
                // than the bright travelling ring the reference art wants. Smoothstepping a wider
                // band of the (unsigned) height catches the crest and the following trough edge.
                half rippleMagnitude = saturate(abs(ripple.height) / max(_RippleAmplitude, 0.0001));
                half crestFoam = smoothstep(0.1h, 0.65h, rippleMagnitude) * _CrestFoam * ripple.energy;
                half foam = saturate(contactFoam * 0.85h + crestFoam);
                color = lerp(color, _FoamColor.rgb, foam);

                // A soft additive shimmer riding the same ripple energy, independent of the foam
                // blend above: this is what gives moving water the slightly unreal, glowing-edge
                // look instead of a flat physically "correct" one.
                color += _RippleGlowColor.rgb * (ripple.energy * _RippleGlowStrength * 0.35h);

                // ---- lighting -----------------------------------------------------------
                Light mainLight = GetMainLight();
                half3 halfDirection = SafeNormalize(mainLight.direction + viewDirection);
                half normalHalf = saturate(dot(normalWS, halfDirection));
                half specular = pow(normalHalf, _SpecularPower) * _SpecularStrength;
                half broadSpecular = pow(normalHalf, max(8.0h, _SpecularPower * 0.18h)) * _SpecularStrength * 0.1h;
                color += (specular + broadSpecular) * mainLight.color;

                // ---- opacity ------------------------------------------------------------
                // Water feathers out where it meets a wall instead of ending on a hard line.
                half depthAlpha = saturate(waterDepth / max(_EdgeFadeDepth, 0.001h));
                half alpha = saturate(max(depthAlpha, fresnel * 0.9h) * _MaxOpacity);
                alpha = max(alpha, foam * 0.9h);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
