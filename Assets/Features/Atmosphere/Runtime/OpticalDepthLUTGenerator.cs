using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Precomputes a 2D optical depth LUT for atmospheric scattering.
///
/// LUT axes:
///   U = height above ground  [0, atmosphereRadius - planetRadius]
///   V = cos(sun zenith)      [-1, 1]
///
/// Stored as RG16 (R = Rayleigh τ, G = Mie τ).
/// Transmittance T = exp(-β·τ) is computed at runtime by SampleTransmittanceLUT().
///
/// Usage:
///   1. Generate() the LUT with current atmosphere parameters.
///   2. Set it as a global texture before rendering:
///      Shader.SetGlobalTexture("_OpticalDepthLUT", generator.OpticalDepthLUT);
///   3. Shaders sample it via SampleTransmittanceLUT().
/// </summary>
public class OpticalDepthLUTGenerator : System.IDisposable
{
    public RenderTexture OpticalDepthLUT => m_LUT;

    private ComputeShader m_Compute;
    private int m_Kernel;
    private RenderTexture m_LUT;

    private const int k_LUTSize = 256;

    // Cached parameters for dirty-check
    private float m_LastPlanetRadius;
    private float m_LastAtmosphereRadius;
    private float m_LastScaleHeight;
    private float m_LastMieScaleHeight;
    private int m_LastLightSamples;

    public OpticalDepthLUTGenerator(ComputeShader compute)
    {
        m_Compute = compute;
        m_Kernel = m_Compute.FindKernel("ComputeOpticalDepthLUT");
    }

    /// <summary>
    /// (Re)generates the LUT if any parameter changed.
    /// Call once per frame or whenever atmosphere settings are modified.
    /// </summary>
    public void Generate(float planetRadius, float atmosphereRadius,
                         float scaleHeight, float mieScaleHeight,
                         int lightSamples)
    {
        if (m_LUT == null)
        {
            m_LUT = new RenderTexture(k_LUTSize, k_LUTSize, 0,
                RenderTextureFormat.RGHalf)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "OpticalDepthLUT"
            };
            m_LUT.Create();

            // Force first generation
            m_LastPlanetRadius = float.NaN;
        }

        // Dirty check — skip if nothing changed
        if (Mathf.Approximately(planetRadius, m_LastPlanetRadius) &&
            Mathf.Approximately(atmosphereRadius, m_LastAtmosphereRadius) &&
            Mathf.Approximately(scaleHeight, m_LastScaleHeight) &&
            Mathf.Approximately(mieScaleHeight, m_LastMieScaleHeight) &&
            lightSamples == m_LastLightSamples)
            return;

        m_LastPlanetRadius = planetRadius;
        m_LastAtmosphereRadius = atmosphereRadius;
        m_LastScaleHeight = scaleHeight;
        m_LastMieScaleHeight = mieScaleHeight;
        m_LastLightSamples = lightSamples;

        m_Compute.SetFloat("_PlanetRadius", planetRadius);
        m_Compute.SetFloat("_AtmosphereRadius", atmosphereRadius);
        m_Compute.SetFloat("_ScaleHeight", scaleHeight);
        m_Compute.SetFloat("_MieScaleHeight", mieScaleHeight);
        m_Compute.SetFloat("_LightSamples", lightSamples);
        m_Compute.SetFloat("_InvLUTSize", 1.0f / k_LUTSize);
        m_Compute.SetTexture(m_Kernel, "_OpticalDepthLUT", m_LUT);

        int threadGroups = Mathf.CeilToInt(k_LUTSize / 8.0f);
        m_Compute.Dispatch(m_Kernel, threadGroups, threadGroups, 1);
    }

    public void Dispose()
    {
        if (m_LUT != null)
        {
            m_LUT.Release();
            Object.DestroyImmediate(m_LUT);
            m_LUT = null;
        }
    }
}
