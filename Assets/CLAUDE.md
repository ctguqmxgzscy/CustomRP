# CLAUDE.md

在输出的时候，必须遵守以下6条规则

第一条，直接给结论，不要前置解释和铺垫。

第二条，不要复述我的问题，不要说"好的”、“明白了”、“让我来."这类引导词。

第三条，区分大小事项，简单问题一句话回答，复杂问题才展开。

第四条，客观陈述事实和方案，不要"很棒的问题"、“非常聪明"这类捧场。！

第五条，不要在回答末尾加多余总结。

第六条，不确定就直接停下来问或者使用Context7查询文档，不要瞎猜

**Feature 与 Mesh Lit 是两条独立管线**：修改 Feature 侧（AtmosphereSettings、SkyViewLut、AtmosphereSkyboxLutFeature、AtmosphereSkyboxLut.shader）时，**不要动** Mesh Lit 侧的文件（AtmosphereLitInput.hlsl、AtomsphereLit.shader、AtmosphereSkybox.shader、AtmosphereLUTBinder、AtmosphereSkyboxBinder、OpticalDepthLUTGenerator 及其材质）。`OpticalDepthLUT.compute` 是共享层，Feature 通过 `AtmosphereSkyboxLutFeature` 传外半径 `_AtmosphereRadius` 给 compute shader（内部从 settings.atmosphereHeight + planetRadius 计算）。

## Context7 查文档规则

遇到以下情况时，**必须先调用 Context7 MCP 工具查文档**，再继续回答或写代码：

- 不确定某个 API / 函数 / 参数的用途或正确用法
- 需要验证某个公式、常量值、算法细节是否正确
- 引用了第三方库的接口但不确定签名或行为
- 用户问"查一下资料"、"确认一下"、"这个对吗"等
- 凭记忆给出的数值或公式存在多个版本，需要确认权威来源

工作流：`resolve-library-id` → `query-docs`；Context7 未收录则回退 `WebSearch`。都找不到才用记忆，且必须声明"未验证"。

## 项目概述

Unity 2022.3.62f3c1 自定义渲染管线（Custom RP）项目。URP/Core RP 以本地包形式引入（`Packages/custom packages/`），可直接修改管线源码。

**大气散射参考**：[AKGWSB/RealTimeAtmosphere](https://github.com/AKGWSB/RealTimeAtmosphere)

## 关键目录

```
Assets/
├── Features/Atmosphere/Runtime/
│   ├── AtmosphereSettings.cs              # ScriptableObject 参数
│   ├── Atmosphere Settings.asset          # 参数实例
│   ├── AtmosphereSkyboxLutFeature.cs      # RenderFeature: LUT 生成 + skybox
│   ├── AtmosphereSkyboxBinder.cs          # 同步参数到 skybox 材质
│   ├── AtmosphereLUTBinder.cs             # 独立生成 Optical Depth LUT
│   ├── OpticalDepthLUTGenerator.cs        # Optical Depth LUT 生成器
│   └── Shaders/SkyViewLut.compute         # Sky View LUT compute shader
├── Shaders/
│   ├── ShaderLibrary/
│   │   ├── ScatteringUtils.hlsl           # 散射函数库 (phase, transmittance, ray intersect)
│   │   ├── OpticalDepthLUT.compute        # Optical Depth LUT compute shader
│   │   └── AtmosphereLitInput.hlsl        # Custom/AtmosphereLit 公共输入
│   └── Enviroment/
│       ├── AtmosphereSkyboxLut.shader     # Skybox/AtmosphericScatteringLUT (采样 LUT)
│       ├── AtmosphereSkybox.shader        # Skybox/AtmosphericScattering (全量 ray-march)
│       └── AtomsphereLit.shader           # Custom/AtmosphereLit (PBR + 大气散射)
├── Features/ScreenSpaceReflection/        # SSR (HDRP 迁移)
└── Resources/Material/Environment/        # 大气散射材质
```

Packages/custom packages/
├── com.unity.render-pipelines.core@14.0.12/
└── com.unity.render-pipelines.universal@14.0.12/

## 大气散射架构

### 两条管线

| 管线 | 入口 | 用途 |
|------|------|------|
| **Skybox LUT** | `AtmosphereSkyboxLutFeature` → `AtmosphereSkyboxLut.shader` | 天空盒（预计算 LUT + 采样） |
| **Mesh Lit** | `AtmosphereLUTBinder` → `Custom/AtmosphereLit` (NormalExtrusion pass) | Sphere 表面大气散射 |

两条管线**独立**，参数来源不同：
- Skybox LUT 从 `AtmosphereSettings` (ScriptableObject) 读取
- Mesh Lit 从 `Atmosphere Scattering.mat` 材质属性读取

### LUT 数据流

```
AtmosphereSkyboxLutFeature.Execute():
  1. 读参数 (AtmosphereSettings)
  2. OpticalDepthLUT.compute → _OpticalDepthLUT (64×64, RGHalf)
  3. SkyViewLut.compute       → _SkyViewLut (256×128, ARGBFloat)
  4. SetGlobalTexture + SetGlobalFloat → skybox shader 采样
  5. RenderSettings.skybox = m_SkyboxMaterial
```

### 散射系数（物理值，m⁻¹）

```hlsl
kRayleighScattering = float3(5.8e-6, 1.35e-5, 3.31e-5)  // R, G, B
kMieScattering      = float3(3.99e-6, 3.99e-6, 3.99e-6)
```

## 开发命令

- **项目路径**: `D:\Unity Responsity\Custom RP\Custom RP`，Unity 2022.3.62f3c1
- **验证**: `bash .claude/skills/run-custom-rp/validate.sh`
