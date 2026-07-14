using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    ///     Color Pyramid 生成器。
    ///     将当前帧的 _CameraOpaqueTexture 生成逐级 mip 链（Color Pyramid），
    ///     供 SSR 下一帧按粗糙度采样不同 mip level 的模糊反射颜色。
    ///     使用双缓冲：current（本帧生成） / previous（上帧，供 SSR 采样）。
    /// </summary>
    public class ColorPyramidGenerator : IDisposable
    {
        // Shader property IDs
        private static readonly int ColorPyramidRW = Shader.PropertyToID("_ColorPyramidRW");
        private static readonly int CameraOpaqueTexture = Shader.PropertyToID("_CameraOpaqueTexture");

        // Gaussian pyramid shader properties（预留给未来的 Gaussian blur 路径）
        private static readonly int SrcTextureId = Shader.PropertyToID("_SrcTexture");
        private static readonly int SrcMipLevelId = Shader.PropertyToID("_SrcMipLevel");
        private static readonly int SrcSizeId = Shader.PropertyToID("_SrcSize");
        private static readonly int DstTextureId = Shader.PropertyToID("_DstTexture");

        private readonly ComputeShader m_ComputeShader;
        private readonly int m_CopyKernel;
        private readonly int m_DownsampleKernel;
        private readonly int m_GaussianHKernel;
        private readonly int m_GaussianVKernel;
        private readonly bool m_UseGaussianPyramid;

        private RTHandle m_CurrentPyramid;
        private RTHandle m_PreviousPyramid;

        // Gaussian 路径临时 RT（ping-pong），按需分配
        private RTHandle m_TempRT0;
        private RTHandle m_TempRT1;
        private Vector2Int m_TempRTSize;

        public ColorPyramidGenerator(ComputeShader computeShader)
        {
            m_ComputeShader = computeShader;
            m_CopyKernel = computeShader.FindKernel("KColorPyramidCopy");
            m_DownsampleKernel = computeShader.FindKernel("KColorDownsample");
            m_GaussianHKernel = computeShader.FindKernel("KColorGaussianH");
            m_GaussianVKernel = computeShader.FindKernel("KColorGaussianV");
            m_UseGaussianPyramid = m_DownsampleKernel >= 0
                                   && m_GaussianHKernel >= 0
                                   && m_GaussianVKernel >= 0;
        }

        /// <summary>上一帧生成的 Color Pyramid，供本帧 SSR 采样。</summary>
        public RTHandle PreviousColorPyramidRT => m_PreviousPyramid;

        /// <summary>当前 Color Pyramid 的 mip 层级数。</summary>
        public int MipCount { get; private set; }

        public void Dispose()
        {
            ReleaseTexture(ref m_CurrentPyramid);
            ReleaseTexture(ref m_PreviousPyramid);
            ReleaseTempRTs();
        }

        /// <summary>
        ///     确保 Color Pyramid 纹理尺寸匹配当前视口。在需要时重建。
        /// </summary>
        public void EnsureTexture(Vector2Int viewportSize)
        {
            if (m_CurrentPyramid != null
                && m_CurrentPyramid.rt.width == viewportSize.x
                && m_CurrentPyramid.rt.height == viewportSize.y)
                return;

            ReleaseTexture(ref m_CurrentPyramid);
            ReleaseTexture(ref m_PreviousPyramid);

            var desc = new RenderTextureDescriptor(viewportSize.x, viewportSize.y,
                GraphicsFormat.R16G16B16A16_SFloat, 0);
            desc.useMipMap = true;
            desc.autoGenerateMips = false;
            desc.enableRandomWrite = true;
            desc.msaaSamples = 1;

            m_CurrentPyramid = RTHandles.Alloc(desc,
                FilterMode.Trilinear, TextureWrapMode.Clamp,
                name: "ColorPyramid");
            m_PreviousPyramid = RTHandles.Alloc(desc,
                FilterMode.Trilinear, TextureWrapMode.Clamp,
                name: "ColorPyramidPrev");

            MipCount = desc.mipCount;
        }

        /// <summary>
        ///     从当前帧不透明颜色纹理生成 Color Pyramid。
        ///     1. KColorPyramidCopy: 将 _CameraOpaqueTexture 复制到 mip 0
        ///     2. Gaussian 路径: 逐 mip 执行 Downsample → H-Blur → V-Blur（9-tap）
        ///        若 Gaussian kernel 不可用则回退到硬件 GenerateMips（box filter）
        /// </summary>
        public void Generate(CommandBuffer cmd, Vector2Int viewportSize)
        {
            EnsureTexture(viewportSize);

            var cs = m_ComputeShader;

            // Step 1: Copy _CameraOpaqueTexture → Color Pyramid mip 0 (via UAV write)
            cmd.SetComputeTextureParam(cs, m_CopyKernel, CameraOpaqueTexture,
                Shader.GetGlobalTexture(CameraOpaqueTexture));
            cmd.SetComputeTextureParam(cs, m_CopyKernel, ColorPyramidRW, m_CurrentPyramid);
            cmd.DispatchCompute(cs, m_CopyKernel,
                DivRoundUp(viewportSize.x, 8),
                DivRoundUp(viewportSize.y, 8),
                1);

            // Step 2: Generate mip chain
            if (m_UseGaussianPyramid && MipCount > 1)
                GenerateGaussianMips(cmd, viewportSize);
            else
                cmd.GenerateMips(m_CurrentPyramid);
        }

        /// <summary>
        ///     Gaussian mip chain 生成（对齐 HDRP ColorPyramidPS 9-tap）。
        ///     每级 mip 三步骤: Downsample(2×2 box) → Horizontal Gaussian → Vertical Gaussian。
        ///     使用两个临时 RT 做 ping-pong，避免同一次 dispatch 内 SRV/UAV 冲突。
        /// </summary>
        private void GenerateGaussianMips(CommandBuffer cmd, Vector2Int viewportSize)
        {
            var cs = m_ComputeShader;
            EnsureTempRTs(viewportSize);

            for (int mip = 1; mip < MipCount; mip++)
            {
                var srcSize = new Vector2Int(
                    Mathf.Max(1, viewportSize.x >> (mip - 1)),
                    Mathf.Max(1, viewportSize.y >> (mip - 1)));
                var dstSize = new Vector2Int(
                    Mathf.Max(1, viewportSize.x >> mip),
                    Mathf.Max(1, viewportSize.y >> mip));

                // --- 2a. Downsample: Color Pyramid mip N-1 → tempRT0 ---
                cmd.SetComputeTextureParam(cs, m_DownsampleKernel, SrcTextureId, m_CurrentPyramid);
                cmd.SetComputeIntParam(cs, SrcMipLevelId, mip - 1);
                cmd.SetComputeVectorParam(cs, SrcSizeId,
                    new Vector4(srcSize.x, srcSize.y, 1.0f / srcSize.x, 1.0f / srcSize.y));
                cmd.SetComputeTextureParam(cs, m_DownsampleKernel, DstTextureId, m_TempRT0);
                cmd.DispatchCompute(cs, m_DownsampleKernel,
                    DivRoundUp(dstSize.x, 8),
                    DivRoundUp(dstSize.y, 8),
                    1);

                // --- 2b. Horizontal Gaussian: tempRT0 → tempRT1 ---
                var dstSizeVec = new Vector4(dstSize.x, dstSize.y,
                    1.0f / dstSize.x, 1.0f / dstSize.y);
                cmd.SetComputeTextureParam(cs, m_GaussianHKernel, SrcTextureId, m_TempRT0);
                cmd.SetComputeIntParam(cs, SrcMipLevelId, 0);
                cmd.SetComputeVectorParam(cs, SrcSizeId, dstSizeVec);
                cmd.SetComputeTextureParam(cs, m_GaussianHKernel, DstTextureId, m_TempRT1);
                cmd.DispatchCompute(cs, m_GaussianHKernel,
                    DivRoundUp(dstSize.x, 8),
                    DivRoundUp(dstSize.y, 8),
                    1);

                // --- 2c. Vertical Gaussian: tempRT1 → Color Pyramid mip N ---
                cmd.SetComputeTextureParam(cs, m_GaussianVKernel, SrcTextureId, m_TempRT1);
                cmd.SetComputeIntParam(cs, SrcMipLevelId, 0);
                cmd.SetComputeVectorParam(cs, SrcSizeId, dstSizeVec);
                cmd.SetComputeTextureParam(cs, m_GaussianVKernel, DstTextureId,
                    m_CurrentPyramid, mip);
                cmd.DispatchCompute(cs, m_GaussianVKernel,
                    DivRoundUp(dstSize.x, 8),
                    DivRoundUp(dstSize.y, 8),
                    1);
            }
        }

        /// <summary>
        ///     绑定上一帧的 Color Pyramid 为全局纹理，供非 compute shader 使用。
        ///     对于 compute shader，使用 BindReadPyramid / BindWritePyramid。
        /// </summary>
        public void SetGlobalTextures(CommandBuffer cmd)
        {
            if (m_PreviousPyramid != null)
                cmd.SetGlobalTexture(ColorPyramidRW, m_PreviousPyramid);
        }

        /// <summary>
        ///     将上一帧 Color Pyramid 绑定到 compute shader 的指定 kernel（作为 SRV，只读）。
        /// </summary>
        public void BindReadPyramid(CommandBuffer cmd, ComputeShader cs, int kernel, int propertyId)
        {
            if (m_PreviousPyramid != null)
                cmd.SetComputeTextureParam(cs, kernel, propertyId, m_PreviousPyramid);
        }

        /// <summary>
        ///     将当前帧 Color Pyramid 绑定到 compute shader 的指定 kernel（作为 UAV，写入 mip 0）。
        /// </summary>
        public void BindWritePyramid(CommandBuffer cmd, ComputeShader cs, int kernel, int propertyId)
        {
            if (m_CurrentPyramid != null)
                cmd.SetComputeTextureParam(cs, kernel, propertyId, m_CurrentPyramid);
        }

        /// <summary>
        ///     交换当前 ↔ 上一帧金字塔（每帧末尾调用）。
        /// </summary>
        public void Swap()
        {
            (m_CurrentPyramid, m_PreviousPyramid) = (m_PreviousPyramid, m_CurrentPyramid);
        }

        /// <summary>
        ///     确保临时 RT 尺寸足够（在最大中间 mip 尺寸处分配）。
        ///     高斯路径需要两个无 mip 的临时纹理做 ping-pong。
        /// </summary>
        private void EnsureTempRTs(Vector2Int viewportSize)
        {
            // 最大临时纹理 = mip 1 的尺寸（viewportSize / 2）
            var maxSize = new Vector2Int(
                Mathf.Max(1, viewportSize.x / 2),
                Mathf.Max(1, viewportSize.y / 2));

            if (m_TempRT0 != null && m_TempRTSize == maxSize)
                return;

            ReleaseTempRTs();

            var desc = new RenderTextureDescriptor(maxSize.x, maxSize.y,
                GraphicsFormat.R16G16B16A16_SFloat, 0);
            desc.useMipMap = false;
            desc.autoGenerateMips = false;
            desc.enableRandomWrite = true;
            desc.msaaSamples = 1;

            m_TempRT0 = RTHandles.Alloc(desc,
                FilterMode.Point, TextureWrapMode.Clamp,
                name: "ColorPyramidTemp0");
            m_TempRT1 = RTHandles.Alloc(desc,
                FilterMode.Point, TextureWrapMode.Clamp,
                name: "ColorPyramidTemp1");
            m_TempRTSize = maxSize;
        }

        private void ReleaseTempRTs()
        {
            ReleaseTexture(ref m_TempRT0);
            ReleaseTexture(ref m_TempRT1);
            m_TempRTSize = Vector2Int.zero;
        }

        private static void ReleaseTexture(ref RTHandle handle)
        {
            if (handle != null)
            {
                handle.Release();
                handle = null;
            }
        }

        private static int DivRoundUp(int x, int y)
        {
            return (x + y - 1) / y;
        }
    }
}
