// Ported from Sources/InnerVoiceKit/TextCleanup.swift. A kept line keeps its delivery hint
// and its tone (still in Extra on Windows).
// TODO(voices): run ToneRules after a cleanup, as the Mac does, once tones are ported.

using System.Text.Json.Serialization;
using InnerVoice.Core.AI;

namespace InnerVoice.Core;

/// A problem left in the text by importing (a scan, a PDF, an ebook).
public enum CleanupIssue { Ocr, Hyphenation, PageNumber, Header, Footnote, Spacing, Other }

public static class CleanupIssues
{
    public static string Title(this CleanupIssue issue) => issue switch
    {
        CleanupIssue.Ocr => "Scanning mistake",
        CleanupIssue.Hyphenation => "Broken hyphenation",
        CleanupIssue.PageNumber => "Page number",
        CleanupIssue.Header => "Page header",
        CleanupIssue.Footnote => "Footnote marker",
        CleanupIssue.Spacing => "Letter spacing",
        _ => "Other",
    };

    /// The Mac's raw values ("ocr", "page-number", …).
    public static CleanupIssue Parse(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "ocr" => CleanupIssue.Ocr,
        "hyphenation" => CleanupIssue.Hyphenation,
        "page-number" => CleanupIssue.PageNumber,
        "header" => CleanupIssue.Header,
        "footnote" => CleanupIssue.Footnote,
        "spacing" => CleanupIssue.Spacing,
        _ => CleanupIssue.Other,
    };
}

/// One proposed fix: replace `Original` (exact text in the paragraph) with `Replacement`.
public sealed record CleanupFix(Guid ParagraphID, string Original, string Replacement, CleanupIssue Issue = CleanupIssue.Other, string Note = "")
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

/// Clean Up Text: Claude reads the book a passage at a time and proposes small fixes for
/// import problems. Only the project changes; the imported file never does.
public static class CleanupPrompt
{
    public const string System =
        "You proofread a manuscript that was imported from a scan, a PDF or an ebook, so an " +
        "audiobook narrator can read it aloud. Find only problems the import caused: scanning " +
        "(OCR) mistakes such as \"tbe\" for \"the\" or \"rn\" read as \"m\", words broken by a hyphen at a " +
        "line end (\"con- tinue\"), page numbers and running page headers or footers left in the " +
        "text, footnote markers glued to words (\"night.12\"), and letters spread apart (\"T H E\"). " +
        "Never change the author's wording, spelling, dialect, grammar, punctuation style or " +
        "names, and never rewrite a sentence. If a passage has no import problems, return no fixes. " +
        "Answer with JSON only.";

    public sealed record Chunk(Dictionary<string, Guid> Labels, List<ChatMessage> Messages);

    public sealed class Reply
    {
        [JsonPropertyName("paragraph")] public string Paragraph { get; set; } = "";
        [JsonPropertyName("find")] public string Find { get; set; } = "";
        [JsonPropertyName("replace")] public string Replace { get; set; } = "";
        [JsonPropertyName("issue")] public string? Issue { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    /// The paragraphs Claude reads: body text and block quotes (headings are chapter titles).
    static bool Readable(Paragraph p) =>
        p.Style is ParagraphStyle.Body or ParagraphStyle.BlockQuote && !string.IsNullOrWhiteSpace(p.Text);

    /// The whole book, or `chapters`, in passages of about `maxWords`.
    public static List<Chunk> Chunks(Project project, IReadOnlyList<int>? chapters = null, int maxWords = 2500)
    {
        var picked = (chapters ?? Enumerable.Range(0, project.Chapters.Count).ToList()).Where(c => c >= 0 && c < project.Chapters.Count);
        var output = new List<Chunk>();
        var labels = new Dictionary<string, Guid>();
        var passage = new System.Text.StringBuilder();
        int words = 0;
        void Flush()
        {
            if (labels.Count == 0) return;
            var user = "Passage (each paragraph starts with its label):\n\n" + passage.ToString().Trim() + "\n\n" +
                "For each problem, \"find\" is the exact text in that paragraph, copied character for " +
                "character, with a few words around the problem so it is found only once; \"replace\" " +
                "is the same text fixed. To remove a page number or header that fills a whole " +
                "paragraph, \"find\" is the whole paragraph and \"replace\" is \"\". issue is one of ocr, " +
                "hyphenation, page-number, header, footnote, spacing, other. note says what is wrong " +
                "in a few words.\n" +
                "Reply as {\"fixes\":[{\"paragraph\":\"P1\",\"find\":\"…\",\"replace\":\"…\",\"issue\":\"ocr\",\"note\":\"…\"}]}, or {\"fixes\":[]}.";
            output.Add(new Chunk(labels, [new(ChatRole.System, System), new(ChatRole.User, user)]));
            labels = [];
            passage.Clear();
            words = 0;
        }
        int n = 0;
        foreach (int c in picked)
            foreach (var p in project.Chapters[c].Paragraphs.Where(Readable))
            {
                n++;
                labels[$"P{n}"] = p.Id;
                passage.Append($"[P{n}] {p.Text}\n\n");
                words += p.WordCount;
                if (words >= maxWords) Flush();
            }
        Flush();
        return output;
    }

    sealed class Wrapper { [JsonPropertyName("fixes")] public List<Reply>? Fixes { get; set; } }

    public static List<Reply>? Parse(string text)
    {
        if (JsonText.Extract(text) is { } json && Replies.Decode<Wrapper>(json)?.Fixes is { } whole) return whole;
        var partial = JsonText.Objects(text).Select(Replies.Decode<Reply>).Where(r => r is not null && r.Paragraph.Length > 0).Cast<Reply>().ToList();
        return partial.Count == 0 ? null : partial;
    }

    /// Fixes that can be trusted: the text is really in the cited paragraph, the change is
    /// small (a repair, not a rewrite), and it stays one paragraph.
    public static List<CleanupFix> Fixes(IEnumerable<Reply> replies, Chunk chunk, Project project, BookIndex index)
    {
        var output = new List<CleanupFix>();
        foreach (var r in replies)
        {
            if (!chunk.Labels.TryGetValue(r.Paragraph.Trim(), out var pid) || project.Paragraph(pid, index) is not { } p) continue;
            if (r.Find.Length == 0 || r.Find == r.Replace || !p.Text.Contains(r.Find, StringComparison.Ordinal)
                || r.Replace.Contains('\n') || r.Find.Length > 600) continue;
            if (!IsRepair(r.Find, r.Replace, wholeParagraph: r.Find == p.Text)) continue;
            if (output.Any(f => f.ParagraphID == pid && f.Original == r.Find)) continue;
            output.Add(new CleanupFix(pid, r.Find, r.Replace, CleanupIssues.Parse(r.Issue), r.Note ?? ""));
        }
        return output;
    }

    /// A repair changes a few letters, joins or splits words, or removes stray numbers and
    /// short headers. A rewrite is refused.
    public static bool IsRepair(string find, string replace, bool wholeParagraph)
    {
        if (replace.Length == 0)
            // Removing: a whole short paragraph (a page number or header), or a few characters.
            return wholeParagraph ? Words.Count(find) <= 12 : find.Length <= 40;
        var a = find.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray();
        var b = replace.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray();
        int allowed = Math.Max(3, Math.Min(a.Length, b.Length) / 5);
        return EditDistance(a, b, allowed) <= allowed;
    }

    /// Levenshtein distance, giving up past `limit`.
    static int EditDistance(char[] a, char[] b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        if (a.Length == 0 || b.Length == 0) return Math.Max(a.Length, b.Length);
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        var cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int best = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                best = Math.Min(best, cur[j]);
            }
            if (best > limit) return limit + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

public static class TextCleanup
{
    /// The fix in its sentence, before and after, for the review list.
    public static (string Before, string After) Preview(CleanupFix fix, string paragraph, int around = 40)
    {
        int at = paragraph.IndexOf(fix.Original, StringComparison.Ordinal);
        if (at < 0) return (fix.Original, fix.Replacement);
        int end = at + fix.Original.Length;
        int start = Math.Max(0, at - around);
        int stop = Math.Min(paragraph.Length, end + around);
        var lead = (start > 0 ? "…" : "") + paragraph[start..at];
        var tail = paragraph[end..stop] + (stop < paragraph.Length ? "…" : "");
        return (lead + fix.Original + tail, lead + fix.Replacement + tail);
    }

    /// Applies the fixes to the project's text and returns the ids of those applied. Lines in
    /// edited paragraphs are found again, keeping each line's speaker (and its tone and delivery,
    /// in Extra) where its words match. A paragraph whose fixes would lose a line the narrator
    /// assigned keeps its old text (none of its fixes apply). A paragraph a fix empties (a page
    /// number) is removed, unless it is all its chapter has.
    public static List<Guid> Apply(IEnumerable<CleanupFix> fixes, Project p)
    {
        var byParagraph = fixes.GroupBy(f => f.ParagraphID).ToDictionary(g => g.Key, g => g.ToList());
        if (byParagraph.Count == 0) return [];
        var finder = new DialogueFinder(DialogueFinder.DetectStyle(p.Chapters.SelectMany(c => c.Paragraphs)));
        var linesByParagraph = p.Lines.GroupBy(l => l.ParagraphID).ToDictionary(g => g.Key, g => g.ToList());
        var applied = new List<Guid>();
        var newLines = new Dictionary<Guid, List<Line>>(); // edited paragraph → its lines now
        var removed = new HashSet<Guid>();

        foreach (var chapter in p.Chapters.Where(c => c.Paragraphs.Any(x => byParagraph.ContainsKey(x.Id))))
        {
            bool open = false;
            int removedHere = 0;
            for (int i = 0; i < chapter.Paragraphs.Count; i++)
            {
                var old = chapter.Paragraphs[i];
                if (!byParagraph.TryGetValue(old.Id, out var fs) || old.Style is ParagraphStyle.Heading or ParagraphStyle.SceneBreak)
                {
                    open = finder.Find(old, open).EndsOpen;
                    continue;
                }
                var edited = old.Copy();
                var done = new List<Guid>();
                foreach (var f in fs)
                {
                    int at = edited.Text.IndexOf(f.Original, StringComparison.Ordinal);
                    if (at < 0) continue;
                    edited.Text = edited.Text[..at] + f.Replacement + edited.Text[(at + f.Original.Length)..];
                    edited.Italic = Shift(edited.Italic, new TextSpan(at, f.Original.Length), f.Replacement.Length);
                    done.Add(f.Id);
                }
                if (done.Count == 0)
                {
                    open = finder.Find(old, open).EndsOpen;
                    continue;
                }
                var oldLines = linesByParagraph.GetValueOrDefault(old.Id) ?? [];
                if (string.IsNullOrWhiteSpace(edited.Text))
                {
                    // A page number or header on its own: the paragraph goes, unless it holds the
                    // narrator's line or is all the chapter has.
                    if (oldLines.Any(l => l.Source == AssignmentSource.User) || chapter.Paragraphs.Count - removedHere <= 1)
                    {
                        open = finder.Find(old, open).EndsOpen;
                        continue;
                    }
                    removed.Add(old.Id);
                    removedHere++;
                    applied.AddRange(done);
                    continue;
                }
                var result = finder.Find(edited, open);
                if (Match(result.Lines, edited, oldLines, old) is not { } lines)
                {
                    open = finder.Find(old, open).EndsOpen;
                    continue;
                }
                open = result.EndsOpen;
                chapter.Paragraphs[i] = edited;
                newLines[old.Id] = lines;
                applied.AddRange(done);
            }
        }
        if (applied.Count == 0) return [];
        if (removed.Count > 0)
        {
            foreach (var c in p.Chapters) c.Paragraphs.RemoveAll(x => removed.Contains(x.Id));
            p.Recorded.ExceptWith(removed);
            p.Retakes.RemoveAll(r => removed.Contains(r.ParagraphID));
            foreach (var f in p.Characters.SelectMany(c => c.Facts))
                if (f.SourceParagraphID is { } src && removed.Contains(src)) f.SourceParagraphID = null;
            foreach (var pr in p.Pronunciations)
                if (pr.FirstParagraphID is { } first && removed.Contains(first)) pr.FirstParagraphID = null;
        }
        // Lines: the edited paragraphs' new ones replace their old ones, in book order.
        var order = new Dictionary<Guid, int>();
        int g = 0;
        foreach (var c in p.Chapters) foreach (var para in c.Paragraphs) order[para.Id] = g++;
        var all = p.Lines.Where(l => !newLines.ContainsKey(l.ParagraphID) && !removed.Contains(l.ParagraphID)).ToList();
        all.AddRange(newLines.Values.SelectMany(x => x));
        p.Lines = all.Select((l, n) => (l, n))
            .OrderBy(x => order.GetValueOrDefault(x.l.ParagraphID, int.MaxValue))
            .ThenBy(x => x.l.Range.Location)
            .ThenBy(x => x.n)
            .Select(x => x.l).ToList();
        // New lines (a quote the scan had broken) get the rules' reading; settled ones keep theirs.
        RuleAttribution.Apply(p);
        return applied;
    }

    /// Lines found in an edited paragraph, each keeping the old line with the same words (its
    /// id, speaker, and everything in Extra). null when a line the narrator assigned has no match.
    static List<Line>? Match(List<DialogueFinder.Found> found, Paragraph paragraph, List<Line> old, Paragraph oldText)
    {
        var used = new HashSet<Guid>();
        var output = new List<Line>();
        foreach (var f in found)
        {
            var line = new Line(paragraph.Id, f.Range, f.Kind, continuesPrevious: f.ContinuesPrevious);
            var words = paragraph.Substring(f.Range);
            Line? best = null;
            double bestScore = 0;
            foreach (var ol in old.Where(o => !used.Contains(o.Id)))
            {
                double s = ol.Range == f.Range && oldText.Substring(ol.Range) == words ? 1 : TextMatch.Similarity(oldText.Substring(ol.Range), words);
                if (s >= 0.6 && s > bestScore) { best = ol; bestScore = s; }
            }
            if (best is not null)
            {
                used.Add(best.Id);
                line = best.Copy();
                line.Range = f.Range;
                line.ContinuesPrevious = f.ContinuesPrevious;
            }
            output.Add(line);
        }
        if (old.Any(o => o.Source == AssignmentSource.User && !used.Contains(o.Id))) return null;
        return output;
    }

    /// Italic runs after `range` is replaced by text `length` long.
    public static List<TextSpan> Shift(List<TextSpan> spans, TextSpan range, int length)
    {
        int delta = length - range.Length;
        int end = range.End;
        var output = new List<TextSpan>();
        foreach (var s in spans)
        {
            if (s.End <= range.Location) output.Add(s);
            else if (s.Location >= end) output.Add(new TextSpan(s.Location + delta, s.Length));
            else
            {
                int start = Math.Min(s.Location, range.Location);
                int newEnd = s.End > end ? s.End + delta : range.Location + length;
                if (newEnd > start) output.Add(new TextSpan(start, newEnd - start));
            }
        }
        return output;
    }
}
