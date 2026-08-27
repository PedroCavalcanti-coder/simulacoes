// Art-directable calibrated liquid for URP.
// The runtime supplies a world-space free-surface plane and the motion state.
Shader "Liquid/Calibrated Realistic Liquid"
{
    Properties
    {
        [Header(Volume Runtime Contract)]
        [HideInInspector] _SurfacePlane("World Surface Plane", Vector) = (0, 1, 0, 0)
        [HideInInspector] _LayerBottomPlane("World Layer Bottom Plane", Vector) = (0, 1, 0, 0)
        [HideInInspector] _HasLayerBottom("Has Layer Bottom", Float) = 0
        [HideInInspector] _LayerWaveStrength("Layer Wave Strength", Range(0, 1)) = 1
        [HideInInspector] _InteriorColor("Interior Color", Color) = (0.2, 0.55, 0.7, 1)
        [HideInInspector] _Volume01("Calibrated Volume", Range(0, 1)) = 0.5
        [HideInInspector] _WaveAmplitude("Dynamic Wave Amplitude", Float) = 0
        [HideInInspector] _WavePhase("Dynamic Wave Phase", Float) = 0
        [HideInInspector] _WaveDirection("Dynamic Wave Direction", Vector) = (1, 0, 0, 0)
        [HideInInspector] _VerticalWaveAmplitude("Vertical Wave Amplitude", Float) = 0
        [HideInInspector] _WaveOrigin("World Wave Center XYZ And Inner Radius", Vector) = (0, 0, 0, 0.1)
        [HideInInspector] _IsSurfaceMesh("Is Generated Surface", Float) = 0
        [HideInInspector] _Viscosity01("Normalized Viscosity", Range(0, 1)) = 0

        [Header(Colour And Absorption)]
        _SurfaceColor("Surface Colour", Color) = (0.72, 0.96, 0.90, 0.9)
        _BodyColor("Body Colour", Color) = (0.16, 0.68, 0.69, 0.68)
        _DeepColor("Deep Colour", Color) = (0.035, 0.25, 0.29, 1)
        _AbsorptionColor("Transmission Colour", Color) = (0.24, 0.76, 0.72, 1)
        _AbsorptionDensity("Absorption Density", Range(0, 12)) = 2.6
        _DepthGradient("Vertical Gradient", Range(0, 8)) = 2.4
        _Turbidity("Turbidity", Range(0, 1)) = 0.18
        _Transparency("Transparency", Range(0, 1)) = 0.48
        _MaxOpticalDepth("Maximum Optical Depth", Range(0.05, 5)) = 1.5

        [Header(Refraction And Lighting)]
        _IOR("Index Of Refraction", Range(1, 2.5)) = 1.333
        _RefractionStrength("Refraction Strength", Range(0, 0.08)) = 0
        _TransmissionStrength("Scene Transmission", Range(0, 1)) = 0
        _Smoothness("Smoothness", Range(0, 1)) = 0.82
        _SpecularIntensity("Specular Intensity", Range(0, 4)) = 0.3
        _ReflectionIntensity("Skybox Reflection", Range(0, 2)) = 0
        _AmbientIntensity("Ambient Intensity", Range(0, 2)) = 0.75
        _LightingInfluence("Lighting Influence", Range(0, 1)) = 0.3
        _NormalSeamSmoothing("Vertical Seam Smoothing", Range(0, 1)) = 0.9
        _FresnelPower("Fresnel Power", Range(0.5, 8)) = 4.5
        _FresnelIntensity("Fresnel Intensity", Range(0, 2)) = 0.8

        [Header(Wave Detail)]
        _MaximumWaveHeight("Maximum Dynamic Wave Height", Range(0, 0.08)) = 0.03
        _MaximumVerticalWaveHeight("Vertical Motion Wave Height", Range(0, 0.08)) = 0.026
        _WaveFrequency("Wave Frequency", Range(0.1, 30)) = 8.5
        _WaveIrregularity("Wave Irregularity", Range(0, 1)) = 0.55
        _WaveDetail("Wave Detail", Range(0, 1)) = 0.7
        _MicroWaveAmplitude("Calm Micro Waves", Range(0, 0.01)) = 0.0012
        _MicroWaveSpeed("Micro Wave Speed", Range(0, 5)) = 0.35

        [Header(Meniscus And Foam)]
        _MeniscusWidth("Meniscus Width", Range(0.0005, 0.08)) = 0.012
        _MeniscusStrength("Meniscus Strength", Range(0, 2)) = 0.85
        _SurfaceBandWidth("Rear Surface Band", Range(0.0005, 0.05)) = 0.009
        _SurfaceOpacity("Surface Band Opacity", Range(0, 1)) = 0.78
        _FoamWidth("Foam Width", Range(0.0005, 0.05)) = 0.006
        _FoamAmount("Foam Amount", Range(0, 1)) = 0.08
        _FoamColor("Foam Colour", Color) = (0.86, 0.98, 1, 0.9)

        [Header(Phosphorescence And Playful Mode)]
        [HDR] _EmissionColor("Emission Colour", Color) = (0.08, 0.9, 0.62, 1)
        _EmissionStrength("Phosphorescence", Range(0, 10)) = 0
        _EmissionPulseAmount("Emission Pulse", Range(0, 1)) = 0
        _EmissionPulseFrequency("Pulse Frequency", Range(0, 12)) = 1
        [HDR] _PlayfulColor("Playful Rim Colour", Color) = (0.2, 0.8, 1.4, 1)
        _PlayfulAmount("Playful Mode", Range(0, 1)) = 0
        _Iridescence("Iridescence", Range(0, 1)) = 0
        _Sparkle("Sparkle", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
            "RenderPipeline"="UniversalPipeline"
            "IgnoreProjector"="True"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _SurfacePlane;
            float4 _LayerBottomPlane;
            float4 _WaveDirection;
            float4 _WaveOrigin;
            float4 _SurfaceColor;
            float4 _BodyColor;
            float4 _InteriorColor;
            float4 _DeepColor;
            float4 _AbsorptionColor;
            float4 _FoamColor;
            float4 _EmissionColor;
            float4 _PlayfulColor;
            float _Volume01;
            float _WaveAmplitude;
            float _VerticalWaveAmplitude;
            float _WavePhase;
            float _Viscosity01;
            float _IsSurfaceMesh;
            float _HasLayerBottom;
            float _LayerWaveStrength;
            float _AbsorptionDensity;
            float _DepthGradient;
            float _Turbidity;
            float _Transparency;
            float _MaxOpticalDepth;
            float _IOR;
            float _RefractionStrength;
            float _TransmissionStrength;
            float _Smoothness;
            float _SpecularIntensity;
            float _ReflectionIntensity;
            float _AmbientIntensity;
            float _LightingInfluence;
            float _NormalSeamSmoothing;
            float _FresnelPower;
            float _FresnelIntensity;
            float _MaximumWaveHeight;
            float _MaximumVerticalWaveHeight;
            float _WaveFrequency;
            float _WaveIrregularity;
            float _WaveDetail;
            float _MicroWaveAmplitude;
            float _MicroWaveSpeed;
            float _MeniscusWidth;
            float _MeniscusStrength;
            float _SurfaceBandWidth;
            float _SurfaceOpacity;
            float _FoamWidth;
            float _FoamAmount;
            float _EmissionStrength;
            float _EmissionPulseAmount;
            float _EmissionPulseFrequency;
            float _PlayfulAmount;
            float _Iridescence;
            float _Sparkle;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            float4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float4 screenPos : TEXCOORD2;
            half fogFactor : TEXCOORD3;
            half3 seamlessNormalWS : TEXCOORD4;
            float2 uv : TEXCOORD5;
            half surfaceFlag : TEXCOORD6;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        float WaveHeight(float3 positionWS);

        Varyings Vert(Attributes input)
        {
            Varyings output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

            VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
            float surfaceFlag = max(_IsSurfaceMesh, input.color.a);
            if (surfaceFlag > 0.5)
            {
                // A superficie e o recorte do corpo precisam usar exatamente a
                // mesma onda. Travar o aro deixava a lateral se mover sozinha e
                // abria uma fresta muito maior que a saia de vedacao.
                positionInputs.positionWS -= SafeNormalize(_SurfacePlane.xyz)
                    * WaveHeight(positionInputs.positionWS);
                positionInputs.positionCS = TransformWorldToHClip(positionInputs.positionWS);
            }
            output.positionCS = positionInputs.positionCS;
            output.positionWS = positionInputs.positionWS;
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.screenPos = ComputeScreenPos(positionInputs.positionCS);
            output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
            output.uv = input.uv;
            output.surfaceFlag = surfaceFlag;

            // Rotational containers commonly duplicate vertices at their UV seam.
            // Rebuilding only the radial part keeps the slope from the authored
            // normal while making illumination continuous across that seam.
            float radialLength = length(input.positionOS.xz);
            float2 radialDirection = radialLength > 1e-5
                ? input.positionOS.xz / radialLength
                : normalize(input.normalOS.xz + float2(1e-5, 0));
            float normalY = clamp(input.normalOS.y, -1.0, 1.0);
            float radialMagnitude = sqrt(saturate(1.0 - normalY * normalY));
            float3 seamlessNormalOS = float3(
                radialDirection.x * radialMagnitude,
                normalY,
                radialDirection.y * radialMagnitude);
            output.seamlessNormalWS = TransformObjectToWorldNormal(seamlessNormalOS);
            return output;
        }

        float Hash21(float2 value)
        {
            value = frac(value * float2(123.34, 456.21));
            value += dot(value, value + 45.32);
            return frac(value.x * value.y);
        }

        float2 SafeWaveDirection()
        {
            // Runtime packs the world XZ direction into this vector's XY fields.
            float2 direction = _WaveDirection.xy;
            float lengthSquared = dot(direction, direction);
            return lengthSquared > 1e-5 ? direction * rsqrt(lengthSquared) : float2(1, 0);
        }

        float WaveHeight(float3 positionWS)
        {
            float2 direction = SafeWaveDirection();
            float2 perpendicular = float2(-direction.y, direction.x);
            // Use coordenadas projetadas no plano de repouso. Assim um vertice
            // deslocado ao longo da normal continua consultando a mesma onda que
            // recorta a parede, inclusive quando o plano esta inclinado.
            float3 planeNormal = _SurfacePlane.xyz;
            float normalLengthSquared = max(dot(planeNormal, planeNormal), 1e-5);
            float distanceToRestPlane =
                (dot(planeNormal, positionWS) + _SurfacePlane.w) / normalLengthSquared;
            float3 projectedPosition = positionWS - planeNormal * distanceToRestPlane;
            float2 position = projectedPosition.xz;
            float viscosityDamping = lerp(1.0, 0.18, saturate(_Viscosity01));
            float frequency = max(_WaveFrequency, 0.01);

            float primary = sin(dot(position, direction) * frequency + _WavePhase);
            float secondary = sin(dot(position, perpendicular) * frequency * 1.57 - _WavePhase * 0.73);
            float detailA = sin(dot(position, direction * 0.38 + perpendicular * 0.92)
                * frequency * 2.31 + _WavePhase * 1.43);
            float detailB = sin(dot(position, direction * -0.81 + perpendicular * 0.59)
                * frequency * 4.17 - _WavePhase * 2.07);
            float microPhase = _Time.y * _MicroWaveSpeed * lerp(1.0, 0.15, _Viscosity01);
            float micro = sin(dot(position, direction + perpendicular * 0.37) * frequency * 2.73 + microPhase);

            float irregularity = saturate(_WaveIrregularity);
            float detail = saturate(_WaveDetail);
            float combined = primary
                + secondary * irregularity
                + detailA * detail * 0.32
                + detailB * detail * 0.13;
            combined /= 1.0 + irregularity + detail * 0.45;

            float radius = length(projectedPosition - _WaveOrigin.xyz);
            float radial = sin(radius * frequency * 2.15 - _WavePhase * 1.82);
            radial += sin(radius * frequency * 3.77 + _WavePhase * 1.17) * 0.28;
            radial /= 1.28;

            float dynamicWave = combined * saturate(_WaveAmplitude) * _MaximumWaveHeight;
            float verticalWave = radial * saturate(_VerticalWaveAmplitude) * _MaximumVerticalWaveHeight;
            // A linha de contato fica presa ao vidro. O mesmo falloff e usado
            // pela malha superior e pelo clip das laterais, portanto nao existe
            // fresta nem aro atravessando a parede curva durante o slosh.
            float innerRadius = max(_WaveOrigin.w, 0.001);
            float edgeFreedom = 1.0 - smoothstep(innerRadius * 0.72, innerRadius * 0.98, radius);
            return ((dynamicWave + verticalWave) * viscosityDamping
                + micro * _MicroWaveAmplitude * viscosityDamping) * edgeFreedom;
        }

        float SurfaceDistance(float3 positionWS)
        {
            return dot(_SurfacePlane.xyz, positionWS) + _SurfacePlane.w
                + WaveHeight(positionWS) * _LayerWaveStrength;
        }

        float3 SurfaceNormal(float3 positionWS)
        {
            float epsilon = 0.01;
            float heightX0 = WaveHeight(positionWS - float3(epsilon, 0, 0));
            float heightX1 = WaveHeight(positionWS + float3(epsilon, 0, 0));
            float heightZ0 = WaveHeight(positionWS - float3(0, 0, epsilon));
            float heightZ1 = WaveHeight(positionWS + float3(0, 0, epsilon));
            float derivativeX = (heightX1 - heightX0) / (2.0 * epsilon);
            float derivativeZ = (heightZ1 - heightZ0) / (2.0 * epsilon);
            return SafeNormalize(_SurfacePlane.xyz - float3(derivativeX, 0, derivativeZ));
        }

        half3 SampleEnvironment(float3 direction, float roughness)
        {
            half4 encoded = SAMPLE_TEXTURECUBE_LOD(
                unity_SpecCube0, samplerunity_SpecCube0, direction, roughness * 6.0);
            return DecodeHDREnvironment(encoded, unity_SpecCube0_HDR);
        }

        half3 IridescentColour(float amount)
        {
            float3 phase = float3(0.0, 0.333, 0.667);
            return 0.5 + 0.5 * cos(6.2831853 * (amount * 1.7 + phase));
        }

        float Fresnel(float3 normalWS, float3 viewDirWS)
        {
            float ior = max(_IOR, 1.0001);
            float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
            float ndv = saturate(dot(normalWS, viewDirWS));
            float physical = f0 + (1.0 - f0) * pow(1.0 - ndv, 5.0);
            float artistic = pow(1.0 - ndv, max(_FresnelPower, 0.01));
            return saturate(lerp(physical, artistic, 0.35) * _FresnelIntensity);
        }

        void MainLightTerms(
            float3 positionWS,
            float3 normalWS,
            float3 viewDirWS,
            out half3 diffuse,
            out half3 specular)
        {
            float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
            Light mainLight = GetMainLight(shadowCoord);
            float attenuation = mainLight.distanceAttenuation * mainLight.shadowAttenuation;
            float ndl = saturate(dot(normalWS, mainLight.direction));
            float3 halfDirection = SafeNormalize(mainLight.direction + viewDirWS);
            float specularPower = exp2(4.0 + saturate(_Smoothness) * 8.0);
            float specularTerm = pow(saturate(dot(normalWS, halfDirection)), specularPower);
            diffuse = mainLight.color * ndl * attenuation;
            specular = mainLight.color * specularTerm * attenuation * _SpecularIntensity;
        }

        half3 EmissionAndPlayful(float3 positionWS, float3 normalWS, float3 viewDirWS, float fresnel)
        {
            float pulse = lerp(
                1.0,
                0.5 + 0.5 * sin(_Time.y * _EmissionPulseFrequency * 6.2831853),
                _EmissionPulseAmount);
            half3 emission = _EmissionColor.rgb * _EmissionStrength * pulse;

            half3 playfulColour = lerp(_PlayfulColor.rgb, IridescentColour(fresnel), _Iridescence);
            emission += playfulColour * fresnel * _PlayfulAmount;

            float sparkleNoise = Hash21(floor(positionWS.xz * 180.0 + _Time.y * 1.7));
            float sparkle = step(lerp(1.0, 0.975, _Sparkle), sparkleNoise);
            emission += playfulColour * sparkle * fresnel * _PlayfulAmount * 2.0;
            return emission;
        }

        half4 ShadeGeneratedSurface(Varyings input)
        {
            // The reference LiquidEffect shader renders the mesh backfaces and
            // colours them as the top of the liquid.  Keep the real surface used
            // by the boiling waves, but shade its centre as the same volume instead
            // of as a uniformly bright, independent lid.
            float skirtMask = smoothstep(1.0005, 1.01, input.uv.x);
            float3 normalWS = SafeNormalize(lerp(
                SurfaceNormal(input.positionWS),
                input.normalWS,
                skirtMask));
            float3 viewDirWS = SafeNormalize(GetCameraPositionWS() - input.positionWS);
            if (dot(normalWS, viewDirWS) < 0.0) normalWS = -normalWS;

            float fresnel = Fresnel(normalWS, viewDirWS);
            float ndv = saturate(dot(normalWS, viewDirWS));
            float grazing = pow(1.0 - ndv, 2.0);
            half3 diffuse;
            half3 specular;
            MainLightTerms(input.positionWS, normalWS, viewDirWS, diffuse, specular);
            half3 ambient = SampleSH(normalWS) * _AmbientIntensity;
            half3 surfaceLight = ambient + diffuse * 0.7 + 0.3;
            half3 integratedSurface = lerp(
                _BodyColor.rgb,
                _SurfaceColor.rgb,
                saturate(grazing * 0.55));
            half3 colour = integratedSurface
                * lerp(half3(1, 1, 1), surfaceLight, _LightingInfluence);

            if (_ReflectionIntensity > 1e-5)
            {
                half3 reflection = SampleEnvironment(
                    reflect(-viewDirWS, normalWS), 1.0 - _Smoothness);
                colour = lerp(colour, reflection, saturate(fresnel * _ReflectionIntensity));
            }
            colour += specular * lerp(0.2, 1.0, _LightingInfluence);

            // Restrict the brighter colour to a narrow meniscus.  The previous
            // 0.76 threshold affected almost half of the disc area and made the
            // generated surface read as a separate cap.
            float edge = smoothstep(0.94, 1.0, input.uv.x);
            float foamNoise = lerp(0.68, 1.0, Hash21(floor(input.positionWS.xz * _WaveFrequency * 11.0)));
            float foamMask = saturate(edge * _FoamAmount * foamNoise);
            colour = lerp(colour, _FoamColor.rgb, foamMask);
            colour = lerp(
                colour,
                _SurfaceColor.rgb,
                saturate(edge * _MeniscusStrength * 0.32));
            colour += EmissionAndPlayful(input.positionWS, normalWS, viewDirWS, fresnel);
            colour = MixFog(max(colour, 0.0), input.fogFactor);

            float bodyAlpha = max(0.16, _BodyColor.a * 0.78);
            float alpha = lerp(bodyAlpha, _SurfaceOpacity, saturate(grazing + edge * 0.65));
            alpha = max(alpha, foamMask * _FoamColor.a);
            // Blend the short sealing skirt continuously into the body.  A hard
            // step made a dark circular seam halfway down the skirt.
            colour = lerp(colour, _BodyColor.rgb, skirtMask);
            alpha = lerp(alpha, max(0.12, _BodyColor.a * 0.78), skirtMask);
            return half4(colour, saturate(alpha));
        }
        ENDHLSL

        // Como no LiquidEffect, a camada de superficie aceita as duas faces.
        // O corpo continua limitado as faces externas dentro do fragment shader.
        Pass
        {
            Name "LiquidBody"
            Tags { "LightMode"="UniversalForward" }
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragBody
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            half4 FragBody(
                Varyings input,
                FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // A calibrated volume of exactly zero must render no liquid.
                clip(_Volume01 - 1e-5);

                if (input.surfaceFlag > 0.5)
                    return ShadeGeneratedSurface(input);

                float signedDistance = SurfaceDistance(input.positionWS);
                clip(-signedDistance);
                float bottomDistance = dot(_LayerBottomPlane.xyz, input.positionWS)
                    + _LayerBottomPlane.w;
                clip(lerp(1.0, bottomDistance, saturate(_HasLayerBottom)));

                // Como no LiquidEffect original, mantenha o interior visivel e
                // pinte as faces traseiras com a mesma resposta do centro da
                // superficie. Nao existe tampa geometrica: estas faces fecham o
                // volume e recebem a mesma resposta visual da superficie livre.
                if (IS_FRONT_VFACE(frontFace, 1.0, -1.0) < 0.0)
                {
                    Varyings interior = input;
                    interior.uv = float2(0.0, 0.0);
                    half4 interiorColour = ShadeGeneratedSurface(interior);
                    // Todas as fases usam a cor da fase que toca o ar. Isso evita
                    // que a parede interna revele uma camada inferior.
                    interiorColour.rgb = _InteriorColor.rgb;
                    // O interior precisa fechar o raio visual por si proprio:
                    // nao ha mais uma tampa geometrica atras dele.
                    interiorColour.a = lerp(
                        1.0,
                        max(0.18, _BodyColor.a * 0.65),
                        saturate(_TransmissionStrength));
                    return interiorColour;
                }

                float3 normalWS = SafeNormalize(lerp(
                    input.normalWS,
                    input.seamlessNormalWS,
                    saturate(_NormalSeamSmoothing)));
                float3 viewDirWS = SafeNormalize(GetCameraPositionWS() - input.positionWS);
                float fresnel = Fresnel(normalWS, viewDirWS);

                float2 screenUV = input.screenPos.xy / max(input.screenPos.w, 1e-5);
                screenUV = UnityStereoTransformScreenSpaceTex(screenUV);
                float rawSceneDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = LinearEyeDepth(rawSceneDepth, _ZBufferParams);
                float liquidEyeDepth = max(0.0, -TransformWorldToView(input.positionWS).z);
                float sceneThickness = max(0.0, sceneEyeDepth - liquidEyeDepth);
                float verticalDepth = max(0.0, -signedDistance) * _DepthGradient;
                float opticalDepth = min(max(sceneThickness, verticalDepth), _MaxOpticalDepth);

                float3 transmissionColour = max(_AbsorptionColor.rgb, 0.005);
                float3 absorptionCoefficient = -log(transmissionColour) * _AbsorptionDensity;
                float3 transmittance = exp(-absorptionCoefficient * opticalDepth);

                float3 normalVS = TransformWorldToViewDir(normalWS);
                float refractionScale = _RefractionStrength * (1.0 - rcp(max(_IOR, 1.0001)));
                refractionScale *= lerp(1.0, 0.25, _Turbidity);
                float2 refractedUV = saturate(screenUV + normalVS.xy * refractionScale * (0.4 + opticalDepth));
                float depthBlend = 1.0 - exp(-verticalDepth);
                half3 scatterColour = lerp(_BodyColor.rgb, _DeepColor.rgb, depthBlend);
                half3 liquidBase = scatterColour;

                // Scene colour is opt-in. Refraction at exactly zero is a hard
                // guarantee that the liquid stays fully colour-driven.
                float sceneTransmission = saturate(_TransmissionStrength);
                if (sceneTransmission > 1e-5)
                {
                    half3 sceneColour = SampleSceneColor(refractedUV);
                    half3 refractedLiquid = sceneColour * transmittance;
                    refractedLiquid += scatterColour * (1.0 - transmittance)
                        * lerp(0.45, 1.0, _Turbidity);
                    liquidBase = lerp(liquidBase, refractedLiquid, sceneTransmission);
                }

                half3 diffuse;
                half3 specular;
                MainLightTerms(input.positionWS, normalWS, viewDirWS, diffuse, specular);
                half3 ambient = SampleSH(normalWS) * _AmbientIntensity;
                half3 lightFactor = ambient + diffuse * 0.55 + 0.25;
                half3 colour = liquidBase * lerp(half3(1, 1, 1), lightFactor, _LightingInfluence);

                if (_ReflectionIntensity > 1e-5)
                {
                    half3 reflection = SampleEnvironment(
                        reflect(-viewDirWS, normalWS), 1.0 - _Smoothness);
                    colour = lerp(
                        colour,
                        reflection,
                        saturate(fresnel * _ReflectionIntensity));
                }
                colour += specular * lerp(0.2, 1.0, _LightingInfluence);

                float meniscus = 1.0 - smoothstep(0.0, max(_MeniscusWidth, 1e-5), abs(signedDistance));
                float foamNoise = lerp(0.7, 1.0, Hash21(floor(input.positionWS.xz * _WaveFrequency * 9.0)));
                float foamBand = 1.0 - smoothstep(0.0, max(_FoamWidth, 1e-5), abs(signedDistance));
                float foamMask = saturate(foamBand * _FoamAmount * foamNoise);
                colour = lerp(colour, _SurfaceColor.rgb, meniscus * _MeniscusStrength * 0.35);
                half3 foamLighting = lerp(half3(1, 1, 1), ambient + diffuse + 0.4, _LightingInfluence);
                colour = lerp(colour, _FoamColor.rgb * foamLighting, foamMask);
                colour += EmissionAndPlayful(input.positionWS, normalWS, viewDirWS, fresnel);

                float absorptionOpacity = 1.0 - dot(transmittance, float3(0.2126, 0.7152, 0.0722));
                float alpha = (1.0 - _Transparency) + _BodyColor.a * 0.25;
                alpha += absorptionOpacity * 0.65 + fresnel * 0.35 + _Turbidity * 0.25;
                alpha = max(alpha, meniscus * _SurfaceColor.a * 0.55);
                alpha = max(alpha, foamMask * _FoamColor.a);

                colour = MixFog(max(colour, 0.0), input.fogFactor);
                return half4(colour, saturate(alpha));
            }
            ENDHLSL
        }

    }

    FallBack Off
}
