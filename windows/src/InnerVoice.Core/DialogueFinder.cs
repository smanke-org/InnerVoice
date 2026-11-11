// Ported from Sources/InnerVoiceKit/DialogueFinder.swift. Works on UTF-16 code units, like
// the Swift version's NSString, so the ranges match.

namespace InnerVoice.Core;

/// Finds the lines in a book with plain code, before any AI: quoted speech in every common
/// style, dash dialogue, italic thoughts, and block-quoted written text.
public sealed class DialogueFinder(DialogueFinder.QuoteStyle style)
{
    public enum QuoteStyle
    {
        /// “ ” or " " around speech, ‘ ’ inside.
        Double,
        /// ‘ ’ around speech (common in UK books), “ ” inside.
        Single,
        /// « » around speech.
        Guillemets,
    }

    public readonly record struct Found(TextSpan Range, LineKind Kind, bool ContinuesPrevious);

    public QuoteStyle Style { get; } = style;

    static bool IsWhitespace(char c) => char.IsWhiteSpace(c);

    /// Picks the quote style from how quotes open across the book.
    public static QuoteStyle DetectStyle(IEnumerable<Paragraph> paragraphs)
    {
        int dbl = 0, sgl = 0, guil = 0;
        foreach (var p in paragraphs.Take(4000))
        {
            var s = p.Text;
            for (int i = 0; i < s.Length; i++)
            {
                bool atStart = i == 0 || IsWhitespace(s[i - 1]) || "—–-([".Contains(s[i - 1]);
                switch (s[i])
                {
                    case '“' or '„': dbl++; break;
                    case '"': if (atStart) dbl++; break;
                    case '‘': if (atStart) sgl++; break;
                    case '\'': if (atStart && i + 1 < s.Length && char.IsUpper(s[i + 1])) sgl++; break;
                    case '«': guil++; break;
                }
            }
        }
        if (guil > dbl && guil > sgl) return QuoteStyle.Guillemets;
        if (sgl > dbl * 2 && sgl > 10) return QuoteStyle.Single;
        return QuoteStyle.Double;
    }

    /// Finds the lines in one paragraph. `openFromPrevious` is true when the previous
    /// paragraph's quote never closed. Returns the lines and whether this paragraph also
    /// ends inside an open quote.
    public (List<Found> Lines, bool EndsOpen) Find(Paragraph paragraph, bool openFromPrevious)
    {
        if (paragraph.Style is ParagraphStyle.Heading or ParagraphStyle.SceneBreak) return ([], false);
        var text = paragraph.Text;
        int length = text.Length;
        if (length == 0) return ([], false);

        // Dash dialogue: — Speech, — he said. Speech continues after a second dash.
        char first = text[0];
        if (first is '—' or '―' || (first == '–' && length > 1 && text[1] == ' '))
        {
            int end = length;
            int search = text.IndexOf(" —", 1, StringComparison.Ordinal);
            if (search >= 0) end = search;
            int start = 1 + (text.Substring(1, Math.Min(2, length - 1)).StartsWith(' ') ? 1 : 0);
            if (end > start) return ([new Found(new TextSpan(start, end - start), LineKind.Spoken, false)], false);
        }

        var found = new List<Found>();
        int? openAt = null;
        bool continues = openFromPrevious && IsOpen(text[0], 0, text);
        for (int i = 0; i < length; i++)
        {
            char c = text[i];
            if (openAt is { } start)
            {
                if (IsClose(c, i, text))
                {
                    if (i > start + 1 || i > start)
                        found.Add(new Found(new TextSpan(start + 1, i - start - 1), LineKind.Spoken, continues && found.Count == 0));
                    openAt = null;
                }
            }
            else if (IsOpen(c, i, text))
            {
                openAt = i;
            }
        }
        bool endsOpen = false;
        if (openAt is { } st && st + 1 < length)
        {
            found.Add(new Found(new TextSpan(st + 1, length - st - 1), LineKind.Spoken, continues && found.Count == 0));
            endsOpen = true;
        }
        found = found.Select(f => Trimmed(f, text)).Where(f =>
        {
            var s = Sub(text, f.Range);
            return Words.Count(s) >= 2 || s.EndsWith('!') || s.EndsWith('?') || s.EndsWith('.') || s.EndsWith(',') || s.EndsWith('…');
        }).ToList();

        if (found.Count == 0 && paragraph.Style == ParagraphStyle.BlockQuote)
            return ([new Found(new TextSpan(0, length), LineKind.Written, false)], false);
        // Italic runs outside quotes, three words or more, read as thoughts.
        foreach (var run in paragraph.Italic)
        {
            bool overlaps = found.Any(f => Intersects(f.Range, run));
            if (overlaps || run.End > length) continue;
            if (Words.Count(Sub(text, run)) < 3) continue;
            found.Add(Trimmed(new Found(run, LineKind.Thought, false), text));
        }
        found.Sort((a, b) => a.Range.Location.CompareTo(b.Range.Location));
        return (found, endsOpen);
    }

    static string Sub(string text, TextSpan r) => text.Substring(r.Location, r.Length);

    static bool Intersects(TextSpan a, TextSpan b)
    {
        int lo = Math.Max(a.Location, b.Location), hi = Math.Min(a.End, b.End);
        return hi - lo > 0;
    }

    static Found Trimmed(Found f, string text)
    {
        int loc = f.Range.Location, len = f.Range.Length;
        while (len > 0 && IsWhitespace(text[loc])) { loc++; len--; }
        while (len > 0 && IsWhitespace(text[loc + len - 1])) len--;
        return f with { Range = new TextSpan(loc, len) };
    }

    static bool IsLetter(char c) => char.IsLetter(c) || char.IsDigit(c) || char.GetUnicodeCategory(c) is
        System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark;

    static bool IsSpaceOrStart(int i, string s) => i <= 0 || IsWhitespace(s[i - 1]) || "—–-([{/".Contains(s[i - 1]);

    bool IsOpen(char c, int i, string s) => Style switch
    {
        QuoteStyle.Double => c is '“' or '„' || (c == '"' && IsSpaceOrStart(i, s)),
        QuoteStyle.Single => (c == '‘' && IsSpaceOrStart(i, s)) ||
                             (c == '\'' && IsSpaceOrStart(i, s) && i + 1 < s.Length && IsLetter(s[i + 1])),
        _ => c == '«',
    };

    bool IsClose(char c, int i, string s)
    {
        switch (Style)
        {
            case QuoteStyle.Double:
                if (c == '”') return true;
                if (c == '"') return !IsSpaceOrStart(i, s) || i == s.Length - 1;
                if (c == '“' && i > 0 && !IsSpaceOrStart(i, s)) return true;
                return false;
            case QuoteStyle.Single:
                if (c is not ('’' or '\'')) return false;
                bool before = i > 0 && IsLetter(s[i - 1]);
                bool after = i + 1 < s.Length && IsLetter(s[i + 1]);
                if (before && after) return false;
                return !IsSpaceOrStart(i, s);
            default:
                return c == '»';
        }
    }

    /// Finds every line in a book, carrying open quotes across paragraphs.
    public static List<Line> FindAll(List<Chapter> chapters)
    {
        var all = chapters.SelectMany(c => c.Paragraphs).ToList();
        var finder = new DialogueFinder(DetectStyle(all));
        var lines = new List<Line>();
        foreach (var chapter in chapters)
        {
            bool open = false;
            foreach (var p in chapter.Paragraphs)
            {
                var (found, endsOpen) = finder.Find(p, open);
                open = endsOpen;
                lines.AddRange(found.Select(f => new Line(p.Id, f.Range, f.Kind, continuesPrevious: f.ContinuesPrevious)));
            }
        }
        // Long runs of italic paragraphs are a stylistic section (a dream, a letter), not thoughts.
        int? runStart = null;
        var drop = new HashSet<Guid>();
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            bool wholeItalic = p.Italic.Count == 1 && p.Italic[0].Length >= p.Text.Length - 2 && p.Text.Length > 20;
            if (wholeItalic) runStart ??= i;
            else
            {
                if (runStart is { } s && i - s > 3) for (int k = s; k < i; k++) drop.Add(all[k].Id);
                runStart = null;
            }
        }
        if (runStart is { } rs && all.Count - rs > 3) for (int k = rs; k < all.Count; k++) drop.Add(all[k].Id);
        return lines.Where(l => !(l.Kind == LineKind.Thought && drop.Contains(l.ParagraphID))).ToList();
    }
}
