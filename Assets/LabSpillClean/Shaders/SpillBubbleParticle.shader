Shader "LabSpill/Bubble Particle"
{
    Properties
    {
        [HDR] _TintColor("Tint", Color) = (1, 1, 1, 1)
        _Intensity("Brightness", Range(0, 3)) = 1.15
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent+100"
            "RenderType"="Transparent"
            "RenderPipeline"="UniversalPipeline"
            "IgnoreProjector"="True"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "Bubble"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
                half4 _TintColor;
                half _Intensity;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 p = input.uv * 2.0 - 1.0;
                float radius = length(p);
                clip(1.0 - radius);
                float outer = 1.0 - smoothstep(0.82, 1.0, radius);
                float rim = smoothstep(0.48, 0.82, radius) * outer;
                float highlight = 1.0 - smoothstep(0.0, 0.22,
                    length(p - float2(-0.34, 0.34)));
                float inner = saturate(1.0 - radius) * 0.10;
                float alpha = saturate(rim * 0.88 + highlight * 0.70 + inner)
                    * input.color.a * _TintColor.a;
                half3 colour = input.color.rgb * _TintColor.rgb * _Intensity;
                colour = lerp(colour, half3(1, 1, 1),
                    highlight * 0.72 + rim * 0.18);
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
