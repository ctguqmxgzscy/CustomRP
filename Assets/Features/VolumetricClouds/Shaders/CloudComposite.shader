// Cloud Composite — 将体积云结果合成到场景颜色
// _BlitTexture = 场景颜色（已拷贝），_CloudTarget = 体积云 (RGB=散射色, A=不透明度)
// 输出 lerp(scene, cloud.rgb, cloud.a)
//
// 顶点阶段复用 Blit.hlsl 官方 Vert（GetFullScreenTriangleTexCoord 处理 DX y 翻转），
// 禁止手写 uv = positionCS.xy*0.5+0.5 —— 否则合成结果上下颠倒

Shader "Custom/CloudComposite"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_CloudTarget);
            SAMPLER(sampler_CloudTarget);

            float4 Frag(Varyings input) : SV_Target
            {
                float4 scene = FragBlit(input, sampler_LinearClamp);
                float4 cloud = SAMPLE_TEXTURE2D(_CloudTarget, sampler_CloudTarget, input.texcoord);
                return float4(lerp(scene.rgb, cloud.rgb, cloud.a), 1.0);
            }
            ENDHLSL
        }
    }
}
