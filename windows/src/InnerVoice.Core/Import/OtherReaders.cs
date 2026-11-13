// Word and OpenDocument readers, ported from Sources/InnerVoiceKit/Import/
// ManuscriptImporter.swift and EpubPdfReaders.swift (EPUB: EpubReader.cs). The Swift readers use XMLParser's
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
