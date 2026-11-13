namespace InnerVoice.Core.Speech.Neural;

/// The log-mel spectrogram Qwen3-TTS's speaker encoder reads, as the Mac's mlx-audio computes
/// it (and Qwen's reference): reflect-padded frames of 1024 with a hop of 256, a Hann window,
/// magnitudes, 128 Slaney mel bands from 0 to 12 kHz, then the log.
public static class Mel
{
    public const int FftSize = 1024, Hop = 256, Bands = 128;

    /// Frames × 128, row-major.
    public static float[] LogMel(float[] audio, out int frames, int sampleRate = 24000)
    {
        int pad = (FftSize - Hop) / 2;
        var padded = ReflectPad(audio, pad);
        frames = padded.Length < FftSize ? 0 : 1 + (padded.Length - FftSize) / Hop;
        var filters = Filters(sampleRate, FftSize, Bands, 0, 12000);
        var window = Hann(FftSize);
        int bins = FftSize / 2 + 1;
        var mel = new float[frames * Bands];
        var re = new double[FftSize];
        var im = new double[FftSize];
        var mag = new double[bins];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < FftSize; i++) { re[i] = padded[f * Hop + i] * window[i]; im[i] = 0; }
            Fft(re, im);
            for (int k = 0; k < bins; k++) mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k] + 1e-9);
            for (int m = 0; m < Bands; m++)
            {
                double s = 0;
                var row = filters[m];
                for (int k = 0; k < bins; k++) s += row[k] * mag[k];
                mel[f * Bands + m] = (float)Math.Log(Math.Max(s, 1e-5));
            }
        }
        return mel;
    }

    static float[] ReflectPad(float[] x, int pad)
    {
        if (x.Length <= pad) pad = Math.Max(0, x.Length - 1);
        var y = new float[x.Length + 2 * pad];
        for (int i = 0; i < pad; i++) y[i] = x[pad - i];
        Array.Copy(x, 0, y, pad, x.Length);
        for (int i = 0; i < pad; i++) y[pad + x.Length + i] = x[x.Length - 2 - i];
        return y;
    }

    /// The periodic Hann window (torch's and MLX's default).
    static double[] Hann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        return w;
    }

    // Slaney's mel scale (librosa's htk=False).
    static double HzToMel(double hz)
    {
        const double fsp = 200.0 / 3, minLogHz = 1000, minLogMel = minLogHz / fsp;
        double logstep = Math.Log(6.4) / 27;
        return hz >= minLogHz ? minLogMel + Math.Log(hz / minLogHz) / logstep : hz / fsp;
    }

    static double MelToHz(double mel)
    {
        const double fsp = 200.0 / 3, minLogHz = 1000, minLogMel = minLogHz / fsp;
        double logstep = Math.Log(6.4) / 27;
        return mel >= minLogMel ? minLogHz * Math.Exp(logstep * (mel - minLogMel)) : fsp * mel;
    }

    /// librosa.filters.mel with Slaney normalization: bands × (n_fft/2 + 1).
    public static double[][] Filters(int sampleRate, int nFft, int bands, double fMin, double fMax)
    {
        int bins = nFft / 2 + 1;
        var fft = Enumerable.Range(0, bins).Select(k => k * (double)sampleRate / nFft).ToArray();
        double lo = HzToMel(fMin), hi = HzToMel(fMax);
        var edges = Enumerable.Range(0, bands + 2).Select(i => MelToHz(lo + (hi - lo) * i / (bands + 1))).ToArray();
        var w = new double[bands][];
        for (int m = 0; m < bands; m++)
        {
            w[m] = new double[bins];
            double d0 = edges[m + 1] - edges[m], d1 = edges[m + 2] - edges[m + 1];
            double norm = 2.0 / (edges[m + 2] - edges[m]);
            for (int k = 0; k < bins; k++)
            {
                double lower = (fft[k] - edges[m]) / d0, upper = (edges[m + 2] - fft[k]) / d1;
                w[m][k] = Math.Max(0, Math.Min(lower, upper)) * norm;
            }
        }
        return w;
    }

    /// In-place radix-2 FFT (n a power of two).
    public static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
