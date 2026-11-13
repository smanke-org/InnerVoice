using System.Security.Cryptography;
using System.Text;

namespace InnerVoice.Core.Speech.Neural;

/// Picks the next token from a model's scores, the way the Mac's MLX models do, from a seeded
/// random source so the same line with the same voice comes out the same.
public sealed class Sampler(ulong seed)
{
    ulong state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    /// The Mac's seed for a string: the first 8 bytes of its SHA-256.
    public static ulong Seed(string s) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// SplitMix64: small, fast and the same on every computer.
    public double NextDouble()
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    /// A standard normal draw (Box–Muller): Pocket TTS's noise.
    public double NextGaussian()
    {
        double u = 1 - NextDouble(), v = NextDouble();
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }

    /// Lowers the scores of tokens already said (scores above zero are divided, below zero
    /// multiplied), as Hugging Face and MLX do.
    public static void Penalize(Span<float> logits, IEnumerable<int> seen, float penalty)
    {
        if (penalty == 1) return;
        foreach (var t in seen.Distinct())
        {
            if ((uint)t >= (uint)logits.Length) continue;
            logits[t] = logits[t] > 0 ? logits[t] / penalty : logits[t] * penalty;
        }
    }

    /// Samples with temperature, then top-k (0: all), then top-p (1: all). Temperature 0 picks
    /// the best.
    public int Sample(ReadOnlySpan<float> logits, float temperature, int topK = 0, float topP = 1)
    {
        int n = logits.Length;
        if (temperature <= 0)
        {
            int best = 0;
            for (int i = 1; i < n; i++) if (logits[i] > logits[best]) best = i;
            return best;
        }
        var idx = new List<int>(n);
        for (int i = 0; i < n; i++) if (!float.IsNegativeInfinity(logits[i])) idx.Add(i);
        if (idx.Count == 0) return 0;
        var copy = logits.ToArray();
        idx.Sort((a, b) => copy[b].CompareTo(copy[a]));
        if (topK > 0 && idx.Count > topK) idx.RemoveRange(topK, idx.Count - topK);
        double max = copy[idx[0]] / temperature;
        var p = new double[idx.Count];
        double sum = 0;
        for (int i = 0; i < idx.Count; i++) { p[i] = Math.Exp(copy[idx[i]] / temperature - max); sum += p[i]; }
        int count = idx.Count;
        if (topP < 1)
        {
            double cum = 0;
            for (int i = 0; i < idx.Count; i++)
            {
                cum += p[i] / sum;
                if (cum >= topP) { count = i + 1; break; }
            }
            sum = 0;
            for (int i = 0; i < count; i++) sum += p[i];
        }
        double r = NextDouble() * sum;
        for (int i = 0; i < count; i++)
        {
            r -= p[i];
            if (r <= 0) return idx[i];
        }
        return idx[count - 1];
    }
}
