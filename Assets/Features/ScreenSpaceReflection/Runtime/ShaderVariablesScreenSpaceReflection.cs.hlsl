//
// 手动生成 — 与 ShaderVariablesScreenSpaceReflection.cs 中 [GenerateHLSL] 的输出一致
// C# 结构体位于 UnityEngine.Rendering.Universal.ShaderVariablesScreenSpaceReflection
//

#ifndef SHADERVARIABLESSCREENSPACEREFLECTION_CS_HLSL
#define SHADERVARIABLESSCREENSPACEREFLECTION_CS_HLSL

CBUFFER_START(ShaderVariablesScreenSpaceReflection)
    float ssrThicknessScale;
    float ssrThicknessBias;
    int   ssrStencilBit;
    int   ssrIterLimit;
    float ssrRoughnessFadeEnd;
    float ssrRoughnessFadeRcpLength;
    float ssrRoughnessFadeEndTimesRcpLength;
    float ssrEdgeFadeRcpLength;
    int   ssrDepthPyramidMaxMip;
    int   ssrColorPyramidMaxMip;
    int   ssrReflectsSky;
    float ssrAccumulationAmount;
    float ssrPbrSpeedRejection;
    float ssrPbrBias;
    float ssrPrbSpeedRejectionScalerFactor;
    float ssrUniformRoughness;
    float ssrPbrPad0;
CBUFFER_END

#endif
