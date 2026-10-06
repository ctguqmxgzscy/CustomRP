using UnityEditor;
using UnityEngine;

/// <summary>
/// VolumetricCloudFeature 自定义 Inspector：
/// 按 Lighting Mode（BeerLambertCone / HillaireMS）显示各自需要的参数，
/// 公共参数（Assets/Sun/Planet/Density/CloudType/Noise/Weather/March 共享项）始终显示。
/// </summary>
[CustomEditor(typeof(VolumetricCloudFeature))]
public class VolumetricCloudFeatureEditor : Editor
{
    // 模式选择
    private SerializedProperty m_LightingMode;
    private SerializedProperty m_RenderMode;
    private SerializedProperty m_FragmentShader;

    // Mode 0 (BeerLambertCone) 专属
    private SerializedProperty m_CloudBaseColor;
    private SerializedProperty m_CloudTopColor;
    private SerializedProperty m_AmbientLightFactor;
    private SerializedProperty m_Density;
    private SerializedProperty m_MieGBackward;

    // Mode 1 (HillaireMS) 专属
    private SerializedProperty m_ScatterCoeff;
    private SerializedProperty m_MSAttenuation;
    private SerializedProperty m_MSContribution;
    private SerializedProperty m_MSEccentricity;

    private void OnEnable()
    {
        m_LightingMode = serializedObject.FindProperty("m_LightingMode");
        m_RenderMode = serializedObject.FindProperty("m_RenderMode");
        m_FragmentShader = serializedObject.FindProperty("m_FragmentShader");

        m_CloudBaseColor = serializedObject.FindProperty("m_CloudBaseColor");
        m_CloudTopColor = serializedObject.FindProperty("m_CloudTopColor");
        m_AmbientLightFactor = serializedObject.FindProperty("m_AmbientLightFactor");
        m_Density = serializedObject.FindProperty("m_Density");
        m_MieGBackward = serializedObject.FindProperty("m_MieGBackward");

        m_ScatterCoeff = serializedObject.FindProperty("m_ScatterCoeff");
        m_MSAttenuation = serializedObject.FindProperty("m_MSAttenuation");
        m_MSContribution = serializedObject.FindProperty("m_MSContribution");
        m_MSEccentricity = serializedObject.FindProperty("m_MSEccentricity");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // 公共参数（两种模式共用，始终显示）——排除所有条件字段
        DrawPropertiesExcluding(serializedObject,
            "m_LightingMode",
            "m_CloudBaseColor", "m_CloudTopColor", "m_AmbientLightFactor",
            "m_Density", "m_MieGBackward",
            "m_ScatterCoeff", "m_MSAttenuation", "m_MSContribution", "m_MSEccentricity",
            "m_RenderMode", "m_FragmentShader");

        EditorGUILayout.Space(4);
        EditorGUILayout.PropertyField(m_LightingMode);

        EditorGUILayout.Space(4);
        EditorGUILayout.PropertyField(m_RenderMode);
        if ((VolumetricCloudFeature.CloudRenderMode)m_RenderMode.enumValueIndex == VolumetricCloudFeature.CloudRenderMode.FragmentShader)
            EditorGUILayout.PropertyField(m_FragmentShader);

        var mode = (VolumetricCloudFeature.CloudLightingMode)m_LightingMode.enumValueIndex;
        if (mode == VolumetricCloudFeature.CloudLightingMode.BeerLambertCone)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("BeerLambert 光锥参数（仅当前模式）", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_CloudBaseColor);
            EditorGUILayout.PropertyField(m_CloudTopColor);
            EditorGUILayout.PropertyField(m_AmbientLightFactor);
            EditorGUILayout.PropertyField(m_Density);
            EditorGUILayout.PropertyField(m_MieGBackward);
        }
        else
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Hillaire MS 参数（仅当前模式）", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_ScatterCoeff);
            EditorGUILayout.PropertyField(m_MSAttenuation);
            EditorGUILayout.PropertyField(m_MSContribution);
            EditorGUILayout.PropertyField(m_MSEccentricity);
        }

        serializedObject.ApplyModifiedProperties();
    }
}
