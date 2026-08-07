#ifndef DITHER_INCLUDED
#define DITHER_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// 声明
float4 _TAAJitter;

// jitter dither map
static half dither[16] = {
    0.0, 0.5, 0.125, 0.625,
    0.75, 0.25, 0.875, 0.375,
    0.187, 0.687, 0.0625, 0.562,
    0.937, 0.437, 0.812, 0.312
};

// Unity官方风格的Dither实现
static float DITHER_THRESHOLDS[16] = {
    1.0 / 17.0, 9.0 / 17.0, 3.0 / 17.0, 11.0 / 17.0,
    13.0 / 17.0, 5.0 / 17.0, 15.0 / 17.0, 7.0 / 17.0,
    4.0 / 17.0, 12.0 / 17.0, 2.0 / 17.0, 10.0 / 17.0,
    16.0 / 17.0, 8.0 / 17.0, 14.0 / 17.0, 6.0 / 17.0
};

// 选项3: 蓝噪声风格矩阵（视觉噪声更少，但计算稍复杂）
static float dither4x4_blue[16] = {
    0.0625, 0.5625, 0.1875, 0.6875,
    0.8125, 0.3125, 0.9375, 0.4375,
    0.25, 0.75, 0.125, 0.625,
    1.0, 0.5, 0.875, 0.375
};

TEXTURE2D(_DitherTexture);

float SampleBlueNoise(float2 screenPos)
{
    float2 interleavedPos = fmod(floor(screenPos), 64.0);
    return SAMPLE_TEXTURE2D(_DitherTexture, sampler_LinearRepeat,
                            interleavedPos / 64.0 + float2(0.5 / 64.0, 0.5 / 64.0)).w;
}
#endif
