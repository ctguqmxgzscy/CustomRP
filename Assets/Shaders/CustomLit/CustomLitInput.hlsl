#ifndef UNIVERSAL_CUSTOM_LIT_INPUT_INCLUDED
#define UNIVERSAL_CUSTOM_LIT_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Assets/Shaders/ShaderLibrary/CustomSurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ParallaxMapping.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

#if defined(_DETAIL_MULX2) || defined(_DETAIL_SCALED)
#define _DETAIL
#endif

// NOTE: Do not ifdef the properties here as SRP batcher can not handle different layouts.
CBUFFER_START(UnityPerMaterial)
    half4 _BaseMap_ST;
    half4 _BaseColor;
    half4 _EmissionColor;
    half _Cutoff;
    half _Smoothness;
    half _Metallic;
    half _BumpScale;
    half _Parallax;
    half _OcclusionStrength;

    half _Translucency;
    float _SSSWidth;
    half4 _TransmittanceTint;
    // ------------------ Custom -------------------
    #if defined(_ALPHADITHER_ON)
    float _DitherSize;
    float _DitherFade;
    #endif
CBUFFER_END

// NOTE: Do not ifdef the properties for dots instancing, but ifdef the actual usage.
// Otherwise you might break CPU-side as property constant-buffer offsets change per variant.
// NOTE: Dots instancing is orthogonal to the constant buffer above.
#ifdef INSTANCING_ON

UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
    UNITY_DEFINE_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DEFINE_INSTANCED_PROP(float, _Scale)
    UNITY_DEFINE_INSTANCED_PROP(float, _SpecularTreshold)
    UNITY_DEFINE_INSTANCED_PROP(float4, _EdgeColor)
    UNITY_DEFINE_INSTANCED_PROP(float, _EdgeTreshold)
    UNITY_DEFINE_INSTANCED_PROP(float, _EdgeThickness)
    UNITY_DEFINE_INSTANCED_PROP(float, _UVDisturStrength)
    UNITY_DEFINE_INSTANCED_PROP(float, _UVDisturFrequency)
    UNITY_DEFINE_INSTANCED_PROP(float, _UVScale)
    UNITY_DEFINE_INSTANCED_PROP(float, _Cutoff)
UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)

#define _BaseColor              UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _BaseColor)
#define _Scale                  UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Scale)
#define _SpecularTreshold       UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _SpecularTreshold)
#define _EdgeColor              UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EdgeColor)
#define _EdgeTreshold           UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EdgeTreshold)
#define _EdgeThickness          UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EdgeThickness)
#define _UVDisturStrength       UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _UVDisturStrength)
#define _UVDisturFrequency      UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _UVDisturFrequency)
#define _UVScale                UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _UVScale)

#define _Cutoff                 UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Cutoff)
#endif

TEXTURE2D(_ParallaxMap);
SAMPLER(sampler_ParallaxMap);
TEXTURE2D(_OcclusionMap);
SAMPLER(sampler_OcclusionMap);
TEXTURE2D(_DetailMask);
SAMPLER(sampler_DetailMask);
TEXTURE2D(_DetailAlbedoMap);
SAMPLER(sampler_DetailAlbedoMap);
TEXTURE2D(_DetailNormalMap);
SAMPLER(sampler_DetailNormalMap);
TEXTURE2D(_MetallicGlossMap);
SAMPLER(sampler_MetallicGlossMap);
TEXTURE2D(_SpecGlossMap);
SAMPLER(sampler_SpecGlossMap);
TEXTURE2D(_ClearCoatMap);
SAMPLER(sampler_ClearCoatMap);
// GLES2 has limited amount of interpolators
#if defined(_PARALLAXMAP) && !defined(SHADER_API_GLES)
#define REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR
#endif

#if (defined(_NORMALMAP) || (defined(_PARALLAXMAP) && !defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)))
#define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
#endif

float3 GetNormal(float2 uv, float3 normalWS, float4 tangentWS)
{
    float sgn = tangentWS.w; // should be either +1 or -1
    float3 bitangent = sgn * cross(normalWS.xyz, tangentWS.xyz);
    half3x3 tangentToWorld = half3x3(tangentWS.xyz, bitangent.xyz, normalWS.xyz);
    float3 normalTS = SampleNormal(uv, _BumpMap, sampler_BumpMap, _BumpScale);
    normalTS.z = pow(1 - pow(normalTS.x, 2) - pow(normalTS.y, 2), 0.5); //规范化法线
    return normalize(TransformTangentToWorld(normalTS, tangentToWorld));
}

half4 GetMetallicAndSmoothness(float2 uv)
{
    half4 metallic;
    #ifdef _METALLICSPECGLOSSMAP
    metallic = half4(SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv));
    metallic.a *= _Smoothness;
    #else
    metallic.rgb = _Metallic.rrr;
    metallic.a = _Smoothness;
    #endif

    return metallic;
}

half GetOcclusion(float2 uv)
{
    #ifdef _OCCLUSIONMAP
    half occ = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, uv).g;
    return LerpWhiteTo(occ, _OcclusionStrength);
    #else
    return half(1.0);
    #endif
}

half4 SampleMetallicSpecGloss(float2 uv, half albedoAlpha)
{
    half4 specGloss;

    #ifdef _METALLICSPECGLOSSMAP
    specGloss = half4(SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv));
    #ifdef _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
    specGloss.a = albedoAlpha * _Smoothness;
    #else
    specGloss.a *= _Smoothness;
    #endif
    #else // _METALLICSPECGLOSSMAP
    #if _SPECULAR_SETUP
    specGloss.rgb = _SpecColor.rgb;
    #else
    specGloss.rgb = _Metallic.rrr;
    #endif

    #ifdef _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
    specGloss.a = albedoAlpha * _Smoothness;
    #else
    specGloss.a = _Smoothness;
    #endif
    #endif

    return specGloss;
}

half SampleOcclusion(float2 uv)
{
    #ifdef _OCCLUSIONMAP
    half occ = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, uv).g;
    return LerpWhiteTo(occ, _OcclusionStrength);
    #else
    return half(1.0);
    #endif
}

inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData outSurfaceData)
{
    half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
    outSurfaceData.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);

    half4 specGloss = SampleMetallicSpecGloss(uv, albedoAlpha.a);
    outSurfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;
    outSurfaceData.albedo = AlphaModulate(outSurfaceData.albedo, outSurfaceData.alpha);

    #if _SPECULAR_SETUP
    outSurfaceData.metallic = half(1.0);
    outSurfaceData.specular = specGloss.rgb;
    #else
    outSurfaceData.metallic = specGloss.r;
    outSurfaceData.specular = half3(0.0, 0.0, 0.0);
    #endif

    outSurfaceData.smoothness = specGloss.a;
    outSurfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
    outSurfaceData.occlusion = SampleOcclusion(uv);
    outSurfaceData.emission = SampleEmission(uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));

    #if defined(_CLEARCOAT) || defined(_CLEARCOATMAP)
    half2 clearCoat = SampleClearCoat(uv);
    outSurfaceData.clearCoatMask = clearCoat.r;
    outSurfaceData.clearCoatSmoothness = clearCoat.g;
    #else
    outSurfaceData.clearCoatMask = half(0.0);
    outSurfaceData.clearCoatSmoothness = half(0.0);
    #endif

    #if defined(_DETAIL)
    half detailMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, uv).a;
    float2 detailUv = uv * _DetailAlbedoMap_ST.xy + _DetailAlbedoMap_ST.zw;
    outSurfaceData.albedo = ApplyDetailAlbedo(detailUv, outSurfaceData.albedo, detailMask);
    outSurfaceData.normalTS = ApplyDetailNormal(detailUv, outSurfaceData.normalTS, detailMask);
    #endif
}

#endif
