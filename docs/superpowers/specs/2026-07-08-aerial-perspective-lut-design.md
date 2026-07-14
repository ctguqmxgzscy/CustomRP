# Aerial Perspective LUT — 设计文档

2026-07-08

## 概述

在现有大气散射系统（SkyView LUT + OpticalDepth LUT）基础上，增加基于 frustum voxelization 的 Aerial Perspective 3D LUT，为场景中不透明和透明物体提供距离相关的大气散射效果。

## 设计决策

| 决策 | 结论 |
|------|------|
| 目标场景 | 地面场景，相机始终在大气层内（无大气层外边缘情况） |
| 集成方式 | 集成进 `AtmosphereSkyboxLutFeature`，作为第四个 dispatch |
| 3D LUT 分辨率 | 128×72×64 |
| 3D LUT 格式 | `ARGBHalf`（RGBA16F），~4.5MB。rgb=累积in-scattering, a=scalar透射率（已知局限：日落场景 T.r≠T.b，scalar T 会导致轻微色偏，可在后续版本升级为双 LUT 方案） |
| Z 切片分布 | 平方指数：`z(t) = near × (far/near)^(t²)`, t∈[0,1] |
| Voxel 散射计算 | Option C：太阳方向查 TransmittanceLUT + 相机方向增量累积 T_cam + MultiScatteringLUT + 可选 CSM 阴影 |
| Post-process 合成 | `finalColor = srcColor × T + inScatter`，HDR 线性空间 |
| 管线时机 | LUT 生成 @BeforeRenderingOpaques(250)，合成 @BeforeRenderingTransparents(450) |

## 架构

```
AtmosphereSkyboxLutFeature
├── AtmosphereLutPass (所有 LUT 生成 @250)
│   ├── [1] OpticalDepthLUT (256×256, RGHalf)
│   ├── [2] MultiScatteringLUT (32×32, ARGBFloat)     ← 依赖 [1]
│   ├── [3] AerialPerspectiveLUT (128×72×64, ARGBHalf) ← 依赖 [1][2]，新增
│   └── [4] SkyViewLut (512×256, ARGBFloat)            ← 依赖 [1][2]
│
└── AerialPerspectiveCompositePass (合成 @450)            ← 新增
    └── Full-screen Blit: srcColor × T + inScatter
```

### 依赖顺序

Aerial LUT 依赖 OpticalDepthLUT 和 MultiScatteringLUT，必须在这两张 LUT 生成之后 dispatch。因此**所有 LUT 生成统一放到 @250**，通过 CommandBuffer 顺序保证依赖：

```
cmd:
  1. Dispatch OpticalDepthLUT
  2. Dispatch MultiScatteringLUT  ← 依赖 1 的输出
  3. Dispatch AerialPerspectiveLUT ← 依赖 1 + 2 的输出（新增）
  4. Dispatch SkyViewLut          ← 依赖 1 + 2 的输出（已有）
```

### Pass 拆分理由

- **AtmosphereLutPass (@250)**：所有 LUT 生成。不依赖任何场景 buffer，只依赖大气参数和太阳方向。越早越好。
- **AerialPerspectiveCompositePass (@450)**：合成 post-process。依赖场景颜色 + 深度 buffer，必须在 Opaque 渲染完之后。

## 3D LUT 结构

### 坐标系统

```
X (128): 屏幕水平方向，均匀映射到 viewport UV
Y (72):  屏幕垂直方向，均匀映射到 viewport UV
Z (64):  平方指数深度切片
```

### 平方指数深度映射

正向（slice index → world depth）：
```
z(t) = zNear × (zFar / zNear)^(t²)
t = sliceIndex / (NUM_SLICES - 1)
```

逆向（world depth → slice UV）：
```
t(z) = sqrt(log(z / zNear) / log(zFar / zNear))
sliceUV = t(z)
```

远近平面参数：`zNear = camera.nearClipPlane`, `zFar = atmosphereRadius`（大气外球半径，确保覆盖相机到大气边界的最远距离）。

### 每 Voxel 存储内容

```
_AerialPerspectiveLUT[x, y, z]:
  .rgb = 从相机到该切片中点的累积 in-scattering radiance
  .a   = 从相机到该切片中点的透射率（scalar luminance）
```

T_cam 写入使用切片中点（非入口）的透射率，与 in-scatter 累积使用的中点采样位置一致：

```hlsl
// 中点透射率（与 scatter 累积的采样位置一致）
float3 T_half = T_cam * exp(-extinction * ds * 0.5);
_AerialPerspectiveLUT[...] = float4(scatterAccum, Luminance(T_half));
// 然后更新到切片出口
T_cam *= exp(-extinction * ds);
```

### 线程分发结构（关键约束）

增量 T_cam 累积要求每条 XY 射线串行处理所有 Z 切片：

```hlsl
[numthreads(8, 8, 1)]
void ComputeAerialPerspectiveLUT(uint3 id : SV_DispatchThreadID)
{
    float3 T_cam = float3(1, 1, 1);
    float3 scatterAccum = float3(0, 0, 0);

    for (int z = 0; z < 64; z++)
    {
        // 计算当前切片的世界坐标
        // 计算 J, T_sun, MS
        // 累积 scatterAccum += contrib × T_cam
        // T_cam *= exp(-extinction × ds)
        // 写入 _AerialPerspectiveLUT[id.xy, z]
    }
}
```

```
Dispatch: ceil(128/8) × ceil(72/8) × 1 = 16 × 9 × 1
每线程处理 64 个切片（串行循环）
```

**不能**展开为 128×72×64 的三维 dispatch —— T_cam 状态无法跨线程传递。

## Voxel 散射计算（Option C）

每条 XY 射线（循环外，沿列不变）：

```hlsl
float3 viewDir  = normalize(ViewRayAt(id.xy, 1.0));
float  cosToSun = dot(viewDir, _SunDirection);  // view-sun 夹角，phase function 用，沿列常数
```

每个切片（循环内）：

```hlsl
float3 worldPos   = ViewRayAt(id.xy, depth);
float  height     = length(worldPos) - _PlanetRadius;
float  cosSunZenith = dot(normalize(worldPos), _SunDirection);  // 天顶角，TransmittanceLUT 用

// 太阳透射率（LUT 查表 + 可选场景阴影）
// CSM 在 compute shader 需手动 PCF（SamplerComparisonState 不可用），
// 首版可走 fragment shader 合成 pass 采样，compute 侧暂不乘阴影
float3 T_sun = SampleTransmittanceLUT(height, cosSunZenith, ...)
             * SampleShadowCSM_ManualPCF(worldPos);  // 可选，god rays

// 散射源函数（cosToSun 来自循环外，沿列不变）
float3 J = EvaluateInScattering(height, _ScaleHeight, _MieScaleHeight,
                                cosToSun, _MieG);

// 多重散射（查表，用天顶角 cosSunZenith）
float3 ms = GetMultiScattering(height, cosSunZenith, ...);

// 累积
float3 extinction = kRayleighScattering * exp(-height / _ScaleHeight)
                  + kMieScattering      * exp(-height / _MieScaleHeight);
float3 T_step     = exp(-extinction * ds);
float3 T_half     = T_cam * exp(-extinction * ds * 0.5);

// 累积（使用切片中点透射率）
// 单散射 + 多散射（ms 已内嵌多 bounce T_sun，只乘 _SunIntensity 不乘 T_sun）
float3 contrib = _SunIntensity * (T_sun * J + ms) * T_half * ds;
scatterAccum += contrib;

// 写入 LUT（切片出口累积散射 + 切片中点透射率，深度差 < 半切片厚度，双边插值兜底）
_AerialPerspectiveLUT[uint3(id.xy, z)] = float4(scatterAccum, Luminance(T_half));

// 更新到切片出口
T_cam *= T_step;
```

### 优化：cosTheta 沿列是常数

对于给定 XY 列，view direction 和 sun direction 都是固定的 → `cosTheta(viewDir, sunDir)` 沿整列不变 → Phase function 可以提到循环外。

## 合成 Post-process（@450）

```hlsl
float4 CompositeAerial(float2 screenUV)
{
    float rawDepth = SampleDepthBuffer(screenUV);

    // 跳过天空像素
    if (rawDepth >= 0.9999)
        return float4(SampleSceneColor(screenUV).rgb, 1.0);

    // 线性深度 → 切片 UV（平方指数逆映射）
    float linearDepth = LinearEyeDepth(rawDepth);
    float sliceUV     = DepthToSliceUV_SquaredExp(linearDepth);

    // 双边采样（防 depth bleeding）
    float4 aerial = BilateralSampleAerialLUT(screenUV, sliceUV, linearDepth);

    float3 inScatter = aerial.rgb;
    float3 T         = aerial.aaa;  // scalar T per channel

    // HDR 线性空间合成（Tonemapping 之前）
    float3 srcColor = SampleSceneColor(screenUV).rgb;
    float3 result   = srcColor * T + inScatter;

    return float4(result, 1.0);
}
```

### 双边采样

使用每个切片到像素深度的独立距离权重，而非共享边缘权重（后者在深度边缘处会同时压低两个样本，导致过度模糊）：

```hlsl
float slice = sliceUV * (NUM_SLICES - 1);
int   s0    = floor(slice);
int   s1    = min(s0 + 1, NUM_SLICES - 1);
float d0    = SliceToDepth(s0);
float d1    = SliceToDepth(s1);

// 每个切片独立加权：离像素深度越近的切片权重越高
float w0 = exp(-abs(linearDepth - d0) * BILATERAL_SIGMA);
float w1 = exp(-abs(linearDepth - d1) * BILATERAL_SIGMA);
float total = w0 + w1 + 1e-6;

// XY 用硬件双线性，Z 手动双边（避免 128×72→1920×1080 的阶梯感）
float4 a0 = _AerialPerspectiveLUT.SampleLevel(
    sampler_linear_clamp, float3(screenUV, (s0 + 0.5) / NUM_SLICES), 0);
float4 a1 = _AerialPerspectiveLUT.SampleLevel(
    sampler_linear_clamp, float3(screenUV, (s1 + 0.5) / NUM_SLICES), 0);
return (a0 * w0 + a1 * w1) / total;
```

BILATERAL_SIGMA 控制深度边缘敏感度：值越大对深度间隙越敏感（越不容易跨边 bleeding），值越小越接近标准线性插值。

### 合成时机约束

- 必须在 Tonemapping 之前（HDR 空间）
- 必须在 Transparent Pass 之前（透明物体用自身深度采样 LUT）
- 跳过 far plane 像素避免 SkyView LUT 二次叠加

## 透明物体

Transparent shader 用**自身深度**（非 depth buffer）采样 LUT：

```hlsl
// Transparent fragment shader
float linearDepth = ComputeLinearDepth(positionCS);
float sliceUV     = DepthToSliceUV_SquaredExp(linearDepth);
float4 aerial     = _AerialPerspectiveLUT.Sample(sampler, float3(screenUV, sliceUV));

// 近似：用表面 alpha 调制大气 in-scatter。
// 物理上 in-scatter 取决于 P→相机路径上的累积散射，此处假设表面
// 厚度可忽略、后方散射均匀，用 color.a 近似。
color.rgb = color.rgb * aerial.a + aerial.rgb * color.a;
```

## 完整管线时序

```
250  BeforeRenderingOpaques
     └── AtmosphereLutPass: [1]OpticalDepth → [2]MultiScatter → [3]AerialLUT + [4]SkyViewLut

300  Opaque Pass
     └── 场景几何体 → 颜色 + 深度 buffer

350  BeforeRenderingSkybox
     └── Skybox 渲染（已有，采样 SkyViewLut）

450  BeforeRenderingTransparents
     ├── AerialPerspectiveCompositePass: 合成 Aerial Perspective
     └── Transparent Pass: 透明物体采样 AerialLUT

550  BeforeRenderingPostProcessing
     └── Bloom / DOF / ...

600  Tonemapping
     └── HDR → LDR
```

## TAA / 抖动兼容性

如果管线开启了 TAA，每帧相机的 jitter offset 会使 screen-space UV 偏移。合成 pass 采样 Aerial LUT 时必须使用 **unjittered UV**：

```hlsl
// 正确的 UV: 去掉 TAA jitter
float2 unjitteredUV = (positionCS.xy - _TaaJitter.xy) * _ScreenSize.zw;

// 错误的 UV（会引入时间抖动）:
float2 jitteredUV = positionCS.xy * _ScreenSize.zw;
```

原因：LUT 的 XY 网格是均匀映射到 viewport 的，不跟随 jitter 移动。用 jittered UV 采样会导致每帧采样到略微偏移的 voxel，产生时间上的微闪。

## 性能分析

### 逐帧重建开销

128×72×64 = 589,824 voxels，每 voxel 操作：2 次 LUT 采样 + EvaluateInScattering + 若干 exp/mul。Dispatch 16×9 线程组，每线程 64 次循环迭代。

对比参考：
- SkyViewLUT (512×256×32 samples) ≈ 4M 采样点，已有 dispatch
- Aerial LUT ≈ 0.6M voxels，约为 SkyView LUT 的 15%

在桌面 GPU（GTX 1060 级别）上预计 **< 0.5ms**（不含 CSM shadow 采样；开启 CSM 后每 voxel 额外 1~4 次 shadow map 查表，视级联数量增加 ~0.1-0.3ms）。

### 静态大气优化机会

大气参数帧间不变时（太阳方向固定、相机高度微变），LUT 内容几乎不变。后续可考虑：
- **帧间隔更新**：每 N 帧重建一次（如 N=2~4），降低平均开销 50~75%
- **跳过条件**：检测 `_SunDirection` 和相机位置变化量，低于阈值跳过重建
- 首版实现不做 temporal 复用，逐帧全量重建作为正确性基线

### 低端机降级方案

- 分辨率降到 64×36×32（开销 ~1/8）
- 或直接用解析高度雾 fallback（已有 EvaluateTransmittance 逐像素计算，跳过 LUT）

## 关键实现细节

| # | 细节 | 说明 |
|---|------|------|
| 1 | 深度映射一致性 | 正逆函数必须和 LUT 生成完全一致（平方指数），且 zFar = atmosphereRadius（非 camera.farClipPlane） |
| 2 | 双边采样 | 不能朴素三线性插值，需要深度边缘感知 |
| 3 | Scalar T 局限 | 日落 0° elevation 场景 T.r/T.b 可达 2~3×，scalar luminance 合成导致远景色调偏蓝（~5-10% 感知色差）。已知局限，后续可升级双 LUT |
| 4 | HDR 合成 | Tonemapping 之前，保持高动态范围 |
| 5 | 天空跳过 | far plane 深度检测，避免 SkyView LUT 二次叠加 |
| 6 | 线程结构 | 每线程一列 XY 串行 Z，不能全展开 |
| 7 | LUT 依赖顺序 | OpticalDepth → MultiScatter → AerialLUT + SkyViewLut，前序未完成不能 dispatch 后继 |
| 8 | TAA jitter | 合成 pass 采样 LUT 必须用 unjittered UV，否则每帧产生时间微闪 |
| 9 | CSM 在 Compute Shader | URP 的 `SAMPLE_TEXTURE2D_SHADOW` 依赖 `SamplerComparisonState`，compute shader 不支持。需要手动 PCF（Load raw depth + compare），或 CSM 路径只在 fragment shader 合成 pass 走 |

## 文件清单

| 文件 | 类型 | 用途 |
|------|------|------|
| `AerialPerspectiveLut.compute` | Compute Shader | 3D LUT 生成 |
| `AerialPerspectiveComposite.shader` | Shader | 全屏合成 Blit |
| `AtmosphereSkyboxLutFeature.cs` | C# | 集成 AerialLutPass + CompositePass |
| `ScatteringUtils.hlsl` | HLSL | 可能需要新增深度映射函数 |

## 依赖

- `OpticalDepthLUT`（已有，256×256 RGHalf）—— 太阳透射率查表
- `MultiScatteringLUT`（已有，32×32 ARGBFloat）—— 多重散射查表
- `AtmosphereSettings`（已有 ScriptableObject）—— 大气参数来源
- `Scattering.hlsl` / `ScatteringUtils.hlsl` —— 散射函数库
