# GPU 遮挡剔除 — 技术文档

## 概述

GPU-driven 遮挡剔除系统（静态 + 动态）：Frustum Culling + Hi-Z Occlusion Culling 在 GPU 上并行执行，结果通过 `AsyncGPUReadback` 回读到 CPU 控制 `Renderer.enabled`。

仅支持 reversed-Z 平台：D3D11 / Vulkan / Metal。

## 文件结构

```
Assets/Features/
├── OcclusionCulling/Runtime/
│   ├── OcclusionCullingSystem.cs         # 单体管理器：注册、GPU dispatch、回读
│   ├── OccludeeDesc.cs                   # 被遮挡物数据结构（bounds + renderer）
│   ├── HiZDepthRenderer.cs               # URP 管线挂钩：调度 culling + 生成 Hi-Z
│   └── Shaders/
│       ├── StaticOcclusionCulling.compute # 剔除 compute shader（frustum + Hi-Z）
│       └── OcclusionCullingInput.hlsl     # GPU 端数据结构定义
└── HiZDepth/Runtime/
    ├── HiZDepthGenerator.cs              # Hi-Z 金字塔生成器
    └── Shaders/
        └── HiZDepth.compute              # Hi-Z 金字塔 compute（clear + copy + reduce）
```

## 静态 vs 动态

GPU shader 同一套，区别仅在 CPU 侧 bounds 刷新策略：

| | 静态 | 动态 |
|---|---|---|
| 注册 API | `Register(r)` 或 `ScanScene()` | `RegisterDynamic(r)` 或 `ScanScene(onlyStatic: false)` |
| bounds 上传 | 注册时一次，之后不变 | **每帧** `renderer.bounds` → GPU buffer |
| 运行期 CPU 开销 | 零 | `RefreshDynamicBounds()` 遍历动态 indices |
| Unregister | 自动清理 | 自动清理（含动态标记） |

动静混排无额外成本——同一 buffer 按 slot 混存，一次 dispatch 全测。

### HLOD 集成

HLOD 切换与遮挡剔除互补：HLOD 管距离，遮挡剔除管视线。

```
HLOD cluster 激活 → Register(cluster)         ← 1 个 occludee 代理数百子物体
  子节点              → Unregister(子物体)       ← 从 buffer 注销，不测
HLOD cluster 休眠  → Unregister(cluster)
  子节点              → Register/RegisterDynamic  ← 恢复 per-object culling
```

动态 chunk 加载后第一帧 bounds 可能过期（上一帧缓存的），per-frame 刷新自然修正，无需额外处理。

### 用法示例

```csharp
var sys = OcclusionCullingSystem.Instance;

// 扫全场（动静混合）
sys.ScanScene(onlyStatic: false);

// 运行时注册动态物体
sys.RegisterDynamic(movingEnemy.GetComponent<Renderer>());

// 卸载
sys.Unregister(movingEnemy.GetComponent<Renderer>());
```

## 数据流

```
帧 N 开始 (beginCameraRendering)
  │
  ├─ DispatchCulling(camera, cmd) ───────────────────────┐
  │   • 如有动态物体: RefreshDynamicBounds()               │
  │     (遍历 m_DynamicIndices, 读 renderer.bounds)        │
  │   • 上传 occludee bounds 到 GPU Buffer                │
  │   • Set GPU 投影矩阵 (_CullingVP)                     │
  │   • Set Hi-Z (帧 N-1 的深度，首帧跳过)                │
  │   • Dispatch compute → frustum + Hi-Z culling         │
  │   • AsyncGPUReadback.Request (非阻塞)                 │
  │                                                       │
  └─ 渲染帧 N (遮挡剔除不影响当前帧渲染，下帧生效)         │
                                                          │
帧 N 结束 (endCameraRendering)                            │
  │                                                       │
  ├─ HiZDepthGenerator.Generate(cmd, camera) ─────────    │
  │   • KClear: 清 mip 0                                  │
  │   • KDepthCopy: _CameraDepthTexture → Hi-Z mip 0      │
  │   • KDepthReduce: N 次 2×2 downsampling → mip 1..N   │
  │   • hiZHandle → OcclusionCullingSystem.hiZHandle      │
  │                                                       │
  ├─ OnReadbackComplete (帧 N-1 的结果到达) ◄───────────  │
  │   • AsyncGPUReadback 回调                              │
  │   • visibility → m_OccludedFrameCounter + hideDelay   │
  │   • Renderer.enabled = visible || frameCounter < delay│
```

**一帧延迟**：当前帧的 culling 用上一帧的 Hi-Z，当前帧渲染结果生成 Hi-Z 给下一帧用。静止时无影响，相机快速移动时可能短暂误显——由 `hideDelayFrames` 缓解。

## 深度约定

整条管线统一使用 **GPU 投影矩阵**（`GL.GetGPUProjectionMatrix(proj, false)`）+ **reversed-Z**（near=1 / far=0）。

| 层 | 约定 | 说明 |
|----|------|------|
| `_CullingVP` | `GetGPUProjectionMatrix(proj, false) * worldToCamera` | z∈[0,w]，reversed-Z，无 Y 翻转 |
| Hi-Z 金字塔 | near=1 / far=0 | `KDepthCopy` 归一化所有平台后，`KDepthReduce` 无条件 `min` |
| `instanceNearestDepth` | `max(corner.z / corner.w)` | reversed-Z 下值越大越近 |
| `sceneFarthestDepth` | `min(HiZ 多点采样)` | 值越小越远 |
| 遮挡判定 | `instanceNearestDepth < sceneFarthestDepth - bias` | 最近点比最远遮挡更远 → 剔除 |

**为何用 GPU 矩阵而非 GL 矩阵**：GPU 矩阵下 `z/w` 直接等于深度缓冲存的数，与 Hi-Z 同值域同方向，无需 `1-x` 翻转和 `*0.5+0.5` 重映射。`renderIntoTexture=false` 因为 UV 采样 RT 内容（v=0 恒定在底部），不需要 Y 翻转。

## Frustum Culling

将世界空间 AABB 的 8 角点变换到 clip space，对每个轴测试是否所有角点都在视锥体外：

- x/y：`[-w, w]`（GL 标准）
- z：`[0, w]`（GPU 投影，nrear=w / far=0）

`FRUSTUM_OFFSET` 提供保守余量。

## Hi-Z Occlusion Culling

### 金字塔生成

1. **KClear**：mip 0 清零，防残留值
2. **KDepthCopy**：将 `_CameraDepthTexture`（视口尺寸）复制到 PoT 纹理 mip 0。`_ReverseZ==0` 平台翻转成 near=1/far=0（实际 `#pragma only_renderers` 已排除这些平台，保留兼容）
3. **KDepthReduce**：2×2 downsample，每 tile 无条件 `min`（保留最远深度，保守剔除）。同 SSR 的 `DepthPyramid` 取 `max`（保留最近深度，保守跳跃）方向相反——用途不同

### 采样与判定

1. 物体 clip-space 包围盒 → UV rect + `instanceNearestDepth` / `instanceFarthestDepth`
2. **相机进包围盒守卫**：`instanceNearestDepth > 1.0`（穿近面）或 `instanceFarthestDepth < 0.0` → 标记可见，不进遮挡判定
3. 根据 rect texel 尺寸选 mip level：`ceil(log2(max(texelW, texelH) / (accuracy * 2)))`
4. 多点采样 Hi-Z → `min`（最远遮挡深度）
   - Accuracy=1: 5 samples（中心 + 四角）
   - Accuracy=2: 9 samples（+ 四边中点）
   - Accuracy=3: 17 samples（+ 8 个附加点）
5. `instanceNearestDepth < sceneFarthestDepth - OCCLUSION_DEPTH_BIAS` → 遮挡

## 可调参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `frustumOffset` | 0.01 | 视锥测试保守余量 |
| `occlusionDepthBias` | 0.005 | 深度比较偏置（越大越不容易剔） |
| `rectPadding` | 0.005 | UV rect 缩边（防边界采样到 rect 外） |
| `occlusionAccuracy` | 2 | 采样点数（1/2/3） |
| `hideDelayFrames` | 3 | 被遮挡后等 N 帧再隐藏（防闪烁） |
| `debugMode` | Full | Full / FrustumOnly / OcclusionOnly |

## 已知限制

| 限制 | 影响 | 对策 |
|------|------|------|
| Hi-Z 一帧延迟 | 相机快速移动时短暂误显 | hideDelayFrames |
| 回读延迟 | 结果晚 1-2 帧生效 | 同上 |
| 视图外区域无 Hi-Z 数据 | 相机旋转后新进入视野的物体可能一度误显 | hideDelayFrames |
| 仅 reversed-Z 平台 | GL/GLES 不支持 | `#pragma only_renderers d3d11 vulkan metal` |
| 所有物体既是 occluder 也是 occludee | 大量小物体降低 Hi-Z 精度 | 参见下方 TODO |

## TODO

- **occluder/occludee 分离**：大物体（墙、地面）写深度到 Hi-Z，小物体（碎片、道具）只被剔除不贡献遮挡。需新增 per-registration 标记、Hi-Z 生成时过滤
- **间接绘制支持**：大批量 instance（草、植被）不能走 `AsyncGPUReadback → CPU → Renderer.enabled` 路径。需在 culling compute 后新增 GPU 侧输出：读 `_OccludeeVisibility` → 写 `DrawCommand.count` 或 append visible index。关键约束：indirect kernel 必须与 culling 在同一条 CommandBuffer 内同帧执行，`context.Submit()` 一次提交
