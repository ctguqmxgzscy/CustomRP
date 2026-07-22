// Grass Indirect Renderer — GPU-driven terrain detail rendering.
// Compute dispatched directly (cs.Dispatch), draw in CommandBuffer (matches ToyRP pattern).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace GPUDrivenGrass
{
    [DefaultExecutionOrder(150)]
    public class GrassIndirectRenderer : MonoBehaviour
    {
        [Header("Compute")]
        [SerializeField] private ComputeShader m_InstanceGenCS;
        [SerializeField] private ComputeShader m_CullingCS;

        [Header("Prototypes")]
        public List<GrassPrototypeData> m_Prototypes = new();

        [Header("Fallback")]
        [SerializeField] public Mesh m_FallbackGrassMesh;
        [SerializeField] public Material m_FallbackGrassMaterial;

        [Header("Settings")]
        [Range(0f, 16f)] public float globalDensity = 1f;
        [Range(10f, 500f)] public float maxDrawDistance = 150f;
        [Range(0f, 2f)] public float windStrength = 0.5f;
        public Vector2 windDirection = new(0.4f, 0.8f);

        [Header("Hi-Z Occlusion")]
        [SerializeField] private bool m_enableHiZ = true;
        [Range(0f, 0.2f)] public float occlusionDepthBias = 0.02f;

        [Header("Debug")]
        [SerializeField] private bool m_disableDefaultDetails = true;
        [SerializeField] private bool m_logStats;

        Camera m_Camera;
        bool m_Initialized;
        int m_FrameLogCounter;
        MaterialPropertyBlock m_MPB;
        List<ProtoRenderGroup> m_ProtoGroups = new();
        ComputeBuffer _densityBuffer, _protoDescBuffer, _debugCounterBuffer;
        int _kernelGen, _kernelCull;

        sealed class ProtoRenderGroup : IDisposable
        {
            public GrassPrototypeData data;
            public int capacity;
            public float maxBladeHeight;
            public ComputeBuffer transformBuffer, instanceBuffer, argsBuffer, genCounter;
            public void Allocate(int maxInstances, float bladeHeight)
            {
                capacity = maxInstances; maxBladeHeight = bladeHeight;
                transformBuffer = new ComputeBuffer(capacity * 2, 4 * 4, ComputeBufferType.Structured);
                instanceBuffer  = new ComputeBuffer(capacity * 2, 4 * 4, ComputeBufferType.Structured);
                argsBuffer      = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);
                genCounter      = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
                var idxCount = data.mesh != null ? (uint)data.mesh.GetIndexCount(0) : 0u;
                argsBuffer.SetData(new uint[5] { idxCount, 0, 0, 0, 0 });
            }
            public void Dispose() { transformBuffer?.Release(); instanceBuffer?.Release(); argsBuffer?.Release(); genCounter?.Release(); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuProtoDesc
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

        void Awake() { m_Camera = Camera.main ?? GetComponent<Camera>(); m_MPB = new MaterialPropertyBlock(); }

        void OnEnable()
        {
            if (m_InstanceGenCS == null || m_CullingCS == null) { Debug.LogWarning("[GrassIndirect] No compute shader."); return; }
            Initialize();
            if (m_disableDefaultDetails) DisableUnityDetailRendering();
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        void OnDisable() { RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering; ReleaseAll(); m_Initialized = false; }

        void Initialize()
        {
            if (m_FallbackGrassMesh == null) m_FallbackGrassMesh = CreateDefaultQuad();
            _kernelGen  = m_InstanceGenCS.FindKernel("CSGenerateInstances");
            _kernelCull = m_CullingCS.FindKernel("CSCullInstances");
            if (m_Prototypes.Count == 0) { Debug.LogWarning("[GrassIndirect] No prototypes."); return; }

            var gpuDescs = new List<GpuProtoDesc>();
            var densityLists = new List<float[]>();
            uint densityOffset = 0;
            foreach (var proto in m_Prototypes)
            {
                if (proto == null) continue;
                int dw = proto.densityWidth, dh = proto.densityHeight;
                if (dw <= 0 || dh <= 0) { dw = 512; dh = 512; }
                var density = ReadDensityLayer(proto.terrainProtoIndex, dw, dh, out var instanceCount);
                densityLists.Add(density);
                var t = Terrain.activeTerrains.Length > 0 ? Terrain.activeTerrains[0] : null;
                gpuDescs.Add(new GpuProtoDesc {
                    densityWidth = dw, densityHeight = dh,
                    terrainWidth = t?.terrainData?.size.x ?? 512, terrainHeight = t?.terrainData?.size.z ?? 512,
                    terrainHeightY = t?.terrainData?.size.y ?? 100,
                    terrainPosX = t?.transform.position.x ?? 0, terrainPosY = t?.transform.position.y ?? 0,
                    terrainPosZ = t?.transform.position.z ?? 0,
                    minWidth = proto.minWidth, maxWidth = proto.maxWidth,
                    minHeight = proto.minHeight, maxHeight = proto.maxHeight,
                    noiseSpread = proto.noiseSpread,
                    renderMode = proto.isVertexLit ? 1 : 0,
                    densityOffset = densityOffset,
                });
                densityOffset += (uint)(dw * dh);
                var cap = instanceCount + Mathf.CeilToInt(instanceCount * 0.5f);
                cap = Mathf.Max(cap, 64);
                var grp = new ProtoRenderGroup { data = proto };
                grp.Allocate(cap, Mathf.Max(proto.maxHeight, 0.3f));
                m_ProtoGroups.Add(grp);
            }

            var totalTexels = (int)densityOffset;
            var combined = new float[totalTexels]; int off = 0;
            foreach (var l in densityLists) { Array.Copy(l, 0, combined, off, l.Length); off += l.Length; }
            _densityBuffer = new ComputeBuffer(totalTexels, sizeof(float), ComputeBufferType.Structured);
            _densityBuffer.SetData(combined);
            _protoDescBuffer = new ComputeBuffer(gpuDescs.Count, Marshal.SizeOf<GpuProtoDesc>(), ComputeBufferType.Structured);
            _protoDescBuffer.SetData(gpuDescs.ToArray());
            _debugCounterBuffer = new ComputeBuffer(2, sizeof(uint), ComputeBufferType.Structured);
            m_Initialized = true;
            if (m_logStats) Debug.Log($"[GrassIndirect] {m_ProtoGroups.Count} protos, {totalTexels} texels");
        }

        static float[] ReadDensityLayer(int protoIndex, int dw, int dh, out int instanceCount)
        {
            instanceCount = 0; var result = new float[dw * dh];
            foreach (var t in Terrain.activeTerrains) {
                if (t?.terrainData == null || protoIndex >= t.terrainData.detailPrototypes.Length) continue;
                var td = t.terrainData;
                var layer = td.GetDetailLayer(0, 0, td.detailWidth, td.detailHeight, protoIndex);
                for (int y = 0; y < td.detailHeight && y < dh; y++)
                for (int x = 0; x < td.detailWidth  && x < dw; x++)
                { int i = y * dw + x; if (i >= result.Length) break;
                  result[i] = layer[y, x] * t.detailObjectDensity / 16f; instanceCount += (int)result[i]; }
            }
            return result;
        }

        void ReleaseAll() { foreach (var g in m_ProtoGroups) g.Dispose(); m_ProtoGroups.Clear(); _densityBuffer?.Release(); _protoDescBuffer?.Release(); _debugCounterBuffer?.Release(); }

        // ================================================================
        // Per-frame: direct CS dispatch + CommandBuffer draw (ToyRP pattern)
        // ================================================================
        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!m_Initialized) return;
            if (cam != m_Camera && cam.cameraType != CameraType.SceneView) return;

            // Frustum planes — always from Game camera so Scene view matches
            var ps = GeometryUtility.CalculateFrustumPlanes(m_Camera);
            var planes = new Vector4[6];
            for (int i = 0; i < 6; i++)
                planes[i] = new Vector4(ps[i].normal.x, ps[i].normal.y, ps[i].normal.z, ps[i].distance);

            // Shared gen params (directly on CS objects, not CommandBuffer)
            m_InstanceGenCS.SetBuffer(_kernelGen, ShaderID.DensityBuffer, _densityBuffer);
            m_InstanceGenCS.SetBuffer(_kernelGen, ShaderID.ProtoDescs, _protoDescBuffer);
            if (Terrain.activeTerrains.Length > 0) {
                var hm = Terrain.activeTerrains[0].terrainData.heightmapTexture;
                if (hm != null) m_InstanceGenCS.SetTexture(_kernelGen, ShaderID.TerrainHeightmap, hm);
            }

            // Shared cull params
            m_CullingCS.SetVectorArray(ShaderID.FrustumPlanes, planes);
            m_CullingCS.SetVector(ShaderID.CameraPos, m_Camera.transform.position);
            m_CullingCS.SetFloat(ShaderID.MaxDrawDistance, maxDrawDistance);

            // Hi-Z params (before per-proto dispatch)
            if (m_enableHiZ)
            {
                var occSys = GPUDrivenOcclusion.OcclusionCullingSystem.Instance;
                if (occSys != null && occSys.hiZHandle?.rt != null)
                {
                    var hiZRt = occSys.hiZHandle.rt;
                    m_CullingCS.SetTexture(_kernelCull, ShaderID.HiZDepth, hiZRt);
                    var vp = GL.GetGPUProjectionMatrix(m_Camera.projectionMatrix, false)
                             * m_Camera.worldToCameraMatrix;
                    m_CullingCS.SetMatrix(ShaderID.CullingVP, vp);
                    m_CullingCS.SetFloat(ShaderID.HiZDepthBias, occlusionDepthBias);
                    m_CullingCS.SetInt(ShaderID.EnableHiZ, 1);
                    if (m_logStats && Time.frameCount % 60 == 0)
                        Debug.Log($"[GrassIndirect] Hi-Z enabled, size={hiZRt.width}");
                }
                else
                {
                    m_CullingCS.SetInt(ShaderID.EnableHiZ, 0);
                    if (m_logStats && Time.frameCount % 60 == 0)
                        Debug.LogWarning($"[GrassIndirect] Hi-Z disabled: " +
                            $"occSys={(occSys != null ? "found" : "null")}, " +
                            $"hiZHandle={(occSys != null && occSys.hiZHandle != null ? "found" : "null")}");
                }
            }
            else
            {
                m_CullingCS.SetInt(ShaderID.EnableHiZ, 0);
            }

            // Debug counters
            m_CullingCS.SetBuffer(_kernelCull, ShaderID.DebugCounters, _debugCounterBuffer);
            _debugCounterBuffer.SetData(new uint[] { 0, 0 });

            for (int pi = 0; pi < m_ProtoGroups.Count; pi++)
            {
                var grp = m_ProtoGroups[pi];
                var proto = grp.data;
                var idxCount = grp.data.mesh != null ? (uint)grp.data.mesh.GetIndexCount(0) : 0u;

                // CPU reset
                grp.genCounter.SetData(new uint[] { 0 });
                grp.argsBuffer.SetData(new uint[] { idxCount, 0, 0, 0, 0 });

                // Gen — direct dispatch
                m_InstanceGenCS.SetBuffer(_kernelGen, ShaderID.InstanceOutput, grp.transformBuffer);
                m_InstanceGenCS.SetBuffer(_kernelGen, ShaderID.GenCounter, grp.genCounter);
                m_InstanceGenCS.SetInt(ShaderID.CurrentProto, pi);
                m_InstanceGenCS.SetInt(ShaderID.MaxInstances, grp.capacity);
                m_InstanceGenCS.SetFloat(ShaderID.DensityScale, globalDensity * proto.densityMultiplier);
                m_InstanceGenCS.Dispatch(_kernelGen, Mathf.CeilToInt(proto.densityWidth * proto.densityHeight / 64f), 1, 1);

                // Cull — direct dispatch
                m_CullingCS.SetBuffer(_kernelCull, ShaderID.InstanceOutput, grp.transformBuffer);
                m_CullingCS.SetBuffer(_kernelCull, ShaderID.CullingOutput, grp.instanceBuffer);
                m_CullingCS.SetBuffer(_kernelCull, ShaderID.GenCounter, grp.genCounter);
                m_CullingCS.SetBuffer(_kernelCull, ShaderID.IndirectArgs, grp.argsBuffer);
                m_CullingCS.SetInt(ShaderID.MaxInstances, grp.capacity);
                m_CullingCS.Dispatch(_kernelCull, Mathf.CeilToInt(grp.capacity / 64f), 1, 1);
            }

            // Draw
            SetupMPB(cam);
            var bounds = CalculateTerrainBounds();
            foreach (var grp in m_ProtoGroups)
            {
                var mesh = grp.data.mesh ?? m_FallbackGrassMesh;
                var mat  = grp.data.material ?? m_FallbackGrassMaterial;
                if (mesh == null || mat == null) continue;
                m_MPB.SetBuffer(ShaderID.GrassInstanceData, grp.instanceBuffer);
                Graphics.DrawMeshInstancedIndirect(mesh, 0, mat, bounds,
                    grp.argsBuffer, 0, m_MPB, ShadowCastingMode.Off, true, 0, cam);
            }
            DebugLogBuffers();
        }

        void SetupMPB(Camera cam)
        {
            m_MPB.Clear();
            m_MPB.SetVector(ShaderID.WindParams, new Vector4(windDirection.x, windDirection.y, windStrength, Time.time));
            m_MPB.SetVector(ShaderID.GrassCameraPos, cam.transform.position);
            if (Terrain.activeTerrains.Length > 0) {
                var t = Terrain.activeTerrains[0]; var td = t.terrainData; var hm = td.heightmapTexture;
                if (hm != null) { m_MPB.SetTexture(ShaderID.TerrainHeightmap, hm);
                    m_MPB.SetVector(ShaderID.TerrainTexelSize, hm.texelSize);
                    m_MPB.SetVector(ShaderID.TerrainSize, td.size);
                    m_MPB.SetVector(ShaderID.TerrainPosition, t.transform.position); }
            }
        }

        static Bounds CalculateTerrainBounds() {
            var b = new Bounds(); bool first = true;
            foreach (var t in Terrain.activeTerrains) { if (t?.terrainData == null) continue;
              var tb = new Bounds(t.transform.position + t.terrainData.size * 0.5f, t.terrainData.size);
              if (first) { b = tb; first = false; } else b.Encapsulate(tb); }
            return first ? new Bounds(Vector3.zero, Vector3.one * 1000f) : b;
        }

        static Mesh CreateDefaultQuad() { var m = new Mesh { name = "GrassBlade_Quad" };
          m.SetVertices(new[] { new Vector3(-0.5f,0,0), new Vector3(0.5f,0,0), new Vector3(0,1,0) });
          m.SetUVs(0, new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(0.5f,1) });
          m.SetTriangles(new[] { 0,1,2 }, 0); m.RecalculateNormals(); m.RecalculateBounds(); return m; }

        static void DisableUnityDetailRendering() { foreach (var t in Terrain.activeTerrains) if (t != null) { t.detailObjectDistance = 0f; t.drawTreesAndFoliage = false; } }

        void DebugLogBuffers() {
            if (!m_logStats || ++m_FrameLogCounter < 60) return; m_FrameLogCounter = 0;
            var sb = new StringBuilder("[GrassIndirect DEBUG]\n");
            foreach (var g in m_ProtoGroups) { if (g.argsBuffer == null) continue;
              var a = new uint[5]; g.argsBuffer.GetData(a);
              sb.AppendLine($"  '{g.data.name}': idx={a[0]} inst={a[1]}, cap={g.capacity}"); }
            sb.AppendLine($"  dist={maxDrawDistance}, T={Terrain.activeTerrains.Length}");
            var dc = new uint[2];
            _debugCounterBuffer.GetData(dc);
            sb.AppendLine($"  HiZ: attempted={dc[1]} culled={dc[0]}");
            Debug.Log(sb.ToString());
        }

        static class ShaderID
        {
            public static readonly int GrassInstanceData = Shader.PropertyToID("_GrassInstanceData");
            public static readonly int WindParams        = Shader.PropertyToID("_WindParams");
            public static readonly int GrassCameraPos    = Shader.PropertyToID("_GrassCameraPos");
            public static readonly int TerrainHeightmap  = Shader.PropertyToID("_TerrainHeightmap");
            public static readonly int TerrainTexelSize  = Shader.PropertyToID("_TerrainHeightmap_TexelSize");
            public static readonly int TerrainSize       = Shader.PropertyToID("_TerrainSize");
            public static readonly int TerrainPosition   = Shader.PropertyToID("_TerrainPosition");
            public static readonly int DensityBuffer     = Shader.PropertyToID("_DensityBuffer");
            public static readonly int ProtoDescs        = Shader.PropertyToID("_ProtoDescs");
            public static readonly int InstanceOutput    = Shader.PropertyToID("_InstanceOutput");
            public static readonly int GenCounter        = Shader.PropertyToID("_GenCounter");
            public static readonly int CullingOutput     = Shader.PropertyToID("_OutputBuffer");
            public static readonly int IndirectArgs      = Shader.PropertyToID("_IndirectArgs");
            public static readonly int MaxInstances      = Shader.PropertyToID("_MaxInstances");
            public static readonly int CurrentProto      = Shader.PropertyToID("_CurrentProtoIndex");
            public static readonly int DensityScale      = Shader.PropertyToID("_DensityScale");
            public static readonly int FrustumPlanes     = Shader.PropertyToID("_FrustumPlanes");
            public static readonly int CameraPos         = Shader.PropertyToID("_CameraPos");
            public static readonly int MaxDrawDistance   = Shader.PropertyToID("_MaxDrawDistance");
            public static readonly int HiZDepth          = Shader.PropertyToID("_HiZDepth");
            public static readonly int CullingVP         = Shader.PropertyToID("_CullingVP");
            public static readonly int HiZDepthBias      = Shader.PropertyToID("_HiZDepthBias");
            public static readonly int EnableHiZ         = Shader.PropertyToID("_EnableHiZ");
            public static readonly int DebugCounters     = Shader.PropertyToID("_DebugCounters");
        }
    }
}
