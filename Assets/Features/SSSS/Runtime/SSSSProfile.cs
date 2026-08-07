using UnityEngine;

[CreateAssetMenu(menuName = "SSSS/Profile")]
public class SSSSProfile : ScriptableObject
{
    [Tooltip("Per-channel scattering strength. Normalized internally. R dominates for skin.")]
    public Color mainColor = new Color(1.0f, 0.31f, 0.20f);

    [Tooltip("Per-channel scattering distance. Normalized internally. Larger = shorter distance.")]
    public Color falloff = new Color(1.0f, 0.51f, 0.29f);

    [Tooltip("Scatter radius multiplier on skin's 5-Gaussian profile. 1.0 = skin baseline. Based on Jensen 2001 dmfp ratios.")]
    [Range(0.1f, 5.0f)]
    public float scatterScale = 1.0f;

    [Header("Dual Scatter (Near / Far)")]

    [Tooltip("Enable dual near/far scatter blending. When off, uses scatterScale only.")]
    public bool useDualScale = false;

    [Tooltip("Near scatter radius multiplier. Smaller = sharper detail, only short-range scatter.")]
    [Range(0.02f, 5.0f)]
    public float nearScatterScale = 0.5f;

    [Tooltip("Far scatter radius multiplier. Larger = broader, softer light diffusion.")]
    [Range(0.1f, 5.0f)]
    public float farScatterScale = 2.0f;

    [Tooltip("Blend between near and far profiles. 0 = all far, 1 = all near.")]
    [Range(0.0f, 1.0f)]
    public float nearFarBalance = 0.5f;
}
