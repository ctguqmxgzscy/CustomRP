Shader "Hidden/Custom RP/DebugLutOverlay"
{
    // Full-screen debug overlay for atmosphere LUTs.
    // Controlled via global _DebugMode (AtmosphereDebugMode enum).
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "DebugLutOverlay"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma enable_d3d11_debug_symbols

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ── Global debug control (set via Shader.SetGlobal*) ───────
            int _DebugMode; // AtmosphereDebugMode enum: 0=None, 1=OpticalDepth, 2=MultiScatter, 3=SkyView, 10=AP_Slice, 11=AP_Grid
            float _DebugSliceZ; // [0, 1] Z slice for AerialPerspectiveLUT_Slice

            // ── 2D LUT textures (set as globals by AtmosphereLutPass) ──
            TEXTURE2D(_OpticalDepthLUT);
            SAMPLER(sampler_OpticalDepthLUT);

            TEXTURE2D(_MultiScatteringLUT);
            SAMPLER(sampler_MultiScatteringLUT);

            TEXTURE2D(_SkyViewLut);
            SAMPLER(sampler_SkyViewLut);

            // ── 3D Aerial Perspective LUT ─────────────────────────────
            TEXTURE3D(_AerialPerspectiveLUT);
            SAMPLER(sampler_AerialPerspectiveLUT);

            // ── Tone mapping helpers ───────────────────────────────────
            float3 Reinhard(float3 hdr)
            {
                return hdr / (1.0 + hdr);
            }

            // ACES approximation — better for HDR sky radiance
            float3 ACESFilm(float3 x)
            {
                float a = 2.51;
                float b = 0.03;
                float c = 2.43;
                float d = 0.59;
                float e = 0.14;
                return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
            }

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
                float2 uv = input.uv;

                // ── 2D LUT: Optical Depth ──────────────────────────────
                if (_DebugMode == 1)
                {
                    float2 od = SAMPLE_TEXTURE2D_LOD(_OpticalDepthLUT, sampler_OpticalDepthLUT, uv, 0).rg;
                    // R = Rayleigh τ, G = Mie τ. Scale for visibility.
                    // Typical τ range [0, ~5]; scale so τ=1 ≈ 0.2 intensity.
                    return float4(od.r * 0.2, od.g * 0.2, 0.0, 1.0);
                }

                // ── 2D LUT: Multi-Scattering ───────────────────────────
                if (_DebugMode == 2)
                {
                    float3 ms = SAMPLE_TEXTURE2D_LOD(_MultiScatteringLUT, sampler_MultiScatteringLUT, uv, 0).rgb;
                    // G_ALL values are dimensionless, typically [0, 1].
                    // Multiply for visibility in case values are small.
                    return float4(ms * 5.0, 1.0);
                }

                // ── 2D LUT: Sky View ───────────────────────────────────
                if (_DebugMode == 3)
                {
                    float3 sky = SAMPLE_TEXTURE2D_LOD(_SkyViewLut, sampler_SkyViewLut, uv, 0).rgb;
                    // HDR radiance — apply ACES tone mapping
                    return float4(ACESFilm(sky * 0.5), 1.0);
                }

                // ── 3D LUT: Aerial Perspective — single Z slice ────────
                if (_DebugMode == 10)
                {
                    float4 ap = SAMPLE_TEXTURE3D_LOD(_AerialPerspectiveLUT,
                        sampler_AerialPerspectiveLUT,
                        float3(uv, _DebugSliceZ), 0);
                    // RGB = accumulated inscatter, A = transmittance
                    // Show RGB scaled, and transmittance in green channel overlay
                    return float4(ap.rgb * 200.0 + float3(0.0, ap.a * 0.5, 0.0), 1.0);
                }

                // ── 3D LUT: Aerial Perspective — 8×8 slice grid ───────
                if (_DebugMode == 11)
                {
                    const float gridCols = 8.0;
                    const float gridRows = 8.0;
                    float2 cellUV = frac(uv * float2(gridCols, gridRows));
                    float2 cellIdx = floor(uv * float2(gridCols, gridRows));
                    float z = (cellIdx.y * gridCols + cellIdx.x + 0.5) / 64.0;
                    float4 ap = SAMPLE_TEXTURE3D_LOD(_AerialPerspectiveLUT,
                        sampler_AerialPerspectiveLUT,
                        float3(cellUV, z), 0);
                    return float4(ap.rgb * 200.0, 1.0);
                }

                // ── None / invalid ─────────────────────────────────────
                return float4(0, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
