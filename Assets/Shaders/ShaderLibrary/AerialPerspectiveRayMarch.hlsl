#ifndef AERIAL_PERSPECTIVE_RAY_MARCH_INCLUDED
#define AERIAL_PERSPECTIVE_RAY_MARCH_INCLUDED

// Per-pixel aerial perspective ray-march for fragment shaders.
// Replaces the 3D LUT lookup when SHADOWMAP_ENABLED is defined.
//
// Uses URP native shadow sampling (TransformWorldToShadowCoord +
// MainLightRealtimeShadow) — only works in fragment shaders.

// ── LUT textures (must be declared before Atmosphere.hlsl) ─────────────
Texture2D<float4> _OpticalDepthLUT;
SamplerState sampler_OpticalDepthLUT;
Texture2D<float4> _MultiScatteringLUT;
SamplerState sampler_MultiScatteringLUT;

// Atmosphere.hlsl declares its own globals (_PlanetRadius, etc.) and
// includes Scattering.hlsl (phase functions, ray-sphere intersect).
#define ATM_SUN_DISK
#define ATM_MULTI_SCATTERING
#include "Assets/Shaders/ShaderLibrary/Atmosphere.hlsl"

// URP shadow sampling requires CommonMaterial.hlsl for LerpWhiteTo
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

// ═════════════════════════════════════════════════════════════════════════
// IntegrateScatteredLuminance
//
// Ray-marches from worldCam along viewDir, integrating inscattered light
// and computing transmittance.
//
//   tMax         — max distance from camera (from depth buffer or atmo top)
//   enableShadow — sample URP shadowmap at each step (terrain occlusion)
//
// Returns (scatteredLight, transmittance) through out parameters.
// ═════════════════════════════════════════════════════════════════════════
void IntegrateScatteredLuminance(
    float3 worldCam,
    float3 viewDir,
    float  tMax,
    float3 sunDir,
    bool   enableShadow,
    out float3 scatteredLight,
    out float3 transmittance)
{
    scatteredLight = float3(0, 0, 0);
    transmittance  = float3(1, 1, 1);
    if (tMax <= 0.0) return;

    float3 planetCenter    = float3(0, 0, 0);
    float  atmosphereRadius = _PlanetRadius + _AtmosphereHeight;

    float tBottom = RaySphereIntersectNearest(worldCam, viewDir, planetCenter, _PlanetRadius);
    float tTop    = RaySphereIntersectNearest(worldCam, viewDir, planetCenter, atmosphereRadius);

    float tAtmo = 0.0;
    if (tBottom < 0.0)      { if (tTop < 0.0) return; tAtmo = tTop; }
    else if (tTop > 0.0)    { tAtmo = min(tTop, tBottom); }
    tMax = min(tMax, tAtmo);
    if (tMax <= 0.0) return;

    // ── Variable sample count ──────────────────────────────────────────
    float sampleCountF     = lerp(4.0, 64.0, saturate(tMax * 0.01));
    float sampleCountFloor = floor(sampleCountF);
    float tMaxFloor        = tMax * sampleCountFloor / sampleCountF;

    float cosTheta            = dot(viewDir, sunDir);
    const float sampleSegT    = 0.3;

    float3 L          = float3(0, 0, 0);
    float3 throughput = float3(1, 1, 1);
    float  t          = 0.0;

    [loop]
    for (float s = 0.0; s < sampleCountF; s += 1.0)
    {
        // Squared distribution (more samples near camera)
        float t0 = s        / sampleCountFloor;
        float t1 = (s + 1.0) / sampleCountFloor;
        t0 = tMaxFloor * (t0 * t0);
        t1 = (t1 > 1.0) ? tMax : tMaxFloor * (t1 * t1);
        t  = t0 + (t1 - t0) * sampleSegT;
        float dt = t1 - t0;

        float3 P      = worldCam + viewDir * t;
        float  height = length(P - planetCenter) - _PlanetRadius;

        float3 extinction = kRayleighScattering * exp(-height / _ScaleHeight)
                          + kMieScattering    * exp(-height / _MieScaleHeight)
                          + kOzoneAbsorption  * GetOzoneDensity(height);

        const float3 sampleTau = extinction * dt;
        const float3 sampleT   = exp(-sampleTau);

        float3 upDir        = normalize(P - planetCenter);
        float  cosSunZenith = dot(upDir, sunDir);
        float3 T_sun        = GetTransmittanceToSun(height, cosSunZenith);

        float3 J = EvaluateInScattering(height, cosTheta, _MieG);
        float3 ms = GetMultiScattering(height, cosSunZenith);

        // Planet sphere occlusion
        float tEarth     = RaySphereIntersectNearest(P, sunDir,
            planetCenter + upDir * PLANET_RADIUS_OFFSET, _PlanetRadius);
        float earthShadow = tEarth >= 0.0 ? 0.0 : 1.0;

        // Terrain occlusion via URP shadowmap
        // P is in planet-centric coords; convert to Unity world space
        // (camera = _WorldSpaceCameraPos + (0, _PlanetRadius, 0), planet at origin)
        float terrainShadow = 1.0;
        if (enableShadow)
        {
            float3 unityWorldPos = P - float3(0, _PlanetRadius, 0);
            float4 shadowCoord = TransformWorldToShadowCoord(unityWorldPos);
            terrainShadow = MainLightRealtimeShadow(shadowCoord);
        }

        // Multi-scattering (ms) represents diffuse ambient from higher-order
        // scattering paths. When terrain is in shadow, those paths are
        // partially blocked — apply a smooth attenuation rather than hard 0.
        float terrainShadowMS = lerp(0.3, 1.0, terrainShadow);

        float3 S = _SunIntensity * _SunLightColor
                 * (earthShadow * terrainShadow * T_sun * J + ms * terrainShadowMS);

        float3 Sint = (S - S * sampleT) / max(extinction, 1e-6);
        L           += throughput * Sint;
        throughput  *= sampleT;
    }

    scatteredLight = L;
    transmittance  = throughput;
}

#endif
