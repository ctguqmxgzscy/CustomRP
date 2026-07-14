# 禁止猜测式修改 Shader 代码

## 规则

修改 `.shader` / `.hlsl` / `.compute` 文件前，**必须先诊断验证**，确认根因后再改。禁止以下行为：

- ❌ 改一行 → 测试 → 不行 → 再改一行 → 循环试错
- ❌ 不验证假设就直接改代码
- ❌ 连续多个版本的猜测式修改

## 工作流

1. **诊断** — 用 `script-execute` 采样像素值、打印中间变量，收集数据
2. **定位根因** — 用数据确认问题所在，不是一个猜测
3. **改一处** — 基于定位的根因改代码
4. **验证** — 确认修改产生预期效果

## 反例（本次犯的错误）

修改 `AtmosphereSkyboxLut.shader` 的 view direction 计算时，连试了 4 个版本：
1. `normalize(positionWS - _WorldSpaceCameraPos)` — 猜的，全黑
2. `normalize(positionWS)` + Y-flip — 猜的，用户说不对
3. `normalize(positionOS)` — 猜的，不跟相机旋转
4. clip-space 重建 — 猜的，还是不对

正确的做法：第一步就应该采样 LUT 和像素，发现 LUT 本身正确、天空盒方向映射和相机俯仰角吻合，不需要改代码。
