// Editor tool: reads terrain detail prototypes and generates GrassPrototypeData assets.
// Select a GrassIndirectRenderer and click "Generate Prototype Assets" in the inspector.

using System.IO;
using UnityEditor;
using UnityEngine;

namespace GPUDrivenGrass
{
    [CustomEditor(typeof(GrassIndirectRenderer))]
    public class GrassPrototypeDataGenerator : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Prototype Generation", EditorStyles.boldLabel);

            if (GUILayout.Button("Generate Prototype Assets from Terrain", GUILayout.Height(30)))
                GenerateAssets((GrassIndirectRenderer)target);

            var renderer = (GrassIndirectRenderer)target;
            if (renderer.m_Prototypes != null && renderer.m_Prototypes.Count > 0)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField($"Protos: {renderer.m_Prototypes.Count}");
            }
        }

        private static void GenerateAssets(GrassIndirectRenderer renderer)
        {
            var terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0)
            {
                Debug.LogWarning("[Grass] No active terrains in scene.");
                return;
            }

            // Determine output path (next to renderer prefab or in Assets/)
            var rendererPath = AssetDatabase.GetAssetPath(renderer);
            var outputDir = string.IsNullOrEmpty(rendererPath)
                ? "Assets/GrassPrototypes"
                : Path.GetDirectoryName(rendererPath) + "/Prototypes";
            Directory.CreateDirectory(outputDir);

            var existingProtos = renderer.m_Prototypes;
            var newList = new System.Collections.Generic.List<GrassPrototypeData>();

            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                var td = t.terrainData;
                var prototypes = td.detailPrototypes;
                if (prototypes == null) continue;

                for (int p = 0; p < prototypes.Length; p++)
                {
                    var dp = prototypes[p];
                    var assetPath = $"{outputDir}/GrassProto_{t.name}_{p}.asset";

                    // Try to find existing asset or create new
                    var data = AssetDatabase.LoadAssetAtPath<GrassPrototypeData>(assetPath);
                    if (data == null)
                    {
                        data = CreateInstance<GrassPrototypeData>();
                        AssetDatabase.CreateAsset(data, assetPath);
                    }

                    // Fill from detail prototype
                    data.isVertexLit       = dp.renderMode == DetailRenderMode.VertexLit;
                    data.densityWidth      = td.detailWidth;
                    data.densityHeight     = td.detailHeight;
                    data.terrainProtoIndex = p;
                    data.minWidth          = dp.minWidth;
                    data.maxWidth          = dp.maxWidth;
                    data.minHeight         = dp.minHeight;
                    data.maxHeight         = dp.maxHeight;
                    data.noiseSpread       = dp.noiseSpread;

                    // Mesh + material from prefab (VertexLit) or fallback
                    if (dp.usePrototypeMesh && dp.prototype != null)
                    {
                        var mf = dp.prototype.GetComponent<MeshFilter>();
                        var mr = dp.prototype.GetComponent<MeshRenderer>();
                        data.mesh     = mf != null ? mf.sharedMesh : null;
                        data.material = mr != null ? mr.sharedMaterial : null;
                    }
                    else
                    {
                        data.mesh     = null; // uses fallback quad
                        data.material = null; // uses fallback blade material
                    }

                    EditorUtility.SetDirty(data);
                    newList.Add(data);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            renderer.m_Prototypes = newList;
            EditorUtility.SetDirty(renderer);

            Debug.Log($"[Grass] Generated {newList.Count} prototype assets to {outputDir}");
        }
    }
}
