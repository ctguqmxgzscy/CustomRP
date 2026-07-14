# Multi-Scattering Implementation Plan

基于 Hillaire 2020 闭合公式 + RealTimeAtmosphere 参考项目。

## 核心公式

```
G   = 球面积分单次散射光（64方向 × 32步）
f   = 球面积分散射率
MS  = G × 1/(1 - f)   ← 等价于所有高阶散射之和
```

## 涉及文件

| 操作 | 文件 |
|------|------|
| **新建** | `Assets/Features/Atmosphere/Shaders/MultiScatteringLut.compute` |
| **修改** | `Assets/Features/Atmosphere/Shaders/SkyViewLut.compute` |
| **修改** | `Assets/Features/Atmosphere/Runtime/AtmosphereSkyboxLutFeature.cs` |

---

## 1. MultiScatteringLut.compute（新建）

### 规格
- 输出：`_MultiScatteringLUT`，32×32，ARGBFloat，enableRandomWrite
- 参数维度：X = cos(太阳天顶角) [-1,1]，Y = 高度 [0, AtmosphereHeight]
- thread group: [4,4,1]，dispatch: ceil(32/4) × ceil(32/4) × 1 = [8,8,1]

### 输入参数（通过 cmd.SetCompute*）
```
_PlanetRadius       (float)
_AtmosphereHeight   (float)
_ScaleHeight        (float)  — Rayleigh
_MieScaleHeight     (float)  — Mie
_MieG               (float)
_SunLightColor      (float3)
_OpticalDepthLUT    (Texture2D<float2>)  — 已有的 Transmittance LUT
```

### 算法（每个 texel）

```
1. uv → (mu_s = cos(sun zenith), height)
   samplePoint = (0, planetRadius + height, 0)
   sunDir = (sin(acos(mu_s)), mu_s, 0)

2. 初始化 G = 0, f = 0

3. for 64个均匀球面方向 viewDir:
     a. rayIntersect → distance:
        - 大气外边界: RaySphereIntersection(P, viewDir, 0, planetRadius + atmosphereHeight)
        - 行星表面:   RaySphereIntersection(P, viewDir, 0, planetRadius)
        - 如果 hit planet → distance = min(atmosphere, planet)
        - 如果 miss atmosphere → 跳过此方向
     
     b. ds = distance / 32, opticalDepth = 0
     
     c. for 32步 along viewDir:
        p += viewDir * ds
        h = length(p) - planetRadius
        
        sigma_s = RayleighCoeff(h) + MieCoeff(h)  // 散射系数
        sigma_a = MieAbsorption(h)                 // 吸收 (4.4e-6 常量)
        sigma_t = sigma_s + sigma_a                // 总消光
        
        T_sun  = SampleTransmittanceLUT(h, dot(normalize(p), sunDir))
        s      = EvaluateInScattering(h, cos(viewDir, sunDir))  // 含相位函数
        T_view = exp(-opticalDepth)
        
        G += T_sun × s × T_view × (1/(4π)) × ds
        f += T_view × sigma_s × (1/(4π)) × ds
        
        opticalDepth += sigma_t × ds  // ← 用 sigma_t（含吸收），不是 sigma_s

4. G *= (4π / 64)   // 球面方向数归一化
   f *= (4π / 64)

5. result = G × 1.0 / max(1.0 - f, 1e-4)   // 安全上限防除零
```

### 关键常量
```hlsl
static const float3 kRayleighScattering = float3(5.8e-6, 1.35e-5, 3.31e-5);
static const float3 kMieScattering      = float3(3.99e-6, 3.99e-6, 3.99e-6);
static const float3 kMieAbsorption      = float3(4.4e-6, 4.4e-6, 4.4e-6);

float3 EvaluateInScattering(float height, float cosTheta, float mieG)
{
    float densityR = exp(-height / _ScaleHeight);
    float densityM = exp(-height / _MieScaleHeight);
    float3 rayleigh = kRayleighScattering * RayleighPhase(cosTheta) * densityR;
    float3 mie      = kMieScattering      * MiePhase(mieG, cosTheta)  * densityM;
    return rayleigh + mie;
}
```

### 64个球面采样方向
硬编码常量数组（从 RealTimeAtmosphere 参考项目复制），确保球面均匀覆盖。

### 注意事项
- **MieAbsorption 必须加**：没有吸收时 sigma_s = sigma_t，f_ms 在近地面水平方向会趋近 1，导致 `1/(1-f)` 爆炸。加 MieAbsorption(4.4e-6) 后 sigma_t > sigma_s，f 始终 < 1。
- 多散积分内部的光学深度用 `sigma_t`（散射+吸收），但 SkyViewLut 的视线光学深度仍用 `sigma_s`（保持与现有 Transmittance LUT 一致）——这个不一致在视觉上不可见。

---

## 2. SkyViewLut.compute（修改）

### 新增输入
```hlsl
Texture2D<float4> _MultiScatteringLUT;
SamplerState sampler_MultiScatteringLUT;   // 或复用已有的
_SunLightColor (float3)
```

### 新增函数
```hlsl
float3 GetMultiScattering(float3 p, float3 sunDir)
{
    float h = length(p) - _PlanetRadius;
    float3 up = normalize(p);
    float cosSunZenith = dot(up, sunDir);
    
    // 散射系数（不带相位）
    float densityR = exp(-h / _ScaleHeight);
    float densityM = exp(-h / _MieScaleHeight);
    float3 sigma_s = kRayleighScattering * densityR + kMieScattering * densityM;
    
    float2 uv = float2(cosSunZenith * 0.5 + 0.5, saturate(h / _AtmosphereHeight));
    float3 G_ALL = _MultiScatteringLUT.SampleLevel(sampler_MultiScatteringLUT, uv, 0).rgb;
    
    return G_ALL * sigma_s;  // 查表值 × 散射系数 = 朝相机贡献
}
```

### 修改积分循环

在现有单次散射行 **之后** 追加：
```hlsl
// 单次散射（现有）
scatter += _SunIntensity * _SunLightColor * J * T_PA * T_CP * ds;

// 多重散射（新增）
float3 msContrib = GetMultiScattering(P, sunDir);
scatter += _SunIntensity * _SunLightColor * msContrib * T_PA * ds;
```

注意：多散项**不乘 T_CP**（sun transmittance），因为 LUT 里已经包含了各方向到达该点的光的总和（含各自的 sun transmittance）。

### _SunLightColor
现有代码只用了 `_SunIntensity`（float），缺少颜色分量。改成 `_SunIntensity * _SunLightColor`。

---

## 3. AtmosphereSkyboxLutFeature.cs（修改）

### 新增字段
```csharp
[Header("Compute Shaders")]
[SerializeField] private ComputeShader m_MultiScatteringLutCompute;

// In AtmosphereLutPass:
private const int k_MultiScatteringLutSize = 32;
private static readonly int s_MultiScatteringLutId = Shader.PropertyToID("_MultiScatteringLUT");
private RenderTexture m_MultiScatteringLut;
private int m_MultiScatteringKernel;
```

### InitializeIfNeeded() 追加
```csharp
m_MultiScatteringKernel = multiScatteringLutCompute.FindKernel("ComputeMultiScatteringLut");

m_MultiScatteringLut = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGBFloat)
{
    enableRandomWrite = true,
    filterMode = FilterMode.Bilinear,
    wrapMode = TextureWrapMode.Clamp,
    name = "MultiScatteringLut"
};
m_MultiScatteringLut.Create();
```

### Execute() 插入新步骤（OpticalDepth 之后、SkyView 之前）

```csharp
// ══════════════════════════════════════════════════════════════
// 1.5. Generate Multi-Scattering LUT
// ══════════════════════════════════════════════════════════════
var mulSC = multiScatteringLutCompute;
cmd.SetComputeFloatParam(mulSC, s_PlanetRadiusId, planetRadius);
cmd.SetComputeFloatParam(mulSC, s_AtmosphereHeightId, atmosphereHeight);
cmd.SetComputeFloatParam(mulSC, s_ScaleHeightId, scaleHeight);
cmd.SetComputeFloatParam(mulSC, s_MieScaleHeightId, mieScaleHeight);
cmd.SetComputeFloatParam(mulSC, s_MieGId, mieG);
cmd.SetComputeVectorParam(mulSC, s_SunLightColorId, (Vector4)sunLightColor);
cmd.SetComputeTextureParam(mulSC, m_MultiScatteringKernel,
    "_OpticalDepthLUT", m_OpticalDepthLut);   // 复用已有 LUT
cmd.SetComputeTextureParam(mulSC, m_MultiScatteringKernel,
    s_MultiScatteringLutId, m_MultiScatteringLut);

cmd.DispatchCompute(mulSC, m_MultiScatteringKernel, 8, 8, 1);  // 32/4 = 8
```

### SkyViewLut dispatch 追加参数
```csharp
// 新加这两行
cmd.SetComputeTextureParam(skyViewLutCompute, m_SkyViewKernel,
    s_MultiScatteringLutId, m_MultiScatteringLut);
cmd.SetComputeVectorParam(skyViewLutCompute, s_SunLightColorId, (Vector4)sunLightColor);
```

### SetGlobalTexture 追加
```csharp
cmd.SetGlobalTexture(s_MultiScatteringLutId, m_MultiScatteringLut);
```

### Dispose() 追加
```csharp
if (m_MultiScatteringLut != null)
{
    m_MultiScatteringLut.Release();
    DestroyImmediate(m_MultiScatteringLut);
}
```

---

## 生成顺序（关键）

```
1. OpticalDepthLUT  (256×256)   ← 已有，不变
2. MultiScatteringLUT (32×32)   ← 新增，依赖 OpticalDepthLUT
3. SkyViewLUT       (512×256)   ← 修改，依赖上面两张
```

必须按 1→2→3 顺序，因为 MultiScattering 依赖 Transmittance，SkyView 依赖 MultiScattering。

---

## 潜在问题与对策

| 问题 | 对策 |
|------|------|
| `1/(1-f)` 除零爆炸 | 加 MieAbsorption(4.4e-6) 到多散积分的消光系数里，使 f<1 |
| MieAbsorption 与现有 Transmittance LUT 不一致 | 多散积分内部用含吸收的 sigma_t，SkyView 视线积分仍用现有 sigma_s。视觉差异不可见 |
| 64个硬编码球面方向维护困难 | 参考项目已验证过，直接复制 |
| 多散 LUT 在低分辨率(32×32)下质量 | 多散本身极其低频，32×32 足够（HDRP 和参考项目都用这个尺寸） |
| Compute shader 采样纹理需要 SamplerState | Unity compute shader 中 `SamplerState` 声明 + `.SampleLevel()` 可用；若不支持则用 `Texture2D<float4>[uint2]` 的 `[]` 操作符 |

---

## 验证方法

```bash
bash .claude/skills/run-custom-rp/validate.sh
```

预期变化：
- 地平线附近天空变亮（多散在光程长的方向贡献更大）
- 天顶变化很小（光程短，多散贡献少）
- 日落时效果最明显（光程最长）
