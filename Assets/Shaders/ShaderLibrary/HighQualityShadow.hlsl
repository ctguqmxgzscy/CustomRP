#ifndef HIGH_QUALITY_SHADOW_INCLUDED
#define HIGH_QUALITY_SHADOW_INCLUDED

// =============================================================================
//  Per-object High Quality Shadow —— 接收端采样库
//
//  阴影图由 Assets/Features/PerObjectShadow/HighQualityShadowFeature.cs 每帧渲染，
//  它是一张贴合角色包围盒（光空间 x/y 收紧）的高分辨率主光阴影图。
//
//  全局量（由 Feature 写入）：
//    _HighQualityShadowmapTexture  RenderTextureFormat.Shadowmap，支持硬件比较采样
//    _HighQualityShadowMatrix      world → [0,1]^3，含纹理 scale/bias，
//                                  与 URP 的 worldToShadow 同一约定（ShadowUtils.GetShadowTransform）
//    _HighQualityShadowParams      x = 启用(0/1)
//                                  y = 边缘淡出宽度(UV)
//                                  z = 沿光线方向的深度偏移(世界单位)
//                                  w = 未使用
//    _HighQualityShadowTexelSize   xy = 1/分辨率，zw = 分辨率
//
//  约定：本文件必须在 URP 的 RealtimeLights.hlsl（即 Light 结构体与主光阴影定义）
//        之后被 include。CustomLighting.hlsl 已满足该条件。
// =============================================================================

TEXTURE2D_SHADOW(_HighQualityShadowmapTexture);

// sampler_LinearClampCompare 由 URP 的 Shadows.hlsl 声明。若某个 shader 只 include 了
// 本文件而没走到 Shadows.hlsl，就在这里补上，避免重定义。
#if !defined(UNIVERSAL_SHADOWS_INCLUDED)
    SAMPLER_CMP(sampler_LinearClampCompare);
#endif

float4x4 _HighQualityShadowMatrix;
float4 _HighQualityShadowParams;
float4 _HighQualityShadowTexelSize;

// 返回 (阴影衰减, 权重)；权重为 0 表示该像素不在 HQ 体积内，调用方应保留原生阴影。
half2 SampleHighQualityShadow(float3 positionWS, half3 lightDirectionWS)
{
    half2 result = half2(1.0h, 0.0h);

    if (_HighQualityShadowParams.x <= 0.0h)
        return result;

    // 接收面沿光线方向（lightDirectionWS 指向光源）偏移，抑制自阴影痤疮
    float3 biasedWS = positionWS + lightDirectionWS * _HighQualityShadowParams.z;
    float4 coord = mul(_HighQualityShadowMatrix, float4(biasedWS, 1.0));

    // z 越界 → 接收面不在阴影体积内
    half weight = half(_HighQualityShadowParams.x) * step(0.0, coord.z) * step(coord.z, 1.0);

    // 体积边界淡出，避免 HQ 与原生级联之间出现硬接缝
    half2 edge = min(coord.xy, 1.0h - coord.xy);
    weight *= saturate(min(edge.x, edge.y) / max(_HighQualityShadowParams.y, 1e-5h));

    if (weight <= 0.0h)
        return result;

    // 3x3 PCF：中心 4、边 2、角 1，共 16
    float2 texel = _HighQualityShadowTexelSize.xy;
    half atten = 0.0h;
    atten += 4.0h * SAMPLE_TEXTURE2D_SHADOW(_HighQualityShadowmapTexture, sampler_LinearClampCompare, float3(coord.xy, coord.z));
    atten += 2.0h * SAMPLE_TEXTURE2D_SHADOW(_HighQualityShadowmapTexture, sampler_LinearClampCompare, float3(coord.xy + float2(texel.x, 0.0), coord.z));
    atten += 2.0h * SAMPLE_TEXTURE2D_SHADOW(_HighQualityShadowmapTexture, sampler_LinearClampCompare, float3(coord.xy + float2(-texel.x, 0.0), coord.z));
    atten += 2.0h * SAMPLE_TEXTURE2D_SHADOW(_HighQualityShadowmapTexture, sampler_LinearClampCompare, float3(coord.xy + float2(0.0, texel.y), coord.z));
    atten += 2.0h * SAMPLE_TEXTURE2D_SHADOW(_HighQualityShadowmapTexture, sampler_LinearClampCompare, float3(coord.xy + float2(0.0, -texel.y), coord.z));
    atten += 1.0h * SAMPLE_TEXTURE2D_SHADOW(_HighQualityShadowmapTexture, sampler_LinearClampCompare, float3(coord.xy + float2(texel.x, texel.y), coord.z));
    atten += 1.0h * SAMPLE_TEXTURE2D_SHADOW(_HighQualityShadowmapTexture, sampler_LinearClampCompare, float3(coord.xy + float2(texel.x, -texel.y), coord.z));
    atten += 1.0h * SAMPLE_TEXTURE2D_SHADOW(_HighQualityShadowmapTexture, sampler_LinearClampCompare, float3(coord.xy + float2(-texel.x, texel.y), coord.z));
    atten += 1.0h * SAMPLE_TEXTURE2D_SHADOW(_HighQualityShadowmapTexture, sampler_LinearClampCompare, float3(coord.xy + float2(-texel.x, -texel.y), coord.z));

    result = half2(atten * (1.0h / 16.0h), weight);
    return result;
}

// 体积内用 HQ 阴影替换原生级联阴影；体积外（weight = 0）保持原生不动。
void ApplyHighQualityShadow(inout Light light, float3 positionWS)
{
    half2 hq = SampleHighQualityShadow(positionWS, light.direction);
    light.shadowAttenuation = lerp(light.shadowAttenuation, hq.x, hq.y);
}

#endif // HIGH_QUALITY_SHADOW_INCLUDED
