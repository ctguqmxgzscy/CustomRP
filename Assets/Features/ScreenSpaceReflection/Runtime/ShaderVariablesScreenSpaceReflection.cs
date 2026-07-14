namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    ///     C# 端 SSR Shader 常量结构体。
    ///     配合 [GenerateHLSL] 属性自动生成对应的 HLSL cbuffer。
    /// </summary>
    [GenerateHLSL(needAccessors = false, generateCBuffer = true)]
    internal struct ShaderVariablesScreenSpaceReflection
    {
        // ---- Ray Marching 参数 ----
        /// <summary>深度厚度缩放：depth_bias = depth * _SsrThicknessScale + _SsrThicknessBias</summary>
        public float ssrThicknessScale;

        /// <summary>深度厚度偏移。</summary>
        public float ssrThicknessBias;

        /// <summary>Stencil 位掩码，标记接受 SSR 的像素。</summary>
        public int ssrStencilBit;

        /// <summary>射线最大迭代次数。</summary>
        public int ssrIterLimit;

        // ---- 光滑度淡出 ----
        /// <summary>SSR 完全淡出的粗糙度值。</summary>
        public float ssrRoughnessFadeEnd;

        /// <summary>光滑度淡出区长度的倒数。</summary>
        public float ssrRoughnessFadeRcpLength;

        /// <summary>光滑度淡出终点乘以淡出区长度的倒数。</summary>
        public float ssrRoughnessFadeEndTimesRcpLength;

        /// <summary>屏幕边缘淡出区长度的倒数。</summary>
        public float ssrEdgeFadeRcpLength;

        // ---- 金字塔 ----
        /// <summary>Depth Pyramid 最大 MIP 层级。</summary>
        public int ssrDepthPyramidMaxMip;

        /// <summary>Color Pyramid 最大 MIP 层级。</summary>
        public int ssrColorPyramidMaxMip;

        /// <summary>是否反射天空（0 = 不反射，1 = 反射）。</summary>
        public int ssrReflectsSky;

        /// <summary>累积因子（用于 PBR 模式的时间累积）。</summary>
        public float ssrAccumulationAmount;

        // ---- PBR 速度拒绝 ----
        /// <summary>PBR 速度拒绝参数。</summary>
        public float ssrPbrSpeedRejection;

        /// <summary>PBR bias 参数。</summary>
        public float ssrPbrBias;

        /// <summary>PBR 速度拒绝缩放因子。</summary>
        public float ssrPrbSpeedRejectionScalerFactor;

        /// <summary>Uniform roughness 回退值（当 per-pixel smoothness 不可用时）。</summary>
        public float ssrUniformRoughness;

        /// <summary>填充字节。</summary>
        public float ssrPbrPad0;
    }
}