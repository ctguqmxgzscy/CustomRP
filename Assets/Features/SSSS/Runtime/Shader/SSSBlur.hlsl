#ifndef SSS_BLUR_INCLUDED
#define SSS_BLUR_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

SAMPLER(sampler_BlitTexture);

#define SamplerSteps 25
float4   _Kernel[SamplerSteps];
int      _KernelSize;
float    _SSSScale;

float4 SSSBlur(float2 uv, float2 dir)
{
    float2 texelSize = _ScreenParams.zw - 1.0;   // 1/width, 1/height
    float eyeDepth = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);

    float distanceToProjectionWindow = unity_CameraProjection._m11;

    // UVOffset = SSSIntencity * BlurLength
    //   SSSIntencity = _SSSScale * dir * texelSize  (screen-space, before depth)
    //   BlurLength   = DistanceToProjectionWindow / eyeDepth
    float2 sssIntencity = _SSSScale * dir * texelSize;
    float blurLength = distanceToProjectionWindow / eyeDepth;
    float2 uvOffset = sssIntencity * blurLength;

    float4 result = float4(0, 0, 0, 0);
    float3 centerColor = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv).rgb;
    result.rgb = _Kernel[0].rgb * centerColor;

    for (int k = 1; k < SamplerSteps; k++)
    {
        float2 sampleUV = uv + _Kernel[k].a * uvOffset;
        float3 neighborColor = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, sampleUV).rgb;

        // 深度感知边界修正：saturate(DPTimes300 * SSSIntencity * |Δdepth|)
        // 邻居深度与中心差越大，clampFactor 越高，邻居色越接近中心色
        float neighborDepth = LinearEyeDepth(SampleSceneDepth(sampleUV), _ZBufferParams);
        float depthDiff = abs(neighborDepth - eyeDepth);
        float clampFactor = saturate(distanceToProjectionWindow * 300.0
            * length(sssIntencity) * depthDiff);
        neighborColor = lerp(neighborColor, centerColor, clampFactor);

        result.rgb += _Kernel[k].rgb * neighborColor;
    }

    return result;
}

#endif
