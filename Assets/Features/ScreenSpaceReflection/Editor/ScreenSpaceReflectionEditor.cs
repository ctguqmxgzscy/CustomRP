using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(ScreenSpaceReflection))]
    sealed class ScreenSpaceReflectionEditor : VolumeComponentEditor
    {
        // General
        SerializedDataParameter m_Enabled;
        SerializedDataParameter m_UsedAlgorithm;
        SerializedDataParameter m_MinSmoothness;
        SerializedDataParameter m_SmoothnessFadeStart;
        SerializedDataParameter m_ReflectSky;

        // Ray Marching
        SerializedDataParameter m_ScreenFadeDistance;
        SerializedDataParameter m_DepthBufferThickness;
        SerializedDataParameter m_RayMaxIterations;

        // PBR Accumulation
        SerializedDataParameter m_AccumulationFactor;
        SerializedDataParameter m_BiasFactor;
        SerializedDataParameter m_SpeedRejectionParam;
        SerializedDataParameter m_SpeedRejectionScalerFactor;
        SerializedDataParameter m_SpeedSmoothReject;
        SerializedDataParameter m_SpeedSurfaceOnly;
        SerializedDataParameter m_SpeedTargetOnly;
        SerializedDataParameter m_EnableWorldSpeedRejection;

        static readonly GUIContent k_Algo = EditorGUIUtility.TrTextContent("Algorithm",
            "The screen space reflection algorithm used.");
        static readonly GUIContent k_ReflectSky = EditorGUIUtility.TrTextContent("Reflect Sky",
            "When enabled, SSR handles sky reflection for opaque objects.");
        static readonly GUIContent k_MinSmoothness = EditorGUIUtility.TrTextContent("Minimum Smoothness",
            "Controls the smoothness value at which SSR activates and the smoothness-controlled fade out stops.");
        static readonly GUIContent k_SmoothnessFadeStart = EditorGUIUtility.TrTextContent("Smoothness Fade Start",
            "Controls the smoothness value at which the smoothness-controlled fade out starts.");
        static readonly GUIContent k_ScreenFadeDistance = EditorGUIUtility.TrTextContent("Screen Edge Fade Distance",
            "Controls the distance at which SSR fades out near the edge of the screen.");
        static readonly GUIContent k_DepthBufferThickness = EditorGUIUtility.TrTextContent("Object Thickness",
            "Controls the typical thickness of objects the reflection rays may pass behind.");
        static readonly GUIContent k_RayMaxIterations = EditorGUIUtility.TrTextContent("Max Ray Steps",
            "Sets the maximum number of steps used for ray marching. Affects both correctness and performance.");
        static readonly GUIContent k_AccumulationFactor = EditorGUIUtility.TrTextContent("Accumulation Factor",
            "Controls the amount of accumulation (0 no accumulation, 1 just accumulate).");
        static readonly GUIContent k_BiasFactor = EditorGUIUtility.TrTextContent("Roughness Bias",
            "Controls the relative roughness offset. A low value means material roughness stays the same, a high value means smoother reflections.");
        static readonly GUIContent k_EnableWorldSpeedRejection = EditorGUIUtility.TrTextContent("World Space Speed Rejection",
            "When enabled, speed will be computed in world space to reject samples.");
        static readonly GUIContent k_SpeedRejectionParam = EditorGUIUtility.TrTextContent("Speed Rejection",
            "Controls the likelihood history will be rejected based on the previous frame motion vectors.");
        static readonly GUIContent k_SpeedRejectionScaler = EditorGUIUtility.TrTextContent("Speed Rejection Scaler Factor",
            "Controls the upper range of speed. The faster the objects or camera are moving, the higher this number should be.");
        static readonly GUIContent k_SpeedSmoothReject = EditorGUIUtility.TrTextContent("Speed Smooth Rejection",
            "When enabled, history can be partially rejected for moving objects. When disabled, history is either kept or totally rejected.");
        static readonly GUIContent k_SpeedSurfaceOnly = EditorGUIUtility.TrTextContent("Speed From Reflecting Surface",
            "When enabled, the reflecting surface movement is considered as a valid rejection condition.");
        static readonly GUIContent k_SpeedTargetOnly = EditorGUIUtility.TrTextContent("Speed From Reflected Surface",
            "When enabled, the reflected surface movement is considered as a valid rejection condition.");

        public override void OnEnable()
        {
            var o = new PropertyFetcher<ScreenSpaceReflection>(serializedObject);

            m_Enabled = Unpack(o.Find(x => x.enabled));
            m_UsedAlgorithm = Unpack(o.Find(x => x.usedAlgorithm));
            m_MinSmoothness = Unpack(o.Find(x => x.minSmoothness));
            m_SmoothnessFadeStart = Unpack(o.Find(x => x.smoothnessFadeStart));
            m_ReflectSky = Unpack(o.Find(x => x.reflectSky));
            m_ScreenFadeDistance = Unpack(o.Find(x => x.screenFadeDistance));
            m_DepthBufferThickness = Unpack(o.Find(x => x.depthBufferThickness));
            m_RayMaxIterations = Unpack(o.Find(x => x.rayMaxIterations));
            m_AccumulationFactor = Unpack(o.Find(x => x.accumulationFactor));
            m_BiasFactor = Unpack(o.Find(x => x.biasFactor));
            m_SpeedRejectionParam = Unpack(o.Find(x => x.speedRejectionParam));
            m_SpeedRejectionScalerFactor = Unpack(o.Find(x => x.speedRejectionScalerFactor));
            m_SpeedSmoothReject = Unpack(o.Find(x => x.speedSmoothReject));
            m_SpeedSurfaceOnly = Unpack(o.Find(x => x.speedSurfaceOnly));
            m_SpeedTargetOnly = Unpack(o.Find(x => x.speedTargetOnly));
            m_EnableWorldSpeedRejection = Unpack(o.Find(x => x.enableWorldSpeedRejection));
        }

        public override void OnInspectorGUI()
        {
            PropertyField(m_Enabled);

            if (!m_Enabled.value.boolValue)
                return;

            PropertyField(m_UsedAlgorithm, k_Algo);

            // Shared parameters
            PropertyField(m_MinSmoothness, k_MinSmoothness);
            PropertyField(m_SmoothnessFadeStart, k_SmoothnessFadeStart);
            // Ensure smoothnessFadeStart >= minSmoothness
            if (m_SmoothnessFadeStart.value.floatValue < m_MinSmoothness.value.floatValue)
                m_SmoothnessFadeStart.value.floatValue = m_MinSmoothness.value.floatValue;

            PropertyField(m_ReflectSky, k_ReflectSky);
            PropertyField(m_ScreenFadeDistance, k_ScreenFadeDistance);
            PropertyField(m_DepthBufferThickness, k_DepthBufferThickness);
            m_DepthBufferThickness.value.floatValue = Mathf.Clamp(m_DepthBufferThickness.value.floatValue, 0.001f, 1.0f);

            PropertyField(m_RayMaxIterations, k_RayMaxIterations);
            m_RayMaxIterations.value.intValue = Mathf.Max(0, m_RayMaxIterations.value.intValue);

            // PBR Accumulation settings (only shown when PBR algorithm is selected)
            if (m_UsedAlgorithm.value.intValue == (int)ScreenSpaceReflectionAlgorithm.PBRAccumulation)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("PBR Accumulation", EditorStyles.boldLabel);

                PropertyField(m_AccumulationFactor, k_AccumulationFactor);
                PropertyField(m_EnableWorldSpeedRejection, k_EnableWorldSpeedRejection);

                using (new EditorGUI.IndentLevelScope())
                {
                    if (m_EnableWorldSpeedRejection.value.boolValue)
                    {
                        PropertyField(m_SpeedRejectionScalerFactor, k_SpeedRejectionScaler);
                        PropertyField(m_SpeedSmoothReject, k_SpeedSmoothReject);
                    }

                    PropertyField(m_SpeedRejectionParam, k_SpeedRejectionParam);

                    // At least one of surface/target must be enabled
                    if (!m_SpeedSurfaceOnly.value.boolValue && !m_SpeedTargetOnly.value.boolValue)
                        m_SpeedSurfaceOnly.value.boolValue = true;

                    PropertyField(m_SpeedSurfaceOnly, k_SpeedSurfaceOnly);
                    PropertyField(m_SpeedTargetOnly, k_SpeedTargetOnly);
                    PropertyField(m_BiasFactor, k_BiasFactor);
                }
            }
        }
    }
}
