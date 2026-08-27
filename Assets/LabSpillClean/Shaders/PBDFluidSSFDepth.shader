Shader "Hidden/PBDFluid/SSFDepth"
{
    Properties
    {
        [HideInInspector] _FluidZTest ("Depth comparison", Float) = 4
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "Depth"
            ColorMask R
            ZWrite On
            ZTest [_FluidZTest]
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _Positions;   // xyz = posicao mundo
            StructuredBuffer<uint> _SubstanceIds;
            int      _SubstanceIndex;
            float    _UseSubstanceFilter;
            float    _Scale;                       // diametro (2*raio)
            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 sphereCoord : TEXCOORD0;
                nointerpolation float3 centerVS : TEXCOORD1;
                nointerpolation float valid : TEXCOORD2;
            };

            void setup()
            {
            }

            Varyings vert (Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                uint instanceId = 0;
#if UNITY_ANY_INSTANCING_ENABLED
                instanceId = unity_InstanceID;
#endif
                float3 centerWS = _Positions[instanceId].xyz;
                float3 centerVS = TransformWorldToView(centerWS);
                float radius = _Scale * 0.5;
                float2 corner = IN.positionOS.xy;
                float3 vertexVS = centerVS + float3(corner * radius, 0.0);
                Varyings o;
                o.positionHCS = TransformWViewToHClip(vertexVS);
                o.sphereCoord = corner;
                o.centerVS = centerVS;
                o.valid = 1.0;
#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
                if (_UseSubstanceFilter > 0.5 &&
                    _SubstanceIds[instanceId] != (uint)_SubstanceIndex)
                    o.valid = 0.0;
#endif
                return o;
            }

            struct FragmentOutput
            {
                float eye : SV_Target;
                float depth : SV_Depth;
            };

            FragmentOutput frag (Varyings IN)
            {
                clip(IN.valid - 0.5);
                float radiusSq = dot(IN.sphereCoord, IN.sphereCoord);
                clip(1.0 - radiusSq);

                float radius = _Scale * 0.5;
                float front = sqrt(saturate(1.0 - radiusSq)) * radius;
                float3 surfaceVS = IN.centerVS +
                    float3(IN.sphereCoord * radius, front);
                float eyeDepth = -surfaceVS.z;
                clip(eyeDepth - 1e-5);

                FragmentOutput output;
                output.eye = eyeDepth;
                float4 surfaceHCS = TransformWViewToHClip(surfaceVS);
                output.depth = surfaceHCS.z / surfaceHCS.w;
                return output;
            }
            ENDHLSL
        }

        // O depth pass acima preserva somente R. Este segundo pass carrega o
        // mesmo target e acumula em G a espessura de todas as particulas ao
        // longo do raio da camera. Ele nao usa o depth das outras particulas:
        // a soma precisa incluir tambem as camadas atras da superficie frontal.
        Pass
        {
            Name "Thickness"
            ColorMask G
            Blend One One
            BlendOp Add
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _Positions;
            StructuredBuffer<uint> _SubstanceIds;
            int      _SubstanceIndex;
            float    _UseSubstanceFilter;
            float    _Scale;                       // diametro visual em world space

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 sphereCoord : TEXCOORD0;
                nointerpolation float valid : TEXCOORD1;
                nointerpolation float centerEye : TEXCOORD2;
            };

            void setup()
            {
            }

            Varyings vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                uint instanceId = 0;
#if UNITY_ANY_INSTANCING_ENABLED
                instanceId = unity_InstanceID;
#endif
                float3 centerWS = _Positions[instanceId].xyz;
                float3 centerVS = TransformWorldToView(centerWS);
                float radius = _Scale * 0.5;
                float2 corner = IN.positionOS.xy;
                float3 vertexVS = centerVS + float3(corner * radius, 0.0);

                Varyings output;
                output.positionHCS = TransformWViewToHClip(vertexVS);
                output.sphereCoord = corner;
                output.valid = 1.0;
                output.centerEye = -centerVS.z;
#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
                if (_UseSubstanceFilter > 0.5 &&
                    _SubstanceIds[instanceId] != (uint)_SubstanceIndex)
                    output.valid = 0.0;
#endif
                return output;
            }

            float4 frag(Varyings IN) : SV_Target0
            {
                clip(IN.valid - 0.5);
                clip(IN.centerEye - 1e-5);
                float radiusSq = dot(IN.sphereCoord, IN.sphereCoord);
                clip(1.0 - radiusSq);

                // Corda completa da esfera atravessada por este raio. Como G
                // usa Blend One One, sobreposicoes viram espessura coletiva.
                float thicknessWS = sqrt(saturate(1.0 - radiusSq)) * _Scale;
                return float4(0.0, thicknessWS, 0.0, 0.0);
            }
            ENDHLSL
        }
    }
}
