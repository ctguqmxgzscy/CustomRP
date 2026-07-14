Shader "Skybox/AtmosphericScattering"
{
    Properties
    {
        _ViewSamples("View Samples", Range(1, 256)) = 64
        _AtmosphereRadius("Atmosphere Radius", Float) = 1.025
        _PlanetRadius("Planet Radius", Float) = 1.0

        // Atmospheric Scattering
        _ScaleHeight("Scale Height", Float) = 0.025
        _SunIntensity("Sun Intensity", Float) = 10.0
        _LightSamples("Light Samples", Range(1, 32)) = 4
        _MieG("Mie G (Asymmetry)", Range(-1, 1)) = 0.76

        // Set by C# script — world-space center of the planet/atmosphere
        [HideInInspector] _PlanetCenter("Planet Center", Vector) = (0, 0, 0, 0)
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
            Name "AtmosphericSkybox"
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ── Scattering library (LUT, phase functions, ray intersect, etc.) ──
            #include "Assets/Shaders/ShaderLibrary/ScatteringUtils.hlsl"

            // ── Material parameters ──
            float _ViewSamples;
            float _AtmosphereRadius;
            float _PlanetRadius;
            float _ScaleHeight;
            float _MieScaleHeight;
            float _SunIntensity;
            float _MieG;
            float3 _PlanetCenter;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 viewDirWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Skybox rendering: Unity strips camera translation from the view
                // matrix, so the skybox cube is always centered on the camera.
                // The object-space vertex position represents a VIEW-SPACE
                // direction. We convert it to world space via the inverse view
                // matrix (rotation only, since translation is zeroed).
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                float3 viewDirVS = input.positionOS.xyz;
                output.viewDirWS = mul((float3x3)UNITY_MATRIX_I_V, viewDirVS);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 viewDirWS = normalize(input.viewDirWS);
                float3 center = _PlanetCenter;

                // ── Ray-intersect the atmosphere shell ─────────────────────
                float tEntry, tExit;
                if (!AtmosphericRayIntersect(_WorldSpaceCameraPos, viewDirWS, center,
                                             _AtmosphereRadius, _PlanetRadius, tEntry, tExit))
                {
                    // View ray misses the atmosphere entirely → space / background
                    return half4(0, 0, 0, 1);
                }

                // Ray origin at atmosphere entry point, length through atmosphere
                float3 rayOrigin = _WorldSpaceCameraPos + viewDirWS * tEntry;
                float rayLength = tExit - tEntry;

                float3 sunDirection = normalize(_MainLightPosition.xyz);
                float sunIntensity = _SunIntensity;
                float cosTheta = dot(viewDirWS, sunDirection);
                float scaleHeightM = _MieScaleHeight;

                // ── Numerical Integration along the View Ray ─────────────
                float3 scatter = half3(0, 0, 0);
                float tauPA_R = 0.0, tauPA_M = 0.0;
                float time = 0.0;
                float ds = rayLength / _ViewSamples;

                for (int i = 0; i < _ViewSamples; i++)
                {
                    float3 P = rayOrigin + viewDirWS * (time + ds * 0.5);
                    float height = distance(center, P) - _PlanetRadius;

                    // ① In-scattering source at P: J = β·γ(θ)·ρ(h)
                    half3 J = EvaluateInScattering(height,
                                                   _ScaleHeight, scaleHeightM,
                                                   cosTheta, _MieG);

                    // ② PA transmittance — separate τ_R, τ_M
                    tauPA_R += exp(-height / _ScaleHeight) * ds;
                    tauPA_M += exp(-height / scaleHeightM) * ds;
                    half3 T_PA = exp(-kRayleighScattering * tauPA_R - kMieScattering * tauPA_M);

                    // ③ CP transmittance — LUT lookup from P towards sun
                    float cosSunZenith = dot(normalize(P - center), sunDirection);
                    half3 T_CP = SampleTransmittanceLUT(height, cosSunZenith,
                                                      _AtmosphereRadius, _PlanetRadius,
                                                      kRayleighScattering, kMieScattering);

                    // ④ Single scattering: I_sun · J · T(PA) · T(CP) · ds
                    scatter += sunIntensity * J * T_PA * T_CP * ds;

                    time += ds;
                }

                return half4(scatter, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}