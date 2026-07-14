#ifndef SCATTERING_INCLUDED
#define SCATTERING_INCLUDED


// ── Rayleigh Scattering Coefficient at Sea Level ─────────────────────
// β_R⁰(λ) = 8π³(n²-1)² · F_K / (3Nλ⁴)
//
//   n   = 1.00029   refractive index of air at STP
//   N   = 2.504e25  molecular number density [m⁻³]
//   F_K = (6+3δ)/(6-7δ) ≈ 1.06  King factor (δ=0.035 depolarization)
//   K   = 8π³(n²-1)² · F_K / (3N) ≈ 1.18×10⁻³⁰ [m³]
//
// RGB wavelengths: 680 / 550 / 440 nm → β_R = K / λ⁴.
// Blue scatters ~5.7× more than red — this is why the sky is blue.
//
// Altitude: β_R(h) = kRayleighScattering · exp(-h / H_R),  H_R ≈ 8 km.
static const float3 kRayleighScattering = float3(5.8e-6, 1.35e-5, 3.31e-5); // R, G, B [m⁻¹]

// ── Mie Scattering Coefficient at Sea Level ──────────────────────────
// Mie (aerosol) scattering is wavelength-independent at the scales
// relevant for atmospheric rendering — particles are large relative to
// visible wavelengths.
//
// Standard Bruneton reference value for a clean atmosphere.
//
// Altitude: β_M(h) = kMieScattering · exp(-h / H_M),  H_M ≈ 1.2 km.
static const float3 kMieScattering = float3(3.99e-6, 3.99e-6, 3.99e-6); // R, G, B [m⁻¹]

// ── Ozone Absorption (Chappuis band) ─────────────────────────────────────
// Ozone absorbs green-yellow light (500–650 nm), reddening sunsets.
// Unlike Rayleigh/Mie, ozone is a pure absorber (no scattering) and
// concentrates in the stratosphere around 25 km.
//
// Profile: Gaussian centered at 25 km, half-width ~8 km.
// Absorption coefficient at peak density, in m⁻¹.
//
// Ozone absorption peaks at low sun angles (long slant path through
// stratosphere) and is nearly invisible at noon (short vertical path).
// G absorbs green-yellow, R and B are negligible — Rayleigh handles blue.
//
// Vertical optical depth at peak ≈ k · √π · halfWidth ≈ 0.06 (subtle at noon)
// Horizon slant path ~20× longer → OD ≈ 1.2 → deep red at sunset.
static const float3 kOzoneAbsorption = float3(0.05e-6, 10.0e-6, 0.01e-6);

#define OZONE_CENTER_HEIGHT 25000.0
#define OZONE_HALF_WIDTH     8000.0

// Ray-Sphere Intersection (Alan Zucconi)
// Reference: https://www.alanzucconi.com/2017/10/10/atmospheric-scattering-6/
//
// Ray: O (origin) + t * D (direction, MUST be normalized)
// Sphere: center C, radius R
//
// Returns true if the ray hits the sphere.
//   tNear — first intersection (entry). May be negative if sphere is behind the ray.
//   tFar  — second intersection (exit).
//
// For atmospheric scattering, caller typically clamps:
//   float tEntry = max(tNear, 0.0);
//   float tExit  = max(tFar,  0.0);
bool RayIntersect(
    float3 O, float3 D, // Ray origin & normalized direction
    float3 C, float R, // Sphere center & radius
    out float tNear, out float tFar
)
{
    float3 L = C - O;
    float tCA = dot(L, D); // projection of center onto ray
    float d2 = dot(L, L) - tCA * tCA; // squared distance from center to closest point on ray

    if (d2 > R * R)
        return false;

    float tHC = sqrt(R * R - d2); // half-chord length
    tNear = tCA - tHC;
    tFar = tCA + tHC;
    return true;
}

// ─────────────────────────────────────────────────────────────────────────────
// Atmospheric Ray March — intersection with both atmosphere and planet
// ─────────────────────────────────────────────────────────────────────────────
//
// Runs RayIntersect twice:
//   1. Ray ↔ atmosphere sphere → tNear_atm, tFar_atm
//   2. Ray ↔ planet sphere      → tNear_planet, tFar_planet
//
// If the ray hits the planet, the atmosphere journey terminates early
// at the planet's surface instead of continuing to the far atmosphere edge:
//
//     tFar_atm = min(tFar_atm, tNear_planet)
//
// Then both tNear and tFar are clamped to [0, ∞) so the caller gets
// usable, forward-only distances.
//
// Returns true if the ray passes through any atmosphere (tEntry < tExit).
//
// ┌──────────── 大气层 ────────────┐
// │  ┌──────── 行星 ────────┐      │
// │  │                      │      │
// │··│·· ray ·············→ │······│ → tFar_atm
// │  │                      │      │   ↓ (被行星截断)
// │  └──────────────────────┘      │ → tNear_planet = new tFar_atm
// └────────────────────────────────┘
//
bool AtmosphericRayIntersect(
    float3 rayOrigin, float3 rayDir, // Ray (dir must be normalized)
    float3 planetCenter, // Sphere center (shared)
    float atmosphericRadius, float planetRadius, // Outer & inner sphere radii
    out float tEntry, out float tExit // Clamped, forward-only distances
)
{
    tEntry = 0.0;
    tExit = 0.0;

    // Intersect with atmosphere outer shell
    float tNear_atm, tFar_atm;
    if (!RayIntersect(rayOrigin, rayDir, planetCenter, atmosphericRadius, tNear_atm, tFar_atm))
        return false;

    // Intersect with planet surface — may truncate the atmosphere journey
    float tNear_planet, tFar_planet;
    if (RayIntersect(rayOrigin, rayDir, planetCenter, planetRadius, tNear_planet, tFar_planet))
    {
        // Planet truncation: must actually enter the planet (not just tangent).
        // tFar_planet > tNear_planet ensures the ray has non-zero path through planet.
        // tNear_planet can be 0 when camera is on the surface looking downward.
        if (tNear_planet >= 0.0 && tFar_planet > tNear_planet)
            tFar_atm = min(tFar_atm, tNear_planet);
    }

    // Clamp everything to forward direction
    tEntry = max(tNear_atm, 0.0);
    tExit = max(tFar_atm, 0.0);

    return tEntry < tExit;
}


// ── EvaluateTransmittance ─────────────────────────────────────────────
// Transmittance from point P towards the sun to atmosphere exit.
//   T = exp( -β_R·τ_R(s) - β_M·τ_M(s) )
//
// Rayleigh and Mie have different scale heights, so their optical
// depths τ_R, τ_M are accumulated separately within the same loop
// (no extra samples).
//
// Returns float3(0,0,0) if the ray hits the planet (fully occluded).
float3 EvaluateTransmittance(
    float3 P, float3 dir, // start point & normalized direction
    int sampleCount,
    float3 planetCenter, float planetRadius, float atmosphereRadius,
    float scaleHeightR, float scaleHeightM,
    float3 betaR, float3 betaM)
{
    float tNear, tFar;
    if (!RayIntersect(P, dir, planetCenter, atmosphereRadius, tNear, tFar))
        return float3(1.0, 1.0, 1.0); // outside atmosphere — no extinction

    float dist = max(tFar, 0.0);
    float ds = dist / float(sampleCount);
    float tauR = 0.0, tauM = 0.0;
    float t = 0.0;

    for (int i = 0; i < sampleCount; i++)
    {
        float3 Q = P + dir * (t + ds * 0.5);
        float height = distance(planetCenter, Q) - planetRadius;

        if (height < 0.0)
            return float3(0.0, 0.0, 0.0); // occluded by planet

        tauR += exp(-height / scaleHeightR) * ds;
        tauM += exp(-height / scaleHeightM) * ds;
        t += ds;
    }

    return exp(-betaR * tauR - betaM * tauM);
}

// ── Rayleigh Phase Function ──────────────────────────────────────────
// Standard Rayleigh scattering angular distribution.
// γ(θ) = 3/(16π) · (1 + cos²θ)
// Wavelength dependence is carried by the scattering coefficients, not the
// phase function, so this returns a scalar.
float RayleighPhase(float cosTheta)
{
    return (3.0 / (16.0 * PI)) * (1.0 + cosTheta * cosTheta);
}

// ── Mie Phase Function (Cornette–Shanks, 1992) ────────────────────────
// Improved Henyey-Greenstein variant that better reproduces observed
// atmospheric scattering for small particles:
//   p(θ) = 3/(8π) · (1-g²)/(2+g²) · (1+cos²θ) / (1+g²-2g·cosθ)^(3/2)
//
// Advantages over standard Henyey-Greenstein:
//   - Reduces exactly to RayleighPhase when g = 0.
//   - Captures back-scattering and glory effects that HG misses.
//
// g ∈ [-1, 1]: asymmetry parameter
//   g > 0 → forward-scattering  (large particles, e.g. haze)
//   g = 0 → isotropic / Rayleigh
//   g < 0 → backward-scattering (rare; can model certain aerosols)
float MiePhase(float g, float cosTheta)
{
    float g2 = g * g;
    float a = 3.0 / (8.0 * PI);
    float b = (1.0 - g2) / (2.0 + g2);
    float c = 1.0 + cosTheta * cosTheta;
    // d ≥ (1 - |g|)² ≥ 0; clamp to avoid division by zero when g=±1, cosθ=±1
    float d = pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-5), 1.5);
    return a * b * (c / d);
}

// ── EvaluateInScattering ──────────────────────────────────────────────
// Source function J at point P: β(λ) · γ(θ) · ρ(h).
// Rayleigh and Mie use different density profiles (ρ_R vs ρ_M).
// Mie is commented out for now — add back once Mie parameters are wired.
float3 EvaluateInScattering(float height,
                            float scaleHeightR, float scaleHeightM,
                            float cosTheta, float mieG)
{
    float densityR = exp(-height / scaleHeightR);
    float densityM = exp(-height / scaleHeightM);

    float3 rayleigh = kRayleighScattering * RayleighPhase(cosTheta) * densityR;
    float3 mie = kMieScattering * MiePhase(mieG, cosTheta) * densityM;

    return rayleigh + mie;
}

#endif // SCATTERING_UTILS_INCLUDED
