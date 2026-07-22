// Grass Shader (Non-Indirect)
// Standard Unity instancing path — no StructuredBuffer, no SV_InstanceID.
// Compatible with DrawMeshInstanced, Terrain detail, regular MeshRenderer.

Shader "Custom/Grass"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.3, 0.6, 0.2, 1)
        _TipColor ("Tip Color", Color) = (0.5, 0.8, 0.3, 1)
        _ShadowColor ("Shadow Color", Color) = (0.05, 0.1, 0.02, 1)
        _HighlightColor ("Highlight Color", Color) = (0.6, 0.85, 0.3, 1)
        _BaseMap ("Base Texture", 2D) = "white" {}
        _MaskMap ("Mask Map (R=Metallic G=AO B=-- A=Smoothness)", 2D) = "white" {}
        [Normal] _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalScale ("Normal Scale", Range(0, 2)) = 1.0
        _SpecularColor ("Specular Color", Color) = (1, 1, 1, 1)
        _SpecularSmoothness ("Specular Smoothness", Range(0, 1)) = 0.5
        _WindStrength ("Wind Strength", Range(0, 2)) = 0.5
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 tangentWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                float height : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _TipColor;
                float4 _ShadowColor;
                float4 _HighlightColor;
                float4 _SpecularColor;
                float _Cutoff;
                float _NormalScale;
                float _SpecularSmoothness;
                float _WindStrength;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_MaskMap);
            SAMPLER(sampler_MaskMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            // -----------------------------------------------------------------
            // GGX Specular — URP builtin functions (BSDF.hlsl)
            // D_GGX(NdotH, roughness)           → Normal Distribution (incl. 1/PI)
            // V_SmithJointGGX(NdotL,NdotV,rough) → Visibility (G / 4*NdotL*NdotV)
            // F_Schlick(f0, u)                  → Fresnel
            // -----------------------------------------------------------------
            // Note: spec BRDF = D * V * F  (no extra division needed, V handles it)
            // -----------------------------------------------------------------
            float GGX_DistanceFade(float3 N, float3 V, float3 L, float roughness, float distanceFade)
            {
                float3 H = normalize(L + V);
                float NdotH = saturate(dot(N, H));
                float NdotV = saturate(dot(N, V));

                float D = D_GGX(NdotH, roughness);
                float F = F_Schlick(0.04, NdotV);

                // Kill G for more natural specular on vegetation
                return D * F * distanceFade;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // World position from transform matrix
                float3 worldPos = TransformObjectToWorld(input.positionOS);

                // Scale from instance matrix (assumes uniform scale)
                float bladeScale = length(float3(
                    UNITY_MATRIX_M._m00, UNITY_MATRIX_M._m10, UNITY_MATRIX_M._m20));

                // Wind
                float tipFactor = input.positionOS.y;
                float windWave = sin(_Time.y * 2.5 + worldPos.x * 0.3 + worldPos.z * 0.3)
                    + sin(_Time.y * 1.8 + worldPos.x * 0.7 - worldPos.z * 0.5) * 0.7;
                float windAmount = tipFactor * _WindStrength * 0.5;
                float2 windDir = float2(0.4, 0.8);
                float2 windOffset = windDir * windAmount * windWave;

                // Blade shape
                float3 pos = input.positionOS;
                pos.xz *= (1.0 - tipFactor * 0.8);
                pos.xz *= bladeScale;
                pos.xz += windOffset * tipFactor;

                float3 finalWorldPos = worldPos + float3(pos.x, pos.y * bladeScale, pos.z);
                output.positionWS = finalWorldPos;
                output.positionCS = TransformWorldToHClip(finalWorldPos);
                output.uv = input.uv;

                // TBN
                float3 viewDir = normalize(_WorldSpaceCameraPos - finalWorldPos);
                float3 up = float3(0, 1, 0);
                output.normalWS = up;
                output.tangentWS = normalize(cross(up, viewDir));
                output.height = tipFactor;

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                clip(texColor.a - _Cutoff);
                half4 col = lerp(_BaseColor, _TipColor, input.height) * texColor;

                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv), _NormalScale);
                real3x3 tbn = CreateTangentToWorld(input.normalWS, input.tangentWS, 1.0);
                float3 N = normalize(TransformTangentToWorld(normalTS, tbn));

                half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, input.uv);
                half ao = lerp(1.0, mask.g, 0.5);

                Light light = GetMainLight();
                float3 L = light.direction;
                half NdotL = saturate(dot(N, L));

                // Three-color diffuse ramp
                float v1 = NdotL + 1;
                float v2 = NdotL;
                float3 diffuse1 = lerp(_ShadowColor.rgb, _BaseColor.rgb, v1);
                float3 diffuse2 = lerp(_BaseColor.rgb, _HighlightColor.rgb, v2);
                half3 diffuse = lerp(diffuse1, diffuse2, NdotL > 0);

                // Specular (GGX)
                float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);
                float roughness = lerp(0.8, 0.2, mask.a); // smoothness → roughness
                float dist = distance(_WorldSpaceCameraPos, input.positionWS);
                float distFade = saturate(1.0 - dist / 50.0); // fade over 50m
                float spec = GGX_DistanceFade(N, V, L, roughness, distFade);
                return half4(spec.rrr, 1);
                half3 ambient = col.rgb * 0.3 * ao;
                col.rgb = diffuse + spec * _SpecularColor.rgb + ambient;
                return col;
            }
            ENDHLSL
        }
    }
}