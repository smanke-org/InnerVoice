// Word, OpenDocument and EPUB readers, ported from Sources/InnerVoiceKit/Import/
// ManuscriptImporter.swift and EpubPdfReaders.swift. The Swift readers use XMLParser's
// events; `XmlEvents` gives the same start, text and end events with qualified names.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace InnerVoice.Core.Import;

public static partial class ManuscriptImporter
{
    private static partial OtherResult? ReadOther(string path, string ext) => ext switch
    {
        "docx" => new(DocxReader.Read(path)),
        "odt" => new(OdtReader.Read(path)),
        "epub" => EpubReader.Read(path),
        "pdf" => new(PdfReader.Read(path)),
        "rtf" => new(HtmlReader.ReadRtf(path)),
        "html" or "htm" or "xhtml" => new(HtmlReader.ReadHtml(path)),
        _ => null,
    };

    /// Merges touching or overlapping italic runs and clips them to the text.
    public static List<TextSpan> MergeRanges(IEnumerable<TextSpan> ranges, int limit)
    {
        var sorted = ranges.Select(r =>
        {
            int loc = Math.Min(r.Location, limit), end = Math.Min(r.End, limit);
            return new TextSpan(loc, Math.Max(0, end - loc));
        }).Where(r => r.Length > 0).OrderBy(r => r.Location).ToList();
        var output = new List<TextSpan>();
        foreach (var r in sorted)
        {
            if (output.Count > 0 && r.Location <= output[^1].End + 1)
            {
                var last = output[^1];
                output[^1] = new TextSpan(last.Location, Math.Max(last.End, r.End) - last.Location);
            }
            else output.Add(r);
        }
        return output;
    }

    /// Trims a paragraph and shifts its italic runs to match.
    internal static RawBlock? Finish(string text, List<TextSpan> italic, int? level = null, bool quote = false, bool centered = false)
    {
        int leading = 0;
        while (leading < text.Length && char.IsWhiteSpace(text[leading])) leading++;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        var shifted = italic.Select(i => new TextSpan(Math.Max(0, i.Location - leading), i.Length));
        return new RawBlock(trimmed, MergeRanges(shifted, trimmed.Length), level, quote, centered);
    }
}

/// XMLParser-style events: start (qualified name, attributes), text, end.
internal static class XmlEvents
{
    public enum Kind { Start, Text, End }
    public readonly record struct Event(Kind Kind, string Name, Dictionary<string, string>? Attributes, string? Text);

    public static IEnumerable<Event> Read(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = false };
        using var r = XmlReader.Create(stream, settings);
        while (r.Read())
        {
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                    var name = r.Name;
                    var attrs = new Dictionary<string, string>();
                    if (r.HasAttributes)
                    {
                        for (int i = 0; i < r.AttributeCount; i++)
                        {
                            r.MoveToAttribute(i);
                            attrs[r.Name] = r.Value;
                        }
                        r.MoveToElement();
                    }
                    bool empty = r.IsEmptyElement;
                    yield return new Event(Kind.Start, name, attrs, null);
                    if (empty) yield return new Event(Kind.End, name, null, null);
                    break;
                case XmlNodeType.EndElement:
                    yield return new Event(Kind.End, r.Name, null, null);
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    yield return new Event(Kind.Text, "", null, r.Value);
                    break;
            }
        }
    }

    public static string? Get(this Dictionary<string, string>? a, string key) => a is not null && a.TryGetValue(key, out var v) ? v : null;
}

internal static class Zip
{
    public static byte[]? Entry(ZipArchive zip, string path)
    {
        var e = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, path, StringComparison.OrdinalIgnoreCase));
        if (e is null) return null;
        using var s = e.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    public static ZipArchive Open(string path, string name, string kind)
    {
        try { return ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new ImportException($"Unable to read “{name}”. It isn't {kind}."); }
    }
}

/// Reads word/document.xml directly, so heading styles and italics survive.
internal static class DocxReader
{
    public static List<RawBlock> Read(string path)
    {
        var name = Path.GetFileName(path);
        using var zip = Zip.Open(path, name, "a Word document");
        var data = Zip.Entry(zip, "word/document.xml") ?? throw new ImportException($"Unable to read “{name}”. It isn't a Word document.");
        var styles = Zip.Entry(zip, "word/styles.xml") is { } s ? StyleNames(s) : [];
        try { return Parse(data, styles); }
        catch (XmlException) { throw new ImportException($"Unable to read “{name}”. The document is damaged."); }
    }

    /// Maps style ids to names ("Heading1" → "heading 1"), since ids are localized.
    static Dictionary<string, string> StyleNames(byte[] data)
    {
        var map = new Dictionary<string, string>();
        string? current = null;
        try
        {
            foreach (var e in XmlEvents.Read(new MemoryStream(data)))
            {
                if (e.Kind != XmlEvents.Kind.Start) continue;
                if (e.Name == "w:style") current = e.Attributes.Get("w:styleId");
                if (e.Name == "w:name" && current is not null && e.Attributes.Get("w:val") is { } v) map[current] = v.ToLowerInvariant();
            }
        }
        catch (XmlException) { }
        return map;
    }

    static List<RawBlock> Parse(byte[] data, Dictionary<string, string> styleNames)
    {
        var blocks = new List<RawBlock>();
        var text = new StringBuilder();
        var italic = new List<TextSpan>();
        bool inParagraph = false, inRunProps = false, runItalic = false, centered = false, inText = false;
        string paragraphStyle = "";
        int leftIndent = 0, inDeleted = 0;
        foreach (var e in XmlEvents.Read(new MemoryStream(data)))
        {
            switch (e.Kind)
            {
                case XmlEvents.Kind.Start:
                    var a = e.Attributes;
                    switch (e.Name)
                    {
                        case "w:p":
                            inParagraph = true; text.Clear(); italic = []; paragraphStyle = ""; centered = false; leftIndent = 0;
                            break;
                        case "w:pStyle":
                            var val = a.Get("w:val") ?? "";
                            paragraphStyle = styleNames.TryGetValue(val, out var n) ? n : val.ToLowerInvariant();
                            break;
                        case "w:jc": centered = a.Get("w:val") == "center"; break;
                        case "w:ind": leftIndent = int.TryParse(a.Get("w:left") ?? a.Get("w:start") ?? "0", out var li) ? li : 0; break;
                        case "w:r": runItalic = false; break;
                        case "w:rPr": inRunProps = true; break;
                        case "w:i" or "w:iCs":
                            if (inRunProps) runItalic = a.Get("w:val") is not ("0" or "false" or "off");
                            break;
                        case "w:rStyle":
                            var rs = a.Get("w:val") ?? "";
                            var style = (styleNames.TryGetValue(rs, out var rn) ? rn : rs).ToLowerInvariant();
                            if (style.Contains("emphasis") || style.Contains("italic")) runItalic = true;
                            break;
                        case "w:t": inText = true; break;
                        case "w:tab" or "w:br" or "w:cr": if (inParagraph && inDeleted == 0) text.Append(' '); break;
                        case "w:del" or "w:moveFrom": inDeleted++; break;
                    }
                    break;
                case XmlEvents.Kind.Text:
                    if (!inText || !inParagraph || inDeleted != 0) break;
                    int start = text.Length;
                    text.Append(e.Text);
                    if (runItalic)
                    {
                        int len = e.Text!.Length;
                        if (italic.Count > 0 && italic[^1].End == start) italic[^1] = new TextSpan(italic[^1].Location, italic[^1].Length + len);
                        else italic.Add(new TextSpan(start, len));
                    }
                    break;
                case XmlEvents.Kind.End:
                    switch (e.Name)
                    {
                        case "w:t": inText = false; break;
                        case "w:rPr": inRunProps = false; break;
                        case "w:del" or "w:moveFrom": inDeleted--; break;
                        case "w:p":
                            inParagraph = false;
                            int? level = null;
                            if (paragraphStyle == "title" || paragraphStyle.StartsWith("heading 1") || paragraphStyle == "heading1") level = 1;
                            else if (paragraphStyle.StartsWith("heading") || paragraphStyle == "subtitle") level = 2;
                            bool quote = paragraphStyle.Contains("quote") || leftIndent >= 1080;
                            if (ManuscriptImporter.Finish(text.ToString(), italic, level, quote, centered) is { } b) blocks.Add(b);
                            break;
                    }
                    break;
            }
        }
        return blocks;
    }
}

internal static class OdtReader
{
    public static List<RawBlock> Read(string path)
    {
        var name = Path.GetFileName(path);
        using var zip = Zip.Open(path, name, "an OpenDocument text file");
        var data = Zip.Entry(zip, "content.xml") ?? throw new ImportException($"Unable to read “{name}”. It isn't an OpenDocument text file.");
        try { return Parse(data); }
        catch (XmlException) { throw new ImportException($"Unable to read “{name}”. The document is damaged."); }
    }

    static List<RawBlock> Parse(byte[] data)
    {
        var blocks = new List<RawBlock>();
        var italicStyles = new HashSet<string>();
        var centeredStyles = new HashSet<string>();
        string? currentStyle = null;
        int depth = 0, inNote = 0;
        var text = new StringBuilder();
        var italic = new List<TextSpan>();
        var spanItalic = new List<bool>();
        int? level = null;
        bool centered = false;

        void Append(string s)
        {
            if (depth <= 0 || inNote != 0) return;
            int start = text.Length;
            text.Append(s);
            if (spanItalic.Count > 0 && spanItalic[^1]) italic.Add(new TextSpan(start, s.Length));
        }

        foreach (var e in XmlEvents.Read(new MemoryStream(data)))
        {
            switch (e.Kind)
            {
                case XmlEvents.Kind.Start:
                    var a = e.Attributes;
                    switch (e.Name)
                    {
                        case "style:style": currentStyle = a.Get("style:name"); break;
                        case "style:text-properties":
                            if (currentStyle is not null && a.Get("fo:font-style") == "italic") italicStyles.Add(currentStyle);
                            break;
                        case "style:paragraph-properties":
                            if (currentStyle is not null && a.Get("fo:text-align") == "center") centeredStyles.Add(currentStyle);
                            break;
                        case "text:p" or "text:h":
                            depth++;
                            if (depth == 1)
                            {
                                text.Clear(); italic = []; spanItalic.Clear();
                                var style = a.Get("text:style-name") ?? "";
                                centered = centeredStyles.Contains(style);
                                if (e.Name == "text:h") level = (int.TryParse(a.Get("text:outline-level") ?? "1", out var ol) ? ol : 1) == 1 ? 1 : 2;
                                else level = style.ToLowerInvariant().Contains("title") ? 1 : null;
                            }
                            break;
                        case "text:span": spanItalic.Add(italicStyles.Contains(a.Get("text:style-name") ?? "")); break;
                        case "text:s": Append(new string(' ', int.TryParse(a.Get("text:c") ?? "1", out var c) ? c : 1)); break;
                        case "text:tab" or "text:line-break": Append(" "); break;
                        case "text:note": inNote++; break;
                    }
                    break;
                case XmlEvents.Kind.Text:
                    Append(e.Text!);
                    break;
                case XmlEvents.Kind.End:
                    switch (e.Name)
                    {
                        case "style:style": currentStyle = null; break;
                        case "text:span": if (spanItalic.Count > 0) spanItalic.RemoveAt(spanItalic.Count - 1); break;
                        case "text:note": inNote--; break;
                        case "text:p" or "text:h":
                            depth--;
                            if (depth != 0) break;
                            if (ManuscriptImporter.Finish(text.ToString(), italic, level, false, centered) is { } b) blocks.Add(b);
                            break;
                    }
                    break;
            }
        }
        return blocks;
    }
}

internal static class EpubReader
{
    public static ManuscriptImporter.OtherResult Read(string path)
    {
        var name = Path.GetFileName(path);
        using var zip = Zip.Open(path, name, "an EPUB book");
        var container = Zip.Entry(zip, "META-INF/container.xml");
        var opfPath = container is null ? null : FirstAttribute(container, "rootfile", "full-path");
        var opfData = opfPath is null ? null : Zip.Entry(zip, opfPath);
        if (opfData is null) throw new ImportException($"Unable to read “{name}”. It isn't an EPUB book.");
        var (manifest, spine, title, author) = ParseOpf(opfData);
        var baseDir = opfPath!.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
        var blocks = new List<RawBlock>();
        foreach (var idref in spine)
        {
            if (!manifest.TryGetValue(idref, out var href)) continue;
            var file = Normalize(baseDir + Uri.UnescapeDataString(href));
            var data = Zip.Entry(zip, file);
            if (data is null) continue;
            var chunk = XhtmlBlocks.Parse(data) ?? HtmlReader.Blocks(Encoding.UTF8.GetString(data));
            if (chunk.Count == 0) continue;
            // Each spine file usually starts a chapter; a short first line becomes its heading.
            if (chunk[0].HeadingLevel is null && chunk[0].Text.Length < 60 && chunk.Count > 2) chunk[0] = chunk[0] with { HeadingLevel = 1 };
            if (chunk.FindIndex(b => b.HeadingLevel is not null) == 0) chunk[0] = chunk[0] with { HeadingLevel = 1 };
            blocks.AddRange(chunk);
        }
        if (blocks.Count == 0) throw new ImportException($"“{name}” has no text in it.");
        return new ManuscriptImporter.OtherResult(blocks, title, author);
    }

    /// "OEBPS/../Text/c1.xhtml" → "Text/c1.xhtml".
    static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Split('/'))
        {
            if (p is "" or ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(p);
        }
        return string.Join("/", parts);
    }

    static (Dictionary<string, string> Manifest, List<string> Spine, string Title, string Author) ParseOpf(byte[] data)
    {
        var manifest = new Dictionary<string, string>();
        var spine = new List<string>();
        string title = "", author = "";
        string? field = null;
        var buffer = new StringBuilder();
        try
        {
            foreach (var e in XmlEvents.Read(new MemoryStream(data)))
            {
                var local = e.Name.Contains(':') ? e.Name[(e.Name.LastIndexOf(':') + 1)..] : e.Name;
                switch (e.Kind)
                {
                    case XmlEvents.Kind.Start:
                        var a = e.Attributes;
                        if (local == "item" && a.Get("id") is { } id && a.Get("href") is { } href && (a.Get("media-type") ?? "").Contains("html")) manifest[id] = href;
                        else if (local == "itemref" && a.Get("idref") is { } idref && a.Get("linear") != "no") spine.Add(idref);
                        else if (local is "title" or "creator") { field = local; buffer.Clear(); }
                        break;
                    case XmlEvents.Kind.Text:
                        if (field is not null) buffer.Append(e.Text);
                        break;
                    case XmlEvents.Kind.End:
                        if (local != field) break;
                        var value = buffer.ToString().Trim();
                        if (local == "title" && title.Length == 0) title = value;
                        if (local == "creator" && author.Length == 0) author = value;
                        field = null;
                        break;
                }
            }
        }
        catch (XmlException) { }
        return (manifest, spine, title, author);
    }

    static string? FirstAttribute(byte[] data, string element, string attribute)
    {
        try
        {
            foreach (var e in XmlEvents.Read(new MemoryStream(data)))
                if (e.Kind == XmlEvents.Kind.Start && e.Name.EndsWith(element) && e.Attributes.Get(attribute) is { } v) return v;
        }
        catch (XmlException) { }
        return null;
    }
}

/// Block-level reading of well-formed XHTML: p, h1–h6, blockquote, li, and leaf divs.
internal static class XhtmlBlocks
{
    static readonly (string, string)[] Entities =
    [
        ("&nbsp;", " "), ("&mdash;", "—"), ("&ndash;", "–"), ("&hellip;", "…"), ("&rsquo;", "’"), ("&lsquo;", "‘"),
        ("&rdquo;", "”"), ("&ldquo;", "“"), ("&eacute;", "é"), ("&egrave;", "è"), ("&copy;", "©"), ("&shy;", ""),
        ("&thinsp;", " "), ("&emsp;", " "), ("&ensp;", " "), ("&laquo;", "«"), ("&raquo;", "»"), ("&bull;", "•"),
    ];
    static readonly HashSet<string> BlockTags = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "div", "blockquote", "br", "tr"];
    static readonly HashSet<string> SkipTags = ["head", "script", "style", "aside", "nav"];
    static readonly Regex Doctype = new("<!DOCTYPE[^>]*>", RegexOptions.CultureInvariant);

    public static List<RawBlock>? Parse(byte[] data)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); } catch (DecoderFallbackException) { return null; }
        foreach (var (k, v) in Entities) text = text.Replace(k, v);
        text = Doctype.Replace(text, "", 1);
        var blocks = new List<RawBlock>();
        var buf = new StringBuilder();
        var italic = new List<TextSpan>();
        int italicDepth = 0, italicStart = 0, quoteDepth = 0, skipDepth = 0;
        int? level = null;
        var italicStack = new List<bool>();

        void Flush()
        {
            var raw = buf.ToString();
            int leading = 0;
            while (leading < raw.Length && char.IsWhiteSpace(raw[leading])) leading++;
            var trimmed = raw.Replace('\n', ' ').Trim();
            if (trimmed.Length > 0)
            {
                var shifted = italic.Select(i => new TextSpan(Math.Max(0, i.Location - leading), i.Length));
                blocks.Add(new RawBlock(trimmed, ManuscriptImporter.MergeRanges(shifted, trimmed.Length), level, quoteDepth > 0));
            }
            buf.Clear();
            italic = [];
            level = null;
            if (italicDepth > 0) italicStart = 0;
        }

        try
        {
            foreach (var e in XmlEvents.Read(new MemoryStream(Encoding.UTF8.GetBytes(text))))
            {
                switch (e.Kind)
                {
                    case XmlEvents.Kind.Start:
                    {
                        var tag = e.Name.ToLowerInvariant();
                        var a = e.Attributes;
                        if (SkipTags.Contains(tag) || a.Get("epub:type") == "footnote") { skipDepth++; break; }
                        if (BlockTags.Contains(tag))
                        {
                            Flush();
                            if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1])) level = tag[1] - '0' <= 2 ? 1 : 2;
                            if (tag == "blockquote") quoteDepth++;
                            var cls = (a.Get("class") ?? "").ToLowerInvariant();
                            if (cls.Contains("chapter") && (cls.Contains("title") || cls.Contains("head"))) level = 1;
                        }
                        bool opens = tag is "i" or "em" or "cite" || (a.Get("class") ?? "").ToLowerInvariant().Contains("italic");
                        italicStack.Add(opens);
                        if (opens)
                        {
                            if (italicDepth == 0) italicStart = buf.Length;
                            italicDepth++;
                        }
                        break;
                    }
                    case XmlEvents.Kind.Text:
                        if (skipDepth == 0) buf.Append(e.Text);
                        break;
                    case XmlEvents.Kind.End:
                    {
                        var tag = e.Name.ToLowerInvariant();
                        if (SkipTags.Contains(tag)) { skipDepth = Math.Max(0, skipDepth - 1); break; }
                        bool opened = italicStack.Count > 0 && italicStack[^1];
                        if (italicStack.Count > 0) italicStack.RemoveAt(italicStack.Count - 1);
                        if (opened && italicDepth > 0)
                        {
                            italicDepth--;
                            if (italicDepth == 0 && buf.Length > italicStart) italic.Add(new TextSpan(italicStart, buf.Length - italicStart));
                        }
                        if (BlockTags.Contains(tag))
                        {
                            Flush();
                            if (tag == "blockquote") quoteDepth = Math.Max(0, quoteDepth - 1);
                        }
                        break;
                    }
                }
            }
        }
        catch (XmlException) { return null; }
        Flush();
        return blocks;
    }
}
