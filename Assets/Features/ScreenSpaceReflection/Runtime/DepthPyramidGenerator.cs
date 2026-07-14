using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    ///     打包的 Mip Chain 信息，管理各 mip 层级在 Atlas 纹理中的偏移和大小。
    ///     从 HDRP HDUtils.PackedMipChainInfo 迁移。
    /// </summary>
    public struct DepthPyramidMipChainInfo
    {
        public Vector2Int textureSize;
        public int mipLevelCount;
        public Vector2Int[] mipLevelSizes;
        public Vector2Int[] mipLevelOffsets;

        private Vector2 cachedTextureScale;
        private Vector2Int cachedHardwareTextureSize;
        private bool offsetBufferDirty;

        private static readonly int MaxMipLevels = 15;

        public void Allocate()
        {
            mipLevelOffsets = new Vector2Int[MaxMipLevels];
            mipLevelSizes = new Vector2Int[MaxMipLevels];
            offsetBufferDirty = true;
        }

        /// <summary>
        ///     计算打包 mip chain 布局。
        ///     所有 mip 层级打包到单一纹理中，避免逐级 mip 的限制。
        /// </summary>
        public void ComputePackedMipChainInfo(Vector2Int viewportSize)
        {
            var drsEnabled = DynamicResolutionHandler.instance != null &&
                             DynamicResolutionHandler.instance.HardwareDynamicResIsEnabled();
            var hwSize = drsEnabled
                ? DynamicResolutionHandler.instance.ApplyScalesOnSize(viewportSize)
                : viewportSize;
            var texScale = drsEnabled
                ? new Vector2((float)viewportSize.x / hwSize.x, (float)viewportSize.y / hwSize.y)
                : new Vector2(1.0f, 1.0f);

            if (cachedHardwareTextureSize == hwSize && cachedTextureScale == texScale)
                return;

            cachedHardwareTextureSize = hwSize;
            cachedTextureScale = texScale;

            mipLevelSizes[0] = hwSize;
            mipLevelOffsets[0] = Vector2Int.zero;

            var mipLevel = 0;
            var mipSize = hwSize;

            do
            {
                mipLevel++;
                mipSize.x = Math.Max(1, (mipSize.x + 1) >> 1);
                mipSize.y = Math.Max(1, (mipSize.y + 1) >> 1);

                mipLevelSizes[mipLevel] = mipSize;

                var prevMipBegin = mipLevelOffsets[mipLevel - 1];
                var prevMipEnd = prevMipBegin + mipLevelSizes[mipLevel - 1];

                var mipBegin = Vector2Int.zero;
                if ((mipLevel & 1) != 0) // Odd mip level
                {
                    mipBegin.x = prevMipBegin.x;
                    mipBegin.y = prevMipEnd.y;
                }
                else // Even mip level
                {
                    mipBegin.x = prevMipEnd.x;
                    mipBegin.y = prevMipBegin.y;
                }

                mipLevelOffsets[mipLevel] = mipBegin;

                hwSize.x = Math.Max(hwSize.x, mipBegin.x + mipSize.x);
                hwSize.y = Math.Max(hwSize.y, mipBegin.y + mipSize.y);
            } while (mipSize.x > 1 || mipSize.y > 1);

            textureSize = new Vector2Int(
                Mathf.CeilToInt(hwSize.x * texScale.x),
                Mathf.CeilToInt(hwSize.y * texScale.y));

            mipLevelCount = mipLevel + 1;
            offsetBufferDirty = true;
        }

        public ComputeBuffer GetOffsetBufferData(ComputeBuffer buffer)
        {
            if (offsetBufferDirty)
            {
                buffer.SetData(mipLevelOffsets);
                offsetBufferDirty = false;
            }

            return buffer;
        }
    }

    /// <summary>
    ///     Depth Pyramid (Hi-Z) 生成器。
    ///     将 Camera Depth Texture 生成逐级最小深度金字塔，供 SSR 射线行进使用。
    /// </summary>
    public class DepthPyramidGenerator : IDisposable
    {
        // Shader property IDs
        private static readonly int SrcOffsetAndLimit = Shader.PropertyToID("_SrcOffsetAndLimit");
        private static readonly int DstOffset = Shader.PropertyToID("_DstOffset");
        private static readonly int DepthMipChain = Shader.PropertyToID("_DepthMipChain");
        private static readonly int DepthPyramidMipLevelOffsets = Shader.PropertyToID("_DepthPyramidMipLevelOffsets");
        private static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
        private readonly ComputeShader m_ComputeShader;

        private readonly int m_DownsampleKernel;
        private readonly int m_CopyKernel;
        private readonly int[] m_DstOffset = new int[4];
        private DepthPyramidMipChainInfo m_MipChainInfo;
        private ComputeBuffer m_MipLevelOffsetsBuffer;

        private readonly int[] m_SrcOffset = new int[4];

        public DepthPyramidGenerator(ComputeShader computeShader)
        {
            m_ComputeShader = computeShader;
            m_DownsampleKernel = computeShader.FindKernel("KDepthDownsample8");
            m_CopyKernel = computeShader.FindKernel("KDepthCopy");
            m_MipChainInfo.Allocate();
            m_MipLevelOffsetsBuffer = new ComputeBuffer(15, sizeof(int) * 2);
        }

        public ComputeBuffer MipLevelOffsetsBuffer => m_MipChainInfo.GetOffsetBufferData(m_MipLevelOffsetsBuffer);
        public RTHandle DepthPyramidRT { get; private set; }

        public int MipLevelCount => m_MipChainInfo.mipLevelCount;

        public void Dispose()
        {
            ReleaseTexture();
            if (m_MipLevelOffsetsBuffer != null)
            {
                m_MipLevelOffsetsBuffer.Release();
                m_MipLevelOffsetsBuffer = null;
            }
        }

        /// <summary>
        ///     确保 Depth Pyramid 纹理尺寸匹配当前视口。在需要时重建。
        /// </summary>
        public void EnsureTexture(Vector2Int viewportSize)
        {
            m_MipChainInfo.ComputePackedMipChainInfo(viewportSize);

            var requiredSize = m_MipChainInfo.textureSize;

            if (DepthPyramidRT == null ||
                DepthPyramidRT.rt.width != requiredSize.x ||
                DepthPyramidRT.rt.height != requiredSize.y)
            {
                ReleaseTexture();

                DepthPyramidRT = RTHandles.Alloc(
                    requiredSize.x, requiredSize.y,
                    dimension: TextureDimension.Tex2D,
                    colorFormat: GraphicsFormat.R32_SFloat,
                    filterMode: FilterMode.Point,
                    enableRandomWrite: true,
                    useMipMap: false,
                    autoGenerateMips: false,
                    name: "DepthPyramid"
                );
            }
        }

        /// <summary>
        ///     生成 Depth Pyramid。
        ///     渲染流程：
        ///     1. Dispatch KDepthCopy：从 _CameraDepthTexture 读入金字塔 mip 0
        ///     2. Dispatch KDepthDownsample8：逐级 downscale
        ///     全程使用 compute shader 读写，不依赖 CopyTexture（避免
        ///     D32_SFloat_S8_UInt → R32_SFloat 格式不兼容）。
        /// </summary>
        public void Generate(CommandBuffer cmd, Vector2Int viewportSize)
        {
            EnsureTexture(viewportSize);

            var cs = m_ComputeShader;
            var mip0Size = m_MipChainInfo.mipLevelSizes[0];

            // Step 1: KDepthCopy 将 _CameraDepthTexture 复制到金字塔 mip 0
            cmd.SetComputeTextureParam(cs, m_CopyKernel, CameraDepthTexture,
                Shader.GetGlobalTexture(CameraDepthTexture));
            cmd.SetComputeTextureParam(cs, m_CopyKernel, DepthMipChain, DepthPyramidRT);
            cmd.DispatchCompute(cs, m_CopyKernel,
                DivRoundUp(mip0Size.x, 8),
                DivRoundUp(mip0Size.y, 8),
                1);

            // Step 2: 逐级 downsample
            var kernel = m_DownsampleKernel;

            for (var i = 1; i < m_MipChainInfo.mipLevelCount; i++)
            {
                var dstSize = m_MipChainInfo.mipLevelSizes[i];
                var dstOffset = m_MipChainInfo.mipLevelOffsets[i];
                var srcSize = m_MipChainInfo.mipLevelSizes[i - 1];
                var srcOffset = m_MipChainInfo.mipLevelOffsets[i - 1];
                var srcLimit = srcOffset + srcSize - Vector2Int.one;

                m_SrcOffset[0] = srcOffset.x;
                m_SrcOffset[1] = srcOffset.y;
                m_SrcOffset[2] = srcLimit.x;
                m_SrcOffset[3] = srcLimit.y;

                m_DstOffset[0] = dstOffset.x;
                m_DstOffset[1] = dstOffset.y;
                m_DstOffset[2] = 0;
                m_DstOffset[3] = 0;

                cmd.SetComputeIntParams(cs, SrcOffsetAndLimit, m_SrcOffset);
                cmd.SetComputeIntParams(cs, DstOffset, m_DstOffset);
                cmd.SetComputeTextureParam(cs, kernel, DepthMipChain, DepthPyramidRT);

                cmd.DispatchCompute(cs, kernel,
                    DivRoundUp(dstSize.x, 8),
                    DivRoundUp(dstSize.y, 8),
                    1);
            }
        }

        /// <summary>
        ///     绑定 Depth Pyramid 到全局 shader 属性，供 SSR 着色器使用。
        /// </summary>
        public void SetGlobalTextures(CommandBuffer cmd)
        {
            cmd.SetGlobalTexture(DepthMipChain, DepthPyramidRT);
            cmd.SetGlobalBuffer(DepthPyramidMipLevelOffsets, MipLevelOffsetsBuffer);
        }

        private void ReleaseTexture()
        {
            if (DepthPyramidRT != null)
            {
                DepthPyramidRT.Release();
                DepthPyramidRT = null;
            }
        }

        private static int DivRoundUp(int x, int y)
        {
            return (x + y - 1) / y;
        }
    }
}