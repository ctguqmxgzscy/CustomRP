# 大气散射实时渲染 — 参考文档

基于 [AKGWSB/RealTimeAtmosphere](https://github.com/AKGWSB/RealTimeAtmosphere) 和 UnitySkyAtmosphere 两个实现的分析对比，覆盖从物理原理到 GPU 管线设计的完整知识。

---

## 1. 物理基础

### 1.1 散射系数

大气中有两类散射粒子：

| | 来源 | 尺度 | 波长依赖 | 海平面系数 (m⁻¹) | 标高 |
|---|------|------|----------|-------------------|------|
| Rayleigh | 空气分子 (N₂, O₂) | << λ | ∝ 1/λ⁴ | (5.8, 13.5, 33.1)×10⁻⁶ | 8 km |
| Mie | 气溶胶 (灰尘, 水滴) | ≈ λ | 弱 | (3.99, 3.99, 3.99)×10⁻⁶ | 1.2 km |

Rayleigh 蓝光散射是红光的 ~5.7 倍，所以天空是蓝色的、日落是红色的。

密度随海拔指数衰减：

```
ρ_R(h) = exp(-h / H_R)     H_R = 8000m
ρ_M(h) = exp(-h / H_M)     H_M = 1200m
```

### 1.2 相位函数

**Rayleigh**: `γ(θ) = 3/(16π) · (1 + cos²θ)`

前向和后向散射对称；90° 方向散射是前向的一半。

**Mie (Cornette-Shanks 1992)**:

```
p(θ) = 3/(8π) · (1-g²)/(2+g²) · (1+cos²θ) / (1+g²-2g·cosθ)^(3/2)
```

- g → 0：退化为 Rayleigh
- g → 1：强前向散射（雾、霾）。典型地球大气 g ≈ 0.8
- 比标准 Henyey-Greenstein 更准确地再现后向散射和光环效应

### 1.3 透射率 (Transmittance)

Beer-Lambert 定律——光穿过介质时的衰减：

```
T(A→B) = exp( -∫[A→B] β_R·ρ_R(s) + β_M·ρ_M(s) ds )
       = exp( -β_R·τ_R - β_M·τ_M )
```

τ_R, τ_M 是光学深度（optical depth）——路径上密度对距离的积分，**与散射系数无关**。将两者分离的好处：同一个 τ 可以被不同的 β 复用。

### 1.4 体积渲染方程

沿视线方向从相机 C 到点 P 积分：

```
L(C→P) = ∫[0→d] T(C→Q) · S(Q) · dQ

其中：
  T(C→Q) = 从相机到 Q 的透射率（衰减）
  S(Q)   = Q 处的散射源项（向视线方向散射了多少光）
```

**单次散射源项**：

```
S₁(Q) = SunIntensity · T(Q→Sun) · β·ρ(h) · γ(θ)
       = SunIntensity · (阳光到达 Q)      · (散射能力)  · (相位)
```

**多次散射源项**（Hillaire 2020）：

```
S_ms(Q) = SunIntensity · σ_s(h) · F_ms     （F_ms 来自预计算 LUT）
```

---

## 2. LUT 管线总览

```
OpticalDepthLUT (256×256, RG16Half)
    │
    ├─→ MultiScatteringLUT (32×32, ARGBFloat)
    │       │
    │       └─→ SkyViewLut (512×256, ARGBFloat)  ──→ 天空盒
    │       └─→ AerialPerspectiveLUT (128×72×64, ARGBHalf)  ──→ 场景物体
```

四个 LUT 按依赖顺序生成，每帧更新。

### 2.1 Optical Depth LUT

**用途**：存储从大气中任意点到大气层顶的光学深度。

**UV 映射**：
- U = cos(sun_zenith)，从 -1（太阳在地平线下）到 +1（太阳在头顶）
- V = 高度，从 0（地面）到 atmosphereHeight（大气层顶）

**输出**：`float2(τ_Rayleigh, τ_Mie)` — 原始光学深度，不含 β 系数。

**关键设计选择**：存储 τ 而非 T。运行时乘 β 恢复透射率：
```
T = exp(-β_R·τ_R - β_M·τ_M)
```

优点：可以用不同波长的 β 读取同一个 LUT，只需一个 RG 通道而非 RGB。

**算法**：对每个 (cosSunZenith, height)，沿太阳方向 ray-march 到大气层顶，累加 `exp(-h/H) · ds`。若射线击中行星则返回 (1e20, 1e20) 确保 `exp(-β·1e20) ≈ 0` 且 GPU 双线性插值友好（不会在边界产生错误插值）。

### 2.2 Multi-Scattering LUT

**用途**：高阶散射辐射的预计算。不逐视线重复计算 2 阶+散射。

**理论**（Hillaire 2020）：

从某点 P 出发，向全空间所有方向发射射线，累积：

```
G = Σ_d Σ_s  T_sun · J · T_view · (1/4π) · ds · (4π/N_directions)
f = Σ_d Σ_s  T_view · σ_s · (1/4π) · ds · (4π/N_directions)
```

- **G** = 从 P 看到的所有方向来的单次散射光的总和（对各向同性相位归一化）
- **f** = P 处散射-消光比的球面积分。f < 1 时几何级数收敛

高阶散射总和 = G / (1 - f)。这是几何级数 `G(1 + f + f² + ...)` 的闭合形式。

**为什么 f < 1 很重要**：如果 f → 1，级数发散。实际实现中：
- 消光系数 kExtinction 包含 Mie 吸收项 (4.4×10⁻⁶ m⁻¹)，确保 σ_s < σ_e
- 分母加 `max(1.0 - f, 1e-4)` 保护

**UV 映射**：cosSunZenith × height，32×32 分辨率足够（变化缓慢）。

**方向采样**：64 个 Fibonacci 球面方向（黄金角分布，比经纬网格更均匀）。

**注意**：per-direction 积分必须用各向同性相位 `1/(4π)`，不能用 Mie 各向异性相位。高阶散射经过多次弹射后方向已随机化。

**运行时使用**：
```
ms_radiance = LUT.Sample(cosSunZenith, height).rgb × σ_s(height)
```
存储的是 G/(1-f)，乘回 σ_s 得到该点当前的散射辐射。

### 2.3 Sky View LUT

**用途**：从相机位置看每个方向的天空颜色。

**UV 映射 — 非线性等距矩形投影（Hillaire 2020）**：

```
latitude  = asin(v.y)
n         = latitude / (π/2)        // [-1, 1]
uv.y      = 0.5 + 0.5·sign(n)·√|n|  // 地平线附近更多像素
uv.x      = atan2(v.z, v.x)/(2π) + 0.5
```

为什么需要非线性：黄昏时散射在 0°~3° 仰角范围内剧烈变化。线性映射会在这个区域只有极少数像素 → 严重条带。sqrt 压缩把更多 texel 分配给地平线附近。

**算法**：对每个视线方向，ray-march 从相机到大气层顶（或到行星表面）：

```
for each step s:
    J       = β_R·ρ_R·γ_R(θ) + β_M·ρ_M·γ_M(θ)   // 散射源
    T_PA    = exp(-β_R·τ_R - β_M·τ_M)              // 到相机的透射率（累计）
    T_CP    = SampleOpticalDepthLUT(h, cosSun)      // 到太阳的透射率
    ms      = SampleMultiScatteringLUT(h, cosSun)   // 多散贡献

    L += SunIntensity · SunColor · (J·T_CP + ms) · T_PA · ds

    τ_R += exp(-h/H_R)·ds   // 累积光学深度
    τ_M += exp(-h/H_M)·ds
```

多散项 **不乘 T_CP**，因为 MultiScatteringLUT 已经考虑了从所有方向到达的阳光。

**太阳圆盘**：采样完 LUT 后额外叠加分析计算的太阳圆盘（Bruneton 2017 地平线 smoothstep），当太阳在地平线以下时平滑消隐。

### 2.4 Aerial Perspective LUT

**用途**：对场景中每个物体应用大气透视——远处物体被大气散射光覆盖 + 被衰减。

**3D 纹理**：128×72×64，ARGBHalf。
- X, Y = 屏幕空间 UV（与相机视锥体对齐）
- Z = 深度，平方分布（近处密集、远处稀疏）

**深度映射**：

```
LUT 生成（slice index → world distance）:
  normalized = (z+0.5) / NUM_SLICES
  tMax = normalized² · NUM_SLICES · AP_METERS_PER_SLICE

Composite 采样（world distance → texture Z）:
  w = sqrt(tDepth / (AP_METERS_PER_SLICE · NUM_SLICES))
```

`AP_METERS_PER_SLICE` 控制 LUT 覆盖的最大深度（默认 400m × 64 = 25.6km）。

**积分**：与 SkyViewLut 相同算法，但每 voxel 独立计算从相机到该 voxel 深度的积分。近 voxel 用 2 个采样点，远 voxel 用 128 个采样点。

**输出**：`float4(L.rgb, 1-T)` — inscattered 光 + 不透明度。

**Composite 合成**：

```
AP = AerialPerspectiveLUT.Sample(screenUV, z=w)
finalColor = sceneColor + AP.rgb    // 叠加大气散射光
finalAlpha = AP.a                     // 不透明度
```

注意：参考实现用纯加法（不衰减场景色）。物理正确的公式 `sceneColor·T + L` 在大气透视为次要效果的场景中与纯加法差异很小。

**天空像素处理**：Reversed-Z 下 depth=0 是天空盒，必须跳过 AP。天空本身就是大气散射的结果，不能再叠一层。

---

## 3. 地平线处理

### 3.1 透射率到太阳（Bruneton 2017 smoothstep）

当太阳在地平线以下时，LUT 查找仍会返回非零透射率（因为大气层延伸到地平线以下）。需要额外衰减：

```
sinThetaH = PlanetRadius / (PlanetRadius + height)   // 地平线角的正弦
cosThetaH = -√(1 - sinThetaH²)                        // 地平线的 cos zenith

visibility = smoothstep(-sinΘH·α, sinΘH·α, cosθ - cosΘH)
             // α = sunAngularRadius（太阳视角半径, 约 0.5°）

T_to_sun = SampleLUT(height, cosθ) × visibility
```

物理含义：`cosΘH` 是地平线方向的 cos zenith。当 `cosθ < cosΘH` 时太阳在地平线下。`smoothstep` 在太阳圆盘宽度范围内平滑过渡。

### 3.2 地球阴影

对每个采样点 P，向太阳方向投射阴影射线：

```
tEarth = RaySphereIntersect(P, sunDir, planetCenter + offset, planetRadius)
shadow = tEarth ≥ 0 ? 0 : 1   // 射线击中行星 = 在阴影中
```

offset 是微小的向外偏移（PLANET_RADIUS_OFFSET = 0.001m），防止自交叉。

Bruneton smoothstep 更高效（无额外 ray-sphere），地球阴影更精确（考虑具体几何），两者可以互补使用。

### 3.3 Ground Clamping（AP LUT 特有）

视锥体延伸到地平线以下的 voxel，其世界坐标会穿透行星。处理方式：

```
if (viewHeight ≤ planetRadius + offset):
    1. 将 voxel 位置投影到行星表面
    2. 重新计算 viewDir = normalize(surfacePoint - cameraPos)
    3. 设 tMax = distance(cameraPos, surfacePoint)
    4. 射线从相机出发沿新的 viewDir 积分到行星表面
```

**关键**：射线积分起点必须保持为相机位置，不能改为 surfacePoint。否则射线从表面往地心走，tMax = 0，输出黑色孔洞。

---

## 4. 积分方法

### 4.1 简单加和 vs 解析积分

**简单加和**：
```
L += S · T_camera · ds
```

每一步假设 S 和 extinction 在步长内恒定。步长较大时有能量误差。

**解析积分（Frostbite 2015 slide 28）**：
```
Sint = (S - S·exp(-σ_e·ds)) / σ_e
L   += T_camera · Sint
T_camera *= exp(-σ_e·ds)
```

假设步长内 extinction 恒定，解析解出该段的光传输。步长大时精度更高；细步长时两者等价。

### 4.2 平方采样分布（SkyViewLut 参考实现）

```
t = tMax · (s/sampleCount)²
```

近相机处采样更密集（消光系数大、变化快），远处稀疏。在没有大幅增加采样数的情况下提升近场精度。

### 4.3 解析太阳地平线（GetTransmittanceToSun）

见第 3.1 节。比 per-sample 地球阴影射线更高效（省掉每次 ray-sphere 交点计算）。

---

## 5. 单位换算陷阱

参考实现用 km 单位（`mPositionScale = 0.001`），散射系数比物理值大 1000 倍。但 extinction·dt 的乘积不变——单位系统对最终结果没有影响。

**唯一会出错的地方**：控制 LUT 覆盖范围的距离参数。`AP_KM_PER_SLICE` 在 km 系统中设为 0.2（覆盖 12.8km），在 m 系统中需相应地设为 200m。值差 1000 倍，错一个量级 LUT 就完全不工作。

---

## 6. 常见 Bug

### 6.1 AP LUT 偏黑

**原因**：`height` 用了径向距离（`length(P)` ≈ 6.37×10⁶m）而非海拔高度。`exp(-6.37e6/8000) ≈ 0` → 所有散射密度为零。

**修复**：`height = length(P - planetCenter) - planetRadius`

### 6.2 AP LUT 地平线以下黑色孔洞

**原因**：ground clamping 后把射线起点 `positionWS` 移到了行星表面，导致后续积分从表面往地心走，tMax = 0。

**修复**：射线积分始终从 `worldCam`（相机位置）出发，ground clamping 只用于修正 `viewDir` 和 `tMaxMax`。

### 6.3 MultiScatteringLUT 用各向异性相位

**原因**：per-direction 积分调用了 `EvaluateInScattering`（含 MiePhase 各向异性），但高阶散射经过多次弹射后是各向同性的。

**修复**：多散积分中所有方向统一用 `1/(4π)` 相位。

### 6.4 AP LUT 深度范围不够

**原因**：`AP_METERS_PER_SLICE` 太小（如 0.5）导致 LUT 只覆盖几十米。

**修复**：设为 `atmosphereHeight × 2 / NUM_SLICES` 以上的值，覆盖整个大气层路径。

---

## 7. 参数调优

| 参数 | 效果 | 建议范围 |
|------|------|----------|
| `SunIntensity` | 整体亮度（天空 + 大气透视） | 10000–200000 |
| `APIntensity` | 仅大气透视强度 | 0.5–3.0 |
| `MieG` | 雾的集中方向。0 = 各向同性，0.9 = 强前向 | 0.7–0.9 |
| `ScaleHeight` (Rayleigh) | 天蓝色层厚度 | 6000–12000m |
| `MieScaleHeight` | 霾层厚度。越小 = 越低 = 越浓 | 800–2000m |
| `AP_METERS_PER_SLICE` | AP LUT 覆盖深度 | 200–800m |
| `ViewSamples` | SkyViewLut 采样质量 | 16–64 |

---

## 8. 参考资源

- **Bruneton 2017**: Precomputed Atmospheric Scattering — LUT 参数化的数学基础
- **Hillaire 2020**: A Scalable and Production Ready Sky and Atmosphere — 多散闭合公式 `G/(1-f)` 来源
- **Frostbite 2015**: Physically Based Unified Volumetric Rendering — 解析积分方法
- **Cornette & Shanks 1992**: Mie 相位函数的改进形式
- [AKGWSB/RealTimeAtmosphere](https://github.com/AKGWSB/RealTimeAtmosphere) — 本项目参考实现
- [UnitySkyAtmosphere](https://github.com/ducandu/UnitySkyAtmosphere) — 对比参考实现（UE SkyAtmosphere 移植）
