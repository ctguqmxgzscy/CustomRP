using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Screen Space Reflection 算法类型。
    /// </summary>
    public enum ScreenSpaceReflectionAlgorithm
    {
        /// <summary>传统近似 SSR（快但不够精确）。</summary>
        Approximation,
        /// <summary>基于物理的 PBR 屏幕空间反射，支持多帧累积。</summary>
        PBRAccumulation
    }

    /// <summary>
    /// Volume 组件：屏幕空间反射（SSR）设置。
    /// 从 HDRP 的 ScreenSpaceReflection 迁移，移除了光线追踪和 HDRP 特有参数。
    /// </summary>
    [Serializable]
    [VolumeComponentMenuForRenderPipeline("Lighting/Screen Space Reflection", typeof(UniversalRenderPipeline))]
    public class ScreenSpaceReflection : VolumeComponent
    {
        #region General

        /// <summary>启用屏幕空间反射。</summary>
        [Tooltip("Enable Screen Space Reflections.")]
        public BoolParameter enabled = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);

        /// <summary>选取的 SSR 算法。</summary>
        [Tooltip("Screen Space Reflections Algorithm used.")]
        public SSRAlgoParameter usedAlgorithm = new SSRAlgoParameter(ScreenSpaceReflectionAlgorithm.Approximation);

        /// <summary>最低光滑度阈值：低于此值的表面不应用 SSR。</summary>
        [Tooltip("Controls the smoothness value at which SSR activates and the smoothness-controlled fade out stops.")]
        public ClampedFloatParameter minSmoothness = new ClampedFloatParameter(0.9f, 0.0f, 1.0f);

        /// <summary>光滑度衰减起点：光滑度从 minSmoothness → smoothnessFadeStart 之间 SSR 会淡出。</summary>
        [Tooltip("Controls the smoothness value at which the smoothness-controlled fade out starts. The fade is in the range [Min Smoothness, Smoothness Fade Start].")]
        public ClampedFloatParameter smoothnessFadeStart = new ClampedFloatParameter(0.95f, 0.0f, 1.0f);

        /// <summary>当启用时，SSR 会处理天空反射（仅不透明物体）。</summary>
        [Tooltip("When enabled, SSR handles sky reflection for opaque objects.")]
        public BoolParameter reflectSky = new BoolParameter(true);

        #endregion

        #region Ray Marching

        /// <summary>深度缓冲厚度：射线行进时，允许穿透物体背后的容忍度。</summary>
        [Tooltip("Controls the typical thickness of objects the reflection rays may pass behind.")]
        public ClampedFloatParameter depthBufferThickness = new ClampedFloatParameter(0.01f, 0, 1);

        /// <summary>屏幕边缘淡出距离。</summary>
        [Tooltip("Controls the distance at which SSR fades out near the edge of the screen.")]
        public ClampedFloatParameter screenFadeDistance = new ClampedFloatParameter(0.1f, 0.0f, 1.0f);

        /// <summary>射线最大迭代次数。影响精度和性能。</summary>
        [Tooltip("Sets the maximum number of steps used for ray marching. Affects both correctness and performance.")]
        public MinIntParameter rayMaxIterations = new MinIntParameter(64, 0);

        /// <summary>启用凹面反射追踪（射线朝向相机）。性能开销较高。</summary>
        [Tooltip("When enabled, SSR also traces rays that travel towards the camera, which handles concave reflections. Has a performance cost.")]
        public BoolParameter traceTowardsEye = new BoolParameter(false);

        #endregion

        #region PBR Accumulation

        /// <summary>多帧累积因子（0 = 无累积，1 = 全累积）。</summary>
        [Tooltip("Controls the amount of accumulation (0 no accumulation, 1 just accumulate).")]
        public ClampedFloatParameter accumulationFactor = new ClampedFloatParameter(0.75f, 0.0f, 1.0f);

        /// <summary>PBR Roughness Bias（0 = 不偏移，1 = 完全平滑反射）。</summary>
        [Tooltip("Controls the relative roughness offset. A low value means material roughness stays the same, a high value means smoother reflections.")]
        public ClampedFloatParameter biasFactor = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);

        /// <summary>速度拒绝阈值：控制基于上一帧运动向量的历史帧被拒绝的概率。</summary>
        [Tooltip("Controls the likelihood history will be rejected based on the previous frame motion vectors of both the surface and the hit object.")]
        public ClampedFloatParameter speedRejectionParam = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);

        /// <summary>速度上限缩放因子：场景/相机移动越快，这个值应该越高。</summary>
        [Tooltip("Controls the upper range of speed. The faster the objects or camera are moving, the higher this number should be.")]
        public ClampedFloatParameter speedRejectionScalerFactor = new ClampedFloatParameter(0.2f, 0.001f, 1.0f);

        /// <summary>启用平滑速度拒绝（部分拒绝），否则为硬阈值（完全接受或完全拒绝）。</summary>
        [Tooltip("When enabled, history can be partially rejected for moving objects which gives a smoother transition.")]
        public BoolParameter speedSmoothReject = new BoolParameter(false);

        /// <summary>使用反射表面（source）的世界空间速度进行速度拒绝。</summary>
        [Tooltip("When enabled, the reflecting surface movement is considered as a valid rejection condition.")]
        public BoolParameter speedSurfaceOnly = new BoolParameter(true);

        /// <summary>使用 SSR 命中表面（target）的世界空间速度进行速度拒绝。</summary>
        [Tooltip("When enabled, the reflected surface movement is considered as a valid rejection condition.")]
        public BoolParameter speedTargetOnly = new BoolParameter(true);

        /// <summary>启用基于世界空间运动向量的速度拒绝（否则使用 NDC 空间）。</summary>
        [Tooltip("When enabled, world space speed from Motion vector is used to reject samples.")]
        public BoolParameter enableWorldSpeedRejection = new BoolParameter(false);

        #endregion
    }

    /// <summary>
    /// SSR 算法类型的 Volume 参数封装。
    /// </summary>
    [Serializable]
    public sealed class SSRAlgoParameter : VolumeParameter<ScreenSpaceReflectionAlgorithm>
    {
        public SSRAlgoParameter(ScreenSpaceReflectionAlgorithm value, bool overrideState = false)
            : base(value, overrideState) { }
    }
}
