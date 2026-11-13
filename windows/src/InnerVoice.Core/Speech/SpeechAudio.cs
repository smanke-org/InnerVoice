// Audio in the app's one format: 24 kHz mono, 32-bit float (what Qwen3-TTS and Orpheus make;
// Windows' voices and ElevenLabs are converted). The Mac does this with AVAudioEngine and
// AVAudioUnitTimePitch; here it's plain C#, so it runs the same on every PC and in tests.

namespace InnerVoice.Core.Speech;

public static class SpeechAudio
{
    public const int SampleRate = 24_000;

    public static double Seconds(float[] samples) => samples.Length / (double)SampleRate;

    public static float[] Silence(double seconds) => new float[Math.Max(1, (int)(seconds * SampleRate))];

    /// The audio without the silence a voice leaves before and after the words, so the pauses
    /// between pieces are what the narrator set. Keeps 20 ms at each end.
    public static float[] Trimmed(float[] x, float threshold = 0.008f)
    {
        int n = x.Length;
        if (n == 0) return x;
        int first = 0, last = n - 1;
        while (first < n && Math.Abs(x[first]) < threshold) first++;
        while (last > first && Math.Abs(x[last]) < threshold) last--;
        if (first >= last) return x;
        int pad = (int)(SampleRate * 0.02);
        first = Math.Max(0, first - pad);
        last = Math.Min(n - 1, last + pad);
        int count = last - first + 1;
        if (count >= n) return x;
        return x[first..(last + 1)];
    }

    /// Brings every line to the same loudness (models vary by 10 dB or more from one line to
    /// the next), without letting the peaks clip. As the Mac's ModelSpeech.level.
    public static float[] Leveled(float[] samples, float targetDB = -20)
    {
        if (samples.Length == 0) return samples;
        // Loudness of the speech itself: the louder half of 20 ms frames, so pauses don't count.
        const int frame = 480;
        var energies = new List<float>();
        for (int i = 0; i < samples.Length; i += frame)
        {
            int end = Math.Min(samples.Length, i + frame);
            float e = 0;
            for (int k = i; k < end; k++) e += samples[k] * samples[k];
            energies.Add(e / (end - i));
        }
        energies.Sort();
        var loud = energies.Skip(energies.Count / 2).ToList();
        float rms = MathF.Sqrt(loud.Sum() / Math.Max(1, loud.Count));
        if (rms <= 1e-5f) return samples;
        float gain = MathF.Pow(10, targetDB / 20) / rms;
        float peak = samples.Max(MathF.Abs);
        if (peak * gain > 0.95f) gain = 0.95f / peak;
        return samples.Select(s => s * gain).ToArray();
    }

    /// Scales the audio (a tone's loudness: whispered, sad).
    public static float[] Scaled(float[] x, float gain) => gain == 1 ? x : x.Select(s => s * gain).ToArray();

    /// Joins pieces end to end.
    public static float[] Join(IEnumerable<float[]> pieces)
    {
        var list = pieces.ToList();
        var output = new float[list.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in list)
        {
            Array.Copy(p, 0, output, at, p.Length);
            at += p.Length;
        }
        return output;
    }

    /// Mono audio at any rate to the app's rate. Downsampling low-passes first (a windowed
    /// sinc), so 44.1 or 48 kHz voices don't alias.
    public static float[] Resampled(float[] x, int fromRate, int toRate = SampleRate)
    {
        if (fromRate == toRate || x.Length == 0) return x;
        if (toRate < fromRate) x = LowPass(x, 0.45 * toRate / fromRate);
        return Interpolated(x, (double)fromRate / toRate);
    }

    /// Reads the input at `step` samples per output sample (Catmull-Rom). A step above 1
    /// raises the pitch and shortens the audio.
    public static float[] Interpolated(float[] x, double step)
    {
        if (x.Length == 0 || step <= 0) return x;
        int n = (int)Math.Floor((x.Length - 1) / step) + 1;
        var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            double pos = i * step;
            int k = (int)pos;
            float t = (float)(pos - k);
            float p0 = x[Math.Max(0, k - 1)], p1 = x[k], p2 = x[Math.Min(x.Length - 1, k + 1)], p3 = x[Math.Min(x.Length - 1, k + 2)];
            y[i] = p1 + 0.5f * t * (p2 - p0 + t * (2 * p0 - 5 * p1 + 4 * p2 - p3 + t * (3 * (p1 - p2) + p3 - p0)));
        }
        return y;
    }

    /// A windowed-sinc low-pass; `cutoff` is a fraction of the sample rate (below 0.5).
    static float[] LowPass(float[] x, double cutoff, int taps = 63)
    {
        var h = new float[taps];
        int m = taps / 2;
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            int k = i - m;
            double sinc = k == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * k) / (Math.PI * k);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            h[i] = (float)(sinc * w);
            sum += h[i];
        }
        for (int i = 0; i < taps; i++) h[i] /= (float)sum;
        var y = new float[x.Length];
        for (int n = 0; n < x.Length; n++)
        {
            float acc = 0;
            for (int i = 0; i < taps; i++)
            {
                int j = n + i - m;
                if (j >= 0 && j < x.Length) acc += h[i] * x[j];
            }
            y[n] = acc;
        }
        return y;
    }

    /// Faster or slower without changing the pitch (WSOLA): `rate` 2 is twice as fast. Used
    /// for Play's speed and for shading a voice's pace.
    public static float[] TimeStretched(float[] x, double rate)
    {
        if (Math.Abs(rate - 1) < 1e-4 || x.Length == 0) return x;
        const int N = 960;          // 40 ms frames
        const int Hs = N / 2;       // 50% overlap out
        const int Tolerance = 240;  // ±10 ms search
        double Ha = Hs * rate;
        int outLength = (int)(x.Length / rate);
        var y = new float[outLength + N];
        var window = new float[N];
        for (int i = 0; i < N; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));   // periodic Hann: sums to 1 at 50%
        // A clip shorter than two frames: resampling is close enough.
        if (x.Length < 2 * N) return Interpolated(x, rate);
        int prev = 0, maxStart = x.Length - N;
        for (int k = 0; ; k++)
        {
            int outPos = k * Hs;
            if (outPos >= outLength) break;
            int nominal = (int)(k * Ha);
            int start = Math.Min(nominal, maxStart);
            if (k > 0)
            {
                // The candidate most like the natural continuation of the last frame.
                int natural = prev + Hs;
                int lo = Math.Max(0, nominal - Tolerance), hi = Math.Min(maxStart, nominal + Tolerance);
                if (hi < lo) lo = hi = maxStart;
                double best = double.NegativeInfinity;
                start = lo;
                for (int c = lo; c <= hi; c += 2)
                {
                    double corr = 0;
                    for (int i = 0; i < N; i += 4)
                    {
                        int a = natural + i;
                        if (a >= x.Length) break;
                        corr += x[c + i] * x[a];
                    }
                    if (corr > best) { best = corr; start = c; }
                }
            }
            for (int i = 0; i < N && outPos + i < y.Length; i++) y[outPos + i] += x[start + i] * window[i];
            prev = start;
        }
        // The first half-frame had no partner: it faded in. Put the start back as it was.
        for (int i = 0; i < Math.Min(Hs, x.Length) && i < y.Length; i++) y[i] = x[i];
        return y[..Math.Min(y.Length, outLength)];
    }
}

/// Shading one voice for a character (Casting Style, docs/CASTING-STYLE.md §6): pitch in
/// semitones and pace in steps. Engines that can't shade themselves get the finished audio
/// shifted here (as the Mac's ShadeAudio does for Orpheus and ElevenLabs).
public static class ShadeAudio
{
    /// How much one pace step changes speed (±2 steps: ±16%).
    public static double PaceRate(int pace) => 1 + 0.08 * pace;

    /// One pitch step is a semitone.
    public static double PitchMultiplier(int steps) => Math.Pow(2, steps / 12.0);

    /// The audio shifted `semitones` and sped up or slowed by `pace` steps.
    public static float[] Apply(float[] input, int semitones, int pace)
    {
        if ((semitones == 0 && pace == 0) || input.Length == 0) return input;
        double f = PitchMultiplier(semitones);
        // Resampling by f moves the pitch by f and the length by 1/f; the stretch puts the
        // length where the pace wants it.
        var shifted = semitones == 0 ? input : SpeechAudio.Interpolated(input, f);
        return SpeechAudio.TimeStretched(shifted, PaceRate(pace) / (semitones == 0 ? 1 : f));
    }
}
