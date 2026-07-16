// GPU-Driven Occlusion Culling — System Manager
// Manages occludee registration, GPU buffer lifecycle, and culling dispatch

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GPUDrivenOcclusion
{
    /// <summary>
    ///     Singleton manager for GPU-driven static &amp; dynamic object occlusion culling.
    ///     Maintains a pool of occludees, uploads their bounds to GPU,
    ///     dispatches frustum + Hi-Z culling, and reads back visibility results.
    ///     Dynamic occludees have their bounds refreshed every frame.
    ///
    ///     TODO: occluder/occludee 分离——大物体（墙、地面）写深度到 Hi-Z，
    ///     小物体（碎片、道具）只被剔除、不贡献遮挡。当前所有物体既是
    ///     occluder 也是 occludee，静态场景够用，复杂场景需要分层以减少
    ///     遮挡物数量并提升精度。
    ///
    ///     TODO: 间接绘制（Indirect Draw）支持——草、植被等大批量 instance
    ///     无法走 CPU 回读路径，需新增 GPU 侧输出：culling compute 完成后
    ///     将 _OccludeeVisibility 直接用于后续 indirect dispatch（写入
    ///     DrawCommand.count 或 append visible index），不经过 CPU。关键
    ///     约束：草系统的 indirect kernel 必须与 culling 在同一条 CommandBuffer
    ///     内同帧执行，context.Submit() 一次提交。
    /// </summary>
    [DefaultExecutionOrder(500)]
    public class OcclusionCullingSystem : MonoBehaviour
    {
        private static readonly int STRIDE_FLOAT4 = 16; // sizeof(float4)
        private static readonly int STRIDE_UINT = 4;

        // -----------------------------------------------------------------
        // Camera reference (for VP)
        // -----------------------------------------------------------------

        // -----------------------------------------------------------------
        // Shader property IDs (cached)
        // -----------------------------------------------------------------
        private static readonly int _OccludeeBoundsBuffer = Shader.PropertyToID("_OccludeeBounds");
        private static readonly int _OccludeeVisibility = Shader.PropertyToID("_OccludeeVisibility");
        private static readonly int _OccludeeCount = Shader.PropertyToID("_OccludeeCount");
        private static readonly int _CullingVP = Shader.PropertyToID("_CullingVP");
        private static readonly int _FrustumOcclusionParams = Shader.PropertyToID("_FrustumOcclusionParams");
        private static readonly int SID_HiZDepth  = Shader.PropertyToID("_HiZDepth");
        private static readonly int SID_DebugMode = Shader.PropertyToID("_DebugMode");

        // -----------------------------------------------------------------
        // Settings
        // -----------------------------------------------------------------
        [Header("Culling")] [Tooltip("Frustum offset for conservative culling (0 = exact).")] [Range(0f, 0.1f)]
        public float frustumOffset = 0.01f;

        [Tooltip("Occlusion depth bias for conservative culling.")] [Range(0f, 0.05f)]
        public float occlusionDepthBias = 0.005f;

        [Tooltip("UV rect padding (shrink rect to avoid boundary artifacts).")] [Range(0f, 0.05f)]
        public float rectPadding = 0.005f;

        [Tooltip("Occlusion accuracy: 1 = 5 samples, 2 = 9 samples, 3 = 17 samples.")] [Range(1, 3)]
        public int occlusionAccuracy = 2;

        [Tooltip("Frames to wait before hiding an occluded object (reduces flicker).")] [Range(0, 5)]
        public int hideDelayFrames = 3;

        public enum DebugMode { Full, FrustumOnly, OcclusionOnly }
        [Header("Debug")] public DebugMode debugMode = DebugMode.Full;

        [Header("Compute")] [SerializeField] private ComputeShader cullingShader;

        private readonly List<int> m_FreeSlots = new(); // recycled slots for fast add/remove
        private readonly Dictionary<int, int> m_RendererToIndex = new(); // instanceID → occludee index
        private readonly HashSet<int> m_DynamicIndices = new(); // which occludee slots need per-frame bounds refresh

        /// <summary>Hi-Z RTHandle from HiZDepthGenerator (standard mip chain).</summary>
        [NonSerialized] public RTHandle hiZHandle;

        // -----------------------------------------------------------------
        // GPU Buffers
        // -----------------------------------------------------------------
        private ComputeBuffer m_BoundsBuffer; // StructuredBuffer<float4>[count*2] — center + extents
        private bool m_BoundsDirty;

        private int m_BufferCapacity;
        private int[] m_OccludedFrameCounter; // per-occludee counter for delayed hide

        // -----------------------------------------------------------------
        // Occludee storage (CPU side)
        // -----------------------------------------------------------------
        private readonly List<OccludeeDesc> m_Occludees = new();
        private bool m_ReadbackPending;
        private ComputeBuffer m_VisibilityBuffer; // RWStructuredBuffer<uint>[count] — visibility

        // -----------------------------------------------------------------
        // Result readback
        // -----------------------------------------------------------------
        private uint[] m_VisibilityResultCPU;

        // -----------------------------------------------------------------
        // Singleton
        // -----------------------------------------------------------------
        public static OcclusionCullingSystem Instance { get; private set; }

        public int OccludeeCount => m_Occludees.Count - m_FreeSlots.Count;

        /// <summary>Editor: get all registered renderers (includes dead slots as null).</summary>
        public Renderer[] GetAllRenderers()
        {
            var result = new Renderer[m_Occludees.Count];
            for (int i = 0; i < m_Occludees.Count; i++)
                result[i] = m_Occludees[i].renderer;
            return result;
        }

        // -----------------------------------------------------------------
        // Lifetime
        // -----------------------------------------------------------------
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;

            if (cullingShader == null)
                Debug.LogWarning("[OcclusionCulling] Compute shader not assigned. Waiting for assignment.");
        }

        private void Start()
        {
            if (OccludeeCount == 0)
                ScanScene();
        }

        private void OnDestroy()
        {
            ReleaseBuffers();
            if (Instance == this) Instance = null;
        }

        /// <summary>Editor/Runtime: scan scene for renderers and register them.
        /// Set onlyStatic=false to include dynamic objects.</summary>
        public int ScanScene(bool onlyStatic = true, bool skipParticles = true)
        {
            int count = 0;
            foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer && skipParticles) continue;
                    if (onlyStatic && !r.gameObject.isStatic) continue;
                    Register(r);
                    count++;
                }
            }
            return count;
        }

        // -----------------------------------------------------------------
        // Debug
        // -----------------------------------------------------------------
        private void OnDrawGizmosSelected()
        {
            if (m_Occludees == null) return;
            Gizmos.color = new Color(0, 1, 0, 0.15f);
            foreach (var o in m_Occludees)
                if (o.renderer != null && o.renderer.enabled)
                    Gizmos.DrawWireCube(o.worldBounds.center, o.worldBounds.size);
        }

        // -----------------------------------------------------------------
        // Registration API
        // -----------------------------------------------------------------

        /// <summary>Register a Renderer for GPU occlusion culling.</summary>
        public void Register(Renderer r)
        {
            RegisterInternal(r, false);
        }

        /// <summary>Register a dynamic Renderer — bounds refreshed every frame.</summary>
        public void RegisterDynamic(Renderer r)
        {
            RegisterInternal(r, true);
        }

        private void RegisterInternal(Renderer r, bool isDynamic)
        {
            if (r == null) return;
            var id = r.GetInstanceID();
            if (m_RendererToIndex.ContainsKey(id)) return; // already registered

            int index;
            if (m_FreeSlots.Count > 0)
            {
                index = m_FreeSlots[m_FreeSlots.Count - 1];
                m_FreeSlots.RemoveAt(m_FreeSlots.Count - 1);
                m_Occludees[index] = new OccludeeDesc(r);
            }
            else
            {
                index = m_Occludees.Count;
                m_Occludees.Add(new OccludeeDesc(r));
            }

            m_RendererToIndex[id] = index;
            if (isDynamic)
                m_DynamicIndices.Add(index);
            m_BoundsDirty = true;
        }

        /// <summary>Unregister a Renderer from occlusion culling.</summary>
        public void Unregister(Renderer r)
        {
            if (r == null) return;
            var id = r.GetInstanceID();
            if (!m_RendererToIndex.TryGetValue(id, out var index)) return;

            m_RendererToIndex.Remove(id);
            m_FreeSlots.Add(index);
            m_Occludees[index] = default;
            m_DynamicIndices.Remove(index);
            m_BoundsDirty = true;

            // Always re-enable on unregister
            r.enabled = true;
        }

        /// <summary>Update bounds for a registered renderer (e.g., after transform change).</summary>
        public void UpdateBounds(Renderer r)
        {
            if (r == null) return;
            var id = r.GetInstanceID();
            if (!m_RendererToIndex.TryGetValue(id, out var index)) return;

            var desc = m_Occludees[index];
            desc.UpdateBounds();
            m_Occludees[index] = desc;
            m_BoundsDirty = true;
        }

        // -----------------------------------------------------------------
        // GPU Buffer Management
        // -----------------------------------------------------------------
        private void EnsureBuffers()
        {
            var required = m_Occludees.Count;
            if (m_BufferCapacity >= required) return;

            var newCap = Mathf.Max(256, m_BufferCapacity * 2);
            while (newCap < required) newCap *= 2;

            ReleaseBuffers();

            m_BoundsBuffer = new ComputeBuffer(newCap, STRIDE_FLOAT4 * 2, ComputeBufferType.Structured);
            m_VisibilityBuffer = new ComputeBuffer(newCap, STRIDE_UINT, ComputeBufferType.Structured);
            m_VisibilityResultCPU = new uint[newCap];
            m_OccludedFrameCounter = new int[newCap];
            m_BufferCapacity = newCap;
            m_BoundsDirty = true;
        }

        private void UploadBounds()
        {
            if (!m_BoundsDirty) return;
            m_BoundsDirty = false;

            EnsureBuffers();

            var packed = new Vector4[m_Occludees.Count * 2];
            for (var i = 0; i < m_Occludees.Count; i++)
            {
                var b = m_Occludees[i].worldBounds;
                packed[i * 2] = new Vector4(b.center.x, b.center.y, b.center.z, 0f);
                packed[i * 2 + 1] = new Vector4(b.extents.x, b.extents.y, b.extents.z, 0f);
            }

            m_BoundsBuffer.SetData(packed, 0, 0, packed.Length);
        }

        /// <summary>Re-read bounds for all dynamic occludees.</summary>
        private void RefreshDynamicBounds()
        {
            foreach (var idx in m_DynamicIndices)
            {
                var desc = m_Occludees[idx];
                desc.UpdateBounds();
                m_Occludees[idx] = desc;
            }
            m_BoundsDirty = true;
        }

        private void ReleaseBuffers()
        {
            m_BoundsBuffer?.Release();
            m_BoundsBuffer = null;
            m_VisibilityBuffer?.Release();
            m_VisibilityBuffer = null;
            m_BufferCapacity = 0;
            m_VisibilityResultCPU = null;
            m_OccludedFrameCounter = null;
        }

        // -----------------------------------------------------------------
        // Culling Dispatch (called per-frame, after Hi-Z is ready)
        // -----------------------------------------------------------------
        public void DispatchCulling(Camera camera, CommandBuffer cmd = null)
        {
            if (cullingShader == null || m_Occludees.Count == 0) return;
            if (camera == null) camera = Camera.main;
            if (camera == null) return;

            // Per-frame bounds refresh for dynamic occludees
            if (m_DynamicIndices.Count > 0)
                RefreshDynamicBounds();

            UploadBounds();

            var kernel = cullingShader.FindKernel("CSStaticOcclusionCull");
            var useCmd = cmd != null;

            if (useCmd)
            {
                cmd.SetComputeBufferParam(cullingShader, kernel, _OccludeeBoundsBuffer, m_BoundsBuffer);
                cmd.SetComputeBufferParam(cullingShader, kernel, _OccludeeVisibility, m_VisibilityBuffer);
                cmd.SetComputeIntParam(cullingShader, _OccludeeCount, m_Occludees.Count);
                cmd.SetComputeIntParam(cullingShader, SID_DebugMode, (int)debugMode);

                var vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix;
                cmd.SetComputeMatrixParam(cullingShader, _CullingVP, vp);
                cmd.SetComputeVectorParam(cullingShader, _FrustumOcclusionParams,
                    new Vector4(frustumOffset, occlusionDepthBias, occlusionAccuracy, rectPadding));

                // Hi-Z
                if (hiZHandle?.rt != null)
                    cmd.SetComputeTextureParam(cullingShader, kernel, SID_HiZDepth, hiZHandle.rt);
            }
            else
            {
                cullingShader.SetBuffer(kernel, _OccludeeBoundsBuffer, m_BoundsBuffer);
                cullingShader.SetBuffer(kernel, _OccludeeVisibility, m_VisibilityBuffer);
                cullingShader.SetInt(_OccludeeCount, m_Occludees.Count);
                cullingShader.SetInt(SID_DebugMode, (int)debugMode);

                var vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix;
                cullingShader.SetMatrix(_CullingVP, vp);
                cullingShader.SetVector(_FrustumOcclusionParams,
                    new Vector4(frustumOffset, occlusionDepthBias, occlusionAccuracy, rectPadding));

                // Hi-Z
                if (hiZHandle?.rt != null)
                    cullingShader.SetTexture(kernel, SID_HiZDepth, hiZHandle.rt);
            }

            var threadGroups = Mathf.CeilToInt(m_Occludees.Count / 64f);
            if (useCmd)
                cmd.DispatchCompute(cullingShader, kernel, threadGroups, 1, 1);
            else
                cullingShader.Dispatch(kernel, threadGroups, 1, 1);

            // Request readback
            if (!m_ReadbackPending)
            {
                m_ReadbackPending = true;
                AsyncGPUReadback.Request(m_VisibilityBuffer, OnReadbackComplete);
            }
        }

        // -----------------------------------------------------------------
        // Readback & Apply
        // -----------------------------------------------------------------
        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            m_ReadbackPending = false;

            if (request.hasError || !request.done) return;

            var data = request.GetData<uint>();
            if (data.Length == 0) return;

            data.CopyTo(m_VisibilityResultCPU);
            ApplyVisibilityResults();
        }

        private void ApplyVisibilityResults()
        {
            for (var i = 0; i < m_Occludees.Count; i++)
            {
                var desc = m_Occludees[i];
                if (desc.renderer == null) continue;

                var gpuVisible = m_VisibilityResultCPU[i] != 0;

                if (!gpuVisible)
                    m_OccludedFrameCounter[i]++;
                else
                    m_OccludedFrameCounter[i] = 0;

                var shouldShow = gpuVisible || m_OccludedFrameCounter[i] < hideDelayFrames;
                desc.renderer.enabled = shouldShow;
            }
        }
    }
}