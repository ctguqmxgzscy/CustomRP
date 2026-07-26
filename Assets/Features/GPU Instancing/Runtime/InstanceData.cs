// Grass Prototype Data — one ScriptableObject per terrain detail prototype.
// Holds mesh, material, and rendering settings. Editor creates these from terrain data.
// Pattern follows ToyRenderPipeline InstanceData.

using UnityEngine;

namespace GPUDriven
{
    [CreateAssetMenu(menuName = "GPU Instancing/Prototype Data")]
    public class InstanceData : ScriptableObject
    {
        [Header("Mesh & Material")] public Mesh mesh;

        public Material material;

        [HideInInspector] public Matrix4x4[] mats; // 变换矩阵         
        [HideInInspector] public int subMeshIndex;
        [HideInInspector] public int instanceCount;

        [Header("Density (read from terrain, editable)")] [Range(0f, 2f)]
        public float densityMultiplier = 1f;

        [Header("Blade Shape")] [Min(0.01f)] public float minWidth = 0.05f;

        [Min(0.01f)] public float maxWidth = 0.15f;
        [Min(0.01f)] public float minHeight = 0.3f;
        [Min(0.01f)] public float maxHeight = 0.8f;
        [Range(0f, 1f)] public float noiseSpread = 0.1f;

        [Header("Rendering")] public bool isVertexLit; // true = mesh proto, false = billboard blade

        [Header("Runtime (editor sets these)")] [HideInInspector]
        public int terrainProtoIndex; // which terrain detail proto this maps to

        [HideInInspector] public int densityWidth;
        [HideInInspector] public int densityHeight;
        [HideInInspector] public int capacity; // buffer 容量（max instances）

        public Vector3 center = new(0, 0, 0);
        public int randomInstanceNum = 5000;
        public float distanceMin = 5.0f;
        public float distanceMax = 50.0f;
        public float heightMin = -0.5f;
        public float heightMax = 0.5f;
        public ComputeBuffer argsBuffer; // 绘制参数                     （Runtime）
        public ComputeBuffer genCounter; // GPU gen 计数                  （Runtime）
        public ComputeBuffer matrixBuffer; // 全部实体的变换矩阵            （Runtime）
        public ComputeBuffer validMatrixBuffer; // 剔除后剩余instance的变换矩阵  （Runtime）

        // 随机生成
        public void GenerateRandomData()
        {
            instanceCount = randomInstanceNum;

            // 生成变换矩阵
            mats = new Matrix4x4[instanceCount];
            for (var i = 0; i < instanceCount; i++)
            {
                var angle = Random.Range(0.0f, Mathf.PI * 2.0f);
                var distance = Mathf.Sqrt(Random.Range(0.0f, 1.0f)) * (distanceMax - distanceMin) + distanceMin;
                var height = Random.Range(heightMin, heightMax);

                var pos = new Vector3(Mathf.Sin(angle) * distance, height, Mathf.Cos(angle) * distance);
                var dir = pos - center;

                var q = new Quaternion();
                q.SetLookRotation(dir, new Vector3(0, 1, 0));

                var m = Matrix4x4.Rotate(q);
                m.SetColumn(3, new Vector4(pos.x, pos.y, pos.z, 1));

                mats[i] = m;
            }

            matrixBuffer?.Release();
            matrixBuffer = null;
            validMatrixBuffer?.Release();
            validMatrixBuffer = null;
            argsBuffer?.Release();
            argsBuffer = null;

            Debug.Log("Instance Data Generate Success");
        }
    }
}