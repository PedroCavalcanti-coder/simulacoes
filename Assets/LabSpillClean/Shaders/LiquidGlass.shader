// ============================================================================
//  Liquid/Liquid  — URP port do shader Triple Axis (original Built-in).
//  Refracao/reflexo via reflection probe (unity_SpecCube0), igual ao caminho
//  default (_UseGrabpass=0) do original -> visual limpo, sem Opaque Texture.
//  Plano de corte (_Plane) e demais uniforms setados por LiquidContainer/Liquid.cs.
// ============================================================================
Shader "Liquid/Liquid"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _NormalMap("Normal Map", 2D) = "bump" {}
        _WavesTex("Waves", 2D) = "black" {}
        _PerlinNoise("Perlin Noise", 2D) = "black" {}
        _BubbleTex("Bubble", 2D) = "bump" {}
        _LiquidColor("Liquid Color", Color) = (1,1,1,1)
        _TopColor("Top Color", Color) = (1,1,1,1)
        _FoamColor("Foam Color", Color) = (1,1,1,1)
        _Refraction("Refraction Index", Float) = 1.33
        _ProbeLod("Murkiness", Float) = 0.05
        _Syrup("Syrup", Float) = 0
        _EdgeThickness("Edge Thickness", Float) = 0.02
        _FresnelPower("Fresnel Power", Float) = 1.5
        _MeniscusHeight("Meniscus Height", Float) = 0.04
        _MeniscusCurve("Meniscus Curve", Float) = 0.75
        _FoamAmount("Foam Amount", Float) = 1.0
        _BubbleScale("Bubble Scale", Float) = 1.0
        _BubbleCount("Maximum Bubbles", Float) = 30
        _UseGrabpass("Refraction Method", Float) = 0
        _Plane("Plane", Vector) = (0,1,0,0)
        _PlanePos("PlanePos", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        // refracao usa a Opaque Texture da cena (URP: "Opaque Texture" ON no RP Asset)
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

        TEXTURE2D(_MainTex);     SAMPLER(sampler_MainTex);
        TEXTURE2D(_NormalMap);   SAMPLER(sampler_NormalMap);
        TEXTURE2D(_WavesTex);    SAMPLER(sampler_WavesTex);
        TEXTURE2D(_BubbleTex);   SAMPLER(sampler_BubbleTex);
        TEXTURE2D(_PerlinNoise); SAMPLER(sampler_PerlinNoise);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            float  _Refraction;
            float4 _LiquidColor, _TopColor, _FoamColor;
            float  _BoundsL, _BoundsH, _BoundsX, _BoundsZ;
            float  _ProbeLod, _EdgeThickness, _WavesMult, _FresnelIntensity, _FresnelPower;
            float  _MeshScale, _MeniscusHeight, _MeniscusCurve, _Syrup, _Foam, _FoamAmount;
            float  _BubbleScale, _BubbleCount, _UseGrabpass;
            float4 _Plane;
            float3 _PlanePos;
        CBUFFER_END

        struct appdata { float3 normal:NORMAL; float4 vertex:POSITION; float2 uv:TEXCOORD0; float4 tangent:TANGENT; };
        struct v2f
        {
            float2 uv:TEXCOORD0; float4 pos:SV_POSITION; float3 worldPos:TEXCOORD1;
            float4 screenPos:TEXCOORD2; float3 viewDir:TEXCOORD4; float3 normal:TEXCOORD5;
            half3 tspace0:TEXCOORD6; half3 tspace1:TEXCOORD7; half3 tspace2:TEXCOORD8; float fogCoord:TEXCOORD9;
        };

        half3 SampleProbe(float3 dir, float lod)
        {
            half4 enc = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, dir, lod);
            return DecodeHDREnvironment(enc, unity_SpecCube0_HDR);
        }
        half3 AmbientColor() { return unity_AmbientSky.rgb; }

        float3 GetLighting(float type, float3 worldPos, float3 normal, float shininess)
        {
            Light L = GetMainLight();
            float3 lightDir = L.direction;
            float3 viewDir = normalize(GetCameraPositionWS() - worldPos);
            normal = normalize(normal);
            float3 diffuse = L.color * max(0.0, dot(normal, lightDir));
            shininess = clamp(shininess, 1, 1000);
            float3 refl = reflect(lightDir, normal);
            float spec = pow(saturate(dot(refl, -viewDir)), shininess);
            return (type == 0) ? spec * L.color : diffuse;
        }
        float3 GetLighting(float type, float3 worldPos, float3 normal) { return GetLighting(type, worldPos, normal, 0); }

        float4 BiplanarTex(TEXTURE2D_PARAM(tex, samp), float3 wp, float2 scale, float3 offset)
        {
            float4 x = SAMPLE_TEXTURE2D(tex, samp, (wp.yz + offset.yz) * scale);
            float4 z = SAMPLE_TEXTURE2D(tex, samp, (wp.xy + offset.xy) * scale);
            return x + z;
        }
        float4 TriplanarTex(TEXTURE2D_PARAM(tex, samp), float3 wp, float3 normal, float2 scale, float3 offset)
        {
            normal = abs(normal);
            float3 w = normal / (normal.x + normal.y + normal.z);
            float4 x = SAMPLE_TEXTURE2D(tex, samp, (wp.yz + offset.yz) * scale);
            float4 y = SAMPLE_TEXTURE2D(tex, samp, (wp.xz + offset.xz) * scale);
            float4 z = SAMPLE_TEXTURE2D(tex, samp, (wp.xy + offset.xy) * scale);
            return w.x * x + w.y * y + w.z + z;
        }
        float GetFresnel(float3 normal, float3 viewDir, float facing, float power, float intensity)
        {
            float dp = 1 - pow(saturate(dot(normal, normalize(facing * viewDir))), power) * intensity;
            return saturate(smoothstep(0.5, 1.0, dp));
        }
        float CalculateWaves(v2f i, float facing)
        {
            float fresnel = GetFresnel(i.normal, i.viewDir, facing, _MeniscusCurve, 0.5);
            float3 worldOrigin = unity_ObjectToWorld._m03_m13_m23;
            float4 wavesTex = BiplanarTex(TEXTURE2D_ARGS(_WavesTex, sampler_WavesTex), i.worldPos, 0.25 / _MeshScale, -_Time.x * 10 - worldOrigin);
            float waves = saturate(wavesTex.rgb) - 0.5;
            waves = waves * 0.005 * pow(_WavesMult, 5) * (1 + fresnel) * _MeshScale - (_WavesMult - 1) * 0.1;
            return waves;
        }

        v2f vertex(appdata v, float facing)
        {
            v2f o = (v2f)0;
            VertexPositionInputs p = GetVertexPositionInputs(v.vertex.xyz);
            VertexNormalInputs n = GetVertexNormalInputs(v.normal, v.tangent);
            o.tspace0 = half3(n.tangentWS.x, n.bitangentWS.x, n.normalWS.x);
            o.tspace1 = half3(n.tangentWS.y, n.bitangentWS.y, n.normalWS.y);
            o.tspace2 = half3(n.tangentWS.z, n.bitangentWS.z, n.normalWS.z);
            o.worldPos = p.positionWS;
            o.pos = p.positionCS;
            o.screenPos = p.positionNDC;
            o.uv = TRANSFORM_TEX(v.uv, _MainTex);
            o.normal = n.normalWS;
            o.viewDir = normalize(GetWorldSpaceViewDir(p.positionWS));
            o.fogCoord = ComputeFogFactor(p.positionCS.z);
            return o;
        }

        float4 fragment(v2f i, float facing)
        {
            float4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
            float4 colorAdd = float4(0, 0, 0, 0);
            float fresnel = GetFresnel(i.normal, i.viewDir, facing, _MeniscusCurve, 0.5);
            float height = (_BoundsH - _BoundsL);
            float waves = CalculateWaves(i, facing);

            // plano de corte (nivel do liquido)
            float distance = dot(i.worldPos, _Plane.xyz);
            distance += _Plane.w + waves / (_WavesMult + 1) / (_WavesMult + 1);

            // menisco
            float increment = _EdgeThickness * 0.33;
            float edgeOffset = fresnel * _MeniscusHeight + _EdgeThickness;
            colorAdd = lerp(float4(0,0,0,0), float4(0.35,0.35,0.35,0), saturate((distance - edgeOffset + increment*3)*75));
            colorAdd = lerp(colorAdd, float4(-0.35,-0.35,-0.35,0), saturate((distance - edgeOffset + increment*2.5)*75));
            colorAdd = lerp(colorAdd, float4(0,0,0,-0.5), saturate((distance - edgeOffset + increment*1.6)*75));
            colorAdd = lerp(colorAdd, float4(0,0,0,-0.5), saturate((distance - edgeOffset + increment*waves*20)*75));
            colorAdd = lerp(colorAdd, float4(0,0,0,-1), saturate((distance - edgeOffset + increment*waves*25)*75));

            // normais
            float3 worldOrigin = unity_ObjectToWorld._m03_m13_m23;
            float4 normalMapB = BiplanarTex(TEXTURE2D_ARGS(_NormalMap, sampler_NormalMap), i.worldPos, 1, -worldOrigin);
            half3 tangentNormal = (facing > 0)
                ? UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, i.uv))
                : UnpackNormal(normalMapB);
            half3 worldNormal;
            worldNormal.x = dot(i.tspace0, tangentNormal);
            worldNormal.y = dot(i.tspace1, tangentNormal);
            worldNormal.z = dot(i.tspace2, tangentNormal);

            // bolhas
            float4 bubbles = 0;
            float bubbleDistance = saturate(distance * 3 + 1);
            float numBubbles = _BubbleCount * (_WavesMult/2 - 1);
            numBubbles = clamp(numBubbles, 0, _BubbleCount);
            float perlin = BiplanarTex(TEXTURE2D_ARGS(_PerlinNoise, sampler_PerlinNoise), i.worldPos, 2, float3(_SinTime.x+1, _CosTime.z+2, _SinTime.y+3)).r;
            [loop]
            for (int j = 1; j < numBubbles; j++)
            {
                float3 bubblePos = float3(sin(_Time.w + j) * _BoundsX/3 + perlin/40,
                    height/2 - ((_Time.y + j*(height*0.1)) % lerp(0, height, _PlanePos.y + 0.55)),
                    cos(_Time.w - j) * _BoundsZ/3 + perlin/40) - worldOrigin;
                float2 bubbleScale = 25.0 / _BubbleScale + j;
                float4 b0 = BiplanarTex(TEXTURE2D_ARGS(_BubbleTex, sampler_BubbleTex), i.worldPos, bubbleScale, bubblePos);
                float4 b1 = BiplanarTex(TEXTURE2D_ARGS(_BubbleTex, sampler_BubbleTex), i.worldPos, bubbleScale*(1.0/j+3), bubblePos+0.01);
                float4 b2 = BiplanarTex(TEXTURE2D_ARGS(_BubbleTex, sampler_BubbleTex), i.worldPos, bubbleScale*(1.0/j+2), bubblePos-0.02);
                bubbles.rgb += b0.rgb*b0.a + b1.rgb*b1.a + b2.rgb*b2.a;
                bubbles.a += (b0.a + b1.a + b2.a);
            }

            // normal da superficie (topo) vs lateral
            half3 surfNormal = worldNormal;
            half3 topNormal = normalize(half3(_Plane.x + waves*10, _Plane.y, _Plane.z + waves*10));
            if (facing < 0)
            {
                surfNormal = topNormal;
                surfNormal = lerp(surfNormal, -worldNormal, saturate((distance - edgeOffset + increment*3)*25));
            }
            else
            {
                surfNormal = lerp(surfNormal, topNormal, saturate((distance - edgeOffset + increment*3)*25));
                surfNormal = lerp(surfNormal, worldNormal, saturate((distance - edgeOffset + _EdgeThickness/3)*100));
                surfNormal *= (bubbles.rgb * bubbleDistance * 4 + 1);
            }

            float refraction_idx = lerp(_Refraction, _Refraction + 0.5, saturate((distance - edgeOffset + increment*3)*25));

            // refracao pela Opaque Texture (cena atras) + reflexo pelo probe/skybox.
            // Em URP o unity_SpecCube0 aqui volta preto -> refracao usa a cena (mais fiel).
            float2 suv = i.screenPos.xy / i.screenPos.w;
            float3 refractedDirection = refract(-normalize(i.viewDir), normalize(surfNormal), 1.0 / refraction_idx);
            float3 reflectedDirection = reflect(i.viewDir, normalize(surfNormal));
            half3 refraction = SampleSceneColor(suv + refractedDirection.xy * 0.05);
            half3 reflection = SampleProbe(-reflectedDirection, _ProbeLod * 6.0);

            col.rgb *= refraction * (1 - _Syrup);
            col.rgb += _Syrup;

            float shininess = 30 * (1 - _ProbeLod);
            float3 specularReflection = GetLighting(0, i.worldPos, surfNormal, shininess);
            float3 diffuseReflection = GetLighting(1, i.worldPos, surfNormal);
            float3 ambientLighting = AmbientColor();

            float foamClamped = clamp(_Foam, 0, _FoamAmount * 0.03);
            float4 noise = TriplanarTex(TEXTURE2D_ARGS(_WavesTex, sampler_WavesTex), i.worldPos, surfNormal, float2(1,1), float3(0, waves, 0));

            if (facing > 0)
            {
                float refresnel = GetFresnel(i.normal, i.viewDir, facing, _FresnelPower, 1);
                col.rgb *= (1 - refresnel);
                col.rgb += reflection * refresnel;
                col.rgb *= _LiquidColor.rgb;
                col += colorAdd;
                col.rgb += specularReflection;
                col.rgb -= bubbles.a / 4 * bubbleDistance;
                col.rgb += saturate(bubbles.rgb) / 4 * bubbleDistance;
                col = lerp(col, _FoamColor * float4((diffuseReflection + ambientLighting)*noise.rgb*0.25+0.5, 1), saturate((distance - edgeOffset + _EdgeThickness/3)*100) * saturate(foamClamped*100));
                col = lerp(col, float4(col.r,col.g,col.b,0), saturate((distance/6 - foamClamped - edgeOffset/6 + _EdgeThickness/18)*600));
            }
            else
            {
                float bfFresnel = pow(1 + dot(-normalize(i.viewDir), _Plane.xyz), _FresnelPower * 0.35);
                col.rgb *= (1 - bfFresnel);
                col.rgb += reflection * bfFresnel;
                col.rgb *= _TopColor.rgb;
                col = lerp(col, float4(col.r,col.g,col.b,0), saturate((distance - edgeOffset + increment*1.6)*100));
                col.rgb += specularReflection * bfFresnel;
                col.a = lerp(col.a, 1, saturate((distance - edgeOffset + _EdgeThickness)*100));
                col.a = lerp(col.a, 0, saturate((distance/6 - foamClamped - edgeOffset/6 + _EdgeThickness/18)*600));
                col.rgb = lerp(col.rgb, _FoamColor.rgb * (bfFresnel*0.5+0.5), saturate(foamClamped*100));
            }

            if (col.a <= 0) discard;
            col.rgb = MixFog(col.rgb, i.fogCoord);
            return col;
        }
        ENDHLSL

        // back faces (superficie do liquido)
        Pass
        {
            Name "LiquidBack"
            Tags { "LightMode"="UniversalForward" }
            Cull Front
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma target 3.5

            v2f vert(appdata v) { return vertex(v, -1); }
            half4 frag(v2f i, FRONT_FACE_TYPE vf : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float facing = IS_FRONT_VFACE(vf, 1.0, -1.0);
                return fragment(i, facing);
            }
            ENDHLSL
        }

        // front faces
        Pass
        {
            Name "LiquidFront"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma target 3.5

            v2f vert(appdata v) { return vertex(v, 1); }
            half4 frag(v2f i, FRONT_FACE_TYPE vf : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float facing = IS_FRONT_VFACE(vf, 1.0, -1.0);
                return fragment(i, facing);
            }
            ENDHLSL
        }
    }
}
