struct VSInput
{
    float3 Position : POSITION;
    float2 Texcoord : TEXCOORD0;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 Texcoord : TEXCOORD0;
};

Texture2D<float4> SpriteTexture : register(t0);
SamplerState SpriteSampler : register(s0);

// Sharp-bilinear placement of the finished frame (DisplayLayout).
cbuffer Scaling : register(b0)
{
    float2 SourceSize;
    float2 Prescale;
};

// The sprite batch: quads in pixels of the frame being drawn.
cbuffer Target : register(b1)
{
    float2 TargetSize;
    float2 TargetPadding;
};

struct BatchInput
{
    float2 Position : POSITION;
    float2 Texcoord : TEXCOORD0;
};

PSInput VSBatch(BatchInput input)
{
    PSInput result;
    result.Position = float4(input.Position.x / TargetSize.x * 2.0f - 1.0f, 1.0f - input.Position.y / TargetSize.y * 2.0f, 0.0f, 1.0f);
    result.Texcoord = input.Texcoord;
    return result;
}

PSInput VSMain(VSInput input)
{
    PSInput result;
    result.Position = float4(input.Position, 1.0f);
    result.Texcoord = input.Texcoord;
    return result;
}

float4 PSMain(PSInput input) : SV_TARGET
{
    return SpriteTexture.Sample(SpriteSampler, input.Texcoord);
}

// Enlarges every source pixel by the whole Prescale with point sampling and
// lets the linear sampler blend only the strip where two enlarged pixels
// meet, so a non-whole scale keeps even, sharp pixels.
float4 PSSharpBilinear(PSInput input) : SV_TARGET
{
    float2 texel = input.Texcoord * SourceSize;
    float2 texelFloored = floor(texel);
    float2 centreDistance = frac(texel) - 0.5f;
    float2 regionRange = 0.5f - 0.5f / Prescale;
    float2 offset = (centreDistance - clamp(centreDistance, -regionRange, regionRange)) * Prescale + 0.5f;
    return SpriteTexture.Sample(SpriteSampler, (texelFloored + offset) / SourceSize);
}
