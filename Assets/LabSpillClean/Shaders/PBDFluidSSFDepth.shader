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
            // Alvo 0 guarda a profundidade em R; alvo 1, a substancia vencedora.
            // O ZTest escolhe o fragmento mais proximo, entao a substancia gravada e
            // sempre a da particula que realmente aparece naquele pixel.
            ColorMask R 0
            ColorMask R 1
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
            float    _Scale;                       // diametro (2*raio)
            float    _SubstanceEncode;             // 1/255: id -> canal de 8 bits

            // Slot morto carrega este id. Ver FluidPool.
            static const uint DEAD_SUBSTANCE = 0xFFFFFFFF;

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
                nointerpolation float substance : TEXCOORD2;
            };

            void setup()
            {
            }

            // Manda o quad inteiro para fora do frustum. Descartar a particula morta
            // com clip() no fragmento custava a rasterizacao completa do quad antes do
            // descarte; aqui o triangulo some antes de virar pixel.
            float4 CullVertex()
            {
                return float4(-2.0, -2.0, 0.5, 1.0);
            }

            Varyings vert (Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                uint instanceId = 0;
#if UNITY_ANY_INSTANCING_ENABLED
                instanceId = unity_InstanceID;
#endif
                uint substance = _SubstanceIds[instanceId];

                float3 centerWS = _Positions[instanceId].xyz;
                float3 centerVS = TransformWorldToView(centerWS);
                float radius = _Scale * 0.5;
                float2 corner = IN.positionOS.xy;
                float3 vertexVS = centerVS + float3(corner * radius, 0.0);

                Varyings o;
                o.positionHCS = substance == DEAD_SUBSTANCE
                    ? CullVertex()
                    : TransformWViewToHClip(vertexVS);
                o.sphereCoord = corner;
                o.centerVS = centerVS;
                o.substance = (float)substance * _SubstanceEncode;
                return o;
            }

            struct FragmentOutput
            {
                float eye : SV_Target0;
                float substance : SV_Target1;
                float depth : SV_Depth;
            };

            FragmentOutput frag (Varyings IN)
            {
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
                output.substance = IN.substance;
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
            float    _Scale;                       // diametro visual em world space

            static const uint DEAD_SUBSTANCE = 0xFFFFFFFF;

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 sphereCoord : TEXCOORD0;
                nointerpolation float centerEye : TEXCOORD1;
            };

            void setup()
            {
            }

            float4 CullVertex()
            {
                return float4(-2.0, -2.0, 0.5, 1.0);
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
                output.positionHCS = _SubstanceIds[instanceId] == DEAD_SUBSTANCE
                    ? CullVertex()
                    : TransformWViewToHClip(vertexVS);
                output.sphereCoord = corner;
                output.centerEye = -centerVS.z;
                return output;
            }

            float4 frag(Varyings IN) : SV_Target0
            {
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
