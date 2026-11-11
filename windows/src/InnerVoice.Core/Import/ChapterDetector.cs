// Ported from Sources/InnerVoiceKit/Import/ChapterDetector.swift.

using System.Text.RegularExpressions;

namespace InnerVoice.Core.Import;

/// One block of text as a reader found it, before chapters are found.
public sealed record RawBlock(string Text, List<TextSpan> Italic, int? HeadingLevel = null, bool IsBlockQuote = false, bool IsCentered = false)
{
    public RawBlock(string text) : this(text, []) { }
}

public sealed class ImportedBook(string title, string author, List<Chapter> chapters)
{
    public string Title { get; set; } = title;
    public string Author { get; set; } = author;
    public List<Chapter> Chapters { get; set; } = chapters;
    public int ParagraphCount => Chapters.Sum(c => c.Paragraphs.Count);
}

/// Splits a run of blocks into chapters at headings.
public static class ChapterDetector
{
    const string NumberWords = "one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred";
    static readonly string N = $@"(?:\d+|[ivxlcdm]+|(?:{NumberWords})(?:[- ](?:{NumberWords}))*)";
    static readonly Regex HeadingPattern = new(
        $@"^(?:(?:chapter|ch\.|book|part|act|volume)\s+{N}\b.*|(?:prologue|epilogue|interlude|introduction|preface|foreword|afterword|acknowledg(?:e)?ments|author'?s note|author’s note|dedication|contents|table of contents|about the author)\b.*|{N}\.?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// True for "Chapter 12", "CHAPTER TWELVE", "Prologue", "Part II", "7".
    public static bool LooksLikeHeading(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 80) return false;
        if (t.EndsWith('”') || t.EndsWith('"')) return false;
        return HeadingPattern.IsMatch(t);
    }

    const string SceneBreakChars = "*#~•·-—–_=+◆◇❖⁂ ";

    public static bool IsSceneBreak(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 12) return false;
        return t.EnumerateRunes().All(r => SceneBreakChars.Contains(r.ToString(), StringComparison.Ordinal));
    }

    /// Builds chapters. Explicit level-1 headings win; otherwise heading-like lines split.
    /// Text before the first heading becomes "Opening" (or the title page is dropped when tiny).
    public static List<Chapter> Chapters(IEnumerable<RawBlock> blocks, string fallbackTitle)
    {
        var clean = blocks.Select(b => b with { Text = b.Text.Trim() }).Where(b => b.Text.Length > 0).ToList();
        int explicitCount = clean.Count(b => b.HeadingLevel == 1);
        bool IsHeading(RawBlock b)
        {
            if (b.HeadingLevel == 1) return true;
            if (b.Text.Length > 80) return false;
            if (explicitCount >= 3) return LooksLikeHeading(b.Text) && (b.HeadingLevel is not null || b.IsCentered);
            return LooksLikeHeading(b.Text) || (b.HeadingLevel == 2 && explicitCount == 0 && b.Text.Length < 60);
        }

        var chapters = new List<Chapter>();
        var current = new Chapter("Opening", []);
        bool pendingSubtitle = false;
        foreach (var block in clean)
        {
            if (IsHeading(block))
            {
                if (current.Paragraphs.Count == 0 && pendingSubtitle)
                {
                    current.Title += (LooksLikeHeading(block.Text) ? " · " : ": ") + TitleCase(block.Text);
                }
                else
                {
                    if (current.Paragraphs.Count > 0) chapters.Add(current);
                    current = new Chapter(TitleCase(block.Text), []);
                }
                pendingSubtitle = true;
                continue;
            }
            if (pendingSubtitle && current.Paragraphs.Count == 0 && block.HeadingLevel is not null && block.Text.Length < 60 &&
                !block.Text.Contains('“') && !block.Text.Contains('"'))
            {
                current.Title += ": " + block.Text;
                pendingSubtitle = false;
                continue;
            }
            pendingSubtitle = false;
            var style = IsSceneBreak(block.Text) ? ParagraphStyle.SceneBreak : block.IsBlockQuote ? ParagraphStyle.BlockQuote : ParagraphStyle.Body;
            current.Paragraphs.Add(new Paragraph(block.Text, style, [.. block.Italic]));
        }
        if (current.Paragraphs.Count > 0) chapters.Add(current);
        if (chapters.Count > 1 && chapters[0].Title == "Opening" && chapters[0].WordCount < 40 &&
            chapters[0].Paragraphs.All(p => !p.Text.Contains('“') && !p.Text.Contains('"')))
            chapters.RemoveAt(0);
        if (chapters.Count == 0) return [];
        if (chapters.Count == 1 && chapters[0].Title == "Opening") chapters[0].Title = fallbackTitle;
        return chapters;
    }

    static readonly HashSet<string> Roman = ["i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x", "xi", "xii"];

    /// "CHAPTER ONE" reads better as "Chapter One"; mixed case is kept.
    public static string TitleCase(string s)
    {
        var letters = new string(s.Where(char.IsLetter).ToArray());
        if (letters.Length == 0 || letters != letters.ToUpperInvariant()) return s;
        return string.Join(" ", s.ToLowerInvariant().Split(' ').Select(w =>
            Roman.Contains(w) ? w.ToUpperInvariant() : w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
