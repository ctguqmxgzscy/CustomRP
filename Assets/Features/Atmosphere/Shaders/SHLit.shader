Shader "Custom/SHLit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.3, 0.6, 0.2, 1)
        _BaseMap ("Base Map", 2D) = "white"{}
        [Normal] _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalScale ("Normal Scale", Range(0, 2)) = 1.0
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
            Name "SHLitPass"
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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _Cutoff;
                float _NormalScale;
                float _WindStrength;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

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

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // World position from transform matrix
                float3 positionWS = TransformObjectToWorld(input.positionOS);

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;

                // TBN
                float3 viewDir = normalize(_WorldSpaceCameraPos - positionWS);
                float3 up = float3(0, 1, 0);
                output.normalWS = up;
                output.tangentWS = normalize(cross(up, viewDir));
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half3 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor;
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv), _NormalScale);
                real3x3 tbn = CreateTangentToWorld(input.normalWS, input.tangentWS, 1.0);
                float3 N = normalize(TransformTangentToWorld(normalTS, tbn));

                return half4(SampleAtmoSH(N) * color.rgb, 1);
            }
            ENDHLSL
        }
    }
}