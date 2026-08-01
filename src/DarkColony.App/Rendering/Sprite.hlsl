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
SamplerState PointSampler : register(s0);

PSInput VSMain(VSInput input)
{
    PSInput result;
    result.Position = float4(input.Position, 1.0f);
    result.Texcoord = input.Texcoord;
    return result;
}

float4 PSMain(PSInput input) : SV_TARGET
{
    return SpriteTexture.Sample(PointSampler, input.Texcoord);
}
