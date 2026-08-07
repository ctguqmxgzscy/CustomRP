using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DisallowMultipleRendererFeature("SSSS")]
public class SSSSPassFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {        
        [Range(0, 10)]
        public float scaler;
        public LayerMask sssLayerMask = -1;
        public SSSSProfileSet profileSet;
    }
    
    class CustomRenderPass : ScriptableRenderPass
    {
        private RTHandle _diffuseRT;
        private RTHandle _rtSpecular;
        private RTHandle _rtAmbient;
        private RTHandle _rtBlurTemp;

        private Shader _shader;
        private Dictionary<int, Material> _materials = new Dictionary<int, Material>();

        private readonly int _rtDiffuseID   = Shader.PropertyToID("_SkinDiffuseRT"),
            _rtSpecularID  = Shader.PropertyToID("_SkinSpecularRT"),
            _rtAmbientID   = Shader.PropertyToID("_SkinAmbientRT"),
            _sssScaleID    = Shader.PropertyToID("_SSSScale"),
            _kernelID      = Shader.PropertyToID("_Kernel"),
            _stencilRefID  = Shader.PropertyToID("_StencilRef");
        
        private Settings _settings;        
        private FilteringSettings _filteringSettings;
        private readonly ShaderTagId _skinTag = new ShaderTagId("Skin MRT");
        internal enum ShaderPass
        {
            SSSBlurX = 0,    
            SSSBlurY = 1,  
            Composite,
        }
        
        private List<Vector4> _kernels = new List<Vector4>(); 
        
        const string ProfilerTag = "Seperable-Subsurface-Scattering";
        private readonly ProfilingSampler _sampler = new(ProfilerTag);
        
        public CustomRenderPass(Settings settings, Shader shader)
        {
            _shader = shader;
            _settings = settings;
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, settings.sssLayerMask);
        }

        public void UpdateParams(Settings settings)
        {
            _settings = settings;
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, settings.sssLayerMask);
        }
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;

            // 分配三张 RT：漫反射、镜面反射、环境光
            RenderingUtils.ReAllocateIfNeeded(ref _diffuseRT,  desc, name: "_SkinDiffuseRT");
            // 中间 RT：水平 blur 结果暂存
            RenderingUtils.ReAllocateIfNeeded(ref _rtBlurTemp, desc, FilterMode.Bilinear, name: "_SSSBlurTempRT");

            // 环境光 + 镜面反射 RT 使用相机格式（RGB），保留颜色信息
            var colorDesc = renderingData.cameraData.cameraTargetDescriptor;
            colorDesc.depthBufferBits = 0;
            RenderingUtils.ReAllocateIfNeeded(ref _rtSpecular, colorDesc, name: "_SkinSpecularRT");
            RenderingUtils.ReAllocateIfNeeded(ref _rtAmbient,  colorDesc, name: "_SkinAmbientRT");

            // 把 RT 设置为全局纹理，方便后续 Blur Pass 采样
            cmd.SetGlobalTexture(_rtDiffuseID,  _diffuseRT);
            cmd.SetGlobalTexture(_rtSpecularID, _rtSpecular);
            cmd.SetGlobalTexture(_rtAmbientID,  _rtAmbient);
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        { 
            CommandBuffer cmd = CommandBufferPool.Get();
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            
            using (new ProfilingScope(cmd, _sampler))
            {
                var depthAttachment = renderingData.cameraData.renderer.cameraDepthTargetHandle;
            
                RenderTargetIdentifier[] mrts =
                {
                    _diffuseRT.nameID,
                    _rtSpecular.nameID,
                    _rtAmbient.nameID
                };

                cmd.SetRenderTarget(mrts, depthAttachment);
                cmd.ClearRenderTarget(false, true, Color.clear);

                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                DrawPass(context, ref renderingData, _skinTag);

                // 每帧清理 blur 暂存 RT，避免物体位移后旧帧残留数据被 kernel 采样
                CoreUtils.SetRenderTarget(cmd, _rtBlurTemp, depthAttachment);
                cmd.ClearRenderTarget(false, true, Color.clear);

                SSSSProfileSet profileSet = _settings.profileSet;
                var entries = profileSet != null ? profileSet.entries : null;
                if (entries == null || entries.Length == 0)
                {
                    // fallback: single skin profile at stencil 1
                    var fallbackMat = GetMaterial(1);
                    CalcAndSetKernel(null);
                    cmd.SetGlobalVectorArray(_kernelID, _kernels);
                    cmd.SetGlobalFloat(_sssScaleID, _settings.scaler);

                    CoreUtils.SetRenderTarget(cmd, _rtBlurTemp, depthAttachment);
                    Blitter.BlitTexture(cmd, _diffuseRT,
                        new Vector4(1, 1, 0, 0), fallbackMat, (int)ShaderPass.SSSBlurX);

                    CoreUtils.SetRenderTarget(cmd, _diffuseRT, depthAttachment);
                    Blitter.BlitTexture(cmd, _rtBlurTemp,
                        new Vector4(1, 1, 0, 0), fallbackMat, (int)ShaderPass.SSSBlurY);

                    cmd.SetGlobalTexture(_rtDiffuseID,  _diffuseRT);
                    cmd.SetGlobalTexture(_rtSpecularID, _rtSpecular);

                    var cameraColor = renderingData.cameraData.renderer.cameraColorTargetHandle;
                    CoreUtils.SetRenderTarget(cmd, cameraColor, depthAttachment);
                    Blitter.BlitTexture(cmd, _diffuseRT,
                        new Vector4(1, 1, 0, 0), fallbackMat, (int)ShaderPass.Composite);
                }
                else
                {
                    var cameraColor = renderingData.cameraData.renderer.cameraColorTargetHandle;
                    for (int i = 0; i < entries.Length; i++)
                    {
                        if (entries[i].profile == null) continue;

                        var mat = GetMaterial(entries[i].stencilRef);
                        CalcAndSetKernel(entries[i].profile);
                        cmd.SetGlobalVectorArray(_kernelID, _kernels);
                        cmd.SetGlobalFloat(_sssScaleID, _settings.scaler);

                        CoreUtils.SetRenderTarget(cmd, _rtBlurTemp, depthAttachment);
                        Blitter.BlitTexture(cmd, _diffuseRT,
                            new Vector4(1, 1, 0, 0), mat, (int)ShaderPass.SSSBlurX);

                        CoreUtils.SetRenderTarget(cmd, _diffuseRT, depthAttachment);
                        Blitter.BlitTexture(cmd, _rtBlurTemp,
                            new Vector4(1, 1, 0, 0), mat, (int)ShaderPass.SSSBlurY);

                        cmd.SetGlobalTexture(_rtDiffuseID,  _diffuseRT);
                        cmd.SetGlobalTexture(_rtSpecularID, _rtSpecular);
                        CoreUtils.SetRenderTarget(cmd, cameraColor, depthAttachment);
                        Blitter.BlitTexture(cmd, _diffuseRT,
                            new Vector4(1, 1, 0, 0), mat, (int)ShaderPass.Composite);
                    }
                }
            }
            
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
        
        void CalcAndSetKernel(SSSSProfile profile)
        {
            Color mc = profile != null ? profile.mainColor : new Color(1.0f, 0.31f, 0.20f);
            Color fo = profile != null ? profile.falloff  : new Color(1.0f, 0.51f, 0.29f);
            Vector3 sssColor   = Vector3.Normalize(new Vector3(mc.r, mc.g, mc.b));
            Vector3 sssFallOff = Vector3.Normalize(new Vector3(fo.r, fo.g, fo.b));

            if (profile != null && profile.useDualScale)
            {
                KernelCalculate.CalculateKernel(_kernels, 25, sssColor, sssFallOff,
                    profile.nearScatterScale, profile.farScatterScale, profile.nearFarBalance);
            }
            else
            {
                float sc = profile != null ? profile.scatterScale : 1.0f;
                KernelCalculate.CalculateKernel(_kernels, 25, sssColor, sssFallOff, sc);
            }
        }

        Material GetMaterial(int stencilRef)
        {
            if (!_materials.TryGetValue(stencilRef, out var mat) || mat == null)
            {
                mat = CoreUtils.CreateEngineMaterial(_shader);
                mat.SetInt(_stencilRefID, stencilRef);
                _materials[stencilRef] = mat;
            }
            return mat;
        }

        public void Dispose()
        {
            _diffuseRT?.Release();
            _rtSpecular?.Release();
            _rtAmbient?.Release();
            _rtBlurTemp?.Release();
            foreach (var mat in _materials.Values)
                CoreUtils.Destroy(mat);
            _materials.Clear();
        }
        
        private void DrawPass(ScriptableRenderContext context, ref RenderingData renderingData, ShaderTagId tagId)
        {
            var drawingSettings = CreateDrawingSettings(tagId, ref renderingData, SortingCriteria.CommonOpaque);
            context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref _filteringSettings);
        }
    }

    public Shader sssShader;    
    public Settings settings = new Settings();
    public RenderPassEvent renderPassEvent;
    
    private CustomRenderPass _renderPass;

    public override void Create()
    {
        if (_renderPass == null)
        {
            _renderPass = new CustomRenderPass(settings, sssShader);
        }
        else
        {
            _renderPass.UpdateParams(settings);
        }

        _renderPass.renderPassEvent = renderPassEvent;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if(!ShouldRender(in renderingData))
            return;
        renderer.EnqueuePass(_renderPass);
    }   
    
    protected override void Dispose(bool disposing)
    {
        _renderPass?.Dispose();
    }    
    
    bool ShouldRender(in RenderingData data)
    {
        if(!data.cameraData.postProcessEnabled || data.cameraData.cameraType != CameraType.Game)
        {
            return false;
        }

        if (sssShader == null)
        {
            Debug.Log("SSS shader is null!");
            return false;
        }

        if(_renderPass == null)
        {
            Debug.Log("RenderPass is null!");
            return false;
        }

        return true;
    }
}