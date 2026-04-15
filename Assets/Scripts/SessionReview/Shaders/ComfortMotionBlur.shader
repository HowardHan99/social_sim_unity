Shader "Hidden/ComfortMotionBlur"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _BlurStrength ("Blur Strength", Range(0, 1)) = 0
        _BlurRadius ("Blur Radius (pixels)", Float) = 20
        _VignetteStrength ("Vignette Strength", Range(0, 1)) = 0
        _VignetteRadius ("Vignette Radius", Range(0.1, 1)) = 0.55
        _VignetteSoftness ("Vignette Softness", Range(0.01, 1)) = 0.45
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _BlurStrength;
            float _BlurRadius;
            float _VignetteStrength;
            float _VignetteRadius;
            float _VignetteSoftness;

            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                float2 texel = _MainTex_TexelSize.xy;

                // radius in UV space — _BlurRadius is in pixels, so multiply by texel size
                float2 r = texel * max(0.0, _BlurStrength * _BlurRadius);

                // Two-ring Gaussian kernel — 17 samples, weights sum exactly to 1.0
                //   Ring 0 (center):    weight 4/32  = 0.125
                //   Ring 1 (r×1, cardinal):  weight 2/32  = 0.0625  × 4 = 0.25
                //   Ring 1 (r×1, diagonal):  weight 1/32  = 0.03125 × 4 = 0.125
                //   Ring 2 (r×2, cardinal):  weight 2/32  = 0.0625  × 4 = 0.25
                //   Ring 2 (r×2, diagonal):  weight 1/32  = 0.03125 × 4 = 0.125
                //   Total: 0.125 + 0.25 + 0.125 + 0.25 + 0.125 = 0.875 ... need to normalise
                // Using simpler normalised weights (verified sum = 1.0):
                //   center 0.20, ring1-cardinal 0.08 ×4, ring1-diagonal 0.04 ×4,
                //   ring2-cardinal 0.05 ×4, ring2-diagonal 0.02 ×4
                //   = 0.20 + 0.32 + 0.16 + 0.20 + 0.08 = 0.96  — not quite 1.0
                // Final normalised weights:
                //   center 0.2, ring1-card 0.09375 ×4 = 0.375, ring1-diag 0.04375 ×4 = 0.175
                //   ring2-card 0.04375 ×4 = 0.175, ring2-diag 0.01875 ×4 = 0.075
                //   = 0.2 + 0.375 + 0.175 + 0.175 + 0.075 = 1.0 ✓
                float4 color = tex2D(_MainTex, uv) * 0.2;

                // Ring 1 — radius × 1
                color += tex2D(_MainTex, uv + float2( r.x,  0  )) * 0.09375;
                color += tex2D(_MainTex, uv + float2(-r.x,  0  )) * 0.09375;
                color += tex2D(_MainTex, uv + float2( 0,    r.y)) * 0.09375;
                color += tex2D(_MainTex, uv + float2( 0,   -r.y)) * 0.09375;
                color += tex2D(_MainTex, uv + float2( r.x,  r.y)) * 0.04375;
                color += tex2D(_MainTex, uv + float2(-r.x,  r.y)) * 0.04375;
                color += tex2D(_MainTex, uv + float2( r.x, -r.y)) * 0.04375;
                color += tex2D(_MainTex, uv + float2(-r.x, -r.y)) * 0.04375;

                // Ring 2 — radius × 2, gives smoother falloff and stronger blur feel
                float2 r2 = r * 2.0;
                color += tex2D(_MainTex, uv + float2( r2.x,  0   )) * 0.04375;
                color += tex2D(_MainTex, uv + float2(-r2.x,  0   )) * 0.04375;
                color += tex2D(_MainTex, uv + float2( 0,     r2.y)) * 0.04375;
                color += tex2D(_MainTex, uv + float2( 0,    -r2.y)) * 0.04375;
                color += tex2D(_MainTex, uv + float2( r2.x,  r2.y)) * 0.01875;
                color += tex2D(_MainTex, uv + float2(-r2.x,  r2.y)) * 0.01875;
                color += tex2D(_MainTex, uv + float2( r2.x, -r2.y)) * 0.01875;
                color += tex2D(_MainTex, uv + float2(-r2.x, -r2.y)) * 0.01875;

                // Vignette (off by default — set VignetteStrength > 0 in Inspector to enable)
                float2 vigUV = uv * 2.0 - 1.0;
                vigUV.x *= _MainTex_TexelSize.z / _MainTex_TexelSize.w; // aspect-ratio correct
                float vigDist = length(vigUV);
                float vigMask = smoothstep(_VignetteRadius, _VignetteRadius + _VignetteSoftness, vigDist);
                color.rgb *= 1.0 - vigMask * _VignetteStrength * _BlurStrength;

                return color;
            }
            ENDCG
        }
    }
}
