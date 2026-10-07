using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 逐物体高质量阴影：为角色单独渲染一张贴合其包围盒的主光阴影图，
/// 体积内的接收面（角色本体 + 它脚下的地面）用它替换 URP 原生级联阴影。
///
/// 矩阵来源：直接复用 CullingResults.ComputeDirectionalShadowMatricesAndCullingPrimitives
/// 产出的 view/proj（与 URP 内部同一对，手性/符号一致），只收紧 x/y 行与 z 范围，
/// 不手搓整个正交投影。
/// </summary>
public class HighQualityShadowFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        [Tooltip("阴影图分辨率。整张图只覆盖角色，1024~2048 已经远超级联精度")]
        [Range(256, 4096)] public int resolution = 2048;

        [Tooltip("参与投射的层：角色 + 需要遮挡角色的场景物体")]
        public LayerMask casterLayers = ~0;

        [Tooltip("按相机视锥裁剪阴影图的横向覆盖：只保留角色可见部分，镜头贴近时纹素密度自动提升。\n" +
                 "代价：角色位于画面外的部件投到画面内的影子会退回原生级联（边界由 edgeFade 过渡）。")]
        public bool frustumFit = true;

        [Tooltip("光空间包围盒外扩（世界单位），避免轮廓被裁")]
        [Range(0f, 2f)] public float boundsPadding = 0.15f;

        [Tooltip("体积边界 UV 淡出宽度，避免 HQ 与原生级联之间出现硬接缝")]
        [Range(0f, 0.3f)] public float edgeFade = 0.08f;

        [Tooltip("接收面沿光线方向的偏移（世界单位），抑制自阴影痤疮")]
        [Range(0f, 0.5f)] public float depthBias = 0.02f;

        [Tooltip("每帧打印拟合后的光空间框尺寸（排查裁剪问题时临时打开）")]
        public bool debugLog = false;
    }

    public Settings settings = new Settings();

    private HighQualityShadowPass m_Pass;

    public override void Create()
    {
        m_Pass = new HighQualityShadowPass(settings)
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingShadows
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var cameraType = renderingData.cameraData.cameraType;
        if (cameraType != CameraType.Game && cameraType != CameraType.SceneView)
            return;

        // 即使没有主光也要进队：pass 会在这个分支里把全局开关清零，
        // 否则上一帧的矩阵会残留在接收端。
        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
        m_Pass?.Dispose();
        m_Pass = null;
    }

    // -------------------------------------------------------------------------

    private sealed class HighQualityShadowPass : ScriptableRenderPass
    {
        private static readonly int s_ShadowmapId = Shader.PropertyToID("_HighQualityShadowmapTexture");
        private static readonly int s_MatrixId = Shader.PropertyToID("_HighQualityShadowMatrix");
        private static readonly int s_ParamsId = Shader.PropertyToID("_HighQualityShadowParams");
        private static readonly int s_TexelSizeId = Shader.PropertyToID("_HighQualityShadowTexelSize");

        private static readonly ShaderTagId s_ShadowCasterTag = new ShaderTagId("ShadowCaster");
        private static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler("High Quality Shadow");

        private const int k_ShadowmapBits = 32;
        private const string k_ShadowmapName = "HighQualityShadowmap";

        private readonly Settings m_Settings;
        private readonly List<Vector3> m_Corners = new List<Vector3>(8);
        private RTHandle m_ShadowRT;

        public HighQualityShadowPass(Settings settings)
        {
            m_Settings = settings;
        }

        public void Dispose()
        {
            m_ShadowRT?.Release();
            m_ShadowRT = null;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, s_ProfilingSampler))
            {
                Render(cmd, context, ref renderingData);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void Render(CommandBuffer cmd, ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var source = FindSource();
            int lightIndex = renderingData.lightData.mainLightIndex;

            if (source == null || lightIndex < 0 || lightIndex >= renderingData.lightData.visibleLights.Length)
            {
                Disable(cmd);
                return;
            }

            if (!source.RefreshBounds())
            {
                Disable(cmd);
                return;
            }

            var visibleLight = renderingData.lightData.visibleLights[lightIndex];
            var light = source.ResolvedLight != null ? source.ResolvedLight : visibleLight.light;

            if (light == null || light.type != LightType.Directional || light.shadows == LightShadows.None
                || visibleLight.lightType != LightType.Directional)
            {
                Disable(cmd);
                return;
            }

            int resolution = Mathf.Clamp(m_Settings.resolution, 256, 4096);
            var cullResults = renderingData.cullResults;

            // 与 URP 用同一对原生矩阵：手性、镜像、z 约定全部一致，不需要猜平台
            if (!cullResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(
                    lightIndex, 0, 1, new Vector3(1f, 0f, 0f), resolution, light.shadowNearPlane,
                    out Matrix4x4 view, out Matrix4x4 proj, out ShadowSplitData splitData))
            {
                Disable(cmd);
                return;
            }

            if (!TightenProjection(ref proj, view, source, renderingData.cameraData.camera))
            {
                Disable(cmd);
                return;
            }

            // ── 渲染阴影图 ──
            // 复用 URP 自己的 RTHandle 分配器：显式尺寸、shadowSamplingMode、isShadowMap 都已处理
            ShadowUtils.ShadowRTReAllocateIfNeeded(ref m_ShadowRT, resolution, resolution, k_ShadowmapBits,
                name: k_ShadowmapName);

            CoreUtils.SetRenderTarget(cmd, m_ShadowRT, ClearFlag.Depth);
            cmd.SetGlobalDepthBias(1f, 2.5f);
            cmd.SetViewProjectionMatrices(view, proj);

            Vector4 shadowBias = ShadowUtils.GetShadowBias(ref visibleLight, lightIndex, ref renderingData.shadowData, proj, resolution);
            ShadowUtils.SetupShadowCasterConstantBuffer(cmd, ref visibleLight, shadowBias);

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            var sortingSettings = new SortingSettings(renderingData.cameraData.camera)
            {
                criteria = SortingCriteria.CommonOpaque
            };
            var drawingSettings = new DrawingSettings(s_ShadowCasterTag, sortingSettings)
            {
                enableDynamicBatching = false,
                enableInstancing = true
            };
            var filteringSettings = new FilteringSettings(RenderQueueRange.all, m_Settings.casterLayers);

            context.DrawRenderers(cullResults, ref drawingSettings, ref filteringSettings);

            // ── 交给接收端 ──
            cmd.SetGlobalDepthBias(0f, 0f);
            cmd.SetGlobalTexture(s_ShadowmapId, m_ShadowRT.nameID);
            cmd.SetGlobalMatrix(s_MatrixId, GetShadowTransform(proj, view));
            cmd.SetGlobalVector(s_TexelSizeId, new Vector4(1f / resolution, 1f / resolution, resolution, resolution));
            cmd.SetGlobalVector(s_ParamsId, new Vector4(1f, Mathf.Max(m_Settings.edgeFade, 1e-4f), m_Settings.depthBias, 0f));

            // 还原相机矩阵，避免影响后续 pass
            cmd.SetViewProjectionMatrices(renderingData.cameraData.GetViewMatrix(), renderingData.cameraData.GetProjectionMatrix());
        }

        private void Disable(CommandBuffer cmd)
        {
            cmd.SetGlobalVector(s_ParamsId, Vector4.zero);
        }

        private static HighQualityShadow s_CachedSource;
        private static int s_NextSceneSearchFrame;

        private static HighQualityShadow FindSource()
        {
            // Play 模式下走静态注册表（OnEnable 维护），零查找开销
            var list = HighQualityShadow.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s != null && s.isActiveAndEnabled)
                {
                    s_CachedSource = s;
                    return s;
                }
            }

            // 编辑模式下 MonoBehaviour 的 OnEnable 不会执行，注册表是空的，
            // 这里兜底做场景查找（限流，避免每帧 FindObjectOfType）。
            if (s_CachedSource != null && s_CachedSource.isActiveAndEnabled)
                return s_CachedSource;

            if (Time.frameCount < s_NextSceneSearchFrame)
                return null;

            s_NextSceneSearchFrame = Time.frameCount + 30;
            s_CachedSource = Object.FindObjectOfType<HighQualityShadow>();
            return s_CachedSource != null && s_CachedSource.isActiveAndEnabled ? s_CachedSource : null;
        }

        /// <summary>
        /// 把角色包围盒收紧进光空间 x/y，并沿光线方向把体积向两侧外扩。
        /// 只重写 x/y/z 三行，符号沿用原生投影，避免坐标手性反转。
        /// </summary>
        /// <summary>
        /// 计算光空间正交框并重写投影矩阵的 x/y/z 三行。
        /// x/y 可以裁到「角色包围盒 ∩ 相机视锥」（frustum fit），z 不能裁。
        /// </summary>
        private bool TightenProjection(ref Matrix4x4 proj, Matrix4x4 view, HighQualityShadow source, Camera camera)
        {
            source.GetWorldCorners(m_Corners);

            float xMin = float.MaxValue, xMax = float.MinValue;
            float yMin = float.MaxValue, yMax = float.MinValue;
            float zMin = float.MaxValue, zMax = float.MinValue;

            for (int i = 0; i < m_Corners.Count; i++)
            {
                Vector3 p = view.MultiplyPoint(m_Corners[i]);
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z))
                    return false;

                xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x);
                yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
                zMin = Mathf.Min(zMin, p.z); zMax = Mathf.Max(zMax, p.z);
            }

            // frustum fit 必须用角色的真实包围盒（z 也用真实范围）。
            // 如果把它和下面为接收面外扩出来的假长度混在一起，远端 30 单位处的可行点
            // 会重新把 xy 撑满（视锥是锥体，越远越宽），裁剪就白做了。
            if (m_Settings.frustumFit && camera != null)
            {
                BuildHalfSpaces(camera, view, xMin, xMax, yMin, yMax, zMin, zMax);

                if (!ClipXyByHalfSpaces(ref xMin, ref xMax, ref yMin, ref yMax))
                    return false; // 角色完全在视锥外，这个相机不需要 HQ 阴影
            }

            // 接收面（地面）在角色背后、遮挡物在身前，z 两侧都要外扩；只影响投影，不参与上面的裁剪
            float extend = Mathf.Max(0f, source.shadowClipDistance);
            float zLo = zMin - extend;
            float zHi = zMax + extend;

            float pad = Mathf.Max(0f, m_Settings.boundsPadding);
            xMin -= pad; xMax += pad;
            yMin -= pad; yMax += pad;

            float sx = Mathf.Sign(proj.m00); if (sx == 0f) sx = 1f;
            float sy = Mathf.Sign(proj.m11); if (sy == 0f) sy = 1f;
            float sz = Mathf.Sign(proj.m22); if (sz == 0f) sz = 1f;

            float dx = xMax - xMin;
            float dy = yMax - yMin;
            float dz = zHi - zLo;

            if (dx <= 1e-4f || dy <= 1e-4f || dz <= 1e-4f)
                return false;

            proj.m20 = 0f; proj.m21 = 0f;
            proj.m00 = sx * 2f / dx; proj.m03 = -sx * (xMax + xMin) / dx;
            proj.m11 = sy * 2f / dy; proj.m13 = -sy * (yMax + yMin) / dy;
            proj.m22 = sz * 2f / dz; proj.m23 = -sz * (zHi + zLo) / dz;
            proj.m30 = 0f; proj.m31 = 0f; proj.m32 = 0f; proj.m33 = 1f;

            if (m_Settings.debugLog)
            {
                Debug.Log($"[HighQualityShadow] frustumFit={m_Settings.frustumFit} cam={camera?.name} " +
                          $"x[{xMin:F2},{xMax:F2}] y[{yMin:F2},{yMax:F2}] " +
                          $"size=({dx:F2},{dy:F2})  texel={(dx / m_Settings.resolution) * 1000f:F2}mm");
            }

            return true;
        }

        // ── frustum fit ──────────────────────────────────────────────────────
        // 12 个半空间，全部在光空间：0-5 是光空间包围盒的 6 个面，6-11 是相机视锥的 6 个面。
        // 约定 (a,b,c,d) 表示 a*x + b*y + c*z + d >= 0 为内侧，法线已归一化。
        private const int k_HalfSpaceCount = 12;
        private readonly Vector4[] m_HalfSpaces = new Vector4[k_HalfSpaceCount];

        private void BuildHalfSpaces(Camera camera, Matrix4x4 view,
            float xMin, float xMax, float yMin, float yMax, float zLo, float zHi)
        {
            m_HalfSpaces[0] = new Vector4(1f, 0f, 0f, -xMin);
            m_HalfSpaces[1] = new Vector4(-1f, 0f, 0f, xMax);
            m_HalfSpaces[2] = new Vector4(0f, 1f, 0f, -yMin);
            m_HalfSpaces[3] = new Vector4(0f, -1f, 0f, yMax);
            m_HalfSpaces[4] = new Vector4(0f, 0f, 1f, -zLo);
            m_HalfSpaces[5] = new Vector4(0f, 0f, -1f, zHi);

            // camera.projectionMatrix 是 OpenGL 约定（ndc 各轴 ∈ [-1,1]），
            // 视锥面直接由视投影矩阵的行给出：-w <= c <= w  →  row3 ± rowN >= 0
            Matrix4x4 vp = camera.projectionMatrix * camera.worldToCameraMatrix;
            Vector4 r0 = vp.GetRow(0), r1 = vp.GetRow(1), r2 = vp.GetRow(2), r3 = vp.GetRow(3);

            Matrix4x4 lightToWorld = view.inverse;

            m_HalfSpaces[6] = ToLightSpace(r3 + r0, lightToWorld);  // left
            m_HalfSpaces[7] = ToLightSpace(r3 - r0, lightToWorld);  // right
            m_HalfSpaces[8] = ToLightSpace(r3 + r1, lightToWorld);  // bottom
            m_HalfSpaces[9] = ToLightSpace(r3 - r1, lightToWorld);  // top
            m_HalfSpaces[10] = ToLightSpace(r3 + r2, lightToWorld); // near
            m_HalfSpaces[11] = ToLightSpace(r3 - r2, lightToWorld); // far

            for (int i = 0; i < k_HalfSpaceCount; i++)
                m_HalfSpaces[i] = NormalizePlane(m_HalfSpaces[i]);
        }

        /// <summary>世界空间半空间 → 光空间半空间：把 p_world = lightToWorld * p_light 代进去。</summary>
        private static Vector4 ToLightSpace(Vector4 plane, Matrix4x4 lightToWorld)
        {
            return new Vector4(
                plane.x * lightToWorld.m00 + plane.y * lightToWorld.m10 + plane.z * lightToWorld.m20,
                plane.x * lightToWorld.m01 + plane.y * lightToWorld.m11 + plane.z * lightToWorld.m21,
                plane.x * lightToWorld.m02 + plane.y * lightToWorld.m12 + plane.z * lightToWorld.m22,
                plane.x * lightToWorld.m03 + plane.y * lightToWorld.m13 + plane.z * lightToWorld.m23 + plane.w);
        }

        private static Vector4 NormalizePlane(Vector4 plane)
        {
            float len = Mathf.Sqrt(plane.x * plane.x + plane.y * plane.y + plane.z * plane.z);
            return len > 1e-8f ? plane / len : plane;
        }

        /// <summary>
        /// 求 (包围盒 ∩ 视锥) 这个凸多面体的横向范围，就地收窄 xMin/xMax/yMin/yMax。
        /// 凸多面体的顶点必落在 12 个半空间中至少 3 个的边界上，所以枚举全部 C(12,3) 个
        /// 三平面交点、保留可行解、取极值即可——结果精确，且只会比真实交集大不会小。
        /// 返回 false 表示交集为空。
        /// </summary>
        private bool ClipXyByHalfSpaces(ref float xMin, ref float xMax, ref float yMin, ref float yMax)
        {
            const float kInsideEps = -1e-3f; // 允许 1mm 的浮点误差，宁可多留不可裁掉
            const float kDetEps = 1e-4f;

            float nx = float.MaxValue, xx = float.MinValue;
            float ny = float.MaxValue, xy = float.MinValue;

            for (int i = 0; i < k_HalfSpaceCount - 2; i++)
            {
                for (int j = i + 1; j < k_HalfSpaceCount - 1; j++)
                {
                    for (int k = j + 1; k < k_HalfSpaceCount; k++)
                    {
                        if (!IntersectPlanes(m_HalfSpaces[i], m_HalfSpaces[j], m_HalfSpaces[k], kDetEps, out Vector3 p))
                            continue;

                        bool inside = true;
                        for (int m = 0; m < k_HalfSpaceCount; m++)
                        {
                            Vector4 pl = m_HalfSpaces[m];
                            if (pl.x * p.x + pl.y * p.y + pl.z * p.z + pl.w < kInsideEps)
                            {
                                inside = false;
                                break;
                            }
                        }

                        if (!inside)
                            continue;

                        nx = Mathf.Min(nx, p.x); xx = Mathf.Max(xx, p.x);
                        ny = Mathf.Min(ny, p.y); xy = Mathf.Max(xy, p.y);
                    }
                }
            }

            if (nx > xx || ny > xy)
                return false;

            if (m_Settings.debugLog)
            {
                Debug.Log($"[HighQualityShadow] clip feasible=({nx:F2},{xx:F2})x({ny:F2},{xy:F2}) " +
                          $"box=({xMin:F2},{xMax:F2})x({yMin:F2},{yMax:F2})");
            }

            // 可行点本来就在包围盒内，这里 clamp 只是数值保险
            xMin = Mathf.Max(xMin, nx); xMax = Mathf.Min(xMax, xx);
            yMin = Mathf.Max(yMin, ny); yMax = Mathf.Min(yMax, xy);

            return xMax > xMin && yMax > yMin;
        }

        /// <summary>Cramer 法则解三个平面的交点；行列式过小（近乎共面）时返回 false。</summary>
        private static bool IntersectPlanes(Vector4 a, Vector4 b, Vector4 c, float detEps, out Vector3 p)
        {
            float det = a.x * (b.y * c.z - b.z * c.y)
                      - a.y * (b.x * c.z - b.z * c.x)
                      + a.z * (b.x * c.y - b.y * c.x);

            if (Mathf.Abs(det) < detEps)
            {
                p = default;
                return false;
            }

            float d1 = -a.w, d2 = -b.w, d3 = -c.w;
            float invDet = 1f / det;

            p.x = (d1 * (b.y * c.z - b.z * c.y) - a.y * (d2 * c.z - b.z * d3) + a.z * (d2 * c.y - b.y * d3)) * invDet;
            p.y = (a.x * (d2 * c.z - b.z * d3) - d1 * (b.x * c.z - b.z * c.x) + a.z * (b.x * d3 - d2 * c.x)) * invDet;
            p.z = (a.x * (b.y * d3 - d2 * c.y) - a.y * (b.x * d3 - d2 * c.x) + d1 * (b.x * c.y - b.y * c.x)) * invDet;
            return true;
        }

        /// <summary>
        /// world → [0,1]^3 的采样矩阵。逐字复刻 URP 的 ShadowUtils.GetShadowTransform
        /// （它是 private，跨类调不到）：反 Z 平台翻投影 z 行，再乘纹理 scale/bias。
        /// </summary>
        private static Matrix4x4 GetShadowTransform(Matrix4x4 proj, Matrix4x4 view)
        {
            if (SystemInfo.usesReversedZBuffer)
            {
                proj.m20 = -proj.m20;
                proj.m21 = -proj.m21;
                proj.m22 = -proj.m22;
                proj.m23 = -proj.m23;
            }

            var textureScaleAndBias = Matrix4x4.identity;
            textureScaleAndBias.m00 = 0.5f;
            textureScaleAndBias.m11 = 0.5f;
            textureScaleAndBias.m22 = 0.5f;
            textureScaleAndBias.m03 = 0.5f;
            textureScaleAndBias.m13 = 0.5f;
            textureScaleAndBias.m23 = 0.5f;

            return textureScaleAndBias * (proj * view);
        }
    }
}
