using UnityEngine;

[CreateAssetMenu(menuName = "SSSS/Profile Set")]
public class SSSSProfileSet : ScriptableObject
{
    [System.Serializable]
    public struct Entry
    {
        public SSSSProfile profile;
        [Range(0, 255)]
        public int stencilRef;
    }

    public Entry[] entries;
}
