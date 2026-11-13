// Ported from Sources/InnerVoiceKit/AI/CostEstimate.swift. Windows has no tone pass, so
// Tag Speakers reads each passage once here (the Mac reads it twice: speakers, then tones).

using System.Globalization;

namespace InnerVoice.Core.AI;

/// What a Claude model costs, in US dollars per million tokens.
public readonly record struct ClaudePrice(double Input, double Output);

public static class ClaudePricing
{
    /// Anthropic's list prices for the built-in models. A model that isn't listed (new ones
    /// come from the Models API) shows tokens only.
    public static readonly IReadOnlyDictionary<string, ClaudePrice> PerMillion = new Dictionary<string, ClaudePrice>
    {
        ["claude-fable-5-2"] = new(8, 40),
        ["claude-opus-5-5"] = new(4, 20),
        ["claude-sonnet-5-5"] = new(2, 10),
        ["claude-haiku-5-5"] = new(0.10, 0.50),
    };

    public static ClaudePrice? Price(string modelID) => PerMillion.TryGetValue(modelID, out var p) ? p : null;
}

/// A rough count of what a Claude run sends and gets back, worked out on the PC from the text
/// it would send. Never asks Anthropic (counting tokens there would send the book).
public readonly record struct CostEstimate(int InputTokens = 0, int OutputTokens = 0)
{
    static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static CostEstimate operator +(CostEstimate a, CostEstimate b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens);

    public int TotalTokens => InputTokens + OutputTokens;

    /// null for a model without a known price.
    public double? Dollars(string modelID) =>
        ClaudePricing.Price(modelID) is { } p ? InputTokens / 1_000_000.0 * p.Input + OutputTokens / 1_000_000.0 * p.Output : null;

    /// "about $1.20", "less than $0.01", or "about 120,000 tokens" when the price isn't known.
    public string Phrase(string modelID)
    {
        if (Dollars(modelID) is not { } d) return $"about {RoundTokens(TotalTokens).ToString("N0", Us)} tokens";
        if (d < 0.01) return "less than $0.01";
        return "about " + Money(d);
    }

    /// One sentence for a confirmation: "Estimated cost: about $1.20 (about 450,000 tokens).
    /// This is an estimate made on your PC; your bill may differ."
    public string Sentence(string modelID)
    {
        var tokens = RoundTokens(TotalTokens).ToString("N0", Us);
        if (ClaudePricing.Price(modelID) is not null)
            return $"Estimated cost: {Phrase(modelID)} (about {tokens} tokens). This is an estimate made on your PC; your bill may differ.";
        return $"Estimated size: about {tokens} tokens. InnerVoice doesn't know this model's price; check it in the Claude Console. This is an estimate; your bill may differ.";
    }

    internal static string Money(double d) =>
        d < 10 ? "$" + d.ToString("0.00", Us) : "$" + ((int)Math.Round(d)).ToString("N0", Us);

    internal static int RoundTokens(int n) => n < 1000 ? Math.Max(n, 0) : (int)Math.Round(n / 1000.0, MidpointRounding.AwayFromZero) * 1000;
}

public static class CostEstimator
{
    /// About four characters of English prose to a token, plus a margin for the newer
    /// tokenizer, which uses more.
    public static int Tokens(string text) => (int)Math.Ceiling(text.Length / 4.0 * 1.2);

    public static int Tokens(int words) => (int)Math.Ceiling(words * 1.6);

    /// Claude thinks before it answers, and thinking is billed as output: allow for it.
    public const double Thinking = 2.0;

    /// Requests whose text is known: every message counts as input, and each reply is about
    /// `outputEach` tokens (before thinking).
    public static CostEstimate Requests(IEnumerable<IEnumerable<ChatMessage>> messages, int outputEach)
    {
        int input = 0, count = 0;
        foreach (var request in messages)
        {
            count++;
            foreach (var m in request) input += Tokens(m.Content) + 4;
        }
        return new CostEstimate(input, (int)(outputEach * count * Thinking));
    }

    /// Tag Speakers over these chapters (all when null): each passage with the instructions.
    public static CostEstimate TagSpeakers(Project project, IReadOnlyList<int>? chapters = null, int passageWords = 2500)
    {
        var picked = (chapters ?? Enumerable.Range(0, project.Chapters.Count).ToList()).Where(c => c >= 0 && c < project.Chapters.Count).ToList();
        var ids = picked.SelectMany(c => project.Chapters[c].Paragraphs.Select(p => p.Id)).ToHashSet();
        int words = picked.Sum(c => project.Chapters[c].WordCount);
        int lines = project.Lines.Count(l => ids.Contains(l.ParagraphID));
        int passages = picked.Sum(c => Math.Max(1, (int)Math.Ceiling(project.Chapters[c].WordCount / (double)passageWords)));
        int overhead = Tokens(AttributionPrompt.System) + 400; // instructions + cast list
        int input = Tokens(words) + passages * overhead;
        int output = 40 * passages + lines * 34;
        return new CostEstimate(input, (int)(output * Thinking));
    }

    /// Build Summaries for `characters` characters: up to four passages of the book about
    /// each (about 2,700 words in all), then a voice brief.
    public static CostEstimate BuildSummaries(Project project, int characters, int passageWords = 2500)
    {
        int paragraphs = Math.Max(1, project.Chapters.Sum(c => c.Paragraphs.Count));
        double perParagraph = project.WordCount / (double)paragraphs;
        double wordsEach = Math.Min(passageWords + 200, perParagraph * 48);
        int readEach = Tokens((int)wordsEach) + Tokens(SummaryPrompt.System) + 150;
        int briefEach = Tokens(VoiceBriefPrompt.System) + 900;
        int input = characters * (readEach + briefEach);
        int output = characters * (700 + 260);
        return new CostEstimate(input, (int)(output * Thinking));
    }
}
