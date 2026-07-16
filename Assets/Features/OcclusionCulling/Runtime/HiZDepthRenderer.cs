// Hi-Z Depth Renderer — hooks into URP pipeline.

using UnityEngine;
using UnityEngine.Rendering;

namespace GPUDrivenOcclusion
{
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(100)]
    public class HiZDepthRenderer : MonoBehaviour
    {
        [SerializeField] private ComputeShader computeShader;
        [SerializeField] private OcclusionCullingSystem occlusionSystem;

        private Camera m_Camera;
        private HiZDepthGenerator m_Generator;
        private bool m_HasHiZ;

        private void Awake()
        {
            m_Camera = GetComponent<Camera>();
        }

        private void OnEnable()
        {
            if (m_Generator == null && computeShader != null)
                m_Generator = new HiZDepthGenerator(computeShader);

            if (occlusionSystem == null)
                occlusionSystem = OcclusionCullingSystem.Instance;

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering   += OnEndCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering   -= OnEndCameraRendering;
            m_Generator?.ReleaseTexture();
            m_HasHiZ = false;
        }

        private void OnDestroy()
        {
            m_Generator?.Dispose();
            m_Generator = null;
        }

        // ---- Begin: dispatch culling with last frame's Hi-Z ----
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (cam != m_Camera || occlusionSystem == null || !m_HasHiZ) return;

            var cmd = CommandBufferPool.Get("OcclusionCulling");
            occlusionSystem.DispatchCulling(cam, cmd);
            context.ExecuteCommandBuffer(cmd);
            context.Submit();
            CommandBufferPool.Release(cmd);
        }

        // ---- End: generate Hi-Z for next frame ----
        private void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (cam != m_Camera || m_Generator == null) return;

            var cmd = CommandBufferPool.Get("HiZDepth");
            m_Generator.Generate(cmd, m_Camera);
            context.ExecuteCommandBuffer(cmd);
            context.Submit();
            CommandBufferPool.Release(cmd);

            occlusionSystem.hiZHandle = m_Generator.HiZHandle;
            m_HasHiZ = true;
        }
    }
}
