// Ported from the PDF part of Sources/InnerVoiceKit/Import/EpubPdfReaders.swift. PdfPig reads
// the words and their positions (PDF page space, origin bottom left, like PDFKit); the
// paragraph logic from `dropRunningLines` on is the Mac's, line for line.

using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace InnerVoice.Core.Import;

public static class PdfReader
{
    public readonly record struct Rect(double X, double Y, double Width, double Height)
    {
        public double MinX => X;
        public double MaxX => X + Width;
        public double MinY => Y;
        public double MaxY => Y + Height;
        public double MidX => X + Width / 2;
    }

    public sealed record PdfLine(string Text, Rect Rect, int Page, double FontSize, List<TextSpan> Italic, bool Bold);

    public static List<RawBlock> Read(string path)
    {
        var name = Path.GetFileName(path);
        PdfDocument document;
        try { document = PdfDocument.Open(path); }
        catch (Exception e) when (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || e.GetType().Name.Contains("Encrypt"))
        {
            throw new ImportException($"Unable to read “{name}”. The PDF is locked with a password.");
        }
        catch (Exception)
        {
            throw new ImportException($"Unable to read “{name}”. The PDF is damaged or locked.");
        }
        using (document)
        {
            var lines = new List<PdfLine>();
            var heights = new List<double>();
            foreach (var page in document.GetPages())
            {
                heights.Add(page.Height);
                lines.AddRange(LinesOf(page, page.Number - 1));
            }
            int totalChars = lines.Sum(l => l.Text.Length);
            if (totalChars < 200)
            {
                if (document.NumberOfPages > 0)
                    throw new ImportException($"“{name}” has no text, only pictures of pages. Export it with text, or run it through a text recognition app first.");
                throw new ImportException($"“{name}” has no text in it.");
            }
            lines = DropRunningLines(lines, document.NumberOfPages, heights);
            return Paragraphs(lines);
        }
    }

    /// The page's words grouped into lines by baseline, top to bottom (PDFKit's
    /// selectionsByLine).
    static IEnumerable<PdfLine> LinesOf(Page page, int index)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var rows = new List<List<Word>>();
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            double size = w.Letters.Count > 0 ? w.Letters.Max(l => l.PointSize) : w.BoundingBox.Height;
            var row = rows.FirstOrDefault(r => Math.Abs(Baseline(r[0]) - Baseline(w)) < Math.Max(2, size * 0.35));
            if (row is null) rows.Add([w]); else row.Add(w);
        }
        foreach (var row in rows)
        {
            row.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            var text = new System.Text.StringBuilder();
            var italic = new List<TextSpan>();
            bool bold = true;
            double size = 0;
            Word? prev = null;
            foreach (var w in row)
            {
                if (prev is not null)
                {
                    // A gap wider than a fraction of the font size is a space.
                    double gap = w.BoundingBox.Left - prev.BoundingBox.Right;
                    double em = prev.Letters.Count > 0 ? prev.Letters[^1].PointSize : 10;
                    if (gap > em * 0.12) text.Append(' ');
                }
                int start = text.Length;
                var t = Ligatures(w.Text.Replace("\u00AD", ""));
                text.Append(t);
                var fontName = w.Letters.Count > 0 ? (w.Letters[0].FontName ?? "") : "";
                var lower = fontName.ToLowerInvariant();
                if (!(lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy"))) bold = false;
                if (lower.Contains("italic") || lower.Contains("oblique")) italic.Add(new TextSpan(start, t.Length));
                size = Math.Max(size, w.Letters.Count > 0 ? w.Letters.Max(l => l.PointSize) : 0);
                prev = w;
            }
            var s = text.ToString().Trim();
            if (s.Length == 0) continue;
            double left = row.Min(w => w.BoundingBox.Left), right = row.Max(w => w.BoundingBox.Right);
            double bottom = row.Min(w => w.BoundingBox.Bottom), top = row.Max(w => w.BoundingBox.Top);
            yield return new PdfLine(s, new Rect(left, bottom, right - left, top - bottom), index, size,
                ManuscriptImporter.MergeRanges(italic, s.Length), bold);
        }
    }

    /// PDFKit spells out typographic ligatures ("ﬁ" → "fi"); so does this.
    static string Ligatures(string s) => s.Any(c => c is >= '\uFB00' and <= '\uFB06') ? s.Normalize(System.Text.NormalizationForm.FormKC) : s;

    static double Baseline(Word w) => w.Letters.Count > 0 ? w.Letters[0].StartBaseLine.Y : w.BoundingBox.Bottom;

    static readonly Regex PageNumber = new(@"^(page\s*)?[0-9ivxlcdm]+(\s*(of|/)\s*[0-9]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// Removes page numbers and lines that repeat near the top or bottom of many pages.
    public static List<PdfLine> DropRunningLines(List<PdfLine> lines, int pageCount, List<double> pageHeights)
    {
        static string Key(string s) => new(s.ToLowerInvariant().Where(c => !char.IsDigit(c) && !char.IsWhiteSpace(c)).ToArray());
        bool NearEdge(PdfLine l)
        {
            double h = l.Page < pageHeights.Count ? pageHeights[l.Page] : 792;
            return l.Rect.MinY < h * 0.09 || l.Rect.MaxY > h * 0.91;
        }
        var counts = new Dictionary<string, int>();
        foreach (var l in lines.Where(NearEdge)) counts[Key(l.Text)] = counts.GetValueOrDefault(Key(l.Text)) + 1;
        int threshold = Math.Max(3, pageCount / 4);
        return lines.Where(l =>
        {
            if (!NearEdge(l)) return true;
            var t = l.Text.Trim();
            if (PageNumber.IsMatch(t)) return false;
            var k = Key(t);
            return k.Length != 0 && counts.GetValueOrDefault(k) < threshold;
        }).ToList();
    }

    static double Median(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }

    /// The left edge of each page's text block. A printed book mirrors its margins, so one
    /// margin for the whole book reads every indent on the other side as a plain line. A low
    /// percentile of the page's line starts finds the edge; a page with few lines uses its
    /// side of the spread.
    public static Dictionary<int, double> PageLefts(List<PdfLine> lines)
    {
        static double Edge(List<double> xs) { xs.Sort(); return xs[xs.Count / 10]; }
        var byPage = new Dictionary<int, List<double>>();
        var bySide = new Dictionary<int, List<double>>();
        foreach (var l in lines)
        {
            if (!byPage.TryGetValue(l.Page, out var bp)) byPage[l.Page] = bp = [];
            bp.Add(l.Rect.MinX);
            if (!bySide.TryGetValue(l.Page % 2, out var bs)) bySide[l.Page % 2] = bs = [];
            bs.Add(l.Rect.MinX);
        }
        var side = bySide.ToDictionary(kv => kv.Key, kv => Edge([.. kv.Value]));
        return byPage.ToDictionary(kv => kv.Key, kv => kv.Value.Count >= 4 ? Edge([.. kv.Value]) : side.TryGetValue(kv.Key % 2, out var e) ? e : Edge([.. kv.Value]));
    }

    public static List<RawBlock> Paragraphs(List<PdfLine> pdfLines)
    {
        if (pdfLines.Count == 0) return [];
        var lefts = PageLefts(pdfLines);
        var lines = pdfLines.Select(l => l with { Rect = l.Rect with { X = l.Rect.X - lefts.GetValueOrDefault(l.Page) } }).ToList();
        double bodySize = Median(lines.Select(l => l.FontSize).Where(s => s > 0));
        double left = Median(lines.Select(l => l.Rect.MinX));
        double right = Median(lines.Select(l => l.Rect.MaxX));
        double width = Math.Max(1, right - left);
        var gaps = new List<double>();
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].Page != lines[i - 1].Page) continue;
            double g = lines[i - 1].Rect.MinY - lines[i].Rect.MinY;
            if (g > 0) gaps.Add(g);
        }
        double lineGap = Math.Max(1, Median(gaps));

        var blocks = new List<RawBlock>();
        var text = new System.Text.StringBuilder();
        var italic = new List<TextSpan>();
        int? level = null;
        bool centered = false;
        void Flush()
        {
            var t = text.ToString().Trim(' ', '\t');
            if (t.Length > 0) blocks.Add(new RawBlock(t, ManuscriptImporter.MergeRanges(italic, t.Length), level, false, centered));
            text.Clear();
            italic = [];
            level = null;
            centered = false;
        }
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            bool isHeadingSize = bodySize > 0 && line.FontSize >= bodySize * 1.25;
            bool isCentered = Math.Abs(line.Rect.MidX - (left + right) / 2) < width * 0.06 && line.Rect.Width < width * 0.7;
            bool startsNew = i == 0;
            if (i > 0)
            {
                var prev = lines[i - 1];
                bool samePage = prev.Page == line.Page;
                double gap = samePage ? prev.Rect.MinY - line.Rect.MinY : lineGap;
                bool indented = line.Rect.MinX > left + Math.Max(8, width * 0.025);
                bool prevShort = prev.Rect.MaxX < right - width * 0.12;
                bool prevHeading = bodySize > 0 && prev.FontSize >= bodySize * 1.25;
                startsNew = gap > lineGap * 1.45 || indented || (prevShort && samePage) || prevHeading != isHeadingSize || (isCentered && !samePage);
                // A page that ends mid-sentence carries on.
                if (!samePage && !indented && !prevShort && !isHeadingSize) startsNew = false;
                if (!samePage && prevShort && prev.Text.Length > 0 && !".!?”\"’:".Contains(prev.Text[^1])) startsNew = false;
            }
            if (startsNew)
            {
                Flush();
                if (isHeadingSize) level = line.FontSize >= bodySize * 1.6 ? 1 : 2;
                else if (line.Bold && line.Text.Length < 60) level = 2;
                centered = isCentered;
            }
            if (text.Length > 0)
            {
                if (text[^1] == '-' && line.Text.Length > 0 && char.IsLower(line.Text[0])) text.Length--; // hyphenated across lines
                else if (text[^1] != '—') text.Append(' ');
            }
            int offset = text.Length;
            text.Append(line.Text);
            italic.AddRange(line.Italic.Select(r => new TextSpan(r.Location + offset, r.Length)));
        }
        Flush();
        return blocks;
    }
}
