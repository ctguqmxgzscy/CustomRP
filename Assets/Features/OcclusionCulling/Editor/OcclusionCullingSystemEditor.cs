using UnityEditor;
using UnityEngine;

namespace GPUDrivenOcclusion
{
    [CustomEditor(typeof(OcclusionCullingSystem))]
    public class OcclusionCullingSystemEditor : Editor
    {
        private bool _showOccludees = true;
        private Vector2 _scroll;

        public override void OnInspectorGUI()
        {
            var sys = (OcclusionCullingSystem)target;

            EditorGUILayout.LabelField($"Registered: {sys.OccludeeCount}", EditorStyles.boldLabel);

            GUILayout.Space(4);

            // Nested serialized fields (frustum, occlusion, etc.)
            DrawDefaultInspector();

            GUILayout.Space(8);

            _showOccludees = EditorGUILayout.Foldout(_showOccludees, "Occludees");
            if (_showOccludees)
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(300));

                var renderers = sys.GetAllRenderers();
                if (renderers == null || renderers.Length == 0)
                {
                    EditorGUILayout.HelpBox("No occludees registered. Use Tools > Occlusion Culling > Scan & Register All.", MessageType.Warning);
                }
                else
                {
                    foreach (var r in renderers)
                    {
                        if (r == null)
                        {
                            EditorGUILayout.LabelField("(null)", EditorStyles.miniLabel);
                            continue;
                        }

                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.ObjectField(r, typeof(Renderer), true);
                        EditorGUILayout.LabelField(r.enabled ? "●" : "○", GUILayout.Width(14));
                        EditorGUILayout.EndHorizontal();
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }
    }
}
