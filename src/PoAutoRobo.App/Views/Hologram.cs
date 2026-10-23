using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace PoAutoRobo.App.Views;

/// <summary>What the hologram of the R1 rehearses: the kind of skill the lesson is about.</summary>
public enum Motion { Idle, Walk, StandUp, Wave, Reach }

/// <summary>
/// A hologram of the R1 turning slowly on its projector, rehearsing the skill the lesson is about: walking, standing
/// up, waving or reaching, read from the lesson's own words. The figure is worked out on the graphics card from simple
/// solid shapes (see <see cref="R1Light"/>); its joints are posed here each frame. Drawn over the page's moving light,
/// so on a plain canvas on the window's frames, and only while it can be seen. Windows' switch for no animation leaves it out.
/// </summary>
public sealed partial class Hologram : UserControl
{
    private readonly CanvasControl _canvas = new() { IsHitTestVisible = false, IsTabStop = false };
    private readonly FrameClock _clock;
    private readonly bool _animated = new UISettings().AnimationsEnabled;
    private PixelShaderEffect<R1Light>? _light;
    private bool _wanted, _inFront = true, _loaded;
    private float _hurry; // 0 at the usual pace, rising to 1 while a script is being written

    public Hologram()
    {
        Content = _canvas;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _clock = new FrameClock((_, _) => _canvas.Invalidate());
        _canvas.Draw += OnDraw;
        Loaded += (_, _) =>
        {
            _loaded = true;
            if (!_animated) Visibility = Visibility.Collapsed;
            Pace();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _clock.Stop();
            _canvas.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak
            _light?.Dispose();
            _light = null;
        };
    }

    /// <summary>The skill being rehearsed.</summary>
    public Motion Motion { get; set; }

    /// <summary>True while a script is being written: the figure works faster and its light runs brighter.</summary>
    public bool Busy { get; set; }

    /// <summary>True while the page it is on is the one showing.</summary>
    public bool Wanted
    {
        get => _wanted;
        set
        {
            _wanted = value;
            Pace();
        }
    }

    /// <summary>Told by the window: false while another app is in front, when there is nobody to draw for.</summary>
    public void SetInFront(bool inFront)
    {
        _inFront = inFront;
        Pace();
    }

    private void Pace()
    {
        if (_loaded && _animated && _wanted && _inFront) _clock.Start(); else _clock.Stop();
    }

    /// <summary>
    /// The skill a lesson is about, from its words: the first of these found wins, so "stand up and walk" stands up.
    /// Anything else is rehearsed as the robot idling, looking about.
    /// </summary>
    public static Motion MotionFor(string lesson)
    {
        var words = lesson.ToLowerInvariant();
        (string[] Words, Motion Motion)[] skills =
        [
            (["stand", "sit", "squat", "get up", "rise", "chair"], Motion.StandUp),
            (["walk", "stair", "step", "climb", "run", "jog", "march"], Motion.Walk),
            (["wave", "greet", "hello", "bow", "dance"], Motion.Wave),
            (["reach", "grab", "pick", "hold", "hand", "carry", "push", "lift", "open"], Motion.Reach),
        ];
        foreach (var (keys, motion) in skills)
            if (keys.Any(words.Contains)) return motion;
        return Motion.Idle;
    }

    /// <summary>What the figure is doing, in a few words to show beneath it.</summary>
    public static string Describe(Motion motion) => motion switch
    {
        Motion.Walk => "R1 · rehearsing a walk",
        Motion.StandUp => "R1 · rehearsing standing up",
        Motion.Wave => "R1 · rehearsing a wave",
        Motion.Reach => "R1 · rehearsing a reach",
        _ => "R1 · waiting for a lesson",
    };

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!_clock.Running) return;
        _hurry += ((Busy ? 1 : 0) - _hurry) * 0.05f;
        var time = _clock.Time * (1 + _hurry * 0.6f);
        var pose = Pose.Of(Motion, time);
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        var dpi = sender.Dpi / 96;
        var (width, height) = ((float)sender.ActualWidth, (float)sender.ActualHeight);
        _light ??= new PixelShaderEffect<R1Light>();
        _light.ConstantBuffer = new R1Light(
            _clock.Time, new float2(width * dpi, height * dpi), new float3(accent.R / 255f, accent.G / 255f, accent.B / 255f),
            _clock.Time * 0.35f, 1 + _hurry * 0.5f, ActualTheme == ElementTheme.Light ? 1 : 0,
            V(pose.Pelvis), V(pose.Chest), V(pose.Head), pose.Look,
            V(pose.ShoulderL), V(pose.ElbowL), V(pose.WristL), V(pose.ShoulderR), V(pose.ElbowR), V(pose.WristR),
            V(pose.HipL), V(pose.KneeL), V(pose.AnkleL), V(pose.HipR), V(pose.KneeR), V(pose.AnkleR));
        args.DrawingSession.DrawImage(_light, new Rect(0, 0, width, height), new Rect(0, 0, width * dpi, height * dpi));

        static float3 V(Vector3 v) => new(v.X, v.Y, v.Z);
    }

    /// <summary>
    /// Where each joint is, in metres, the feet on the projector at height 0 and the robot facing the viewer at the
    /// start of its turn. Legs and arms bend in the plane they swing in; the hips are set so a standing foot stays on
    /// the floor however the knees bend.
    /// </summary>
    internal readonly record struct Pose(
        Vector3 Pelvis, Vector3 Chest, Vector3 Head, float Look,
        Vector3 ShoulderL, Vector3 ElbowL, Vector3 WristL, Vector3 ShoulderR, Vector3 ElbowR, Vector3 WristR,
        Vector3 HipL, Vector3 KneeL, Vector3 AnkleL, Vector3 HipR, Vector3 KneeR, Vector3 AnkleR)
    {
        private const float Thigh = 0.42f, Shin = 0.4f, UpperArm = 0.26f, Forearm = 0.24f, Spine = 0.4f;

        public static Pose Of(Motion motion, float t)
        {
            float thighL = 0, thighR = 0, kneeL = 0.08f, kneeR = 0.08f, lean = 0, look = 0.35f * MathF.Sin(t * 0.5f);
            float armL = 0.05f * MathF.Sin(t * 1.3f), armR = -armL, elbowL = 0.15f, elbowR = 0.15f, breathe = 0.008f * MathF.Sin(t * 2);
            float waveUp = 0, wave = 0;
            switch (motion)
            {
                case Motion.Walk:
                    var stride = t * 3.4f;
                    (thighL, thighR) = (0.42f * MathF.Sin(stride), -0.42f * MathF.Sin(stride));
                    kneeL = 0.12f + 0.85f * MathF.Max(0, MathF.Sin(stride + 1.7f));
                    kneeR = 0.12f + 0.85f * MathF.Max(0, MathF.Sin(stride + 1.7f + MathF.PI));
                    (armL, armR) = (-0.4f * MathF.Sin(stride), 0.4f * MathF.Sin(stride));
                    (elbowL, elbowR, lean, look) = (0.35f, 0.35f, 0.08f, 0);
                    break;
                case Motion.StandUp:
                    // Down into a crouch, a pause, then up through the feet, arms forward for balance.
                    var cycle = (t * 0.45f) % 2f;
                    var crouch = cycle < 1 ? Smooth(Math.Clamp(cycle * 1.6f, 0, 1)) : 1 - Smooth(Math.Clamp((cycle - 1) * 1.6f, 0, 1));
                    (thighL, thighR) = (1.25f * crouch, 1.25f * crouch);
                    (kneeL, kneeR) = (0.08f + 2.0f * crouch, 0.08f + 2.0f * crouch);
                    lean = 0.62f * crouch;
                    (armL, armR, elbowL, elbowR) = (1.1f * crouch, 1.1f * crouch, 0.15f, 0.15f);
                    look = 0;
                    break;
                case Motion.Wave:
                    (waveUp, wave) = (1, 0.45f * MathF.Sin(t * 7));
                    break;
                case Motion.Reach:
                    var reach = Smooth(0.5f + 0.5f * MathF.Sin(t * 1.2f));
                    (armL, armR, elbowL, elbowR) = (1.35f * reach, 1.35f * reach, 0.3f - 0.25f * reach, 0.3f - 0.25f * reach);
                    (lean, look) = (0.25f * reach, 0);
                    (thighL, thighR, kneeL, kneeR) = (0.25f * reach, 0.25f * reach, 0.08f + 0.5f * reach, 0.08f + 0.5f * reach);
                    break;
            }

            // The higher of the two legs' reach sets the hips, so the straighter leg stands on the floor.
            static float Drop(float thigh, float knee) => Thigh * MathF.Cos(thigh) + Shin * MathF.Cos(thigh - knee);
            var hipHeight = 0.08f + MathF.Max(Drop(thighL, kneeL), Drop(thighR, kneeR));
            static float Forward(float thigh, float knee) => Thigh * MathF.Sin(thigh) + Shin * MathF.Sin(thigh - knee);
            var back = -0.5f * (Forward(thighL, kneeL) + Forward(thighR, kneeR)); // the body moves back over the feet as it crouches
            var pelvis = new Vector3(0, hipHeight + 0.04f + breathe, back);
            var chest = pelvis + Bent(Spine, lean, up: true);
            var head = chest + Bent(0.2f, lean * 0.5f, up: true);

            var (hipL, hipR) = (pelvis + new Vector3(-0.1f, -0.04f, 0), pelvis + new Vector3(0.1f, -0.04f, 0));
            var kneeJointL = hipL + Bent(Thigh, thighL);
            var kneeJointR = hipR + Bent(Thigh, thighR);
            var ankleL = kneeJointL + Bent(Shin, thighL - kneeL);
            var ankleR = kneeJointR + Bent(Shin, thighR - kneeR);

            var (shoulderL, shoulderR) = (chest + new Vector3(-0.2f, -0.03f, 0), chest + new Vector3(0.2f, -0.03f, 0));
            var elbowJointL = shoulderL + Bent(UpperArm, armL);
            var wristL = elbowJointL + Bent(Forearm, armL + elbowL);
            Vector3 elbowJointR, wristR;
            if (waveUp > 0)
            {
                // The right arm raised out to the side, the forearm swinging from the elbow.
                elbowJointR = shoulderR + Vector3.Normalize(new Vector3(0.75f, 0.45f, 0.1f)) * UpperArm;
                wristR = elbowJointR + new Vector3(MathF.Sin(wave), MathF.Cos(wave), 0.1f) * Forearm;
            }
            else
            {
                elbowJointR = shoulderR + Bent(UpperArm, armR);
                wristR = elbowJointR + Bent(Forearm, armR + elbowR);
            }
            return new(pelvis, chest, head, look, shoulderL, elbowJointL, wristL, shoulderR, elbowJointR, wristR, hipL, kneeJointL, ankleL, hipR, kneeJointR, ankleR);
        }

        // A limb of the given length swung forward by an angle from hanging straight down, or from standing straight up.
        private static Vector3 Bent(float length, float angle, bool up = false) =>
            up ? new Vector3(0, MathF.Cos(angle), MathF.Sin(angle)) * length : new Vector3(0, -MathF.Cos(angle), MathF.Sin(angle)) * length;

        private static float Smooth(float x) => x * x * (3 - 2 * x);
    }
}

/// <summary>
/// The R1 as a hologram: rays are cast from a camera circling the robot and stepped through a scene of soft solid
/// shapes (capsules for the limbs, rounded boxes for the head and feet, blended where they meet) until each touches the
/// surface. The surface glows at its edges, where light skims it, is crossed by scan lines rising up the figure, and
/// flickers and slips sideways now and then as a projected picture does. A ring projector glows beneath it, and the
/// light the rays pass close to on their way gives the figure a faint halo.
/// </summary>
/// <param name="time">Seconds since it began, for the scan lines and the flicker.</param>
/// <param name="size">The drawing's size in pixels.</param>
/// <param name="accent">The colour of the light, each part 0 to 1.</param>
/// <param name="turn">How far round the camera has gone, in radians.</param>
/// <param name="power">1 normally; more while a script is being written, when it burns brighter.</param>
/// <param name="light">1 over a light page, where the light is drawn darker so it still shows; 0 over a dark one.</param>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct R1Light(
    float time, float2 size, float3 accent, float turn, float power, float light,
    float3 pelvis, float3 chest, float3 head, float look,
    float3 shoulderL, float3 elbowL, float3 wristL, float3 shoulderR, float3 elbowR, float3 wristR,
    float3 hipL, float3 kneeL, float3 ankleL, float3 hipR, float3 kneeR, float3 ankleR) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 pixel = D2D.GetScenePosition().XY;

        // Now and then a band of rows slips sideways, as a projected picture does.
        float row = Hlsl.Floor(pixel.Y / Hlsl.Max(size.Y / 70f, 1f));
        float slip = Hash(row + Hlsl.Floor(time * 11f)) > 0.975f ? (Hash(row * 1.7f) - 0.5f) * 0.07f : 0f;
        float2 uv = (pixel - size * 0.5f) / size.Y;
        uv.X += slip;
        uv.Y = -uv.Y;

        // The camera circles the robot, a little above its waist, looking at its middle.
        float3 eye = new(Hlsl.Sin(turn) * 4.2f, 1.05f, Hlsl.Cos(turn) * 4.2f);
        float3 ahead = Hlsl.Normalize(new float3(0f, 0.8f, 0f) - eye);
        float3 right = Hlsl.Normalize(Hlsl.Cross(ahead, new float3(0f, 1f, 0f)));
        float3 up = Hlsl.Cross(right, ahead);
        float3 ray = Hlsl.Normalize(ahead * 2.1f + right * uv.X + up * uv.Y);

        // Step along the ray until it touches the robot, gathering a halo from how near it passes.
        float travelled = 2.6f;
        float halo = 0f;
        float hit = 0f;
        for (int i = 0; i < 72; i++)
        {
            float3 at = eye + ray * travelled;
            float distance = Scene(at);
            halo += 0.006f / (1f + distance * distance * 900f);
            if (distance < 0.0015f)
            {
                hit = 1f;
                break;
            }
            travelled += distance * 0.9f;
            if (travelled > 6.2f) break;
        }

        float flicker = 0.9f + 0.1f * Hlsl.Sin(time * 37f) * Hlsl.Sin(time * 13f);
        float3 colour = new(0f, 0f, 0f);
        float alpha = 0f;

        if (hit > 0f)
        {
            float3 at = eye + ray * travelled;
            float3 normal = Normal(at);
            float edge = Hlsl.Pow(Hlsl.Saturate(1f - Hlsl.Abs(Hlsl.Dot(normal, ray))), 2.2f);
            float scan = 0.78f + 0.22f * Hlsl.Sin(at.Y * 260f - time * 5f);
            float sweep = Hlsl.SmoothStep(0.06f, 0f, Hlsl.Abs(Hlsl.Frac(at.Y * 0.35f - time * 0.25f) - 0.5f)); // a bright band climbing the figure
            float body = (0.32f + 1.05f * edge) * scan + sweep * 0.5f;

            // The face: a dark visor across the front of the head, with two eyes lit in it.
            float3 face = Facing(at - head);
            float visor = Hlsl.Step(0.065f, face.Z) * Hlsl.Step(Hlsl.Abs(face.Y - 0.005f), 0.034f);
            float eyes = visor * Hlsl.SmoothStep(0.022f, 0.012f, Hlsl.Length(new float2(Hlsl.Abs(face.X) - 0.042f, face.Y - 0.005f)));
            body = Hlsl.Lerp(body, 0.08f, visor) + eyes * 2.5f;

            colour = Hlsl.Lerp(accent, new float3(1f, 1f, 1f), Hlsl.Saturate(edge * 0.6f + eyes));
            alpha = Hlsl.Saturate(body);
        }
        else if (ray.Y < 0f)
        {
            // The projector: rings of light on the floor beneath the robot, rippling outwards.
            float3 floor = eye + ray * (-eye.Y / ray.Y);
            float from = Hlsl.Length(new float2(floor.X, floor.Z));
            float pad = Hlsl.SmoothStep(0.78f, 0.7f, from);
            float rings = 0.2f + 0.8f * Hlsl.Pow(Hlsl.Saturate(0.5f + 0.5f * Hlsl.Sin(from * 38f - time * 3f)), 6f);
            float rim = Hlsl.SmoothStep(0.05f, 0f, Hlsl.Abs(from - 0.72f));
            colour = accent;
            alpha = Hlsl.Saturate(pad * rings * 0.35f + rim * 0.8f);
        }

        // A cone of faint light rising from the projector, strongest near the floor.
        float beam = Hlsl.Saturate(1f - Hlsl.Abs(uv.X) * 2.2f) * Hlsl.Saturate(0.45f - uv.Y) * 0.08f;
        alpha = Hlsl.Saturate((alpha + halo + beam) * flicker * power);
        float lines = 0.85f + 0.15f * Hlsl.Sin(pixel.Y * 1.3f); // the fine lines of the projected picture itself
        colour = Hlsl.Lerp(colour + accent * halo * 4f, accent * 0.6f + colour * 0.15f, light * 0.75f) * lines;
        return new float4(colour * alpha, alpha); // light and colour multiplied together, as the drawing surface expects
    }

    // ---- The robot as soft solid shapes: how far a point is from its surface ----

    private float Scene(float3 p)
    {
        float body = Capsule(p, pelvis + new float3(0f, 0.04f, 0f), chest, 0.115f);
        body = Blend(body, Capsule(p, shoulderL, shoulderR, 0.075f), 0.07f);
        body = Blend(body, Capsule(p, hipL, hipR, 0.08f), 0.06f);
        body = Blend(body, Capsule(p, chest, head, 0.035f), 0.03f);
        body = Hlsl.Min(body, Box(Facing(p - head), new float3(0.085f, 0.075f, 0.08f), 0.045f));

        float arms = Hlsl.Min(Capsule(p, shoulderL, elbowL, 0.045f), Capsule(p, elbowL, wristL, 0.038f));
        arms = Hlsl.Min(arms, Hlsl.Min(Capsule(p, shoulderR, elbowR, 0.045f), Capsule(p, elbowR, wristR, 0.038f)));
        arms = Hlsl.Min(arms, Hlsl.Min(Hlsl.Length(p - wristL) - 0.045f, Hlsl.Length(p - wristR) - 0.045f));
        arms = Hlsl.Min(arms, Hlsl.Min(Hlsl.Length(p - elbowL) - 0.05f, Hlsl.Length(p - elbowR) - 0.05f));

        float legs = Hlsl.Min(Capsule(p, hipL, kneeL, 0.06f), Capsule(p, kneeL, ankleL, 0.05f));
        legs = Hlsl.Min(legs, Hlsl.Min(Capsule(p, hipR, kneeR, 0.06f), Capsule(p, kneeR, ankleR, 0.05f)));
        legs = Hlsl.Min(legs, Hlsl.Min(Hlsl.Length(p - kneeL) - 0.065f, Hlsl.Length(p - kneeR) - 0.065f));
        legs = Hlsl.Min(legs, Box(p - (ankleL + new float3(0f, -0.045f, 0.05f)), new float3(0.04f, 0.015f, 0.08f), 0.025f));
        legs = Hlsl.Min(legs, Box(p - (ankleR + new float3(0f, -0.045f, 0.05f)), new float3(0.04f, 0.015f, 0.08f), 0.025f));

        return Blend(body, Hlsl.Min(arms, legs), 0.04f);
    }

    // The head turns as the robot looks about; points near it are seen in its own turned frame.
    private float3 Facing(float3 p)
    {
        float c = Hlsl.Cos(look), s = Hlsl.Sin(look);
        return new float3(c * p.X - s * p.Z, p.Y, s * p.X + c * p.Z);
    }

    private float3 Normal(float3 p)
    {
        float2 k = new(1f, -1f);
        float e = 0.0015f;
        return Hlsl.Normalize(
            k.XYY * Scene(p + k.XYY * e) + k.YYX * Scene(p + k.YYX * e) +
            k.YXY * Scene(p + k.YXY * e) + k.XXX * Scene(p + k.XXX * e));
    }

    private static float Capsule(float3 p, float3 a, float3 b, float radius)
    {
        float3 pa = p - a, ba = b - a;
        float along = Hlsl.Saturate(Hlsl.Dot(pa, ba) / Hlsl.Max(Hlsl.Dot(ba, ba), 0.000001f));
        return Hlsl.Length(pa - ba * along) - radius;
    }

    private static float Box(float3 p, float3 half, float round)
    {
        float3 q = Hlsl.Abs(p) - half;
        return Hlsl.Length(Hlsl.Max(q, 0f)) + Hlsl.Min(Hlsl.Max(q.X, Hlsl.Max(q.Y, q.Z)), 0f) - round;
    }

    // Two shapes joined with a smooth fillet where they meet, as a moulded shell is.
    private static float Blend(float a, float b, float k)
    {
        float h = Hlsl.Saturate(0.5f + 0.5f * (b - a) / k);
        return Hlsl.Lerp(b, a, h) - k * h * (1f - h);
    }

    private static float Hash(float n) => Hlsl.Frac(Hlsl.Sin(n * 12.9898f) * 43758.5453f);
}
