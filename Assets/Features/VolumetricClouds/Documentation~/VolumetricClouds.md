# Volumetric Clouds — 技术文档

参考 [Hidden/Clouds 体积云 shader](https://github.com/) + Nubis（SIGGRAPH 2015/2017）+ GPU Pro 7 + Nubis³（体素）思路移植，运行于 Custom RP（URP 14.0.12 本地包）。

## 1. 架构总览

```
VolumetricCloudFeature（ScriptableRendererFeature, BeforeRenderingTransparents）
  ├─ CloudRayMarchTest.compute    ★ 主路径：全屏 ray march → _CloudTarget (ARGBHalf)
  │     ├─ 密度采样：CloudCommon.hlsl（天气图 + 三段云型梯度 + 噪声侵蚀 + 砧状）
  │     ├─ 光照：    CloudLighting.hlsl（双模式：BeerLambert 光锥 / Hillaire MS，_CloudLightingMode 切换）
  │     └─ 步进：    距离自适应步长（minStep → maxStep=500m）+ Nubis 状态机
  ├─ CloudRayMarchFragment.shader 非 compute 版（RenderMode=FragmentShader 时 Blitter 全屏三角形渲染，逻辑一致）
  ├─ CloudRayMarch.compute        旧自适应 march（保留未动）
  └─ CloudComposite.shader        合成：lerp(场景, 云色, 云不透明度)
```

渲染顺序：**大气散射（Skybox LUT 管线）→ 体积云 → Transparents**。云 Feature 在 Renderer 资产中位于 AtmosphereSkyboxLutFeature 之后，云合成在"场景 + 大气"之上。

参数传递：`ApplyCloudParams(cmd)` 统一 `SetGlobal*`（compute 与 fragment 版共用全局 uniform），大气侧（`_SkyViewLut`/`_OpticalDepthLUT`）由 AtmosphereSkyboxLutFeature 每帧 SetGlobal。

两条渲染路径（`m_RenderMode`，编辑器可切）：
- `ComputeShader`：默认，Dispatch CloudRayMarchTest.compute
- `FragmentShader`：Blitter.BlitTexture 全屏三角形渲染 CloudRayMarchFragment.shader（与 compute 逐行对应，调试/对照用）

## 2. 密度场建模（2.5D）

云密度 = **天气图（宏观分布）× 云型高度梯度（垂直分布）× 噪声（形状雕刻）**。

### 2.1 天气图（2D，世界 XZ 平铺）

| 通道 | 语义 |
|---|---|
| R | 云覆盖率（0 晴 ~ 1 阴） |
| G | 吸收率（降雨，放大消光） |
| B | 云型（0=层云 ~ 1=积云） |

采样（`SampleWeatherData`）：`(pos.xz + 风偏移) × _WeatherMapScale × 1e-6`（1000km 平铺）。

全局云量控制（参考 `_Coverage`）：
```hlsl
weatherData.r = saturate(weatherData.r - _Coverage);  // 0=原图，越大云越少越薄
```
随后 `cloudDensityAdjust` 经 `Interpolation3` 重映射 0~0.5~1 → 0~通道值~1。

### 2.2 云型高度梯度（三段云型）

参考 Hidden/Clouds 的 `getDensityHeightGradient`——**三段梯度插值**（替代旧的层云/积云双带）：

```hlsl
CloudGradient1 = float4(0.0, 0.065, 0.203, 0.371);  // stratus 层云
CloudGradient2 = float4(0.0, 0.156, 0.468, 0.674);  // cumulus 积云
CloudGradient3 = float4(0.0, 0.188, 0.818, 1);      // cumulonimbus 积雨云
gradient = lerp(lerp(G1, G2, type×2), G3, saturate((type-0.5)×2));  // type = weatherData.g
heightGradient = smoothstep(g.x, g.y, h) - smoothstep(g.z, g.w, h);  // 双 smoothstep 剖面
```

`GetHeightFractionForPoint`：`(length(pos - planetCenter) - planetRadius - 云底) / 厚度`（球面径向）。

### 2.3 噪声侵蚀 + 砧状塑形（`SampleCloudDensity`）

```
风：p += heightFraction × windDir × 500（高度剪切）+ (windDir + (0,0.1,0)) × _Time.y × windSpeed
baseCloud = 低频 Perlin-Worley（R 通道 + Worley FBM 侵蚀，带 mipLevel——当前恒 0，见 §6）
baseCloud ×= 云型梯度（2.2）
coverage 指数：cloudCoverage = pow(coverage, Remap(h, 0.7, 0.8, 1.0, lerp(1.0, 0.5, u_AnvilBias)))
              ——砧状：云顶（h>0.7~0.8）覆盖率指数 1→0.5（u_AnvilBias=1），云顶铺开成砧状
cloudWithCoverage = saturate(Remap(baseCloud, cloudCoverage, 1, 0, 1)) × cloudCoverage
高频侵蚀（sampleDetail=true 时）：
  highFreqModifier = lerp(fBm, 1-fBm, saturate(h × 10))  // 底部纤细 ↔ 顶部波浪
  finalCloud = Remap(cloudWithCoverage, saturate(modifier × 0.5), 1, 0, 1)
输出：finalCloud × cloudDensityScale × 0.1
```

## 3. 视线重建与相交（精度关键）

### 3.1 GetViewDir 分步重建（高空精度修复）

```hlsl
// 逆投影在相机空间算方向（近平面小量 ~0.3m，float32 精度 2e-8）+ 逆视图纯旋转转世界
float4 viewPos = mul(UNITY_MATRIX_I_P, float4(ndc, nearDepth, 1.0));
float3 dirCS = viewPos.xyz / max(viewPos.w, 1e-6);
float3 viewDir = normalize(mul((float3x3)UNITY_MATRIX_I_V, dirCS));
```

**为何不能直接用 `UNITY_MATRIX_I_VP`**：原实现在世界坐标重建近平面点再减相机位置——两个 6.3e6 量级大数相减，float32 ulp ≈ 0.5m 而近平面偏移仅 ~0.3m，**相机万米以上方向被量化失真**（云形破裂/抖动）。分步重建全程无大数相减，任意高度稳定。

**分帧模式下用 no-jitter 变体**（`GetViewDirNoJitter`）：`UNITY_MATRIX_I_P` 在 TAA 开启时含 ±0.5px jitter（逐帧变化），分帧目标像素每次更新（N² 帧一次）的 march 值在不同 jitter 下采样 → 新旧值跳变（流动伪影）。分帧模式改用 C# 每帧传入的 `_NoJitterInvProj`（`GetGPUProjectionMatrixNoJitter().inverse`），旋转部分用 `UNITY_MATRIX_I_V`（view 矩阵无 jitter）。全帧模式保持 jittered（TAA 自身平滑）。

### 3.2 云层相交（RayCloudLayerDst）

统一球壳相交（相机在云下/云内/云上三态由相交自动处理），返回 (到云层距离, 云层内厚度)；配合地平线剔除（视线先碰地球表面 → 输出 0）与深度提前退出（`tExit = min(endPos, sceneDist)`，far 像素不裁剪）。

## 4. 步进策略（距离自适应，当前主路径）

```
minStep = min(ds, _LightStepLength)      // ds = 云层路径/64；垂直视线不劣化，掠射收敛到光步长
kStepGrowth = 0.1                         // 几何增长：t_{n+1} = t_n × (1+k)
maxStep = 500.0                           // 步长上限（验证值：500m 无断裂伪影）
stepSize = clamp((inCloud ? t - dstToCloud : t) × kStepGrowth, minStep, maxStep)
```

**三个关键设计**：

1. **云内以"云内深度"（t - dstToCloud）为基准重新增长**：高空相机空区让 t 巨大，若用 t 作基准步长直接 clamp 到 maxStep——垂直俯视 8km 云层只剩 8 个采样点（云形失真）。重置后云内从 minStep 重新增长。

2. **maxStep = 500m（相切带/掠射防断裂）**：水平视线与球壳相切时云层路径可达 100km+，步长若到 km 级，密度结构（几百米~km 尺度）欠采样 → 切线方向伪影/断裂。500m 每噪声周期 ≥18 采样点（实测无断裂）。预算：云下远处 200km 路径 ≈ 465 步 < 循环上限 512（×8）。

3. **空区大步钳制**：`t = min(t + 2×stepSize, dstToCloud - minStep)`——初始空区（高空云顶边缘带可达 400-1000km）直接逼近入点前一个细步，不一步步走完（否则耗尽预算 → 边缘锯齿阶梯）；已进入过云层（t > 入点，云断裂区）保持原大步，避免倒退死循环。

**云内采样位置抖动（Nubis³）**：`posWS = position + viewDir × (t + jitterHash × stepSize)`——全步长抖动防切片伪影，`t < 250m` 用逐帧 IGN（消除切片），`≥250m` 用静态 IGN（帧号 0，时间稳定防闪烁）。

**状态机（Nubis 2017）**：云外判断采样（cheap）+ 大步跳过；检测到云后退一步防越界；云内细步全采样；连续零密度 ≥ 11 判定穿出云层切回大步；`T_PA ≤ 0.01` 提前退出。

**alpha 增强（艺术补偿）**：`alpha = 1 - exp(-τ_PA × 3)`——消光参数量级低（_ExtinctionCoeff 0.0035 × density≤0.1 → 8km 厚云 τ≤2.8 → alpha≤0.94），合成 lerp 时透出大气天空。物理 σt 应高 10-100 倍，但直接调 extinction 会压暗光照（Beer 自阴影），故仅增强 alpha 输出，光照路径不动。

## 5. 光照模型

### 5.1 双模式（`_CloudLightingMode`，编辑器 Lighting Mode 切换）

| 模式 | 公式 | 特点 |
|---|---|---|
| **BeerLambertCone**（默认） | `2 × BeerLambert(τ_CP, _Density) × PowderEffect(ρ, cosθ) × 双HG相位 × _SunColor × _SunIntensity × T_CP × 0.3` + 大气 LUT 环境光 | 光锥自阴影 + 粉糖 + 银边 + 环境光 |
| **HillaireMS** | `J × T_CP + ms`；`J = EvaluateCloudInScattering(散射核 × phase[0] × exp(-τ_CP))`，`ms = Σ_{o≥1} kernel × exp(-τ_CP·att^o) × phase[o] × con^o` | 方式 B：单散射 J + 多次散射补益（octave 1-2），厚云内部白 |

编辑器按模式条件显示参数（BeerLambert 模式：Density/MieGBackward/Ambient/CloudBaseTopColor；HillaireMS 模式：ScatterCoeff/MSAttenuation/MSContribution/MSEccentricity）。

### 5.2 各项语义（BeerLambertCone 模式）

| 项 | 公式 | 语义 |
|---|---|---|
| BeerLambert | `max(exp(-τ·p), exp(-τ·p/2)×0.7)` | Dual Beer 光路透射（τ_CP = 锥形光路光学深度，自阴影） |
| PowderEffect | `lerp(1, 1-exp(-2ρ), saturate(0.5-0.5cosθ))` | 粉糖效应（边缘亮/背光） |
| 双 HG 相位 | `max(HG(fwd g₁), HG(-bwd g₂)) × 0.6909 + 0.8` | 前向银边 + 后向背光晕，0.8 散射底值 |
| T_CP | `SampleTransmittanceLUT`（`_OpticalDepthLUT`） | 太阳光穿过大气的衰减（与天空盒同源） |
| T_PA | `exp(-τ_PA)`，march 累积 | 视线方向透射 |
| Δτ | `1 - exp(-σt·ρ·ds)` | 本步新增不透明度（累积权重） |

### 5.3 圆锥采样（Cone Sampling）

```
光步长固定：lightStep = _LightStepLength × lightDir   // 不跟视步长——地平线视步长暴涨会炸 τ_CP
偏移：p = conePos + noise_kernel[j] × _LightStepLength × _LightConeRadius × (j+1)   // 半径递增 → 锥形
累积：τ_CP += ρ(p) × _LightStepLength × σt
密度 < 0.3 → 全采样；≥ 0.3 → cheap 采样
```

### 5.4 大气 LUT 环境光（BeerLambertCone 模式）

```hlsl
ambientTop    = SampleSkyViewLut(float3(0, 1, 0));       // 天顶天空色
ambientBottom = SampleSkyViewLut(float3(0, -1, 0));      // 地面方向天空色
upwardAO      = exp(-τ_CP × sin(太阳仰角));              // 上方云遮蔽（HP 思路）
ambient = (ambientTop × upwardAO + ambientBottom × (1 - heightFraction)) × _AmbientLightFactor
```

与 `AtmosphereSkyboxLut.shader` 共用同一 `_SkyViewLut`（全局纹理，大气 Feature 每帧生成）和同一 Hillaire 非线性纬度 UV 映射——黄昏/太阳位置自动与天空盒一致。

## 6. 渲染优化

| 优化 | 实现 |
|---|---|
| 距离自适应步长 | `minStep = min(ds, 光步长)` → 几何增长 → `maxStep = 500m`；云内以云内深度为基准重新增长 |
| 空区大步钳制 | 初始空区直接逼近入点前一个细步（高空边缘带不耗预算） |
| 云内抖动 | 全步长 jitter × stepSize，<250m 逐帧 / ≥250m 静态（防切片+防闪烁） |
| 零密度出云检测 | 连续两次零密度计数 +1，≥ 11 判定穿出云层 → 切回大步 |
| alpha 饱和 | `alpha ≤ 1.0` 循环条件 + `T_PA ≤ 0.01` 提前退出 |
| cheap 采样 | `doCheaply`——云外判断/圆锥采样跳过高频细节噪声 |
| 地平线剔除 | 视线先碰地球表面（`earthDist < dstToCloud`）→ 直接输出 0 |
| 深度提前退出 | 场景几何遮挡：`tExit = min(tExit, sceneDist)`（重建世界位置，修正 1/cosθ） |
| viewDir 精度 | GetViewDir 分步重建（I_P 相机空间 + I_V 旋转），高空不失真 |

## 6.5 分帧渲染（时间切片 + 重投影）

**性能原理**：每帧只 march 全分辨率的 1/N² 像素（N×N 块轮换），其余像素由上一帧结果经**重投影（相机运动搬移）**提供——静止场景 16 帧收敛到全分辨率质量，成本降至 1/N²。仅 ComputeShader 模式生效（fragment 模式忽略）。

### 6.5.1 每帧流程

```
分帧模式（m_TimeSlicing = true）：
  CSReproject（全屏，轻）——上一帧结果按相机运动搬到当前帧位置
    ├─ 3×3 最近深度选择（深度不连续边缘用前景深度——防转动小鬼影/边缘条纹）
    ├─ 深度不连续检测（3×3 跨度 > _ReprojEdgeThreshold）→ 保留原地（消云/地形缝隙）
    ├─ 反投影：ComputeWorldSpacePosition(uv, depth, _NoJitterInvViewProj)（no-jitter）
    ├─ prevVP 投影 → prevUV（CPU 约定矩阵，NDC y 向上——不能做 UNITY_UV_STARTS_AT_TOP
    │   翻转，否则 y 镜像 → 上下对称云）
    ├─ 双线性采样 prev（消 prevUV 整数化条带）
    └─ far 像素统一反投影（不做特判：旋转搬移、平移视差≈0 自然退化为恒等）
  CSMain（全屏 dispatch）——每像素自行判断本帧是否目标
    ├─ 目标像素（any(id.xy % n != offset) 为假）：正常 march（_DebugSlice 时可高亮）
    └─ 非目标像素：return（保留 CSReproject 写入的搬移值）
  合成（Blit：lerp(场景, 云, alpha)）
```

轮换顺序（`GetInterleaveOffset`，LUT 对角/十字散布）：`t = _FrameCount % N²` → 查表得块内偏移——**避免行主序（t%n, t/n）的水平扫描带**（更新像素成带扫过屏幕 → 新旧值差异呈定向"流动"伪影）；对角/十字让差异随机化（无方向）。LUT 为 `frameBlockN` 的反查（双射：覆盖率/收敛帧数不变）。

**轮换 LUT 必须在全局作用域声明**：函数内 `static const` 数组在 D3D11 编译失败（kernel invalid）。

### 6.5.2 乒乓双缓冲（RTHandle name 坑）

云 RT 用**固定引用数组 + index 轮换**（`m_CloudTargets[2]` + `m_CloudIndex`，帧末 `m_CloudIndex = 1 - m_CloudIndex`）：

```
⚠ 不能用引用交换（元组交换）：RenderingUtils.ReAllocateIfNeeded 把 RTHandle 的 name 纳入
  重分配判断——交换后 cur/prev 的 name 与角色不符 → 每帧误重分配 → RT 内容被清空
  → 分帧模式下云淡/只剩 1/N²（N 越大越淡，曾为此根因）。
  固定引用 + index 轮换保证 name 恒匹配，不误触发。
```

### 6.5.3 no-jitter 矩阵体系（TAA 兼容）

TAA 开启时 `GetProjectionMatrix()` 返回 `m_JitterMatrix × 投影`（每帧 ±0.5px）——分帧模式下所有依赖投影矩阵的计算逐帧偏移会产生伪影（目标像素每次更新的 march 值在不同 jitter 下采样 → 新旧值跳变流动；重投影搬移位置抖动）。分帧模式统一改用 C# 每帧传入的 no-jitter 矩阵：

| 矩阵 | 来源 | 用途 |
|---|---|---|
| `_NoJitterInvViewProj` | `(GetGPUProjectionMatrixNoJitter() × GetViewMatrix()).inverse` | CSReproject 反投影、CSMain 深度遮挡判断 |
| `_NoJitterInvProj` | `GetGPUProjectionMatrixNoJitter().inverse` | `GetViewDirNoJitter` 相机空间重建（投影矩阵无大平移，求逆精度无碍） |
| `_PrevViewProjMatrix` | `camera.projectionMatrix × GetViewMatrix()`（无 jitter） | 重投影搬移（CPU 约定，NDC y 向上） |

全帧模式保持 jittered（TAA 自身平滑）。

### 6.5.4 转动分级（RotationAdaptive）

相机转动时画面快速变化，分帧旧值滞后最多 N² 帧 → 鬼影。按角速度分级降低滞后：

| 角速度（度/帧） | 模式 | 每帧 march | 收敛 |
|---|---|---|---|
| > `_FastRotationThreshold`（1.5） | 全帧（直接覆盖，不混合） | 100% | 1 帧 |
| > `_RotationThreshold`（0.5） | N=2 | 25% | 4 帧 |
| ≤ 0.5 | 配置 N（默认 4） | 6.25% | 16 帧 |

`m_RotationAdaptive` 开关可整体关闭（验证其他方案时固定分帧）。首帧（`m_FirstFrame`）恒全帧 march（prev 重建）。

### 6.5.5 调试

`_DebugSlice`（Debug 分组）：只显示本帧 march 的目标像素（非目标清透明）——验证分帧覆盖分布（LUT 对角/十字散布）。

### 6.5.6 已知取舍与踩坑记录

- **重投影的滞后值 vs march 精确值同帧混合 → 流动伪影**（实测）：注释重投影（非目标返回 prevColor）后无流动但有拖影——取舍后保留重投影（拖影更不可接受），配合 no-jitter + 3×3 最近深度 + LUT 散布缓解
- **尝试过但放弃的方案**：目标像素与旧值时间混合（收敛慢/干扰验证）、新区域写透明（转动时空白面积过大）、位移加权渐隐（同样空白问题）、TAA 式滤波 pass（历史 clamp——分帧下 AABB 参考不完整）、双边滤波采样（cs_5_0 编译问题）
- **残余问题**：转动时仍有少量小鬼影（3×3 + 双线性已大幅缓解）、云/地形边缘缝隙（遮挡边缘保留原地缓解，`_ReprojEdgeThreshold` 可调）
- **分帧 + TAA 本质冲突**：TAA 的 velocity/clamp/history 全部依赖"每帧完整当前帧"——分帧给它的当前帧是滞后的 → TAA 对分帧云基本失效（云的 motion vector = 0，历史对齐错位）。完整 TAA 系滤波若要生效需"每帧全量 march"（空间降采样方案，Nubis 路线）
- **HLSL 陷阱**：函数内 `static const` 数组编译失败（kernel invalid）；`if` 需要标量（向量比较用 `any()`）；`exp()` 在 cs_5_0 可能映射失败（用 `exp2(x×1.4427)`）

## 7. 参数（VolumetricCloudFeature）

| 分组 | 参数 | 说明 |
|---|---|---|
| Assets | `m_CloudRayMarchCompute`、`m_CompositeShader` | compute / 合成 shader |
| Render Mode | `m_RenderMode`（ComputeShader/FragmentShader）、`m_FragmentShader` | 渲染路径（fragment 版调试用） |
| Performance | `m_TimeSlicing`、`m_InterleaveSize`（2~4） | 分帧开关 / 粒度（N² 帧收敛） |
| Rotation | `m_RotationAdaptive`、`m_FastRotationThreshold`（1.5）、`m_RotationThreshold`（0.5） | 转动分级（全帧/N=2/配置 N）；`ReprojEdgeThreshold`（0.1）遮挡边缘保留原地（消缝隙） |
| Debug | `m_DebugSlice` | 只显示本帧 march 目标像素（验证覆盖分布） |
| Shared | `m_AtmosphereSettings` | 大气预设（planetRadius/sunIntensity/mieG 等公共参数来源） |
| Sun | `m_SunColor` / `m_SunIntensity` | 太阳颜色/强度（预设覆盖时以预设为准） |
| Lighting Mode | `m_LightingMode`（BeerLambertCone/HillaireMS） | 光照模式，条件显示对应参数 |
| Planet | `m_PlanetRadius` / `m_CloudHeightMin/Max` | 行星半径 / 云层壳 |
| Cloud Type | `m_StratusRange/Feather`、`m_CumulusRange/Feather` | 层云/积云垂直范围（旧双带模型，新梯度模型已不使用） |
| Weather Map | `m_WeatherMapTex`、`m_WeatherMapScale`、`m_CloudDensityAdjust`、`m_AbsorptionStrength`、`m_Coverage` | 天气图 / 吸收 / 全局云量 |
| Density | `m_CloudDensityScale`、`m_Density` | 云量密度 / Beer 消光强度 |
| Noise | `m_ShapeNoiseTex/Scale`、`m_DetailNoiseTex/Scale`、`m_BaseShapeDetailEffect`、`m_DetailEffect` | 形状/细节噪声 |
| March | `m_ShapeStepCount`、`m_ExtinctionCoeff`、`m_ScatterCoeff`、`m_LightStepLength`、`m_LightConeRadius` | 视步数 / 消光 / 散射 / 光步长 / 光锥半径 |
| Phase | `m_MieG`、`m_MieGBackward`、`m_SilverIntensity`、`m_SilverSpread` | 双 HG 前向/后向 / 银边 |
| Multi-Scattering | `m_MSAttenuation/Contribution/Eccentricity` | Hillaire MS（HillaireMS 模式用） |
| Ambient | `m_CloudBaseColor`、`m_CloudTopColor`、`m_AmbientLightFactor` | 环境光（LUT 版只用 `m_AmbientLightFactor`） |
| Wind | `m_WindDirection`、`m_WindSpeed` | 风向 / 风速 |

## 8. 已知问题与待办

- **距离 LOD/mip 未做（断裂根治项）**：`SampleLowFrequencyNoise` 已带 `mipLevel` 参数但 march 内恒 0；且 3D 噪声纹理（ShapeNoise3D 128³ / DetailNoise3D 64³）**无 mipmaps**（mipCount=1，生成器 `CloudNoiseBaker.cs` 的 `tex3D.Apply()` 未开 updateMipmaps）。Nubis³ 思路：步长与密度频率匹配——远处采样高 mip 低频噪声，大步长也不断裂。当前靠 maxStep=500m 钳制缓解，远处仍可能轻度欠采样
- **cheap 阈值 0.3**：硬编码（参考实现值），未参数化
- **分帧渲染**：已实现（§6.5）——残余：转动小鬼影（3×3 最近深度缓解）、云/地形边缘缝隙（遮挡边缘保留原地缓解）。**根治方向 = 空间降采样**（低分辨率全量 march + TAA 重建，Nubis 路线——从根上避免时间切片的滞后/伪影，性能等价）
- **`_LightStepLength` 与视步长的量纲**：锥形 τ_CP = ρ × 光步长 × σt——自阴影强度由 `_LightStepLength × _Density` 共同决定，需联合调参
- **环境光遮蔽**：upwardAO 用 τ_CP 近似（单点光路），未做多方向遮蔽积分
- **云型双带 vs 三段梯度**：新旧密度模型并存，迁移完成后可清理
- **fragment 版未与 compute 完全同步**：CloudRayMarchFragment.shader 部分改动（如三态/仰角步长实验）可能滞后于 compute 主路径
