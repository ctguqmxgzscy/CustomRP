#ifndef SCATTERING_UTILS_INCLUDED
#define SCATTERING_UTILS_INCLUDED

#include "Scattering.hlsl"
// URP core library — dot, sqrt, etc. (Lighting.hlsl not needed for pure math)

// ── Transmittance LUT ─────────────────────────────────────────────────
// 2D lookup: X = height above ground, Y = cos(sun zenith angle)
// .r = τ_R (Rayleigh optical depth), .g = τ_M (Mie optical depth).
// Occluded rays store 1e20 → exp(-β·1e20) ≈ 0, bilinear-friendly.
TEXTURE2D(_OpticalDepthLUT);
SAMPLER(sampler_OpticalDepthLUT);

// ── SampleTransmittanceLUT ────────────────────────────────────────────
// Fast LUT-based alternative to EvaluateTransmittance.
// Looks up precomputed τ_R, τ_M for a given height and sun angle,
// then computes T = exp(-β_R·τ_R - β_M·τ_M) at runtime.
//
// height    — P's altitude above ground (same unit as planet/atmosphere radii)
// cosSunZenith — dot(radialFromCenter, sunDir); +1 = sun overhead, -1 = below
// betaR, betaM — sea-level scattering coefficients per channel
//
// Returns float3(0,0,0) if the ray is occluded by the planet.
float3 SampleTransmittanceLUT(float height, float cosSunZenith,
                              float atmosphereRadius, float planetRadius,
                              float3 betaR, float3 betaM)
{
    // U: cos(sun zenith),   V: height
    float maxHeight = atmosphereRadius - planetRadius;
    float2 uv = float2(
        saturate(cosSunZenith * 0.5 + 0.5),
        saturate(height / maxHeight)
    );

    float2 tau = SAMPLE_TEXTURE2D_LOD(_OpticalDepthLUT, sampler_OpticalDepthLUT, uv, 0).rg;

    // Occluded rays store τ = 1e20 → exp(-β * 1e20) ≈ 0, no special branch needed.
    return exp(-betaR * tau.r - betaM * tau.g);
}

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

#endif // SCATTERING_UTILS_INCLUDED
