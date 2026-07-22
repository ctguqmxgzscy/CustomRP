// Grass Prototype Data — one ScriptableObject per terrain detail prototype.
// Holds mesh, material, and rendering settings. Editor creates these from terrain data.
// Pattern follows ToyRenderPipeline InstanceData.

using UnityEngine;

namespace GPUDrivenGrass
{
    [CreateAssetMenu(menuName = "Grass/Prototype Data")]
    public class GrassPrototypeData : ScriptableObject
    {
        [Header("Mesh & Material")]
        public Mesh     mesh;
        public Material material;

        [Header("Density (read from terrain, editable)")]
        [Range(0f, 2f)] public float densityMultiplier = 1f;

        [Header("Blade Shape")]
        [Min(0.01f)] public float minWidth  = 0.05f;
        [Min(0.01f)] public float maxWidth  = 0.15f;
        [Min(0.01f)] public float minHeight = 0.3f;
        [Min(0.01f)] public float maxHeight = 0.8f;
        [Range(0f, 1f)] public float noiseSpread = 0.1f;

        [Header("Rendering")]
        public bool isVertexLit; // true = mesh proto, false = billboard blade

        [Header("Runtime (editor sets these)")]
        [HideInInspector] public int terrainProtoIndex; // which terrain detail proto this maps to
        [HideInInspector] public int densityWidth;
        [HideInInspector] public int densityHeight;
    }
}
