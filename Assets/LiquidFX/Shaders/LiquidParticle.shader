// Droplets, splash sheets, rings and bubbles.
//
// One shader for every particle in the system, kept deliberately cheap: a single texture fetch,
// a vertex-colour tint, and a soft depth fade so nothing shows a hard intersection line where it
// meets the water or the basin. GPU instancing is on, so all four systems batch.

Shader "LiquidFX/Liquid Particle"
{
    Properties
    {
        [MainTexture] _BaseMap("Particle Texture", 2D) = "white" {}
        [MainColor] _BaseColor("Tint", Color) = (1, 1, 1, 1)
        _SoftFade("Soft Depth Fade", Range(0.001, 0.5)) = 0.04
        _Brightness("Brightness", Range(0, 4)) = 1.15
        [Toggle(_ADDITIVE_ON)] _Additive("Additive Blending", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend("Source Blend", Float) = 5   // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend("Destination Blend", Float) = 10 // OneMinusSrcAlpha
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+20"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "LiquidParticleForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _ADDITIVE_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

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
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _SoftFade;
                half _Brightness;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 texel = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 color = texel * input.color * _BaseColor;
                color.rgb *= _Brightness;

                // Soft particles: fade out as the billboard approaches whatever is behind it.
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                half fade = saturate((sceneEye - input.positionCS.w) / max(_SoftFade, 0.001h));
                color.a *= fade;

                #if defined(_ADDITIVE_ON)
                    color.rgb *= color.a;
                #endif

                return color;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
