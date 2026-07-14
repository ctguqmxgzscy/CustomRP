# Atmospheric Scattering — Technical Documentation

> Unity 2022.3.62f3c1 · Custom RP (URP-based) · last updated 2026-07-14, [test controller](#14-test-controller)

---

## 1. Project Overview

Real-time physically-based atmospheric scattering for a custom URP render pipeline. Supports skybox rendering, aerial perspective (depth-aware atmospheric haze), terrain shadow occlusion, and ozone absorption for sunset reddening.

Reference implementations:
- [AKGWSB/RealTimeAtmosphere](https://github.com/AKGWSB/RealTimeAtmosphere)
- [UnitySkyAtmosphere](https://github.com/UnitySkyAtmosphere) (Bruneton 2017 + Hillaire 2020)
- Bruneton 2017: [Precomputed Atmospheric Scattering](https://github.com/ebruneton/precomputed_atmospheric_scattering)
- Hillaire 2020: [A Scalable and Production Ready Sky and Atmosphere Rendering Technique](https://sebh.github.io/publications/egsr2020.pdf)

---

## 2. Physical Model

### 2.1 Rayleigh Scattering

Molecular (Rayleigh) scattering from air molecules. Strongly wavelength-dependent: blue light scatters ~5.7× more than red.

| Property | Value |
|----------|-------|
| Coefficient at sea level | `(5.80, 13.5, 33.1) × 10⁻⁶ m⁻¹` (R, G, B) |
| Scale height H_R | `8,000 m` |
| Density profile | `exp(-h / H_R)` |
| Phase function | `3/(16π) · (1 + cos²θ)` |

```
β_R(λ) = 8π³(n²−1)² · F_K / (3Nλ⁴)
  n   = 1.00029 (air refractive index at STP)
  N   = 2.504×10²⁵ m⁻³ (molecular number density)
  F_K ≈ 1.06 (King depolarization factor)
```

### 2.2 Mie Scattering

Aerosol (Mie) scattering from large particles — haze, dust, water droplets. Wavelength-independent at visible scales.

| Property | Value |
|----------|-------|
| Coefficient at sea level | `(3.99, 3.99, 3.99) × 10⁻⁶ m⁻¹` |
| Scale height H_M | `1,200 m` |
| Absorption | `4.40 × 10⁻⁶ m⁻¹` (prevents f → 1 in Hillaire closed-form) |
| Density profile | `exp(-h / H_M)` |
| Phase function | Cornette-Shanks (reduces to Rayleigh when g = 0) |

### 2.3 Ozone Absorption (Chappuis Band)

Ozone is a pure absorber — it increases extinction without scattering. Concentrated in the stratosphere (15–35 km), it absorbs green-yellow light (500–650 nm). This is the primary mechanism for red sunsets.

| Property | Value |
|----------|-------|
| Density profile | Gaussian, peak at 25 km, σ = 8 km |
| Absorption at peak | `(0.05, 10.0, 0.01) × 10⁻⁶ m⁻¹` (R, G, B) |
| Vertical OD at zenith | ~0.06 (94% green transmission — barely visible) |
| Slant OD at horizon | ~1.2 (30% green transmission — deep red) |

```hlsl
density_ozone(h) = exp(−((h − 25000) / 8000)²)
```

The Gaussian profile naturally creates angle-dependent reddening: vertical path through the stratosphere is short → minimal absorption; slant path at sunset is 10–40× longer → green is heavily absorbed, leaving only red (blue already scattered away by Rayleigh at long paths).

### 2.4 Phase Functions

```
Rayleigh:  p(θ) = 3/(16π) · (1 + cos²θ)

Mie (Cornette-Shanks):
  p(θ) = 3/(8π) · (1−g²)/(2+g²) · (1+cos²θ) / (1+g²−2g·cosθ)^(3/2)

  g ∈ [−1, 1]:  g > 0 → forward-scattering (haze)
                 g = 0 → reduces to Rayleigh
```

### 2.5 Scattering vs. Extinction

| Function | Includes | Used by |
|----------|----------|---------|
| `ScatteringCoefAtHeight(h)` | Rayleigh + Mie scattering only | Multi-scattering LUT, albedo |
| `ExtinctionCoefAtHeight(h)` | Rayleigh + Mie scattering + Mie absorption + Ozone absorption | Multi-scattering LUT, ray-march σ_t |

---

## 3. Architecture — Two Pipelines

The project has two independent pipelines that share `_OpticalDepthLUT` but differ in parameter sourcing and rendering path.

### 3.1 Feature Pipeline (Skybox LUT)

```
AtmosphereSkyboxLutFeature (RenderFeature)
  │
  ├─ AtmosphereLutPass (BeforeRenderingOpaques)
  │   ├─ OpticalDepthLUT.compute     → _OpticalDepthLUT (256², ARGBHalf)
  │   │                                  R=τ_Rayleigh, G=τ_Mie, B=τ_Ozone
  │   ├─ MultiScatteringLut.compute  → _MultiScatteringLUT (32², ARGBFloat)
  │   ├─ SkyViewLut.compute          → _SkyViewLut (512×256, ARGBFloat)
  │   ├─ AerialPerspectiveLut.compute → _AerialPerspectiveLUT (128×72×64)
  │   │   (skipped if enableTerrainShadow — SHADOWMAP mode does per-pixel ray-march)
  │   └─ SetGlobalTexture / SetGlobalFloat / SetGlobalVector
  │
  ├─ AerialPerspectiveCompositePass (BeforeRenderingTransparents)
  │   ├─ Copy camera color to temp RT
  │   ├─ Keyword toggle: enableTerrainShadow → SHADOWMAP_ENABLED
  │   ├─ DrawProcedural full-screen composite
  │   │   ├─ FAST mode (default): sample _AerialPerspectiveLUT 3D LUT
  │   │   └─ SHADOWMAP mode: per-pixel IntegrateScatteredLuminance()
  │   │       ray-march + URP MainLightRealtimeShadow terrain occlusion
  │   └─ Output to camera color target
  │
  └─ DebugLutPass (AfterRenderingTransparents, optional)
      └─ Draw LUT overlay for debugging (_DebugMode 0–11)
```

Parameters sourced from `AtmosphereSettings` (ScriptableObject).

### 3.2 Mesh Lit Pipeline

```
AtmosphereLUTBinder (MonoBehaviour)
  └─ Custom/AtmosphereLit.shader
      └─ AtmosphereSkybox.shader (full ray-march skybox)
```

Parameters sourced from `Atmosphere Scattering.mat` material properties. **Do not modify Mesh Lit files when working on Feature side.**

### 3.3 Shared Layer

`OpticalDepthLUT.compute` is shared between both pipelines. Feature side passes `_AtmosphereRadius` (computed from `settings.atmosphereHeight + planetRadius`).

---

## 4. LUT Precomputation

All LUTs are regenerated every frame by the `AtmosphereLutPass`. Generation order matters — each LUT may depend on previously generated ones.

### 4.1 Optical Depth LUT

| Property | Value |
|----------|-------|
| Resolution | 256 × 256 |
| Format | ARGBHalf |
| Channels | R = τ_Rayleigh, G = τ_Mie, B = τ_Ozone |
| Kernel | `ComputeOpticalDepthLUT` (8×8 thread groups) |

**Axes:**
- U: cos(sun zenith), [-1, 1] → [0, 1] (sun position overhead → below horizon)
- V: height above ground, [0, atmosphereHeight]

**Algorithm:** For each (cosZenith, height) pair, ray-march from point P towards atmosphere exit along the sun direction. Integrate Rayleigh and Mie density (exponential profile) and ozone density (Gaussian profile at 25 km). Returns `1e20` for occluded rays (planet blocks the sun) — `exp(−β·1e20) ≈ 0` is bilinear-friendly.

**Transmittance:** Computed at runtime by `SampleTransmittanceLUT()` in `Atmosphere.hlsl`:
```hlsl
T = exp(−kRayleigh·τ_r − kMie·τ_m − kOzone·τ_o)
```

### 4.2 Multi-Scattering LUT

| Property | Value |
|----------|-------|
| Resolution | 32 × 32 |
| Format | ARGBFloat |
| Kernel | `ComputeMultiScatteringLut` (4×4 thread groups) |

Hillaire 2020 closed-form precomputation. For each (cosSunZenith, height) pair:
1. Cast 64 Fibonacci-sphere directions from point P
2. For each direction, ray-march 32 steps through atmosphere
3. Accumulate first-order scattered radiance → **G**
4. Accumulate scattering-to-extinction ratio → **f**
5. Closed-form geometric series: `MS = G / (1 − f)`

Uses `ExtinctionCoefAtHeight(h)` which includes ozone absorption — increasing extinction reduces `f`, reducing multi-scattering in the ozone layer (physically correct: ozone absorbs, so less light is available for higher-order scattering).

### 4.3 Sky View LUT

| Property | Value |
|----------|-------|
| Resolution | 512 × 256 (equirectangular) |
| Format | ARGBFloat |
| Kernel | `ComputeSkyViewLut` (8×8 thread groups) |

Equirectangular sky color map. Each pixel maps to a view direction; ray-marched from the camera position. Uses:
- `GetTransmittanceToSun()` — Bruneton 2017 horizon smoothstep (ozone included via LUT)
- `GetMultiScattering()` — Hillaire multi-scattering
- Custom tauPA accumulation including ozone

Non-linear latitude mapping (Hillaire 2020): compresses more texels near the horizon where scattering varies fastest, reducing banding at sunrise/sunset.

### 4.4 Aerial Perspective LUT

| Property | Value |
|----------|-------|
| Resolution | 128 × 72 × 64 (frustum voxel grid) |
| Format | ARGBHalf |
| Kernel | `ComputeAerialPerspectiveLUT` (8×8×8 thread groups) |

3D voxel grid covering the camera frustum. Each voxel integrates inscattered light from camera to its depth slice. Variable sample count: near slices = 2 samples, far slices = 128 samples. Squared depth distribution for near-field precision.

**Skipped when `enableTerrainShadow` is true** — the SHADOWMAP composite mode does per-pixel ray-march instead and doesn't need this LUT.

---

## 5. Aerial Perspective Composite

The composite pass runs at `BeforeRenderingTransparents` as a full-screen `DrawProcedural`. It copies the camera color target to a temporary RT, then blends atmospheric inscattering on top.

### 5.1 FAST Mode

Default mode. Samples the precomputed `_AerialPerspectiveLUT` 3D texture:
- Screen UV gives the frustum column
- Depth buffer gives the voxel slice index
- Squared-exponential depth mapping: dense near camera, sparse far away
- Near-field blend (< 0.5 slice): linear fade to prevent artifacts at very close range

### 5.2 SHADOWMAP Mode

Enabled when `enableTerrainShadow = true` in `AtmosphereSettings`. Replaces the 3D LUT lookup with per-pixel ray-marching:

```
For each pixel:
  1. Reconstruct world-space position from depth buffer
  2. Compute view direction from camera to pixel
  3. Convert camera position to planet-centric coordinates
  4. IntegrateScatteredLuminance():
     ├─ Ray-sphere intersect: atmosphere top + planet surface
     ├─ Clamp tMax to min(depth, atmosphere top)
     ├─ Variable sample count: lerp(4, 64, saturate(tMax * 0.01))
     ├─ Squared sample distribution (more samples near camera)
     ├─ At each step:
     │   ├─ Extinction: Rayleigh + Mie density + Ozone density
     │   ├─ Transmittance to sun: LUT lookup (includes ozone)
     │   ├─ Phase function: Rayleigh + Cornette-Shanks Mie
     │   ├─ Multi-scattering: Hillaire LUT lookup
     │   ├─ Planet occlusion: sphere intersection test
     │   ├─ Terrain occlusion: URP MainLightRealtimeShadow
     │   ├─ Multi-scattering attenuated by terrainShadow
     │   └─ Frostbite 2015 analytical integration
     └─ Returns (scatteredLuminance, transmittance)
  5. Composite: sceneColor.rgb + L * intensity, alpha = 1 − T
```

**Keyword dependencies:** `SHADOWMAP_ENABLED` (material keyword) + `_MAIN_LIGHT_SHADOWS` / `_MAIN_LIGHT_SHADOWS_CASCADE` (URP global keywords). Both required for shadow sampling to work.

**Shadow prerequisites:**
- URP Asset → Shadows → Main Light → enabled
- Directional Light → Shadow Type → Hard or Soft
- `_MAIN_LIGHT_SHADOWS` keyword enabled by URP `MainLightShadowCasterPass`

---

## 6. Coordinate Systems

### Planet-Centric vs. Unity World Space

```
Planet-centric:
  origin = planet center
  planet surface at radius = _PlanetRadius

Unity world space:
  origin = scene origin
  planet surface at Y = 0
  (planet model placed at scene origin)
```

**Conversion:**
```hlsl
// Unity → Planet-centric
planetPos = unityPos + float3(0, _PlanetRadius, 0);

// Planet-centric → Unity
unityPos = planetPos - float3(0, _PlanetRadius, 0);
```

Direction vectors are identical in both systems (pure Y-offset translation).

### Shadow Sampling Coordinate Flow

```
Ray-march sample point P (planet-centric)
  → unityPos = P − (0, _PlanetRadius, 0)
  → TransformWorldToShadowCoord(unityPos)
  → MainLightRealtimeShadow(shadowCoord)
```

---

## 7. Global Shader Properties

Set by `AtmosphereLutPass` every frame:

| Property | Type | Source |
|----------|------|--------|
| `_PlanetRadius` | float | settings.planetRadius |
| `_AtmosphereHeight` | float | settings.atmosphereHeight |
| `_ScaleHeight` | float | settings.scaleHeight |
| `_ScaleHeight` | float | settings.scaleHeight |
| `_MieScaleHeight` | float | settings.mieScaleHeight |
| `_MieG` | float | settings.mieG |
| `_SunDiskAngle` | float | settings.sunDiskAngle |
| `_SunIntensity` | float | settings.sunIntensity |
| `_SunDirection` | float3 | RenderSettings.sun (directional light). Set as global in AtmosphereLutPass |
| `_SunLightColor` | float4 | settings.sunLightColor |
| `_APIntensity` | float | settings.apIntensity |
| `_OpticalDepthLUT` | Texture2D | Compute shader output |
| `_MultiScatteringLUT` | Texture2D | Compute shader output |
| `_SkyViewLut` | Texture2D | Compute shader output |
| `_AerialPerspectiveLUT` | Texture3D | Compute shader output (FAST mode only) |

---

## 8. Key Parameters (AtmosphereSettings)

| Parameter | Default | Unit | Description |
|-----------|---------|------|-------------|
| `planetRadius` | 3.16 | world units | Planet radius (demo scale) |
| `atmosphereHeight` | 0.59 | world units | Atmosphere thickness |
| `scaleHeight` | 8000 | m | Rayleigh scale height |
| `mieScaleHeight` | 1200 | m | Mie/aerosol scale height |
| `mieG` | 0.8 | — | Mie asymmetry (−1 to 1) |
| `sunIntensity` | 100000 | — | Sun luminance multiplier |
| `sunDiskAngle` | 0.5 | degrees | Sun angular radius |
| `sunLightColor` | white | — | Sun tint |
| `enableTerrainShadow` | false | — | Enable per-pixel shadow ray-march |
| `apIntensity` | 1.0 | — | Aerial perspective strength |
| `viewSamples` | 64 | — | View ray samples for LUT generation |
| `lightSamples` | 32 | — | Light ray samples for optical depth LUT |

---

## 9. File Reference

### Core HLSL Libraries (`Assets/Shaders/ShaderLibrary/`)

| File | Purpose |
|------|---------|
| `MathHelper.hlsl` | `RaySphereIntersectNearest()`, `RaySphereIntersection()` |
| `Scattering.hlsl` | Physical constants (kRayleigh, kMie, kOzone), phase functions, `RayIntersect()`, `AtmosphericRayIntersect()`, `EvaluateTransmittance()`, `EvaluateInScattering()` |
| `Atmosphere.hlsl` | `SampleTransmittanceLUT()`, `GetTransmittanceToSun()`, `GetOzoneDensity()`, `ScatteringCoefAtHeight()`, `ExtinctionCoefAtHeight()`, `GetMultiScattering()`, view-direction ↔ UV mapping |
| `AerialPerspectiveRayMarch.hlsl` | `IntegrateScatteredLuminance()` — per-pixel ray-march with depth buffer clipping, shadowmap sampling, ozone extinction |
| `ScatteringUtils.hlsl` | Standalone `SampleTransmittanceLUT()` (Mesh Lit side — don't modify from Feature) |
| `AtmosphereLitInput.hlsl` | Mesh Lit input (don't modify from Feature) |

### Compute Shaders (`Assets/Features/Atmosphere/Shaders/`)

| File | Output | Format | Dispatch | Key Dependency |
|------|--------|--------|----------|----------------|
| `OpticalDepthLUT.compute` | `_OpticalDepthLUT` | 256² ARGBHalf | 32×32×1 (8×8) | None — generated first |
| `MultiScatteringLut.compute` | `_MultiScatteringLUT` | 32² ARGBFloat | 8×8×1 (4×4) | `_OpticalDepthLUT` |
| `SkyViewLut.compute` | `_SkyViewLut` | 512×256 ARGBFloat | 64×32×1 (8×8) | `_OpticalDepthLUT`, `_MultiScatteringLUT` |
| `AerialPerspectiveLut.compute` | `_AerialPerspectiveLUT` | 128×72×64 ARGBHalf 3D | 16×9×8 (8×8×8) | `_OpticalDepthLUT`, `_MultiScatteringLUT` |

#### OpticalDepthLUT.compute — details
- Kernel `ComputeOpticalDepthLUT`, thread group [8,8,1]
- Axes: U = cos(sun zenith) [-1,1], V = height [0, atmosphereHeight]
- Uses spherical symmetry: places point at (0, r, 0), constructs sun direction from cos zenith
- Output channels: R = τ_Rayleigh, G = τ_Mie, B = τ_Ozone
- Planet occlusion: stores 1e20 in all channels (exp(-β·1e20) ≈ 0, bilinear-friendly)
- Dependencies: `MathHelper.hlsl` only (ozone constants duplicated locally)

#### MultiScatteringLut.compute — details
- Kernel `ComputeMultiScatteringLut`, thread group [4,4,1]
- For each (cosSunZenith, height) pair: 64 Fibonacci-sphere directions × 32 steps
- Accumulates `G` (single-scattered radiance) and `f` (scatter-to-extinction ratio)
- Result: `G / (1−f)` with 1e-4 clamp to prevent division by zero
- Uses `ExtinctionCoefAtHeight()` which includes ozone — ozone reduces multi-scattering albedo

#### SkyViewLut.compute — details
- Kernel `ComputeSkyViewLut`, thread group [8,8,1]
- Equirectangular 512×256 with non-linear latitude (Hillaire 2020)
- Ray-marches `_ViewSamples` steps from camera through atmosphere
- Integrates: `EvaluateInScattering + GetTransmittanceToSun + GetMultiScattering`
- PA transmittance accumulated analytically (Rayleigh, Mie, Ozone density integrals)
- Output: RGB = accumulated radiance, A = 1.0

#### AerialPerspectiveLut.compute — details
- Kernel `ComputeAerialPerspectiveLUT`, thread group [8,8,8]
- Voxelizes camera frustum: XY = screen space, Z = depth (squared distribution)
- Variable sample count: `2 × (z+1)` steps (near=2, far=128)
- Includes terrain shadow sampling via `_MainLightShadowmapTexture` manual depth compare
- **Skipped** when `enableTerrainShadow = true` (composite shader does per-pixel ray-march)

### Fragment Shaders (`Assets/Features/Atmosphere/Shaders/`)

| File | Purpose |
|------|---------|
| `AerialPerspectiveComposite.shader` | Full-screen composite pass, dual mode (FAST / SHADOWMAP) |
| `DebugLutOverlay.shader` | Debug overlay for inspecting LUTs in-game |

### C# Runtime (`Assets/Features/Atmosphere/Runtime/`)

| File | Role |
|------|------|
| `AtmosphereSettings.cs` | ScriptableObject parameter asset |
| `AtmosphereSkyboxLutFeature.cs` | RenderFeature orchestrator: `AtmosphereLutPass` (LUT generation) + `AerialPerspectiveCompositePass` (composite) + `DebugLutPass` (debug overlay) |
| `AtmosphereLUTBinder.cs` | Mesh Lit — generates Optical Depth LUT independently (don't modify from Feature) |
| `AtmosphereSkyboxBinder.cs` | Mesh Lit — syncs parameters to skybox material (don't modify from Feature) |
| `OpticalDepthLUTGenerator.cs` | Mesh Lit — standalone LUT generator (don't modify from Feature) |

### Environment Shaders (`Assets/Shaders/Enviroment/`)

| File | Shader Name | Use |
|------|-------------|-----|
| `AtmosphereSkyboxLut.shader` | `Skybox/AtmosphericScatteringLUT` | Primary skybox — samples `_SkyViewLut`, adds analytic sun disk with Bruneton horizon smoothstep |
| `AtmosphereSkybox.shader` | `Skybox/AtmosphericScattering` | Standalone skybox — full per-pixel ray-march, no LUTs. Used by Mesh Lit pipeline |
| `AtomsphereLit.shader` | `Custom/AtmosphereLit` | PBR surface + atmospheric scattering via NormalExtrusion pass. Mesh Lit pipeline (don't modify from Feature) |

### Debug (`Assets/Features/Atmosphere/Shaders/`)

| File | Purpose |
|------|---------|
| `DebugLutOverlay.shader` | Full-screen overlay visualizing any LUT by `_DebugMode` (0–11). Supports ACES + Reinhard tone-mapping for HDR LUTs |

### Test Controller (`Assets/Scripts/Features/`)

| File | Purpose |
|------|---------|
| `AtmosphereTestController.cs` | Editor-time parameter tuning MonoBehaviour for rapid iteration |

---

## 10. Test Controller

`AtmosphereTestController.cs` — attach to the Camera GameObject for runtime testing.

**Controls:**
| Input | Action |
|-------|--------|
| Left-drag | Rotate sun (azimuth / elevation) |
| Right-drag | Rotate camera (pitch / yaw) |
| Scroll wheel | Camera altitude (proportional to current height) |
| WASD | Move camera (Self space, Shift = 5× speed) |
| Q / E | Move down / up |
| 1 / 2 / 3 / 4 | Sun presets (noon / sunrise / sunset / night) |
| Space | Reset camera to initial position/rotation |

**Setup:** Drag `AtmosphereSettings` asset and a Directional Light into the inspector fields. The controller does NOT modify the camera position on Start — it reads the initial position and treats it as the reference. Sun angles are read from the light's current rotation, preserving scene-authored values.

The controller uses pure `Transform.Rotate`/`Transform.Translate` operations — no spherical coordinate math. Movement is velocity-based, not position-override.

---

## 11. Dependency Graph

```
MathHelper.hlsl  (standalone, no deps)
  │
  ├─→ Scattering.hlsl  (includes MathHelper.hlsl)
  │     │
  │     └─→ Atmosphere.hlsl  (includes MathHelper, Scattering)
  │           │
  │           ├─→ OpticalDepthLUT.compute  (MathHelper ONLY, standalone)
  │           │     writes: _OpticalDepthLUT (256² ARGBHalf)
  │           │
  │           ├─→ MultiScatteringLut.compute  (includes Atmosphere.hlsl)
  │           │     reads:  _OpticalDepthLUT
  │           │     writes: _MultiScatteringLUT (32² ARGBFloat)
  │           │
  │           ├─→ SkyViewLut.compute  (includes Atmosphere.hlsl)
  │           │     reads:  _OpticalDepthLUT, _MultiScatteringLUT
  │           │     writes: _SkyViewLut (512×256 ARGBFloat)
  │           │
  │           ├─→ AerialPerspectiveLut.compute  (includes Atmosphere + URP)
  │           │     reads:  _OpticalDepthLUT, _MultiScatteringLUT
  │           │     writes: _AerialPerspectiveLUT (128×72×64 ARGBHalf)
  │           │
  │           └─→ AerialPerspectiveRayMarch.hlsl  (includes Atmosphere + URP Shadows)
  │                 │
  │                 └─→ AerialPerspectiveComposite.shader
  │                       reads:  _BlitTexture, _AerialPerspectiveLUT (FAST)
  │                               or ray-marches directly (SHADOWMAP)
  │
  └─→ AtmosphereSkyboxLut.shader  (standalone, reads _SkyViewLut)
```

### Runtime Generation Order

```
1. OpticalDepthLUT.compute     → _OpticalDepthLUT
2. MultiScatteringLut.compute  → _MultiScatteringLUT     (depends on 1)
3. SkyViewLut.compute          → _SkyViewLut              (depends on 1, 2)
4. AerialPerspectiveLut.compute → _AerialPerspectiveLUT   (depends on 1, 2; skipped in SHADOWMAP mode)
5. AerialPerspectiveComposite.shader                       (composites onto scene)
6. AtmosphereSkyboxLut.shader                              (renders skybox)
```

---

## 12. Design Decisions

| Decision | Rationale |
|----------|-----------|
| Optical depth LUT stores density integrals, not transmittance | Keeps LUT generic — scattering coefficients applied at runtime; ozone channel added without format change |
| Ozone uses Gaussian profile, not Bruneton piecewise linear | Simpler (2 params vs 6), visually equivalent, easier to tune |
| SHADOWMAP mode skips AP 3D LUT generation | Per-pixel ray-march replaces LUT lookup; saves 128×72×64 compute dispatch |
| Multi-scattering attenuated by terrain shadow (30% floor) | Physically: higher-order scattering partially fills shadow regions. Hard = 0 looks unnatural |
| Composite runs at `BeforeRenderingTransparents` | After opaque depth is available, before transparents are rendered |
| `SHADOWMAP_ENABLED` is a material keyword (not global) | Allows per-feature toggle without affecting other shaders |
| Ozone half-width duplicated in `OpticalDepthLUT.compute` | Compute shader doesn't include full Atmosphere/Scattering chain; minimal duplication avoids global dependency |

## 13. Known Limitations

- **Terrain shadow distance**: URP cascade max distance limits effective shadow range. Atmospheric sample points beyond cascade range always return `terrainShadow = 1.0`.
- **Multi-scattering in shadow**: Uses attenuated shadow (30% min) rather than path-traced occlusion. Accurate solution requires compute-intensive integration over the occluded solid angle.
- **No volumetric clouds**: Current implementation is clear-sky only.
- **Ozone tuning**: `kOzoneAbsorption.g` may need scene-dependent adjustment for desired sunset redness.
- **OpticalDepthLUT resolution at horizon**: 256² LUT may show banding at extreme sunset angles where optical depth changes rapidly. Mitigated by bilinear sampling.

## 14. References

1. Bruneton, E. & Neyret, F. (2008). Precomputed Atmospheric Scattering. *Computer Graphics Forum*, 27(4).
2. Bruneton, E. (2017). Precomputed Atmospheric Scattering: a New Implementation. [GitHub](https://github.com/ebruneton/precomputed_atmospheric_scattering)
3. Hillaire, S. (2020). A Scalable and Production Ready Sky and Atmosphere Rendering Technique. *EGSR 2020*. [PDF](https://sebh.github.io/publications/egsr2020.pdf)
4. Cornette, W.M. & Shanks, J.G. (1992). Physically reasonable analytic expression for the single-scattering phase function. *Applied Optics*, 31(16).
5. Zucconi, A. (2017). Atmospheric Scattering Series. [Blog](https://www.alanzucconi.com/2017/10/10/atmospheric-scattering-6/)
6. Frostbite (2015). Physically Based Unified Volumetric Rendering in Frostbite. [Slides](http://www.frostbite.com/2015/08/physically-based-unified-volumetric-rendering-in-frostbite/)
