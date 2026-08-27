// The falling stream.
//
// The mesh is a flat ribbon, but a falling jet is a cylinder, so the shader rebuilds the round
// cross-section from the across-ribbon coordinate: u = 0 and u = 1 are the silhouette edges and
// u = 0.5 is the front of the cylinder. That gives a real specular line down the middle, a rim
// that catches light, and refraction that bends the most at the edges, all from a two-triangle-
// per-segment strip.
//
// It also fades against the scene depth so the stream sinks into the receiving liquid instead of
// ending on a hard line across it.

Shader "LiquidFX/Liquid Stream"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (0.72, 0.92, 1, 1)
        _RimColor("Rim Color", Color) = (0.95, 0.99, 1, 1)
        _FlowMap("Flow Detail (R)", 2D) = "white" {}
        _FlowStrength("Flow Detail Strength", Range(0, 1)) = 0.35
        _Opacity("Opacity", Range(0, 1)) = 0.72
        _RimPower("Rim Power", Range(0.5, 8)) = 2.4
        _RimStrength("Rim Strength", Range(0, 3)) = 1.1
        _SpecularPower("Specular Power", Range(4, 128)) = 42
        _SpecularStrength("Specular Strength", Range(0, 4)) = 1.6
        [Toggle(_REFRACTION_ON)] _Refraction("Refraction", Float) = 1
        _RefractionStrength("Refraction Strength", Range(0, 0.08)) = 0.016
        _SoftFade("Soft Depth Fade", Range(0.001, 0.5)) = 0.05
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+10"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "LiquidStreamForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _REFRACTION_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_FlowMap);
            SAMPLER(sampler_FlowMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _RimColor;
                float4 _FlowMap_ST;
                half _FlowStrength;
                half _Opacity;
                half _RimPower;
                half _RimStrength;
                half _SpecularPower;
                half _SpecularStrength;
                half _Refraction;
                half _RefractionStrength;
                half _SoftFade;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // Rebuild the cylinder. across = -1 at one silhouette, +1 at the other.
                half across = input.uv.x * 2.0h - 1.0h;
                half acrossSquared = saturate(1.0h - across * across);
                half depthAcross = sqrt(acrossSquared);   // 1 at the centre, 0 at the edges

                half3 viewDirection = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));

                // Camera facing basis: the ribbon is always billboarded, so the cylinder normal
                // is the view direction rotated toward the silhouette edge.
                half3 up = half3(0, 1, 0);
                half3 side = normalize(cross(up, viewDirection));
                half3 normalWS = normalize(side * across + viewDirection * depthAcross);

                // Streaks running down the jet.
                half detail = SAMPLE_TEXTURE2D(_FlowMap, sampler_FlowMap, TRANSFORM_TEX(input.uv, _FlowMap)).r;
                half detailTerm = lerp(1.0h, detail, _FlowStrength);

                half rim = pow(1.0h - depthAcross, _RimPower) * _RimStrength;

                Light mainLight = GetMainLight();
                half3 halfDirection = SafeNormalize(mainLight.direction + viewDirection);
                half specular = pow(saturate(dot(normalWS, halfDirection)), _SpecularPower) * _SpecularStrength;

                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);

                half3 behind;
                #if defined(_REFRACTION_ON)
                    // Bend hardest at the edges of the cylinder, like a real lens of water.
                    float2 offset = float2(across * _RefractionStrength, 0.0);
                    behind = SampleSceneColor(saturate(screenUV + offset));
                #else
                    behind = SampleSceneColor(screenUV);
                #endif

                // input.color.rgb carries the per-pour liquid tint (set from C#, already blended
                // toward white by how strong the source liquid's own colour is - see
                // LiquidPourController.LiquidColor). Multiplying it straight in here is correct
                // because the tempering already happened once, upstream.
                half3 color = behind * _BaseColor.rgb * detailTerm * input.color.rgb;
                color += _RimColor.rgb * rim;
                color += mainLight.color * specular;

                // Soft fade where the stream meets whatever it is landing in.
                float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                half contactFade = saturate((sceneEye - input.positionCS.w) / max(_SoftFade, 0.001h));

                half alpha = input.color.a * _Opacity * _BaseColor.a;
                // Thicker through the middle of the cylinder, thinner at the silhouette.
                alpha *= saturate(depthAcross * 0.75h + 0.25h + rim * 0.35h);
                alpha *= contactFade;

                return half4(color, saturate(alpha));
            }
            ENDHLSL
        }
    }

    Fallback Off
}
