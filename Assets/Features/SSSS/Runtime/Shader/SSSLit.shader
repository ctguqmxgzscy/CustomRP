Shader "Custom/SSSLit"
{
    Properties
    {
        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0

        [MainTexture] _BaseMap("Base Texture",2D) = "white"{}
        [HDR] _BaseColor("Base Color",Color)=(1,1,1,1)

        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.1

        _Metallic("Metallic", Range(0.0, 1.0)) = 0.1
        _MetallicGlossMap("Metallic", 2D) = "white" {}

        [ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleOff] _EnvironmentReflections("Environment Reflections", Float) = 1.0

        _BumpScale("Scale", Float) = 1.0
        _BumpMap("Normal Map", 2D) = "bump" {}

        _Parallax("Scale", Range(0.005, 0.08)) = 0.005
        _ParallaxMap("Height Map", 2D) = "black" {}

        _OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap("Occlusion", 2D) = "white" {}

        [HDR] _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}

        // Blending state
        _Surface("__surface", Float) = 0.0
        _Blend("__blend", Float) = 0.0
        _Cull("__cull", Float) = 2.0
        [ToggleUI] _AlphaClip("__clip", Float) = 0.0
        [ToggleUI] _UseDither("Use Dither", Float) = 0.0
        _DitherSize("Dither Size", Range(1.0, 100.0)) = 1.0

        [HideInInspector] _BlendModePreserveSpecular("_BlendModePreserveSpecular", Float) = 1.0

        // Editmode props
        _QueueOffset("Queue offset", Float) = 0.0

        [IntRange] _StencilRef("SSS Stencil Ref", Range(0, 255)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "RenderType"="Opaque"
        }

        LOD 300

        Pass
        {
            Name "Custom Forward Lit"
            Tags
            {
                "LightMode" = "Skin MRT"
            }

            Blend One Zero
            ZWrite On
            Cull[_Cull]

            // 写入 Stencil 标记
            Stencil
            {
                Ref [_StencilRef]
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment Frag

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ _ALPHAPREMULTIPLY_ON _ALPHAMODULATE_ON
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULAR_SETUP

            // -------------------------------------
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _FORWARD_PLUS

            #pragma multi_compile_fog

            #include "Assets/Shaders/CustomLit/CustomBRDF.hlsl"
            #include "Assets/Shaders/CustomLit/CustomLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Shaders/ShaderLibrary/CustomLighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 tangentOS : TANGENT;
                float2 texcoord : TEXCOORD0;
                float3 normal : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                float4 positionNDC : TEXCOORD4;
                float fogCoord : TEXCOORD5;
                #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
                float4 tangentWS : TEXCOORD6;
                #endif
            };

            // ── 声明三输出结构体 ──
            struct FragOutput
            {
                float4 diffuse : SV_Target0;  // _SkinDiffuseRT  — 直接漫反射（将被 SSS blur）
                float4 specular : SV_Target1; // _SkinSpecularRT — 镜面反射（保持锐利）
                float4 ambient : SV_Target2;  // _SkinAmbientRT  — 间接光 + 自发光（保持锐利）
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normal, input.tangentOS);
                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;

                // already normalized from normal transform to WS.
                output.normalWS = normalInput.normalWS;
                #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR) || defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                real sign = input.tangentOS.w * GetOddNegativeScale();
                half4 tangentWS = half4(normalInput.tangentWS.xyz, sign);
                #endif

                #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
                output.tangentWS = tangentWS;
                #endif

                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
                output.positionNDC = vertexInput.positionNDC;
                output.fogCoord = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            FragOutput Frag(Varyings input) : SV_Target
            {
                float4 metallicAndSmothness = GetMetallicAndSmoothness(input.uv);
                float metallic = metallicAndSmothness.r;

                float occlusion = GetOcclusion(input.uv);
                AmbientOcclusionFactor aoFactor;
                aoFactor.directAmbientOcclusion = 1;
                aoFactor.indirectAmbientOcclusion = 1;
                aoFactor.indirectAmbientOcclusion = min(aoFactor.indirectAmbientOcclusion, occlusion);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = input.normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
                Light light = GetMainLight(inputData.shadowCoord, input.positionWS, half4(1, 1, 1, 1));
                ApplyHighQualityShadow(light, input.positionWS);

                #if defined(_NORMALMAP)
                float3 N = GetNormal(input.uv, input.normalWS, input.tangentWS);
                #else
                float3 N = normalize(input.normalWS);
                #endif

                float3 L = normalize(light.direction);
                float3 V = normalize(GetWorldSpaceViewDir(input.positionWS));
                float3 H = normalize(L + V);

                float NdotH = saturate(dot(N, H));
                float NdotV = saturate(dot(N, V));
                float NdotL = saturate(dot(N, L));
                float halfLambert = 0.5 + 0.5 * dot(N, L);
                float HdotV = saturate(dot(H, V));

                float roughnessPercep = PerceptualSmoothnessToPerceptualRoughness(metallicAndSmothness.a);
                float roughness = max(PerceptualRoughnessToRoughness(roughnessPercep), HALF_MIN_SQRT);
                float roughness2 = max(roughness * roughness, HALF_MIN);

                float D = DistributionGGX(NdotH, roughness);
                float G = GeometrySmith(NdotL, NdotV, roughness);

                float3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                float3 F0 = lerp(kDielectricSpec.rgb, albedo.rgb, metallic);
                float3 F = FresnelSchlick(HdotV, F0);
                float3 brdfSpecular = D * G * F / (4 * NdotL * NdotV + 0.00001);
                // F已经给出了反射率
                float3 kS = F;
                float3 kD = (1 - kS) * (1 - metallic);
                half3 radiance = light.color * (light.distanceAttenuation * light.shadowAttenuation * NdotL) * PI;

                float3 kS_Indirect = FresnelSchlickRoughness(NdotV, F0, roughness);
                // 从而得到对间接光的折射率
                float3 kD_Indirect = (1 - kS_Indirect) * (1 - metallic) * albedo.rgb;
                // 给定N方向向量，得到所有能打到该片段的间接漫反射光 * 间接折射率
                float3 indirectDiffuse = kD_Indirect * SampleSH(N);

                float3 reflectDir = reflect(-V, N);
                float3 indirectSpecular = GlossyEnvironmentReflection(reflectDir, inputData.positionWS, roughnessPercep,
                                                                      1.0h, inputData.normalizedScreenSpaceUV);

                // 计算F项,为什么是N在计算kS_Indirect是已给出解释
                float F_Indirect = Pow4(1.0 - NdotV);
                float surfaceReduction = 1.0 / (roughness2 + 1.0);
                half oneMinusReflectivity = OneMinusReflectivityMetallic(metallic);
                half grazingTerm = saturate(half(1.0) - oneMinusReflectivity + (1 - roughnessPercep));
                // 间接光的brdf = DG * F
                float3 envBRDF = surfaceReduction * lerp(kS_Indirect, grazingTerm, F_Indirect);
                float3 emissionCol = SampleEmission(input.uv, _EmissionColor.rgb,
                                                    TEXTURE2D_ARGS(
                                                        _EmissionMap, sampler_EmissionMap));

                // Lo 拆为三部分：直接漫反射（blur）、镜面反射、间接光/自发光
                float3 diffusePart = kD * albedo.rgb / PI * radiance;

                float3 specularPart = brdfSpecular * radiance
                    + indirectSpecular.xyz * envBRDF * aoFactor.indirectAmbientOcclusion;

                float3 ambientPart = indirectDiffuse + emissionCol;

                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();

                #if USE_FORWARD_PLUS
                for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS);
                  lightIndex++)
                {
                    FORWARD_PLUS_SUBTRACTIVE_LIGHT_CHECK
                    Light addLight = GetAdditionalLight(lightIndex, inputData.positionWS, half4(1, 1, 1, 1));
                    float3 L_add = normalize(addLight.direction);
                    float3 H_add = normalize(L_add + V);

                    float NdotL_add = saturate(dot(N, L_add));
                    float NdotH_add = saturate(dot(N, H_add));
                    float HdotV_add = saturate(dot(H_add, V));

                    float D_add = DistributionGGX(NdotH_add, roughness);
                    float G_add = GeometrySmith(NdotL_add, NdotV, roughness);
                    float3 F_add = FresnelSchlick(HdotV_add, F0);
                    float3 brdfSpecular_add = D_add * G_add * F_add / (4 * NdotL_add * NdotV + 0.00001);

                    float3 kS_add = F_add;
                    float3 kD_add = (1 - kS_add) * (1 - metallic);

                    half3 radiance_add = addLight.color * (addLight.distanceAttenuation * addLight.shadowAttenuation *
                        NdotL_add) * PI;
                    diffusePart += kD_add * albedo / PI * radiance_add * aoFactor.directAmbientOcclusion;
                    specularPart += brdfSpecular_add * radiance_add * aoFactor.directAmbientOcclusion;
                }
                #endif

                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light additionalLight = GetAdditionalLight(lightIndex, inputData.positionWS, half4(1, 1, 1, 1));
                    float3 L_add = normalize(additionalLight.direction);
                    float3 H_add = normalize(L_add + V);

                    float NdotL_add = saturate(dot(N, L_add));
                    float NdotH_add = saturate(dot(N, H_add));
                    float HdotV_add = saturate(dot(H_add, V));

                    float D_add = DistributionGGX(NdotH_add, roughness);
                    float G_add = GeometrySmith(NdotL_add, NdotV, roughness);
                    float3 F_add = FresnelSchlick(HdotV_add, F0);
                    float3 brdfSpecular_add = D_add * G_add * F_add / (4 * NdotL_add * NdotV + 0.00001);

                    float3 kS_add = F_add;
                    float3 kD_add = (1 - kS_add) * (1 - metallic);

                    half3 radiance_add = additionalLight.color * (additionalLight.distanceAttenuation * additionalLight.
                        shadowAttenuation * NdotL_add) * PI;
                    diffusePart += kD_add * albedo / PI * radiance_add * aoFactor.directAmbientOcclusion;
                    specularPart += brdfSpecular_add * radiance_add * aoFactor.directAmbientOcclusion;
                LIGHT_LOOP_END
                #endif

                FragOutput o;
                o.diffuse  = float4(diffusePart, 1.0);
                o.specular = float4(specularPart, 1.0);
                o.ambient  = float4(ambientPart, 1.0);
                return o;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ALPHADITHER_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Universal Pipeline keywords

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE

            // This is used during shadow map generation to differentiate between directional and punctual light shadows, as they use different formulas to apply Normal Bias
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            // -------------------------------------
            // Includes
            #include "Assets/Shaders/CustomLit/CustomLitInput.hlsl"
            #include "Assets/Shaders/ShaderLibrary/CustomShadowCaster.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ALPHADITHER_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "Assets/Shaders/CustomLit/CustomLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // This pass is used when drawing to a _CameraNormalsTexture texture
        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormals"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ALPHADITHER_ON
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE

            // -------------------------------------
            // Universal Pipeline keywords
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "Assets/Shaders/CustomLit/CustomLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #if defined(LOD_FADE_CROSSFADE)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
            #endif

            #if defined(_DETAIL_MULX2) || defined(_DETAIL_SCALED)
            #define _DETAIL
            #endif

            // GLES2 has limited amount of interpolators
            #if defined(_PARALLAXMAP) && !defined(SHADER_API_GLES)
            #define REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR
            #endif

            #if (defined(_NORMALMAP) || (defined(_PARALLAXMAP) && !defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR))) || defined(_DETAIL)
            #define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 tangentOS : TANGENT;
                float2 texcoord : TEXCOORD0;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD1;
                half3 normalWS : TEXCOORD2;

                #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
                half4 tangentWS : TEXCOORD4; // xyz: tangent, w: sign
                #endif

                half3 viewDirWS : TEXCOORD5;

                #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                half3 viewDirTS : TEXCOORD8;
                #endif

                float3 positionWS : TEXCOORD9;

                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normal, input.tangentOS);

                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
                output.normalWS = half3(normalInput.normalWS);
                output.positionWS = vertexInput.positionWS;
                #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR) || defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                float sign = input.tangentOS.w * float(GetOddNegativeScale());
                half4 tangentWS = half4(normalInput.tangentWS.xyz, sign);
                #endif

                #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
                output.tangentWS = tangentWS;
                #endif

                #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                half3 viewDirTS = GetViewDirectionTangentSpace(tangentWS, output.normalWS, viewDirWS);
                output.viewDirTS = viewDirTS;
                #endif

                return output;
            }

            void DepthNormalsFragment(
                Varyings input
                , out half4 outNormalWS : SV_Target0
                #ifdef _WRITE_RENDERING_LAYERS
                , out float4 outRenderingLayers : SV_Target1
                #endif
            )
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                #if defined(_ALPHADITHER_ON)
                float2 screenPos = input.positionCS.xy / _DitherSize;
                uint index = (uint(screenPos.x) % 4) * 4 + uint(screenPos.y) % 4;
                float jitter = SampleBlueNoise(input.positionCS / _DitherSize);
                Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, jitter);
                #else
                Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
                #endif

                #ifdef LOD_FADE_CROSSFADE
                LODFadeCrossFade(input.positionCS);
                #endif

                #if defined(_GBUFFER_NORMALS_OCT)
                float3 normalWS = normalize(input.normalWS);
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                // values between [-1, +1], must use fp32 on some platforms
                float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5); // values between [ 0,  1]
                half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS); // values between [ 0,  1]
                outNormalWS = half4(packedNormalWS, 0.0);
                #else
                float2 uv = input.uv;
                #if defined(_PARALLAXMAP)
                #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                half3 viewDirTS = input.viewDirTS;
                #else
                half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, input.viewDirWS);
                #endif
                ApplyPerPixelDisplacement(viewDirTS, uv);
                #endif

                #if defined(_NORMALMAP) || defined(_DETAIL)
                float sgn = input.tangentWS.w; // should be either +1 or -1
                float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
                float3 normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);

                #if defined(_DETAIL)
                half detailMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, uv).a;
                float2 detailUv = uv * _DetailAlbedoMap_ST.xy + _DetailAlbedoMap_ST.zw;
                normalTS = ApplyDetailNormal(detailUv, normalTS, detailMask);
                #endif

                float3 normalWS = TransformTangentToWorld(
                    normalTS, half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz));
                #else
                float3 normalWS = input.normalWS;
                #endif

                float4 metallicAndSmothness = GetMetallicAndSmoothness(input.uv);
                float metallic = metallicAndSmothness.r;
                outNormalWS = half4(NormalizeNormalPerPixel(normalWS), metallicAndSmothness.a);
                #endif

                #ifdef _WRITE_RENDERING_LAYERS
                uint renderingLayers = GetMeshRenderingLayer();
                outRenderingLayers = float4(EncodeMeshRenderingLayer(renderingLayers), 0, 0, 0);
                #endif
            }
            ENDHLSL
        }

        Pass
        {
            // Lightmode matches the ShaderPassName set in UniversalRenderPipeline.cs. SRPDefaultUnlit and passes with
            // no LightMode tag are also rendered by Universal Render Pipeline
            Name "GBuffer"
            Tags
            {
                "LightMode" = "UniversalGBuffer"
            }

            // -------------------------------------
            // Render State Commands
            Blend One Zero
            ZWrite On
            ZTest LEqual
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 4.5

            // Deferred Rendering Path does not support the OpenGL-based graphics API:
            // Desktop OpenGL, OpenGL ES 3.0, WebGL 2.0.
            #pragma exclude_renderers gles3 glcore

            // -------------------------------------
            // Shader Stages
            #pragma vertex LitGBufferPassVertex
            #pragma fragment LitGBufferPassFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ALPHADITHER_ON
            //#pragma shader_feature_local_fragment _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED

            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULAR_SETUP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF

            // -------------------------------------
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            //#pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            //#pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "Assets/Shaders/CustomLit/CustomLitInput.hlsl"
            #include "Assets/Shaders/CustomLit/CustomLitGBufferPass.hlsl"
            ENDHLSL
        }

    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "Star3D.Editor.ShaderGUI.CustomLitShader"
}