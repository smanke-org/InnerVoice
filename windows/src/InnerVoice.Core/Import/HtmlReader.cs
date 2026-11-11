// HTML and RTF, like the Mac's AttributedReader (which reads both through NSAttributedString):
// paragraphs, italics, and headings by size relative to the body text. RTF goes through
// RtfPipe to HTML first.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace InnerVoice.Core.Import;

internal static class HtmlReader
{
    public static List<RawBlock> ReadHtml(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string html;
        try { html = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            html = Encoding.GetEncoding(1252).GetString(bytes);
        }
        return Blocks(html);
    }

    public static List<RawBlock> ReadRtf(string path)
    {
        string html;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var rtf = File.ReadAllText(path, Encoding.GetEncoding(1252));
            html = RtfPipe.Rtf.ToHtml(rtf);
        }
        catch (Exception e)
        {
            throw new ImportException($"Unable to read “{Path.GetFileName(path)}”. {e.Message}");
        }
        return Blocks(html);
    }

    sealed record Style(double Size, bool Italic, bool Bold, bool Centered, bool Indented);

    sealed class Para
    {
        public readonly StringBuilder Text = new();
        public readonly List<(int Start, int Length, Style Style)> Runs = [];
        public bool Centered, Indented;
        public int? TagLevel;
    }

    static readonly HashSet<string> BlockTags = ["P", "DIV", "H1", "H2", "H3", "H4", "H5", "H6", "LI", "BLOCKQUOTE", "TR", "PRE", "SECTION", "ARTICLE", "CENTER", "BR", "TD", "DT", "DD"];
    static readonly HashSet<string> SkipTags = ["HEAD", "SCRIPT", "STYLE", "TITLE", "NOSCRIPT"];

    public static List<RawBlock> Blocks(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var paras = new List<Para>();
        var current = new Para();
        void Flush()
        {
            if (current.Text.ToString().Trim().Length > 0) paras.Add(current);
            current = new Para();
        }
        void Walk(INode node, Style style)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IText t)
                {
                    var text = Regex.Replace(t.Data, @"[\r\n\t ]+", " ");
                    if (text.Length == 0) continue;
                    current.Centered |= style.Centered;
                    current.Indented |= style.Indented;
                    current.Runs.Add((current.Text.Length, text.Length, style));
                    current.Text.Append(text);
                    continue;
                }
                if (child is not IElement e) continue;
                var tag = e.TagName.ToUpperInvariant();
                if (SkipTags.Contains(tag)) continue;
                var s = StyleOf(e, style);
                bool block = BlockTags.Contains(tag);
                if (block)
                {
                    Flush();
                    if (tag.Length == 2 && tag[0] == 'H' && char.IsDigit(tag[1])) current.TagLevel = tag[1] - '0' <= 2 ? 1 : 2;
                }
                Walk(e, s);
                if (block) Flush();
            }
        }
        Walk(doc.Body ?? (INode)doc.DocumentElement, new Style(12, false, false, false, false));
        Flush();

        // The body size is the most common font size, weighted by text length.
        var weights = new Dictionary<double, int>();
        foreach (var p in paras) foreach (var r in p.Runs) weights[Math.Round(r.Style.Size)] = weights.GetValueOrDefault(Math.Round(r.Style.Size)) + r.Length;
        double body = weights.Count > 0 ? weights.MaxBy(kv => kv.Value).Key : 12;

        var blocks = new List<RawBlock>();
        foreach (var p in paras)
        {
            var raw = p.Text.ToString();
            int leading = 0;
            while (leading < raw.Length && char.IsWhiteSpace(raw[leading])) leading++;
            var text = raw.Trim();
            var italic = new List<TextSpan>();
            bool allBold = true;
            double maxSize = 0;
            foreach (var (start, length, style) in p.Runs)
            {
                maxSize = Math.Max(maxSize, style.Size);
                if (!style.Bold && raw.Substring(start, length).Trim().Length > 0) allBold = false;
                if (style.Italic) italic.Add(new TextSpan(Math.Max(0, start - leading), length + Math.Min(0, start - leading)));
            }
            int? level = p.TagLevel;
            if (level is null)
            {
                if (maxSize >= body * 1.5) level = 1;
                else if (maxSize >= body * 1.15 || (allBold && text.Length < 60)) level = 2;
            }
            blocks.Add(new RawBlock(text, ManuscriptImporter.MergeRanges(italic, text.Length), level, p.Indented, p.Centered));
        }
        return blocks;
    }

    static readonly Dictionary<string, double> HeadingEm = new() { ["H1"] = 2, ["H2"] = 1.5, ["H3"] = 1.17, ["H4"] = 1, ["H5"] = 0.83, ["H6"] = 0.67 };

    static Style StyleOf(IElement e, Style parent)
    {
        var tag = e.TagName.ToUpperInvariant();
        double size = parent.Size;
        bool italic = parent.Italic || tag is "I" or "EM" or "CITE";
        bool bold = parent.Bold || tag is "B" or "STRONG" || HeadingEm.ContainsKey(tag);
        bool centered = parent.Centered || tag == "CENTER" || string.Equals(e.GetAttribute("align"), "center", StringComparison.OrdinalIgnoreCase);
        bool indented = parent.Indented || tag == "BLOCKQUOTE";
        if (HeadingEm.TryGetValue(tag, out var em)) size = parent.Size * em;
        var css = e.GetAttribute("style");
        if (!string.IsNullOrEmpty(css))
        {
            foreach (var decl in css.Split(';'))
            {
                var kv = decl.Split(':', 2);
                if (kv.Length != 2) continue;
                var key = kv[0].Trim().ToLowerInvariant();
                var value = kv[1].Trim().ToLowerInvariant();
                switch (key)
                {
                    case "font-size": size = Size(value, parent.Size) ?? size; break;
                    case "font-style": italic = value.Contains("italic") || value.Contains("oblique"); break;
                    case "font-weight": bold = value is "bold" or "bolder" || (int.TryParse(value, out var w) && w >= 600); break;
                    case "text-align": centered = value == "center"; break;
                    case "margin-left" or "padding-left" or "text-indent":
                        if (key != "text-indent" && Size(value, parent.Size) is { } m && m >= 22) indented = true;
                        break;
                }
            }
        }
        return new Style(size, italic, bold, centered, indented);
    }

    /// "14pt", "18px", "1.5em", "120%" in points.
    static double? Size(string v, double parent)
    {
        var m = Regex.Match(v, @"^([0-9.]+)\s*(pt|px|em|rem|%)?");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        return m.Groups[2].Value switch
        {
            "px" => n * 0.75,
            "em" or "rem" => n * parent,
            "%" => n / 100 * parent,
            _ => n,
        };
    }
}
