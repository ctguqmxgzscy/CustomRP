# Aerial Perspective LUT — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add frustum-voxelized 3D Aerial Perspective LUT generation + post-process composite to the existing atmosphere scattering system.

**Architecture:** One compute shader generates a 128×72×64 ARGBHalf 3D texture via squared-exponential Z-sliced frustum rays. A composite shader bilinearly samples it with bilateral Z weighting and blends onto opaque scene color in HDR before tonemapping. Both integrate into the existing `AtmosphereSkyboxLutFeature` as a new dispatch and a new ScriptableRenderPass.

**Tech Stack:** Unity 2022.3 URP, HLSL compute/fragment shaders, C# ScriptableRenderPass

## Global Constraints

- Camera always inside atmosphere (ground scene); no outer-atmosphere boundary cases
- 3D LUT: 128×72×64, `RenderTextureFormat.ARGBHalf`, `TextureDimension.Tex3D`
- Z slices: squared-exponential distribution `z(t) = zNear × (zFar/zNear)^(t²)`, zNear=camera.nearClipPlane, zFar=atmosphereRadius
- Threads: `[numthreads(8,8,1)]`, dispatch 16×9×1, each thread loops over all 64 Z slices
- Aerial LUT dispatch must happen AFTER OpticalDepthLUT and MultiScatteringLUT in the same CommandBuffer
- All LUT generation moves to `RenderPassEvent.BeforeRenderingOpaques` (250)
- Composite at `RenderPassEvent.BeforeRenderingTransparents` (450)
- Composite in HDR space (before tonemapping)
- Skip far-plane pixels (skybox) in composite
- TAA: composite uses unjittered UV
- First version: no CSM shadow in compute shader (SamplerComparisonState not available); scalar T luminance (not RGB T)

## ⚠️ Common Pitfalls

| # | Pitfall | Why it breaks | Where to check |
|---|---------|---------------|----------------|
| 1 | **renderPassEvent 没从 350 改为 250** | Aerial LUT dispatch 在 OpticalDepthLUT 生成之前执行，读到的是上一帧的过期 LUT | Task 3 Step 5: `renderPassEvent = RenderPassEvent.BeforeRenderingOpaques` |
| 2 | **RenderTexture 没设 dimension=Tex3D** | Unity 默认 dimension=Tex2D，3D texture 创建失败或维度错误 | Task 3 Step 3: `dimension = UnityEngine.Rendering.TextureDimension.Tex3D, volumeDepth = 64` |
| 3 | **GetMultiScattering 期望跨文件 include** | `.hlsl` 的 `#include` 在 `.compute` 里可工作，但函数定义在 SkyViewLut.compute 里（不在 .hlsl 中），无法复用 | Task 2 Step 2: 在 AerialPerspectiveLut.compute 里完整内联 |
| 4 | **CompositePass 缺 ConfigureInput** | 没有 `ConfigureInput(Color \| Depth)` 则 `SampleSceneColor`/`SampleSceneDepth` 读不到纹理 | Task 5 Step 1: `ConfigureInput(ScriptableRenderPassInput.Color \| ScriptableRenderPassInput.Depth)` |
| 5 | **_zFar 没显式设置** | 深度映射需要 zNear 和 zFar 完全一致；只靠 AtmosphereLutPass 的全局变量传递不可靠 | Task 5 Step 1: `cmd.SetGlobalFloat(s_zFarId, zFar)` |
| 6 | **天空像素二次叠加** | 合成 pass 对 skybox 像素叠加 aerial → 天空颜色 × T + inScatter，与 SkyViewLUT 的渲染结果叠加 | Task 4 Step 1 frag: `if (rawDepth >= 0.9999) return` |
| 7 | **Kernel dispatch 顺序错误** | OpticalDepth → MultiScatter → AerialLUT → SkyViewLUT，顺序错会导致读到未生成或过期的 LUT | Task 3 Step 6: Aerial dispatch 在 MultiScatter 之后、SkyView 之前 |

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `Assets/Shaders/ShaderLibrary/ScatteringUtils.hlsl` | Modify | Add squared-exponential depth mapping functions |
| `Assets/Features/Atmosphere/Shaders/AerialPerspectiveLut.compute` | Create | 3D LUT generation compute shader |
| `Assets/Features/Atmosphere/Runtime/AerialPerspectiveComposite.shader` | Create | Full-screen composite blit |
| `Assets/Features/Atmosphere/Runtime/AtmosphereSkyboxLutFeature.cs` | Modify | Move to @250, add Aerial LUT dispatch, add CompositePass @450 |

---

### Task 1: Add Depth Mapping Utilities

**Files:**
- Modify: `Assets/Shaders/ShaderLibrary/ScatteringUtils.hlsl`

**Interfaces:**
- Produces: `SquaredExpDepthToSliceUV(float linearDepth, float zNear, float zFar, int numSlices)` — converts linear depth to normalized slice UV (0..1)
- Produces: `SliceIndexToDepth(int slice, int numSlices, float zNear, float zFar)` — reverses the mapping for bilateral sampling

**Why:** Both the compute shader (generation) and composite shader (sampling) need identical squared-exponential mapping. Put it in the shared library to guarantee consistency.

- [ ] **Step 1: Add squared-exponential depth mapping functions**

Append to `Assets/Shaders/ShaderLibrary/ScatteringUtils.hlsl` before `#endif`:

```hlsl
// ── Squared-Exponential Depth Mapping ──────────────────────────────────
// Aerial Perspective LUT: distributes depth slices with squared-exponential
// bias — dense near camera (Mie scattering region), sparse far away.
//
// Forward:  z(t) = zNear × (zFar / zNear)^(t²),   t ∈ [0, 1]
// Inverse:  t(z) = sqrt(log(z / zNear) / log(zFar / zNear))

// Convert linear eye depth (world units) to normalized slice UV [0, 1]
float SquaredExpDepthToSliceUV(float linearDepth, float zNear, float zFar)
{
    // Guard: depths outside range clamp to [0, 1]
    float clampedDepth = clamp(linearDepth, zNear, zFar);
    float logRatio = log(zFar / zNear);
    if (logRatio <= 0.0) return 0.0;
    return sqrt(log(clampedDepth / zNear) / logRatio);
}

// Convert slice index [0, numSlices-1] back to linear eye depth
float SliceIndexToDepth(int slice, int numSlices, float zNear, float zFar)
{
    float t = float(slice) / float(max(numSlices - 1, 1));
    float logRatio = log(zFar / zNear);
    return zNear * exp(t * t * logRatio);
}
```

- [ ] **Step 2: Verify the functions compile**

Run: `bash .claude/skills/run-custom-rp/validate.sh`
Expected: no shader compilation errors

- [ ] **Step 3: Commit**

```bash
git add Assets/Shaders/ShaderLibrary/ScatteringUtils.hlsl
git commit -m "feat: add squared-exponential depth mapping for Aerial Perspective LUT"
```

---

### Task 2: Create AerialPerspectiveLut.compute

**Files:**
- Create: `Assets/Features/Atmosphere/Shaders/AerialPerspectiveLut.compute`

**Interfaces:**
- Consumes: `_OpticalDepthLUT` (Texture2D, global), `_MultiScatteringLUT` (Texture2D, global)
- Consumes: `SquaredExpDepthToSliceUV`, `SampleTransmittanceLUT`, `EvaluateInScattering` from ScatteringUtils/Scattering
- Consumes: `GetMultiScattering` — inline adaptation (same as SkyViewLut.compute lines 127-141)
- Produces: `_AerialPerspectiveLUT` (RWTexture3D<float4>, set as global)

- [ ] **Step 1: Create file with pragmas, includes, and macros**

```hlsl
// Aerial Perspective LUT Generator — Compute Shader
// Voxelizes the camera frustum into a 128×72×64 3D texture.
// Each voxel stores accumulated in-scattering radiance + transmittance
// from camera to that depth slice.
//
// Thread structure: [numthreads(8,8,1)], dispatch 16×9×1
// Each thread processes one XY column, looping over all 64 Z slices.
//
// Inputs:  _OpticalDepthLUT, _MultiScatteringLUT (set as global textures)
// Output:  _AerialPerspectiveLUT (RWTexture3D<float4>)

#pragma only_renderers d3d11 vulkan metal

#pragma kernel ComputeAerialPerspectiveLUT

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/TextureXR.hlsl"
#include "Assets/Shaders/ShaderLibrary/MathHelper.hlsl"
#include "Assets/Shaders/ShaderLibrary/Scattering.hlsl"
#include "Assets/Shaders/ShaderLibrary/ScatteringUtils.hlsl"

#define LUT_WIDTH  128
#define LUT_HEIGHT 72
#define NUM_SLICES 64

// ── Input textures (set as globals by AtmosphereLutPass) ──────────────
Texture2D<float2> _OpticalDepthLUT;
SamplerState sampler_OpticalDepthLUT;

Texture2D<float4> _MultiScatteringLUT;
SamplerState sampler_MultiScatteringLUT;

// ── Output ─────────────────────────────────────────────────────────────
RWTexture3D<float4> _AerialPerspectiveLUT;

// ── Atmosphere parameters ──────────────────────────────────────────────
float _PlanetRadius;
float _AtmosphereRadius;
float _ScaleHeight;
float _MieScaleHeight;
float _MieG;
float _SunIntensity;
float3 _SunDirection;

// ── Frustum / camera ───────────────────────────────────────────────────
float4x4 _CameraInvProj;
float4x4 _CameraToWorld;
float _CameraNearPlane;
```

- [ ] **Step 2: Add GetMultiScattering helper**

Inline the same function used in SkyViewLut.compute:

```hlsl
// ── Multi-Scattering LUT lookup ─────────────────────────────────────────
// Returns G_ALL * sigma_s.
// G_ALL already accounts for multi-bounce sun transmittance from all
// directions → only needs _SunIntensity, NOT T_sun.
float3 GetMultiScattering(float height, float cosSunZenith, float3 up)
{
    float densityR = exp(-height / _ScaleHeight);
    float densityM = exp(-height / _MieScaleHeight);
    float3 sigma_s = kRayleighScattering * densityR + kMieScattering * densityM;

    float2 uv = float2(cosSunZenith * 0.5 + 0.5, saturate(height / (_AtmosphereRadius - _PlanetRadius)));
    float3 G_ALL = _MultiScatteringLUT.SampleLevel(sampler_MultiScatteringLUT, uv, 0).rgb;

    return G_ALL * sigma_s;
}
```

- [ ] **Step 3: Add frustum ray reconstruction**

```hlsl
// ── Frustum Ray ─────────────────────────────────────────────────────────
// Reconstruct world-space view direction from thread ID via inverse
// projection matrix. Bilinearly interpolates across the frustum.
float3 FrustumViewDir(uint2 threadId)
{
    // UV in [0, 1], pixel-centered
    float2 uv = (float2(threadId) + 0.5) / float2(LUT_WIDTH, LUT_HEIGHT);

    // NDC far-plane position
    float4 clipPos = float4(uv.x * 2.0 - 1.0, uv.y * 2.0 - 1.0, 1.0, 1.0);
    float4 viewPos = mul(_CameraInvProj, clipPos);
    viewPos /= viewPos.w;

    // Transform to world-space direction
    float3 worldDir = normalize(mul((float3x3)_CameraToWorld, viewPos.xyz));
    return worldDir;
}
```

- [ ] **Step 4: Add slice-to-depth helper**

```hlsl
// ── Slice-to-Depth ──────────────────────────────────────────────────────
// Uses squared-exponential distribution: dense near, sparse far.
float SliceToDepth(int slice)
{
    return SliceIndexToDepth(slice, NUM_SLICES, _CameraNearPlane, _AtmosphereRadius);
}
```

- [ ] **Step 5: Add main kernel**

```hlsl
// ═════════════════════════════════════════════════════════════════════════
// Main kernel
// ═════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void ComputeAerialPerspectiveLUT(uint3 id : SV_DispatchThreadID)
{
    // Bounds check
    if (id.x >= LUT_WIDTH || id.y >= LUT_HEIGHT) return;

    // ── Per-column constants ──────────────────────────────────────────
    float3 viewDir  = FrustumViewDir(id.xy);
    float  cosToSun = dot(viewDir, _SunDirection); // view-sun angle, phase function
    float3 worldCam = float3(0, 0, 0); // camera at origin in local coords
    float3 center   = float3(0, 0, 0); // planet center (local)

    // ── Per-column state ──────────────────────────────────────────────
    float3 T_cam = float3(1.0, 1.0, 1.0);  // transmittance from camera along ray
    float3 scatterAccum = float3(0.0, 0.0, 0.0);

    // ── Ray-atmosphere intersection for full column ───────────────────
    float tEntry, tExit;
    if (!AtmosphericRayIntersect(worldCam, viewDir, center,
                                  _AtmosphereRadius, _PlanetRadius, tEntry, tExit))
    {
        // Ray misses atmosphere — fill all slices with zero
        for (int z = 0; z < NUM_SLICES; z++)
            _AerialPerspectiveLUT[uint3(id.xy, z)] = float4(0, 0, 0, 1);
        return;
    }

    float tPrev = max(tEntry, _CameraNearPlane);

    // ── Integrate over all Z slices ───────────────────────────────────
    for (int z = 0; z < NUM_SLICES; z++)
    {
        // Depth at this slice's midpoint
        float tSlice  = SliceToDepth(z);
        float tMid    = (tSlice + tPrev) * 0.5;
        float ds      = tSlice - tPrev;

        // Clamp to atmosphere bounds
        if (tPrev >= tExit) break;
        tSlice = min(tSlice, tExit);
        tMid   = min(tMid,   tExit);
        ds     = tSlice - tPrev;
        if (ds <= 0.0) { tPrev = tSlice; continue; }

        // ── Sample position ───────────────────────────────────────────
        float3 worldPos = worldCam + viewDir * tMid;
        float  height   = length(worldPos - center) - _PlanetRadius;
        height = max(height, 0.0);

        // ── Sun zenith angle at P ─────────────────────────────────────
        float cosSunZenith = dot(normalize(worldPos - center), _SunDirection);

        // ── T_sun: transmittance from P to sun ────────────────────────
        float3 T_sun = SampleTransmittanceLUT(height, cosSunZenith,
                            _AtmosphereRadius, _PlanetRadius,
                            kRayleighScattering, kMieScattering);

        // ── In-scattering source function J ───────────────────────────
        float3 J = EvaluateInScattering(height, _ScaleHeight, _MieScaleHeight,
                                        cosToSun, _MieG);

        // ── Multi-scattering ──────────────────────────────────────────
        float3 up = normalize(worldPos - center);
        float3 ms = GetMultiScattering(height, cosSunZenith, up);

        // ── Extinction and transmittance ──────────────────────────────
        float3 extinction = kRayleighScattering * exp(-height / _ScaleHeight)
                          + kMieScattering      * exp(-height / _MieScaleHeight);
        float3 T_step = exp(-extinction * ds);
        float3 T_half = T_cam * exp(-extinction * ds * 0.5);

        // ── Accumulate ────────────────────────────────────────────────
        float3 contrib = _SunIntensity * (T_sun * J + ms) * T_half * ds;
        scatterAccum += contrib;

        // ── Write voxel ───────────────────────────────────────────────
        _AerialPerspectiveLUT[uint3(id.xy, z)] = float4(scatterAccum, Luminance(T_half));

        // ── Advance ───────────────────────────────────────────────────
        T_cam *= T_step;
        tPrev = tSlice;
    }

    // Fill remaining slices beyond atmosphere exit with final values
    float3 finalVal = float3(0, 0, 0);
    float finalT   = 1.0;
    if (z_loop_done) // at exit
    {
        finalVal = scatterAccum;
        finalT   = Luminance(T_cam);
    }
    // Note: the loop may exit via `break` before filling all slices.
    // Fill remaining slices with the last accumulated values.
    // We track this via a flag or fill from the first atmosphered slice.
}
```

**Issue with the above:** The `break` leaves remaining slices unfilled. Fix:

```hlsl
    // ── Integrate over all Z slices ───────────────────────────────────
    bool exitedAtmosphere = false;
    float3 exitScatter = float3(0, 0, 0);
    float exitT = 1.0;

    for (int z = 0; z < NUM_SLICES; z++)
    {
        float tSlice  = SliceToDepth(z);
        float ds      = tSlice - tPrev;

        if (tPrev >= tExit)
        {
            // Past atmosphere — fill with last values
            _AerialPerspectiveLUT[uint3(id.xy, z)] = float4(exitScatter, exitT);
            tPrev = tSlice;
            continue;
        }

        // Clamp to exit
        float tClamped = min(tSlice, tExit);
        float dsClamped = tClamped - tPrev;
        float tMid = (tPrev + tClamped) * 0.5;

        float3 worldPos = worldCam + viewDir * tMid;
        float  height   = max(length(worldPos - center) - _PlanetRadius, 0.0);
        float  cosSunZenith = dot(normalize(worldPos - center), _SunDirection);

        float3 T_sun = SampleTransmittanceLUT(height, cosSunZenith,
                            _AtmosphereRadius, _PlanetRadius,
                            kRayleighScattering, kMieScattering);
        float3 J = EvaluateInScattering(height, _ScaleHeight, _MieScaleHeight,
                                        cosToSun, _MieG);
        float3 up = normalize(worldPos - center);
        float3 ms = GetMultiScattering(height, cosSunZenith, up);

        float3 extinction = kRayleighScattering * exp(-height / _ScaleHeight)
                          + kMieScattering      * exp(-height / _MieScaleHeight);
        float3 T_step = exp(-extinction * dsClamped);
        float3 T_half = T_cam * exp(-extinction * dsClamped * 0.5);

        float3 contrib = _SunIntensity * (T_sun * J + ms) * T_half * dsClamped;
        scatterAccum += contrib;

        _AerialPerspectiveLUT[uint3(id.xy, z)] = float4(scatterAccum, Luminance(T_half));

        exitScatter = scatterAccum;
        exitT       = Luminance(T_half);

        T_cam *= T_step;
        tPrev = tSlice;
    }
```

- [ ] **Step 6: Verify compilation**

Run: `bash .claude/skills/run-custom-rp/validate.sh`
Expected: no shader compilation errors for AerialPerspectiveLut.compute

- [ ] **Step 7: Commit**

```bash
git add Assets/Features/Atmosphere/Shaders/AerialPerspectiveLut.compute
git commit -m "feat: add AerialPerspectiveLut.compute with frustum voxelization"
```

---

### Task 3: Integrate Aerial LUT Dispatch into AtmosphereSkyboxLutFeature

**Files:**
- Modify: `Assets/Features/Atmosphere/Runtime/AtmosphereSkyboxLutFeature.cs`

**Interfaces:**
- Consumes: `_AerialPerspectiveLUT` (new RenderTexture 3D), `_AerialPerspectiveLutCompute` (new serialized ComputeShader field)
- Produces: `_AerialPerspectiveLUT` as global texture for composite pass

**What changes:**
1. Move `renderPassEvent` from `BeforeRenderingSkybox` (350) to `BeforeRenderingOpaques` (250)
2. Add serialized field for Aerial LUT compute shader
3. Add `RenderTexture` creation for 3D texture (128×72×64, ARGBHalf, volumeDepth=64)
4. Add kernel dispatch after MultiScatteringLUT, before SkyViewLUT in Execute()
5. Set `_AerialPerspectiveLUT` as global texture
6. Add new inner class `AerialPerspectiveCompositePass` for the composite draw

- [ ] **Step 1: Add serialized fields**

In `AtmosphereSkyboxLutFeature` (outer class, near other `[Header]` fields):

```csharp
[Header("Aerial Perspective")]
[SerializeField] private ComputeShader m_AerialPerspectiveLutCompute;
[SerializeField] private Material m_AerialPerspectiveCompositeMaterial;
```

- [ ] **Step 2: Add Aerial LUT constants and cached property IDs**

In `AtmosphereLutPass`:

```csharp
// Aerial Perspective LUT
private const int k_AerialLutWidth = 128;
private const int k_AerialLutHeight = 72;
private const int k_AerialLutDepth = 64;

private static readonly int s_AerialPerspectiveLutId = Shader.PropertyToID("_AerialPerspectiveLUT");
private static readonly int s_AerialLutWidthId = Shader.PropertyToID("_AerialLutWidth");
private static readonly int s_AerialLutHeightId = Shader.PropertyToID("_AerialLutHeight");
private static readonly int s_AerialLutDepthId = Shader.PropertyToID("_AerialLutDepth");
private static readonly int s_CameraInvProjId = Shader.PropertyToID("_CameraInvProj");
private static readonly int s_CameraToWorldId = Shader.PropertyToID("_CameraToWorld");
private static readonly int s_CameraNearPlaneId = Shader.PropertyToID("_CameraNearPlane");

private RenderTexture m_AerialPerspectiveLut;
private int m_AerialPerspectiveKernel;
```

- [ ] **Step 3: Add RenderTexture creation in InitializeIfNeeded()**

After the existing MultiScatteringLut creation:

```csharp
if (m_AerialPerspectiveLutCompute != null)
{
    m_AerialPerspectiveKernel = m_AerialPerspectiveLutCompute.FindKernel("ComputeAerialPerspectiveLUT");
    m_AerialPerspectiveLut = new RenderTexture(k_AerialLutWidth, k_AerialLutHeight, 0,
        RenderTextureFormat.ARGBHalf)
    {
        enableRandomWrite = true,
        filterMode = FilterMode.Bilinear,
        wrapMode = TextureWrapMode.Clamp,
        dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
        volumeDepth = k_AerialLutDepth,
        name = "AerialPerspectiveLut"
    };
    m_AerialPerspectiveLut.Create();
}
```

- [ ] **Step 4: Add cleanup in Dispose()**

```csharp
if (m_AerialPerspectiveLut != null)
{
    m_AerialPerspectiveLut.Release();
    DestroyImmediate(m_AerialPerspectiveLut);
}
```

- [ ] **Step 5: Pass compute shader reference from Feature to Pass**

In `Create()`:

```csharp
m_Pass = new AtmosphereLutPass
{
    renderPassEvent = RenderPassEvent.BeforeRenderingOpaques, // changed from BeforeRenderingSkybox
    // ... existing fields ...
    aerialPerspectiveLutCompute = m_AerialPerspectiveLutCompute,
};
```

In `AtmosphereLutPass`, add public field:

```csharp
public ComputeShader aerialPerspectiveLutCompute;
```

- [ ] **Step 6: Add Aerial LUT dispatch in Execute()**

After MultiScatteringLUT dispatch (after line ~238), before SkyViewLUT generation:

```csharp
// ══════════════════════════════════════════════════════════════
// 3. Generate Aerial Perspective LUT (depends on 1 + 2)
// ══════════════════════════════════════════════════════════════
if (aerialPerspectiveLutCompute != null)
{
    var ap = aerialPerspectiveLutCompute;

    // Atmosphere parameters (reuse from settings)
    cmd.SetComputeFloatParam(ap, s_PlanetRadiusId, planetRadius);
    cmd.SetComputeFloatParam(ap, s_AtmosphereRadiusId, atmosphereRadius);
    cmd.SetComputeFloatParam(ap, s_ScaleHeightId, scaleHeight);
    cmd.SetComputeFloatParam(ap, s_MieScaleHeightId, mieScaleHeight);
    cmd.SetComputeFloatParam(ap, s_MieGId, mieG);
    cmd.SetComputeFloatParam(ap, s_SunIntensityId, sunIntensity);

    // Sun direction
    cmd.SetComputeVectorParam(ap, s_SunDirectionId, sunDir);

    // Camera matrices
    var cam = renderingData.cameraData.camera;
    Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false);
    Matrix4x4 view = cam.worldToCameraMatrix;
    cmd.SetComputeMatrixParam(ap, s_CameraInvProjId, (proj * view).inverse);
    cmd.SetComputeMatrixParam(ap, s_CameraToWorldId, cam.cameraToWorldMatrix);
    cmd.SetComputeFloatParam(ap, s_CameraNearPlaneId, cam.nearClipPlane);

    // Input textures (already set as globals from steps 1+2)
    cmd.SetComputeTextureParam(ap, m_AerialPerspectiveKernel, s_OpticalDepthLutId, m_OpticalDepthLut);
    cmd.SetComputeTextureParam(ap, m_AerialPerspectiveKernel, s_MultiScatteringLutId, m_MultiScatteringLut);

    // Output
    cmd.SetComputeTextureParam(ap, m_AerialPerspectiveKernel, s_AerialPerspectiveLutId, m_AerialPerspectiveLut);

    var apTgX = Mathf.CeilToInt(k_AerialLutWidth / 8f);
    var apTgY = Mathf.CeilToInt(k_AerialLutHeight / 8f);
    cmd.DispatchCompute(ap, m_AerialPerspectiveKernel, apTgX, apTgY, 1);
}
```

- [ ] **Step 7: Set global texture**

In the "Set global textures" section (currently after SkyViewLut generation, around line 264):

```csharp
if (m_AerialPerspectiveLut != null)
{
    cmd.SetGlobalTexture(s_AerialPerspectiveLutId, m_AerialPerspectiveLut);
    cmd.SetGlobalInt(s_AerialLutWidthId, k_AerialLutWidth);
    cmd.SetGlobalInt(s_AerialLutHeightId, k_AerialLutHeight);
    cmd.SetGlobalInt(s_AerialLutDepthId, k_AerialLutDepth);
}
```

- [ ] **Step 8: Verify compilation**

Run: `bash .claude/skills/run-custom-rp/validate.sh`
Expected: no C# compilation errors for AtmosphereSkyboxLutFeature.cs

- [ ] **Step 9: Commit**

```bash
git add Assets/Features/Atmosphere/Runtime/AtmosphereSkyboxLutFeature.cs
git commit -m "feat: add Aerial Perspective LUT dispatch to AtmosphereSkyboxLutFeature"
```

---

### Task 4: Create AerialPerspectiveComposite.shader

**Files:**
- Create: `Assets/Features/Atmosphere/Runtime/Shaders/AerialPerspectiveComposite.shader`

**Interfaces:**
- Consumes: `_AerialPerspectiveLUT` (Texture3D, global), `_CameraColorTexture`, `_CameraDepthTexture`
- Consumes: `SquaredExpDepthToSliceUV`, `SliceIndexToDepth` from ScatteringUtils.hlsl
- Produces: Full-screen composite output to camera color target

- [ ] **Step 1: Create shader file with full-screen pass**

```hlsl
Shader "Hidden/Custom RP/AerialPerspectiveComposite"
{
    // Full-screen composite pass:
    //   Samples _AerialPerspectiveLUT 3D texture with bilateral Z weighting
    //   Blends: srcColor × T + inScatter  (HDR, before tonemapping)
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "AerialPerspectiveComposite"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Assets/Shaders/ShaderLibrary/ScatteringUtils.hlsl"

            #define NUM_SLICES 64
            #define BILATERAL_SIGMA 0.5 // higher = more sensitive to depth edges

            // ── Aerial Perspective 3D LUT ─────────────────────────────
            TEXTURE3D(_AerialPerspectiveLUT);
            SAMPLER(sampler_AerialPerspectiveLUT);

            int _AerialLutWidth;
            int _AerialLutHeight;
            int _AerialLutDepth;

            float _zNear;
            float _zFar;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // ── Full-screen triangle vertex ───────────────────────────
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            // ── Bilateral Z-weighted 3D LUT sample ────────────────────
            float4 BilateralSampleAerialLUT(float2 screenUV, float sliceUV, float linearDepth)
            {
                float slice = sliceUV * (NUM_SLICES - 1);
                int s0 = floor(slice);
                int s1 = min(s0 + 1, NUM_SLICES - 1);

                float d0 = SliceIndexToDepth(s0, NUM_SLICES, _zNear, _zFar);
                float d1 = SliceIndexToDepth(s1, NUM_SLICES, _zNear, _zFar);

                // Per-slice distance weights (bilateral)
                float w0 = exp(-abs(linearDepth - d0) * BILATERAL_SIGMA);
                float w1 = exp(-abs(linearDepth - d1) * BILATERAL_SIGMA);
                float total = w0 + w1 + 1e-6;

                // XY: hardware bilinear sampling
                // Z:  manual bilateral weighting
                float4 a0 = SAMPLE_TEXTURE3D_LOD(
                    _AerialPerspectiveLUT, sampler_AerialPerspectiveLUT,
                    float3(screenUV, (s0 + 0.5) / NUM_SLICES), 0);
                float4 a1 = SAMPLE_TEXTURE3D_LOD(
                    _AerialPerspectiveLUT, sampler_AerialPerspectiveLUT,
                    float3(screenUV, (s1 + 0.5) / NUM_SLICES), 0);

                return (a0 * w0 + a1 * w1) / total;
            }

            // ── Fragment ──────────────────────────────────────────────
            float4 Frag(Varyings input) : SV_Target
            {
                float2 screenUV = input.uv;

                // Skip skybox pixels (far plane depth)
                float rawDepth = SampleSceneDepth(screenUV);
                if (rawDepth >= 0.9999)
                    return float4(SampleSceneColor(screenUV).rgb, 1.0);

                // Linear depth → slice UV (must match LUT generation)
                float linearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float sliceUV = SquaredExpDepthToSliceUV(linearDepth, _zNear, _zFar);

                // Sample LUT
                float4 aerial = BilateralSampleAerialLUT(screenUV, sliceUV, linearDepth);

                float3 inScatter = aerial.rgb;
                float3 T = aerial.aaa; // scalar luminance transmittance

                // Composite in HDR (before tonemapping)
                float3 srcColor = SampleSceneColor(screenUV).rgb;
                float3 result = srcColor * T + inScatter;

                return float4(result, 1.0);
            }
            ENDHLSL
        }
    }
}
```

- [ ] **Step 2: Verify shader compilation**

Run: `bash .claude/skills/run-custom-rp/validate.sh`
Expected: no errors for AerialPerspectiveComposite.shader

- [ ] **Step 3: Commit**

```bash
git add Assets/Features/Atmosphere/Runtime/Shaders/AerialPerspectiveComposite.shader
git commit -m "feat: add AerialPerspectiveComposite.shader with bilateral sampling"
```

---

### Task 5: Add AerialPerspectiveCompositePass to AtmosphereSkyboxLutFeature

**Files:**
- Modify: `Assets/Features/Atmosphere/Runtime/AtmosphereSkyboxLutFeature.cs`

**Interfaces:**
- Consumes: `m_AerialPerspectiveCompositeMaterial` (serialized field from Task 3 Step 1)
- Produces: Composite draw call at `RenderPassEvent.BeforeRenderingTransparents` (450)

- [ ] **Step 1: Add CompositePass inner class**

Add a new private inner class after `AtmosphereLutPass`:

```csharp
private class AerialPerspectiveCompositePass : ScriptableRenderPass
{
    private static readonly int s_zNearId = Shader.PropertyToID("_zNear");
    private static readonly int s_zFarId = Shader.PropertyToID("_zFar");

    public Material compositeMaterial;

    public AerialPerspectiveCompositePass()
    {
        renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        ConfigureInput(ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth);
    }

    [Obsolete]
    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (compositeMaterial == null) return;

        var cmd = CommandBufferPool.Get("Aerial Perspective Composite");
        var cam = renderingData.cameraData.camera;

        // Must match LUT generation: zNear = camera near, zFar = atmosphere outer radius
        cmd.SetGlobalFloat(s_zNearId, cam.nearClipPlane);
        // _AtmosphereRadius is already set by AtmosphereLutPass; pass it explicitly
        // (read back from global or use the settings value — AtmosphereLutPass sets it
        //  before this runs, so Shader.GetGlobalFloat is safe)
        float zFar = Shader.GetGlobalFloat("_AtmosphereRadius");
        cmd.SetGlobalFloat(s_zFarId, zFar);

        cmd.DrawProcedural(Matrix4x4.identity, compositeMaterial, 0,
            MeshTopology.Triangles, 3, 1);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
}
```

- [ ] **Step 2: Instantiate and register the pass in Feature**

In `AtmosphereSkyboxLutFeature`:

```csharp
private AerialPerspectiveCompositePass m_CompositePass;

public override void Create()
{
    m_Pass = new AtmosphereLutPass { /* ... */ };

    m_CompositePass = new AerialPerspectiveCompositePass
    {
        compositeMaterial = m_AerialPerspectiveCompositeMaterial
    };
}

public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
{
    if (m_Settings == null || m_OpticalDepthLutCompute == null || m_SkyViewLutCompute == null)
        return;

    m_Pass.settings = m_Settings;
    renderer.EnqueuePass(m_Pass);

    // Composite pass runs after opaques
    if (m_CompositePass.compositeMaterial != null)
        renderer.EnqueuePass(m_CompositePass);
}
```

- [ ] **Step 3: Verify compilation**

Run: `bash .claude/skills/run-custom-rp/validate.sh`
Expected: no errors

- [ ] **Step 4: Commit**

```bash
git add Assets/Features/Atmosphere/Runtime/AtmosphereSkyboxLutFeature.cs
git commit -m "feat: add AerialPerspectiveCompositePass for post-process blend"
```

---

### Task 6: Validation

**Files:**
- No new files; verify everything works end-to-end.

- [ ] **Step 1: Full project validation**

Run: `bash .claude/skills/run-custom-rp/validate.sh`
Expected: all shaders and C# scripts compile without errors

- [ ] **Step 2: Assign references in Unity Editor**

In the Forward Renderer asset:
1. Add `AtmosphereSkyboxLutFeature` (if not already present)
2. Assign `AerialPerspectiveLut.compute` to the "Aerial Perspective Lut Compute" field
3. Create a material from `AerialPerspectiveComposite.shader` and assign to "Aerial Perspective Composite Material"
4. Ensure `Atmosphere Settings` is assigned

- [ ] **Step 3: Visual check**

Run the game view:
- Objects at distance should show blue haze (Rayleigh scatter)
- Near objects should be clear
- The skybox should NOT have double fogging
- No depth bleeding artifacts at silhouette edges

- [ ] **Step 4: Commit final touches**

```bash
git commit -am "chore: finalize Aerial Perspective LUT integration"
```

---

## Verification

After all tasks complete, verify with:

```bash
bash .claude/skills/run-custom-rp/validate.sh
```

Expected: zero compilation errors across all shaders and C# scripts.
