// GPU Instancing Renderer — terrain-driven instance generation + GPUInstancingDrawer for cull & draw.
// Compute dispatched directly (cs.Dispatch), draw via CommandBuffer (GPUInstancingDrawer).
//
// Pipeline:  terrain density → Gen CS → InstanceData.matrixBuffer (float4x4)
//             → GPUInstancingDrawer.Draw (cull + CommandBuffer draw)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GPUDrivenOcclusion;
using UnityEngine;
using UnityEngine.Rendering;

namespace GPUDriven
{
    [DefaultExecutionOrder(150)]
    public class GPUInstancingRenderer : MonoBehaviour
    {
        [Header("Compute")] [SerializeField] private ComputeShader m_InstanceGenCS;

        [SerializeField] private ComputeShader m_CullingCS;

        [Header("Prototypes")] public List<InstanceData> m_Prototypes = new();

        [Header("Fallback")] [SerializeField] public Mesh m_FallbackGrassMesh;

        [SerializeField] public Material m_FallbackGrassMaterial;

        [Header("Settings")] [Range(0f, 16f)] public float globalDensity = 1f;

        [Range(10f, 500f)] public float maxDrawDistance = 150f;
        [Range(0f, 2f)] public float windStrength = 0.5f;
        public Vector2 windDirection = new(0.4f, 0.8f);

        [Header("Hi-Z Occlusion")] [SerializeField]
        private bool m_enableHiZ = true;

        [Range(0f, 2f)] public float occlusionDynamicOffset = 0.5f;
        [Range(0f, 0.01f)] public float occlusionOffset;
        [Range(0, 3)] public int occlusionAccuracy = 1;

        [Header("Debug")] [SerializeField] private bool m_disableDefaultDetails = true;

        private Camera m_Camera;
        private bool m_Initialized;

        private ComputeBuffer _densityBuffer, _protoDescBuffer;
        private int _kernelGen;
        private List<GpuProtoDesc> m_GpuProtoDescs; // cached for runtime per-proto updates

        // Per-proto buffer state (owned by InstanceData, created here)
        private bool _protoBuffersCreated;

        private void Awake()
        {
            m_Camera = Camera.main ?? GetComponent<Camera>();
        }

        private void OnEnable()
        {
            if (m_InstanceGenCS == null || m_CullingCS == null)
            {
                Debug.LogWarning("[GrassIndirect] Missing compute shader.");
                return;
            }

            Initialize();
            if (m_disableDefaultDetails) DisableUnityDetailRendering();
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            ReleaseAll();
            m_Initialized = false;
            _protoBuffersCreated = false;
        }

        private void Initialize()
        {
            if (m_FallbackGrassMesh == null) m_FallbackGrassMesh = CreateDefaultQuad();
            _kernelGen = m_InstanceGenCS.FindKernel("CSGenerateInstances");
            if (m_Prototypes.Count == 0)
            {
                Debug.LogWarning("[GrassIndirect] No prototypes.");
                return;
            }

            var gpuDescs = new List<GpuProtoDesc>();
            var densityLists = new List<float[]>();
            uint densityOffset = 0;
            foreach (var proto in m_Prototypes)
            {
                if (proto == null) continue;
                int dw = proto.densityWidth, dh = proto.densityHeight;
                if (dw <= 0 || dh <= 0)
                {
                    dw = 512;
                    dh = 512;
                }

                var density = ReadDensityLayer(proto.terrainProtoIndex, dw, dh, out var instanceCount);
                densityLists.Add(density);
                var t = Terrain.activeTerrains.Length > 0 ? Terrain.activeTerrains[0] : null;
                gpuDescs.Add(new GpuProtoDesc
                {
                    densityWidth = dw, densityHeight = dh,
                    terrainWidth = t?.terrainData?.size.x ?? 512, terrainHeight = t?.terrainData?.size.z ?? 512,
                    terrainHeightY = t?.terrainData?.size.y ?? 100,
                    terrainPosX = t?.transform.position.x ?? 0, terrainPosY = t?.transform.position.y ?? 0,
                    terrainPosZ = t?.transform.position.z ?? 0,
                    minWidth = proto.minWidth, maxWidth = proto.maxWidth,
                    minHeight = proto.minHeight, maxHeight = proto.maxHeight,
                    noiseSpread = proto.noiseSpread,
                    renderMode = proto.isVertexLit ? 1 : 0,
                    densityOffset = densityOffset
                });
                densityOffset += (uint)(dw * dh);

                // Capacity: instanceCount + 20% padding, min 64
                proto.capacity = instanceCount + Mathf.CeilToInt(instanceCount * 0.5f);
                proto.capacity = Mathf.Max(proto.capacity, 64);
            }

            var totalTexels = (int)densityOffset;
            var combined = new float[totalTexels];
            var off = 0;
            foreach (var l in densityLists)
            {
                Array.Copy(l, 0, combined, off, l.Length);
                off += l.Length;
            }

            _densityBuffer = new ComputeBuffer(totalTexels, sizeof(float), ComputeBufferType.Structured);
            _densityBuffer.SetData(combined);
            m_GpuProtoDescs = gpuDescs;
            _protoDescBuffer = new ComputeBuffer(gpuDescs.Count, Marshal.SizeOf<GpuProtoDesc>(),
                ComputeBufferType.Structured);
            _protoDescBuffer.SetData(gpuDescs.ToArray());
            m_Initialized = true;
        }

        /// <summary>
        ///     Create per-proto GPU buffers on InstanceData (once).
        ///     Subsequent frames reuse via GPUInstancingDrawer.CheckAndInit early-return.
        /// </summary>
        private void CreateProtoBuffers()
        {
            if (_protoBuffersCreated) return;
            var sizeofMatrix4X4 = 4 * 4 * 4;
            foreach (var proto in m_Prototypes)
            {
                if (proto == null) continue;
                if (proto.matrixBuffer != null) continue; // already created

                proto.matrixBuffer = new ComputeBuffer(proto.capacity, sizeofMatrix4X4, ComputeBufferType.Structured);
                proto.validMatrixBuffer =
                    new ComputeBuffer(proto.capacity, sizeofMatrix4X4, ComputeBufferType.Append);
                proto.argsBuffer = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);
                proto.genCounter = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);

                // Init args buffer with index count
                var mesh = proto.mesh ?? m_FallbackGrassMesh;
                var args = new uint[5];
                args[0] = mesh != null ? mesh.GetIndexCount(0) : 0u;
                args[1] = 0;
                args[2] = mesh != null ? mesh.GetIndexStart(0) : 0u;
                args[3] = mesh != null ? mesh.GetBaseVertex(0) : 0u;
                proto.argsBuffer.SetData(args);
            }

            _protoBuffersCreated = true;
        }

        private static float[] ReadDensityLayer(int protoIndex, int dw, int dh, out int instanceCount)
        {
            instanceCount = 0;
            var result = new float[dw * dh];
            foreach (var t in Terrain.activeTerrains)
            {
                if (t?.terrainData == null || protoIndex >= t.terrainData.detailPrototypes.Length) continue;
                var td = t.terrainData;
                var layer = td.GetDetailLayer(0, 0, td.detailWidth, td.detailHeight, protoIndex);
                for (var y = 0; y < td.detailHeight && y < dh; y++)
                for (var x = 0; x < td.detailWidth && x < dw; x++)
                {
                    var i = y * dw + x;
                    if (i >= result.Length) break;
                    result[i] = layer[y, x] * t.detailObjectDensity / 16f;
                    instanceCount += (int)result[i];
                }
            }

            return result;
        }

        private void ReleaseAll()
        {
            foreach (var proto in m_Prototypes)
            {
                if (proto == null) continue;
                proto.matrixBuffer?.Release();
                proto.matrixBuffer = null;
                proto.validMatrixBuffer?.Release();
                proto.validMatrixBuffer = null;
                proto.argsBuffer?.Release();
                proto.argsBuffer = null;
                proto.genCounter?.Release();
                proto.genCounter = null;
            }

            _densityBuffer?.Release();
            _protoDescBuffer?.Release();
        }

        // ================================================================
        // Per-frame: gen CS → readback count → GPUInstancingDrawer (cull + draw)
        // ================================================================
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!m_Initialized) return;
            if (cam != m_Camera && cam.cameraType != CameraType.SceneView) return;

            CreateProtoBuffers();

            // Shared gen params
            m_InstanceGenCS.SetBuffer(_kernelGen, ShaderID.DensityBuffer, _densityBuffer);
            m_InstanceGenCS.SetBuffer(_kernelGen, ShaderID.ProtoDescs, _protoDescBuffer);
            if (Terrain.activeTerrains.Length > 0)
            {
                var hm = Terrain.activeTerrains[0].terrainData.heightmapTexture;
                if (hm != null) m_InstanceGenCS.SetTexture(_kernelGen, ShaderID.TerrainHeightmap, hm);
            }

            // Hi-Z params on cull CS (Drawer binds per-instance buffers + dispatches)
            var hiZRt = (RenderTexture)null;
            if (m_enableHiZ)
            {
                var occSys = OcclusionCullingSystem.Instance;
                if (occSys != null && occSys.hiZHandle?.rt != null)
                {
                    hiZRt = occSys.hiZHandle.rt;
                    m_CullingCS.SetInt(ShaderID.EnableHiZ, 1);
                    m_CullingCS.SetFloat(ShaderID.HiZDynamicOffset, occlusionDynamicOffset);
                    m_CullingCS.SetFloat(ShaderID.HiZOcclusionOffset, occlusionOffset);
                    m_CullingCS.SetInt(ShaderID.HiZOcclusionAccuracy, occlusionAccuracy);
                }
                else
                {
                    m_CullingCS.SetInt(ShaderID.EnableHiZ, 0);
                }
            }
            else
            {
                m_CullingCS.SetInt(ShaderID.EnableHiZ, 0);
            }

            // View-projection for Hi-Z (passed to Drawer, which sets _VPMatrix4x4 on CS)
            var vp = GL.GetGPUProjectionMatrix(m_Camera.projectionMatrix, false)
                     * m_Camera.worldToCameraMatrix;

            // Shared cull params
            m_CullingCS.SetFloat(ShaderID.MaxDrawDistance, maxDrawDistance);
            m_CullingCS.SetVector(ShaderID.CameraPos, m_Camera.transform.position);

            // Per-proto: gen → readback count → draw
            for (var pi = 0; pi < m_Prototypes.Count; pi++)
            {
                var proto = m_Prototypes[pi];
                if (proto == null || proto.matrixBuffer == null) continue;

                // Reset gen counter
                proto.genCounter.SetData(new uint[] { 0 });

                // Update proto desc with runtime-editable fields (PlayMode tweaking)
                var desc = m_GpuProtoDescs[pi];
                desc.minWidth = proto.minWidth;
                desc.maxWidth = proto.maxWidth;
                desc.minHeight = proto.minHeight;
                desc.maxHeight = proto.maxHeight;
                desc.noiseSpread = proto.noiseSpread;
                desc.renderMode = proto.isVertexLit ? 1 : 0;
                m_GpuProtoDescs[pi] = desc;
                _protoDescBuffer.SetData(new[] { desc }, 0, pi, 1);

                // Gen
                m_InstanceGenCS.SetBuffer(_kernelGen, ShaderID.MatrixBuffer, proto.matrixBuffer);
                m_InstanceGenCS.SetBuffer(_kernelGen, ShaderID.GenCounter, proto.genCounter);
                m_InstanceGenCS.SetInt(ShaderID.CurrentProto, pi);
                m_InstanceGenCS.SetInt(ShaderID.MaxInstances, proto.capacity);
                m_InstanceGenCS.SetFloat(ShaderID.DensityScale, globalDensity * proto.densityMultiplier);
                m_InstanceGenCS.Dispatch(_kernelGen,
                    Mathf.CeilToInt(proto.densityWidth * proto.densityHeight / 64f), 1, 1);

                // Readback generated count
                var counter = new uint[1];
                proto.genCounter.GetData(counter);
                proto.instanceCount = (int)counter[0];
                if (proto.instanceCount == 0) continue;

                // Material properties
                SetMaterialProps(proto, cam);

                // Delegate cull + draw to Drawer
                GPUInstancingDrawer.Draw(proto, m_Camera, m_CullingCS, vp, hiZRt);
            }
        }

        private void SetMaterialProps(InstanceData proto, Camera cam)
        {
            var mat = proto.material ?? m_FallbackGrassMaterial;
            if (mat == null) return;
            mat.SetVector(ShaderID.WindParams,
                new Vector4(windDirection.x, windDirection.y, windStrength, Time.time));
            mat.SetVector(ShaderID.GrassCameraPos, cam.transform.position);
            if (Terrain.activeTerrains.Length > 0)
            {
                var t = Terrain.activeTerrains[0];
                var td = t.terrainData;
                var hm = td.heightmapTexture;
                if (hm != null)
                {
                    mat.SetTexture(ShaderID.TerrainHeightmap, hm);
                    mat.SetVector(ShaderID.TerrainTexelSize, hm.texelSize);
                    mat.SetVector(ShaderID.TerrainSize, td.size);
                    mat.SetVector(ShaderID.TerrainPosition, t.transform.position);
                }
            }
        }

        private static Mesh CreateDefaultQuad()
        {
            var m = new Mesh { name = "GrassBlade_Quad" };
            m.SetVertices(new[] { new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(0, 1, 0) });
            m.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 1) });
            m.SetTriangles(new[] { 0, 1, 2 }, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static void DisableUnityDetailRendering()
        {
            foreach (var t in Terrain.activeTerrains)
                if (t != null)
                {
                    t.detailObjectDistance = 0f;
                    t.drawTreesAndFoliage = false;
                }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuProtoDesc
        {
            public int densityWidth, densityHeight;
            public float terrainWidth, terrainHeight, terrainHeightY;
            public float terrainPosX, terrainPosY, terrainPosZ;
            public float minWidth, maxWidth, minHeight, maxHeight, noiseSpread;
            public int renderMode;
            public uint densityOffset, instanceOffset;
            public float healthyColorR, dryColorR;
            public int meshIndex;
        }

        private static class ShaderID
        {
            // Gen CS
            public static readonly int DensityBuffer = Shader.PropertyToID("_DensityBuffer");
            public static readonly int ProtoDescs = Shader.PropertyToID("_ProtoDescs");
            public static readonly int TerrainHeightmap = Shader.PropertyToID("_TerrainHeightmap");
            public static readonly int GenCounter = Shader.PropertyToID("_GenCounter");
            public static readonly int MatrixBuffer = Shader.PropertyToID("_MatrixBuffer");
            public static readonly int MaxInstances = Shader.PropertyToID("_MaxInstances");
            public static readonly int CurrentProto = Shader.PropertyToID("_CurrentProtoIndex");
            public static readonly int DensityScale = Shader.PropertyToID("_DensityScale");

            // Hi-Z (set on cull CS by Renderer, consumed by InstancCulling.compute)
            public static readonly int EnableHiZ = Shader.PropertyToID("_EnableHiZ");
            public static readonly int HiZDynamicOffset = Shader.PropertyToID("_HiZDynamicOffset");
            public static readonly int HiZOcclusionOffset = Shader.PropertyToID("_HiZOcclusionOffset");
            public static readonly int HiZOcclusionAccuracy = Shader.PropertyToID("_HiZOcclusionAccuracy");

            // Distance culling
            public static readonly int MaxDrawDistance = Shader.PropertyToID("_MaxDrawDistance");
            public static readonly int CameraPos = Shader.PropertyToID("_CameraPos");

            // Material properties
            public static readonly int WindParams = Shader.PropertyToID("_WindParams");
            public static readonly int GrassCameraPos = Shader.PropertyToID("_GrassCameraPos");
            public static readonly int TerrainTexelSize = Shader.PropertyToID("_TerrainHeightmap_TexelSize");
            public static readonly int TerrainSize = Shader.PropertyToID("_TerrainSize");
            public static readonly int TerrainPosition = Shader.PropertyToID("_TerrainPosition");
        }
    }
}