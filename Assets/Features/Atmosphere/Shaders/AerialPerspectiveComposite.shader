Shader "Hidden/Custom RP/AerialPerspectiveComposite"
{
    // Full-screen composite pass:
    //   Samples _AerialPerspectiveLUT 3D texture with bilateral Z weighting
    //   Blends: srcColor × T + inScatter  (HDR, before tonemapping)
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Assets/Shaders/ShaderLibrary/ScatteringUtils.hlsl"
            #include "Assets/Shaders/ShaderLibrary/Atmosphere.hlsl"
            // ── Camera color (bound by C# as _BlitTexture) ─────────────
            TEXTURE2D_X(_BlitTexture);
            SAMPLER(sampler_BlitTexture);
            float4 _BlitScaleBias;


            // ── Aerial Perspective 3D LUT ─────────────────────────────
            TEXTURE3D(_AerialPerspectiveLUT);
            SAMPLER(sampler_AerialPerspectiveLUT);

            int _AerialLutWidth;
            int _AerialLutHeight;
            int _AerialLutDepth;
            float _APIntensity;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // ── Full-screen triangle vertex ───────────────────────────
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            // ── Fragment ──────────────────────────────────────────────
            float4 Frag(Varyings input) : SV_Target
            {
                float2 screenUV = input.uv;
                float rawDepth = SampleSceneDepth(screenUV);
                float4 sceneColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture,
                                                       screenUV * _BlitScaleBias.xy + _BlitScaleBias.zw);

                // ── Sky / background pixels: skip AP ─────────────────────
                // Reversed-Z: depth == 0 means far plane (skybox).
                // The skybox is the atmosphere itself — don't apply AP on top.
                if (rawDepth == 0.0) return sceneColor;

                // ════════════════════════════════════════════════════════
                // NORMAL MODE
                // ════════════════════════════════════════════════════════

                float4 clipPos = float4(screenUV * float2(2.0, -2.0) - float2(1.0, -1.0), rawDepth, 1.0);
                float4 positionWS = mul(UNITY_MATRIX_I_VP, clipPos);
                positionWS /= positionWS.w;
                float tDepth = length(positionWS.xyz - _WorldSpaceCameraPos);
                float weight = 1.0;

                // Near fade: smooth out aerial perspective within the first half-slice
                // (0.5 * AP_METERS_PER_SLICE meters from camera) to avoid artifacts at depth 0
                float nearDistance = 0.5 * AP_METERS_PER_SLICE;
                if (tDepth < nearDistance)
                {
                    // Multiply by weight to fade to 0 at depth 0
                    weight = saturate(tDepth / nearDistance);
                    tDepth = nearDistance;
                }
                float w = sqrt(tDepth / (AP_METERS_PER_SLICE * NUM_SLICES)); // squared distribution

                float4 AP = weight * _AerialPerspectiveLUT.SampleLevel(
                    sampler_AerialPerspectiveLUT, float3(screenUV, w), 0);
                sceneColor = float4(sceneColor.rgb + AP.rgb * _APIntensity, AP.a);
                return sceneColor;
            }
            ENDHLSL
        }
    }
}