namespace PoAutoRobo.Core.Render;

/// <summary>
/// The video's own sounds, worked out as numbers: the intro's sting when the series has none of its own, and the rush
/// of air into each new beat. Nothing is shipped as a file, nothing is paid for, and the same request always gives the
/// same sound, so what is made from them caches like anything else.
/// </summary>
public static class Synth
{
    public const int Rate = 48000;

    /// <summary>When the made-up sting lands, and the swarm of light in the intro meets: the two are one moment.</summary>
    public static readonly TimeSpan Hit = TimeSpan.FromSeconds(2.2);

    /// <summary>How long a whoosh runs.</summary>
    public static readonly TimeSpan WhooshLength = TimeSpan.FromSeconds(0.9);

    /// <summary>How far into a whoosh the rush is loudest. That point is laid on the cut, so the new beat arrives on it.</summary>
    public static readonly TimeSpan WhooshPeak = TimeSpan.FromSeconds(0.6);

    /// <summary>
    /// An intro sting, <paramref name="length"/> long: a band of noise climbing for two seconds, then a low blow, a
    /// bright chord ringing out and small high notes scattered left and right as the swarm settles.
    /// </summary>
    /// <returns>A complete 16-bit stereo WAV file.</returns>
    public static byte[] Sting(TimeSpan length)
    {
        var count = (int)(length.TotalSeconds * Rate);
        var (left, right) = (new float[count], new float[count]);
        var hit = Math.Min(count, (int)(Hit.TotalSeconds * Rate));
        var random = new Random(7);

        // The climb: each side has its own noise, so it is wide, through a narrow band that rises from 250 Hz to 6 kHz.
        var (bandLeft, bandRight) = (new Band(), new Band());
        var phase = 0.0;
        for (var i = 0; i < hit; i++)
        {
            var x = (double)i / hit;
            var centre = 250 * Math.Pow(24, x);
            var swell = x * x * x;
            phase += (55 + 55 * x) / Rate; // a low tone rising an octave beneath it
            var tone = Math.Sin(2 * Math.PI * phase) * 0.25 * swell;
            left[i] += (float)(bandLeft.Pass(Noise(random), centre, 5) * 0.5 * swell + tone);
            right[i] += (float)(bandRight.Pass(Noise(random), centre, 5) * 0.5 * swell + tone);
        }

        // The blow: a thump falling in pitch, a crack of noise, and the chord (A with an added B), a little apart on each side.
        double[] chord = [110.00, 164.81, 220.00, 277.18, 329.63, 493.88];
        phase = 0;
        for (var i = hit; i < count; i++)
        {
            var t = (double)(i - hit) / Rate;
            phase += (40 + 80 * Math.Exp(-t * 18)) / Rate;
            var thump = Math.Sin(2 * Math.PI * phase) * Math.Exp(-t * 4.5) * 0.9;
            var crack = Noise(random) * Math.Exp(-t * 60) * 0.5;
            var (ringLeft, ringRight) = (0.0, 0.0);
            foreach (var note in chord)
            {
                ringLeft += Bright(note * 0.997, t);
                ringRight += Bright(note * 1.003, t);
            }
            var ring = Math.Min(1, t / 0.005) * Math.Exp(-t * 1.1) * 0.1;
            left[i] += (float)(thump + crack + ringLeft * ring);
            right[i] += (float)(thump + crack + ringRight * ring);
        }

        // Glitter: high notes from a five-note scale, each placed somewhere between the speakers.
        double[] high = [1760.00, 1975.53, 2217.46, 2637.02, 2959.96];
        for (var n = 0; n < 14 && hit < count; n++)
        {
            var start = hit + (int)((0.05 + random.NextDouble() * 1.5) * Rate);
            var (pitch, pan) = (high[random.Next(high.Length)], random.NextDouble());
            for (var i = start; i < Math.Min(count, start + Rate / 2); i++)
            {
                var t = (double)(i - start) / Rate;
                var ping = Math.Sin(2 * Math.PI * pitch * t) * Math.Exp(-t * 9) * 0.12;
                left[i] += (float)(ping * Math.Cos(pan * Math.PI / 2));
                right[i] += (float)(ping * Math.Sin(pan * Math.PI / 2));
            }
        }

        FadeOut(left, right, (int)(0.4 * Rate)); // ends in silence, so the join into the first clip never clicks
        return Wave(left, right);
    }

    /// <summary>
    /// A rush of air passing from left to right, loudest at <see cref="WhooshPeak"/>: noise through a wide band that
    /// rises and falls with it.
    /// </summary>
    /// <returns>A complete 16-bit stereo WAV file.</returns>
    public static byte[] Whoosh()
    {
        var count = (int)(WhooshLength.TotalSeconds * Rate);
        var (left, right) = (new float[count], new float[count]);
        var peak = WhooshPeak.TotalSeconds / WhooshLength.TotalSeconds;
        var random = new Random(11);
        var band = new Band();
        for (var i = 0; i < count; i++)
        {
            var x = (double)i / count;
            var (loud, centre) = x < peak
                ? (Math.Pow(x / peak, 2), 300 * Math.Pow(2500 / 300.0, x / peak))
                : (Math.Exp(-(x - peak) * 12), 2500 * Math.Pow(900 / 2500.0, (x - peak) / (1 - peak)));
            var value = band.Pass(Noise(random), centre, 1.5) * loud * 0.7;
            var pan = (0.1 + 0.8 * x) * Math.PI / 2; // equal-power, so it keeps its loudness as it crosses
            left[i] = (float)(value * Math.Cos(pan));
            right[i] = (float)(value * Math.Sin(pan));
        }
        FadeOut(left, right, Rate / 50);
        return Wave(left, right);
    }

    // A note with its first six overtones, each quieter than the last: brighter than a bare tone, softer than a buzz.
    private static double Bright(double pitch, double t)
    {
        var sum = 0.0;
        for (var k = 1; k <= 6; k++)
            sum += Math.Sin(2 * Math.PI * pitch * k * t) / k;
        return sum;
    }

    private static double Noise(Random random) => random.NextDouble() * 2 - 1;

    private static void FadeOut(float[] left, float[] right, int samples)
    {
        for (var i = Math.Max(0, left.Length - samples); i < left.Length; i++)
        {
            var keep = (float)(left.Length - i) / samples;
            (left[i], right[i]) = (left[i] * keep, right[i] * keep);
        }
    }

    /// <summary>The two channels as a WAV file, gently rounded off where they would clip and brought to just under full scale.</summary>
    private static byte[] Wave(float[] left, float[] right)
    {
        var peak = 1e-6f;
        for (var i = 0; i < left.Length; i++)
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        var gain = 1.2f / peak;

        using var stream = new MemoryStream();
        using var file = new BinaryWriter(stream);
        WavInfo.WriteHeader(file, Rate, channels: 2, left.Length);
        var round = (float)Math.Tanh(1.2);
        for (var i = 0; i < left.Length; i++)
        {
            file.Write((short)(MathF.Tanh(left[i] * gain) / round * 0.89f * short.MaxValue));
            file.Write((short)(MathF.Tanh(right[i] * gain) / round * 0.89f * short.MaxValue));
        }
        file.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Lets through a band of frequencies around a centre that may move from one sample to the next. A state-variable
    /// filter: two running sums standing for a resonator, cheap and steady at these settings.
    /// </summary>
    private sealed class Band
    {
        private double _low, _band;

        /// <param name="sharpness">How narrow the band is: 1 broad, 5 narrow enough to hear as a pitch.</param>
        public double Pass(double input, double centre, double sharpness)
        {
            var f = 2 * Math.Sin(Math.PI * Math.Min(centre, Rate / 7.0) / Rate);
            _low += f * _band;
            var high = input - _low - _band / sharpness;
            _band += f * high;
            return _band;
        }
    }
}
