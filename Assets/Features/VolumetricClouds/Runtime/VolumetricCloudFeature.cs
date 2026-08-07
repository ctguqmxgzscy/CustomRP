using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
///     Volumetric cloud RenderFeature（Nubis 风格 2.5D）。
///     每帧流程：
///     1. Dispatch CloudRayMarch.compute 全屏 → _CloudTarget (RGBA: RGB=散射色, A=不透明度)
///     2. 将 _CloudTarget 与场景颜色合成（lerp(scene, cloud.rgb, cloud.a)）
///     几何约定：行星中心在世界原点，相机在行星球内（背面剔除 → 深度 far）。
/// </summary>
public class VolumetricCloudFeature : ScriptableRendererFeature
{
    [Header("Assets")] [SerializeField] private ComputeShader m_CloudRayMarchCompute;

    [SerializeField] private Shader m_CompositeShader;

    [Header("Shared")] [Tooltip("大气散射预设（Atmosphere Horizontal）：planetRadius/atmosphereHeight/太阳/颜色 等公共参数统一从这里读，保证与大气管线一致。为空时回退到下方字段")]
    [SerializeField] private AtmosphereSettings m_AtmosphereSettings;

    [Header("Sun")] [SerializeField] private Color m_SunColor = Color.white;

    [SerializeField] [Range(0, 20)] private float m_SunIntensity = 3f;

    [Header("Planet")] [SerializeField] private float m_PlanetRadius = 6371000f;

    [SerializeField] private float m_CloudHeightMin = 1000f;
    [SerializeField] private float m_CloudHeightMax = 3000f;

    [Header("Density")] [SerializeField] private float m_CloudDensityScale = 1f;

    [Header("Cloud Type")] [Tooltip("层云高度范围（云层内归一化 0~1）：x=云底, y=云顶")] [SerializeField]
    private Vector2 m_StratusRange = new(0f, 0.3f);

    [Tooltip("层云边缘羽化强度")] [SerializeField] private float m_StratusFeather = 0.1f;

    [Tooltip("积云高度范围（云层内归一化 0~1）：x=云底, y=云顶")] [SerializeField]
    private Vector2 m_CumulusRange = new(0.05f, 0.95f);

    [Tooltip("积云边缘羽化强度")] [SerializeField] private float m_CumulusFeather = 0.1f;

    [Header("Noise")] [Tooltip("预烘焙 3D 噪声纹理（Tools/Volumetric Clouds 菜单生成）")] [SerializeField]
    private Texture3D m_ShapeNoiseTex;

    [SerializeField] private Texture3D m_DetailNoiseTex;

    [Header("Weather Map")]
    [Tooltip("天气图（2D）：R=云覆盖率, G=吸收率(降雨), B=云型(0=层云~1=积云)。世界 XZ 平铺采样，Tools/Volumetric Clouds 菜单可烘焙示例图")]
    [SerializeField]
    private Texture2D m_WeatherMapTex;

    [Tooltip("天气图平铺缩放：UV = posWS.xz × scale（0.00002 ≈ 50km 一个周期）")] [SerializeField]
    private float m_WeatherMapScale = 0.00002f;

    [Tooltip("覆盖率/吸收率/云型调整：0~0.5~1 => 0~天气图对应通道~1（0.5 保持原值）")] [SerializeField] [Range(0, 1)]
    private float m_CloudDensityAdjust = 0.5f;

    [Tooltip("吸收强度：G 通道=1（强降雨）时消光放大倍数")] [SerializeField]
    private float m_AbsorptionStrength = 3f;

    [Header("Wind")] [Tooltip("风向（世界坐标，归一化方向）")] [SerializeField]
    private Vector3 m_WindDirection = new(1, 0, 0);

    [Tooltip("风速 m/s（参考默认 10）")] [SerializeField]
    private float m_WindSpeed = 10f;

    [Tooltip("采样坐标缩放：纹理 UV = posWS × scale（默认 10000m 一个周期）")] [SerializeField]
    private float m_ShapeNoiseScale = 0.0001f;

    [SerializeField] private float m_DetailNoiseScale = 0.0003f;

    [Tooltip("基形 Worley FBM 侵蚀强度 (0~1)")] [SerializeField] [Range(0, 1)]
    private float m_BaseShapeDetailEffect = 0.5f;

    [Tooltip("细节侵蚀强度 (0~1)")] [SerializeField] [Range(0, 1)]
    private float m_DetailEffect = 1f;

    [Header("March")] [SerializeField] [Range(16, 256)]
    private int m_ShapeStepCount = 64;

    [Tooltip("光照步进数（T_CP light march，太阳方向密度采样次数）")] [SerializeField] [Range(4, 64)]
    private int m_LightStepCount = 8;

    [SerializeField] private float m_ExtinctionCoeff = 0.005f;
    [SerializeField] private float m_ScatterCoeff = 0.004f;

    [Tooltip("双 HG 前向散射强度权重（Max 混合，艺术调参）")] [SerializeField]
    private float m_MieForwardScatter = 1f;

    [Tooltip("双 HG 前向散射 g（Nubis 2017 主相位，太阳方向强峰）")] [SerializeField] [Range(0, 1)]
    private float m_MieG = 0.6f;

    [Tooltip("双 HG 后向散射 g（Nubis 2017 第二相位，负值产生背向峰）")] [SerializeField] [Range(-1, 0)]
    private float m_MieG2 = -0.3f;

    [Tooltip("双 HG 后向散射强度权重（Max 混合，艺术调参）")] [SerializeField]
    private float m_MieBackScatter = 0.5f;

    private CloudRayMarchPass m_Pass;

    public override void Create()
    {
        // 关键：Create 会被 Unity 在 asset 序列化变化（如 Inspector 拖参数）时重复调用，
        // 旧 pass 持有的 RT（m_CloudTarget ~16MB + m_CopiedColor）是 Native 资源，
        // 不手动释放会每次泄漏 → 编辑器内存耗尽卡死
        m_Pass?.Dispose();
        m_Pass = new CloudRayMarchPass
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents,
            computeShader = m_CloudRayMarchCompute,
            compositeShader = m_CompositeShader
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_CloudRayMarchCompute == null || m_CompositeShader == null)
            return;
        m_Pass.Setup(this);
        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
        m_Pass?.Dispose();
        m_Pass = null;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Render Pass
    // ═════════════════════════════════════════════════════════════════════
    private class CloudRayMarchPass : ScriptableRenderPass
    {
        private const int k_ThreadGroupSize = 8;

        private static readonly int s_CloudTargetId = Shader.PropertyToID("_CloudTarget");
        private static readonly int s_SunColorId = Shader.PropertyToID("_SunColor");
        private static readonly int s_SunIntensityId = Shader.PropertyToID("_SunIntensity");
        private static readonly int s_PlanetRadiusId = Shader.PropertyToID("_PlanetRadius");
        private static readonly int s_AtmosphereRadiusId = Shader.PropertyToID("_AtmosphereRadius");
        private static readonly int s_CloudHeightMinId = Shader.PropertyToID("_CloudHeightMin");
        private static readonly int s_CloudHeightMaxId = Shader.PropertyToID("_CloudHeightMax");
        private static readonly int s_CloudDensityScaleId = Shader.PropertyToID("_CloudDensityScale");
        private static readonly int s_StratusRangeId = Shader.PropertyToID("_StratusRange");
        private static readonly int s_StratusFeatherId = Shader.PropertyToID("_StratusFeather");
        private static readonly int s_CumulusRangeId = Shader.PropertyToID("_CumulusRange");
        private static readonly int s_CumulusFeatherId = Shader.PropertyToID("_CumulusFeather");
        private static readonly int s_ShapeNoiseTexId = Shader.PropertyToID("_ShapeNoiseTex");
        private static readonly int s_DetailNoiseTexId = Shader.PropertyToID("_DetailNoiseTex");
        private static readonly int s_ShapeNoiseScaleId = Shader.PropertyToID("_ShapeNoiseScale");
        private static readonly int s_DetailNoiseScaleId = Shader.PropertyToID("_DetailNoiseScale");
        private static readonly int s_WeatherMapTexId = Shader.PropertyToID("_WeatherMapTex");
        private static readonly int s_WeatherMapScaleId = Shader.PropertyToID("_WeatherMapScale");
        private static readonly int s_CloudDensityAdjustId = Shader.PropertyToID("_CloudDensityAdjust");
        private static readonly int s_AbsorptionStrengthId = Shader.PropertyToID("_AbsorptionStrength");
        private static readonly int s_WindDirectionId = Shader.PropertyToID("_WindDirection");
        private static readonly int s_WindSpeedId = Shader.PropertyToID("_WindSpeed");
        private static readonly int s_BaseShapeDetailEffectId = Shader.PropertyToID("_BaseShapeDetailEffect");
        private static readonly int s_DetailEffectId = Shader.PropertyToID("_DetailEffect");
        private static readonly int s_ShapeStepCountId = Shader.PropertyToID("_ShapeStepCount");
        private static readonly int s_LightStepCountId = Shader.PropertyToID("_LightStepCount");
        private static readonly int s_ExtinctionCoeffId = Shader.PropertyToID("_ExtinctionCoeff");
        private static readonly int s_ScatterCoeffId = Shader.PropertyToID("_ScatterCoeff");
        private static readonly int s_MieGId = Shader.PropertyToID("_MieG");
        private static readonly int s_MieG2Id = Shader.PropertyToID("_MieG2");
        private static readonly int s_MieForwardScatterId = Shader.PropertyToID("_MieForwardScatter");
        private static readonly int s_MieBackScatterId = Shader.PropertyToID("_MieBackScatter");
        private static readonly int s_FrameCountId = Shader.PropertyToID("_FrameCount");
        public Shader compositeShader;

        public ComputeShader computeShader;
        private RTHandle m_CloudTarget;
        private Material m_CompositeMaterial;
        private RTHandle m_CopiedColor;

        private VolumetricCloudFeature m_Feature;
        private int m_Kernel;

        public void Setup(VolumetricCloudFeature feature)
        {
            m_Feature = feature;
        }

        public void Dispose()
        {
            m_CloudTarget?.Release();
            m_CloudTarget = null;
            m_CopiedColor?.Release();
            m_CopiedColor = null;
            if (m_CompositeMaterial != null)
            {
                CoreUtils.Destroy(m_CompositeMaterial);
                m_CompositeMaterial = null;
            }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            // 请求深度纹理（_CameraDepthTexture），并确保该帧生成
            ConfigureInput(ScriptableRenderPassInput.Depth);

            if (m_Kernel == 0)
                m_Kernel = computeShader.FindKernel("CSMain");

            // 云目标：相机分辨率、可随机写
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.msaaSamples = 1;
            desc.depthBufferBits = (int)DepthBits.None;
            desc.colorFormat = RenderTextureFormat.ARGBHalf;
            desc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref m_CloudTarget, desc, name: "CloudTarget");

            // 合成用场景颜色副本（避免读写冲突）
            var copyDesc = desc;
            copyDesc.enableRandomWrite = false;
            copyDesc.colorFormat = renderingData.cameraData.cameraTargetDescriptor.colorFormat;
            RenderingUtils.ReAllocateIfNeeded(ref m_CopiedColor, copyDesc, name: "CloudColorCopy");

            if (m_CompositeMaterial == null && compositeShader != null)
                m_CompositeMaterial = new Material(compositeShader);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (computeShader == null || m_CompositeMaterial == null) return;

            var cmd = CommandBufferPool.Get("Volumetric Clouds");
            var cam = renderingData.cameraData.camera;

            // ── 参数 ──────────────────────────────────────────────────────
            var f = m_Feature;

            // 公共参数从大气预设读（共享 Atmosphere Horizontal），为空时回退到 Feature 字段
            var atmo = f.m_AtmosphereSettings;
            var sunColor = atmo != null ? atmo.sunLightColor : f.m_SunColor;
            var sunIntensity = atmo != null ? atmo.sunIntensity : f.m_SunIntensity;
            var planetRadius = atmo != null ? atmo.planetRadius : f.m_PlanetRadius;
            var mieG = atmo != null ? atmo.mieG : f.m_MieG;
            var atmosphereRadius = atmo != null ? atmo.planetRadius + atmo.atmosphereHeight : 0f;

            cmd.SetComputeVectorParam(computeShader, s_SunColorId, sunColor);
            cmd.SetComputeFloatParam(computeShader, s_SunIntensityId, sunIntensity);
            cmd.SetComputeFloatParam(computeShader, s_PlanetRadiusId, planetRadius);
            cmd.SetComputeFloatParam(computeShader, s_AtmosphereRadiusId, atmosphereRadius);
            cmd.SetComputeFloatParam(computeShader, s_CloudHeightMinId, f.m_CloudHeightMin);
            cmd.SetComputeFloatParam(computeShader, s_CloudHeightMaxId, f.m_CloudHeightMax);
            cmd.SetComputeFloatParam(computeShader, s_CloudDensityScaleId, f.m_CloudDensityScale);
            cmd.SetComputeVectorParam(computeShader, s_StratusRangeId, f.m_StratusRange);
            cmd.SetComputeFloatParam(computeShader, s_StratusFeatherId, f.m_StratusFeather);
            cmd.SetComputeVectorParam(computeShader, s_CumulusRangeId, f.m_CumulusRange);
            cmd.SetComputeFloatParam(computeShader, s_CumulusFeatherId, f.m_CumulusFeather);
            if (f.m_ShapeNoiseTex != null)
                cmd.SetComputeTextureParam(computeShader, m_Kernel, s_ShapeNoiseTexId, f.m_ShapeNoiseTex);
            if (f.m_DetailNoiseTex != null)
                cmd.SetComputeTextureParam(computeShader, m_Kernel, s_DetailNoiseTexId, f.m_DetailNoiseTex);
            cmd.SetComputeFloatParam(computeShader, s_ShapeNoiseScaleId, f.m_ShapeNoiseScale);
            cmd.SetComputeFloatParam(computeShader, s_DetailNoiseScaleId, f.m_DetailNoiseScale);
            if (f.m_WeatherMapTex != null)
                cmd.SetComputeTextureParam(computeShader, m_Kernel, s_WeatherMapTexId, f.m_WeatherMapTex);
            cmd.SetComputeFloatParam(computeShader, s_WeatherMapScaleId, f.m_WeatherMapScale);
            cmd.SetComputeFloatParam(computeShader, s_CloudDensityAdjustId, f.m_CloudDensityAdjust);
            cmd.SetComputeFloatParam(computeShader, s_AbsorptionStrengthId, f.m_AbsorptionStrength);
            cmd.SetComputeVectorParam(computeShader, s_WindDirectionId, f.m_WindDirection);
            cmd.SetComputeFloatParam(computeShader, s_WindSpeedId, f.m_WindSpeed);
            cmd.SetComputeFloatParam(computeShader, s_BaseShapeDetailEffectId, f.m_BaseShapeDetailEffect);
            cmd.SetComputeFloatParam(computeShader, s_DetailEffectId, f.m_DetailEffect);
            cmd.SetComputeFloatParam(computeShader, s_ShapeStepCountId, f.m_ShapeStepCount);
            cmd.SetComputeFloatParam(computeShader, s_LightStepCountId, f.m_LightStepCount);
            cmd.SetComputeFloatParam(computeShader, s_ExtinctionCoeffId, f.m_ExtinctionCoeff);
            cmd.SetComputeFloatParam(computeShader, s_ScatterCoeffId, f.m_ScatterCoeff);
            cmd.SetComputeFloatParam(computeShader, s_MieGId, mieG);
            cmd.SetComputeFloatParam(computeShader, s_MieG2Id, f.m_MieG2);
            cmd.SetComputeFloatParam(computeShader, s_MieForwardScatterId, f.m_MieForwardScatter);
            cmd.SetComputeFloatParam(computeShader, s_MieBackScatterId, f.m_MieBackScatter);
            // 帧计数：供 InterleavedGradientNoise 逐帧变化（蓝噪声抖动，配合 TAA 消除层次感）
            cmd.SetComputeFloatParam(computeShader, s_FrameCountId, Time.frameCount);

            // ── Dispatch 全屏 ─────────────────────────────────────────────
            cmd.SetComputeTextureParam(computeShader, m_Kernel, s_CloudTargetId, m_CloudTarget);

            var width = m_CloudTarget.rt.width;
            var height = m_CloudTarget.rt.height;
            cmd.DispatchCompute(computeShader, m_Kernel,
                Mathf.CeilToInt(width / (float)k_ThreadGroupSize),
                Mathf.CeilToInt(height / (float)k_ThreadGroupSize), 1);

            // ── 合成到相机颜色 ────────────────────────────────────────────
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            // 1. 拷贝场景颜色到临时 RT（避免读写冲突，官方 Blit 处理平台翻转）
            CoreUtils.SetRenderTarget(cmd, m_CopiedColor);
            Blitter.BlitTexture(cmd, cameraTarget, new Vector4(1, 1, 0, 0), 0.0f, false);

            cmd.SetGlobalTexture(s_CloudTargetId, m_CloudTarget);
            Blitter.BlitTexture(cmd, m_CopiedColor, cameraTarget, m_CompositeMaterial, 0);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}