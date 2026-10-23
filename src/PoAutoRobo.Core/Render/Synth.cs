namespace PoAutoRobo.Core.Render;

/// <summary>
/// The video's own sounds, worked out as numbers: the intro's sting and the outro's music when the series has none of
/// its own, and the rush of air into each new beat. Nothing is shipped as a file, nothing is paid for, and the same request always gives the
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
    /// An intro sting, <paramref name="length"/> long, for a robot being trained: a band of noise climbing for two
    /// seconds under servo moves and quickening data blips, then a low blow, a bright chord ringing out and small high
    /// notes scattered left and right as the swarm settles.
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

        // The robot waking for its lesson: three servo moves, each a small motor's buzz gliding up as a joint turns, from
        // one side then the other; and a run of data blips quickening into the hit, as a machine finishing its checks.
        double[] servos = [0.35, 0.95, 1.5];
        for (var s = 0; s < servos.Length; s++)
        {
            var (start, run, pan) = ((int)(servos[s] * Rate), (int)(0.28 * Rate), s % 2 == 0 ? 0.25 : 0.75);
            var motor = 0.0;
            for (var i = start; i < Math.Min(hit, start + run); i++)
            {
                var x = (double)(i - start) / run;
                motor += (180 + 220 * x) / Rate;
                var buzz = (motor % 1 * 2 - 1) * Math.Sin(Math.PI * x) * 0.16; // a sawtooth: the hard edge of a small motor
                left[i] += (float)(buzz * Math.Cos(pan * Math.PI / 2));
                right[i] += (float)(buzz * Math.Sin(pan * Math.PI / 2));
            }
        }
        double[] blips = [880.00, 1174.66, 1318.51, 1760.00];
        for (var at = 0.7; at < Hit.TotalSeconds - 0.08; at += 0.16 - 0.08 * at / Hit.TotalSeconds)
        {
            var (start, pitch) = ((int)(at * Rate), blips[random.Next(blips.Length)]);
            for (var i = start; i < Math.Min(hit, start + Rate / 25); i++)
            {
                var t = (double)(i - start) / Rate;
                var blip = Math.Sign(Math.Sin(2 * Math.PI * pitch * t)) * Math.Exp(-t * 40) * 0.06; // a square wave: a machine's beep
                left[i] += (float)blip;
                right[i] += (float)blip;
            }
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
    /// The outro's music when the series has none of its own, <paramref name="length"/> long: a robot lab winding down
    /// after the lesson. A soft pad moves through A, F sharp minor, D and E; quick bright blips run up each chord, left
    /// and right in turn; a low pulse keeps the beat. It comes in softly and fades out over its last two seconds.
    /// </summary>
    /// <returns>A complete 16-bit stereo WAV file.</returns>
    public static byte[] OutroTune(TimeSpan length)
    {
        var count = (int)(length.TotalSeconds * Rate);
        var (left, right) = (new float[count], new float[count]);
        const double Beat = 60.0 / 112; // a steady walking pace
        (double Root, double[] Triad)[] chords =
        [
            (110.00, [220.00, 277.18, 329.63]), (92.50, [185.00, 220.00, 277.18]),
            (73.42, [146.83, 185.00, 220.00]), (82.41, [164.81, 207.65, 246.94]),
        ];
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / Rate;
            var bar = (int)(t / (4 * Beat));
            var (root, triad) = chords[bar % chords.Length];
            var inBar = t - bar * 4 * Beat;

            // The pad: each note of the chord, soft, a hair apart on each side, swelling in at each change.
            var swell = Math.Min(1, inBar / 0.3) * 0.07;
            var (padLeft, padRight) = (0.0, 0.0);
            foreach (var note in triad)
            {
                padLeft += (Math.Sin(2 * Math.PI * note * 0.998 * t) + 0.3 * Math.Sin(4 * Math.PI * note * 0.998 * t)) * swell;
                padRight += (Math.Sin(2 * Math.PI * note * 1.002 * t) + 0.3 * Math.Sin(4 * Math.PI * note * 1.002 * t)) * swell;
            }

            // The blips: eighth notes running up the chord two octaves higher, like a machine at work, panned in turn.
            var step = (int)(inBar / (Beat / 2));
            var sinceBlip = inBar - step * Beat / 2;
            var blipPitch = triad[step % triad.Length] * 4;
            var blip = (Math.Sin(2 * Math.PI * blipPitch * sinceBlip) + Math.Sin(6 * Math.PI * blipPitch * sinceBlip) / 3) * Math.Exp(-sinceBlip * 14) * 0.06;
            var blipPan = step % 2 == 0 ? 0.3 : 0.7;

            // The pulse: the chord's root, low and short, on every beat.
            var sinceBeat = inBar % Beat;
            var pulse = Math.Sin(2 * Math.PI * root * sinceBeat) * Math.Exp(-sinceBeat * 7) * 0.22;

            left[i] = (float)(padLeft + pulse + blip * Math.Cos(blipPan * Math.PI / 2));
            right[i] = (float)(padRight + pulse + blip * Math.Sin(blipPan * Math.PI / 2));
        }
        for (var i = 0; i < Math.Min(count, Rate / 2); i++) // in softly, as the outro's picture fades up
            (left[i], right[i]) = (left[i] * i / (Rate / 2f), right[i] * i / (Rate / 2f));
        FadeOut(left, right, 2 * Rate);
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
