// Ported from Sources/InnerVoiceKit/ReadAlongEpub.swift (the packaging only: no audio here).

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace InnerVoice.Core;

/// One stretch the voices read, for a read-along page: which paragraph, and where its words are
/// in the paragraph's text (UTF-16). Source is null for a scene break. Stands in for the Mac's
/// SpeechSegment (id, paragraphID, source); the voices work's SpeechScript segments map onto it.
public sealed record ReadAlongSegment(string Id, Guid ParagraphID, TextSpan? Source);

/// EPUB with Audio: an EPUB 3 book whose text plays in the book's voices and highlights as
/// it's read (Media Overlays). Each chapter is an XHTML page, an audio file and a SMIL file
/// that ties every sentence or line (a fragment of the page) to its clip of the audio.
///
///   mimetype                      (first, stored)
///   META-INF/container.xml
///   OEBPS/content.opf             (media:duration per chapter and in all)
///   OEBPS/nav.xhtml, style.css
///   OEBPS/text/c001.xhtml         (media-overlay="mo-c001")
///   OEBPS/smil/c001.smil
///   OEBPS/audio/c001.m4a
// TODO(voices): Export EPUB with Audio renders each chapter with the voice engine (the Mac's AudioExporter.write timing callback) and passes the file and segment timings here.
public static class ReadAlongEpub
{
    /// One stretch of text and when the audio reads it, in seconds from the start of its chapter's audio.
    public sealed record Clip(string Fragment, double Begin, double End);

    /// A chapter's page and which fragment each segment reads (segment id → span id).
    public sealed record Page(string Xhtml, Dictionary<string, string> Fragments);

    /// A chapter: its page, its audio (M4A; null when nothing in it is read), the seconds of
    /// audio in the file, and the clips.
    public sealed record ChapterFile(string Title, string Xhtml, string? Audio, double Duration, List<Clip> Clips);

    public sealed record Book(string Title, string Author, string Language, string Identifier, DateTime Modified, List<ChapterFile> Chapters);

    /// A file inside the book: written from memory (Text) or copied from disk (FilePath).
    public sealed record Content(string? Text = null, string? FilePath = null);

    // MARK: Segments

    /// Read-along segments for a chapter without the voices: each line and each sentence of
    /// narration, in reading order. Line ranges are kept whole, narration is split into
    /// sentences with a simple splitter (the Mac uses NLTokenizer through SpeechScript).
    // TODO(voices): replace with the ported SpeechScript.segments (with each segment's source), which also knows pronunciations, pauses and who reads what.
    public static List<ReadAlongSegment> Segments(Chapter chapter, Project project)
    {
        var output = new List<ReadAlongSegment>();
        var byParagraph = project.Lines.GroupBy(l => l.ParagraphID).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Range.Location).ToList());
        int n = 0;
        foreach (var paragraph in chapter.Paragraphs)
        {
            if (paragraph.Style == ParagraphStyle.SceneBreak)
            {
                output.Add(new ReadAlongSegment($"s{++n}", paragraph.Id, null));
                continue;
            }
            int cursor = 0;
            foreach (var line in byParagraph.GetValueOrDefault(paragraph.Id) ?? [])
            {
                if (line.Range.Location < cursor) continue;
                foreach (var s in SentenceSpans(paragraph.Text, new TextSpan(cursor, line.Range.Location - cursor), narration: true))
                    output.Add(new ReadAlongSegment($"s{++n}", paragraph.Id, s));
                foreach (var s in SentenceSpans(paragraph.Text, line.Range, narration: false))
                    output.Add(new ReadAlongSegment($"s{++n}", paragraph.Id, s));
                cursor = line.Range.End;
            }
            foreach (var s in SentenceSpans(paragraph.Text, new TextSpan(cursor, paragraph.Text.Length - cursor), narration: true))
                output.Add(new ReadAlongSegment($"s{++n}", paragraph.Id, s));
        }
        return output;
    }

    static readonly Regex Sentence = new(@"[^.!?…]+(?:[.!?…]+[""”’)\]]*|$)", RegexOptions.CultureInvariant);

    /// The sentences of `range` in `text`, trimmed of spaces (and, for narration, of the quote
    /// marks left next to a line).
    static IEnumerable<TextSpan> SentenceSpans(string text, TextSpan range, bool narration)
    {
        if (range.Length <= 0) yield break;
        var piece = text.Substring(range.Location, range.Length);
        foreach (Match m in Sentence.Matches(piece))
        {
            int start = m.Index, end = m.Index + m.Length;
            bool Skip(char c) => char.IsWhiteSpace(c) || (narration && c is '"' or '“' or '”' or '‘' or '’');
            while (start < end && Skip(piece[start])) start++;
            while (end > start && Skip(piece[end - 1])) end--;
            if (end > start && piece[start..end].Any(char.IsLetterOrDigit)) yield return new TextSpan(range.Location + start, end - start);
        }
    }

    // MARK: Pages

    /// The chapter as XHTML: its title, then each paragraph, with every stretch the voices
    /// read (one or more segments with the same source) in a span of its own.
    public static Page MakePage(Chapter chapter, int number, IEnumerable<ReadAlongSegment> segments, string language)
    {
        var byParagraph = segments.GroupBy(s => s.ParagraphID).ToDictionary(g => g.Key, g => g.ToList());
        var fragments = new Dictionary<string, string>();
        int count = 0;
        var body = new StringBuilder($"<h1>{Escape(chapter.Title)}</h1>\n");
        foreach (var paragraph in chapter.Paragraphs)
        {
            var text = paragraph.Text;
            // The spans, in order, never overlapping.
            var spans = new List<(TextSpan Range, string Id)>();
            foreach (var seg in byParagraph.GetValueOrDefault(paragraph.Id) ?? [])
            {
                if (seg.Source is not { } raw) continue;
                int start = Math.Clamp(raw.Location, 0, text.Length), end = Math.Clamp(raw.End, start, text.Length);
                var r = new TextSpan(start, end - start);
                if (r.Length <= 0) continue;
                if (spans.Count > 0 && (spans[^1].Range == r || r.Location < spans[^1].Range.End))
                {
                    fragments[seg.Id] = spans[^1].Id;
                    continue;
                }
                count++;
                var id = $"c{number}-{count}";
                spans.Add((r, id));
                fragments[seg.Id] = id;
            }
            var html = new StringBuilder();
            int cursor = 0;
            foreach (var span in spans)
            {
                html.Append(Inline(text, new TextSpan(cursor, span.Range.Location - cursor), paragraph.Italic));
                html.Append($"<span id=\"{span.Id}\">").Append(Inline(text, span.Range, paragraph.Italic)).Append("</span>");
                cursor = span.Range.End;
            }
            html.Append(Inline(text, new TextSpan(cursor, text.Length - cursor), paragraph.Italic));
            body.Append(paragraph.Style switch
            {
                ParagraphStyle.Heading => $"<h2>{html}</h2>\n",
                ParagraphStyle.SceneBreak => $"<p class=\"break\">{html}</p>\n",
                ParagraphStyle.BlockQuote => $"<blockquote><p>{html}</p></blockquote>\n",
                _ => $"<p>{html}</p>\n",
            });
        }
        var lang = Escape(language);
        var xhtml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE html>\n" +
            $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"{lang}\" lang=\"{lang}\">\n" +
            "<head>\n" +
            "<meta charset=\"UTF-8\"/>\n" +
            $"<title>{Escape(chapter.Title)}</title>\n" +
            "<link rel=\"stylesheet\" type=\"text/css\" href=\"../style.css\"/>\n" +
            "</head>\n" +
            "<body>\n" +
            $"<section epub:type=\"chapter\" id=\"chapter-{number}\">\n" +
            $"{body}</section>\n" +
            "</body>\n" +
            "</html>\n";
        return new Page(xhtml, fragments);
    }

    /// Text in `range`, escaped, with its italic parts in <i>.
    static string Inline(string text, TextSpan range, List<TextSpan> italic)
    {
        if (range.Length <= 0) return "";
        var cuts = new SortedSet<int> { range.Location, range.End };
        foreach (var span in italic)
            foreach (var edge in new[] { span.Location, span.End })
                if (edge > range.Location && edge < range.End) cuts.Add(edge);
        var points = cuts.ToList();
        var output = new StringBuilder();
        for (int i = 0; i + 1 < points.Count; i++)
        {
            int a = points[i], b = points[i + 1];
            var piece = Escape(text[a..b]);
            bool isItalic = italic.Any(s => s.Location <= a && s.End >= b);
            output.Append(isItalic ? $"<i>{piece}</i>" : piece);
        }
        return output.ToString();
    }

    /// Clips from each segment's place in the audio: a fragment runs from its first segment's
    /// start to its last one's end. In reading order.
    public static List<Clip> Clips(Dictionary<string, string> fragments, Dictionary<string, (double Begin, double End)> timing, double duration)
    {
        var byFragment = new Dictionary<string, Clip>();
        foreach (var (seg, fragment) in fragments)
        {
            if (!timing.TryGetValue(seg, out var t)) continue;
            byFragment[fragment] = byFragment.TryGetValue(fragment, out var c)
                ? c with { Begin = Math.Min(c.Begin, t.Begin), End = Math.Max(c.End, t.End) }
                : new Clip(fragment, t.Begin, t.End);
        }
        return byFragment.Values
            .Select(c => new Clip(c.Fragment, Math.Max(0, c.Begin), Math.Min(duration, c.End)))
            .Where(c => c.End - c.Begin >= 0.001)
            .OrderBy(c => c.Begin)
            .ToList();
    }

    // MARK: Package

    /// Every file of the book, in the order they're written (mimetype first).
    public static List<(string Path, Content Content)> Files(Book book)
    {
        var output = new List<(string, Content)>
        {
            ("mimetype", new Content("application/epub+zip")),
            ("META-INF/container.xml", new Content(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n" +
                "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles>\n" +
                "</container>\n")),
            ("OEBPS/content.opf", new Content(Package(book))),
            ("OEBPS/nav.xhtml", new Content(Nav(book))),
            ("OEBPS/style.css", new Content(Style)),
        };
        for (int i = 0; i < book.Chapters.Count; i++)
        {
            var chapter = book.Chapters[i];
            var n = Name(i);
            output.Add(($"OEBPS/text/{n}.xhtml", new Content(chapter.Xhtml)));
            if (HasOverlay(chapter))
            {
                output.Add(($"OEBPS/smil/{n}.smil", new Content(Smil(chapter, i))));
                output.Add(($"OEBPS/audio/{n}.m4a", new Content(FilePath: chapter.Audio)));
            }
        }
        return output;
    }

    /// Writes the book to `path` (replacing a file there): `mimetype` first and stored, text
    /// deflated, audio stored (it's already compressed) and streamed from disk.
    public static void Write(Book book, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in Files(book))
        {
            bool stored = name == "mimetype" || content.FilePath is not null;
            var entry = zip.CreateEntry(name, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var output = entry.Open();
            if (content.FilePath is { } file)
            {
                using var input = File.OpenRead(file);
                input.CopyTo(output);
            }
            else
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content.Text ?? "");
                output.Write(bytes);
            }
        }
    }

    static string Name(int i) => $"c{i + 1:000}";

    static bool HasOverlay(ChapterFile c) => c.Audio is not null && c.Clips.Count > 0;

    static string Package(Book book)
    {
        var modified = book.Modified.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var meta = new StringBuilder();
        var manifest = new StringBuilder(
            "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n" +
            "<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>\n");
        var spine = new StringBuilder();
        // Durations in whole milliseconds, so the parts add up to the total exactly.
        long total = 0;
        for (int i = 0; i < book.Chapters.Count; i++)
        {
            var c = book.Chapters[i];
            var n = Name(i);
            if (HasOverlay(c))
            {
                long ms = (long)Math.Round(c.Duration * 1000, MidpointRounding.AwayFromZero);
                total += ms;
                meta.Append($"<meta property=\"media:duration\" refines=\"#mo-{n}\">{Clock(ms)}</meta>\n");
                manifest.Append($"<item id=\"{n}\" href=\"text/{n}.xhtml\" media-type=\"application/xhtml+xml\" media-overlay=\"mo-{n}\"/>\n");
                manifest.Append($"<item id=\"mo-{n}\" href=\"smil/{n}.smil\" media-type=\"application/smil+xml\"/>\n");
                manifest.Append($"<item id=\"audio-{n}\" href=\"audio/{n}.m4a\" media-type=\"audio/mp4\"/>\n");
            }
            else manifest.Append($"<item id=\"{n}\" href=\"text/{n}.xhtml\" media-type=\"application/xhtml+xml\"/>\n");
            spine.Append($"<itemref idref=\"{n}\"/>\n");
        }
        if (total > 0)
        {
            meta.Append($"<meta property=\"media:duration\">{Clock(total)}</meta>\n");
            meta.Append("<meta property=\"media:active-class\">-epub-media-overlay-active</meta>\n");
        }
        var author = book.Author.Trim();
        return
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            $"<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"book-id\" xml:lang=\"{Escape(book.Language)}\">\n" +
            "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n" +
            $"<dc:identifier id=\"book-id\">{Escape(book.Identifier)}</dc:identifier>\n" +
            $"<dc:title>{Escape(book.Title.Length == 0 ? "Untitled" : book.Title)}</dc:title>\n" +
            (author.Length == 0 ? "" : $"<dc:creator>{Escape(author)}</dc:creator>\n") +
            $"<dc:language>{Escape(book.Language)}</dc:language>\n" +
            $"<meta property=\"dcterms:modified\">{modified}</meta>\n" +
            $"{meta}</metadata>\n" +
            "<manifest>\n" +
            $"{manifest}</manifest>\n" +
            "<spine>\n" +
            $"{spine}</spine>\n" +
            "</package>\n";
    }

    static string Nav(Book book)
    {
        var items = string.Join("\n", book.Chapters.Select((c, i) => $"<li><a href=\"text/{Name(i)}.xhtml\">{Escape(c.Title)}</a></li>"));
        var lang = Escape(book.Language);
        return
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE html>\n" +
            $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"{lang}\" lang=\"{lang}\">\n" +
            "<head><meta charset=\"UTF-8\"/><title>Contents</title></head>\n" +
            "<body>\n" +
            "<nav epub:type=\"toc\" id=\"toc\">\n" +
            "<h1>Contents</h1>\n" +
            "<ol>\n" +
            $"{items}\n" +
            "</ol>\n" +
            "</nav>\n" +
            "<nav epub:type=\"landmarks\" hidden=\"hidden\">\n" +
            $"<ol><li><a epub:type=\"bodymatter\" href=\"text/{Name(0)}.xhtml\">Start</a></li></ol>\n" +
            "</nav>\n" +
            "</body>\n" +
            "</html>\n";
    }

    static string Smil(ChapterFile chapter, int index)
    {
        var n = Name(index);
        var pars = string.Join("\n", chapter.Clips.Select((clip, k) =>
            $"<par id=\"p{k + 1}\"><text src=\"../text/{n}.xhtml#{clip.Fragment}\"/>" +
            $"<audio src=\"../audio/{n}.m4a\" clipBegin=\"{Clock(clip.Begin)}\" clipEnd=\"{Clock(clip.End)}\"/></par>"));
        return
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<smil xmlns=\"http://www.w3.org/ns/SMIL\" xmlns:epub=\"http://www.idpf.org/2007/ops\" version=\"3.0\">\n" +
            "<body>\n" +
            $"<seq id=\"seq-{n}\" epub:textref=\"../text/{n}.xhtml\" epub:type=\"chapter\">\n" +
            $"{pars}\n" +
            "</seq>\n" +
            "</body>\n" +
            "</smil>\n";
    }

    const string Style =
        "body { font-family: serif; line-height: 1.5; margin: 0 5%; }\n" +
        "h1 { text-align: center; font-weight: normal; margin: 2em 0 1.5em; }\n" +
        "h2 { text-align: center; font-size: 1.1em; font-weight: normal; margin: 1.5em 0 1em; }\n" +
        "p { margin: 0; text-indent: 1.5em; }\n" +
        "h1 + p, h2 + p, p.break + p, blockquote + p { text-indent: 0; }\n" +
        "p.break { text-align: center; text-indent: 0; margin: 1em 0; }\n" +
        "blockquote { margin: 1em 2em; }\n" +
        "blockquote p { text-indent: 0; }\n" +
        ".-epub-media-overlay-active { background-color: rgba(255, 213, 79, 0.55); border-radius: 0.15em; }\n";

    /// SMIL full clock value, "0:01:02.345".
    public static string Clock(double seconds) => Clock((long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero));

    static string Clock(long ms) =>
        string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");

    /// XML text, with the control characters XML forbids taken out.
    public static string Escape(string s)
    {
        var output = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': output.Append("&amp;"); break;
                case '<': output.Append("&lt;"); break;
                case '>': output.Append("&gt;"); break;
                case '"': output.Append("&quot;"); break;
                case '\t' or '\n' or '\r': output.Append(c); break;
                default:
                    if (c < 0x20 || c == '￾' || c == '￿') continue;
                    output.Append(c);
                    break;
            }
        }
        return output.ToString();
    }

    static readonly (string Language, string[] Words)[] Common =
    [
        ("en", ["the", "and", "of", "to", "was", "he", "she", "said", "that", "it"]),
        ("fr", ["le", "la", "les", "et", "est", "il", "elle", "dans", "que", "une"]),
        ("de", ["der", "die", "und", "das", "ist", "nicht", "sie", "er", "ich", "ein"]),
        ("es", ["el", "la", "los", "y", "que", "en", "dijo", "una", "por", "con"]),
        ("it", ["il", "la", "che", "e", "di", "non", "una", "per", "disse", "con"]),
        ("nl", ["de", "het", "en", "een", "van", "niet", "zei", "dat", "ik", "zij"]),
    ];

    /// The book's language ("en", "fr"…) from its commonest words; English when unsure.
    /// (The Mac uses NLLanguageRecognizer.)
    public static string LanguageOf(Project project)
    {
        var sample = string.Join(" ", project.Chapters.SelectMany(c => c.Paragraphs).Take(60).Select(p => p.Text)).ToLowerInvariant();
        var words = Regex.Matches(sample, @"\p{L}+").Select(m => m.Value).ToList();
        if (words.Count < 20) return "en";
        var counts = words.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var best = Common.Select(l => (l.Language, Score: l.Words.Sum(w => counts.GetValueOrDefault(w)))).OrderByDescending(x => x.Score).First();
        return best.Score >= words.Count / 20 ? best.Language : "en";
    }

    /// The same book (title and author) gets the same identifier every time, so a reading app
    /// replaces an earlier export instead of keeping both. Same value as the Mac's.
    public static string Identifier(string title, string author)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"innervoice-epub|{title}|{author}"));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30); // version 3 (name-based, MD5)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexStringLower(bytes);
        return $"urn:uuid:{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
