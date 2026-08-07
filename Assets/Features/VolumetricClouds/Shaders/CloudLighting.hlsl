#ifndef CLOUD_LIGHTING_INCLUDED
#define CLOUD_LIGHTING_INCLUDED

#include "CloudCommon.hlsl"
#include "Assets/Shaders/ShaderLibrary/ScatteringUtils.hlsl"

// ── 双 HG 相位（Nubis 2017）────────────────────────────────────────────
// 单 HG 只有一个 g，无法同时表现 Mie 的强前向峰（云朝太阳的银边/辉光）
// 与后向散射（太阳背后云边的亮圈）。Nubis 2017 用两个不同 g 的 HG 组合：
//   前向 lobe：g₁ ≈ 0.8 —— 太阳方向强峰
//   后向 lobe：g₂ ≈ -0.3 —— 背向峰
// 混合方式：Max（每个角度取两叶较大者，双峰都保留）或 Lerp（连续过渡）。
// 注意：Max 后相位函数不再归一化（两峰区域都 > 1/(4π)），强度权重为艺术调参。
float HenyeyGreenstein(float cosTheta, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * cosTheta, 1.5));
}

// 双 HG — Max 混合：前向 g₁ + 后向 g₂，各自带强度权重
float HGScatterMax(float angle, float g_1, float intensity_1, float g_2, float intensity_2)
{
    return max(intensity_1 * HenyeyGreenstein(angle, g_1), intensity_2 * HenyeyGreenstein(angle, g_2));
}

// 双 HG — Lerp 混合：weight 控制后向分量占比
float HGScatterLerp(float angle, float g_1, float g_2, float weight)
{
    return lerp(HenyeyGreenstein(angle, g_1), HenyeyGreenstein(angle, g_2), weight);
}

// ── Beer-Lambert 与多次散射近似（Nubis）────────────────────────────────

// Beer-Lambert 透射率：T = exp(-σt · d)
float Beer(float density, float absorptivity = 1)
{
    return exp(-density * absorptivity);
}

// 粉糖效应（多次散射近似）：深入云内时多次散射让光从侧面漏出 → 边缘比核心亮
float BeerPowder(float density, float absorptivity = 1)
{
    return 2.0 * exp(-density * absorptivity) * (1.0 - exp(-2.0 * density));
}

// 光能量（Nubis Dual Beer 简化版）：黑暗阈值防止完全漆黑
float GetLightEnergy(float density, float absorptivity, float darknessThreshold)
{
    float energy = BeerPowder(density, absorptivity);
    return darknessThreshold + (1.0 - darknessThreshold) * energy;
}

float3 EvaluateCloudInScattering(float density, float scatterCoef, float phase)
{
    return density * phase * scatterCoef;
}

// ── 大气段透射（T_CP 的大气部分）───────────────────────────────────────
// 查 _OpticalDepthLUT（由大气管线生成），参数与大气预设共享：
// _PlanetRadius / _AtmosphereRadius 由 C# 从 AtmosphereSettings 传入，两边单位一致（米）。
// 用法：T_CP_total = EvaluateAtmosphereTransmittance(P, sphereCenter, lightDir) × exp(-τ_CP)
float3 EvaluateAtmosphereTransmittance(float3 posWS, float3 sphereCenter, float3 lightDir)
{
    float height = length(posWS - sphereCenter) - _PlanetRadius;   // 海拔（米），与 LUT 同单位
    float cosSunZenith = dot(normalize(posWS - sphereCenter), lightDir);
    return SampleTransmittanceLUT(height, cosSunZenith, _AtmosphereRadius, _PlanetRadius,
                                  kRayleighScattering, kMieScattering);
}

#endif // CLOUD_LIGHTING_INCLUDED
