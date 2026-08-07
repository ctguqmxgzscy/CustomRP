#ifndef SSSSTRANSMISSION_INCLUDED
#define SSSSTRANSMISSION_INCLUDED

// -----------------------------------------------------------------------------
// SSSS Transmission (Beer-Lambert translucency)
// Adapted from bentoBAUX / Jorge Jimenez "Separable Subsurface Scattering"
//
// 依赖全局变量（由 SSSSPassFeature 或材质设置）：
//   _SSSS_TransmissionDistance  float3  逐通道透射距离（mm），值越大衰减越慢
//   _SSSS_TransmissionScale     float   整体缩放，映射到 [0,1]
//
// 用法（后续启用时 uncomment）：
//   #include "Assets/Features/SSSS/Runtime/Shader/SSSSTransmission.hlsl"
//   float thickness = SAMPLE_TEXTURE2D(_ThicknessMap, sampler_ThicknessMap, uv).r;
//   float3 T = ArtistTransmissionProfile(thickness);
// -----------------------------------------------------------------------------

float3 _SSSS_TransmissionDistance;
float  _SSSS_TransmissionScale;

// 将 scatterScale 映射到透射强度，参考 Blender SSS scale 0–10
float GetTransmissionScaleAmount()
{
    return saturate(_SSSS_TransmissionScale / 10.0);
}

// Beer-Lambert: T = exp(-d / D)
//   d = 穿过的厚度
//   D = 透射距离（值越大，衰减越慢，材料越透光）
float3 ArtistTransmissionProfile(float thickness)
{
    float3 distance = max(_SSSS_TransmissionDistance, 0.0001);
    return saturate(exp(-thickness / distance));
}

#endif
