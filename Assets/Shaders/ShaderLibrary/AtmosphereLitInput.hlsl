#ifndef ATMOSPHERE_LIT_INPUT_INCLUDED
#define ATMOSPHERE_LIT_INPUT_INCLUDED

// URP core libraries — included once, shared by all passes that use this file
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "ScatteringUtils.hlsl"

// ---------------------------------------------------------------------------
// Tangent interpolation — required when normal map is used
// ---------------------------------------------------------------------------
#if defined(_NORMALMAP)
#define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
#endif

// ---------------------------------------------------------------------------
// Textures & Samplers
// ---------------------------------------------------------------------------
TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);

TEXTURE2D(_BumpMap);
SAMPLER(sampler_BumpMap);

TEXTURE2D(_EmissionMap);
SAMPLER(sampler_EmissionMap);

TEXTURE2D(_OcclusionMap);
SAMPLER(sampler_OcclusionMap);

TEXTURE2D(_MetallicGlossMap);
SAMPLER(sampler_MetallicGlossMap);

TEXTURE2D(_SpecGlossMap);
SAMPLER(sampler_SpecGlossMap);

// ---------------------------------------------------------------------------
// Material Properties (SRP Batcher compatible)
// ---------------------------------------------------------------------------
CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half4 _SpecColor;
    half4 _EmissionColor;
    half _Cutoff;
    half _Smoothness;
    half _Metallic;
    half _BumpScale;
    half _OcclusionStrength;
    float _ViewSamples;
    float _AtmosphereRadius;
    float _PlanetRadius;
    // Atmospheric scattering
    float _ScaleHeight;
    float _MieScaleHeight;
    float _SunIntensity;
    float _LightSamples;
    float _MieG;
CBUFFER_END

// ---------------------------------------------------------------------------
// Shared Structs
// ---------------------------------------------------------------------------
struct AtmosphericLitAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    float2 uv : TEXCOORD0;
    float2 staticLightmapUV : TEXCOORD1;
    float2 dynamicLightmapUV : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct AtmosphericLitVaryings
{
    float2 uv : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    float3 normalWS : TEXCOORD2;
    #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    float4 tangentWS : TEXCOORD3; // xyz: tangent, w: sign
    #endif

    #ifdef _ADDITIONAL_LIGHTS_VERTEX
    half4 fogFactorAndVertexLight : TEXCOORD5; // x: fogFactor, yzw: vertex light
    #else
    half fogFactor : TEXCOORD5;
    #endif

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord : TEXCOORD6;
    #endif

    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 7);
    #ifdef DYNAMICLIGHTMAP_ON
    float2 dynamicLightmapUV : TEXCOORD8;
    #endif

    float4 positionCS : SV_POSITION;
    float3 centerWS : TEXCOORD9;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};


// ---------------------------------------------------------------------------
// Initialize SurfaceData
// ---------------------------------------------------------------------------
void InitializeAtmosphericLitSurfaceData(AtmosphericLitVaryings input, out SurfaceData outSurfaceData)
{
    outSurfaceData = (SurfaceData)0;

    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
    half alpha = albedoAlpha.a * _BaseColor.a;

    #ifdef _ALPHATEST_ON
    clip(alpha - _Cutoff);
    #endif

    outSurfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;
    outSurfaceData.alpha = alpha;

    // Normal
    #ifdef _NORMALMAP
    half4 n = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv);
    outSurfaceData.normalTS = UnpackNormalScale(n, _BumpScale);
    #else
    outSurfaceData.normalTS = half3(0, 0, 1);
    #endif

    // Emission
    #ifdef _EMISSION
    outSurfaceData.emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv).rgb * _EmissionColor.rgb;
    #else
    outSurfaceData.emission = half3(0, 0, 0);
    #endif

    // Occlusion
    #ifdef _OCCLUSIONMAP
    half occ = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, input.uv).g;
    outSurfaceData.occlusion = lerp(1.0, occ, _OcclusionStrength);
    #else
    outSurfaceData.occlusion = 1.0;
    #endif

    // Specular / Metallic workflow
    #ifdef _SPECULAR_SETUP
    half4 specGloss = SAMPLE_TEXTURE2D(_SpecGlossMap, sampler_SpecGlossMap, input.uv);
    outSurfaceData.specular = specGloss.rgb * _SpecColor.rgb;
    outSurfaceData.smoothness = specGloss.a * _Smoothness;
    outSurfaceData.metallic = 0;
    #else
    #ifdef _METALLICSPECGLOSSMAP
    half4 metallicGloss = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, input.uv);
    outSurfaceData.metallic = metallicGloss.r * _Metallic;
    outSurfaceData.smoothness = metallicGloss.a * _Smoothness;
    #else
    outSurfaceData.metallic = _Metallic;
    outSurfaceData.smoothness = _Smoothness;
    #endif
    outSurfaceData.specular = half3(0, 0, 0);
    #endif

    // Clear coat — unused
    outSurfaceData.clearCoatMask = 0;
    outSurfaceData.clearCoatSmoothness = 1;
}

// ---------------------------------------------------------------------------
// Initialize InputData
// ---------------------------------------------------------------------------
void InitializeAtmosphericLitInputData(AtmosphericLitVaryings input, half3 normalTS, out InputData outInputData)
{
    outInputData = (InputData)0;

    outInputData.positionWS = input.positionWS;
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(outInputData.positionWS);

    #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    float sgn = input.tangentWS.w;
    float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
    half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz);

    #if defined(_NORMALMAP)
    outInputData.tangentToWorld = tangentToWorld;
    #endif
    outInputData.normalWS = TransformTangentToWorld(normalTS, tangentToWorld);
    #else
    outInputData.normalWS = input.normalWS;
    #endif

    outInputData.normalWS = NormalizeNormalPerPixel(outInputData.normalWS);
    outInputData.viewDirectionWS = SafeNormalize(viewDirWS);

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    outInputData.shadowCoord = input.shadowCoord;
    #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    outInputData.shadowCoord = TransformWorldToShadowCoord(outInputData.positionWS);
    #else
    outInputData.shadowCoord = float4(0, 0, 0, 0);
    #endif

    #ifdef _ADDITIONAL_LIGHTS_VERTEX
    outInputData.fogCoord = InitializeInputDataFog(float4(outInputData.positionWS, 1.0),
                                                   input.fogFactorAndVertexLight.x);
    outInputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
    #else
    outInputData.fogCoord = InitializeInputDataFog(float4(outInputData.positionWS, 1.0), input.fogFactor);
    outInputData.vertexLighting = half3(0, 0, 0);
    #endif

    #if defined(DYNAMICLIGHTMAP_ON)
    outInputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH,
                                     outInputData.normalWS);
    #else
    outInputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, outInputData.normalWS);
    #endif

    outInputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    outInputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
}

// ---------------------------------------------------------------------------
// Common Vertex Shader — fills Varyings, shared by all Forward Lit passes
// ---------------------------------------------------------------------------
AtmosphericLitVaryings AtmosphericLitPassVertex(AtmosphericLitAttributes input)
{
    AtmosphericLitVaryings output = (AtmosphericLitVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    output.normalWS = normalInput.normalWS;

    #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    real sign = input.tangentOS.w * GetOddNegativeScale();
    output.tangentWS = float4(normalInput.tangentWS.xyz, sign);
    #endif

    half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
    half fogFactor = 0;
    #if !defined(_FOG_FRAGMENT)
    fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
    #endif

    #ifdef _ADDITIONAL_LIGHTS_VERTEX
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
    #else
    output.fogFactor = fogFactor;
    #endif

    OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
    #ifdef DYNAMICLIGHTMAP_ON
    output.dynamicLightmapUV = input.dynamicLightmapUV.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
    #endif
    OUTPUT_SH(output.normalWS.xyz, output.vertexSH);

    output.positionWS = vertexInput.positionWS;

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
    #endif

    output.positionCS = vertexInput.positionCS;

    return output;
}

// ---------------------------------------------------------------------------
// Common Fragment Shader — PBR + fog, shared by all Forward Lit passes
// ---------------------------------------------------------------------------
half4 AtmosphericLitPassFragment(AtmosphericLitVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    SurfaceData surfaceData;
    InitializeAtmosphericLitSurfaceData(input, surfaceData);

    InputData inputData;
    InitializeAtmosphericLitInputData(input, surfaceData.normalTS, inputData);

    #if defined(_DBUFFER_MRT1) || defined(_DBUFFER_MRT2) || defined(_DBUFFER_MRT3)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
    #endif

    half4 finalColor = UniversalFragmentPBR(inputData, surfaceData);

    finalColor.rgb = MixFog(finalColor.rgb, inputData.fogCoord);

    return finalColor;
}

// ---------------------------------------------------------------------------
// Common Vertex Shader — fills Varyings, shared by all Forward Lit passes
// ---------------------------------------------------------------------------
AtmosphericLitVaryings NormalExtrusionPassVertex(AtmosphericLitAttributes input)
{
    AtmosphericLitVaryings output = (AtmosphericLitVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // Extrude along world-space normal by world-space distance
    // (avoids unit mismatch when object has non-unit scale)
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
    positionWS += normalWS * (_AtmosphereRadius - _PlanetRadius);
    float3 positionOS = TransformWorldToObject(positionWS);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(positionOS);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    output.normalWS = normalInput.normalWS;

    #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    real sign = input.tangentOS.w * GetOddNegativeScale();
    output.tangentWS = float4(normalInput.tangentWS.xyz, sign);
    #endif

    half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
    half fogFactor = 0;
    #if !defined(_FOG_FRAGMENT)
    fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
    #endif

    #ifdef _ADDITIONAL_LIGHTS_VERTEX
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
    #else
    output.fogFactor = fogFactor;
    #endif

    OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
    #ifdef DYNAMICLIGHTMAP_ON
    output.dynamicLightmapUV = input.dynamicLightmapUV.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
    #endif
    OUTPUT_SH(output.normalWS.xyz, output.vertexSH);

    output.positionWS = vertexInput.positionWS;

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
    #endif

    output.positionCS = vertexInput.positionCS;
    output.centerWS = mul(unity_ObjectToWorld, half4(0, 0, 0, 1));
    return output;
}

half4 NormalExtrusionPassFragment(AtmosphericLitVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    SurfaceData surfaceData;
    InitializeAtmosphericLitSurfaceData(input, surfaceData);

    InputData inputData;
    InitializeAtmosphericLitInputData(input, surfaceData.normalTS, inputData);

    #if defined(_DBUFFER_MRT1) || defined(_DBUFFER_MRT2) || defined(_DBUFFER_MRT3)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
    #endif

    float3 center = input.centerWS;
    // View direction from camera through this pixel
    float3 viewDirWS = normalize(input.positionWS - _WorldSpaceCameraPos);

    // Trace from camera, not from the shell vertex
    float tEntry, tExit;
    if (!AtmosphericRayIntersect(_WorldSpaceCameraPos, viewDirWS, center,
                                 _AtmosphereRadius, _PlanetRadius, tEntry, tExit))
    {
        // View ray misses the atmosphere entirely
        return half4(0, 0, 0, 0);
    }

    // Ray origin at atmosphere entry point, length through atmosphere
    float3 rayOrigin = _WorldSpaceCameraPos + viewDirWS * tEntry;
    float rayLength = tExit - tEntry;

    float3 sunDirection = normalize(_MainLightPosition.xyz);
    float sunIntensity = _SunIntensity;

    float cosTheta = dot(viewDirWS, sunDirection);
    float scaleHeightM = _MieScaleHeight;

    // ── Numerical Integration along the View Ray ─────────────────────────
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

        // ③ CP transmittance — ray march from P towards sun
        // (LUT version below, kept for reference)
        float cosSunZenith = dot(normalize(P - center), sunDirection);
        half3 T_CP = SampleTransmittanceLUT(height, cosSunZenith,
                                            _AtmosphereRadius, _PlanetRadius,
                                            kRayleighScattering, kMieScattering);
        // half3 T_CP = EvaluateTransmittance(
        //     P, sunDirection,
        //     (int)_LightSamples,
        //     center, _PlanetRadius, _AtmosphereRadius,
        //     _ScaleHeight, scaleHeightM,
        //     kRayleighScattering, kMieScattering);

        // ④ Single scattering: I_sun · J · T(PA) · T(CP) · ds
        scatter += sunIntensity * J * T_PA * T_CP * ds;

        time += ds;
    }

    return half4(scatter, 1.0);
}

#endif // ATMOSPHERE_LIT_INPUT_INCLUDED
