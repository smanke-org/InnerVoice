namespace InnerVoice.Core.Speech.Neural;

/// How high a voice is: the median pitch of its voiced 40 ms frames (normalized
/// autocorrelation, 60-400 Hz), and how much of it is voiced.
public static class Pitch
{
    public static (double Voiced, double Hz) Analyze(float[] x, int rate = SpeechAudio.SampleRate)
    {
        int frame = rate / 25, lo = rate / 400, hi = rate / 60;
        var pitches = new List<double>();
        var rs = new double[hi + 2];
        int frames = 0;
        for (int i = 0; i + frame <= x.Length; i += frame / 2)
        {
            frames++;
            double e = 0;
            for (int k = 0; k < frame; k++) e += x[i + k] * x[i + k];
            if (e / frame < 1e-5) continue;
            double best = 0;
            for (int l = lo; l <= hi; l++)
            {
                double c = 0, n1 = 0, n2 = 0;
                for (int k = 0; k + l < frame; k++) { c += x[i + k] * x[i + k + l]; n1 += x[i + k] * x[i + k]; n2 += x[i + k + l] * x[i + k + l]; }
                rs[l] = c / Math.Sqrt(n1 * n2 + 1e-12);
                best = Math.Max(best, rs[l]);
            }
            // The shortest lag near the best avoids taking twice the period (an octave too low).
            int lag = 0;
            for (int l = lo + 1; l < hi && lag == 0; l++)
                if (rs[l] >= 0.9 * best && rs[l] >= rs[l - 1] && rs[l] >= rs[l + 1]) lag = l;
            if (best > 0.6 && lag > 0) pitches.Add(rate / (double)lag);
        }
        pitches.Sort();
        return (frames == 0 ? 0 : pitches.Count / (double)frames, pitches.Count == 0 ? 0 : pitches[pitches.Count / 2]);
    }
}
