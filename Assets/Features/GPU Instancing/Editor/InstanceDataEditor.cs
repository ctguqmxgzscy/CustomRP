using UnityEditor;
using UnityEngine;

namespace GPUDriven
{
    [CustomEditor(typeof(InstanceData))]
    public class InstanceDataEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            var script = (InstanceData)target;
            if (GUILayout.Button("Generate data randomly", GUILayout.Height(40))) script.GenerateRandomData();
        }
    }
}