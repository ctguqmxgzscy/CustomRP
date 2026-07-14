#ifndef ATMOSPHERE_INCLUDED
#define ATMOSPHERE_INCLUDED

// Atmosphere.hlsl — Shared atmospheric scattering functions for LUT compute shaders.
//
// Include chain:  MathHelper.hlsl → Scattering.hlsl → Atmosphere.hlsl
//
// Expected globals (declare in caller before #include):
//   float _PlanetRadius, _AtmosphereHeight, _ScaleHeight, _MieScaleHeight, _MieG
//   float _SunDiskAngle                               (for GetTransmittanceToSun)
//   Texture2D<float4> _OpticalDepthLUT                (for LUT sampling)
//   SamplerState      sampler_OpticalDepthLUT
//   Texture2D<float4> _MultiScatteringLUT              (optional, for GetMultiScattering)
//   SamplerState      sampler_MultiScatteringLUT        (optional)

// ── PI guard — Core.hlsl defines PI, standalone callers may not ─────────
#ifndef PI
#define PI 3.14159265359
#endif

#include "MathHelper.hlsl"
#include "Scattering.hlsl"

// ── Constants ──────────────────────────────────────────────────────────
#define GOLDEN_ANGLE 2.399963229728653  // PI * (3 - sqrt(5))

#define NUM_SLICES 64
#define PLANET_RADIUS_OFFSET 0.001f
#define AP_METERS_PER_SLICE 200.0

// ── Atmosphere parameters (declare BEFORE Atmosphere.hlsl) ──────────────
float _PlanetRadius;
float _ScaleHeight;
float _MieScaleHeight;
float _MieG;
float _SunIntensity;
float _SunDiskAngle;
float3 _SunDirection;
float3 _SunLightColor;
float _AtmosphereHeight;
float _ViewSamples;
float _CameraHeight; // distance from planet center to camera minus planet radius
// ═════════════════════════════════════════════════════════════════════════
// Transmittance LUT — convenience wrapper using global parameters
//
// Reads _OpticalDepthLUT with axes (cosSunZenith, height / _AtmosphereHeight).
// Uses kRayleighScattering & kMieScattering from Scattering.hlsl.
// ═════════════════════════════════════════════════════════════════════════
// ═════════════════════════════════════════════════════════════════════════
// Ozone Density — Gaussian profile centered at ~25 km
// ═════════════════════════════════════════════════════════════════════════
float GetOzoneDensity(float height)
{
    float z = (height - OZONE_CENTER_HEIGHT) / OZONE_HALF_WIDTH;
    return exp(-z * z);
}

float3 SampleTransmittanceLUT(float height, float cosSunZenith)
{
    float maxHeight = _AtmosphereHeight;
    float2 uv = float2(saturate(cosSunZenith * 0.5 + 0.5),
                       saturate(height / maxHeight));
    float4 tau = _OpticalDepthLUT.SampleLevel(sampler_OpticalDepthLUT, uv, 0);
    return exp(-kRayleighScattering * tau.r - kMieScattering * tau.g
               - kOzoneAbsorption * tau.b);
}

// ═════════════════════════════════════════════════════════════════════════
// Transmittance to Sun — Bruneton 2017 analytical horizon smoothstep
//
// Attenuates transmittance when the sun disc is below the geometric
// horizon at a given altitude. Uses the sun's angular radius (_SunDiskAngle
// in degrees) to define the transition band.
//
// Reference: Bruneton 2017 functions.glsl, GetTransmittanceToSun()
//
// Guarded by ATM_SUN_DISK — define before #include if _SunDiskAngle is declared.
// ═════════════════════════════════════════════════════════════════════════
#ifdef ATM_SUN_DISK
float3 GetTransmittanceToSun(float height, float cosSunZenith)
{
    float r = _PlanetRadius + height;
    float sinThetaH = _PlanetRadius / r;
    float cosThetaH = -sqrt(max(1.0 - sinThetaH * sinThetaH, 0.0));
    float sunAngRad = _SunDiskAngle * (PI / 180.0);

    float visibility = smoothstep(-sinThetaH * sunAngRad,
                                  sinThetaH * sunAngRad,
                                  cosSunZenith - cosThetaH);

    return SampleTransmittanceLUT(height, cosSunZenith) * visibility;
}
#endif

// ═════════════════════════════════════════════════════════════════════════
// In-scattering source function — convenience overload using global params
//
// Calls Scattering.hlsl's parameterized EvaluateInScattering with the
// global _ScaleHeight and _MieScaleHeight.
// ═════════════════════════════════════════════════════════════════════════
float3 EvaluateInScattering(float height, float cosTheta, float mieG)
{
    return EvaluateInScattering(height, _ScaleHeight, _MieScaleHeight, cosTheta, mieG);
}

// ═════════════════════════════════════════════════════════════════════════
// Scattering / Extinction coefficient at a given altitude (no phase)
//
// Used by multi-scattering LUT generation and higher-order scattering.
// Extinction includes Mie absorption (4.4e-6 m⁻¹) to prevent f → 1
// divergence in the Hillaire closed-form.
// ═════════════════════════════════════════════════════════════════════════
float3 ScatteringCoefAtHeight(float height)
{
    float densityR = exp(-height / _ScaleHeight);
    float densityM = exp(-height / _MieScaleHeight);
    return kRayleighScattering * densityR + kMieScattering * densityM;
}

float3 ExtinctionCoefAtHeight(float height)
{
    float densityR = exp(-height / _ScaleHeight);
    float densityM = exp(-height / _MieScaleHeight);
    static const float3 kMieAbsorption = float3(4.4e-6, 4.4e-6, 4.4e-6);
    return kRayleighScattering * densityR + (kMieScattering + kMieAbsorption) * densityM
           + kOzoneAbsorption * GetOzoneDensity(height);
}

// ═════════════════════════════════════════════════════════════════════════
// Multi-Scattering (Hillaire 2020 closed-form) — LUT lookup
//
// Multi-scattering LUT stores G / (1-f), the total diffuse ambient
// radiance at point P from all higher-order scattering paths.
// Multiplying by sigma_s recovers the isotropic scattered radiance.
//
// Overloads:
//   GetMultiScattering(float3 P, float3 sunDir)    — compute height & zenith from position
//   GetMultiScattering(float height, float cosSunZenith)  — caller already has these
//
// Guarded by ATM_MULTI_SCATTERING — define before #include if
// _MultiScatteringLUT (readable Texture2D) + sampler_MultiScatteringLUT are declared.
// ═════════════════════════════════════════════════════════════════════════
#ifdef ATM_MULTI_SCATTERING
float3 GetMultiScattering(float height, float cosSunZenith)
{
    float3 sigma_s = ScatteringCoefAtHeight(height);
    float2 uv = float2(cosSunZenith * 0.5 + 0.5, saturate(height / _AtmosphereHeight));
    float3 G = _MultiScatteringLUT.SampleLevel(sampler_MultiScatteringLUT, uv, 0).rgb;
    return G * sigma_s;
}

float3 GetMultiScattering(float3 P, float3 sunDir)
{
    float h = length(P) - _PlanetRadius;
    float cosSunZenith = dot(normalize(P), sunDir);
    return GetMultiScattering(h, cosSunZenith);
}
#endif

// ═════════════════════════════════════════════════════════════════════════
// Fibonacci sphere — quasi-uniform spherical direction sampling
// ═════════════════════════════════════════════════════════════════════════
float3 FibonacciSphereDir(int index, int total)
{
    float y = 1.0 - (2.0 * float(index) + 1.0) / float(total);
    float radiusAtY = sqrt(max(1.0 - y * y, 0.0));
    float theta = float(index) * GOLDEN_ANGLE;
    return float3(cos(theta) * radiusAtY, y, sin(theta) * radiusAtY);
}

// ═════════════════════════════════════════════════════════════════════════
// Direction ↔ Equirectangular UV (latitude-longitude sky dome mapping)
//
// UV: [0,1]².  U = azimuth [-π, +π], V = elevation [π/2, -π/2].
// +Y = zenith (top), -Y = nadir (bottom).
//
// Non-linear latitude (Hillaire 2020):
//   v = 0.5 + 0.5·sign(l)·√(|l| / (π/2))
// Compresses more texels near the horizon where scattering varies fastest,
// reducing banding at sunset/sunrise.
// ═════════════════════════════════════════════════════════════════════════
float2 ViewDirToUV(float3 v)
{
    float latitude = asin(v.y); // [-π/2, π/2]
    float n = latitude / (PI * 0.5); // normalize to [-1, 1]
    return float2(atan2(v.z, v.x) / (2.0 * PI) + 0.5,
                  sign(n) * sqrt(abs(n)) * 0.5 + 0.5 // non-linear stretch
    );
}

float3 UVToViewDir(float2 uv)
{
    float n = (uv.y - 0.5) * 2.0; // [-1, 1], remapped
    float latitude = sign(n) * n * n * (PI * 0.5); // inverse sqrt → radians
    float theta = PI * 0.5 - latitude; // angle from zenith
    float phi = (uv.x * 2.0 - 1.0) * PI;
    return float3(sin(theta) * cos(phi), cos(theta), sin(theta) * sin(phi));
}

#endif // ATMOSPHERE_INCLUDED
