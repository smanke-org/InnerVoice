// Ported from Sources/InnerVoiceKit/ProjectEditing.swift.

using System.Globalization;

namespace InnerVoice.Core;

public static class ProjectEditing
{
    // MARK: Speaker lists

    /// Every speaker list in the same order: pinned characters first, then everyone else,
    /// each part A to Z by first name (then by full name).
    public static (List<BookCharacter> Pinned, List<BookCharacter> Others) SpeakerOrder(this Project p)
    {
        var sorted = p.Characters.ToList();
        sorted.Sort(ByFirstName);
        return (sorted.Where(c => c.IsPinned).ToList(), sorted.Where(c => !c.IsPinned).ToList());
    }

    static readonly CompareInfo Compare = CultureInfo.CurrentCulture.CompareInfo;
    const CompareOptions Standard = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.NumericOrdering;

    /// Finder-like order (Swift's localizedStandardCompare).
    public static int StandardCompare(string a, string b) => Compare.Compare(a, b, Standard);

    public static int ByFirstName(BookCharacter a, BookCharacter b)
    {
        int first = StandardCompare(a.FirstName, b.FirstName);
        return first != 0 ? first : StandardCompare(a.Name, b.Name);
    }

    /// The title to show in lists: a chapter whose heading was deleted is the book's opening.
    public static string ShownTitle(this Chapter c) => c.Title.Length == 0 ? "Opening" : c.Title;

    // MARK: Deleting text

    /// Deletes text the narrator doesn't read: `cuts` are ranges inside paragraphs,
    /// `doomed` whole chapters, `headings` ranges inside chapter titles. Lines inside a cut
    /// go, overlapping ones are trimmed, later lines move up. An emptied paragraph goes too,
    /// with its recorded mark and retakes; facts and pronunciations that pointed at it lose
    /// the link. Only the project's copy changes.
    public static void DeleteText(this Project p, Dictionary<Guid, List<TextSpan>> cuts, HashSet<Guid>? doomed = null,
                                  Dictionary<Guid, TextSpan>? headings = null)
    {
        doomed ??= [];
        headings ??= [];
        var removed = new HashSet<Guid>();
        var lineChanges = new Dictionary<Guid, List<TextSpan>>();
        foreach (var chapter in p.Chapters)
        {
            if (doomed.Contains(chapter.Id))
            {
                foreach (var para in chapter.Paragraphs) removed.Add(para.Id);
                continue;
            }
            foreach (var paragraph in chapter.Paragraphs)
            {
                if (!cuts.TryGetValue(paragraph.Id, out var spans) || spans.Count == 0) continue;
                var applied = new List<TextSpan>();
                foreach (var span in spans.OrderByDescending(s => s.Location))
                {
                    var r = Intersect(span, paragraph.Text.Length);
                    if (r.Length <= 0) continue;
                    r = AbsorbSpace(r, paragraph.Text);
                    paragraph.Text = paragraph.Text.Remove(r.Location, r.Length);
                    paragraph.Italic = paragraph.Italic.Select(i => SpanAfter(i, r)).Where(i => i is not null).Select(i => i!.Value).ToList();
                    applied.Add(r);
                }
                if (paragraph.Text.Trim().Length == 0) removed.Add(paragraph.Id);
                else lineChanges[paragraph.Id] = applied;
            }
            chapter.Paragraphs.RemoveAll(x => removed.Contains(x.Id));
        }
        p.Chapters.RemoveAll(c => doomed.Contains(c.Id));
        CutHeadings(p, headings);

        var kept = new List<Line>();
        foreach (var line in p.Lines)
        {
            if (removed.Contains(line.ParagraphID)) continue;
            if (lineChanges.TryGetValue(line.ParagraphID, out var applied))
            {
                bool gone = false;
                foreach (var cut in applied)
                {
                    if (SpanAfter(line.Range, cut) is { } moved) line.Range = moved;
                    else { gone = true; break; }
                }
                if (gone) continue;
            }
            kept.Add(line);
        }
        p.Lines = kept;

        if (removed.Count == 0) return;
        p.Recorded.ExceptWith(removed);
        p.Retakes.RemoveAll(r => removed.Contains(r.ParagraphID));
        foreach (var f in p.Characters.SelectMany(c => c.Facts))
            if (f.SourceParagraphID is { } s && removed.Contains(s)) f.SourceParagraphID = null;
        foreach (var pr in p.Pronunciations)
            if (pr.FirstParagraphID is { } s && removed.Contains(s)) pr.FirstParagraphID = null;
        foreach (var rev in p.Revisions)
        {
            rev.Added.RemoveAll(removed.Contains);
            rev.Changed.RemoveAll(removed.Contains);
        }
    }

    /// Takes text out of chapter headings; an emptied heading's chapter joins the one before.
    static void CutHeadings(Project p, Dictionary<Guid, TextSpan> headings)
    {
        for (int c = p.Chapters.Count - 1; c >= 0; c--)
        {
            if (!headings.TryGetValue(p.Chapters[c].Id, out var span)) continue;
            var title = p.Chapters[c].Title;
            var r = Intersect(span, title.Length);
            if (r.Length <= 0) continue;
            r = AbsorbSpace(r, title);
            var left = title.Remove(r.Location, r.Length).Trim();
            if (left.Length > 0) p.Chapters[c].Title = left;
            else if (c > 0)
            {
                p.Chapters[c - 1].Paragraphs.AddRange(p.Chapters[c].Paragraphs);
                p.Chapters.RemoveAt(c);
            }
            else if (p.Chapters[c].Paragraphs.Count == 0) p.Chapters.RemoveAt(c);
            else p.Chapters[c].Title = "";
        }
    }

    static TextSpan Intersect(TextSpan s, int length)
    {
        int lo = Math.Max(0, s.Location), hi = Math.Min(length, s.End);
        return new TextSpan(lo, Math.Max(0, hi - lo));
    }

    /// Where a span ends up once `cut` is taken out of its paragraph: moved up when it comes
    /// after, trimmed when it overlaps, null when nothing of it is left.
    public static TextSpan? SpanAfter(TextSpan s, TextSpan cut)
    {
        if (s.End <= cut.Location) return s;
        if (s.Location >= cut.End) return new TextSpan(s.Location - cut.Length, s.Length);
        int before = Math.Max(0, cut.Location - s.Location);
        int afterPart = Math.Max(0, s.End - cut.End);
        int length = before + afterPart;
        if (length <= 0) return null;
        return new TextSpan(Math.Min(s.Location, cut.Location), length);
    }

    /// One space between the words either side of a cut, not two; none at either end.
    public static TextSpan AbsorbSpace(TextSpan r, string s)
    {
        bool IsSpace(int i) => i >= 0 && i < s.Length && s[i] is ' ' or '\t' or ' ' || (i >= 0 && i < s.Length && char.GetUnicodeCategory(s[i]) == UnicodeCategory.SpaceSeparator);
        int loc = r.Location, len = r.Length;
        if (loc == 0) { while (IsSpace(loc + len)) len++; }
        else if (loc + len == s.Length) { while (IsSpace(loc - 1)) { loc--; len++; } }
        else if (IsSpace(loc - 1) && IsSpace(loc + len)) len++;
        return new TextSpan(loc, len);
    }
}
