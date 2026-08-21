sampler2D inputSampler : register(s0);

float Brightness : register(c0);
float Contrast : register(c1);
float Gamma : register(c2);
float Saturation : register(c3);
float Temperature : register(c4);
float Tint : register(c5);
float Vignette : register(c6);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 src = tex2D(inputSampler, uv);
    float3 col = src.rgb;
    
    // 1. Exposure & Brightness
    col += Brightness;
    
    // 2. Contrast
    col = (col - 0.5) * Contrast + 0.5;
    
    // 3. Gamma
    col = max(col, 0.0001);
    if (Gamma > 0.05)
    {
        col = pow(col, 1.0 / Gamma);
    }
    
    // 4. True Luminance-preserving Saturation (ITU-R BT.709)
    float luma = dot(col, float3(0.2126, 0.7152, 0.0722));
    col = lerp(float3(luma, luma, luma), col, Saturation);
    
    // 5. Temperature (Warmth) & Tint
    col.r += Temperature * 0.18;
    col.b -= Temperature * 0.18;
    col.g -= Tint * 0.12;
    col.r += Tint * 0.06;
    col.b += Tint * 0.06;
    
    // 6. Vignette
    if (Vignette > 0.001)
    {
        float2 coord = (uv - 0.5) * 2.0;
        float dist = length(coord);
        float vig = 1.0 - smoothstep(0.5, 1.414, dist) * Vignette;
        col *= vig;
    }
    
    col = saturate(col);
    return float4(col, src.a);
}
