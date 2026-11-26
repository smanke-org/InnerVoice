using System.Globalization;

namespace InnerVoice.Core;

/// The words and billable characters shown beside the book's name, and the cost tooltip.
/// Same words as the Mac's `BookCounts` (Sources/InnerVoiceKit/BookCounts.swift).
public static class BookCounts
{
    static readonly CultureInfo US = CultureInfo.GetCultureInfo("en-US");

    public static string Label(int words, int characters) =>
        string.Create(US, $"{words:N0} words · {characters:N0} characters");

    /// What sending the whole book once costs with each cloud engine, the book's own engine
    /// first. `gemini`, `eleven`: the models chosen in Settings.
    public static string CostHelp(int n, VoiceEngine engine, GeminiModel gemini, DateTime? date = null, ElevenLabsModel? eleven = null)
    {
        string Money(double d) => d.ToString("C2", US);
        var lines = new List<(VoiceEngine Engine, string Text)>
        {
            (VoiceEngine.Elevenlabs, string.Create(US, $"ElevenLabs: about {(eleven?.Credits(n) ?? n):N0} credits") + (eleven is null ? "" : $" ({eleven.Name})")),
            (VoiceEngine.Speechify, $"Speechify: about {Money(SpeechifyTTS.EstimatedDollars(n))} at pay-as-you-go"),
            (VoiceEngine.Gemini, $"Gemini TTS: about {Money(GeminiTTS.EstimatedSeconds(n) * gemini.DollarsPerSecond(date))} ({(gemini == GeminiModel.Flash ? "Flash" : "Flash-Lite")}, paid tier)"),
        };
        var i = lines.FindIndex(l => l.Engine == engine);
        if (i > 0) { var l = lines[i]; lines.RemoveAt(i); lines.Insert(0, l); }
        var out_ = string.Create(US, $"{n:N0} billable characters: letters, numbers, spaces and punctuation, as cloud voices count them.\n\nThe whole book once with a cloud voice:\n")
            + string.Join("\n", lines.Select(l => "• " + l.Text + (l.Engine == engine ? " (this book’s engine)" : "")));
        if (!engine.IsCloud()) out_ += "\n\nThis book’s engine runs on this computer: no charge.";
        out_ += "\n\nRetakes and changed lines cost again; your plan’s allowance isn’t counted.";
        return out_;
    }
}
