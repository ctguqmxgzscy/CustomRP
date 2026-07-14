Shader "Hidden/Custom RP/AerialPerspectiveComposite"
{
    // Full-screen composite pass.
    //
    // Two modes (compile-time keyword):
    //   Default (no keyword):  samples _AerialPerspectiveLUT 3D LUT — fast
    //   SHADOWMAP_ENABLED:     per-pixel ray-march, depth buffer sets tMax,
    //                          URP shadowmap sampled at each step — terrain aware
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "AerialPerspectiveComposite"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ SHADOWMAP_ENABLED
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            // ── Shared constants (also in Atmosphere.hlsl, needed by both paths) ──
            #define NUM_SLICES             64
            #define AP_METERS_PER_SLICE    200.0

            // ── Camera color (bound by C# as _BlitTexture) ─────────────
            TEXTURE2D_X(_BlitTexture);
            SAMPLER(sampler_BlitTexture);
            float4 _BlitScaleBias;

            // ── Aerial Perspective 3D LUT (default mode) ───────────────
            TEXTURE3D(_AerialPerspectiveLUT);
            SAMPLER(sampler_AerialPerspectiveLUT);

            int _AerialLutWidth;
            int _AerialLutHeight;
            int _AerialLutDepth;
            float _APIntensity;

            // ── Camera matrices for view-dir reconstruction ────────────
            float4x4 _InvProjectionMatrix;
            float4x4 _InvCameraViewMatrix;
            int _EnableTerrainShadow;

            #ifdef SHADOWMAP_ENABLED
            #include "Assets/Shaders/ShaderLibrary/AerialPerspectiveRayMarch.hlsl"
            #endif

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 screenUV  = input.uv;
                float  rawDepth  = SampleSceneDepth(screenUV);
                float4 sceneColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture,
                                                       screenUV * _BlitScaleBias.xy + _BlitScaleBias.zw);

                // Sky pixels (depth == 0): the atmosphere IS the background
                if (rawDepth == 0.0) return sceneColor;

                // ── Reconstruct world-space position at depth ──────────
                float4 clipPos   = float4(screenUV * float2(2.0, -2.0) - float2(1.0, -1.0), rawDepth, 1.0);
                float4 wsPos     = mul(UNITY_MATRIX_I_VP, clipPos);
                wsPos           /= wsPos.w;
                float  tDepth    = length(wsPos.xyz - _WorldSpaceCameraPos);

            #ifndef SHADOWMAP_ENABLED
                // ════════════════════════════════════════════════════════
                // FAST MODE: 3D LUT lookup
                // ════════════════════════════════════════════════════════
                float weight = 1.0;
                float nearDist = 0.5 * AP_METERS_PER_SLICE;
                if (tDepth < nearDist)
                {
                    weight  = saturate(tDepth / nearDist);
                    tDepth  = nearDist;
                }
                float  w = sqrt(tDepth / (AP_METERS_PER_SLICE * NUM_SLICES));
                float4 AP = weight * _AerialPerspectiveLUT.SampleLevel(
                    sampler_AerialPerspectiveLUT, float3(screenUV, w), 0);
                sceneColor = float4(sceneColor.rgb + AP.rgb * _APIntensity, AP.a);
            #else
                // ════════════════════════════════════════════════════════
                // SHADOWMAP MODE: per-pixel ray-march
                //   - Depth buffer → tMax (mountains truncate naturally)
                //   - URP shadowmap sampled at each integration step
                // ════════════════════════════════════════════════════════
                float3 viewDir  = normalize(wsPos.xyz - _WorldSpaceCameraPos);
                float3 worldCam = _WorldSpaceCameraPos + float3(0, _PlanetRadius, 0);
                float3 sunDir   = normalize(_SunDirection);
                bool   useShadow = _EnableTerrainShadow > 0;

                float3 L, throughput;
                IntegrateScatteredLuminance(worldCam, viewDir, tDepth, sunDir,
                                            useShadow, L, throughput);
                float T = dot(throughput, float3(1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0));
                sceneColor = float4(sceneColor.rgb + L * _APIntensity,
                                    saturate(1.0 - T));
            #endif

                return sceneColor;
            }
            ENDHLSL
        }
    }
}
