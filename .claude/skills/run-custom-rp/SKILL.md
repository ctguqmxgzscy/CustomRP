---
name: run-custom-rp
description: Build, validate, and run the Custom RP (URP SSR) Unity project. Use for project validation, building, running in-editor, and taking screenshots.
---

# Custom RP — Unity URP SSR 项目

Unity 2022.3.62f3c1 项目，自定义渲染管线，核心是 HDRP → URP 的 Screen Space Reflection 迁移。路径均相对于项目根目录 `Custom RP/`。

## 无需 Unity 的验证

运行验证脚本检查项目完整性（shader kernel 一致性、cbuffer 对齐、已知问题）：

```bash
bash .claude/skills/run-custom-rp/validate.sh
```

此脚本检查：
- 所有 SSR 源文件是否存在
- C# `FindKernel()` 与 `.compute` `#pragma kernel` 声明是否一致
- `ShaderVariablesScreenSpaceReflection.cs` 与 `.cs.hlsl` 的 cbuffer 字段是否对齐
- P3/P4/P7 等已知未解决问题

## 在 Unity Editor 中运行

**前提**: Windows + Unity 2022.3.62f3c1 + GPU（DX11/Vulkan/Metal）

1. 打开项目：Unity Hub → Add → `D:\Unity Responsity\Custom RP\Custom RP`
2. 打开场景：`Assets/Scenes/SampleScene.unity`
3. 进入 Play Mode
4. Frame Debugger: Window → Analysis → Frame Debugger，搜索 `ScreenSpaceReflection` 查看 SSR pass

**前置条件**：
- URP Asset 中 **Opaque Downsampling** 必须禁用
- URP Asset 中 **Depth Texture** / **Opaque Texture** / **Depth Normals** 必须启用
- Volume Profile 中 `Lighting/Screen Space Reflection` 组件必须启用

## 运行测试

```
Window → General → Test Runner → Run All
```
（使用 `com.unity.test-framework@1.1.33`）

## 构建

```
File → Build Settings → Build
```
目标平台：StandaloneWindows

## 关键文件

| 用途 | 路径 |
|------|------|
| 项目文档 + 参数公式 + Bug 记录 | `Assets/CLAUDE.md` |
| SSR compute shader（6 kernel） | `Assets/Features/ScreenSpaceReflection/Runtime/Shaders/ScreenSpaceReflections.compute` |
| Color Pyramid 生成器 | `Assets/Features/ScreenSpaceReflection/Runtime/ColorPyramidGenerator.cs` |
| RenderFeature + RenderPass | `Assets/Scripts/Features/ScreenSpaceReflectionFeature.cs` |
| HDRP 参考源码 | `D:\Unity Responsity\HDRP Sample Project\Library\PackageCache\com.unity.render-pipelines.high-definition@14.0.12\` |

## 已知限制

- 当前环境无 Unity，无法真正启动项目或截图
- 3 个 Gaussian pyramid kernel (`KColorDownsample`, `KColorGaussianH`, `KColorGaussianV`) 已在 shader 中但 C# 端未激活（P3）
- PBR 模式缺少 3×3 block filter（P4）
- `SSR_TRACE_TOWARDS_EYE` 被禁用（P7）
