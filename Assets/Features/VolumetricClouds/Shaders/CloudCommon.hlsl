#ifndef CLOUD_COMMON_INCLUDED
#define CLOUD_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Assets/Shaders/ShaderLibrary/MathHelper.hlsl"

float _WeatherMapScale; // 平铺缩放：UV = posWS.xz × scale
float _CloudDensityAdjust; // 覆盖率/吸收率/云型调整：0~0.5~1 => 0~对应通道~1（0.5 保持原值）
float _Coverage; // 全局云量（参考 _Coverage）：saturate(r - coverage)，0=原图，越大云越少
float _AbsorptionStrength; // 吸收强度：G=1（强降雨）时消光放大倍数

float3 _SunColor; // 太阳颜色
float _SunIntensity; // 太阳强度

float3 _CloudBaseColor; // 云底环境色（参考 _CloudBaseColor）
float3 _CloudTopColor; // 云顶环境色（参考 _CloudTopColor）
float _AmbientLightFactor; // 环境光强度（参考 _AmbientLightFactor）

float _PlanetRadius; // 行星球半径（与大气预设一致）
float _AtmosphereRadius; // 大气外半径（预设 planetRadius + atmosphereHeight），查大气 LUT 用
float _CloudHeightMin; // 云层底部高度（相对地表）
float _CloudHeightMax; // 云层顶部高度

float2 _StratusRange;
float _StratusFeather;
float2 _CumulusRange;
float _CumulusFeather;

float _CloudDensityScale; // 密度整体缩放

float _ShapeNoiseScale; // 形状噪声平铺缩放（TODO: Perlin-Worley 3D 纹理）
float _DetailNoiseScale; // 细节噪声平铺缩放（TODO: 高频细节噪声）

float _ShapeStepCount; // 主步进数（形状采样）
float _ExtinctionCoeff; // 消光系数 σt（Beer-Lambert）
float _ScatterCoeff; // 散射系数 σs
float _Density; // Beer 消光密度（参考 _Density）：与 cloudDensityScale（云量密度）独立——这个是消光强度
float _MieG; // 主 HG 前向散射各向异性（Nubis 定向散射主 lobe）
float _MieGBackward; // 后向 HG 各向异性（参考 _HenyeyGreensteinGBackward，正值，使用时取负）
float _SilverIntensity; // 银边 lobe 强度（Nubis：u_SilverIntensity）
float _SilverSpread; // 银边 lobe 宽度（g = 0.99 - spread，越小银边越窄越亮）

float _MSAttenuation; // Hillaire MS：消光衰减率 per octave（越小高阶光路穿透越深，默认 0.5）
float _MSContribution; // Hillaire MS：散射能量权重 per octave（越大云内部越白，默认 0.5）
float _MSEccentricity; // Hillaire MS：相位偏心率衰减 per octave（越小高阶越各向同性，默认 0.5）

float _LightStepLength; // 光步长度（m，0-200）
float _LightConeRadius; // 光锥半径（0-1）

float3 _WindDirection; // 风向（世界坐标，归一化）
float _WindSpeed; // 风速 m/s

float _FrameCount;

// 预烘焙 3D 噪声（CloudNoiseBaker 生成）：
// _ShapeNoiseTex: R = Perlin-Worley 基形, GBA = 三频率 Worley
// _DetailNoiseTex: RGB = 高频 Worley FBM
TEXTURE3D(_ShapeNoiseTex);
SAMPLER(sampler_ShapeNoiseTex);
TEXTURE3D(_DetailNoiseTex);
SAMPLER(sampler_DetailNoiseTex);
float _BaseShapeDetailEffect; // 基形 Worley FBM 侵蚀强度 (0~1)
float _DetailEffect; // 细节侵蚀强度 (0~1)

float u_AnvilBias; // 砧状云顶塑形强度（参考 u_AnvilBias，0=无效果，1=云顶覆盖率指数 1→0.5）

// 天气图（世界 XZ 平铺采样）：R=云覆盖率, G=吸收率(降雨), B=云型(0=层云 ~ 1=积云)
TEXTURE2D(_WeatherMapTex);
SAMPLER(sampler_WeatherMapTex);

// 大气天空 LUT（与天空盒共享全局 _SkyViewLut，AtmosphereSkyboxLutFeature 每帧生成）：
// 云的环境光与天空盒同源，黄昏/太阳位置自动一致
TEXTURE2D(_SkyViewLut);
SAMPLER(sampler_SkyViewLut);

// 视线方向 → SkyViewLut UV（与 AtmosphereSkyboxLut.shader 完全一致的 Hillaire 非线性纬度映射）
float2 CloudViewDirToUV(float3 v)
{
    float latitude = asin(v.y);
    float n = latitude / (PI * 0.5);
    return float2(atan2(v.z, v.x) / (2.0 * PI) + 0.5,
                  sign(n) * sqrt(abs(n)) * 0.5 + 0.5);
}

// 大气天空色（按世界方向采样，y=上）
float3 SampleSkyViewLut(float3 dir)
{
    return SAMPLE_TEXTURE2D_LOD(_SkyViewLut, sampler_SkyViewLut, CloudViewDirToUV(normalize(dir)), 0).rgb;
}

//采样完后云的信息
struct CloudInfo
{
    float density; //密度
    float absorptivity; //吸收率
};

struct RayMarchInfo
{
    float planetRadius;
    float3 planetCenter;

    float baseShapeTiling;
    float baseShapeEffect;
    float detailShapeTiling;
    float detailShapeEffect;
    float2 cloudHeightRangeMinMax;

    float windSpeed;
    float3 windDirection;

    float weatherTexTiling;
    float cloudDensityScale;
    float cloudDensityAdjust;

    float3 stratusInfo; //层云信息，层云最小高度(x)  层云最大高度(y)  层云边缘羽化强度(z)
    float3 cumulusInfo; //积云信息， 积云最小高度(x)  积云最大高度(y)  积云边缘羽化强度(z)

    int stepCount;
    float mieG;
};

RayMarchInfo GetRayMarchInfo()
{
    RayMarchInfo info;

    info.planetCenter = float3(0.0f, 0.0f, 0.0f);
    info.planetRadius = _PlanetRadius;
    info.baseShapeTiling = _ShapeNoiseScale;
    info.baseShapeEffect = _BaseShapeDetailEffect;
    info.detailShapeTiling = _DetailNoiseScale;
    info.detailShapeEffect = _DetailEffect;
    info.cloudHeightRangeMinMax.xy = float2(_CloudHeightMin, _CloudHeightMax);
    info.windSpeed = _WindSpeed;
    info.windDirection = _WindDirection;

    info.weatherTexTiling = _WeatherMapScale;
    info.cloudDensityScale = _CloudDensityScale;
    info.cloudDensityAdjust = _CloudDensityAdjust;

    // 云属高度范围（云层内归一化 0~1）：x=云底, y=云顶, z=边缘羽化
    info.stratusInfo = float3(_StratusRange.x, _StratusRange.y, _StratusFeather);
    info.cumulusInfo = float3(_CumulusRange.x, _CumulusRange.y, _CumulusFeather);

    info.stepCount = (int)_ShapeStepCount;
    info.mieG = _MieG;
    return info;
}

// 从 UV 重建视线方向（near 平面位置 - 射线起点）
// UV→clip 约定与 ComputeClipSpacePosition 一致（DX 上 clip y 翻转），
// 否则与深度重建方向矛盾 → 云位置/深度错乱
// 精度：分步重建（逆投影在相机空间算方向 + 逆视图纯旋转转世界方向）——
// 原 UNITY_MATRIX_I_VP 在世界坐标（6.3e6 量级，float32 ulp ~0.5m）重建近平面点
// 再减相机位置，大数相减：相机万米以上近平面偏移（~0.3m）被量化 → viewDir 失真
float3 GetViewDir(float2 uv, float3 rayOrigin)
{
    float nearDepth = UNITY_REVERSED_Z ? 1.0 : 0.0;
    float2 ndc = uv * 2.0 - 1.0;
    #if UNITY_UV_STARTS_AT_TOP
    ndc.y = -ndc.y;
    #endif
    float4 viewPos = mul(UNITY_MATRIX_I_P, float4(ndc, nearDepth, 1.0));
    float3 dirCS = viewPos.xyz / max(viewPos.w, 1e-6); // 相机空间方向（近平面小量，高精度）
    return normalize(mul((float3x3)UNITY_MATRIX_I_V, dirCS)); // 纯旋转转世界方向
}

//射线与球体相交, x 到球体最近的距离， y 穿过球体的距离
//原理是将射线方程(x = o + dl)带入球面方程求解(|x - c|^2 = r^2)
float2 RaySphereDst(float3 sphereCenter, float sphereRadius, float3 pos, float3 rayDir)
{
    float3 oc = pos - sphereCenter;
    float b = dot(rayDir, oc);
    float c = dot(oc, oc) - sphereRadius * sphereRadius;
    float t = b * b - c; //t > 0有两个交点, = 0 相切， < 0 不相交

    float delta = sqrt(max(t, 0));
    float dstToSphere = max(-b - delta, 0);
    float dstInSphere = max(-b + delta - dstToSphere, 0);
    return float2(dstToSphere, dstInSphere);
}

//射线与云层相交, x到云层的最近距离, y穿过云层的距离
//通过两个射线与球体相交进行计算
float2 TestRayCloudLayerDst(float3 sphereCenter, float earthRadius, float heightMin, float heightMax, float3 pos,
                            float3 rayDir, bool isShape = true)
{
    float2 cloudDstMin = RaySphereDst(sphereCenter, heightMin + earthRadius, pos, rayDir);
    float2 cloudDstMax = RaySphereDst(sphereCenter, heightMax + earthRadius, pos, rayDir);

    //射线到云层的最近距离
    float dstToCloudLayer = 0;
    //射线穿过云层的距离
    float dstInCloudLayer = 0;

    //形状步进时计算相交
    if (isShape)
    {
        //在地表上
        if (pos.y <= heightMin)
        {
            float3 startPos = pos + rayDir * cloudDstMin.y;
            //开始位置在地平线以上时，设置距离
            if (startPos.y >= 0)
            {
                dstToCloudLayer = cloudDstMin.y;
                dstInCloudLayer = cloudDstMax.y - cloudDstMin.y;
            }
            return float2(dstToCloudLayer, dstInCloudLayer);
        }

        //在云层内
        if (pos.y > heightMin && pos.y <= heightMax)
        {
            dstToCloudLayer = 0;
            dstInCloudLayer = cloudDstMin.y > 0 ? cloudDstMin.x : cloudDstMax.y;
            return float2(dstToCloudLayer, dstInCloudLayer);
        }

        //在云层外
        dstToCloudLayer = cloudDstMax.x;
        dstInCloudLayer = cloudDstMin.y > 0 ? cloudDstMin.x - dstToCloudLayer : cloudDstMax.y;
    }
    else //光照步进时，步进开始点一定在云层内
    {
        dstToCloudLayer = 0;
        dstInCloudLayer = cloudDstMin.y > 0 ? cloudDstMin.x : cloudDstMax.y;
    }

    return float2(dstToCloudLayer, dstInCloudLayer);
}


// 射线与云层球壳相交，返回 (到云层距离, 云层内厚度)；y <= 0 表示无云
// 行星中心跟随相机水平位置：sphereCenter = (camX, -r, camZ)，世界 y = 海拔
// （精度方案：相交在球坐标算 t（r 量级），密度采样在世界坐标（海拔小量））
// isShape = true : 形状步进，pos 任意（地表/云内/云上）
// isShape = false: 光照步进，调用方保证 pos 在云内（取云内分支逻辑）
float2 RayCloudLayerDst(float3 sphereCenter, float earthRadius, float heightMin, float heightMax,
                        float3 pos, float3 rayDir, bool isShape)
{
    float2 cloudDstMin = RaySphereIntersection(pos, rayDir, sphereCenter, heightMin + earthRadius);
    float2 cloudDstMax = RaySphereIntersection(pos, rayDir, sphereCenter, heightMax + earthRadius);

    // 云顶球无正向交点（哨兵 -1 或交点全在身后）→ 无云
    if (cloudDstMax.y < 0)
        return float2(0, 0);

    float dstToCloudLayer = 0;
    float dstInCloudLayer = 0;

    if (isShape)
    {
        if (pos.y <= heightMin)
        {
            // 地表（相机在云底球内）：穿出云底 = 进云
            // 穿出点在水平面以下（射线朝下）→ 被地球遮挡，无云
            float3 startPos = pos + rayDir * cloudDstMin.y;
            if (startPos.y >= 0)
            {
                dstToCloudLayer = cloudDstMin.y;
                dstInCloudLayer = cloudDstMax.y - cloudDstMin.y;
            }
            return float2(dstToCloudLayer, dstInCloudLayer);
        }
        if (pos.y <= heightMax)
        {
            // 云内：从 0 开始，朝下穿云底 / 朝上穿云顶
            dstToCloudLayer = 0;
            dstInCloudLayer = cloudDstMin.y > 0 ? cloudDstMin.x : cloudDstMax.y;
            return float2(dstToCloudLayer, dstInCloudLayer);
        }
        // 云上方：穿入云顶 = 进云
        dstToCloudLayer = cloudDstMax.x;
        // 朝下穿云层 → 穿入云底出云；侧面掠过（穿外球不穿内球）→ 穿出云顶
        dstInCloudLayer = cloudDstMin.y > 0 ? cloudDstMin.x - dstToCloudLayer : cloudDstMax.y - dstToCloudLayer;
    }
    else
    {
        // 光照步进：pos 在云内
        dstToCloudLayer = 0;
        dstInCloudLayer = cloudDstMin.y > 0 ? cloudDstMin.x : cloudDstMax.y;
    }

    return float2(dstToCloudLayer, dstInCloudLayer);
}

float Remap(float originalValue, float originalMin, float originalMax, float newMin, float newMax)
{
    return newMin + ((originalValue - originalMin) / (originalMax - originalMin)) * (newMax - newMin);
}

// 在三个值间进行插值, value1 -> value2 -> value3， offset用于中间值(value2)的偏移
float Interpolation3(float value1, float value2, float value3, float x, float offset = 0.5)
{
    offset = clamp(offset, 0.0001, 0.9999);
    return lerp(lerp(value1, value2, min(x, offset) / offset), value3, max(0, x - offset) / (1.0 - offset));
}

//获取高度比率
float GetHeightFractionForPoint(float3 sphereCenter, float planetRadius, float3 pos, float2 cloudMinMax)
{
    float height = length(pos - sphereCenter) - planetRadius;
    return (height - cloudMinMax.x) / (cloudMinMax.y - cloudMinMax.x);
}

// samples the gradient
float SampleGradient(float4 gradient, float height)
{
    return smoothstep(gradient.x, gradient.y, height) - smoothstep(gradient.z, gradient.w, height);
}

float GetDensityHeightGradientForPoint(float heightFraction, float3 weatherData)
{
    float cloudType = weatherData.b;

    const float4 CloudGradient1 = float4(0.0, 0.065, 0.203, 0.371); //stratus
    const float4 CloudGradient2 = float4(0.0, 0.156, 0.468, 0.674); //cumulus
    const float4 CloudGradient3 = float4(0.0, 0.188, 0.818, 1); //cumulonimbus

    float4 gradient = lerp(lerp(CloudGradient1, CloudGradient2, cloudType * 2.0), CloudGradient3,
                           saturate(cloudType - 0.5) * 2.0);

    return SampleGradient(gradient, heightFraction);
}

// samples weather texture
float3 SampleWeatherData(float3 pos, RayMarchInfo info)
{
    // ── 风：高度加权偏移（高处吹得更远）+ 沿风向时间偏移（_Time.y 由 URP 每帧设置）──
    float3 windDirection = info.windDirection;
    float3 wind = windDirection * _Time.y * info.windSpeed;
    float3 weatherData = SAMPLE_TEXTURE2D_LOD(_WeatherMapTex, sampler_WeatherMapTex,
                                              pos.xz * 0.000001 * info.weatherTexTiling + wind.xz * 0.01, 0).rgb;
    weatherData.r = saturate(weatherData.r - _Coverage);
    return weatherData;
}

float GetCloudTypeDensity(float heightFraction, float cloudMin, float cloudMax, float feather)
{
    // 除零防御：feather 或范围可被调成 0（Inspector），Remap 分母必须非零
    float f = max(feather, 1e-4);
    float range = max(cloudMax - cloudMin, 1e-4);
    return saturate(Remap(heightFraction, cloudMin, cloudMin + f * 0.5, 0, 1)) * saturate(
        Remap(heightFraction, cloudMax - f, cloudMin + range, 1, 0));
}

float SampleLowFrequencyNoise(float3 posWS, RayMarchInfo info, int mipLevel)
{
    float4 lowFreqNoise = SAMPLE_TEXTURE3D_LOD(_ShapeNoiseTex, sampler_ShapeNoiseTex,
                                               posWS * info.baseShapeTiling * 0.0001, mipLevel).rgba;
    float lowFBMNoise = dot(lowFreqNoise.gba, float3(0.625, 0.25, 0.125));
    float baseShape = Remap(lowFreqNoise.r, saturate((1.0 - lowFBMNoise) * info.baseShapeEffect),
                            1.0, 0.0, 1.0);
    return baseShape;
}

float SampleHighFrequencyNoise(float3 posWS, RayMarchInfo info)
{
    float3 highFreqNoise =
        SAMPLE_TEXTURE3D_LOD(_DetailNoiseTex, sampler_DetailNoiseTex, posWS * info.detailShapeTiling * 0.0001, 0).rgb;
    float highFBMNoise = dot(highFreqNoise.rgb, float3(0.625, 0.25, 0.125));
    //根据高度从纤细到波纹的形状进行变化
    return highFBMNoise;
}

// samples cloud density
float SampleCloudDensity(float3 p, RayMarchInfo info, float heightFraction, float3 weatherData, float lod,
                         bool sampleDetail)
{
    // cloud_top offset ，用于偏移高处受风力影响的程度
    float cloud_top_offset = 500.0;

    // 为采样位置增加高度梯度，风向影响;
    p += heightFraction * info.windDirection * cloud_top_offset;

    // 增加一些沿着风向的时间偏移
    p += (info.windDirection + float3(0.0, 0.1, 0.0)) * _Time.y * info.windSpeed;

    float baseCloud = SampleLowFrequencyNoise(p, info, lod);
    baseCloud *= GetDensityHeightGradientForPoint(heightFraction, weatherData); // multiply cloud by its type gradient

    float coverage = weatherData.r;
    float cloudCoverage = pow(coverage, Remap(heightFraction, 0.7, 0.8, 1.0, lerp(1.0, 0.5, u_AnvilBias)));
    float cloudWithCoverage = saturate(Remap(baseCloud, cloudCoverage, 1.0, 0.0, 1.0));
    cloudWithCoverage *= cloudCoverage; // multiply by cloud coverage to smooth them out, GPU Pro 7
    float finalCloud = cloudWithCoverage;

    if (finalCloud > 0.0 && sampleDetail) // If cloud sample > 0 then erode it with detail noise
    {
        // 采样高频噪声并构建FBM
        float high_freq_fBm = SampleHighFrequencyNoise(p, info);

        // 获取height_fraction用于在高度上混合噪声
        float newHeightFraction = GetHeightFractionForPoint(info.planetCenter, info.planetRadius, p,
                                                            info.cloudHeightRangeMinMax);

        // 依据高度从纤细的形状过渡到波浪形状
        float high_freq_noise_modifier = lerp(high_freq_fBm, 1.0 - high_freq_fBm, saturate(newHeightFraction * 10.0));

        // 根据SIG 2017中的做法，使用Remap将扭曲的高频worley噪声用于侵蚀基础云的形状以塑造细节
        finalCloud = Remap(cloudWithCoverage, saturate(high_freq_noise_modifier * 0.5), 1.0, 0.0, 1.0);
    }

    return max(finalCloud * info.cloudDensityScale * 0.1, 0.0);
}

// 云密度采样（参考 Nubis/SIG2017 思路）：
// 风偏移 → 云型高度梯度 × 低频形状噪声 → 覆盖率侵蚀+塔状拉伸 → 高频细节侵蚀
// 返回 [0,1] 的密度；out absorption: 天气图 G 通道（降雨吸收率），供 march 循环缩放消光
// doCheaply = true：跳过高频细节噪声采样（云外判断/圆锥采样用，Nathan Vos 优化）
CloudInfo SampleCloudDensity(float3 posWS, RayMarchInfo info, bool doCheaply = false)
{
    CloudInfo cloudInfo;

    float heightFraction = GetHeightFractionForPoint(info.planetCenter, info.planetRadius, posWS,
                                                     info.cloudHeightRangeMinMax);

    // ── 风：高度加权偏移（高处吹得更远）+ 沿风向时间偏移（_Time.y 由 URP 每帧设置）──
    float3 windDirection = info.windDirection;
    float3 wind = windDirection * _Time.y * info.windSpeed;
    float3 p = posWS + wind * 100;

    //采样天气纹理，默认1000km平铺， r 密度, g 吸收率, b 云类型(0~1 => 层云~积云)
    float2 weatherTexUV = posWS.xz * info.weatherTexTiling;
    float3 weatherData = SAMPLE_TEXTURE2D_LOD(_WeatherMapTex, sampler_WeatherMapTex,
                                              weatherTexUV * 0.000001 + wind.xz * 0.01, 0);
    weatherData.r = Interpolation3(0, weatherData.r, 1, info.cloudDensityAdjust);
    weatherData.b = Interpolation3(0, weatherData.b, 1, info.cloudDensityAdjust);
    // 全局云量（参考 _Coverage，与 SampleWeatherData 一致）：平移 + 截断
    weatherData.r = saturate(weatherData.r - _Coverage);
    if (weatherData.r <= 0)
    {
        cloudInfo.density = 0;
        cloudInfo.absorptivity = 1;
        return cloudInfo;
    }

    //计算云类型密度
    float stratusDensity = GetCloudTypeDensity(heightFraction, info.stratusInfo.x, info.stratusInfo.y,
                                               info.stratusInfo.z);
    float cumulusDensity = GetCloudTypeDensity(heightFraction, info.cumulusInfo.x, info.cumulusInfo.y,
                                               info.cumulusInfo.z);
    float cloudTypeDensity = lerp(stratusDensity, cumulusDensity, weatherData.b);
    if (cloudTypeDensity <= 0)
    {
        cloudInfo.density = 0;
        cloudInfo.absorptivity = 1;
        return cloudInfo;
    }

    //云吸收率
    float cloudAbsorptivity = Interpolation3(0, weatherData.g, 1, info.cloudDensityAdjust);

    // 低频形状噪声 × 云型高度梯度（weather.b 驱动三云属）
    float baseCloud = SampleLowFrequencyNoise(p, info, 0);
    float cloudDensity = baseCloud * cloudTypeDensity * weatherData.r;

    if (cloudDensity > 0 && !doCheaply)
    {
        p += (windDirection + float3(0, 0.1, 0)) * info.windSpeed * _Time.y * 0.1;
        float detailDensity = SampleHighFrequencyNoise(p, info);
        //  lerp(detailTexFBM, 1.0 - detailTexFBM,saturate(heightFraction * 1.0));
        cloudDensity = Remap(cloudDensity, detailDensity * info.detailShapeEffect, 1.0, 0.0, 1.0);
    }

    cloudInfo.density = cloudDensity * info.cloudDensityScale * 0.1;
    cloudInfo.absorptivity = cloudAbsorptivity;
    return cloudInfo;
}

#endif
