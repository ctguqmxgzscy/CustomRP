Shader "Hidden/Universal Render Pipeline/SSRComposite"
{
    // 全屏合成 Pass：将 _SsrAccumTexture（预乘 alpha 反射颜色）与场景混合
    //   对齐 HDRP EvaluateBSDF_ScreenSpaceReflection（Lit.hlsl:1823）：
    //     SSR radiance × Fresnel（specularFGD）→ 按 opacity 与底层场景混合
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "SSRComposite"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            TEXTURE2D_X(_SsrAccumTexture);

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

            // Schlick Fresnel: F0 + (1-F0) * (1-cosTheta)^5
            // F0 从 smoothness 近似: lerp(0.04 (非金属), 1.0 (完美镜面), smoothness)
            half3 FresnelSchlick(half3 F0, half cosTheta)
            {
                half t = 1.0 - cosTheta;
                half t5 = t * t * t * t * t; // pow(t, 5) 展开，避免 pow() 开销
                return F0 + (1.0 - F0) * t5;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);

                // 场景颜色（已包含直接光照 + 间接光照 + probe/sky）
                half3 sceneColor = SampleSceneColor(input.uv);

                // SSR 反射（预乘 alpha：rgb = color * opacity, a = opacity）
                half4 reflection = SAMPLE_TEXTURE2D_X(_SsrAccumTexture, sampler_CameraOpaqueTexture, uv);

                // 无 SSR 覆盖 → 直接返回场景
                if (reflection.a < 0.001)
                    return half4(sceneColor, 1.0);

                half3 reflectionColor = reflection.rgb / reflection.a; // 反预乘
                half  opacity         = reflection.a;

                // ---- Fresnel（对齐 HDRP Lit.hlsl EvaluateBSDF_ScreenSpaceReflection） ----

                // 世界位置 + 视线方向
                float depth = SampleSceneDepth(input.uv);
                float3 worldPos = ComputeWorldSpacePosition(input.uv, depth, UNITY_MATRIX_I_VP);
                half3 V = GetWorldSpaceNormalizeViewDir(worldPos);

                // 直接采样 _CameraNormalsTexture 获取 RGBA 四通道
                //   RGB = world-space normal（URP SNorm 自动解码为 [-1,1]）
                //   A   = per-pixel smoothness [0,1]
                float4 normalAndSmoothness = SAMPLE_TEXTURE2D_X(
                    _CameraNormalsTexture, sampler_CameraNormalsTexture, uv);
                half3 N = normalAndSmoothness.xyz;
                half  smoothness = normalAndSmoothness.a;

                // 若 smoothness 未写入（非 Lit shader），回退到中等反射率
                half3 F0 = (smoothness > 0.001)
                    ? lerp(half3(0.04, 0.04, 0.04), half3(1.0, 1.0, 1.0), smoothness)
                    : half3(0.04, 0.04, 0.04);

                half NdotV = saturate(dot(N, V));
                half3 fresnel = FresnelSchlick(F0, NdotV);

                // ---- 混合 ----
                //   SSR 贡献 = reflectionColor × fresnel × opacity
                //   对齐 HDRP: SSR fills reflectionHierarchyWeight = opacity
                //                 底层场景(probe/sky)在 opacity < 1 时透过
                half3 result = sceneColor + reflectionColor * fresnel * opacity;

                return half4(result, 1.0);
            }
            ENDHLSL
        }
    }
}
