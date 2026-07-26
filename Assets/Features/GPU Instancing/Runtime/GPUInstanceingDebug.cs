using GPUDriven;
using UnityEngine;

[ExecuteAlways]
public class InstanceDebug : MonoBehaviour
{
    public InstanceData idata;
    public ComputeShader cs;
    public Camera camera;

    public bool usingCulling;

    // Update is called once per frame
    private void Update()
    {
        if (camera == null) camera = Camera.main;
        if (cs == null) cs = FindComputeShader("InstanceCulling");

        if (usingCulling)
            GPUInstancingDrawer.Draw(idata, camera, cs);
        else
            GPUInstancingDrawer.Draw(idata);
    }

    private ComputeShader FindComputeShader(string shaderName)
    {
        var css = Resources.FindObjectsOfTypeAll(typeof(ComputeShader)) as ComputeShader[];
        for (var i = 0; i < css.Length; i++)
            if (css[i].name == shaderName)
                return css[i];
        return null;
    }
}