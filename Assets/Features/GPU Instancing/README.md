# GPU Instancing — GPU-Driven 草地渲染

## 概述

GPU-Driven 草地系统。从 Terrain detail density map 读取密度，compute shader 生成实例矩阵，经三级剔除（distance → frustum → Hi-Z），`DrawMeshInstancedIndirect` 渲染。支持多 prototype，每帧 per-proto 独立 gen → cull → draw 流水线。

实例数据以 `float4x4` 矩阵存储在 `StructuredBuffer` 中，通过 `SV_InstanceID` 在 vertex shader 中读取。

## 命名空间

`GPUDriven` — 所有 C# 代码使用 `GPUDriven` 命名空间。

## 文件

```
Features/GPU Instancing/
├── Runtime/
│   ├── GPUInstancingRenderer.cs        # 入口：参数管理 + per-frame gen → cull → draw 调度
│   ├── GPUInstancingDrawer.cs          # 静态工具类：buffer 初始化 + 剔除 dispatch + DrawMeshInstancedIndirect
│   ├── InstanceData.cs                 # ScriptableObject：per-prototype 配置 + GPU buffer 引用
│   ├── TerrainGrassData.cs             # TerrainData 密度层读取 + GPU buffer 上传（独立工具类）
│   ├── GPUInstanceingDebug.cs          # 测试用 MonoBehaviour（Editor 调试）
│   └── Shaders/
│       ├── GrassIndirect.shader        # 主渲染 shader（Custom/GrassIndirect, UniversalForwardOnly）
│       ├── Grass.shader                # 备用 shader（Custom/Grass, 标准 Unity instancing 路径）
│       ├── GrassInstanceGen.compute    # 实例生成 compute shader
│       ├── InstancCulling.compute      # 剔除 compute shader（frustum + Hi-Z 合并）
│       ├── Grass.mat                   # 草地材质预设
│       └── Wet_Grass.mat / Dry_Grass.mat / Custom_Grass.mat
└── Editor/
    ├── GrassPrototypeDataGenerator.cs  # GPUInstancingRenderer Inspector：从 Terrain 生成 InstanceData assets
    └── InstanceDataEditor.cs           # InstanceData Inspector：随机生成测试数据按钮
```

## 数据流

```
Editor:   Terrain.detailPrototypes → InstanceData.asset（ScriptableObject）
Init:     TerrainData.GetDetailLayer() → _DensityBuffer (float[], 打包多proto)
          GpuProtoDesc → _ProtoDescs (StructuredBuffer)
          per-proto: matrixBuffer, validMatrixBuffer (Append), argsBuffer (IndirectArguments), genCounter
Frame:    GrassInstanceGen.compute  → matrixBuffer (per-proto, float4x4)
          CPU readback genCounter   → instanceCount
          InstancCulling.compute    → validMatrixBuffer (compact, append)
          DrawMeshInstancedIndirect(validMatrixBuffer, argsBuffer)
```

### 实例生成 (`GrassInstanceGen.compute`)

每个线程处理一个密度图 texel（dispatch 覆盖所有 proto 的密度 texel）：

1. `densityVal = _DensityBuffer[offset + texelIdx] × _DensityScale`
2. `guaranteed + (hash < frac ? 1 : 0)` 决定生成 N 个实例
3. `InterlockedAdd(_GenCounter[0], count, dstIdx)` — 原子分配连续输出槽位
4. 对每个实例：UV + hash jitter → XZ 世界坐标，heightmap 采样 → Y；hash 随机化 width/height/rotY
5. 写入 `_MatrixBuffer[idx] = transpose(BuildTRS(pos, rotY, w, h))` — float4x4 矩阵（**注意 transpose**：HLSL 列主序 vs C# 行主序）

### 实例剔除 (`InstancCulling.compute`)

四级级联（`CSCullInstances` kernel，128 线程/组），每级 early-out：

| 级别 | 策略 | 详情 |
|------|------|------|
| 1. Null check | `ObjectToWorld._m33 == 0` → skip | 空矩阵检测 |
| 2. Distance | `distance(worldPos, _CameraPos) > _MaxDrawDistance` → cull | 全局距离上限 |
| 3. Frustum | AABB 8 角点 vs 6 个视锥面 | mesh 本地 bounds 变换到世界空间 |
| 4. Hi-Z | NDC bounding box → mip level → 单点深度比较 | 见下文 |

世界坐标从 `_m03/_m13/_m23` 提取（`transpose()` 后矩阵在 `StructuredBuffer` 中以 row-major 存储，position 在每行 column 3）。

通过全部测试的实例经 `_ValidMatrixBuffer.Append(ObjectToWorld)` + `InterlockedAdd(_ArgsBuffer[1], 1)` 紧凑写入。`_ArgsBuffer[1]` 作为间接绘制的 instanceCount。

### Hi-Z 遮挡剔除

**策略：NDC bounding box → 自适应 mip level → 单点采样**

```
1. 将 8 个世界空间 AABB 角点变换到 NDC (× VP matrix, perspective divide)
2. 计算 NDC bounding box: xmin/xmax, ymin/ymax, zmin/zmax, center
3. boxSize = clamp(max(xmax-xmin, ymax-ymin) * 0.5, 0, 1)
4. lod = clamp(ceil(log2(boxSize * _Size)), 0, 11)   — 自适应 mip level
5. uv = center.xy * 0.5 + 0.5 → 缩放至 mip 分辨率
6. depth = _HiZTexture.Load(int3(uv, lod)).r
7. Reversed-Z: depth < zmax → 可见
```

**Hi-Z 参数：**

| 参数 | 默认 | 作用 |
|------|------|------|
| `occlusionDynamicOffset` | 0.5 | 动态深度偏移（GPUI 风格，把 culling 边界推到遮挡体内部） |
| `occlusionOffset` | 0 | 静态深度偏移 |
| `occlusionAccuracy` | 1 | 精度等级（0-3） |

**依赖：** 场景需挂 `OcclusionCullingSystem` + `HiZDepthRenderer`（主相机上）。Hi-Z 为上帧深度金字塔（1 帧延迟）。

## 架构

### GPUInstancingRenderer（入口 MonoBehaviour）

- `[DefaultExecutionOrder(150)]`，挂在主相机上
- `OnEnable`: Initialize → DisableUnityDetailRendering → 注册 `beginCameraRendering`
- `OnBeginCameraRendering`: 每帧 per-proto gen → readback → cull → draw。gen 前更新 `_protoDescBuffer` 中对应 proto 的可编辑字段（`minWidth`/`maxWidth`/`minHeight`/`maxHeight`/`noiseSpread`/`renderMode`），PlayMode 调节 InstanceData 参数实时生效。

**关键字段：**

| 字段 | 类型 | 作用 |
|------|------|------|
| `m_InstanceGenCS` | ComputeShader | 实例生成 |
| `m_CullingCS` | ComputeShader | 剔除（InstancCulling） |
| `m_Prototypes` | List\<InstanceData\> | proto 列表 |
| `m_FallbackGrassMesh` | Mesh | 默认 blade quad（无 proto mesh 时） |
| `m_FallbackGrassMaterial` | Material | 默认 blade 材质 |
| `globalDensity` | 0-16 (1) | 全局密度缩放 |
| `maxDrawDistance` | 10-500 (150) | 最大绘制距离（culling level 2） |
| `windStrength` | 0-2 (0.5) | 风力强度 |
| `windDirection` | Vector2 (0.4,0.8) | 风向 |
| `m_enableHiZ` | bool (true) | 启用 Hi-Z 遮挡剔除 |
| `occlusionDynamicOffset` | 0-2 (0.5) | Hi-Z 动态偏移 |
| `occlusionOffset` | 0-0.01 (0) | Hi-Z 静态偏移 |
| `occlusionAccuracy` | 0-3 (1) | Hi-Z 精度 |

### GPUInstancingDrawer（静态工具类）

三个 `Draw` 重载：

| 重载 | 用途 | 剔除 |
|------|------|------|
| `Draw(InstanceData)` | 无剔除直接绘制 | 无 |
| `Draw(InstanceData, Camera, ComputeShader)` | 视锥剔除 | Frustum only |
| `Draw(InstanceData, Camera, ComputeShader, Matrix4x4, RenderTexture)` | 完整管线 | Frustum + Hi-Z |

`CheckAndInit(InstanceData)` — buffer 惰性初始化，已创建则 early-return。

### InstanceData（ScriptableObject）

每 terrain detail prototype 一个 asset：

| 字段 | 用途 |
|------|------|
| `mesh` / `material` | mesh proto 或 null（用 fallback） |
| `mats` (Matrix4x4[]) | 随机测试用（CPU 路径） |
| `densityMultiplier` | per-proto 密度缩放（× globalDensity） |
| `minWidth/maxWidth/minHeight/maxHeight` | blade 尺寸范围 |
| `noiseSpread` | 噪声散布 |
| `isVertexLit` | true=mesh proto, false=billboard blade |
| `capacity` | buffer 容量（instanceCount + 50% padding, min 64） |
| `instanceCount` | 每帧 readback 的实际实例数 |
| `matrixBuffer` | float4x4 StructuredBuffer（gen CS 写入） |
| `validMatrixBuffer` | float4x4 AppendStructuredBuffer（cull CS 输出） |
| `argsBuffer` | IndirectArguments (5×uint) |
| `genCounter` | uint[1]（gen CS 原子计数，CPU readback） |

## 渲染 Shader（GrassIndirect.shader）

`Custom/GrassIndirect`，`UniversalForwardOnly` Pass，`Cull Off`。

### 光照模型

风格化 PBR 光照，三个层级：

| 层级 | 法线来源 | 用途 |
|------|---------|------|
| 单根 blade | per-vertex normal map | 高光细部、透射 |
| 草丛 group | heightmap 重建地形法线 (`terrainNormalBlur`) | SSS diffuse、group specular、group transmission |
| 过渡 | `lerp(N, terrainNormalBlur, _SSSIntensity)` | 控制层级混合程度 |

### 高光 (Specular)

4 层 GGX lobe（`GrassMultiSpecular`），无几何遮蔽项 (G)：

- Primary: 3 层 lobe (`r`, `r*0.5`, `r*0.25`) × `_GrassWater` 驱动的 roughness
- Secondary: 1 层 lobe (`r*1.5`) 使用地形法线
- `distFade = saturate(dist / 150)` — 越远越强（近处抑制满屏高光，远处显示整体草地高光）
- 总权重 1.0（0.4 + 0.3 + 0.2 + 0.1）

### 次表面散射 (SSS)

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

风格化三色映射（暗部色 / 基础色 / 亮部色）× albedo，基于 `NdotL_sss`。

### 环境光

从大气散射 SH 系数采样（`_AtmoSHAr.._AtmoSHC`），零纹理读取。SH 系数由 `AtmosphereSkyboxLutFeature` 全局设置。

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
| `_SSSIntensity` | 0-2 | 0.5 | SSS/group 混合强度 |
| `_SSSRadius` | 0-1 | 0.3 | 地形法线模糊半径 |

## Vertex Shader（Billboard）

1. 从 `_ValidMatrixBuffer[SV_InstanceID]` 读取 `float4x4` 实例矩阵
2. Blade 顶点在本地空间缩放：`xz *= (1-tipFactor*0.8) * bladeWidth`，`y *= bladeHeight`
3. Billboard 矩阵：`right = normalize(cross(up, viewDir))`，`up, right, cross(right, up)`
4. Wind: `sin` 叠加 × `_WindParams.xy` × `tipFactor` × `_WindParams.z * 0.5`，仅影响 XZ
5. Shadow coord 在 vertex shader 计算（非 cascade 路径）

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

## 矩阵布局注意事项

**CPU → GPU 矩阵布局差异：**

- C# `Matrix4x4` 是行主序存储：`[m00 m01 m02 m03; m10 m11 m12 m13; ...]`
- HLSL `float4x4` 是列主序存储：`[col0 col1 col2 col3]`
- `ComputeBuffer.SetData(Matrix4x4[])` → HLSL StructuredBuffer 读取时发生隐式转置
- `GrassInstanceGen.compute` 中 `BuildTRS` 构建列主序 `float4x4`，写入前调用 `transpose()` 使 GPU 侧与 `SetData` 路径一致
- **位置提取**：transpose 后，world position 在 `_m03/_m13/_m23`（每行 column 3），不是 `_m30/_m31/_m32`。`StructuredBuffer<float4x4>` 按 row-major 存储，`_mXX` 的第一位是 row index。

## 备用 Shader（Grass.shader）

`Custom/Grass`，标准 Unity instancing 路径（`UNITY_MATRIX_M`、`unity_ObjectToWorld`），不依赖 StructuredBuffer。用于 `DrawMeshInstanced` / Terrain detail / 常规 MeshRenderer。简化版光照：三色 diffuse ramp + 单层 GGX specular + distance fade + ambient。

## TODO

- [ ] CSResetArgs / CSWriteArgs — 把 CPU 端 `SetData({0})` 和 `GetData` 读计数器挪进 compute shader（PreVis + PostVis），纯 GPU 管线，无 CPU 同步
- [ ] GPUI-style occlusionOffset 实际应用于 InstancCulling.compute（目前仅设置参数，未在 shader 中比较）
- [ ] Distance-based density fade + LOD
- [ ] 地形法线对齐 blade 生长方向（顺坡而非竖直）
- [ ] 噪声纹理替代纯随机（成片草疏密斑块）
- [ ] 双线性 heightmap 采样（替换 point sample）
