// Ported from JSONText and TextMatch in Sources/InnerVoiceKit/AI/Prompts.swift.

using System.Text;

namespace InnerVoice.Core;

public static class JsonText
{
    /// Every complete innermost `{…}` object in the text, in order: the answers a reply
    /// finished before it was cut off.
    public static List<string> Objects(string text)
    {
        var output = new List<string>();
        int? start = null;
        bool inString = false, escaped = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') start = i;
            else if (c == '}' && start is { } s)
            {
                output.Add(text[s..(i + 1)]);
                start = null;
            }
        }
        return output;
    }

    /// The first balanced `{…}` object, after any reasoning block.
    public static string? Extract(string text)
    {
        var t = text;
        int think = t.IndexOf("</think>", StringComparison.Ordinal);
        if (think >= 0) t = t[(think + "</think>".Length)..];
        int start = t.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inString = false, escaped = false;
        for (int i = start; i < t.Length; i++)
        {
            char c = t[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return t[start..(i + 1)];
            }
        }
        return null;
    }
}

/// Loose text comparison for quotes and duplicates.
public static class TextMatch
{
    static readonly (string, string)[] Swaps = [("’", "'"), ("‘", "'"), ("“", "\""), ("”", "\""), ("—", " "), ("–", " "), ("…", "...")];

    public static string Normalize(string s)
    {
        var o = s.ToLowerInvariant();
        foreach (var (a, b) in Swaps) o = o.Replace(a, b);
        return string.Join(" ", o.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static List<string> WordsIn(string s)
    {
        var output = new List<string>();
        var sb = new StringBuilder();
        foreach (var r in Normalize(s).EnumerateRunes())
        {
            if (Words.IsAlphanumeric(r)) sb.Append(r.ToString());
            else if (sb.Length > 0) { output.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) output.Add(sb.ToString());
        return output;
    }

    /// True when the paragraph contains the quote, or nearly all of its words.
    public static bool Supports(string paragraph, string quote)
    {
        string p = Normalize(paragraph), q = Normalize(quote);
        if (p.Contains(q, StringComparison.Ordinal)) return true;
        var qw = WordsIn(quote);
        if (qw.Count < 2) return false;
        var pw = WordsIn(paragraph).ToHashSet();
        return (double)qw.Count(pw.Contains) / qw.Count >= 0.8;
    }

    /// Dice similarity of the two texts' word sets, 0…1.
    public static double Similarity(string a, string b)
    {
        var wa = WordsIn(a).ToHashSet();
        var wb = WordsIn(b).ToHashSet();
        if (wa.Count == 0 && wb.Count == 0) return 1;
        return 2.0 * wa.Intersect(wb).Count() / (wa.Count + wb.Count);
    }
}
