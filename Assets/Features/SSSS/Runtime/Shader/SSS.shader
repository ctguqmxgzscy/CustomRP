Shader "Hidden/SSS"
{
    Properties
    {
        [HideInInspector] _StencilRef("SSS Stencil Ref", Int) = 1
    }

    SubShader
    {
        ZWrite Off
        ZTest Always
        Cull Off

        // ── Pass 0: X-Blur ─────────────────────────────────────────────────
        Pass
        {
            Name "SSS XBlur"
            
            Stencil
            {
                Ref [_StencilRef]
                Comp Equal
                Pass Keep
            }
            
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragX
            #include "SSSBlur.hlsl"

            float4 FragX(Varyings i) : SV_Target
            {
                return SSSBlur(i.texcoord, float2(1, 0));
            }
            ENDHLSL
        }

        // ── Pass 1: Y-Blur ─────────────────────────────────────────────────
        Pass
        {
            Name "SSS YBlur"
            
            Stencil
            {
                Ref [_StencilRef]
                Comp Equal     // 只处理 Stencil == 1 的像素
                Pass Keep
            }
            
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragY
            #include "SSSBlur.hlsl"

            float4 FragY(Varyings i) : SV_Target
            {
                return SSSBlur(i.texcoord, float2(0, 1));
            }
            ENDHLSL
        }

        // ── Pass 2: Composite ──────────────────────────────────────────────
        Pass
        {
            Name "SSS Composite"
            
            Stencil
            {
                Ref [_StencilRef]
                Comp Equal     // 只处理 Stencil == 1 的像素
                Pass Keep
            }
                        
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            
            TEXTURE2D(_SkinDiffuseRT);  SAMPLER(sampler_SkinDiffuseRT);
            TEXTURE2D(_SkinSpecularRT); SAMPLER(sampler_SkinSpecularRT);
            TEXTURE2D(_SkinAmbientRT);  SAMPLER(sampler_SkinAmbientRT);

            float4 FragComposite(Varyings i) : SV_Target
            {
                float3 diffuse  = SAMPLE_TEXTURE2D(_SkinDiffuseRT,  sampler_SkinDiffuseRT,  i.texcoord).rgb;
                float3 specular = SAMPLE_TEXTURE2D(_SkinSpecularRT, sampler_SkinSpecularRT, i.texcoord).rgb;
                float3 ambient  = SAMPLE_TEXTURE2D(_SkinAmbientRT,  sampler_SkinAmbientRT,  i.texcoord).rgb;
                return float4(diffuse + specular + ambient, 1);
            }
            ENDHLSL
        }
    }
}