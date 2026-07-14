using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
///     Self-contained RenderFeature for atmospheric scattering skybox.
///     Each frame:
///     1. Reads parameters from AtmosphereSettings (ScriptableObject)
///     2. Generates _OpticalDepthLUT (256×256, RG16)
///     3. Generates _MultiScatteringLUT (32×32, ARGBFloat) [optional, Hillaire 2020]
///     4. Generates _SkyViewLut (512×256, RGB float)
///     5. Sets all global shader properties for the skybox shader
///     No MonoBehaviour or material references needed.
/// </summary>
public class AtmosphereSkyboxLutFeature : ScriptableRendererFeature
{
    [Header("Settings")] [SerializeField] private AtmosphereSettings m_Settings;

    [Header("Compute Shaders")] [SerializeField]
    private ComputeShader m_OpticalDepthLutCompute;

    [SerializeField] private ComputeShader m_SkyViewLutCompute;

    [Header("Multi-Scattering")] [SerializeField]
    private ComputeShader m_MultiScatteringLutCompute;

    [Header("Aerial Perspective")] [SerializeField]
    private bool m_EnableAerialPerspective = true;

    [SerializeField] private ComputeShader m_AerialPerspectiveLutCompute;

    [SerializeField] private Shader m_AerialPerspectiveCompositeShader;

    [Header("Skybox (optional)")] [Tooltip("If set, auto-assigned to RenderSettings.skybox.")] [SerializeField]
    private Material m_SkyboxMaterial;

    [Header("Debug")] [SerializeField] private AtmosphereDebugMode m_DebugMode;
    [SerializeField] [Range(0, 1)] private float m_DebugSliceZ = 0.5f;
    [SerializeField] private Shader m_DebugOverlayShader;
    private AerialPerspectiveCompositePass m_CompositePass;
    private DebugLutPass m_DebugPass;

    private AtmosphereLutPass m_Pass;

    public override void Create()
    {
        m_Pass = new AtmosphereLutPass
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques,
            settings = m_Settings,
            opticalDepthLutCompute = m_OpticalDepthLutCompute,
            skyViewLutCompute = m_SkyViewLutCompute,
            multiScatteringLutCompute = m_MultiScatteringLutCompute,
            aerialPerspectiveLutCompute = m_AerialPerspectiveLutCompute,
            skyboxMaterial = m_SkyboxMaterial
        };

        m_CompositePass = new AerialPerspectiveCompositePass
        {
            compositeMaterial = m_AerialPerspectiveCompositeShader != null
                ? new Material(m_AerialPerspectiveCompositeShader)
                : null
        };

        if (m_DebugOverlayShader != null)
            m_DebugPass = new DebugLutPass
            {
                debugMaterial = new Material(m_DebugOverlayShader),
                debugMode = m_DebugMode,
                debugSliceZ = m_DebugSliceZ
            };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_Settings == null || m_OpticalDepthLutCompute == null || m_SkyViewLutCompute == null)
            return;

        m_Pass.settings = m_Settings;
        m_Pass.enableAerialPerspective = m_EnableAerialPerspective;
        renderer.EnqueuePass(m_Pass);

        // Debug overlay replaces composite pass when active
        if (m_DebugMode != AtmosphereDebugMode.None)
        {
            if (m_DebugPass != null && m_DebugPass.debugMaterial != null)
            {
                m_DebugPass.debugMode = m_DebugMode;
                m_DebugPass.debugSliceZ = m_DebugSliceZ;
                renderer.EnqueuePass(m_DebugPass);
            }
        }
        else if (m_CompositePass != null && m_CompositePass.compositeMaterial != null
                                         && m_EnableAerialPerspective)
        {
            m_CompositePass.enableTerrainShadow = m_Settings.enableTerrainShadow;
            renderer.EnqueuePass(m_CompositePass);
        }
    }

    protected override void Dispose(bool disposing)
    {
        m_Pass?.Dispose();
        m_Pass = null;
        m_CompositePass?.Dispose();
        m_CompositePass = null;
        m_DebugPass?.Dispose();
        m_DebugPass = null;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Render Pass
    // ═════════════════════════════════════════════════════════════════════
    private class AtmosphereLutPass : ScriptableRenderPass
    {
        private const int k_OpticalDepthLutSize = 256;
        private const int k_SkyViewLutWidth = 512;
        private const int k_SkyViewLutHeight = 256;
        private const int k_MultiScatteringLutSize = 32;
        private const int k_AerialLutWidth = 128;
        private const int k_AerialLutHeight = 72;
        private const int k_AerialLutDepth = 64;

        // ── RTHandle descriptors ─────────────────────────────────────────
        private static readonly RenderTextureDescriptor s_OpticalDepthLutDesc = new(
            k_OpticalDepthLutSize, k_OpticalDepthLutSize, RenderTextureFormat.ARGBHalf)
        {
            enableRandomWrite = true, sRGB = false
        };

        private static readonly RenderTextureDescriptor s_MultiScatteringLutDesc = new(
            k_MultiScatteringLutSize, k_MultiScatteringLutSize, RenderTextureFormat.ARGBFloat)
        {
            enableRandomWrite = true, sRGB = false
        };

        private static readonly RenderTextureDescriptor s_AerialPerspectiveLutDesc = new(
            k_AerialLutWidth, k_AerialLutHeight, RenderTextureFormat.ARGBHalf)
        {
            enableRandomWrite = true,
            sRGB = false,
            dimension = TextureDimension.Tex3D,
            volumeDepth = k_AerialLutDepth
        };

        private static readonly RenderTextureDescriptor s_SkyViewLutDesc = new(
            k_SkyViewLutWidth, k_SkyViewLutHeight, RenderTextureFormat.ARGBFloat)
        {
            enableRandomWrite = true, sRGB = false
        };

        // ── Cached property IDs ──────────────────────────────────────────
        private static readonly int s_OpticalDepthLutId = Shader.PropertyToID("_OpticalDepthLUT");
        private static readonly int s_SkyViewLutId = Shader.PropertyToID("_SkyViewLut");
        private static readonly int s_PlanetRadiusId = Shader.PropertyToID("_PlanetRadius");
        private static readonly int s_AtmosphereHeightId = Shader.PropertyToID("_AtmosphereHeight");

        private static readonly int
            s_AtmosphereRadiusId = Shader.PropertyToID("_AtmosphereRadius");

        private static readonly int s_ScaleHeightId = Shader.PropertyToID("_ScaleHeight");
        private static readonly int s_MieGId = Shader.PropertyToID("_MieG");
        private static readonly int s_SunIntensityId = Shader.PropertyToID("_SunIntensity");
        private static readonly int s_ViewSamplesId = Shader.PropertyToID("_ViewSamples");
        private static readonly int s_LightSamplesId = Shader.PropertyToID("_LightSamples");
        private static readonly int s_MieScaleHeightId = Shader.PropertyToID("_MieScaleHeight");
        private static readonly int s_InvOpticalDepthLutSizeId = Shader.PropertyToID("_InvLUTSize");
        private static readonly int s_SunDirectionId = Shader.PropertyToID("_SunDirection");
        private static readonly int s_zFarPropId = Shader.PropertyToID("_zFar");
        private static readonly int s_CameraHeightId = Shader.PropertyToID("_CameraHeight");
        private static readonly int s_SunDiskAngleId = Shader.PropertyToID("_SunDiskAngle");
        private static readonly int s_SunLightColorId = Shader.PropertyToID("_SunLightColor");
        private static readonly int s_APIntensityId = Shader.PropertyToID("_APIntensity");

        private static readonly int s_MultiScatteringLutId =
            Shader.PropertyToID("_MultiScatteringLUT");

        private static readonly int s_AerialPerspectiveLutId =
            Shader.PropertyToID("_AerialPerspectiveLUT");

        private static readonly int s_AerialLutWidthId = Shader.PropertyToID("_AerialLutWidth");
        private static readonly int s_AerialLutHeightId = Shader.PropertyToID("_AerialLutHeight");
        private static readonly int s_AerialLutDepthId = Shader.PropertyToID("_AerialLutDepth");

        private static readonly int s_InvProjectionMatrixId =
            Shader.PropertyToID("_InvProjectionMatrix");

        public ComputeShader aerialPerspectiveLutCompute;
        public bool enableAerialPerspective;
        private int m_AerialPerspectiveKernel;
        private RTHandle m_AerialPerspectiveLut;

        private bool m_Initialized;
        private int m_MultiScatteringKernel;
        private RTHandle m_MultiScatteringLut;
        private int m_OpticalDepthKernel;

        // ── RTHandles ────────────────────────────────────────────────────
        private RTHandle m_OpticalDepthLut;
        private int m_SkyViewKernel;
        private RTHandle m_SkyViewLut;
        public ComputeShader multiScatteringLutCompute;
        public ComputeShader opticalDepthLutCompute;

        public AtmosphereSettings settings;
        public Material skyboxMaterial;
        public ComputeShader skyViewLutCompute;

        public void Dispose()
        {
            m_OpticalDepthLut?.Release();
            m_OpticalDepthLut = null;
            m_SkyViewLut?.Release();
            m_SkyViewLut = null;
            m_MultiScatteringLut?.Release();
            m_MultiScatteringLut = null;
            m_AerialPerspectiveLut?.Release();
            m_AerialPerspectiveLut = null;
            m_Initialized = false;
        }

        private void InitializeIfNeeded()
        {
            if (m_Initialized) return;

            m_OpticalDepthKernel = opticalDepthLutCompute.FindKernel("ComputeOpticalDepthLUT");
            if (multiScatteringLutCompute != null)
                m_MultiScatteringKernel =
                    multiScatteringLutCompute.FindKernel("ComputeMultiScatteringLut");
            m_SkyViewKernel = skyViewLutCompute.FindKernel("ComputeSkyViewLut");

            RenderingUtils.ReAllocateIfNeeded(
                ref m_OpticalDepthLut, s_OpticalDepthLutDesc,
                FilterMode.Bilinear, TextureWrapMode.Clamp, name: "OpticalDepthLut");

            RenderingUtils.ReAllocateIfNeeded(
                ref m_MultiScatteringLut, s_MultiScatteringLutDesc,
                FilterMode.Bilinear, TextureWrapMode.Clamp, name: "MultiScatteringLut");

            if (aerialPerspectiveLutCompute != null)
            {
                m_AerialPerspectiveKernel =
                    aerialPerspectiveLutCompute.FindKernel("ComputeAerialPerspectiveLUT");
                RenderingUtils.ReAllocateIfNeeded(
                    ref m_AerialPerspectiveLut, s_AerialPerspectiveLutDesc,
                    FilterMode.Bilinear, TextureWrapMode.Clamp, name: "AerialPerspectiveLut");
            }

            RenderingUtils.ReAllocateIfNeeded(
                ref m_SkyViewLut, s_SkyViewLutDesc,
                FilterMode.Bilinear, TextureWrapMode.Clamp, name: "SkyViewLut");

            m_Initialized = true;
        }

        [Obsolete]
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            InitializeIfNeeded();
        }

        [Obsolete]
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            InitializeIfNeeded();
            if (settings == null) return;

            var cmd = CommandBufferPool.Get("Atmosphere LUTs");

            // ── Parameters from settings ──────────────────────────────────
            var planetRadius = settings.planetRadius;
            var atmosphereHeight = settings.atmosphereHeight;
            var atmosphereRadius = planetRadius + atmosphereHeight;
            var scaleHeight = settings.scaleHeight;
            var mieScaleHeight = settings.mieScaleHeight;
            var mieG = settings.mieG;
            var sunIntensity = settings.sunIntensity;
            var sunDiskAngle = settings.sunDiskAngle;
            var sunLightColor = settings.sunLightColor;
            var apIntensity = settings.apIntensity;
            var viewSamples = settings.viewSamples;
            var lightSamples = settings.lightSamples;

            // ── Camera height ─────────────────────────────────────────────
            var cam = renderingData.cameraData.camera;
            var cameraHeight = Mathf.Max(cam.transform.position.magnitude, 0f);

            // ── Sun direction ─────────────────────────────────────────────
            Vector3 sunDir;
            var sun = RenderSettings.sun;
            if (sun != null)
                sunDir = -sun.transform.forward;
            else
                sunDir = Shader.GetGlobalVector("_MainLightPosition").normalized;

            // ══════════════════════════════════════════════════════════════
            // 1. Generate Optical Depth LUT
            // ══════════════════════════════════════════════════════════════
            cmd.SetComputeFloatParam(opticalDepthLutCompute, s_PlanetRadiusId, planetRadius);
            cmd.SetComputeFloatParam(opticalDepthLutCompute, s_AtmosphereRadiusId, atmosphereRadius);
            cmd.SetComputeFloatParam(opticalDepthLutCompute, s_ScaleHeightId, scaleHeight);
            cmd.SetComputeFloatParam(opticalDepthLutCompute, s_MieScaleHeightId, mieScaleHeight);
            cmd.SetComputeFloatParam(opticalDepthLutCompute, s_LightSamplesId, lightSamples);
            cmd.SetComputeFloatParam(opticalDepthLutCompute, s_InvOpticalDepthLutSizeId, 1f / k_OpticalDepthLutSize);
            cmd.SetComputeTextureParam(opticalDepthLutCompute, m_OpticalDepthKernel,
                s_OpticalDepthLutId, m_OpticalDepthLut);

            var tgT = Mathf.CeilToInt(k_OpticalDepthLutSize / 8f);
            cmd.DispatchCompute(opticalDepthLutCompute, m_OpticalDepthKernel, tgT, tgT, 1);

            // ══════════════════════════════════════════════════════════════
            // 2. Generate Multi-Scattering LUT
            // ══════════════════════════════════════════════════════════════
            if (multiScatteringLutCompute != null)
            {
                var mulSC = multiScatteringLutCompute;
                cmd.SetComputeFloatParam(mulSC, s_PlanetRadiusId, planetRadius);
                cmd.SetComputeFloatParam(mulSC, s_AtmosphereHeightId, atmosphereHeight);
                cmd.SetComputeFloatParam(mulSC, s_ScaleHeightId, scaleHeight);
                cmd.SetComputeFloatParam(mulSC, s_MieScaleHeightId, mieScaleHeight);
                cmd.SetComputeFloatParam(mulSC, s_MieGId, mieG);
                cmd.SetComputeTextureParam(mulSC, m_MultiScatteringKernel,
                    s_OpticalDepthLutId, m_OpticalDepthLut);
                cmd.SetComputeTextureParam(mulSC, m_MultiScatteringKernel,
                    s_MultiScatteringLutId, m_MultiScatteringLut);

                var tgMS = Mathf.CeilToInt(k_MultiScatteringLutSize / 4f);
                cmd.DispatchCompute(mulSC, m_MultiScatteringKernel, tgMS, tgMS, 1);
            }

            // ══════════════════════════════════════════════════════════════
            // 3. Generate Sky View LUT
            // ══════════════════════════════════════════════════════════════
            cmd.SetComputeFloatParam(skyViewLutCompute, s_PlanetRadiusId, planetRadius);
            cmd.SetComputeFloatParam(skyViewLutCompute, s_AtmosphereHeightId, atmosphereHeight);
            cmd.SetComputeFloatParam(skyViewLutCompute, s_ScaleHeightId, scaleHeight);
            cmd.SetComputeFloatParam(skyViewLutCompute, s_MieScaleHeightId, mieScaleHeight);
            cmd.SetComputeFloatParam(skyViewLutCompute, s_MieGId, mieG);
            cmd.SetComputeFloatParam(skyViewLutCompute, s_SunIntensityId, sunIntensity);
            cmd.SetComputeFloatParam(skyViewLutCompute, s_SunDiskAngleId, sunDiskAngle);
            cmd.SetComputeFloatParam(skyViewLutCompute, s_ViewSamplesId, viewSamples);
            cmd.SetComputeFloatParam(skyViewLutCompute, s_CameraHeightId, cameraHeight);
            cmd.SetComputeVectorParam(skyViewLutCompute, s_SunDirectionId, sunDir);
            cmd.SetComputeTextureParam(skyViewLutCompute, m_SkyViewKernel, s_SkyViewLutId, m_SkyViewLut);
            cmd.SetComputeTextureParam(skyViewLutCompute, m_SkyViewKernel, s_OpticalDepthLutId, m_OpticalDepthLut);
            cmd.SetComputeTextureParam(skyViewLutCompute, m_SkyViewKernel,
                s_MultiScatteringLutId, m_MultiScatteringLut);
            cmd.SetComputeVectorParam(skyViewLutCompute, s_SunLightColorId, sunLightColor);

            var tgX = Mathf.CeilToInt(k_SkyViewLutWidth / 8f);
            var tgY = Mathf.CeilToInt(k_SkyViewLutHeight / 8f);
            cmd.DispatchCompute(skyViewLutCompute, m_SkyViewKernel, tgX, tgY, 1);

            // ══════════════════════════════════════════════════════════════
            // 4. Generate Aerial Perspective LUT
            //    Per-voxel integration with variable sample count:
            //    near slices = 2 steps, far slices = 128 steps.
            //    Squared depth distribution for near-field precision.
            // ══════════════════════════════════════════════════════════════
            if (enableAerialPerspective && aerialPerspectiveLutCompute != null
                && !settings.enableTerrainShadow)
            {
                var ap = aerialPerspectiveLutCompute;

                // GPU-adjusted inverse projection (with D3D Y-flip) passed
                // explicitly to avoid the ambiguity of unity_CameraInvProjection
                // in compute shaders.
                var gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
                cmd.SetComputeMatrixParam(ap, s_InvProjectionMatrixId, gpuProj.inverse);
                cmd.SetComputeMatrixParam(ap, "_InvCameraViewMatrix", cam.worldToCameraMatrix.inverse);
                cmd.SetComputeFloatParam(ap, s_PlanetRadiusId, planetRadius);
                cmd.SetComputeFloatParam(ap, s_AtmosphereHeightId, atmosphereHeight);
                cmd.SetComputeFloatParam(ap, s_ScaleHeightId, scaleHeight);
                cmd.SetComputeFloatParam(ap, s_MieScaleHeightId, mieScaleHeight);
                cmd.SetComputeFloatParam(ap, s_MieGId, mieG);
                cmd.SetComputeFloatParam(ap, s_SunIntensityId, sunIntensity);
                cmd.SetComputeFloatParam(ap, s_SunDiskAngleId, sunDiskAngle);
                cmd.SetComputeVectorParam(ap, s_SunDirectionId, sunDir);
                cmd.SetComputeVectorParam(ap, s_SunLightColorId, sunLightColor);
                cmd.SetComputeFloatParam(ap, s_zFarPropId, 100000f);

                // Input textures
                cmd.SetComputeTextureParam(ap, m_AerialPerspectiveKernel,
                    s_OpticalDepthLutId, m_OpticalDepthLut);
                cmd.SetComputeTextureParam(ap, m_AerialPerspectiveKernel,
                    s_MultiScatteringLutId, m_MultiScatteringLut);

                // Output
                cmd.SetComputeTextureParam(ap, m_AerialPerspectiveKernel,
                    s_AerialPerspectiveLutId, m_AerialPerspectiveLut);

                var apTgX = Mathf.CeilToInt(k_AerialLutWidth / 8f);
                var apTgY = Mathf.CeilToInt(k_AerialLutHeight / 8f);
                var apTgZ = Mathf.CeilToInt(k_AerialLutDepth / 8f);
                cmd.DispatchCompute(ap, m_AerialPerspectiveKernel, apTgX, apTgY, apTgZ);
            }

            // ══════════════════════════════════════════════════════════════
            // 5. Set global textures & properties for skybox shader
            // ══════════════════════════════════════════════════════════════
            cmd.SetGlobalTexture(s_SkyViewLutId, m_SkyViewLut);
            cmd.SetGlobalTexture(s_OpticalDepthLutId, m_OpticalDepthLut);
            cmd.SetGlobalTexture(s_MultiScatteringLutId, m_MultiScatteringLut);
            if (m_AerialPerspectiveLut != null)
            {
                cmd.SetGlobalTexture(s_AerialPerspectiveLutId, m_AerialPerspectiveLut);
                cmd.SetGlobalInt(s_AerialLutWidthId, k_AerialLutWidth);
                cmd.SetGlobalInt(s_AerialLutHeightId, k_AerialLutHeight);
                cmd.SetGlobalInt(s_AerialLutDepthId, k_AerialLutDepth);
            }

            cmd.SetGlobalFloat(s_PlanetRadiusId, planetRadius);
            cmd.SetGlobalFloat(s_AtmosphereHeightId, atmosphereHeight);
            cmd.SetGlobalFloat(s_AtmosphereRadiusId, atmosphereRadius);
            cmd.SetGlobalFloat(s_MieScaleHeightId, mieScaleHeight);
            cmd.SetGlobalFloat(s_ScaleHeightId, scaleHeight);
            cmd.SetGlobalFloat(s_MieGId, mieG);
            cmd.SetGlobalFloat(s_SunDiskAngleId, sunDiskAngle);
            cmd.SetGlobalFloat(s_SunIntensityId, sunIntensity);
            cmd.SetGlobalFloat(s_APIntensityId, apIntensity);
            cmd.SetGlobalColor(s_SunLightColorId, sunLightColor);
            cmd.SetGlobalVector(s_SunDirectionId, sunDir);
            cmd.SetGlobalFloat(s_zFarPropId, 100000f);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            // ── Auto-assign skybox material (once) ────────────────────────
            if (skyboxMaterial != null && RenderSettings.skybox != skyboxMaterial)
                RenderSettings.skybox = skyboxMaterial;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Aerial Perspective Composite Pass
    // Follows URP FullScreenPass pattern: copy camera color to temp RT,
    // bind as _BlitTexture, then draw full-screen quad with composite material.
    // ═════════════════════════════════════════════════════════════════════
    private class AerialPerspectiveCompositePass : ScriptableRenderPass
    {
        private static readonly int s_zNearId = Shader.PropertyToID("_zNear");
        private static readonly int s_zFarId = Shader.PropertyToID("_zFar");
        private static readonly int s_BlitTextureId = Shader.PropertyToID("_BlitTexture");
        private static readonly int s_BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        private static readonly int s_DebugModeId = Shader.PropertyToID("_DebugMode");
        private static readonly int s_EnableTerrainShadowId = Shader.PropertyToID("_EnableTerrainShadow");
        private static readonly MaterialPropertyBlock s_PropertyBlock = new();

        public Material compositeMaterial;
        public bool enableTerrainShadow;
        private RTHandle m_CopiedColor;

        public AerialPerspectiveCompositePass()
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ResetTarget();
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.msaaSamples = 1;
            desc.depthBufferBits = (int)DepthBits.None;
            RenderingUtils.ReAllocateIfNeeded(ref m_CopiedColor, desc, name: "_AerialPerspectiveColorCopy");
        }

        public void Dispose()
        {
            m_CopiedColor?.Release();
            m_CopiedColor = null;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (compositeMaterial == null) return;

            var cmd = CommandBufferPool.Get("Aerial Perspective Composite");
            var cam = renderingData.cameraData.camera;
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            cmd.SetGlobalFloat(s_zNearId, cam.nearClipPlane);
            var zFar = 100000f;
            cmd.SetGlobalFloat(s_zFarId, zFar);

            // Ensure debug mode is off during normal composite
            cmd.SetGlobalInt(s_DebugModeId, 0);

            // Toggle between LUT mode (fast) and ray-march mode (terrain-aware)
            if (enableTerrainShadow)
                compositeMaterial.EnableKeyword("SHADOWMAP_ENABLED");
            else
                compositeMaterial.DisableKeyword("SHADOWMAP_ENABLED");
            cmd.SetGlobalFloat(s_EnableTerrainShadowId, enableTerrainShadow ? 1.0f : 0.0f);

            // 1. Copy camera color to temp RT (avoids read-write hazard)
            CoreUtils.SetRenderTarget(cmd, m_CopiedColor);
            Blitter.BlitTexture(cmd, cameraTarget, new Vector4(1, 1, 0, 0), 0.0f, false);

            // 2. Set render target back to camera color for output
            CoreUtils.SetRenderTarget(cmd, cameraTarget);

            // 3. Bind copied color as _BlitTexture for the shader
            s_PropertyBlock.Clear();
            s_PropertyBlock.SetTexture(s_BlitTextureId, m_CopiedColor);
            s_PropertyBlock.SetVector(s_BlitScaleBiasId, new Vector4(1, 1, 0, 0));

            // 4. Draw full-screen composite
            cmd.DrawProcedural(Matrix4x4.identity, compositeMaterial, 0,
                MeshTopology.Triangles, 3, 1, s_PropertyBlock);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Debug LUT Overlay Pass
    // Renders the selected LUT as a full-screen overlay, after all other
    // rendering. Runs only when debug mode != None.
    // ═════════════════════════════════════════════════════════════════════
    private class DebugLutPass : ScriptableRenderPass
    {
        private static readonly int s_DebugModeId = Shader.PropertyToID("_DebugMode");
        private static readonly int s_DebugSliceZId = Shader.PropertyToID("_DebugSliceZ");

        private static readonly MaterialPropertyBlock s_PropertyBlock = new();

        public Material debugMaterial;
        public AtmosphereDebugMode debugMode;
        public float debugSliceZ;

        public DebugLutPass()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        }

        public void Dispose()
        {
            if (debugMaterial != null)
            {
                CoreUtils.Destroy(debugMaterial);
                debugMaterial = null;
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (debugMaterial == null) return;

            var cmd = CommandBufferPool.Get("Debug LUT Overlay");
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            cmd.SetGlobalInt(s_DebugModeId, (int)debugMode);
            cmd.SetGlobalFloat(s_DebugSliceZId, debugSliceZ);

            CoreUtils.SetRenderTarget(cmd, cameraTarget);
            cmd.DrawProcedural(Matrix4x4.identity, debugMaterial, 0,
                MeshTopology.Triangles, 3, 1, s_PropertyBlock);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}