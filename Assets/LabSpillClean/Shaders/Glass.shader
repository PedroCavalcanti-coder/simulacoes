Shader "PBDFluid/Glass"
{
    Properties
    {
        _Tint("Tint (corpo)", Color) = (0.85, 0.92, 1.0, 1)
        _ReflectColor("Reflexo (borda)", Color) = (0.9, 0.95, 1.0, 1)
        _FresnelPower("Fresnel Power", Range(0.5, 8)) = 5
        _FresnelStrength("Fresnel Strength", Range(0, 1)) = 0.9
        _Smoothness("Smoothness", Range(0, 1)) = 0.92
        _SpecIntensity("Specular", Range(0, 4)) = 1.5
        _Alpha("Alpha base", Range(0, 1)) = 0.08
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        LOD 200

        Pass
        {
            Name "Glass"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float4 _ReflectColor;
                float  _FresnelPower;
                float  _FresnelStrength;
                float  _Smoothness;
                float  _SpecIntensity;
                float  _Alpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   n = GetVertexNormalInputs(IN.normalOS);
                o.positionHCS = p.positionCS;
                o.positionWS  = p.positionWS;
                o.normalWS    = n.normalWS;
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                float  fres = pow(1.0 - saturate(dot(N, V)), _FresnelPower) * _FresnelStrength;

                Light light = GetMainLight(TransformWorldToShadowCoord(IN.positionWS));
                float3 H = normalize(light.direction + V);
                float  spec = pow(saturate(dot(N, H)), lerp(16.0, 400.0, _Smoothness)) * _SpecIntensity;

                // corpo do vidro = leve tint; bordas = reflexo (fresnel); brilho especular.
                // alpha baixo no centro -> ve o fluido DENTRO (blend); alto nas bordas.
                float3 col = lerp(_Tint.rgb, _ReflectColor.rgb, saturate(fres));
                col += spec * light.color * light.shadowAttenuation;

                float alpha = saturate(_Alpha + fres + spec);
                return float4(col, alpha);
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
