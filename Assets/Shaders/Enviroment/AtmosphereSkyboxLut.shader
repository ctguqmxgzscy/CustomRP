Shader "Skybox/AtmosphericScatteringLUT"
{
    Properties
    {
        // All parameters are set by the RenderFeature via global shader properties.
        // This shader only samples the precomputed _SkyViewLut texture.
        [HideInInspector] _MainTex("Dummy", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "RenderPipeline" = "UniversalPipeline"
            "PreviewType" = "Skybox"
        }

        Pass
        {
            Name "AtmosphericSkyboxLUT"
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_SkyViewLut);
            SAMPLER(sampler_SkyViewLut);

            float _PlanetRadius;
            float _SunDiskAngle;
            float _SunIntensity;
            float3 _SunLightColor;
            #define ATM_PI 3.14159265359

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // ── Direction → equirectangular UV ──────────────────────────
            // Non-linear latitude (Hillaire 2020): more texels near horizon.
            float2 ViewDirToUV(float3 v)
            {
                float latitude = asin(v.y);
                float n = latitude / (ATM_PI * 0.5);
                return float2(atan2(v.z, v.x) / (2.0 * ATM_PI) + 0.5,
                              sign(n) * sqrt(abs(n)) * 0.5 + 0.5);
            }

            // ── Simple ray-sphere intersect (for sun disk occlusion) ────
            float RayIntersectSphere(float3 center, float radius,
                                     float3 rayStart, float3 rayDir)
            {
                float3 L = center - rayStart;
                float tca = dot(L, rayDir);
                float d2 = dot(L, L) - tca * tca;
                if (d2 > radius * radius) return -1.0;
                float thc = sqrt(radius * radius - d2);
                float t1 = tca - thc;
                float t2 = tca + thc;
                return (t1 < 0.0) ? t2 : t1;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);

                return output;
            }

            float3 GetSunDisk(float3 eyePos, float3 viewDir, float3 sunDir,
                              float planetRadius, float sunDiskAngle,
                              float3 sunColor, float sunIntensity)
            {
                float cosTheta = dot(viewDir, sunDir);
                float theta = acos(saturate(cosTheta)) * (180.0 / ATM_PI);

                // Occluded by planet?
                float disToPlanet = RayIntersectSphere(
                    float3(0, 0, 0), planetRadius, eyePos, viewDir);
                if (disToPlanet >= 0.0) return float3(0, 0, 0);

                if (theta < sunDiskAngle)
                    return sunColor * sunIntensity;

                return float3(0, 0, 0);
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 viewDirWS = normalize(input.positionWS);
                float3 eyePos = float3(0, _PlanetRadius + _WorldSpaceCameraPos.y, 0);

                // Sample the precomputed sky view LUT
                float2 uv = ViewDirToUV(viewDirWS);

                // ── DEBUG: Visualize UV mapping (uncomment next line to test) ──
                // Rotate camera. If colors stay static → viewDirWS is broken.
                // Red=uv.x(azimuth), Green=uv.y(elevation), Blue=0.
                // return half4(uv.x, uv.y, 0.0, 1.0);

                float3 skyColor = SAMPLE_TEXTURE2D_LOD(_SkyViewLut, sampler_SkyViewLut, uv, 0).rgb;

                // ── Sun disk (analytic, on top of LUT-sampled sky) ─────────
                // URP: _MainLightPosition.xyz = direction toward the light source
                float3 towardSun = normalize(_MainLightPosition.xyz); // toward the sun
                float3 sunColor = _SunLightColor;
                if (all(sunColor == 0.0)) sunColor = float3(1, 1, 1);

                // Bruneton horizon smoothstep: sun disk fades when below horizon
                // Bruneton uses direction FROM sun (light travel direction) = -towardSun
                float r = length(eyePos);
                float sinThetaH = _PlanetRadius / max(r, 0.001);
                float cosThetaH = -sqrt(max(1.0 - sinThetaH * sinThetaH, 0.0));
                float sunAngRad = _SunDiskAngle * (ATM_PI / 180.0);
                float mu_s = dot(normalize(eyePos), towardSun);
                float sunVisibility = smoothstep(-sinThetaH * sunAngRad,
                                                 sinThetaH * sunAngRad,
                                                 mu_s - cosThetaH);

                skyColor += GetSunDisk(eyePos, viewDirWS, towardSun,
                                       _PlanetRadius, _SunDiskAngle,
                                       sunColor, _SunIntensity) * sunVisibility;

                return half4(skyColor, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}