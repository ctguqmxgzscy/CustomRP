#ifndef CLOUD_LIGHTING_INCLUDED
#define CLOUD_LIGHTING_INCLUDED

#include "CloudCommon.hlsl"
#include "Assets/Shaders/ShaderLibrary/ScatteringUtils.hlsl"

#define NUM_MULTI_SCATTERING_OCTAVES 3

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

// 定向散射概率（Nubis SIG2017）：主前向 lobe + 银边 lobe 取 max
// 两次 HG 做的光轴偏移——不物理，但效果较好且美术可控：
// 主 lobe（eccentricity≈0.8）：整体前向散射
// 银边 lobe（g = 0.99 - spread，极尖前向）：视线朝太阳（cosTheta→1）时
// 产生极窄高光 → 太阳周围/顺光云边缘的银色亮边
float GetDirectScatterProbability(float eccentricity, float cosTheta)
{
    return max(HenyeyGreenstein(cosTheta, eccentricity),
               _SilverIntensity * HenyeyGreenstein(cosTheta, 0.99 - _SilverSpread));
}

// GPU Pro 7
float BeerLambert(float density, float precipitation)
{
    float d = -density * precipitation;
    return max(exp(d), exp(d * 0.5) * 0.7);
}

// GPU Pro 7
float PowderEffect(float density, float cosAngle)
{
    float powder = 1.0 - exp(-density * 2.0);
    return lerp(1.0f, powder, saturate((-cosAngle * 0.5f) + 0.5f));
}

// 云内单散射源 J = σs · γ(θ) · ρ × S × exp(-dl)（散射核 × phase[0] × 单散射透射）
// 大气侧 J 含 Rayleigh + Mie 双项；云是 Mie 主导（水滴 ≈ 波长量级，无色散）→ 单标量 σs。
// 方式 B 拆分（对齐大气 scatter += I × (J × T_CP + ms) × T_PA × ds）：
//   J  = 单散射项 = 散射核 × phase[0]（含银边）× exp(-dl) —— 即 Hillaire octave 0
//   ms = 多次散射补益项（compute 内，octave 从 1 起，复用 out kernel）
// 注意：octave 0 必须住在 J 里，ms 不能从 o=0 起算，否则单散射被加两遍。
// density     — 当前采样点密度
// scatterCoef — 散射系数 σs
// phase       — 每 octave 定向散射（phase[0] 含银边 lobe，高阶仅主 lobe）
// dl          — 点到光源光学深度 τ_CP（云段）
// stepSize    — light march 步长（米）
// heightFraction — 采样点云层内归一化高度 [0,1]
// kernel      — (out) 散射核 σs·ρ·S（不含相位/透射），J 与 ms 共享
float EvaluateCloudInScattering(float density, float scatterCoef, float3 phase,
                                float dl, float stepSize, float heightFraction, out float kernel)
{
    // 内散射概率（深度相关 + 垂直分布）
    // 注意：Remap 在 heightFraction 越界时输出可为负，pow(负, 非整数) = NaN（云底附近触发），必须夹到 ≥0
    float depthProb = lerp(
        saturate(0.05 + pow(stepSize, max(Remap(heightFraction, 0.3, 0.85, 0.5, 2.0), 0.0))),
        1.0, saturate(dl / max(stepSize, 1e-4)));
    float verticalProb = pow(max(Remap(heightFraction, 0.07, 0.14, 0.1, 1.0), 0.0), 0.8);
    float inScatterProb = depthProb * verticalProb;

    kernel = density * scatterCoef * inScatterProb; // 散射核（σs·ρ·S），J 与 ms 共享
    return kernel * phase[0] * exp(-dl);
}

// 纯单次散射（无多重散射）：
// J = σs · ρ · γ(θ) · exp(-dl)（Beer-Lambert 单衰减）
float3 EvaluateCloudInScattering(float density, float scatterCoef, float phase, float dl)
{
    return density * phase * scatterCoef * exp(-dl);
}

// ── 方式 B 拆分（严格对齐大气结构 scatter += I × (J × T_CP + ms) × T_PA × ds）──
// J  = 单散射项（CloudLighting.hlsl）：散射核 × phase[0] × exp(-dl) —— Hillaire octave 0
// ms = 多次散射补益项（CloudRayMarch.compute）：Σ_{o≥1} kernel × exp(-dl·att^o)·phase[o]·con^o
// CP = 点到光源（Point → Light）：dl 是 P 沿光源方向的光学深度

// 单散射透射：T_CP = exp(-dl)（Beer-Lambert，P → 光源）
float EvaluateSingleScatterTransmittance(float dl)
{
    return exp(-dl);
}

// ── 大气段透射（T_CP 的大气部分：点到光源）────────────────────────────
// 查 _OpticalDepthLUT（由大气管线生成），参数与大气预设共享：
// _PlanetRadius / _AtmosphereRadius 由 C# 从 AtmosphereSettings 传入，两边单位一致（米）。
// 用法：T_CP_total = EvaluateAtmosphereTransmittance(P, sphereCenter, lightDir) × exp(-τ_CP)
float3 EvaluateAtmosphereTransmittance(float3 posWS, float3 sphereCenter, float3 lightDir)
{
    float height = length(posWS - sphereCenter) - _PlanetRadius; // 海拔（米），与 LUT 同单位
    float cosSunZenith = dot(normalize(posWS - sphereCenter), lightDir);
    return SampleTransmittanceLUT(height, cosSunZenith, _AtmosphereRadius, _PlanetRadius,
                                  kRayleighScattering, kMieScattering);
}

// 双 HG 相位（参考 calculateLightEnergy）：
//   max(HG(cos, g_fwd), HG(cos, -g_bwd)) * 0.07 + 0.8（0.8 为散射底值）
// 参考归一化是 /4·π = 标准 /(4π) 的 π²≈9.87 倍 → 系数折合：0.07×π² ≈ 0.6909
float GetDualHGPhase(float cosAngle)
{
    return max(HenyeyGreenstein(cosAngle, _MieG), HenyeyGreenstein(cosAngle, -_MieGBackward)) * 0.6909 + 0.8;
}

float3 EvaluateLightEnergy(float density, RayMarchInfo info, float cosAngle, float powderDensity)
{
    // Beer 用独立 _Density（消光强度），不再复用 cloudDensityScale——
    // density 已含 cloudDensityScale×0.1（云量），两个 scale 角色不同，区分开
    return 2 * BeerLambert(density, _Density) * PowderEffect(powderDensity, cosAngle) *
        GetDualHGPhase(cosAngle);
}

float SampleCloudDensityAlongCone(float3 pos, float3 lightDir, RayMarchInfo info, int mipLevel, float cosAngle,
                                  float density_along_view_cone)
{
    // 计算椎体的偏移，在-(1,1,1)和+(1,1,1)之间使用了六个噪声结果作为Kernel
    static float3 noise_kernel[6] =
    {
        float3(0.38051305, 0.92453449, -0.02111345),
        float3(-0.50625799, -0.03590792, -0.86163418),
        float3(-0.32509218, -0.94557439, 0.01428793),
        float3(0.09026238, -0.27376545, 0.95755165),
        float3(0.28128598, 0.42443639, -0.86065785),
        float3(-0.16852403, 0.14748697, 0.97460106)
    };

    float heightFraction;
    float densityAlongCone = 0.0;
    const int steps = 6; // light cone step count
    float3 weatherData;

    // 每次步进做6份锥形光照采样；
    // 如果沿着视线行进的累积密度已经超过其有效光贡献阈值【Horizon使用0.3】，则切换到低频采样模式以进一步优化
    for (int i = 0; i < steps; i++)
    {
        pos += lightDir * _LightStepLength;
        float3 jitter = noise_kernel[i] * _LightStepLength * _LightConeRadius * (float(i + 1));
        float3 p = pos + jitter;

        heightFraction =
            GetHeightFractionForPoint(info.planetCenter, info.planetRadius, p, info.cloudHeightRangeMinMax);
        weatherData = SampleWeatherData(p, info);

        // weatherDensity 权重（参考）：weatherData.b + 1.0——我们天气图 B 通道 = 云型
        //（0=层云 ~ 1=积云），乘上后积云区光程密度更重，层云区保持 1.0
        if (density_along_view_cone < 0.3)
        {
            densityAlongCone += SampleCloudDensity(p, info, heightFraction, weatherData, mipLevel, false) * (weatherData
                .b + 1.0);
        }
        else
        {
            densityAlongCone += SampleCloudDensity(p, info, heightFraction, weatherData, mipLevel, true) * (weatherData.
                b
                + 1.0);
        }
    }

    // 逐步进采样点计算光照
    return EvaluateLightEnergy(densityAlongCone, info, cosAngle, density_along_view_cone) * _SunColor * _SunIntensity;
}


// 自适应步长 march（参考 Hidden/Clouds 的 raymarch）：
// rayOrigin  = 云层入点（findRayStartPos 算出的射线与云层交点），不是相机位置
// rayDir     = 视线方向 × 基础步长 stepSize（调用方算好传入：stepSize = 入出点距离 / 步数）
// stepLength = 步长倍数（1.0 细步 / 3.0 大步，函数内部按零密度计数切换），不是米
// depth      = 场景深度（世界米，由深度纹理换算，以相机为基准），march 超过即停止（云被几何遮挡）
// 位置推进 = rayDir × stepLength = stepSize × 倍数；i 以基础步为单位计数，总覆盖恒 = t
// 调用方初始 stepLength 传 3.0（参考 BIG_STEP，先粗后细）
float4 RayMarch(float3 rayOrigin, float3 rayDir, float3 lightDir, float stepLength, float depth,
                RayMarchInfo info, float cosAngle)
{
    float3 posWS = rayOrigin;
    float4 result = 0.0;
    float lod = 0.0;

    // Raymarching
    // 参考SIG 2017的代码
    int sampleCount = info.stepCount;

    float zero_density_sample_count = 0;

    // 循环计数用 float + 步长单位递增（参考 raymarch）：回退 i -= stepLength-1 后
    // 递增 i += stepLength 净 +1，与位置推进同步；若用 int i++，回退会把 i 拖成负值，
    // 薄云场景 alpha 达不到 0.99 → i 永远 < sampleCount → 死循环（GPU 卡死风险）
    for (float i = 0.0; i < (float)sampleCount; i += stepLength)
    {
        // 深度检查以相机位置为基准（参考 _CameraWS）——rayOrigin 是入点，用它做基准
        // 会把入点前那段距离也算进去 → 遮挡剔除变弱（云穿过几何）
        if (distance(_WorldSpaceCameraPos, posWS) >= depth || result.a >= 0.99)
        {
            // 超过场景深度（云被几何遮挡）或云不透明度已接近 1 → 停止 march
            break;
        }

        float heightFraction = GetHeightFractionForPoint(info.planetCenter, info.planetRadius, posWS,
                                                         info.cloudHeightRangeMinMax);
        float3 weatherData = SampleWeatherData(posWS, info);

        if (weatherData.r <= 0.1)
        // if value is low, then continue marching, at some specific weather textures makes it a bit faster.
        {
            posWS += rayDir * stepLength;
            zero_density_sample_count += 1.0;
            stepLength = zero_density_sample_count > 10 ? 3.0 : 1.0;
            continue;
        }

        float cloudDensity =
            saturate(SampleCloudDensity(posWS, info, heightFraction, weatherData, lod, true));

        if (cloudDensity > 0.0)
        {
            zero_density_sample_count = 0.0;
            if (stepLength > 1.0)
            {
                i -= stepLength - 1.0;
                posWS -= rayDir * (stepLength - 1.0);
                heightFraction = GetHeightFractionForPoint(info.planetCenter, info.planetRadius, posWS,
                                                           info.cloudHeightRangeMinMax);
                weatherData = SampleWeatherData(posWS, info);
                cloudDensity =
                    saturate(SampleCloudDensity(posWS, info, heightFraction, weatherData, lod, true));
            }

            float3 directLighting = SampleCloudDensityAlongCone(posWS, lightDir, info, lod, cosAngle, cloudDensity);
            // 环境光（参考 raymarch）：按高度在云底/云顶色间插值
            float3 ambientLighting = lerp(_CloudBaseColor, _CloudTopColor, heightFraction) * _AmbientLightFactor;
            result.rgb += cloudDensity;
            result.a += cloudDensity;
        }
        else
        {
            zero_density_sample_count += 1.0;
        }
        stepLength = zero_density_sample_count > 10 ? 3.0 : 1.0;
        posWS += rayDir * stepLength; // 每轮末尾统一推进（参考 raymarch 最后一行）——否则有云像素永远停在起点
    }
    return result;
}


#endif // CLOUD_LIGHTING_INCLUDED
