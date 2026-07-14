# Session State Snapshot — 2026-07-13

## Changes to restore (3 files)

### 1. Atmosphere.hlsl — Non-linear latitude (Hillaire 2020)
ViewDirToUV and UVToViewDir with `v = 0.5 + 0.5·sign(l)·√(|l|/(π/2))`

### 2. AtmosphereSkyboxLut.shader — Non-linear latitude
Local ViewDirToUV synced with Atmosphere.hlsl

### 3. AerialPerspectiveLut.compute — FrustumViewDir Y-flip
- `(1.0 - uv.y)` in clipPos construction
- Uses `UNITY_MATRIX_I_P` / `UNITY_MATRIX_I_V` (not unity_Camera*)
- No explicit float4x4 declarations
