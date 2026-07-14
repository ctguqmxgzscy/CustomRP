using UnityEngine;

/// <summary>
/// Generates the optical depth LUT and exposes it as a global texture.
/// Attach to a GameObject in the scene with the atmosphere material.
///
/// Parameters are read from the attached material's shader properties,
/// so they stay in sync with what the shader uses.
/// </summary>
[ExecuteAlways]
public class AtmosphereLUTBinder : MonoBehaviour
{
    [SerializeField] private Material m_AtmosphereMaterial;
    [SerializeField] private ComputeShader m_OpticalDepthLUTCompute;

    private OpticalDepthLUTGenerator m_Generator;

    private void OnEnable()
    {
        if (m_OpticalDepthLUTCompute == null)
        {
            Debug.LogWarning("AtmosphereLUTBinder: missing OpticalDepthLUT compute shader.");
            return;
        }

        m_Generator = new OpticalDepthLUTGenerator(m_OpticalDepthLUTCompute);
        GenerateLUT();
    }

    private void OnDisable()
    {
        m_Generator?.Dispose();
        m_Generator = null;
    }

    private void Update()
    {
        GenerateLUT();
    }

    private void GenerateLUT()
    {
        if (m_Generator == null || m_AtmosphereMaterial == null) return;

        float planetRadius    = m_AtmosphereMaterial.GetFloat("_PlanetRadius");
        float atmosphereRadius = m_AtmosphereMaterial.GetFloat("_AtmosphereRadius");
        float scaleHeight      = m_AtmosphereMaterial.GetFloat("_ScaleHeight");
        float lightSamples     = m_AtmosphereMaterial.GetFloat("_LightSamples");

        // _MieScaleHeight is a newer parameter; fall back to ratio if absent
        float mieScaleHeight;
        if (m_AtmosphereMaterial.HasProperty("_MieScaleHeight"))
            mieScaleHeight = m_AtmosphereMaterial.GetFloat("_MieScaleHeight");
        else
            mieScaleHeight = scaleHeight * 0.15f;

        m_Generator.Generate(planetRadius, atmosphereRadius,
                             scaleHeight, mieScaleHeight,
                             (int)lightSamples);

        Shader.SetGlobalTexture("_OpticalDepthLUT", m_Generator.OpticalDepthLUT);
    }
}
