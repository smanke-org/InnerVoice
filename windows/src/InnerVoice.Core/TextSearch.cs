// Ported from Sources/InnerVoiceKit/TextSearch.swift. Find in the Manuscript: every place a
// phrase appears, in reading order. Case and diacritics are ignored, and straight and curly
// quotes match each other.

using System.Globalization;

namespace InnerVoice.Core;

public static class TextSearch
{
    /// `Range` is in the paragraph's text (UTF-16).
    public readonly record struct Match(int Chapter, Guid ParagraphID, TextSpan Range);

    public static string Normalized(string s) =>
        s.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"');

    public static List<Match> Matches(string query, Project project, int limit = 5000)
    {
        var q = Normalized(query.Trim());
        var output = new List<Match>();
        if (q.Length == 0) return output;
        var ci = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        for (int c = 0; c < project.Chapters.Count; c++)
        {
            foreach (var p in project.Chapters[c].Paragraphs)
            {
                // Same length after normalizing (each quote is one UTF-16 unit both ways).
                var text = Normalized(p.Text);
                int from = 0;
                while (from < text.Length)
                {
                    int i = ci.IndexOf(text.AsSpan(from), q.AsSpan(), opts, out int length);
                    if (i < 0 || length <= 0) break;
                    output.Add(new Match(c, p.Id, new TextSpan(from + i, length)));
                    if (output.Count >= limit) return output;
                    from += i + length;
                }
            }
        }
        return output;
    }
}
