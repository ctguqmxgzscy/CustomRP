// Hi-Z Depth Pyramid Generator — GPUI-style standard mip chain.

using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GPUDrivenOcclusion
{
    public class HiZDepthGenerator : IDisposable
    {
        private static readonly int SID_HiZDepthMipN = Shader.PropertyToID("_HiZDepthMipN");
        private static readonly int SID_HiZDepthSource = Shader.PropertyToID("_HiZDepthSource");
        private static readonly int SID_HiZTextureSize  = Shader.PropertyToID("_HiZTextureSize");
        private static readonly int SID_HiZDstMipSize   = Shader.PropertyToID("_HiZDstMipSize");
        private static readonly int SID_HiZViewportSize = Shader.PropertyToID("_HiZViewportSize");
        private static readonly int SID_ReverseZ        = Shader.PropertyToID("_ReverseZ");

        private readonly ComputeShader m_CS;
        private readonly int m_KernelClear;
        private readonly int m_KernelCopy;
        private readonly int m_KernelReduce;
        private int m_CurrentSize;

        private RTHandle m_HiZHandle;
        private RenderTargetIdentifier m_HiZRID;

        public HiZDepthGenerator(ComputeShader cs)
        {
            m_CS = cs;
            m_KernelClear  = cs.FindKernel("KClear");
            m_KernelCopy   = cs.FindKernel("KDepthCopy");
            m_KernelReduce = cs.FindKernel("KDepthReduce");
        }

        public RTHandle HiZHandle => m_HiZHandle;

        public void Dispose()
        {
            ReleaseTexture();
        }

        public void EnsureTexture(int viewportWidth, int viewportHeight)
        {
            var size = Mathf.NextPowerOfTwo(Mathf.Max(viewportWidth, viewportHeight));
            if (m_CurrentSize == size) return;

            var desc = new RenderTextureDescriptor(size, size, GraphicsFormat.R32_SFloat, 0)
            {
                useMipMap = true,
                autoGenerateMips = false,
                enableRandomWrite = true
            };

            RenderingUtils.ReAllocateIfNeeded(ref m_HiZHandle, desc, FilterMode.Point, TextureWrapMode.Clamp,
                name: "HiZDepth");

            m_HiZRID = new RenderTargetIdentifier(m_HiZHandle);
            m_CurrentSize = size;
        }

        public void Generate(CommandBuffer cmd, Camera camera)
        {
            EnsureTexture(camera.pixelWidth, camera.pixelHeight);

            var mipCount = m_HiZHandle.rt.mipmapCount;
            var size = m_CurrentSize;

            // Clear mip 0（避免残留值污染第一次采样）
            cmd.SetComputeTextureParam(m_CS, m_KernelClear, SID_HiZDepthMipN, m_HiZRID, 0);
            cmd.SetComputeIntParam(m_CS, SID_HiZTextureSize, size);
            cmd.DispatchCompute(m_CS, m_KernelClear, DivUp(size, 8), DivUp(size, 8), 1);

            // Mip 0: copy _CameraDepthTexture → HiZ (UV mapping fills entire PoT texture)
            cmd.SetComputeTextureParam(m_CS, m_KernelCopy, SID_HiZDepthMipN, m_HiZRID, 0);
            cmd.SetComputeIntParam(m_CS, SID_HiZTextureSize, size);
            cmd.SetComputeIntParams(m_CS, SID_HiZViewportSize,
                new[] { camera.pixelWidth, camera.pixelHeight });
            cmd.SetComputeIntParam(m_CS, SID_ReverseZ, SystemInfo.usesReversedZBuffer ? 1 : 0);
            cmd.DispatchCompute(m_CS, m_KernelCopy, DivUp(size, 8), DivUp(size, 8), 1);

            // Mip 1..N: reduce
            for (var mip = 1; mip < mipCount; mip++)
            {
                var dstSize = Mathf.Max(1, size >> mip);
                cmd.SetComputeTextureParam(m_CS, m_KernelReduce, SID_HiZDepthSource, m_HiZRID, mip - 1);
                cmd.SetComputeTextureParam(m_CS, m_KernelReduce, SID_HiZDepthMipN, m_HiZRID, mip);
                cmd.SetComputeIntParam(m_CS, SID_HiZDstMipSize, dstSize);
                cmd.DispatchCompute(m_CS, m_KernelReduce, DivUp(dstSize, 8), DivUp(dstSize, 8), 1);
            }
        }

        public void ReleaseTexture()
        {
            if (m_HiZHandle != null)
            {
                m_HiZHandle.Release();
                m_HiZHandle = null;
            }

            m_CurrentSize = 0;
        }

        private static int DivUp(int x, int y)
        {
            return (x + y - 1) / y;
        }
    }
}