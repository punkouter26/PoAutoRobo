using ComputeSharp;
using ComputeSharp.D2D1;

namespace PoAutoRobo.App.Views;

/// <summary>
/// The topic page's backdrop: slow folds of light in the accent colour, worked out afresh for every point of every
/// frame on the graphics card. Written here in C# and turned into the graphics card's own language when the app is
/// built, so no shader file is shipped.
/// </summary>
/// <param name="time">Seconds since the page appeared.</param>
/// <param name="size">The backdrop's size in pixels.</param>
/// <param name="accent">The colour of the light, each part 0 to 1.</param>
/// <param name="energy">0 at rest, 1 while stories are being fetched: the folds brighten and quicken.</param>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct Nebula(float time, float2 size, float3 accent, float energy) : ID2D1PixelShader
{
    public float4 Execute()
    {
        // The same scale in both directions, so the folds are not stretched by the window's shape.
        float2 p = D2D.GetScenePosition().XY / size.Y * 2.4f;
        float t = time * (0.05f + 0.07f * energy);

        // Noise bent by noise, twice over: the first bend gives the folds, the second the wisps inside them.
        float2 q = new(Cloud(p + new float2(0f, t)), Cloud(p + new float2(5.2f, 1.3f - t)));
        float2 r = new(Cloud(p + 3.2f * q + new float2(1.7f - t, 9.2f)), Cloud(p + 3.2f * q + new float2(8.3f, 2.8f + t)));
        float folds = Cloud(p + 2.6f * r);

        // The colour leans towards its neighbour on the wheel where the folds run deepest, so it is never one flat tint.
        float3 colour = Hlsl.Lerp(accent, accent.ZXY, Hlsl.Saturate(r.X * 0.7f));
        float light = Hlsl.Saturate(folds * folds * 1.9f + 0.25f * Hlsl.Length(q) - 0.18f) * (0.24f + 0.20f * energy);

        // Fades out towards the left edge, where the page's form begins.
        light *= Hlsl.SmoothStep(0f, 0.25f, D2D.GetScenePosition().X / size.X);
        return new float4(colour * light, light); // light and colour multiplied together, as the drawing surface expects
    }

    private static float Hash(float2 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p, new float2(127.1f, 311.7f))) * 43758.5453f);

    private static float Noise(float2 p)
    {
        float2 cell = Hlsl.Floor(p);
        float2 within = Hlsl.Frac(p);
        float2 smooth = within * within * (3f - 2f * within);
        return Hlsl.Lerp(
            Hlsl.Lerp(Hash(cell), Hash(cell + new float2(1f, 0f)), smooth.X),
            Hlsl.Lerp(Hash(cell + new float2(0f, 1f)), Hash(cell + new float2(1f, 1f)), smooth.X),
            smooth.Y);
    }

    /// <summary>Five layers of noise, each twice as fine and half as strong as the last.</summary>
    private static float Cloud(float2 p)
    {
        float sum = 0f;
        float strength = 0.5f;
        for (int layer = 0; layer < 5; layer++)
        {
            sum += strength * Noise(p);
            p *= 2.02f;
            strength *= 0.5f;
        }
        return sum;
    }
}
