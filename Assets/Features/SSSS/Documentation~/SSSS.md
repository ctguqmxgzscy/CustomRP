# SSSS — Separable Subsurface Scattering

基于 Jorge Jimenez 2015 论文 "Separable Subsurface Scattering" 的屏幕空间次表面散射实现。用两次 separable Gaussian blur 近似扩散剖面卷积，适用于皮肤、大理石、蜡、树叶等半透明材质。

## 架构

```
SSSLit.shader (Forward, "Skin MRT")
  → 写入 Stencil + 三 MRT (_SkinDiffuseRT + _SkinSpecularRT + _SkinAmbientRT)

SSSSPassFeature (ScriptableRendererFeature)
  → 1. 分配 3 RT + blur temp RT，设置全局纹理
  → 2. MRT DrawPass: 三路输出到 RT
  → 3. 清理 _SSSBlurTempRT
  → 4. 遍历 SSSSProfileSet.entries
    → 对每个 entry:
      a. CPU 端用 SSSSProfile 参数计算 5-Gaussian 混合核 (单/双模式)
      b. 按 stencilRef 取对应材质实例，设 _StencilRef
      c. 上传 _Kernel[25] + _SSSScale 到 shader
      d. SSS.shader Pass 0: Blur X → _SSSBlurTempRT  (Stencil Equal, 深度边界修正)
      e. SSS.shader Pass 1: Blur Y → _SkinDiffuseRT  (Stencil Equal, 深度边界修正)
      f. SSS.shader Pass 2: Composite (diffuse + specular + ambient) → camera color (Stencil Equal)
```

## 数据流

```
[CPU] SSSSProfile → KernelCalculate.CalculateKernel()
  → _Kernel[25] (Vector4 per sample: RGB weight + UV offset w)

[GPU] SSSLit.shader Forward Pass ("Skin MRT")
  → Stencil: 写入 _StencilRef 值
  → SV_Target0: 直接漫反射  → _SkinDiffuseRT   (RGB, camera format)
  → SV_Target1: 镜面反射    → _SkinSpecularRT  (RGB, camera format)
  → SV_Target2: 间接光+自发光 → _SkinAmbientRT  (RGB, camera format)

[GPU] SSS.shader
  → Blur X: _SkinDiffuseRT → _SSSBlurTempRT   (Stencil Equal, Ref=_StencilRef)
  → Blur Y: _SSSBlurTempRT → _SkinDiffuseRT   (Stencil Equal, Ref=_StencilRef)
  → Composite: _SkinDiffuseRT + _SkinSpecularRT + _SkinAmbientRT → camera color (Stencil Equal)

SSSBlur.hlsl 卷积管线:
  1. LinearEyeDepth(中心uv) → eyeDepth
  2. sssWidth = unity_CameraProjection._m11 × _SSSScale / eyeDepth   (世界空间散射宽度 → 像素空间)
  3. stepUV = sssWidth × dir × texelSize                              (当前方向的屏幕空间步长)
  4. centerColor = 采样 _BlitTexture[uv]
  5. result = _Kernel[0].rgb × centerColor                            (中心权重 × 中心色)
  6. for k = 1..24:
       neighborDepth = LinearEyeDepth(uv + _Kernel[k].a × stepUV)
       depthDiff     = |neighborDepth - eyeDepth|
       clampFactor   = saturate(focalLength × 300 × sssIntencity × depthDiff)
       neighborColor = lerp(neighborColor, centerColor, clampFactor)  (深度边界修正)
       result.rgb   += _Kernel[k].rgb × neighborColor                 (加权累加)
```

## 关键文件

| 文件 | 职责 |
|------|------|
| `Runtime/SSSSPassFeature.cs` | RenderFeature 入口，MRT 分配 + profile 遍历 + blur/composite dispatch |
| `Runtime/SSSSProfile.cs` | ScriptableObject，单/双散射参数 + 热力图渲染参数 |
| `Runtime/SSSSProfileSet.cs` | ScriptableObject，Entry[] 列表 (profile + stencilRef) |
| `Runtime/KernelCalculate.cs` | CPU 端 5-Gaussian 混合核计算，支持单/双 scatterScale |
| `Runtime/Shader/SSSLit.shader` | Forward 渲染，写 Stencil + 三 MRT (diffuse/specular/ambient) |
| `Runtime/Shader/SSS.shader` | 全屏后处理：BlurX / BlurY / Composite (3 RT 合成) |
| `Runtime/Shader/SSSBlur.hlsl` | 可分离卷积核心：深度感知步长 + 深度边界修正 |
| `Runtime/Shader/SSSSTransmission.hlsl` | Beer-Lambert 透射工具函数（未接入，待 thickness map 就绪） |
| `Runtime/Editor/SSSSProfileEditor.cs` | Inspector 面板 2D 扩散剖面热力图 |

## 参数

### SSSSPassFeature Settings

| 参数 | 类型 | 说明 |
|------|------|------|
| `scaler` | float (0–10) | 屏幕空间散射宽度。影响两个维度：(1) 步长 `sssWidth ∝ scaler` (2) 深度边界 clamp 强度 `∝ scaler` |
| `sssLayerMask` | LayerMask | 参与 SSS 渲染的 Layer |
| `profileSet` | SSSSProfileSet | 要渲染的 profile 列表 |

### SSSSProfile

| 参数 | 类型 | 说明 |
|------|------|------|
| `scatterScale` | float (0.1–5) | 单模式散射半径缩放，1.0 = 皮肤基准 |
| `mainColor` | Color (RGB 0–1) | 逐通道散射强度。`Vector3.Normalize` 后使用，**只有 RGB 比例有意义** |
| `falloff` | Color (RGB 0–1) | 逐通道散射距离。`Vector3.Normalize` 后使用，值越大 → 散射距离越短 |
| `useDualScale` | bool | 启用 Near/Far 双散射剖面混合 |
| `nearScatterScale` | float (0.02–5) | 近场散射半径。小 = 锐利细节，仅短程散射 |
| `farScatterScale` | float (0.1–5) | 远场散射半径。大 = 宽广柔和扩散 |
| `nearFarBalance` | float (0–1) | 混合权重，0 = 全远场，1 = 全近场。典型值 0.2–0.5 |

> **注意** `mainColor` 和 `falloff` 都经过 `Vector3.Normalize`，只有 RGB 三通道之间的比例起作用。`(1, 0.31, 0.20)` 和 `(2, 0.62, 0.40)` 完全等价。

Profile 的 Inspector 面板显示径向扩散剖面热力图——5-Gaussian 混合的**连续剖面形状**（非离散 kernel），逐通道着色，中心最亮。

### SSSSProfileSet

| 字段 | 类型 | 说明 |
|------|------|------|
| `entries` | Entry[] | profile + stencilRef 配对列表 |
| `entries[i].profile` | SSSSProfile | 该材质的散射参数 |
| `entries[i].stencilRef` | int (0–255) | 该材质对应的 Stencil 值，需与 SSSLit 材质的 `_StencilRef` 匹配 |

## 内核算法

### KernelCalculate 流水线

```
1. Offset 生成（非线性采样）          —— 25 个样本点，距中心 [-3, 3]
2. Profile 评估（单/双模式）          —— 逐样本计算 RGB 散射强度
3. 面积加权                           —— 梯形法则离散积分
4. Center reorder                     —— 中心样本移到 kernel[0]
5. 逐通道归一化                       —— sum(r/g/b) = 1
6. Strength 调制                      —— (1-strength) × 1.0 + strength × weight
```

### 单模式 (useDualScale = false)

```csharp
profile(r, falloff, scatterScale):
  scaledR = r / scatterScale
  return 0.100 × G(σ²=0.0484, scaledR, falloff)
       + 0.118 × G(σ²=0.187,  scaledR, falloff)
       + 0.113 × G(σ²=0.567,  scaledR, falloff)
       + 0.358 × G(σ²=1.99,   scaledR, falloff)
       + 0.078 × G(σ²=7.41,   scaledR, falloff)
```

### 双模式 (useDualScale = true)

CPU 端分别计算近场和远场剖面后 lerp 混合：

```csharp
nearProfile = profile(r, falloff, nearScatterScale)
farProfile  = profile(r, falloff, farScatterScale)
blended     = lerp(farProfile, nearProfile, nearFarBalance)
```

混合后进入归一化和 strength 调制流程，与单模式完全相同。Shader 侧无感知。

### Gaussian 基函数

```csharp
G(variance, r, falloff):
  rr = r / (0.001 + falloff[channel])    // falloff 逐通道缩放 r
  return exp(-rr² / (2 × variance)) / (2π × variance)
```

### 关键参数说明

| 参数 | 含义 | 在 kernel 中的作用 |
|------|------|-------------------|
| `r` | 归一化采样偏移 ∈ [-3, 3] | 输入到 profile 评估的距离变量，无物理单位 |
| `scatterScale` | 全局半径缩放 | `scaledR = r / scatterScale`，越大 = profile 越宽 = blur 越远 |
| `falloff[channel]` | 逐通道距离缩放 | `rr = scaledR / falloff[i]`，越大 = 该通道衰减越快 = 散射距离越短 |
| `strength` (mainColor 归一化) | 逐通道散射能量分配 | center = (1−s)×1 + s×profile(0)；others = s×profile(r) |
| `_Kernel[k].rgb` | shader 中实际的卷积权重 | R/G/B 各自独立，已归一化 |
| `_Kernel[k].a` (= `kernel[i].w`) | UV 偏移系数 ∈ [-3, 3] | × stepUV = 屏幕空间像素偏移 |

### 约束

- 5 高斯的权重 (0.100/0.118/0.113/0.358/0.078) 和方差 (0.0484/0.187/0.567/1.99/7.41) 是皮肤 Monte Carlo 拟合结果，固定不可变
- 采样窗口 [-3, 3]，方差锚点已覆盖全窗口
- scatterScale 缩放 r → 方差等效缩放 scale²，保持 5 高斯间的相对关系不变
- 双模式混合在 CPU 端完成，不增加 shader 开销

## 深度边界修正 (Depth-Aware Boundary Clamping)

### 问题

separable blur 的 kernel 在物体边缘会采样到背景像素。由于 R 通道 blur 强度 (~94%) 远高于 B (~19%)，背景黑色对 R 通道的稀释最严重，导致边缘偏绿。

### 公式

参考 Jorge Jimenez 实现，在 SSSBlur.hlsl 中逐样本比较邻居深度：

```hlsl
float depthDiff    = abs(neighborDepth - eyeDepth);
float clampFactor  = saturate(focalLength × 300 × sssIntencity × depthDiff);
float3 neighborCol = lerp(neighborColor, centerColor, clampFactor);
```

- `focalLength` = `unity_CameraProjection._m11`（适应任意 FOV）
- `sssIntencity` = `_SSSScale × dir × texelSize`（当前方向的屏幕空间强度）
- `depthDiff` = 中心与邻居的世界空间深度差
- `clampFactor ≈ 0`：邻居在同一表面 → 正常 blur
- `clampFactor ≈ 1`：邻居在深度突变处（背景/其他物体） → 拉回中心色，防止渗色

### 行为

| scaler | FOV | 1cm 深度差 | 10cm 深度差 | 1m 深度差 |
|--------|-----|-----------|------------|----------|
| 1 | 60° | ~0 | ~0.03 | ~0.27 |
| 10 | 60° | ~0.03 | ~0.27 | ~1.0 |
| 10 | 20° | ~0.09 | ~0.89 | ~1.0 |

scaler 越大 → blur 越宽 → 越界风险越高 → 修正越强。FOV 越窄 → 景物越大 → 同物理深度差对应更多像素 → 修正越强。

## 与 bentoBAUX 实现对比

| 维度 | bentoBAUX | 本项目 |
|------|-----------|--------|
| **MRT** | 3 (Diffuse / Specular / Ambient) | 3 (同上) |
| **卷积核** | GPU 实时 Gaussian，归一化 | CPU 预计算 5-Gaussian Jensen 混合，强度调制，查表 |
| **Sigma 系统** | Near/Far 双 sigma + balance，shader 内混合 | Near/Far 双 scatterScale + balance，CPU 端混合 |
| **深度感知步长** | 无，屏幕空间固定步长 | 有，`sssWidth ∝ focalLength / eyeDepth` |
| **深度边界修正** | 无（靠归一化抑制边界泄漏） | 有，逐样本深度差 → lerp 回中心色 |
| **Stencil** | 无，diffuse RT 做 mask | 完整 Stencil 多材质隔离 |
| **透射** | SSSSTransmission.hlsl，接入各光源 | SSSSTransmission.hlsl 已引入，未接入（缺 thickness map） |
| **Profile 管理** | enum + static array，内联 Feature | ScriptableObject 独立资产 + Inspector 热力图 |
| **API** | RenderGraph (Unity 6+) | 传统 SRP (Unity 2022.3) |

## 预设 Profile

基于 Jensen 2001 "A Practical Model for Subsurface Light Transport" 实测 dmfp 数据。

### Skin

| 参数 | 值 | 依据 |
|------|-----|------|
| scatterScale | 1.0 | 基准，dmfp_R = 3.67mm |
| mainColor | (1.0, 0.31, 0.20) | albedo 归一化：R 散射强度主导 |
| falloff | (1.0, 0.51, 0.29) | dmfp 归一化：R 散射最远，B 最短 |

### Marble

| 参数 | 值 | 依据 |
|------|-----|------|
| scatterScale | 2.3 | dmfp_R = 8.51mm，比值 2.3× |
| mainColor | (1.0, 0.95, 0.91) | albedo 归一化：三通道均匀 |
| falloff | (1.0, 0.65, 0.46) | dmfp 归一化：散射距离较均匀 |

### Wax

| 参数 | 值 | 依据 |
|------|-----|------|
| scatterScale | 1.7 | 介于皮肤和大理石之间（估计） |
| mainColor | (1.0, 0.82, 0.70) | 估计：偏暖色调 |
| falloff | (1.0, 0.78, 0.63) | 估计：中等散射距离 |

### Foliage

| 参数 | 值 | 依据 |
|------|-----|------|
| scatterScale | 0.6 | 薄材质（估计） |
| mainColor | (0.5, 1.0, 0.3) | 估计：G 主导（叶绿素） |
| falloff | (0.4, 1.0, 0.5) | 估计：G 散射最远 |

## Stencil 系统

支持场景中同时存在多种 SSS 材质，各材质的 blur/composite 通过 `Stencil Equal` 隔离。

### 材质侧 (SSSLit.shader)

```hlsl
Properties {
    [IntRange] _StencilRef("SSS Stencil Ref", Range(0, 255)) = 1
}

// Forward Pass:
Stencil {
    Ref [_StencilRef]
    Comp Always
    Pass Replace       // 写入标记值到 Stencil Buffer
}
```

每个 SSSLit 材质实例单独设置 `_StencilRef`（如 Skin=1, Marble=2, Foliage=3）。

### 后处理侧 (SSS.shader)

```hlsl
Properties {
    [HideInInspector] _StencilRef("SSS Stencil Ref", Int) = 1
}

// 所有 Pass (BlurX / BlurY / Composite):
Stencil {
    Ref [_StencilRef]
    Comp Equal         // 只处理 Stencil 值匹配的像素
    Pass Keep
}
```

Feature 在每轮迭代前 `GetMaterial(stencilRef)` 获取预设了 `_StencilRef` 的材质实例。

### 多材质配置示例

```
SSSSProfileSet:
  entries[0]: profile=Skin,    stencilRef=1
  entries[1]: profile=Marble,  stencilRef=2
  entries[2]: profile=Foliage, stencilRef=3

SSSLit 材质实例:
  Character_Skin.mat:    _StencilRef = 1
  Statue_Marble.mat:     _StencilRef = 2
  Tree_Foliage.mat:      _StencilRef = 3
```

## 性能

每多一个 profile entry 增加 3 次全屏 quad pass（BlurX + BlurY + Composite），每次受 Stencil 过滤。

| Entry 数 | 全屏 Pass |
|----------|----------|
| 1 | 3 |
| 2 | 6 |
| 3 | 9 |

25 样本 × 2 方向 separable blur 是主要开销。每个样本额外一次 `SampleSceneDepth` + `LinearEyeDepth`。不同 stencilRef 的材质实例预创建并缓存（Dictionary<int, Material>），运行时无额外分配。

## 使用步骤

1. 为每种材质创建 `SSSSProfile` asset（或使用预设），调整 mainColor / falloff / scatterScale（或启用双模式）
2. 创建 `SSSSProfileSet` asset，添加 (profile, stencilRef) 配对
3. 在 URP Renderer 的 `SSSSPassFeature` 中指定 `profileSet`，设置 `scaler` 和 `sssLayerMask`
4. 为使用 SSS 的 `SSSLit` 材质设置 `_StencilRef`（匹配 ProfileSet 中对应的 stencilRef）
5. 在 SSSSProfile Inspector 中通过热力图预览扩散剖面
6. 添加额外 Profile 时：新 SSSLit 材质设新 `_StencilRef` → ProfileSet 加新 entry → 自动生效

## 参考

- [Jorge Jimenez, "Separable Subsurface Scattering", CGF 2015](https://onlinelibrary.wiley.com/doi/10.1111/cgf.12529)
- [Jensen et al., "A Practical Model for Subsurface Light Transport", SIGGRAPH 2001](https://dl.acm.org/doi/10.1145/383259.383319)
