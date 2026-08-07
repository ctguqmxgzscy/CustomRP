using UnityEngine;

/// <summary>
/// Stores all atmosphere scattering parameters in a single asset.
/// Referenced by AtmosphereSkyboxLutFeature — no MonoBehaviour or material references needed.
///
/// Create via: Assets → Create → Rendering → Atmosphere Settings
/// </summary>
[CreateAssetMenu(menuName = "Rendering/Atmosphere Settings")]
public class AtmosphereSettings : ScriptableObject
{
    [Header("Planet")]
    [Tooltip("Planet radius (world units).")]
    public float planetRadius = 3.16f;

    [Tooltip("Atmosphere thickness (height above planet surface). atmosphereRadius = planetRadius + atmosphereHeight.")]
    public float atmosphereHeight = 0.59f;

    [Header("Rayleigh Scattering")]
    [Tooltip("Scale height for Rayleigh (km for Earth = 8.0).")]
    public float scaleHeight = 8000f;

    [Tooltip("Normalized Rayleigh scattering color at sea level. R=680nm, G=550nm, B=440nm.\nPhysical coefficient = color.rgb × rayleighScatteringScale [m⁻¹].")]
    public Color rayleighScatteringColor = new Color(0.175f, 0.408f, 1.0f);

    [Tooltip("Magnitude multiplier for Rayleigh scattering. 3.31e-5 = Earth sea level.")]
    public float rayleighScatteringScale = 3.31e-5f;

    [Header("Mie Scattering")]
    [Tooltip("Mie asymmetry parameter. g>0 = forward scatter (haze).")]
    [Range(-1f, 1f)]
    public float mieG = 0.8f;

    [Tooltip("Scale height for Mie/aerosol (km for Earth = 1.2).")]
    public float mieScaleHeight = 1200f;

    [Tooltip("Normalized Mie scattering color at sea level. Wavelength-independent for haze particles.\nPhysical coefficient = color.rgb × mieScatteringScale [m⁻¹].")]
    public Color mieScatteringColor = Color.white;

    [Tooltip("Magnitude multiplier for Mie scattering. 3.99e-6 = Earth sea level.")]
    public float mieScatteringScale = 3.99e-6f;

    [Header("Sun")]
    [Tooltip("Sun light intensity multiplier.")]
    public float sunIntensity = 100000f;

    [Tooltip("Sun disk angular radius in degrees.")]
    [Range(0f, 5f)]
    public float sunDiskAngle = 0.5f;

    [Tooltip("Sun light color (tint).")]
    public Color sunLightColor = Color.white;

    [Header("Aerial Perspective")]
    [Tooltip("Enable per-pixel ray-march with URP shadowmap for terrain occlusion.")]
    public bool enableTerrainShadow = false;

    [Tooltip("Enable Hillaire 2020 multi-scattering approximation (boosts haze brightness at all view angles).")]
    public bool enableMultiScattering = true;

    [Tooltip("Multiplier for aerial perspective inscattered light. 1 = physically correct, >1 = stronger haze.")]
    [Range(0f, 20f)]
    public float apIntensity = 1.0f;

    [Header("Quality")]
    [Tooltip("Samples along the view ray for LUT precomputation.")]
    [Range(8, 256)]
    public int viewSamples = 64;

    [Tooltip("Samples along the light ray for transmittance LUT.")]
    [Range(1, 64)]
    public int lightSamples = 32;
}
