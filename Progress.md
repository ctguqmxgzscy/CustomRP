# Progress — 2026-07-13

## 项目背景
Unity 2022.3.62f3c1 Custom RP 项目，大气散射 LUT + Skybox 管线。

## 当前任务
AP LUT 亮斑 bug 修复完成。

## 状态

### 已完成
- [x] **Sky-View LUT 非线性纬度（Hillaire 2020）** — 保留
- [x] **AP LUT 亮斑 bug** — 根因确认并修复
- [x] 矩阵差异分析完成

### 当前代码状态
- `Atmosphere.hlsl` / `AtmosphereSkyboxLut.shader`: 非线性纬度已应用
- `AerialPerspectiveLut.compute`: **FrustumViewDir 已修复**

### 待验证
- [ ] Play Mode 验证最终效果

## AP LUT 亮斑 bug — 根因

`unity_CameraInvProjection` 和 `UNITY_MATRIX_I_VP` / `UNITY_MATRIX_I_P` 在 Compute Shader 中使用的 projection 不同：

| 矩阵 | Compute Shader 中的 Y-flip |
|------|---------------------------|
| `unity_CameraInvProjection` | 无（原始 projection 的逆，OpenGL 约定） |
| `UNITY_MATRIX_I_P` / `UNITY_MATRIX_I_VP` | 有（GPU-adjusted projection 的逆，D3D Y-flip） |

虽然 URP 的 `SetCameraMatrices` 给 `unity_CameraInvProjection` 赋的是 GPU-adjusted 版本，但 Compute Shader dispatch 时 Unity 引擎会用 Camera 原始 projection 重新绑定这个 uniform，导致拿到无 Y-flip 的版本。

**效果等价关系**：
- `unity_CameraInvProjection` + `clipPos.y = 1.0 - clipPos.y` ≈ `UNITY_MATRIX_I_VP`（无手动翻转）
- `UNITY_MATRIX_I_P` + `clipPos.y = 1.0 - clipPos.y` = 双重翻转 → Y 又颠倒了

## 关键文件
- `AerialPerspectiveLut.compute` — FrustumViewDir
- `Atmosphere.hlsl` — ViewDirToUV/UVToViewDir（非线性纬度）
- `AtmosphereSkyboxLut.shader` — 本地 ViewDirToUV（非线性纬度）
- `AtmosphereSkyboxLutFeature.cs` — AP LUT 生成 + composite pass

## 下一步
1. Play Mode 验证效果

## 参考
- 完整项目上下文：`Assets/CLAUDE.md`
