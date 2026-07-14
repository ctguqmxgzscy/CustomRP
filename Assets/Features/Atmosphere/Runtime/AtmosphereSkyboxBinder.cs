using UnityEngine;

/// <summary>
/// Synchronizes atmosphere parameters from the reference material to the
/// skybox material, and assigns it to RenderSettings.skybox.
///
/// Attach to the same GameObject that carries AtmosphereLUTBinder
/// (the planet / atmosphere center).
/// </summary>
[ExecuteAlways]
public class AtmosphereSkyboxBinder : MonoBehaviour
{
    [Header("Materials")]
    [SerializeField] private Material m_SkyboxMaterial;
    [SerializeField] private Material m_AtmosphereMaterial;

    [Header("Settings")]
    [Tooltip("If true, sets RenderSettings.skybox = m_SkyboxMaterial in OnEnable.")]
    [SerializeField] private bool m_SetAsRenderSettingsSkybox = true;

    private void OnEnable()
    {
        if (m_SetAsRenderSettingsSkybox && m_SkyboxMaterial != null)
        {
            RenderSettings.skybox = m_SkyboxMaterial;
        }
    }

    private void Update()
    {
        if (m_SkyboxMaterial == null || m_AtmosphereMaterial == null)
            return;

        // ── Sync scattering parameters ────────────────────────────────
        SyncFloat("_PlanetRadius");
        SyncFloat("_AtmosphereRadius");
        SyncFloat("_ScaleHeight");
        SyncFloat("_MieScaleHeight");
        SyncFloat("_SunIntensity");
        SyncFloat("_LightSamples");
        SyncFloat("_MieG");
        SyncFloat("_ViewSamples");

        // ── Atmosphere center in world space ──────────────────────────
        m_SkyboxMaterial.SetVector("_PlanetCenter", transform.position);
    }

    private void OnDisable()
    {
        if (RenderSettings.skybox == m_SkyboxMaterial)
        {
            RenderSettings.skybox = null;
        }
    }

    private void SyncFloat(string name)
    {
        m_SkyboxMaterial.SetFloat(name, m_AtmosphereMaterial.GetFloat(name));
    }
}
