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
    /// <summary>云光照模式（compute shader _CloudLightingMode 驱动）</summary>
    public enum CloudLightingMode
    {
        BeerLambertCone = 0, // 现有：BeerLambert 光锥 + Powder + 双 HG 相位 + 大气 LUT 环境光
        HillaireMS = 1 // 方式 B：单散射 J（EvaluateCloudInScattering）+ 多次散射补益 ms（octave≥1）
    }

    /// <summary>云渲染路径：compute 全屏 dispatch 或 fragment 全屏三角形（CloudRayMarchFragment.shader）</summary>
    public enum CloudRenderMode
    {
        ComputeShader = 0, // 默认：CloudRayMarchTest.compute 全屏 dispatch
        FragmentShader = 1 // CloudRayMarchFragment.shader（与 compute 逻辑一致，调试/对照用）
    }

    [Header("Assets")] [SerializeField] private ComputeShader m_CloudRayMarchCompute;

    [SerializeField] private Shader m_CompositeShader;

    [Header("Render Mode")]
    [Tooltip("云渲染路径：ComputeShader（默认，全屏 dispatch）或 FragmentShader（CloudRayMarchFragment.shader，逻辑一致，调试/对照用）")]
    [SerializeField]
    private CloudRenderMode m_RenderMode = CloudRenderMode.ComputeShader;

    [Tooltip("Fragment 版 shader（CloudRayMarchFragment.shader）——Render Mode = FragmentShader 时使用")] [SerializeField]
    private Shader m_FragmentShader;

    [Header("Performance")]
    [Tooltip("分帧渲染（棋盘格时间切片 + 时间重投影）：每帧只计算 1/N² 像素（N=4 时约 16x 性能），旧帧结果经重投影搬移到当前相机位置消除拖影。仅 ComputeShader 模式生效")]
    [SerializeField]
    private bool m_TimeSlicing = false;

    [Tooltip("分帧粒度：2=2×2 块（每帧 1/4 像素，4 帧收敛）4=4×4 块（每帧 1/16 像素，16 帧收敛，性能最优）")]
    [SerializeField]
    [Range(2, 4)]
    private int m_InterleaveSize = 4;

    [Tooltip("转动自适应分级：角速度 > Fast 阈值 → 全帧；> Slow 阈值 → N=2；否则配置 N。验证其他方案时可关闭（固定分帧）")]
    [SerializeField]
    private bool m_RotationAdaptive = true;

    [Tooltip("相机转动分级（度/帧）：角速度 > Fast 阈值 → 全帧渲染；> Slow 阈值 → N=2（1/4 像素/帧）；否则配置 N（1/16）")]
    [SerializeField]
    [Range(0.1f, 5f)]
    private float m_FastRotationThreshold = 1.5f;

    [Tooltip("相机转动阈值（度/帧）：超过则 N=2（1/4 像素/帧，4 帧收敛——鬼影折中），静止恢复配置 N")]
    [SerializeField]
    [Range(0.05f, 2f)]
    private float m_RotationThreshold = 0.5f;

    [Tooltip("重投影遮挡边缘阈值（3×3 深度跨度，0~1）：超过则保留原地——消云/地形边缘缝隙（越大越少触发，缝隙可能残留）")]
    [SerializeField]
    [Range(0f, 1f)]
    private float m_ReprojEdgeThreshold = 0.1f;

    [Header("Debug")]
    [Tooltip("分帧调试：只显示本帧 march 的目标像素（非目标像素透明，场景露出）——验证分帧覆盖分布（LUT 对角/十字散布）")]
    [SerializeField]
    private bool m_DebugSlice = false;

    [Header("Shared")]
    [Tooltip("大气散射预设（Atmosphere Horizontal）：planetRadius/atmosphereHeight/太阳/颜色 等公共参数统一从这里读，保证与大气管线一致。为空时回退到下方字段")]
    [SerializeField]
    private AtmosphereSettings m_AtmosphereSettings;

    [Header("Sun")] [SerializeField] private Color m_SunColor = Color.white;

    [SerializeField] [Range(0, 20)] private float m_SunIntensity = 3f;

    [Tooltip("云底环境色（参考 _CloudBaseColor）")] [SerializeField]
    private Color m_CloudBaseColor = new(0.72f, 0.75f, 0.78f, 1f);

    [Tooltip("云顶环境色（参考 _CloudTopColor）")] [SerializeField]
    private Color m_CloudTopColor = new(0.95f, 0.95f, 1f, 1f);

    [Tooltip("环境光强度（参考 _AmbientLightFactor）")] [SerializeField] [Range(0f, 1f)]
    private float m_AmbientLightFactor = 0.4f;

    [Header("Lighting Mode")] [Tooltip("云光照模式：BeerLambertCone（光锥单散射+粉糖）或 HillaireMS（方式B：J + 多次散射补益）")] [SerializeField]
    private CloudLightingMode m_LightingMode = CloudLightingMode.BeerLambertCone;

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

    [Tooltip("全局云量（参考 _Coverage）：saturate(覆盖率 - coverage) 平移+截断。0=原图，越大云越少越薄（参考默认 0.92，我们天气图下需调低）")]
    [SerializeField]
    [Range(0.0f, 2.0f)]
    private float m_Coverage = 0.92f;

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

    [SerializeField] private float m_ExtinctionCoeff = 0.005f;
    [SerializeField] private float m_ScatterCoeff = 0.004f;

    [Tooltip("Beer 消光密度（参考 _Density）：与 CloudDensityScale（云量）独立——这个是消光强度，越大云越暗越实")] [SerializeField] [Range(0.1f, 5f)]
    private float m_Density = 1f;

    [Tooltip("主 HG 前向散射 g（Nubis 定向散射主 lobe）")] [SerializeField] [Range(0, 1)]
    private float m_MieG = 0.8f;

    [Tooltip("后向 HG 各向异性 g（参考 _HenyeyGreensteinGBackward，使用时取负；越大背光晕越强）")] [SerializeField] [Range(0, 1)]
    private float m_MieGBackward = 0.3f;

    [Tooltip("银边 lobe 强度（Nubis u_SilverIntensity；顺光峰值 = HG(0.99-spread) × 强度，过大易过曝）")] [SerializeField]
    private float m_SilverIntensity = 1f;

    [Tooltip("银边 lobe 宽度：g = 0.99 - spread（0.05 ≈ 极窄亮边，0.3 ≈ 宽光晕）")] [SerializeField] [Range(0.001f, 0.5f)]
    private float m_SilverSpread = 0.05f;

    [Header("Multi-Scattering")]
    [Tooltip("Hillaire 2020 三参数多重散射——消光衰减率（per octave）：越小高阶光路穿透越深，云内部越亮")]
    [SerializeField]
    [Range(0.05f, 1f)]
    private float m_MSAttenuation = 0.5f;

    [Tooltip("散射能量权重（per octave）：越大云内部越白，建议 0.5~0.9")] [SerializeField] [Range(0.05f, 1f)]
    private float m_MSContribution = 0.5f;

    [Tooltip("相函数偏心率衰减率（per octave）：越小高阶越接近各向同性")] [SerializeField] [Range(0.05f, 1f)]
    private float m_MSEccentricity = 0.5f;

    [Tooltip("光步长度（m，0-400）：光锥采样每步推进距离")] [SerializeField] [Range(0f, 400f)]
    private float m_LightStepLength = 100f;

    [Tooltip("光锥半径（0-1）：光锥采样扩散强度")] [SerializeField] [Range(0f, 1f)]
    private float m_LightConeRadius = 0.3f;

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
            compositeShader = m_CompositeShader,
            renderMode = m_RenderMode,
            fragmentShader = m_FragmentShader
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_CompositeShader == null)
            return;
        if (m_RenderMode == CloudRenderMode.ComputeShader && m_CloudRayMarchCompute == null)
            return;
        if (m_RenderMode == CloudRenderMode.FragmentShader && m_FragmentShader == null)
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
        private static readonly int s_CloudBaseColorId = Shader.PropertyToID("_CloudBaseColor");
        private static readonly int s_CloudTopColorId = Shader.PropertyToID("_CloudTopColor");
        private static readonly int s_AmbientLightFactorId = Shader.PropertyToID("_AmbientLightFactor");
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
        private static readonly int s_CoverageId = Shader.PropertyToID("_Coverage");
        private static readonly int s_AbsorptionStrengthId = Shader.PropertyToID("_AbsorptionStrength");
        private static readonly int s_WindDirectionId = Shader.PropertyToID("_WindDirection");
        private static readonly int s_WindSpeedId = Shader.PropertyToID("_WindSpeed");
        private static readonly int s_BaseShapeDetailEffectId = Shader.PropertyToID("_BaseShapeDetailEffect");
        private static readonly int s_DetailEffectId = Shader.PropertyToID("_DetailEffect");
        private static readonly int s_ShapeStepCountId = Shader.PropertyToID("_ShapeStepCount");
        private static readonly int s_ExtinctionCoeffId = Shader.PropertyToID("_ExtinctionCoeff");
        private static readonly int s_ScatterCoeffId = Shader.PropertyToID("_ScatterCoeff");
        private static readonly int s_DensityId = Shader.PropertyToID("_Density");
        private static readonly int s_MieGId = Shader.PropertyToID("_MieG");
        private static readonly int s_MieGBackwardId = Shader.PropertyToID("_MieGBackward");
        private static readonly int s_SilverIntensityId = Shader.PropertyToID("_SilverIntensity");
        private static readonly int s_SilverSpreadId = Shader.PropertyToID("_SilverSpread");
        private static readonly int s_MSAttenuationId = Shader.PropertyToID("_MSAttenuation");
        private static readonly int s_MSContributionId = Shader.PropertyToID("_MSContribution");
        private static readonly int s_MSEccentricityId = Shader.PropertyToID("_MSEccentricity");
        private static readonly int s_LightStepLengthId = Shader.PropertyToID("_LightStepLength");
        private static readonly int s_LightConeRadiusId = Shader.PropertyToID("_LightConeRadius");
        private static readonly int s_FrameCountId = Shader.PropertyToID("_FrameCount");
        private static readonly int s_LightingModeId = Shader.PropertyToID("_CloudLightingMode");
        private static readonly int s_CameraDepthTexId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int s_TimeSlicingId = Shader.PropertyToID("_TimeSlicing");
        private static readonly int s_InterleaveSizeId = Shader.PropertyToID("_InterleaveSize");
        private static readonly int s_DebugSliceId = Shader.PropertyToID("_DebugSlice");
        private static readonly int s_CloudTargetPrevTexId = Shader.PropertyToID("_CloudTargetPrevTex");
        private static readonly int s_ReprojEdgeThresholdId = Shader.PropertyToID("_ReprojEdgeThreshold");
        private static readonly int s_PrevViewProjId = Shader.PropertyToID("_PrevViewProjMatrix");
        private static readonly int s_NoJitterInvViewProjId = Shader.PropertyToID("_NoJitterInvViewProj");
        private static readonly int s_NoJitterInvProjId = Shader.PropertyToID("_NoJitterInvProj");
        public Shader compositeShader;
        public ComputeShader computeShader;
        public Shader fragmentShader;

        private RTHandle[] m_CloudTargets = new RTHandle[2]; // 乒乓双缓冲：引用固定（name 恒匹配），
                                                             // 角色由 m_CloudIndex 轮换——ReAllocateIfNeeded
                                                             // 的 name 判断永不误触发（分帧云淡的坑）
        private int m_CloudIndex; // 当前帧使用 m_CloudTargets[m_CloudIndex]
        private Material m_CompositeMaterial;
        private RTHandle m_CopiedColor;

        private VolumetricCloudFeature m_Feature;
        private Material m_FragmentMaterial;
        private int m_Kernel;
        private int m_ReprojectKernel = -1;
        private bool m_FirstFrame = true; // 首帧 prev 未初始化 → 跳过重投影全帧 march
        private bool m_LastFrameTimeSlicing; // 上一帧是否分帧（非分帧→分帧切换时 prev 过期 → 重建）
        private Vector3 m_PrevCameraForward = Vector3.forward; // 上一帧相机前向（转动检测）
        private Matrix4x4 m_PrevViewProj = Matrix4x4.identity; // 上一帧 VP（首帧用当前 VP → 重投影恒等）
        public CloudRenderMode renderMode;

        public void Setup(VolumetricCloudFeature feature)
        {
            m_Feature = feature;
        }

        public void Dispose()
        {
            for (int i = 0; i < m_CloudTargets.Length; i++)
            {
                m_CloudTargets[i]?.Release();
                m_CloudTargets[i] = null;
            }
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

            // 云目标：相机分辨率、可随机写。乒乓双缓冲引用固定（[0]=CloudTarget/[1]=CloudTargetPrev，
            // name 与字段恒匹配）——ReAllocateIfNeeded 会把 RTHandle 的 name 纳入重分配判断，
            // 交换引用会导致每帧误重分配清空 RT 内容（分帧云淡的坑）；角色由 m_CloudIndex 轮换。
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.msaaSamples = 1;
            desc.depthBufferBits = (int)DepthBits.None;
            desc.colorFormat = RenderTextureFormat.ARGBHalf;
            desc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref m_CloudTargets[0], desc, name: "CloudTarget");
            RenderingUtils.ReAllocateIfNeeded(ref m_CloudTargets[1], desc, name: "CloudTargetPrev");

            // 合成用场景颜色副本（避免读写冲突）
            var copyDesc = desc;
            copyDesc.enableRandomWrite = false;
            copyDesc.colorFormat = renderingData.cameraData.cameraTargetDescriptor.colorFormat;
            RenderingUtils.ReAllocateIfNeeded(ref m_CopiedColor, copyDesc, name: "CloudColorCopy");

            if (m_CompositeMaterial == null && compositeShader != null)
                m_CompositeMaterial = new Material(compositeShader);
            if (m_FragmentMaterial == null && fragmentShader != null)
                m_FragmentMaterial = new Material(fragmentShader);
        }

        /// <summary>
        ///     设置云渲染参数为全局 uniform（compute 与 fragment 版共用）：
        ///     compute shader 的 uniform 同样读取全局 uniform 缓冲（SetGlobal* 对后续 Dispatch 生效），
        ///     fragment 版（CloudRayMarchFragment.shader 挂材质）直接读取同名全局变量。
        ///     大气侧参数（_SkyViewLut/_OpticalDepthLUT 等）由 AtmosphereSkyboxLutFeature 每帧 SetGlobal，
        ///     此处只负责云自身的参数。
        /// </summary>
        private void ApplyCloudParams(CommandBuffer cmd)
        {
            var f = m_Feature;

            // 公共参数从大气预设读（共享 Atmosphere Horizontal），为空时回退到 Feature 字段
            var atmo = f.m_AtmosphereSettings;
            var sunColor = atmo != null ? atmo.sunLightColor : f.m_SunColor;
            var sunIntensity = atmo != null ? atmo.sunIntensity : f.m_SunIntensity;
            var planetRadius = atmo != null ? atmo.planetRadius : f.m_PlanetRadius;
            var mieG = atmo != null ? atmo.mieG : f.m_MieG;
            var atmosphereRadius = atmo != null ? atmo.planetRadius + atmo.atmosphereHeight : 0f;

            cmd.SetGlobalVector(s_SunColorId, sunColor);
            cmd.SetGlobalFloat(s_SunIntensityId, sunIntensity);
            cmd.SetGlobalVector(s_CloudBaseColorId, f.m_CloudBaseColor);
            cmd.SetGlobalVector(s_CloudTopColorId, f.m_CloudTopColor);
            cmd.SetGlobalFloat(s_AmbientLightFactorId, f.m_AmbientLightFactor);
            cmd.SetGlobalInt(s_LightingModeId, (int)f.m_LightingMode);
            cmd.SetGlobalFloat(s_PlanetRadiusId, planetRadius);
            cmd.SetGlobalFloat(s_AtmosphereRadiusId, atmosphereRadius);
            cmd.SetGlobalFloat(s_CloudHeightMinId, f.m_CloudHeightMin);
            cmd.SetGlobalFloat(s_CloudHeightMaxId, f.m_CloudHeightMax);
            cmd.SetGlobalFloat(s_CloudDensityScaleId, f.m_CloudDensityScale);
            cmd.SetGlobalVector(s_StratusRangeId, f.m_StratusRange);
            cmd.SetGlobalFloat(s_StratusFeatherId, f.m_StratusFeather);
            cmd.SetGlobalVector(s_CumulusRangeId, f.m_CumulusRange);
            cmd.SetGlobalFloat(s_CumulusFeatherId, f.m_CumulusFeather);
            if (f.m_ShapeNoiseTex != null)
                cmd.SetGlobalTexture(s_ShapeNoiseTexId, f.m_ShapeNoiseTex);
            if (f.m_DetailNoiseTex != null)
                cmd.SetGlobalTexture(s_DetailNoiseTexId, f.m_DetailNoiseTex);
            cmd.SetGlobalFloat(s_ShapeNoiseScaleId, f.m_ShapeNoiseScale);
            cmd.SetGlobalFloat(s_DetailNoiseScaleId, f.m_DetailNoiseScale);
            if (f.m_WeatherMapTex != null)
                cmd.SetGlobalTexture(s_WeatherMapTexId, f.m_WeatherMapTex);
            cmd.SetGlobalFloat(s_WeatherMapScaleId, f.m_WeatherMapScale);
            cmd.SetGlobalFloat(s_CloudDensityAdjustId, f.m_CloudDensityAdjust);
            cmd.SetGlobalFloat(s_CoverageId, f.m_Coverage);
            cmd.SetGlobalFloat(s_AbsorptionStrengthId, f.m_AbsorptionStrength);
            cmd.SetGlobalVector(s_WindDirectionId, f.m_WindDirection);
            cmd.SetGlobalFloat(s_WindSpeedId, f.m_WindSpeed);
            cmd.SetGlobalFloat(s_BaseShapeDetailEffectId, f.m_BaseShapeDetailEffect);
            cmd.SetGlobalFloat(s_DetailEffectId, f.m_DetailEffect);
            cmd.SetGlobalFloat(s_ShapeStepCountId, f.m_ShapeStepCount);
            cmd.SetGlobalFloat(s_ExtinctionCoeffId, f.m_ExtinctionCoeff);
            cmd.SetGlobalFloat(s_ScatterCoeffId, f.m_ScatterCoeff);
            cmd.SetGlobalFloat(s_DensityId, f.m_Density);
            cmd.SetGlobalFloat(s_MieGId, mieG);
            cmd.SetGlobalFloat(s_MieGBackwardId, f.m_MieGBackward);
            cmd.SetGlobalFloat(s_SilverIntensityId, f.m_SilverIntensity);
            cmd.SetGlobalFloat(s_SilverSpreadId, f.m_SilverSpread);
            cmd.SetGlobalFloat(s_MSAttenuationId, f.m_MSAttenuation);
            cmd.SetGlobalFloat(s_MSContributionId, f.m_MSContribution);
            cmd.SetGlobalFloat(s_MSEccentricityId, f.m_MSEccentricity);
            cmd.SetGlobalFloat(s_LightStepLengthId, f.m_LightStepLength);
            cmd.SetGlobalFloat(s_LightConeRadiusId, f.m_LightConeRadius);
            // 帧计数：供 InterleavedGradientNoise 逐帧变化（配合 TAA 消除层次感）
            cmd.SetGlobalFloat(s_FrameCountId, Time.frameCount);
            // 分帧渲染（棋盘格时间切片 + 重投影，仅 compute 模式用；fragment 模式忽略）
            cmd.SetGlobalFloat(s_TimeSlicingId, f.m_TimeSlicing ? 1f : 0f);
            // 分帧调试：只显示本帧 march 的目标像素（非目标像素透明）
            cmd.SetGlobalFloat(s_DebugSliceId, f.m_DebugSlice ? 1f : 0f);
            // 重投影遮挡边缘阈值（消云/地形缝隙）
            cmd.SetGlobalFloat(s_ReprojEdgeThresholdId, f.m_ReprojEdgeThreshold);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_CompositeMaterial == null) return;
            if (renderMode == CloudRenderMode.ComputeShader && computeShader == null) return;
            if (renderMode == CloudRenderMode.FragmentShader && m_FragmentMaterial == null) return;

            var cmd = CommandBufferPool.Get("Volumetric Clouds");
            var cam = renderingData.cameraData.camera;

            // ── 参数（全局 uniform，compute 与 fragment 版共用）─────────────
            ApplyCloudParams(cmd);

            // 非 jittered 逆 VP：TAA 开启时 UNITY_MATRIX_I_VP 每帧含 ±0.5px jitter，
            // 分帧模式下 march 视线/重投影/遮挡判断用它会导致逐帧偏移（流动伪影）
            var noJitterVP = renderingData.cameraData.GetGPUProjectionMatrixNoJitter() * renderingData.cameraData.GetViewMatrix();
            cmd.SetGlobalMatrix(s_NoJitterInvViewProjId, noJitterVP.inverse);

            // no-jitter 投影逆：viewDir 在相机空间重建近平面点用（UNITY_MATRIX_I_P 在 TAA 下含 jitter；
            // 投影矩阵无大平移，求逆精度无碍）
            cmd.SetGlobalMatrix(s_NoJitterInvProjId, renderingData.cameraData.GetGPUProjectionMatrixNoJitter().inverse);

            // 乒乓角色：固定 RT 数组 + index 轮换（引用永不交换，name 恒匹配 → ReAllocateIfNeeded
            // 不误重分配）；非分帧模式 cur 恒为 [0]（CloudTarget）
            var isSliced = renderMode == CloudRenderMode.ComputeShader && m_Feature.m_TimeSlicing;
            var cur = isSliced ? m_CloudTargets[m_CloudIndex] : m_CloudTargets[0];
            var prev = isSliced ? m_CloudTargets[1 - m_CloudIndex] : m_CloudTargets[1];

            if (renderMode == CloudRenderMode.ComputeShader)
            {
                var width = cur.rt.width;
                var height = cur.rt.height;

                if (m_Feature.m_TimeSlicing)
                {
                    // 非分帧→分帧切换：prev RT 是过期/垃圾数据 → 重建（走首帧全帧 march）
                    if (!m_LastFrameTimeSlicing)
                        m_FirstFrame = true;
                    m_LastFrameTimeSlicing = true;

                    // 转动分级：角速度 > Fast 阈值 → 全帧（最高质量，无滞后）；> Slow 阈值 → N=2
                    // （1/4 像素/帧，4 帧收敛）；否则配置 N（1/16）。转动时画面快速变化，
                    // 分帧旧值滞后最多 N² 帧 → 鬼影，分级按角速度降低滞后
                    var fwd = cam.transform.forward;
                    float rotDelta = Vector3.Angle(m_PrevCameraForward, fwd);
                    m_PrevCameraForward = fwd;
                    bool fullFrameRotation = m_Feature.m_RotationAdaptive && rotDelta > m_Feature.m_FastRotationThreshold;
                    bool mediumRotation = m_Feature.m_RotationAdaptive && rotDelta > m_Feature.m_RotationThreshold;

                    // ── 分帧渲染（时间切片 + 重投影）────────────────────────
                    if (m_FirstFrame || fullFrameRotation)
                    {
                        // 首帧：prev 未初始化（新分配 RT 是垃圾）——跳过重投影，全帧 march。
                        // 快速转动：质量优先，全帧 march（无滞后无鬼影）。
                        // 注意：全局 _TimeSlicing=1 会让 CSMain 走分帧分支（dispatch 全屏只盖 1/4 屏幕），
                        // 必须把该 kernel 的 _TimeSlicing 局部覆盖为 0（compute 局部 uniform 优先于全局）
                        cmd.SetComputeIntParam(computeShader, s_TimeSlicingId, 0);
                        cmd.SetComputeTextureParam(computeShader, m_Kernel, s_CloudTargetId, cur);
                        cmd.DispatchCompute(computeShader, m_Kernel,
                            Mathf.CeilToInt(width / (float)k_ThreadGroupSize),
                            Mathf.CeilToInt(height / (float)k_ThreadGroupSize), 1);
                        m_FirstFrame = false;
                    }
                    else
                    {
                        // 1. 重投影（全屏，轻）：上一帧结果按相机运动搬到当前帧位置
                        //    （当前深度 → 世界位置 → 上一帧 VP 投影 → 采样 prev）
                        if (m_ReprojectKernel < 0)
                            m_ReprojectKernel = computeShader.FindKernel("CSReproject");
                        cmd.SetComputeMatrixParam(computeShader, s_PrevViewProjId, m_PrevViewProj);
                        cmd.SetComputeTextureParam(computeShader, m_ReprojectKernel, s_CloudTargetPrevTexId, prev);
                        cmd.SetComputeTextureParam(computeShader, m_ReprojectKernel, s_CloudTargetId, cur);
                        cmd.DispatchCompute(computeShader, m_ReprojectKernel,
                            Mathf.CeilToInt(width / (float)k_ThreadGroupSize),
                            Mathf.CeilToInt(height / (float)k_ThreadGroupSize), 1);

                        // 2. march 分帧：全屏 dispatch，目标像素 march 覆盖，
                        //    非目标像素直接 return（保留重投影写入的搬移旧值）
                        //    必须每帧显式设置 _TimeSlicing=1——SetComputeIntParam 是持久状态，
                        //    首帧设的 0 会永久覆盖全局，否则 march 走全帧分支
                        // 中等转动：N=2（1/4 像素/帧，4 帧收敛——滞后 16→4 帧，鬼影折中）
                        var n = mediumRotation ? 2 : m_Feature.m_InterleaveSize;
                        cmd.SetComputeIntParam(computeShader, s_TimeSlicingId, 1);
                        cmd.SetComputeIntParam(computeShader, s_InterleaveSizeId, n);
                        cmd.SetComputeTextureParam(computeShader, m_Kernel, s_CloudTargetId, cur);
                        cmd.DispatchCompute(computeShader, m_Kernel,
                            Mathf.CeilToInt(width / (float)k_ThreadGroupSize),
                            Mathf.CeilToInt(height / (float)k_ThreadGroupSize), 1);
                    }

                    // 记录本帧 VP 供下一帧重投影。用 camera.projectionMatrix（无 jitter）——
                    // GetProjectionMatrix() 在 TAA 开启时返回 m_JitterMatrix × 投影，
                    // 重投影的 prevUV 会每帧 ±jitter 偏移 → 整片云抖动
                    m_PrevViewProj = renderingData.cameraData.camera.projectionMatrix * renderingData.cameraData.GetViewMatrix();
                }
                else
                {
                    m_LastFrameTimeSlicing = false;
                    // ── 全帧 Dispatch ──────────────────────────────────────
                    // 显式设 kernel 局部 _TimeSlicing=0：SetComputeIntParam 是持久状态，
                    // 否则之前分帧模式设的 1 会残留 → 全帧 dispatch 只盖 1/16 屏幕
                    cmd.SetComputeIntParam(computeShader, s_TimeSlicingId, 0);
                    cmd.SetComputeTextureParam(computeShader, m_Kernel, s_CloudTargetId, cur);
                    cmd.DispatchCompute(computeShader, m_Kernel,
                        Mathf.CeilToInt(width / (float)k_ThreadGroupSize),
                        Mathf.CeilToInt(height / (float)k_ThreadGroupSize), 1);
                }
            }
            else
            {
                // fragment 模式不参与分帧 → 视作"非分帧"，切回分帧时触发 prev 重建
                m_LastFrameTimeSlicing = false;

                // ── Fragment 版：Blitter.Blit 全屏渲染到 _CloudTarget ────────
                // 官方 Blit 处理 RenderTarget 绑定/平台翻转/uv 约定（shader 用 Blit.hlsl 的 Vert，
                // 必须走 Blitter 保证 texcoord 一致）；绑定相机深度纹理供 LoadSceneDepth 读取。
                // m_CopiedColor 仅作 Blit 源占位（shader 不采样 _BlitTexture，纯 ray march）
                var depthTex = renderingData.cameraData.renderer.cameraDepthTargetHandle;
                cmd.SetGlobalTexture(s_CameraDepthTexId, depthTex);
                CoreUtils.SetRenderTarget(cmd, m_CloudTargets[0]);
                Blitter.BlitTexture(cmd, m_CopiedColor, m_CloudTargets[0], m_FragmentMaterial, 0);
            }

            // ── 合成到相机颜色 ────────────────────────────────────────────
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            // 1. 拷贝场景颜色到临时 RT（避免读写冲突，官方 Blit 处理平台翻转）
            CoreUtils.SetRenderTarget(cmd, m_CopiedColor);
            Blitter.BlitTexture(cmd, cameraTarget, new Vector4(1, 1, 0, 0), 0.0f, false);

            cmd.SetGlobalTexture(s_CloudTargetId, cur);
            Blitter.BlitTexture(cmd, m_CopiedColor, cameraTarget, m_CompositeMaterial, 0);

            // ── 乒乓角色轮换（必须在合成之后——合成用当前帧结果）────────────
            if (renderMode == CloudRenderMode.ComputeShader && m_Feature.m_TimeSlicing)
                m_CloudIndex = 1 - m_CloudIndex;

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}