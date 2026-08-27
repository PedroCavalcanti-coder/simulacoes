// Spilled liquid lying on a surface.
//
// A puddle is mostly a change to the surface underneath it: darker toward the middle where it is
// deepest, glossier, with a bright thin rim where surface tension holds the edge up. So this
// samples what is already on screen behind the disc and darkens/highlights it, rather than being
// its own opaque material.
//
// The mesh itself is a real disc (see LiquidFXBuilder.CreateDiscMesh), not a flat quad cropped by
// a radial discard: a discard boundary hiding the corners of a 4-vertex quad is exactly what
// used to make this read as a hard-edged square instead of a puddle. Discard is still used here,
// but only to carve organic irregularity into an already-round shape and to shrink it while it
// dries, never to manufacture the outer boundary from scratch.
//
// The disc's extra rings also let the vertex stage bulge it into a shallow dome, which is what
// "_PuddleDepth" is for: real vertical extent instead of a paper-flat decal, plus an analytic
// normal derived from that bulge so the specular highlight breaks up into something that reads as
// a lens of liquid rather than a flat mirror streak.
//
// _Growth reveals the puddle from the centre outward as liquid arrives.
// _Dryness eats it back from the rim inward, which is how a real puddle evaporates, and is what
// lets the C# side switch the renderer off entirely once the value reaches 1.

Shader "LiquidFX/Liquid Puddle"
{
    Properties
    {
        _BaseColor("Liquid Color", Color) = (0.55, 0.82, 0.9, 0.85)
        _NoiseMap("Edge Noise (R)", 2D) = "white" {}
        _NoiseScale("Edge Noise Scale", Range(0.5, 8)) = 2.6
        _EdgeIrregularity("Edge Irregularity", Range(0, 0.5)) = 0.22
        _RimWidth("Rim Width", Range(0.005, 0.3)) = 0.06
        _RimBrightness("Rim Brightness", Range(0, 3)) = 1.2
        _Darkening("Wet Darkening", Range(0, 1)) = 0.45
        _SpecularPower("Specular Power", Range(8, 256)) = 110
        _SpecularStrength("Specular Strength", Range(0, 4)) = 1.5
        _PuddleDepth("Puddle Depth (world metres)", Range(0, 0.02)) = 0.006
        _BulgeNormalStrength("Bulge Normal Strength", Range(0, 4)) = 1.4
        _Growth("Growth", Range(0, 1)) = 1
        _Dryness("Dryness", Range(0, 1)) = 0
        _Seed("Seed", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-10"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "LiquidPuddleForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_NoiseMap);
            SAMPLER(sampler_NoiseMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _NoiseMap_ST;
                half _NoiseScale;
                half _EdgeIrregularity;
                half _RimWidth;
                half _RimBrightness;
                half _Darkening;
                half _SpecularPower;
                half _SpecularStrength;
                half _PuddleDepth;
                half _BulgeNormalStrength;
                half _Growth;
                half _Dryness;
                float _Seed;
            CBUFFER_END

            // Dome profile shared by the vertex bulge and the fragment normal/darkening: 1 at the
            // centre, 0 at the rim. Its derivative is what makes the rim slope down into the floor
            // instead of the whole disc floating as a flat plate.
            half BulgeProfile(half radius01)
            {
                half r = saturate(radius01);
                return saturate(1.0h - r * r);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float2 centred = input.uv - 0.5;
                float radius = length(centred) * 2.0; // 0 at centre, 1 at the disc rim

                half fillAmount = saturate(_Growth) * (1.0h - saturate(_Dryness));
                half bulge = BulgeProfile(radius) * fillAmount;

                float3 positionOS = input.positionOS.xyz;
                positionOS.y += bulge * _PuddleDepth;

                VertexPositionInputs positions = GetVertexPositionInputs(positionOS);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float2 centred = input.uv - 0.5;
                float radiusRaw = length(centred) * 2.0; // 0 at centre, 1 at the disc rim
                float2 radialDir = radiusRaw > 0.0001 ? centred / (radiusRaw * 0.5) : float2(0.0, 0.0);

                // Break the circle up so the puddle never reads as a perfectly round decal.
                float2 noiseUv = input.uv * _NoiseScale + _Seed;
                half noise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, noiseUv).r;
                float radius = radiusRaw + (noise - 0.5) * _EdgeIrregularity;

                // Growth opens the puddle outward; dryness closes it back in from the rim. Because
                // the mesh itself is already a circle, this only ever has to carve a slightly
                // smaller or irregular circle out of a circle, not fake roundness from a square.
                half outerEdge = lerp(0.05h, 1.0h, saturate(_Growth)) * (1.0h - saturate(_Dryness) * 0.92h);
                half shape = 1.0h - smoothstep(outerEdge - 0.06h, outerEdge, radius);
                if (shape <= 0.001h)
                    discard;

                // Drying also thins the film in patches before the edge reaches them.
                half thinning = 1.0h - saturate(_Dryness) * saturate(noise * 1.4h);
                shape *= saturate(thinning + 0.15h);

                half fillAmount = saturate(_Growth) * (1.0h - saturate(_Dryness));
                half bulge = BulgeProfile(radiusRaw) * fillAmount;

                // Analytic slope of the dome (derivative of 1 - r^2 is -2r), tilting the normal
                // away from flat toward the rim. This is what turns the specular highlight from a
                // single hard streak across a flat plane into something that rounds off like a
                // real lens of liquid.
                half slope = radiusRaw * _BulgeNormalStrength * fillAmount;
                half3 normalOS = normalize(half3(-radialDir.x * slope, 1.0h, -radialDir.y * slope));
                half3 normalWS = normalize(TransformObjectToWorldNormal(normalOS));

                half3 viewDirection = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);

                // A small refraction-like bend sourced from the same slope, strongest at the rim,
                // so the edge of the puddle visibly bends the floor behind it like a real thin lens.
                float2 refractionOffset = normalWS.xz * 0.01 * fillAmount;
                half3 behind = SampleSceneColor(saturate(screenUV + refractionOffset));

                // Deeper toward the centre (proxied by the bulge), lighter and thinner at the rim -
                // the same cue that makes a real puddle look like it has volume instead of being a
                // uniform stain.
                half depthDarkening = _Darkening * (0.55h + 0.45h * bulge);
                half3 wet = behind * lerp(1.0h, 1.0h - depthDarkening, shape) * _BaseColor.rgb;

                Light mainLight = GetMainLight();
                half3 halfDirection = SafeNormalize(mainLight.direction + viewDirection);
                half specular = pow(saturate(dot(normalWS, halfDirection)), _SpecularPower) * _SpecularStrength;

                // Surface tension holds a brighter, thicker bead at the rim.
                half rim = smoothstep(outerEdge - _RimWidth, outerEdge - _RimWidth * 0.25h, radius) * shape;

                half3 color = wet + mainLight.color * specular * shape + rim * _RimBrightness * _BaseColor.rgb;
                half alpha = saturate(shape * _BaseColor.a + rim * 0.35h);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
