using System;
using UnityEngine.Experimental.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    ///     屏幕空间反射 (SSR) ScriptableRendererFeature。
    ///     负责在 URP 渲染管线中注册和调度 SSR 渲染 Pass。
    /// </summary>
    [DisallowMultipleRendererFeature("Screen Space Reflection")]
    [Tooltip("屏幕空间反射 (SSR)——使用 Hi-Z 射线行进来渲染实时反射。")]
    public class ScreenSpaceReflectionFeature : ScriptableRendererFeature
    {
        // =====================================================================
        // 序列化字段
        // =====================================================================

        [SerializeField] [Tooltip("深度金字塔生成 Compute Shader (DepthPyramid.compute)。")]
        private ComputeShader depthPyramidShader;

        [SerializeField] [Tooltip("SSR 光线追踪 Compute Shader (ScreenSpaceReflections.compute)。")]
        private ComputeShader ssrShader;

        [SerializeField] [Tooltip("Pass 在渲染管线中的注入点。需保证 Depth / Normal / Motion / Opaque 纹理已就绪。")]
        private RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        // =====================================================================
        // 私有字段
        // =====================================================================

        private ScreenSpaceReflectionPass m_SsrPass;

        // =====================================================================
        // ScriptableRendererFeature 生命周期
        // =====================================================================

        /// <inheritdoc />
        public override void Create()
        {
#if UNITY_EDITOR
            // 在编辑器下尝试重新加载空引用（支持域重载后恢复）
            if (depthPyramidShader == null)
                depthPyramidShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/Features/ScreenSpaceReflection/Runtime/Shaders/DepthPyramid.compute");
            if (ssrShader == null)
                ssrShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/Features/ScreenSpaceReflection/Runtime/Shaders/ScreenSpaceReflections.compute");
#endif
            if (m_SsrPass == null && depthPyramidShader != null && ssrShader != null)
                m_SsrPass = new ScreenSpaceReflectionPass(passEvent, depthPyramidShader, ssrShader);
        }

        /// <inheritdoc />
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (depthPyramidShader == null || ssrShader == null)
            {
                Debug.LogErrorFormat(
                    "[SSR] {0}.AddRenderPasses(): 缺少 ComputeShader 引用，" +
                    "请在 Inspector 中拖入 DepthPyramid.compute 和 ScreenSpaceReflections.compute。",
                    GetType().Name);
                return;
            }

            // 通过 Volume 系统检查 SSR 是否启用
            var stack = VolumeManager.instance.stack;
            var ssrVolume = stack.GetComponent<ScreenSpaceReflection>();
            if (ssrVolume == null || !ssrVolume.enabled.value)
                return;

            m_SsrPass.Setup();
            renderer.EnqueuePass(m_SsrPass);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            m_SsrPass?.Dispose();
            m_SsrPass = null;
        }
    }

    // =====================================================================
    // ScreenSpaceReflectionPass — 执行 SSR 的 ScriptableRenderPass
    // =====================================================================

    /// <summary>
    ///     SSR 渲染 Pass。每帧执行以下步骤：
    ///     1. 从 Volume 组件读取 SSR 参数
    ///     2. 设置 Shader cbuffer 常量
    ///     3. 生成 Hi-Z Depth Pyramid
    ///     4. Dispatch SsrTrace 计算着色器 → _SsrHitPointTexture
    ///     5. Dispatch SsrReproject 计算着色器 → _SsrAccumTexture
    /// </summary>
    internal class ScreenSpaceReflectionPass : ScriptableRenderPass, IDisposable
    {
        // =====================================================================
        // Shader Property ID 常量
        // =====================================================================

        // 输出纹理
        private static readonly int SsrHitPointTextureId = Shader.PropertyToID("_SsrHitPointTexture");
        private static readonly int SsrAccumTextureId = Shader.PropertyToID("_SsrAccumTexture");
        private static readonly int SsrHistoryTextureId = Shader.PropertyToID("_SsrHistoryTexture");

        // 输入纹理 — compute shader 不会自动继承全局 Shader.SetGlobalTexture 绑定，
        // 必须逐 kernel 调用 SetComputeTextureParam
        private static readonly int CameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int CameraNormalsTextureId = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int CameraOpaqueTextureId = Shader.PropertyToID("_CameraOpaqueTexture");
        private static readonly int CameraMotionVectorsTextureId = Shader.PropertyToID("_MotionVectorTexture");

        // Color Pyramid（阶段 7: 读写分离）
        private static readonly int ColorPyramidTextureId = Shader.PropertyToID("_ColorPyramidTexture");
        private static readonly int ColorPyramidRWId = Shader.PropertyToID("_ColorPyramidRW");

        // ShaderVariablesScreenSpaceReflection cbuffer 字段
        private static readonly int SsrThicknessScaleId = Shader.PropertyToID("ssrThicknessScale");
        private static readonly int SsrThicknessBiasId = Shader.PropertyToID("ssrThicknessBias");
        private static readonly int SsrStencilBitId = Shader.PropertyToID("ssrStencilBit");
        private static readonly int SsrIterLimitId = Shader.PropertyToID("ssrIterLimit");
        private static readonly int SsrRoughnessFadeEndId = Shader.PropertyToID("ssrRoughnessFadeEnd");
        private static readonly int SsrRoughnessFadeRcpLengthId = Shader.PropertyToID("ssrRoughnessFadeRcpLength");

        private static readonly int SsrRoughnessFadeEndTimesRcpLengthId =
            Shader.PropertyToID("ssrRoughnessFadeEndTimesRcpLength");

        private static readonly int SsrEdgeFadeRcpLengthId = Shader.PropertyToID("ssrEdgeFadeRcpLength");
        private static readonly int SsrDepthPyramidMaxMipId = Shader.PropertyToID("ssrDepthPyramidMaxMip");
        private static readonly int SsrColorPyramidMaxMipId = Shader.PropertyToID("ssrColorPyramidMaxMip");
        private static readonly int SsrReflectsSkyId = Shader.PropertyToID("ssrReflectsSky");
        private static readonly int SsrAccumulationAmountId = Shader.PropertyToID("ssrAccumulationAmount");
        private static readonly int SsrUniformRoughnessId = Shader.PropertyToID("ssrUniformRoughness");
        private static readonly int SsrPbrSpeedRejectionId = Shader.PropertyToID("ssrPbrSpeedRejection");

        private static readonly int SsrPrbSpeedRejectionScalerFactorId =
            Shader.PropertyToID("ssrPrbSpeedRejectionScalerFactor");

        private static readonly int SsrPbrBiasId = Shader.PropertyToID("ssrPbrBias");
        private static readonly int SsrTraceTowardsEyeId = Shader.PropertyToID("_SsrTraceTowardsEye");

        // 非 Unity 内置变量（URP 不会自动设置，需显式传入 compute shader）
        private static readonly int ScreenSizeId = Shader.PropertyToID("_ScreenSize");
        private readonly int m_ColorPyramidCopyKernel;

        // Profiling
        private readonly ProfilingSampler m_ProfilingSampler;
        private readonly int m_ReprojectKernel;

        // =====================================================================
        // 成员
        // =====================================================================

        // Compute Shader 引用和内核索引
        private readonly ComputeShader m_SsrShader;
        private readonly int m_TraceKernel;
        private RTHandle m_AccumTexture;

        // Color Pyramid 生成器（阶段 7: 按 roughness LOD 采样的颜色 mip chain）
        private ColorPyramidGenerator m_ColorPyramidGenerator;

        // Color Pyramid 有效性跟踪
        private bool m_ColorPyramidValid;

        // 全屏合成
        private Material m_CompositeMaterial;

        // 分辨率跟踪（尺寸改变时重建 RT）
        private Vector2Int m_CurrentViewportSize;

        // 深度金字塔生成器
        private DepthPyramidGenerator m_DepthPyramidGenerator;

        // 帧计数（阶段 7: 用于首帧保护，前几帧强制完全累积当前帧）
        private int m_FrameCount;

        private RTHandle m_HistoryTexture; // 阶段 7: 上一帧累积结果，与 m_AccumTexture 每帧交换

        // 输出 RT Handle
        private RTHandle m_HitPointTexture;

        // =====================================================================
        // 构造 / 初始化
        // =====================================================================

        public ScreenSpaceReflectionPass(
            RenderPassEvent passEvent,
            ComputeShader depthPyramidShader,
            ComputeShader ssrShader)
        {
            renderPassEvent = passEvent;
            m_SsrShader = ssrShader;

            m_TraceKernel = ssrShader.FindKernel("SsrTrace");
            m_ReprojectKernel = ssrShader.FindKernel("SsrReproject");
            m_ColorPyramidCopyKernel = ssrShader.FindKernel("KColorPyramidCopy");

            m_ProfilingSampler = new ProfilingSampler("ScreenSpaceReflection");

            if (depthPyramidShader != null)
                m_DepthPyramidGenerator = new DepthPyramidGenerator(depthPyramidShader);

            // Color Pyramid 生成器（复用 SSR shader 中的 KColorPyramidCopy kernel）
            m_ColorPyramidGenerator = new ColorPyramidGenerator(ssrShader);

            // 加载 SSR 全屏合成 Shader
            var compositeShader = Shader.Find("Hidden/Universal Render Pipeline/SSRComposite");
            if (compositeShader != null)
                m_CompositeMaterial = new Material(compositeShader);
            else
                Debug.LogError("[SSR] 找不到 SSRComposite.shader，反射无法合成到屏幕。");
        }

        /// <summary>
        ///     释放所有 GPU 资源。
        /// </summary>
        public void Dispose()
        {
            m_DepthPyramidGenerator?.Dispose();
            m_DepthPyramidGenerator = null;

            m_ColorPyramidGenerator?.Dispose();
            m_ColorPyramidGenerator = null;

            m_HitPointTexture?.Release();
            m_HitPointTexture = null;

            m_AccumTexture?.Release();
            m_AccumTexture = null;

            m_HistoryTexture?.Release();
            m_HistoryTexture = null;

            if (m_CompositeMaterial != null)
            {
                CoreUtils.Destroy(m_CompositeMaterial);
                m_CompositeMaterial = null;
            }
        }

        /// <summary>
        ///     在 Feature.AddRenderPasses() 中调用，声明输入需求。
        ///     URP 会根据声明的需求自动生成 _CameraDepthTexture / _CameraNormalsTexture /
        ///     _CameraMotionVectorsTexture / _CameraOpaqueTexture。
        /// </summary>
        public void Setup()
        {
            ConfigureInput(
                ScriptableRenderPassInput.Depth |
                ScriptableRenderPassInput.Normal |
                ScriptableRenderPassInput.Motion |
                ScriptableRenderPassInput.Color);
        }

        // =====================================================================
        // 生命周期
        // =====================================================================

        /// <inheritdoc />
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            var viewportSize = new Vector2Int(desc.width, desc.height);

            if (viewportSize != m_CurrentViewportSize)
            {
                m_CurrentViewportSize = viewportSize;
                EnsureTextures(desc);
                // 分辨率变化 → 重置帧计数（历史缓冲已重建，Color Pyramid 无效）
                m_FrameCount = 0;
                m_ColorPyramidValid = false;
            }
        }

        /// <inheritdoc />
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_DepthPyramidGenerator == null || m_SsrShader == null)
                return;

            // 提前检查 Volume，避免不必要的 CommandBuffer 分配
            var stack = VolumeManager.instance.stack;
            var ssr = stack.GetComponent<ScreenSpaceReflection>();
            if (ssr == null || !ssr.enabled.value)
                return;

            var cmd = CommandBufferPool.Get("ScreenSpaceReflection");

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                // ---------------------------------------------------------
                // 1. 设置非内置全局变量（_ScreenSize 等）
                // ---------------------------------------------------------
                SetShaderGlobals(cmd, ref renderingData);

                // ---------------------------------------------------------
                // 2. 设置 Shader 常量 (ShaderVariablesScreenSpaceReflection)
                // ---------------------------------------------------------
                SetShaderVariables(cmd, ssr, ref renderingData);

                // ---------------------------------------------------------
                // 3. 生成 Color Pyramid — 将 _CameraOpaqueTexture 复制为 mip chain
                //    供下一帧 SsrReproject 按 roughness LOD 采样
                // ---------------------------------------------------------
                m_ColorPyramidGenerator.Generate(cmd, m_CurrentViewportSize);

                // ---------------------------------------------------------
                // 4. 绑定输出 UAV 纹理（compute shader 需显式指定 UAV slot）
                // ---------------------------------------------------------
                cmd.SetComputeTextureParam(m_SsrShader, m_TraceKernel,
                    SsrHitPointTextureId, m_HitPointTexture);
                cmd.SetComputeTextureParam(m_SsrShader, m_ReprojectKernel,
                    SsrAccumTextureId, m_AccumTexture);
                cmd.SetComputeTextureParam(m_SsrShader, m_ReprojectKernel,
                    SsrHitPointTextureId, m_HitPointTexture);

                // 绑定输入纹理 — compute shader 不自动继承全局属性
                cmd.SetComputeTextureParam(m_SsrShader, m_TraceKernel,
                    CameraDepthTextureId, Shader.GetGlobalTexture(CameraDepthTextureId));
                cmd.SetComputeTextureParam(m_SsrShader, m_TraceKernel,
                    CameraNormalsTextureId, Shader.GetGlobalTexture(CameraNormalsTextureId));
                cmd.SetComputeTextureParam(m_SsrShader, m_ReprojectKernel,
                    CameraOpaqueTextureId, Shader.GetGlobalTexture(CameraOpaqueTextureId));
                cmd.SetComputeTextureParam(m_SsrShader, m_ReprojectKernel,
                    CameraMotionVectorsTextureId, Shader.GetGlobalTexture(CameraMotionVectorsTextureId));

                // 阶段 7: 绑定上一帧 Color Pyramid（_ColorPyramidTexture，SRV 只读）
                m_ColorPyramidGenerator.BindReadPyramid(cmd, m_SsrShader, m_ReprojectKernel,
                    ColorPyramidTextureId);

                // 阶段 7: 绑定上一帧累积结果（_SsrHistoryTexture）
                // 注意：m_HistoryTexture 在首帧/分辨率变化时为全黑（新分配的 RT）
                if (m_HistoryTexture != null)
                    cmd.SetComputeTextureParam(m_SsrShader, m_ReprojectKernel,
                        SsrHistoryTextureId, m_HistoryTexture);

                // ---------------------------------------------------------
                // 5. 生成 Depth Pyramid (Hi-Z)
                // ---------------------------------------------------------
                m_DepthPyramidGenerator.Generate(cmd, m_CurrentViewportSize);
                m_DepthPyramidGenerator.SetGlobalTextures(cmd);

                // ---- 算法选择（0 = Approximation, 1 = PBRAccumulation） ----
                cmd.SetComputeIntParam(m_SsrShader, "_SsrAlgorithm",
                    ssr.usedAlgorithm.value == ScreenSpaceReflectionAlgorithm.PBRAccumulation ? 1 : 0);

                // ---- VNDF 随机种子 ----
                cmd.SetComputeFloatParam(m_SsrShader, "_SsrFrameIndex", m_FrameCount);

                // ---- 速度拒绝模式标志 ----
                cmd.SetComputeIntParam(m_SsrShader, "_SsrWorldSpeedRejection",
                    ssr.enableWorldSpeedRejection.value ? 1 : 0);
                cmd.SetComputeIntParam(m_SsrShader, "_SsrSmoothSpeedRejection",
                    ssr.speedSmoothReject.value ? 1 : 0);
                cmd.SetComputeIntParam(m_SsrShader, "_SsrSpeedSurfaceOnly",
                    ssr.speedSurfaceOnly.value ? 1 : 0);
                cmd.SetComputeIntParam(m_SsrShader, "_SsrSpeedTargetOnly",
                    ssr.speedTargetOnly.value ? 1 : 0);

                // ---- 凹面反射（trace towards eye） ----
                cmd.SetComputeIntParam(m_SsrShader, "_SsrTraceTowardsEye",
                    ssr.traceTowardsEye.value ? 1 : 0);

                // ---------------------------------------------------------
                // 6. Dispatch SsrTrace kernel (8×8 thread groups)
                // ---------------------------------------------------------
                var tgX = DivRoundUp(m_CurrentViewportSize.x, 8);
                var tgY = DivRoundUp(m_CurrentViewportSize.y, 8);
                cmd.DispatchCompute(m_SsrShader, m_TraceKernel, tgX, tgY, 1);

                // ---------------------------------------------------------
                // 7. Dispatch SsrReproject kernel (8×8 thread groups)
                // ---------------------------------------------------------
                cmd.DispatchCompute(m_SsrShader, m_ReprojectKernel, tgX, tgY, 1);

                // ---------------------------------------------------------
                // 8. 将累积结果绑定为全局纹理（供 Composite pass 使用）
                //    _CameraOpaqueTexture 由 URP CopyColor pass 的 cmd 设置，此处不覆盖
                // ---------------------------------------------------------
                cmd.SetGlobalTexture(SsrAccumTextureId, m_AccumTexture);

                // ---------------------------------------------------------
                // 9. 全屏合成：将反射叠加到 Camera Color Target
                // ---------------------------------------------------------
                if (m_CompositeMaterial != null)
                {
                    var cameraColorTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
                    CoreUtils.SetRenderTarget(cmd, cameraColorTarget);
                    cmd.DrawProcedural(Matrix4x4.identity, m_CompositeMaterial, 0,
                        MeshTopology.Triangles, 3, 1);
                }
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            // ---------------------------------------------------------
            // 10. 交换双缓冲（阶段 7：下一帧使用当前帧输出作为历史）
            // ---------------------------------------------------------
            (m_HistoryTexture, m_AccumTexture) = (m_AccumTexture, m_HistoryTexture);
            m_ColorPyramidGenerator.Swap();
            m_ColorPyramidValid = m_FrameCount > 0; // 首帧无有效的 previous color pyramid
            m_FrameCount++;
        }

        /// <inheritdoc />
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // 当前无清理需要（后续阶段可能重置全局关键字）
        }

        // =====================================================================
        // 私有辅助方法
        // =====================================================================

        /// <summary>
        ///     设置 compute shader 中需要的非内置全局变量。
        ///     UNITY_MATRIX_VP / UNITY_MATRIX_I_VP / _WorldSpaceCameraPos 等已由 URP
        ///     通过 context.SetupCameraProperties 自动注入，无需重复设置。
        ///     _CameraDepthTexture / _CameraNormalsTexture / _CameraOpaqueTexture /
        ///     _CameraMotionVectorsTexture 由 URP 通过 Shader.SetGlobalTexture 注入，
        ///     compute shader 的 TEXTURE2D_X 声明自动获取绑定，无需 SetComputeTextureParam。
        /// </summary>
        private void SetShaderGlobals(CommandBuffer cmd, ref RenderingData renderingData)
        {
            // _ScreenSize 不是 Unity 内置变量，需手动设置
            var w = renderingData.cameraData.cameraTargetDescriptor.width;
            var h = renderingData.cameraData.cameraTargetDescriptor.height;
            cmd.SetGlobalVector(ScreenSizeId, new Vector4(w, h, 1.0f / w, 1.0f / h));
        }

        /// <summary>
        ///     从 Volume 组件读取 SSR 参数并设置全局 Shader 常量。
        /// </summary>
        private void SetShaderVariables(CommandBuffer cmd, ScreenSpaceReflection ssr, ref RenderingData renderingData)
        {
            var cam = renderingData.cameraData.camera;
            var n = cam.nearClipPlane;
            var f = cam.farClipPlane;
            var thickness = ssr.depthBufferThickness.value;

            // ---- Ray Marching 参数 ----
            // HDRP 公式: thicknessScale = 1/(1+thickness), thicknessBias = -n/(f-n) * thickness * thicknessScale
            // 这保证了世界空间深度的正确性（投影参数感知）
            var thicknessScale = 1.0f / (1.0f + thickness);
            var thicknessBias = -n / (f - n) * (thickness * thicknessScale);
            cmd.SetGlobalFloat(SsrThicknessScaleId, thicknessScale);
            cmd.SetGlobalFloat(SsrThicknessBiasId, thicknessBias);
            cmd.SetGlobalInt(SsrStencilBitId, 0);
            cmd.SetGlobalInt(SsrIterLimitId, ssr.rayMaxIterations.value);

            // ---- 光滑度淡出 ----
            // 关系：roughness = 1 - smoothness
            // SSR 在 smoothness ∈ [minSmoothness, smoothnessFadeStart] 淡出
            //   roughnessFadeStart: 高光滑度边界（低粗糙度）→ 完全不透明
            //   roughnessFadeEnd:   低光滑度边界（高粗糙度）→ 完全淡出
            var roughnessFadeStart = 1.0f - ssr.smoothnessFadeStart.value; // e.g. 0.05
            var roughnessFadeEnd = 1.0f - ssr.minSmoothness.value;          // e.g. 0.10
            var roughnessFadeLength = roughnessFadeEnd - roughnessFadeStart; // e.g. 0.05

            // 对齐 HDRP UpdateSSRConstantBuffer:
            //   fadeRcpLength = 1 / fadeLength (用于 Remap10)
            //   fadeEndTimesRcpLength = fadeEnd / fadeLength (用于 Remap10)
            //   Remap10(x, rcpLength, endTimesRcpLength) = saturate(endTimesRcpLength - x * rcpLength)
            var fadeRcpLength = (roughnessFadeLength != 0.0f)
                ? (1.0f / roughnessFadeLength)
                : 0.0f;
            var fadeEndTimesRcpLength = (roughnessFadeLength != 0.0f)
                ? (roughnessFadeEnd * fadeRcpLength)
                : 1.0f;

            cmd.SetGlobalFloat(SsrRoughnessFadeEndId, roughnessFadeEnd);
            cmd.SetGlobalFloat(SsrRoughnessFadeRcpLengthId, fadeRcpLength);
            cmd.SetGlobalFloat(SsrRoughnessFadeEndTimesRcpLengthId, fadeEndTimesRcpLength);

            // ---- per-pixel roughness 回退值 ----
            cmd.SetGlobalFloat(SsrUniformRoughnessId, roughnessFadeStart);

            // ---- 屏幕边缘淡出 ----
            var edgeFadeRcpLength = Mathf.Min(1.0f / Mathf.Max(ssr.screenFadeDistance.value, 0.001f), float.MaxValue);
            cmd.SetGlobalFloat(SsrEdgeFadeRcpLengthId, edgeFadeRcpLength);

            // ---- 金字塔 ----
            cmd.SetGlobalInt(SsrDepthPyramidMaxMipId, m_DepthPyramidGenerator.MipLevelCount - 1);
            cmd.SetGlobalInt(SsrColorPyramidMaxMipId,
                Mathf.Max(0, m_ColorPyramidGenerator.MipCount - 1));

            // ---- 天空反射 ----
            cmd.SetGlobalInt(SsrReflectsSkyId, ssr.reflectSky.value ? 1 : 0);

            // ---- 累积（阶段 7） ----
            // HDRP 公式: Pow(2, Lerp(0, -7, accumulationFactor))
            //   默认 0.75 → 2^-5.25 ≈ 0.026 (新帧) / 0.974 (历史)
            // 前 3 帧强制 accumulationAmount = 1.0（完全使用当前帧，不累积）
            float accumulationAmount;
            if (m_FrameCount < 3)
                accumulationAmount = 1.0f;
            else
                accumulationAmount = Mathf.Pow(2.0f, Mathf.Lerp(0.0f, -7.0f, ssr.accumulationFactor.value));
            cmd.SetGlobalFloat(SsrAccumulationAmountId, accumulationAmount);

            // ---- 速度拒绝（阶段 7） ----
            // HDRP 逻辑:
            //   - WorldSpeed + HardThreshold: 取反 speedRejectionParam → 用于 threshold comparison
            //   - 其他模式: 直接使用 speedRejectionParam
            var speedRejectionParam = ssr.speedRejectionParam.value;
            if (ssr.enableWorldSpeedRejection.value && !ssr.speedSmoothReject.value)
                speedRejectionParam = Mathf.Clamp01(1.0f - speedRejectionParam);
            cmd.SetGlobalFloat(SsrPbrSpeedRejectionId, speedRejectionParam);

            // HDRP: speedRejectionScalerFactor 经平方后再使用: Pow(value * 0.1, 2)
            var scalerFactor = Mathf.Pow(ssr.speedRejectionScalerFactor.value * 0.1f, 2.0f);
            cmd.SetGlobalFloat(SsrPrbSpeedRejectionScalerFactorId, scalerFactor);

            // ---- PBR Roughness Bias（阶段 7，之前缺失） ----
            cmd.SetGlobalFloat(SsrPbrBiasId, ssr.biasFactor.value);
        }

        /// <summary>
        ///     按需分配 / 重建 RT Handle（分辨率变化时调用）。
        /// </summary>
        private void EnsureTextures(RenderTextureDescriptor cameraDesc)
        {
            var desc = cameraDesc;
            desc.msaaSamples = 1;
            desc.depthBufferBits = 0;

            // _SsrHitPointTexture: R32G32_SFloat（存储命中 NDC xy）
            desc.graphicsFormat = GraphicsFormat.R32G32_SFloat;
            desc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref m_HitPointTexture, desc,
                FilterMode.Point, TextureWrapMode.Clamp, name: "SsrHitPointTexture");

            // _SsrAccumTexture: R16G16B16A16_SFloat（存储反射颜色 + 不透明度，当前帧输出）
            desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            desc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref m_AccumTexture, desc,
                FilterMode.Bilinear, TextureWrapMode.Clamp, name: "SsrAccumTexture");

            // _SsrHistoryTexture: 与 Accum 同格式，存储上一帧累积结果（阶段 7）
            // 注意：ReAllocateIfNeeded 仅在尺寸变化时重建，尺寸不变时保留历史内容
            RenderingUtils.ReAllocateIfNeeded(ref m_HistoryTexture, desc,
                FilterMode.Bilinear, TextureWrapMode.Clamp, name: "SsrHistoryTexture");
        }

        private static int DivRoundUp(int x, int y)
        {
            return (x + y - 1) / y;
        }
    }
}