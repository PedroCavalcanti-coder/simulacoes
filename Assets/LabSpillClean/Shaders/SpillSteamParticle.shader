Shader "LabSpill/Steam Particle"
{
    Properties
    {
        [MainTexture] _BaseMap("Smoke", 2D) = "white" {}
        [HDR] _TintColor("Tint", Color) = (1, 1, 1, 1)
        _Intensity("Brightness", Range(0, 2)) = 0.85
        [HideInInspector] _ZTest("Depth Test", Float) = 4
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent+80"
            "RenderType"="Transparent"
            "RenderPipeline"="UniversalPipeline"
            "IgnoreProjector"="True"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        ZTest [_ZTest]

        Pass
        {
            Name "Steam"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _TintColor;
                half _Intensity;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 smoke = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                float2 p = input.uv * 2.0 - 1.0;
                half softDisc = 1.0 - smoothstep(0.16, 1.0, length(p));
                half textureVariation = lerp(0.68, 1.0,
                    saturate(max(smoke.r, smoke.a)));
                half mask = softDisc * textureVariation;
                half alpha = mask * input.color.a * _TintColor.a;
                half3 colour = input.color.rgb * _TintColor.rgb
                    * lerp(0.55, 1.0, smoke.r) * _Intensity;
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
