namespace InnerVoice.Core.Speech;

/// The part of a recording a voice cloned on this PC learns from (the Mac's CloneClip). Pocket
/// TTS clones well from about 5 to 15 seconds of one person speaking (Kyutai's own voice clips
/// are 10 s), so a longer recording is cut at a pause between words. The words it says are
/// kept with it, for the narrator to check.
public static class CloneClip
{
    /// The longest clip the clone model is given.
    public const double Longest = 15;
    /// Shorter than this carries too little of the voice.
    public const double Shortest = 4;

    /// One recognized word: when it's said, and where it ends in the transcript (UTF-16).
    public readonly record struct Word(double Start, double End, int TextEnd);

    public readonly record struct Span(double Start, double End, int TextEnd)
    {
        public double Seconds => End - Start;
    }

    /// From the first word up to `limit` seconds, ending at the longest pause after at least
    /// 8 seconds (or after `Shortest`, for a short recording). null when there are no words.
    public static Span? Cut(IReadOnlyList<Word> words, double limit = Longest)
    {
        if (words.Count == 0) return null;
        double start = Math.Max(0, words[0].Start - 0.15);
        var fits = Enumerable.Range(0, words.Count).Where(i => words[i].End - start <= limit).ToList();
        if (fits.Count == 0) return null;
        int last = fits[^1];
        Span CutAt(int i)
        {
            double after = i + 1 < words.Count ? words[i + 1].Start : words[i].End + 0.25;
            return new Span(start, Math.Min(words[i].End + 0.25, Math.Max(words[i].End, after)), words[i].TextEnd);
        }
        // Everything fits: use it all.
        if (last == words.Count - 1) return CutAt(last);
        double Gap(int i) => words[i + 1].Start - words[i].End;
        foreach (var minimum in new[] { Math.Min(8, limit - 1), Shortest })
        {
            var choices = fits.Where(i => words[i].End - start >= minimum).ToList();
            if (choices.Count > 0)
            {
                // The longest pause; the later word on a tie.
                int best = choices.Aggregate((a, b) => Gap(b) > Gap(a) || (Gap(b) == Gap(a) && b > a) ? b : a);
                return CutAt(best);
            }
        }
        return CutAt(last);
    }

    /// The transcript the span says, without trailing spaces.
    public static string Text(string transcript, int upTo) => transcript[..Math.Min(Math.Max(0, upTo), transcript.Length)].Trim();
}
