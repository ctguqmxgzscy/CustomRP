// Editor tool: auto-scan static scene objects and register with OcclusionCullingSystem.

using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GPUDrivenOcclusion
{
    public class OcclusionCullingTool : EditorWindow
    {
        private bool _onlyStatic = true;
        private bool _skipParticles = true;

        [MenuItem("Tools/Occlusion Culling/Setup Scene")]
        private static void Open()
        {
            var w = GetWindow<OcclusionCullingTool>("Occlusion Culling");
            w.minSize = new Vector2(300, 200);
        }

        [MenuItem("Tools/Occlusion Culling/Scan & Register All", priority = 1)]
        private static void QuickScanAndRegister()
        {
            var system = FindOrCreateSystem();
            if (system == null) return;

            int count = 0;
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                count += ScanHierarchy(go, system, true, true);

            Debug.Log($"[OcclusionCulling] Registered {count} static renderers.");
        }

        [MenuItem("Tools/Occlusion Culling/Clear All", priority = 2)]
        private static void ClearAll()
        {
            var system = FindObjectOfType<OcclusionCullingSystem>();
            if (system == null) { Debug.LogWarning("[OcclusionCulling] No system in scene."); return; }
            DestroyImmediate(system);
            Debug.Log("[OcclusionCulling] System removed.");
        }

        private void OnGUI()
        {
            GUILayout.Label("Occlusion Culling Setup", EditorStyles.boldLabel);
            GUILayout.Space(8);

            _onlyStatic = EditorGUILayout.Toggle("Static Objects Only", _onlyStatic);
            _skipParticles = EditorGUILayout.Toggle("Skip Particles/Effects", _skipParticles);
            GUILayout.Space(8);

            var system = FindObjectOfType<OcclusionCullingSystem>();
            if (system != null)
                EditorGUILayout.HelpBox($"System active — {system.OccludeeCount} registered", MessageType.Info);

            GUILayout.Space(8);

            if (GUILayout.Button("Scan & Register", GUILayout.Height(36)))
            {
                var sys = FindOrCreateSystem();
                if (sys == null) return;

                Undo.RecordObject(sys, "Scan Occludees");

                int count = 0;
                foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                    count += ScanHierarchy(go, sys, _onlyStatic, _skipParticles);

                EditorUtility.SetDirty(sys);
                Debug.Log($"[OcclusionCulling] Registered {count} renderers.");
            }
        }

        // ---- helpers ----

        private static OcclusionCullingSystem FindOrCreateSystem()
        {
            var sys = FindObjectOfType<OcclusionCullingSystem>();
            if (sys != null) return sys;

            var go = new GameObject("OcclusionCullingSystem");
            Undo.RegisterCreatedObjectUndo(go, "Create OcclusionCullingSystem");
            sys = go.AddComponent<OcclusionCullingSystem>();
            Debug.Log("[OcclusionCulling] Created system GameObject.");
            return sys;
        }

        private static int ScanHierarchy(GameObject root, OcclusionCullingSystem system,
            bool onlyStatic, bool skipParticles)
        {
            int count = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer && skipParticles) continue;
                if (onlyStatic && !r.gameObject.isStatic) continue;

                system.Register(r);
                count++;
            }
            return count;
        }
    }
}
