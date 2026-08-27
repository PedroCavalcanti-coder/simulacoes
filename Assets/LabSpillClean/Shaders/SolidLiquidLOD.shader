Shader "Liquid/Solid Layer LOD"
{
    Properties
    {
        [HideInInspector] _SurfacePlane("Surface Plane", Vector) = (0,1,0,0)
        [HideInInspector] _LayerBottomPlane("Bottom Plane", Vector) = (0,1,0,0)
        [HideInInspector] _HasLayerBottom("Has Bottom", Float) = 0
        [HideInInspector] _Volume01("Volume", Range(0,1)) = 0
        _SolidColor("Solid Color", Color) = (0.2,0.55,0.7,0.72)
        [HideInInspector] _InteriorColor("Interior Color", Color) = (0.2,0.55,0.7,1)
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "SolidLiquidLOD"
            Tags { "LightMode"="UniversalForward" }
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _SurfacePlane;
                float4 _LayerBottomPlane;
                half4 _SolidColor;
                half4 _InteriorColor;
                float _HasLayerBottom;
                float _Volume01;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            half4 Frag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                clip(_Volume01 - 1e-5);
                clip(-(dot(_SurfacePlane.xyz, input.positionWS) + _SurfacePlane.w));
                float bottomDistance = dot(_LayerBottomPlane.xyz, input.positionWS) + _LayerBottomPlane.w;
                clip(lerp(1.0, bottomDistance, saturate(_HasLayerBottom)));
                return IS_FRONT_VFACE(frontFace, _SolidColor, _InteriorColor);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
