using ComputeSharp;
using ComputeSharp.D2D1;

namespace PoAutoRobo.App.Views;

// The app's other pictures worked out on the graphics card, like the Nebula: written in C# and turned into the card's
// own language when the app is built. Positions are in pixels of what is being drawn.

/// <summary>
/// A clip card's picture under a lens of glass that follows the pointer: the picture swells beneath it, its colours
/// part a little towards the rim as real glass splits light, and a highlight sits up and to the left where the light
/// catches the glass.
/// </summary>
/// <param name="pointer">Where the pointer is.</param>
/// <param name="size">The picture's size.</param>
/// <param name="radius">How wide the lens is.</param>
/// <param name="strength">0 for plain glass, which changes nothing, up to 1.</param>
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct Lens(float2 pointer, float2 size, float radius, float strength) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 position = D2D.GetScenePosition().XY;
        float2 away = position - pointer;
        float reach = Hlsl.Length(away) / radius;

        // Inside the lens each point shows a point nearer its middle, so the picture is magnified, most at the centre.
        float bend = Hlsl.Saturate(1f - reach * reach) * 0.35f * strength;
        float2 seen = position - away * bend;
        float fringe = bend * (1f - reach) * 0.5f; // strongest part way out, where real glass splits light most
        float4 red = D2D.SampleInputAtPosition(0, Inside(pointer + (seen - pointer) * (1f + fringe), size));
        float4 middle = D2D.SampleInputAtPosition(0, Inside(seen, size));
        float4 blue = D2D.SampleInputAtPosition(0, Inside(pointer + (seen - pointer) * (1f - fringe), size));

        // A soft highlight up and to the left of the pointer, and a thin bright rim round the glass.
        float shine = Hlsl.Pow(Hlsl.Saturate(1f - Hlsl.Length(away + radius * 0.35f) / (radius * 0.5f)), 2f) * 0.3f;
        float rim = Hlsl.SmoothStep(0.86f, 1f, reach) * (1f - Hlsl.SmoothStep(1f, 1.08f, reach)) * 0.22f;
        float3 colour = new float3(red.X, middle.Y, blue.Z) + (shine + rim) * strength * middle.W;
        return new float4(colour, middle.W);
    }

    // Never past the picture's edge, where there is nothing to show.
    private static float2 Inside(float2 position, float2 size) => Hlsl.Clamp(position, new float2(0.5f, 0.5f), size - 0.5f);
}

/// <summary>
/// The voice made visible while a clip is auditioned: ribbons of light across the panel, swelling where the voice is
/// strong in that part of its range, from its low notes on the left to its high ones on the right.
/// </summary>
/// <param name="time">Seconds since it began.</param>
/// <param name="size">The panel's size.</param>
/// <param name="accent">The colour of the light, each part 0 to 1.</param>
/// <param name="bands0">Sixteen strengths of the voice, 0 to 1, from its lowest band of pitch to its highest, four to a value.</param>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct Aurora(float time, float2 size, float3 accent, float4 bands0, float4 bands1, float4 bands2, float4 bands3) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 uv = D2D.GetScenePosition().XY / size;

        // How strong the voice is here, eased between the bands on either side so the ribbons bend smoothly.
        float place = Hlsl.Saturate(uv.X) * 15f;
        int band = (int)Hlsl.Floor(place);
        float within = Hlsl.SmoothStep(0f, 1f, place - band);
        float strength = Hlsl.Lerp(Pick(bands0, bands1, bands2, bands3, band), Pick(bands0, bands1, bands2, bands3, Hlsl.Min(band + 1, 15)), within);

        // Three ribbons, each a wave travelling at its own pace, as tall as the voice is strong; light falls off from each.
        float light = 0f;
        for (int k = 0; k < 3; k++)
        {
            float wave = Hlsl.Sin(uv.X * (7f + k * 4f) + time * (1.6f + k * 0.9f)) * Hlsl.Cos(uv.X * 3f - time * 0.7f);
            float y = 0.5f + wave * strength * (0.38f - k * 0.09f);
            float apart = Hlsl.Abs(uv.Y - y);
            light += 0.01f / (apart + 0.01f) * (0.5f - k * 0.12f) * (0.25f + strength);
        }
        // Beneath them, soft bars mirrored about the middle, one for each band.
        float bars = Hlsl.SmoothStep(strength * 0.42f + 0.02f, strength * 0.42f, Hlsl.Abs(uv.Y - 0.5f)) * 0.18f * Hlsl.Step(0.12f, Hlsl.Frac(place));

        // Low notes in the accent colour, high ones in its neighbour on the wheel; both ends fade to nothing.
        float3 colour = Hlsl.Lerp(accent, accent.YZX, uv.X);
        float fade = Hlsl.SmoothStep(0f, 0.08f, uv.X) * Hlsl.SmoothStep(1f, 0.92f, uv.X);
        float alpha = Hlsl.Saturate((light * 0.6f + bars) * fade);
        return new float4(Hlsl.Saturate(colour + light * 0.15f) * alpha, alpha); // light and colour multiplied together, as the drawing surface expects
    }

    private static float Pick(float4 a, float4 b, float4 c, float4 d, int band)
    {
        float4 four = band < 4 ? a : band < 8 ? b : band < 12 ? c : d;
        int at = band < 4 ? band : band < 8 ? band - 4 : band < 12 ? band - 8 : band - 12; // no remainder: it is slow on a graphics card
        return at == 0 ? four.X : at == 1 ? four.Y : at == 2 ? four.Z : four.W;
    }
}

/// <summary>
/// A clip's picture as a print developing in the tray: dark under the red safelight at first, coming up through the
/// grain as the clip is drawn, its shadows before its highlights, with a red glow round the brightest parts, and a
/// flash of white light as it is fixed.
/// </summary>
/// <param name="developed">How far the clip has been drawn, 0 to 1.</param>
/// <param name="time">Seconds since the job began, so the grain moves.</param>
/// <param name="flash">The flash as the print is fixed, 0 to 1.</param>
[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct Darkroom(float developed, float time, float flash) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float4 picture = D2D.GetInput(0);
        float2 position = D2D.GetScenePosition().XY;
        float grain = Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(Hlsl.Floor(position / 1.5f) + Hlsl.Floor(time * 24f), new float2(12.9898f, 78.233f))) * 43758.5453f);
        float3 colour = picture.XYZ / Hlsl.Max(picture.W, 0.001f);
        float luma = Hlsl.Dot(colour, new float3(0.3f, 0.59f, 0.11f));

        // Each point comes up once the developer has reached it: the dark parts first, and not all at once, through the grain.
        float reached = Hlsl.Saturate((developed * 1.35f - luma * 0.35f - grain * 0.2f) * 3f);
        float3 paper = new float3(0.30f, 0.04f, 0.03f) * (0.35f + 0.25f * grain);
        float3 print = Hlsl.Lerp(paper, colour, reached);
        // Under the safelight everything is red, until the print is fixed.
        float safe = 1f - developed * developed * developed;
        print *= Hlsl.Lerp(new float3(1f, 1f, 1f), new float3(1f, 0.45f, 0.38f), safe);
        print += Hlsl.Max(0f, luma - 0.7f) * 0.8f * new float3(1f, 0.25f, 0.15f) * safe * reached; // a glow round the brightest parts
        print += (grain - 0.5f) * 0.07f * safe;
        print = Hlsl.Lerp(print, new float3(1f, 1f, 1f), flash);
        return new float4(Hlsl.Saturate(print) * picture.W, picture.W);
    }
}
