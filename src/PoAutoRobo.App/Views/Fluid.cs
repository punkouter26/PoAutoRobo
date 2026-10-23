using ComputeSharp;
using ComputeSharp.D2D1;

namespace PoAutoRobo.App.Views;

// The steps of a real fluid, worked out on the graphics card for the deck's backdrop (see FluidBackdrop): each frame
// the fluid carries itself and its colour along, is pushed where the pointer or a card moves through it, has its small
// whirls strengthened so it stays lively, and is then made incompressible, so it swirls round instead of piling up.
// Every value lives on a small grid, one cell to a pixel of each step's picture; positions are in cells. Values can be
// negative and must not be rounded, so every step writes half-precision floats.

/// <summary>
/// Carries a quantity (the fluid's own movement, or its colour) along the movement: each cell takes what was upstream
/// of it a moment ago. Up to four pushes are added, each a soft round dab, and what is carried fades a little.
/// </summary>
/// <param name="size">The grid's size in cells.</param>
/// <param name="dt">The step in seconds.</param>
/// <param name="keep">How much of the quantity survives the step, just under 1.</param>
/// <param name="wall">1 when carrying the movement, which stops at the edges and has a top speed; 0 for colour, which has neither.</param>
/// <param name="at0">A push: where (x, y) and how wide (z), in cells.</param>
/// <param name="add0">What that push adds at its middle.</param>
[D2DInputCount(2)]
[D2DInputComplex(0)]
[D2DInputComplex(1)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DOutputBuffer(D2D1BufferPrecision.Float16)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidCarry(
    float2 size, float dt, float keep, float wall,
    float4 at0, float4 add0, float4 at1, float4 add1, float4 at2, float4 add2, float4 at3, float4 add3) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 here = D2D.GetScenePosition().XY;
        float2 moving = D2D.SampleInputAtPosition(1, here).XY;
        float2 from = Hlsl.Clamp(here - dt * moving, new float2(0.5f, 0.5f), size - 0.5f);
        float4 value = D2D.SampleInputAtPosition(0, from) * keep;
        value += Dab(here, at0, add0) + Dab(here, at1, add1) + Dab(here, at2, add2) + Dab(here, at3, add3);

        // The movement is held under a top speed, a few cells a step, past which carrying it along would tear it apart.
        float speed = Hlsl.Length(value.XY);
        value.XY *= Hlsl.Lerp(1f, Hlsl.Min(1f, 80f / Hlsl.Max(speed, 0.0001f)), wall);

        // The fluid cannot flow through the edges of the deck.
        float edge = Hlsl.Min(Hlsl.Min(here.X, here.Y), Hlsl.Min(size.X - here.X, size.Y - here.Y));
        float stop = edge < 1f ? wall : 0f;
        return new float4(value.XYZ * (1f - stop), 1f);
    }

    private static float4 Dab(float2 here, float4 at, float4 add)
    {
        float2 away = here - at.XY;
        return add * Hlsl.Exp(-Hlsl.Dot(away, away) / Hlsl.Max(at.Z * at.Z, 0.01f));
    }
}

/// <summary>How fast the fluid turns about each cell: its spin, positive one way and negative the other.</summary>
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DRequiresScenePosition]
[D2DOutputBuffer(D2D1BufferPrecision.Float16)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidSpin(float2 size) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 here = D2D.GetScenePosition().XY;
        float left = Near0(here, -1, 0, size).Y, right = Near0(here, 1, 0, size).Y;
        float below = Near0(here, 0, 1, size).X, above = Near0(here, 0, -1, size).X;
        return new float4(0.5f * (right - left - below + above), 0f, 0f, 1f);
    }

    // A neighbouring cell of input 0, never past the grid's edge.
    private static float4 Near0(float2 here, float x, float y, float2 size) =>
        D2D.SampleInputAtPosition(0, Hlsl.Clamp(here + new float2(x, y), new float2(0.5f, 0.5f), size - 0.5f));
}

/// <summary>
/// Turns the small whirls up: each cell is pushed round its neighbours' strongest spin, which a coarse grid would
/// otherwise smear away. Input 0 is the movement, input 1 the spin.
/// </summary>
/// <param name="strength">How hard; 0 leaves the movement as it is.</param>
[D2DInputCount(2)]
[D2DInputSimple(0)]
[D2DInputComplex(1)]
[D2DRequiresScenePosition]
[D2DOutputBuffer(D2D1BufferPrecision.Float16)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidWhirl(float2 size, float dt, float strength) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 here = D2D.GetScenePosition().XY;
        float left = Near1(here, -1, 0, size).X, right = Near1(here, 1, 0, size).X;
        float below = Near1(here, 0, 1, size).X, above = Near1(here, 0, -1, size).X;
        float spin = D2D.SampleInputAtPosition(1, here).X;
        float2 towards = 0.5f * new float2(Hlsl.Abs(below) - Hlsl.Abs(above), Hlsl.Abs(right) - Hlsl.Abs(left));
        towards /= Hlsl.Length(towards) + 0.0001f;
        float2 force = towards * spin * strength * new float2(1f, -1f);
        float2 moving = D2D.GetInput(0).XY + force * dt;
        return new float4(moving, 0f, 1f);
    }

    // A neighbouring cell of input 1, never past the grid's edge.
    private static float4 Near1(float2 here, float x, float y, float2 size) =>
        D2D.SampleInputAtPosition(1, Hlsl.Clamp(here + new float2(x, y), new float2(0.5f, 0.5f), size - 0.5f));
}

/// <summary>How much fluid each cell gives out (positive) or takes in (negative), which a fluid that cannot be squeezed may not do.</summary>
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DRequiresScenePosition]
[D2DOutputBuffer(D2D1BufferPrecision.Float16)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidSpread(float2 size) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 here = D2D.GetScenePosition().XY;
        float2 middle = D2D.SampleInputAtPosition(0, here).XY;
        // At an edge the fluid bounces back, so the cell beyond is taken as moving the opposite way.
        float left = here.X < 1f ? -middle.X : Near0(here, -1, 0, size).X;
        float right = here.X > size.X - 1f ? -middle.X : Near0(here, 1, 0, size).X;
        float above = here.Y < 1f ? -middle.Y : Near0(here, 0, -1, size).Y;
        float below = here.Y > size.Y - 1f ? -middle.Y : Near0(here, 0, 1, size).Y;
        return new float4(0.5f * (right - left + below - above), 0f, 0f, 1f);
    }

    // A neighbouring cell of input 0, never past the grid's edge.
    private static float4 Near0(float2 here, float x, float y, float2 size) =>
        D2D.SampleInputAtPosition(0, Hlsl.Clamp(here + new float2(x, y), new float2(0.5f, 0.5f), size - 0.5f));
}

/// <summary>
/// One round of settling the pressure that stops the fluid being squeezed: each cell takes the mean of its neighbours
/// less what it gives out. Run many times a frame, it settles. Input 0 is the pressure so far, input 1 the spread.
/// </summary>
/// <param name="keep">How much of last frame's pressure to start from on the first round; 1 on the others.</param>
[D2DInputCount(2)]
[D2DInputComplex(0)]
[D2DInputSimple(1)]
[D2DRequiresScenePosition]
[D2DOutputBuffer(D2D1BufferPrecision.Float16)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidPressure(float2 size, float keep) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 here = D2D.GetScenePosition().XY;
        float around = Near0(here, -1, 0, size).X + Near0(here, 1, 0, size).X + Near0(here, 0, -1, size).X + Near0(here, 0, 1, size).X;
        return new float4((around * keep - D2D.GetInput(1).X) * 0.25f, 0f, 0f, 1f);
    }

    // A neighbouring cell of input 0, never past the grid's edge.
    private static float4 Near0(float2 here, float x, float y, float2 size) =>
        D2D.SampleInputAtPosition(0, Hlsl.Clamp(here + new float2(x, y), new float2(0.5f, 0.5f), size - 0.5f));
}

/// <summary>Takes the pressure's slope out of the movement, which leaves only the part that swirls. Input 0 is the pressure, input 1 the movement.</summary>
[D2DInputCount(2)]
[D2DInputComplex(0)]
[D2DInputSimple(1)]
[D2DRequiresScenePosition]
[D2DOutputBuffer(D2D1BufferPrecision.Float16)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidSettle(float2 size) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 here = D2D.GetScenePosition().XY;
        float2 slope = 0.5f * new float2(
            Near0(here, 1, 0, size).X - Near0(here, -1, 0, size).X,
            Near0(here, 0, 1, size).X - Near0(here, 0, -1, size).X);
        return new float4(D2D.GetInput(1).XY - slope, 0f, 1f);
    }

    // A neighbouring cell of input 0, never past the grid's edge.
    private static float4 Near0(float2 here, float x, float y, float2 size) =>
        D2D.SampleInputAtPosition(0, Hlsl.Clamp(here + new float2(x, y), new float2(0.5f, 0.5f), size - 0.5f));
}

/// <summary>
/// The fluid's colour as it is seen: glowing where it is thick, and lit as a surface would be, its folds catching the
/// light from the top left and a soft sheen on the ridges, so it reads as ink moving in glass rather than a flat stain.
/// </summary>
/// <param name="strength">How strongly it shows; less over a light page, which it would otherwise wash out.</param>
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct FluidLook(float2 size, float strength) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 here = D2D.GetScenePosition().XY;
        float3 ink = Hlsl.Max(D2D.SampleInputAtPosition(0, here).XYZ, 0f);
        float thick = Hlsl.Length(ink);

        // The surface's slope, from how the thickness changes two cells either way (one cell would catch the grid's
        // own grain), lit from up and to the left.
        float dx = Hlsl.Length(Near0(here, 2, 0, size).XYZ) - Hlsl.Length(Near0(here, -2, 0, size).XYZ);
        float dy = Hlsl.Length(Near0(here, 0, 2, size).XYZ) - Hlsl.Length(Near0(here, 0, -2, size).XYZ);
        float3 normal = Hlsl.Normalize(new float3(-dx * 1.5f, -dy * 1.5f, 1f));
        float lit = Hlsl.Saturate(Hlsl.Dot(normal, Hlsl.Normalize(new float3(-0.5f, -0.6f, 0.65f))));
        float sheen = Hlsl.Pow(lit, 16f) * Hlsl.Saturate(thick * 2f) * 0.35f;

        float glow = 1f - Hlsl.Exp(-thick * 1.6f);
        float3 colour = ink / Hlsl.Max(thick, 0.0001f) * (0.65f + 0.5f * lit) + sheen;
        float alpha = Hlsl.Saturate(glow * strength + sheen * 0.15f);
        return new float4(colour * alpha, alpha); // light and colour multiplied together, as the drawing surface expects
    }

    // A neighbouring cell of input 0, never past the grid's edge.
    private static float4 Near0(float2 here, float x, float y, float2 size) =>
        D2D.SampleInputAtPosition(0, Hlsl.Clamp(here + new float2(x, y), new float2(0.5f, 0.5f), size - 0.5f));
}
