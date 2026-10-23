namespace PoAutoRobo.Core.Render;

/// <summary>
/// The intro's opening: thousands of points of light spiral in on a current that never runs dry, are drawn harder and
/// harder towards their places in the finished title card, and land all at once as the sting hits. The blow throws
/// them outwards; each is held to its place by a spring and shivers back. Then they fade, leaving the real card.
/// </summary>
/// <remarks>
/// Drawn here, frame by frame, as plain colour values for FFmpeg to lay over the intro's background, because a drawing
/// library is not worth taking on for one effect. The same card always gives the same swarm, so it caches.
/// Distances are in heights of the frame, so the swarm looks the same at any size.
/// </remarks>
public sealed class Swarm
{
    /// <summary>The most points of light; a card with more lit pixels than this is sampled.</summary>
    private const int MaxPoints = 24000;

    private readonly int _width, _height;
    private readonly float _aspect;
    private readonly (float R, float G, float B) _accent;
    private readonly float[] _homeX, _homeY;      // where each point lands
    private readonly float[] _red, _green, _blue; // the card's colour there
    private readonly float[] _x, _y, _vx, _vy;    // flight before the hit
    private readonly float[] _shownX, _shownY;    // where each was drawn last frame, for its streak
    private readonly float[] _late;               // how long after the start each sets off for home, in seconds
    private readonly float[] _offX, _offY, _offVx, _offVy; // after the hit: thrown off its place and springing back
    private readonly float[] _light;              // red, green and blue for every pixel, added up as the points are drawn
    private readonly float _glow;                 // how much light each point gives: landed together, they make the card as bright as it is
    private bool _struck;

    /// <param name="card">The finished card over black, three bytes (red, green, blue) for each pixel, row by row.</param>
    /// <param name="accent">The series colour, which the points glow in until they take the card's own colours.</param>
    public Swarm(ReadOnlySpan<byte> card, int width, int height, (byte R, byte G, byte B) accent, int seed = 1)
    {
        (_width, _height, _aspect) = (width, height, (float)width / height);
        _accent = (accent.R / 255f, accent.G / 255f, accent.B / 255f);
        _light = new float[width * height * 3];

        // Every pixel bright enough to be part of something (a letter, the logo, the mascot) is a place to land.
        var lit = new List<int>();
        for (var i = 0; i + 2 < card.Length && i / 3 < width * height; i += 3)
            if (Math.Max(card[i], Math.Max(card[i + 1], card[i + 2])) > 70)
                lit.Add(i / 3);
        var random = new Random(seed);
        var step = Math.Max(1.0, lit.Count / (double)MaxPoints);
        var picked = new List<int>();
        for (var k = 0.0; k < lit.Count; k += step)
            picked.Add(lit[Math.Min(lit.Count - 1, (int)(k + random.NextDouble() * step))]);

        var n = picked.Count;
        _glow = Math.Clamp(0.6f * lit.Count / Math.Max(1, n), 0.3f, 1.2f);
        (_homeX, _homeY, _red, _green, _blue) = (new float[n], new float[n], new float[n], new float[n], new float[n]);
        (_x, _y, _vx, _vy, _shownX, _shownY, _late) = (new float[n], new float[n], new float[n], new float[n], new float[n], new float[n], new float[n]);
        (_offX, _offY, _offVx, _offVy) = (new float[n], new float[n], new float[n], new float[n]);
        for (var p = 0; p < n; p++)
        {
            var pixel = picked[p];
            _homeX[p] = (pixel % width + 0.5f) / height;
            _homeY[p] = (pixel / width + 0.5f) / height;
            (_red[p], _green[p], _blue[p]) = (card[pixel * 3] / 255f, card[pixel * 3 + 1] / 255f, card[pixel * 3 + 2] / 255f);
            // Each starts somewhere on a wide ring around the middle, already moving round it, so the swarm turns as it gathers.
            var angle = random.NextDouble() * Math.Tau;
            var radius = 0.55 + random.NextDouble() * 0.6;
            _x[p] = (float)(_aspect / 2 + Math.Cos(angle) * radius * 1.2);
            _y[p] = (float)(0.5 + Math.Sin(angle) * radius);
            var speed = 0.5 + random.NextDouble() * 0.4;
            (_vx[p], _vy[p]) = ((float)(-Math.Sin(angle) * speed), (float)(Math.Cos(angle) * speed));
            (_shownX[p], _shownY[p]) = (_x[p], _y[p]);
            _late[p] = (float)(random.NextDouble() * 0.6);
        }
    }

    /// <summary>How many points of light there are; none when the card had nothing bright on it.</summary>
    public int Count => _homeX.Length;

    /// <summary>Moves every point on to <paramref name="time"/> and draws the frame into <paramref name="frame"/>, three bytes a pixel.</summary>
    /// <param name="step">Time since the last frame; frames must be drawn in order.</param>
    public void Draw(double time, double step, Span<byte> frame)
    {
        var (t, dt) = ((float)time, (float)step);
        var hit = (float)Synth.Hit.TotalSeconds;
        Array.Clear(_light);
        var (cx, cy) = (_aspect / 2, 0.5f);

        if (t >= hit && !_struck)
        {
            // The blow: every point is thrown straight out from the middle, the nearest hardest.
            _struck = true;
            for (var p = 0; p < Count; p++)
            {
                var (dx, dy) = (_homeX[p] - cx, _homeY[p] - cy);
                var distance = MathF.Sqrt(dx * dx + dy * dy) + 1e-4f;
                var push = 0.5f / (0.3f + distance);
                (_offVx[p], _offVy[p]) = (dx / distance * push, dy / distance * push);
            }
        }

        // The points fade out once they have landed and the card behind them shows.
        var gone = 1 - Math.Clamp((t - hit - 0.15f) / 0.7f, 0, 1);
        // Around the blow the colours part a little, red one way and blue the other, as a lens does under a shock.
        var split = 3f * MathF.Exp(-MathF.Abs(t - hit) * 10) * _height / 720f;
        for (var p = 0; p < Count; p++)
        {
            float x, y, landed;
            if (!_struck)
            {
                // Free flight: round the middle, a little inwards, and along a current that swirls but never gathers or drains.
                var (dx, dy) = (_x[p] - cx, _y[p] - cy);
                var distance = MathF.Sqrt(dx * dx + dy * dy) + 1e-3f;
                var (flowX, flowY) = Current(_x[p], _y[p], t);
                _vx[p] += (-dy / distance * 0.9f - dx * 0.6f + flowX * 2.2f) * dt;
                _vy[p] += (dx / distance * 0.9f - dy * 0.6f + flowY * 2.2f) * dt;
                var drag = 1 - 1.2f * dt;
                (_vx[p], _vy[p]) = (_vx[p] * drag, _vy[p] * drag);
                (_x[p], _y[p]) = (_x[p] + _vx[p] * dt, _y[p] + _vy[p] * dt);

                // Drawn home ever harder: slowly at first, then all at once at the hit.
                landed = MathF.Pow(Math.Clamp((t - _late[p]) / (hit - _late[p]), 0, 1), 2.2f);
                x = _x[p] + (_homeX[p] - _x[p]) * landed;
                y = _y[p] + (_homeY[p] - _y[p]) * landed;
            }
            else
            {
                // On its spring: stiff, and only lightly held back, so it overshoots and shivers before it settles.
                _offVx[p] += (-140 * _offX[p] - 11 * _offVx[p]) * dt;
                _offVy[p] += (-140 * _offY[p] - 11 * _offVy[p]) * dt;
                (_offX[p], _offY[p]) = (_offX[p] + _offVx[p] * dt, _offY[p] + _offVy[p] * dt);
                (x, y, landed) = (_homeX[p] + _offX[p], _homeY[p] + _offY[p], 1);
            }

            // Each comes up out of the dark as it sets off, and burns brighter the nearer it is to home, so the card builds in light.
            var light = _glow * Math.Clamp((t - _late[p] * 0.5f) / 0.6f, 0, 1) * (0.5f + 0.5f * landed) * gone;
            var r = _accent.R + (_red[p] * 1.2f - _accent.R) * landed;
            var g = _accent.G + (_green[p] * 1.2f - _accent.G) * landed;
            var b = _accent.B + (_blue[p] * 1.2f - _accent.B) * landed;
            Streak(_shownX[p], _shownY[p], x, y, r * light, g * light, b * light, split);
            (_shownX[p], _shownY[p]) = (x, y);
        }

        var flash = t >= hit ? 0.25f * MathF.Exp(-(t - hit) * 8) : 0;
        var ring = t >= hit ? (t - hit) * 1.6f : -1; // the blow's ring, spreading from the middle
        var ringLight = 0.3f * MathF.Exp(-(t - hit) * 4);
        for (var i = 0; i < _width * _height; i++)
        {
            var extra = flash;
            if (ring >= 0)
            {
                var (dx, dy) = ((i % _width + 0.5f) / _height - cx, (i / _width + 0.5f) / _height - cy);
                var off = (MathF.Sqrt(dx * dx + dy * dy) - ring) / 0.025f;
                if (MathF.Abs(off) < 3) extra += ringLight * MathF.Exp(-off * off);
            }
            for (var c = 0; c < 3; c++)
            {
                var value = (_light[i * 3 + c] + extra) * 1.6f;
                frame[i * 3 + c] = (byte)(value / (1 + value) * 255); // bright light rounds off instead of clipping
            }
        }
    }

    // A current that turns and folds but has no source or drain: the curl of a field of crossing waves, worked out exactly.
    private static (float X, float Y) Current(float x, float y, float t)
    {
        var (a, b) = (3.1f * x + 1.7f * t, 2.3f * y - 1.1f * t);
        var (c, d) = (5.7f * y + 0.9f * t, 4.3f * x + 2.1f * t);
        // The field is sin a cos b + 0.5 sin c cos d; the current runs along its lines: (d/dy, -d/dx).
        var dy = -2.3f * MathF.Sin(a) * MathF.Sin(b) + 0.5f * 5.7f * MathF.Cos(c) * MathF.Cos(d);
        var dx = 3.1f * MathF.Cos(a) * MathF.Cos(b) - 0.5f * 4.3f * MathF.Sin(c) * MathF.Sin(d);
        return (dy, -dx);
    }

    // One point's light laid along its path since the last frame, so fast points draw streaks and slow ones dots.
    private void Streak(float fromX, float fromY, float toX, float toY, float r, float g, float b, float split)
    {
        var (px, py) = ((toX - fromX) * _height, (toY - fromY) * _height);
        var samples = Math.Clamp((int)(MathF.Sqrt(px * px + py * py) / 2) + 1, 1, 6);
        var (sr, sg, sb) = (r / samples, g / samples, b / samples);
        for (var s = 0; s < samples; s++)
        {
            var k = (s + 1f) / samples;
            var x = (fromX + (toX - fromX) * k) * _height - 0.5f;
            var y = (fromY + (toY - fromY) * k) * _height - 0.5f;
            if (split > 0.3f)
            {
                Splat(x + split, y, 0, sr);
                Splat(x, y, 1, sg);
                Splat(x - split, y, 2, sb);
            }
            else
            {
                Splat(x, y, 0, sr);
                Splat(x, y, 1, sg);
                Splat(x, y, 2, sb);
            }
        }
    }

    // Light added to the four pixels around a point, each by how near it is, so a point between pixels is not jagged.
    private void Splat(float x, float y, int channel, float amount)
    {
        var (left, top) = ((int)MathF.Floor(x), (int)MathF.Floor(y));
        var (fx, fy) = (x - left, y - top);
        Add(left, top, channel, amount * (1 - fx) * (1 - fy));
        Add(left + 1, top, channel, amount * fx * (1 - fy));
        Add(left, top + 1, channel, amount * (1 - fx) * fy);
        Add(left + 1, top + 1, channel, amount * fx * fy);
    }

    private void Add(int x, int y, int channel, float amount)
    {
        if ((uint)x < (uint)_width && (uint)y < (uint)_height)
            _light[(y * _width + x) * 3 + channel] += amount;
    }
}
