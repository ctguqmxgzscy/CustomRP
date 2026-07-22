// GPU-Driven Grass Shader — reads per-instance transform from structured buffer.
// Supports wind animation via vertex displacement.
// Indirect lighting: atmosphere SH coefficients (_AtmoSHAr.._AtmoSHC), zero texture reads.
Shader "Custom/GrassIndirect"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.3, 0.6, 0.2, 1)
        _ShadowColor ("Shadow Color", Color) = (0.05, 0.1, 0.02, 1)
        _HighlightColor ("Highlight Color", Color) = (0.6, 0.85, 0.3, 1)
        _BaseMap ("Base Texture", 2D) = "white" {}
        _MaskMap ("Mask Map (R=Metallic G=AO B=-- A=Smoothness)", 2D) = "white" {}
        _ThicknessMap ("Thickness Map (dark=thin bright=thick)", 2D) = "black" {}
        [Normal] _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalScale ("Normal Scale", Range(0, 2)) = 1.0
        _SpecularColor ("Specular Color", Color) = (1, 1, 1, 1)
        _SpecularSmoothness ("Specular Smoothness", Range(0, 1)) = 0.5
        _GrassWater ("Grass Water (0=Dry 1=Wet)", Range(0, 1)) = 0.3
        _TransIntensity ("Transmission Intensity", Range(0, 5)) = 2
        _TransLerp ("Transmission Lerp", Range(0, 1)) = 0.7
        _TransExp ("Transmission Exp", Range(1, 8)) = 2
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        _SSSIntensity ("SSS Intensity", Range(0, 2)) = 0.5
        _SSSRadius ("SSS Radius", Range(0, 1)) = 0.3
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline"
        }

        Pass
        {
            Name "GrassForward"
            Tags
            {
                "LightMode"="UniversalForwardOnly"
            }
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma enable_d3d11_debug_symbols
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SphericalHarmonics.hlsl"

            // Atmosphere SH — set globally by AtmosphereSkyboxLutFeature.
            // Custom names avoid UnityPerDraw CBUFFER (DrawMeshInstancedIndirect
            // doesn't populate it).
            float4 _AtmoSHAr, _AtmoSHAg, _AtmoSHAb;
            float4 _AtmoSHBr, _AtmoSHBg, _AtmoSHBb;
            float4 _AtmoSHC;

            // Equivalent to URP SampleSH9 — zero texture reads.
            half3 SampleAtmoSH(half3 N)
            {
                half4 vA = half4(N, 1.0);
                half3 res;
                res.r = dot(_AtmoSHAr, vA);
                res.g = dot(_AtmoSHAg, vA);
                res.b = dot(_AtmoSHAb, vA);

                half4 vB = N.xyzz * N.yzzx;
                res.r += dot(_AtmoSHBr, vB);
                res.g += dot(_AtmoSHBg, vB);
                res.b += dot(_AtmoSHBb, vB);
                res += _AtmoSHC.rgb * (N.x * N.x - N.y * N.y);

                return max(half3(0, 0, 0), res);
            }

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 tangentWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                float height : TEXCOORD4;
                float4 shadowCoord : TEXCOORD5;
            };

            StructuredBuffer<float4> _GrassInstanceData;
            float4 _WindParams;
            float3 _GrassCameraPos;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor, _ShadowColor, _HighlightColor, _SpecularColor;
                float _Cutoff, _NormalScale, _SpecularSmoothness, _GrassWater;
                float _TransIntensity, _TransLerp, _TransExp, _SSSIntensity, _SSSRadius;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_MaskMap);
            SAMPLER(sampler_MaskMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_ThicknessMap);
            SAMPLER(sampler_ThicknessMap);
            TEXTURE2D(_TerrainHeightmap);
            SAMPLER(sampler_TerrainHeightmap);
            float4 _TerrainHeightmap_TexelSize, _TerrainSize, _TerrainPosition;

            float3 GetTerrainNormal(float3 worldPos, float blurRadius)
            {
                float2 uv = (worldPos.xz - _TerrainPosition.xz) / _TerrainSize.xz;
                uv = saturate(uv);
                float2 ts = _TerrainHeightmap_TexelSize.xy * blurRadius;
                float hL = SAMPLE_TEXTURE2D_LOD(_TerrainHeightmap, sampler_TerrainHeightmap, uv - float2(ts.x, 0), 0).r;
                float hR = SAMPLE_TEXTURE2D_LOD(_TerrainHeightmap, sampler_TerrainHeightmap, uv + float2(ts.x, 0), 0).r;
                float hD = SAMPLE_TEXTURE2D_LOD(_TerrainHeightmap, sampler_TerrainHeightmap, uv - float2(0, ts.y), 0).r;
                float hU = SAMPLE_TEXTURE2D_LOD(_TerrainHeightmap, sampler_TerrainHeightmap, uv + float2(0, ts.y), 0).r;
                float3 n;
                n.x = (hL - hR) * _TerrainSize.y / (2.0 * ts.x * _TerrainSize.x);
                n.z = (hD - hU) * _TerrainSize.y / (2.0 * ts.y * _TerrainSize.z);
                n.y = 1.0;
                return normalize(n);
            }

            float GGX_Lobe(float3 N, float3 V, float3 L, float roughness, float metallic, float smoothness)
            {
                float3 H = normalize(L + V);
                float F0 = lerp(0.04, 0.5, metallic);
                float r = roughness * (1.0 - smoothness * 0.7) * (1.0 - _SpecularSmoothness * 0.5);
                return D_GGX(saturate(dot(N, H)), r) * F_Schlick(F0, saturate(dot(N, V)));
            }

            float3 GrassMultiSpecular(float3 bladeN, float3 terrainN, float3 V, float3 L,
                                      float tipHeight, float water, float metallic,
                                      float smoothness, float distFade)
            {
                float3 primaryN = normalize(lerp(terrainN, bladeN, tipHeight));
                float r = lerp(1.0, 0.35, water);
                float p1 = GGX_Lobe(primaryN, V, L, r, metallic, smoothness);
                float p2 = GGX_Lobe(primaryN, V, L, r * 0.5, metallic, smoothness);
                float p3 = GGX_Lobe(primaryN, V, L, r * 0.25, metallic, smoothness);
                float s1 = GGX_Lobe(terrainN, V, L, r * 1.5, metallic, smoothness);
                float primary = p1 * 0.4 + p2 * 0.3 + p3 * 0.2;
                float secondary = s1 * 0.1;
                float NdotL_blade = saturate(dot(primaryN, L));
                return (primary + secondary) * NdotL_blade * distFade * _SpecularColor.rgb;
            }

            float SimpleTransmission(float3 N, float3 L, float3 V, float thicknessFade)
            {
                float3 fakeN = -normalize(lerp(N, L, _TransLerp));
                float trans = pow(saturate(dot(fakeN, V)), _TransExp);
                return trans * _TransIntensity * thicknessFade;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                uint id = input.instanceID;
                uint i0 = id * 2;
                float4 slot0 = _GrassInstanceData[i0];
                float4 slot1 = _GrassInstanceData[i0 + 1];
                float3 worldPos = slot0.xyz;
                float bladeWidth = slot0.w;
                float bladeHeight = slot1.x;

                float tipFactor = input.positionOS.y;
                float windWave = sin(_WindParams.w * 2.5 + worldPos.x * 0.3 + worldPos.z * 0.3)
                    + sin(_WindParams.w * 1.8 + worldPos.x * 0.7 - worldPos.z * 0.5) * 0.7;
                float2 windOffset = _WindParams.xy * (tipFactor * _WindParams.z * 0.5) * windWave;

                float3 pos = input.positionOS;
                pos.xz *= (1.0 - tipFactor * 0.8) * bladeWidth;
                pos.y *= bladeHeight;

                float3 viewDir = normalize(_GrassCameraPos - worldPos);
                float3 up = float3(0, 1, 0);
                float3 right = normalize(cross(up, viewDir));
                float3x3 billboardMat = float3x3(right, up, cross(right, up));
                pos = mul(billboardMat, pos);
                pos.xz += windOffset;

                float3 finalWorldPos = worldPos + pos;
                output.positionWS = finalWorldPos;
                output.positionCS = TransformWorldToHClip(finalWorldPos);
                output.uv = input.uv;
                output.normalWS = up;
                output.tangentWS = right;
                output.height = tipFactor;
                output.shadowCoord = TransformWorldToShadowCoord(finalWorldPos);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                clip(texColor.a - _Cutoff);

                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv), _NormalScale);
                real3x3 tbn = CreateTangentToWorld(input.normalWS, input.tangentWS, 1.0);
                float3 N = normalize(TransformTangentToWorld(normalTS, tbn));

                half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, input.uv);
                half ao = saturate(mask.g + 0.1);
                ao *= lerp(0.3, 1.0, input.height);
                half metallic = mask.r;
                half smoothness = mask.a;

                #if defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #else
                float4 shadowCoord = input.shadowCoord;
                #endif
                Light light = GetMainLight(shadowCoord, input.positionWS, half4(0, 0, 0, 1));
                float3 V = normalize(_GrassCameraPos - input.positionWS);

                float3 terrainN = GetTerrainNormal(input.positionWS, _SSSRadius);
                float3 N_sss = normalize(lerp(N, terrainN, _SSSIntensity));
                float3 L = light.direction;
                half NdotL_sss = saturate(dot(N_sss, L));

                half3 albedo = lerp(_BaseColor, _HighlightColor, input.height) * texColor.rgb;
                float v1 = NdotL_sss + 1;
                float v2 = NdotL_sss;
                float3 diffuse1 = lerp(_ShadowColor, _BaseColor, v1);
                float3 diffuse2 = lerp(_BaseColor, _HighlightColor, v2);
                half3 diffuse = lerp(diffuse1, diffuse2, NdotL_sss > 0) * texColor.rgb;

                float distFade = saturate(distance(_GrassCameraPos, input.positionWS) / 150.0);
                float3 spec = GrassMultiSpecular(N, terrainN, V, L, input.height,
                                                 _GrassWater, metallic, smoothness,
                                                 distFade);
                float3 radiance = light.color * (light.shadowAttenuation * light.distanceAttenuation)
                    * NdotL_sss;

                float thickness = SAMPLE_TEXTURE2D(_ThicknessMap, sampler_ThicknessMap, input.uv).r;
                float thicknessFade = (1.0 - thickness) * ao * lerp(0.1, 0.3, input.height);
                float trans = lerp(
                    SimpleTransmission(N, L, V, thicknessFade),
                    SimpleTransmission(N_sss, L, V, thicknessFade), _SSSIntensity);

                float3 result = lerp(diffuse, spec + trans * albedo, _GrassWater) * radiance;

                // Indirect diffuse from atmosphere SH — zero texture reads
                half3 ambient = SampleAtmoSH(N_sss) * albedo * ao;
                result += ambient;

                return half4(result, 1);
            }
            ENDHLSL
        }
    }
}