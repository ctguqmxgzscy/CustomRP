#ifndef UNIVERSAL_CUSTOM_BRDF_INCLUDED
#define UNIVERSAL_CUSTOM_BRDF_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

float DistributionGGX(float NdotH, float roughness)
{
    float a2 = roughness * roughness;
    float NdotH2 = NdotH * NdotH;

    float nom = a2;
    float denom = NdotH2 * (a2 - 1) + 1;
    denom = denom * denom * PI;
    return nom / denom;
}

//这里的k是α的重映射(Remapping)，取决于我们要用的是针对直接光照还是针对IBL光照的几何函数
/// @param k
/// Direct_K = (roughness + 1) ^ 2 / 8
/// IBL_K = roughness ^ 2 / 2 
float GeometrySchlickGGX(float dot, float k)
{
    float nom = dot;
    float denom = lerp(dot, 1, k);
    return nom / denom;
}

float GeometrySmith(float NdotL, float NdotV, float roughness)
{
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;
    float ggx2 = GeometrySchlickGGX(NdotV, k);
    float ggx1 = GeometrySchlickGGX(NdotL, k);

    return ggx1 * ggx2;
}

float3 FresnelSchlick(float HdotV, float3 F0)
{
    return lerp(pow(clamp(1.0 - HdotV, 0.0, 1.0), 5.0), 1, F0);
}

float3 FresnelSchlickRoughness(float HdotV, float3 F0, float roughness)
{
    float3 OneMinusRoughness = 1.0 - roughness;
    return F0 + (max(OneMinusRoughness, F0) - F0) * pow(clamp(1.0 - HdotV, 0.0, 1.0), 5.0);
}
#endif
