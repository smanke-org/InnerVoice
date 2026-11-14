// Ported from Sources/InnerVoiceKit/Chatterbox.swift.

namespace InnerVoice.Core;

/// Chatterbox (Resemble AI, MIT), the third engine on this PC that clones (engine
/// "chatterbox"). It shares Pocket TTS's stock clips and the voices cloned on this PC
/// (voiceID "stock:<name>" or "clone:<id>"), plus its own default voice. Its speech tokenizer
/// and the flow decoder come from Alibaba's CosyVoice and its speaker encoder (CAM++) from
/// Alibaba's 3D-Speaker, so it carries a "Uses parts developed in China" label (the user chose
/// that on 2026-11-14, like Qwen3-TTS). It can't design a voice from a description.
public static class ChatterboxVoice
{
    /// Chatterbox's own default voice, as a stock clip (Resources/StockVoices/chatterbox.wav,
    /// the Mac's chatterbox.m4a: made once from the default voice Resemble ships with
    /// Chatterbox Turbo).
    public const string BuiltInID = PocketVoice.StockPrefix + "chatterbox";
}

/// How Chatterbox says a line in each tone. Chatterbox has no tone words; it has two dials
/// (the values are the model's own, 0…1):
/// - `Exaggeration` (emotion exaggeration): 0.5 is its default, an even read. Higher is more
///   intense and more animated; it also speeds the speech up, and past about 0.9 it gets
///   unsteady (Resemble's notes on the model), so nothing here goes above 0.8. Lower is
///   flatter and calmer.
/// - `CfgWeight` (how closely it follows the clip's pacing): 0.5 is its default. Resemble's
///   advice for dramatic lines is a higher exaggeration with a lower weight (about 0.7 and
///   0.3): the lower weight slows the delivery down again, so an intense line doesn't rush.
///
/// The values were chosen by reasoning from those two notes, not by listening:
/// - Angry and excited are the most intense, so the highest exaggeration with the lowest
///   weight (0.8/0.3 and 0.75/0.35).
/// - Scared is tense but held in (0.7/0.4); sarcastic and confused only color the words a
///   little (0.6 and 0.55, weight 0.45: the words carry them).
/// - Tender and sad are quieter than an even read: exaggeration below 0.5 flattens them;
///   sad gets the lower weight (0.35/0.4) so it comes out slower than tender (0.4/0.45).
/// - Whispered: Chatterbox can't whisper. The lowest exaggeration (0.3) and a quieter line
///   (`Volume` 0.7, about −3 dB after leveling) are the nearest it comes.
/// - Narration is always read even (0.5/0.5), like an audiobook narrator.
///
/// On this PC the ONNX export (onnx-community/chatterbox-ONNX) takes the exaggeration but has
/// no classifier-free guidance pass, so `CfgWeight` shapes the pace instead (`Rate`): a lower
/// weight slows the finished audio a little, as it slows the delivery on the Mac.
public readonly record struct ChatterboxDelivery(double Exaggeration, double CfgWeight, double Volume = 1)
{
    public static readonly ChatterboxDelivery Even = new(0.5, 0.5, 1);

    /// `narration`: the line is narration (read even whatever its tone says).
    public static ChatterboxDelivery For(Tone tone, bool narration = false)
    {
        if (narration) return Even;
        return tone switch
        {
            Tone.Neutral => Even,
            Tone.Excited => new(0.75, 0.35),
            Tone.Angry => new(0.8, 0.3),
            Tone.Scared => new(0.7, 0.4),
            Tone.Sarcastic => new(0.6, 0.45),
            Tone.Confused => new(0.55, 0.45),
            Tone.Tender => new(0.4, 0.45),
            Tone.Sad => new(0.35, 0.4),
            _ => new(0.3, 0.5, 0.7), // whispered
        };
    }

    /// The speed the finished audio is stretched to on this PC, standing in for the CFG weight
    /// the ONNX export can't take: 1 at the default weight (0.5), about 4% slower at 0.3.
    public double Rate => Math.Round(1 - (0.5 - CfgWeight) * 0.2, 3);
}

/// Chatterbox sometimes runs on after a short line: the line, a pause, then a second or so of
/// made-up speech ("The path was underwater" came back as 2 s of the line, 0.7 s of silence and
/// 1.5 s more; 2026-11-13). The run-on follows a clear pause once the line has had time to be
/// said, so the audio is cut there. A pause that comes before then (a comma in a long line) is
/// kept.
public static class ChatterboxRunOn
{
    /// About how long the text takes to say: 14 characters a second, at least 0.6 s.
    public static double ExpectedSeconds(string text) => Math.Max(0.6, text.Trim().Length / 14.0);

    /// The audio up to the first pause of 0.3 s or more that comes after at least 90% of the
    /// expected time of speech, with 0.1 s of the pause kept. Unchanged when there is no such
    /// pause or nothing follows it.
    public static float[] Trim(float[] samples, double sampleRate, string text)
    {
        int frame = Math.Max(1, (int)(sampleRate * 0.02));
        if (samples.Length <= frame * 10) return samples;
        var rms = new List<float>();
        for (int i = 0; i < samples.Length;)
        {
            int end = Math.Min(samples.Length, i + frame);
            float e = 0;
            for (int k = i; k < end; k++) e += samples[k] * samples[k];
            rms.Add(MathF.Sqrt(e / (end - i)));
            i = end;
        }
        float loudest = rms.Max();
        if (loudest <= 0) return samples;
        var silent = rms.Select(r => r < loudest * 0.03f).ToArray();
        double need = ExpectedSeconds(text) * 0.9;
        const int minGap = 15; // frames: 0.3 s
        int speech = 0; // frames of speech so far
        for (int f = 0; f < silent.Length;)
        {
            if (!silent[f]) { speech++; f++; continue; }
            int g = f;
            while (g < silent.Length && silent[g]) g++;
            if (speech > 0 && g < silent.Length && g - f >= minGap && speech * 0.02 >= need)
                return samples[..Math.Min(samples.Length, (f + 5) * frame)];
            f = g;
        }
        return samples;
    }
}
