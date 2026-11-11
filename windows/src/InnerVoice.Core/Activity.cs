// Ported from Sources/InnerVoiceKit/Activity.swift (without the iCloud device sharing).

using System.Globalization;

namespace InnerVoice.Core;

/// The per-day counts InnerVoice keeps for Settings › Activity: numbers only, never anything
/// from a book. Days are "yyyy-MM-dd" in local time.
public sealed class ActivityLog
{
    public string? FirstDay { get; set; }
    /// Day → key → count.
    public Dictionary<string, Dictionary<string, int>> Days { get; set; } = [];
    /// Key → count since counting began (old days are trimmed).
    public Dictionary<string, int> Lifetime { get; set; } = [];

    public static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTime? DateOf(string key) =>
        DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public void Record(string key, int count = 1, DateTime? at = null)
    {
        if (count <= 0) return;
        var day = Key(at ?? DateTime.Now);
        Lifetime[key] = Lifetime.GetValueOrDefault(key) + count;
        if (!Days.TryGetValue(day, out var perKey)) Days[day] = perKey = [];
        perKey[key] = perKey.GetValueOrDefault(key) + count;
        if (FirstDay is null || string.CompareOrdinal(day, FirstDay) < 0) FirstDay = day;
    }

    /// Keeps about 400 days; lifetime totals and `FirstDay` stay.
    public void Trim(int keepDays = 400, DateTime? now = null)
    {
        var cutoff = Key((now ?? DateTime.Now).Date.AddDays(-keepDays));
        Days = Days.Where(kv => string.CompareOrdinal(kv.Key, cutoff) >= 0).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// Totals per key since `start` (null: since counting began).
    public Dictionary<string, int> Totals(DateTime? start = null)
    {
        if (start is null) return new(Lifetime);
        var startKey = Key(start.Value);
        var result = new Dictionary<string, int>();
        foreach (var (day, perKey) in Days)
        {
            if (string.CompareOrdinal(day, startKey) < 0) continue;
            foreach (var (k, n) in perKey) result[k] = result.GetValueOrDefault(k) + n;
        }
        return result;
    }

    /// Per-day totals of the given keys, for the graph.
    public Dictionary<DateTime, int> Counts(IReadOnlySet<string> keys)
    {
        var result = new Dictionary<DateTime, int>();
        foreach (var (day, perKey) in Days)
        {
            if (DateOf(day) is not { } date) continue;
            int total = perKey.Where(kv => keys.Contains(kv.Key)).Sum(kv => kv.Value);
            if (total > 0) result[date] = result.GetValueOrDefault(date) + total;
        }
        return result;
    }
}

/// What InnerVoice counts (the same keys as the Mac).
public static class ActivityKey
{
    public const string LinesRules = "linesRules";
    public const string LinesAI = "linesAI";
    public const string LinesByHand = "linesByHand";
    public const string GuessesKept = "guessesKept", GuessesChanged = "guessesChanged";
    public const string Facts = "facts";
    /// No longer counted or shown (pronunciations, recording and retakes left the app); kept so
    /// older counts still load and the keys stay the Mac's.
    public const string Pronunciations = "pronunciations";
    public const string ParagraphsRecorded = "paragraphsRecorded", RetakesMarked = "retakesMarked", RetakesCleared = "retakesCleared";
    public const string LinesCarriedOver = "linesCarriedOver";
    public static string AiKept(string model) => "aiKept|" + model;
    public static string AiChanged(string model) => "aiChanged|" + model;

    /// Lines tagged for the narrator: by the rules or the AI.
    public static readonly IReadOnlySet<string> TaggedForYou = new HashSet<string> { LinesRules, LinesAI };
    /// About how long finding and marking one line's speaker by hand takes (an estimate).
    public const double SecondsPerLineByHand = 6.0;
}

/// The AI's accuracy on one model: of the guesses the narrator reviewed, how many were kept.
public sealed record ModelAccuracy(string Model, int Kept, int Changed)
{
    public int Reviewed => Kept + Changed;
    public double Rate => Reviewed == 0 ? 0 : (double)Kept / Reviewed;

    public static List<ModelAccuracy> All(Dictionary<string, int> totals)
    {
        var byModel = new Dictionary<string, (int Kept, int Changed)>();
        foreach (var (k, n) in totals)
        {
            if (k.StartsWith("aiKept|", StringComparison.Ordinal)) { var m = k[7..]; var v = byModel.GetValueOrDefault(m); byModel[m] = (v.Kept + n, v.Changed); }
            else if (k.StartsWith("aiChanged|", StringComparison.Ordinal)) { var m = k[10..]; var v = byModel.GetValueOrDefault(m); byModel[m] = (v.Kept, v.Changed + n); }
        }
        return byModel.Select(kv => new ModelAccuracy(kv.Key, kv.Value.Kept, kv.Value.Changed)).OrderByDescending(a => a.Reviewed).ToList();
    }
}
