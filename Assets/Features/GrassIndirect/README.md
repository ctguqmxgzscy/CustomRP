# Grass Indirect — GPU-Driven 草地渲染

## 概述

GPU-Driven 草地系统。从 Terrain detail density map 读取密度，compute shader 生成实例，经三级剔除（distance → frustum → Hi-Z），`DrawMeshInstancedIndirect` 渲染。

实例数据 `float4(pos.xyz, width)` + `float4(height, rotY, 0, 0)` 存在 `StructuredBuffer`。

## 文件

```
Features/GrassIndirect/
├── Runtime/
│   ├── GrassIndirectRenderer.cs         # 入口：参数管理 + per-frame dispatch/draw
│   ├── GrassPrototypeData.cs            # ScriptableObject：per-prototype 配置
│   ├── TerrainGrassData.cs              # TerrainData 密度层读取 + GPU buffer 上传
│   ├── Shaders/
│   │   ├── GrassIndirect.shader         # 主渲染 shader (UniversalForwardOnly)
│   │   ├── GrassInstanceGen.compute     # 实例生成 compute shader
│   │   └── GrassCulling.compute         # 剔除 compute shader (distance + frustum + Hi-Z)
│   └── GrassTestDraw.cs                 # 测试用 ProceduralMesh draw
└── Editor/
    └── GrassPrototypeDataGenerator.cs   # 从 Terrain detail prototype 生成 ScriptableObject
```

## 数据流

```
1. Editor: Terrain.detailPrototypes → GrassPrototypeData.asset
2. Init:   TerrainData.GetDetailLayer() → _DensityBuffer (float[], packed)
           ProtoParams → _ProtoDescs (StructuredBuffer)
3. Frame:  GrassInstanceGen.compute  → transformBuffer
           GrassCulling.compute      → instanceBuffer (compact)
           DrawMeshInstancedIndirect(instanceBuffer, _IndirectArgs)
```

### 实例生成 (`GrassInstanceGen.compute`)

每个线程处理一个密度图 texel：
- densityVal = `_DensityBuffer[offset + texelIdx] × _DensityScale`
- `guaranteed + (hash < frac ? 1 : 0)` 决定生成 N 个实例
- `InterlockedAdd(_GenCounter, count, dstIdx)` — 原子分配连续输出槽位
- 对每个实例：UV → XZ 世界坐标，heightmap 采样 → Y；Hash 随机化 width/height/rotY
- 写入 `_InstanceOutput[idx*2] = float4(pos, width)`, `[idx*2+1] = float4(height, rotY, 0, 0)`

### 实例剔除 (`GrassCulling.compute`)

三级级联（每级 early-out）：

| 级别 | 策略 | 详情 |
|------|------|------|
| 1. Distance | `|pos - camera| > _MaxDrawDistance` → cull | 全局距离上限 |
| 2. Frustum | AABB 8 角点 vs 6 个视锥面 | XZ 范围用 `max(w,h)*0.5` 覆盖 Y 旋转 |
| 3. Hi-Z | 单点 mip0 深度比较 | 见下文 |

通过全部测试的实例经 `InterlockedAdd(_IndirectArgs[1], 1, dstIdx)` 紧凑写入 `_OutputBuffer`，同时 `_IndirectArgs[1]` 作为间接绘制 instanceCount。

### Hi-Z 遮挡剔除

**策略：单点 mip0 + 相对深度偏置**

草叶片在屏幕上只占 1-5 像素宽。多点采样 + padding 会把采样矩形扩到遮挡体外，命中天空像素（depth=0），导致 `sceneFarthestDepth` 被拉到 0，遮挡判断永远失败。故采用最激进策略：单点中心采样 mip0，等价于逐像素深度检测。

**双 AABB 设计：**

| AABB | XZ 范围 | 用途 |
|------|---------|------|
| Frustum AABB | `max(w, h) × 0.5` | 视锥剔除，保守覆盖任意 Y 旋转 |
| Hi-Z AABB | `w × 0.5` | 深度采样，紧致避免溢出遮挡体 |

**相对深度偏置：**

reversed-Z 下远处 depth 值仅 0.00x，绝对偏置（如 0.005）比 depth 本身大，比较永远失败：

```
threshold = occluderDepth × (1 - bias)
```

bias=0.02 时：远处 occluderDepth=0.002，threshold=0.00196；近处 occluderDepth=0.06，threshold=0.0588。自动适配全深度范围。

**依赖：** 场景需挂 `HiZDepthRenderer`（主相机上）+ `OcclusionCullingSystem`。Hi-Z 为上帧的深度金字塔（1 帧延迟）。

## 渲染参数

| 参数 | 范围 | 默认 | 作用 |
|------|------|------|------|
| `globalDensity` | 0-16 | 1 | 全局密度缩放 |
| `maxDrawDistance` | 10-500 | 150 | 最大绘制距离 |
| `windStrength` | 0-2 | 0.5 | 风力强度 |
| `windDirection` | Vector2 | (0.4, 0.8) | 风向 |
| `m_enableHiZ` | bool | true | 启用 Hi-Z 遮挡剔除 |
| `occlusionDepthBias` | 0-0.2 | 0.02 | 相对深度偏置 (2% = 草必须在遮挡物后 2% 才剔除) |

## 光照模型

风格化 PBR 光照，三个层级：

| 层级 | 法线来源 | 用途 |
|------|---------|------|
| 单根 blade | per-vertex normal map | 高光细部、透射 |
| 草丛 group | heightmap 重建的地形法线 (`terrainNormalBlur`) | SSS diffuse、group specular、group transmission |
| 过渡 | `lerp(N, terrainNormalBlur, _SSSIntensity)` | 控制层级混合程度 |

### 高光 (Specular)

4 层 GGX lobe，无几何遮蔽项 (G)，用距离衰减替代：

- Primary: 3 层 lobe (`r`, `r*0.5`, `r*0.25`) × `_GrassWater` 驱动的 roughness
- Secondary: 1 层 lobe (`r*1.5`) 使用地形法线
- `distFade = saturate(dist / 150)` — 越远越强（近处抑制满屏高光，远处显示整体草地高光）
- 总权重 1.0（0.9 + 0.1）

### 次表面散射 (SSS) — 小曲率

**不作额外计算。** 通过模糊地形法线 → 普通漫反射即可自然产生小曲率散射：

1. `GetTerrainNormal(worldPos, _SSSRadius)`: 从 heightmap 中心差分重建地形法线，`_SSSRadius` 放大采样步长 → 法线平滑
2. `N_sss = lerp(N_blade, terrainNormalBlur, _SSSIntensity)`
3. Diffuse、specular、transmission 均用 `N_sss`

### 透射 (Transmission)

两层混合：
- `transBlade`: blade normal → 单根草薄叶透光
- `transGroup`: terrain normal → 整体草地背散射
- `lerp(transBlade, transGroup, _SSSIntensity)`

### Diffuse

风格化三色映射（暗部色 / 基础色 / 亮部色）× texColor，基于 `NdotL_sss`。

## Shader 参数

| 参数 | 范围 | 默认 | 作用 |
|------|------|------|------|
| `_BaseColor` | Color | (0.3, 0.6, 0.2) | 基础色 |
| `_ShadowColor` | Color | (0.05, 0.1, 0.02) | 暗部色 |
| `_HighlightColor` | Color | (0.6, 0.85, 0.3) | 亮部色 |
| `_GrassWater` | 0-1 | 0.3 | 水分（控制高光 roughness 和多层 lobe 强度） |
| `_SpecularSmoothness` | 0-1 | 0.5 | 全局高光平滑度缩放 |
| `_SpecularColor` | Color | (1,1,1) | 高光颜色 |
| `_TransIntensity` | 0-5 | 2 | 透射强度 |
| `_TransLerp` | 0-1 | 0.7 | 透射法线弯曲程度 |
| `_TransExp` | 1-8 | 2 | 透射指数 |
| `_SSSIntensity` | 0-2 | 0.5 | 小曲率散射/group 混合强度 |
| `_SSSRadius` | 0-1 | 0.3 | 地形法线模糊半径 |

## Cascade Shadow 条带问题

### 现象

`UniversalForwardOnly` + `DrawMeshInstancedIndirect`。开启 cascade shadow 后草表面出现环形黑色阴影带。

### 根因

`TransformWorldToShadowCoord()` 在顶点着色器中计算 cascade index。草 blade 的顶/底顶点可能落在不同 cascade，光栅化时线性插值产生无效 index。

### 修复

```hlsl
// Frag shader
#if defined(_MAIN_LIGHT_SHADOWS_CASCADE)
    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS); // 逐像素重算
#else
    float4 shadowCoord = input.shadowCoord;
#endif
```

## TODO

- [ ] Distance-based density fade + LOD
- [ ] 地形法线对齐 blade 生长方向（顺坡而非竖直）
- [ ] 噪声纹理替代纯随机（成片草疏密斑块）
- [ ] 双线性 heightmap 采样（替换 point sample）
