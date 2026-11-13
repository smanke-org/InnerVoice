// EPUB reading, ported from Sources/InnerVoiceKit/Import/EpubReader.swift. The Swift code
// unpacks the book to a folder and uses file URLs; this reads the zip in place and uses the
// entries' paths ("OEBPS/Text/ch1.xhtml") the same way.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace InnerVoice.Core.Import;

/// Reads an EPUB 2 or 3 book. Chapters come from the book's own table of contents (the
/// EPUB 3 nav document or the EPUB 2 toc.ncx), fragment anchors included, so a book kept in
/// one big file still splits where its contents say. Without a usable table of contents the
/// blocks go through `ChapterDetector` as before. Footnotes, endnotes and the note markers
/// in the text are left out: they aren't read aloud.
internal static class EpubReader
{
    /// One spine file as read.
    internal sealed record Document(string Path, List<RawBlock> Blocks, Dictionary<string, int> Anchors, HashSet<string> Types);

    /// A table of contents entry, in contents order.
    internal sealed record TocEntry(string Title, string Path, string? Fragment, int? Parent);

    sealed record ManifestItem(string Href, string MediaType, string Properties);

    sealed class Opf
    {
        public Dictionary<string, ManifestItem> Manifest { get; } = [];
        public List<string> Spine { get; } = [];
        /// The spine's toc attribute (EPUB 2: the NCX's manifest id).
        public string? TocID { get; set; }
        /// EPUB 2 guide references: (type, href).
        public List<(string Type, string Href)> Guide { get; } = [];
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
    }

    public static ManuscriptImporter.OtherResult Read(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        using var zip = Zip.Open(path, name, "an EPUB book");
        var container = Zip.Entry(zip, "META-INF/container.xml");
        var opfPath = container is null ? null : FirstAttribute(container, "rootfile", "full-path");
        var opfData = opfPath is null ? null : Zip.Entry(zip, opfPath);
        if (opfData is null) throw new ImportException($"Unable to read “{name}”. It isn't an EPUB book.");
        var opf = ParseOpf(opfData);
        var baseDir = opfPath!.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
        string PathOf(string href) => Normalize(baseDir + Uri.UnescapeDataString(href));

        var documents = new List<Document>();
        var blocks = new List<RawBlock>();
        foreach (var idref in opf.Spine)
        {
            if (!opf.Manifest.TryGetValue(idref, out var item) || !item.MediaType.Contains("html")) continue;
            var file = PathOf(item.Href);
            var data = Zip.Entry(zip, file);
            if (data is null) continue;
            var doc = XhtmlBlocks.Document(data) is { } d ? d with { Path = file }
                : new Document(file, HtmlReader.Blocks(Encoding.UTF8.GetString(data)), [], []);
            documents.Add(doc);
            var chunk = new List<RawBlock>(doc.Blocks);
            if (chunk.Count == 0) continue;
            // Each spine file usually starts a chapter; a short first line becomes its heading.
            if (chunk[0].HeadingLevel is null && chunk[0].Text.Length < 60 && chunk.Count > 2) chunk[0] = chunk[0] with { HeadingLevel = 1 };
            if (chunk.FindIndex(b => b.HeadingLevel is not null) == 0) chunk[0] = chunk[0] with { HeadingLevel = 1 };
            blocks.AddRange(chunk);
        }
        if (blocks.Count == 0) throw new ImportException($"“{name}” has no text in it.");

        // The table of contents: EPUB 3's nav document first, then EPUB 2's NCX.
        var toc = new List<TocEntry>();
        var skipped = opf.Guide.Where(g => Unread.Contains(g.Type)).Select(g => PathOf(g.Href.Split('#')[0])).ToHashSet();
        if (opf.Manifest.Values.FirstOrDefault(i => i.Properties.Split(' ').Contains("nav")) is { } nav
            && Zip.Entry(zip, PathOf(nav.Href)) is { } navData)
        {
            var navPath = PathOf(nav.Href);
            var (entries, landmarks) = NavReader.Parse(navData, navPath);
            toc = entries;
            skipped.UnionWith(landmarks.Where(l => Unread.Contains(l.Type)).Select(l => l.Path));
            skipped.Add(navPath);
        }
        var ncx = (opf.TocID is { } tocID && opf.Manifest.TryGetValue(tocID, out var byID) ? byID : null)
            ?? opf.Manifest.Values.FirstOrDefault(i => i.MediaType.Contains("dtbncx"));
        if (toc.Count < 2 && ncx is not null && Zip.Entry(zip, PathOf(ncx.Href)) is { } ncxData)
        {
            var entries = NcxReader.Parse(ncxData, PathOf(ncx.Href));
            if (entries.Count >= 2) toc = entries;
        }
        var fromTOC = ChaptersFromToc(documents, toc, skipped);
        return new ManuscriptImporter.OtherResult(blocks, opf.Title, opf.Author, fromTOC);
    }

    /// Landmark and guide types that are never read aloud.
    static readonly HashSet<string> Unread = ["toc", "copyright-page", "copyright", "loi", "lot"];
    /// Contents titles left out: the contents page itself, the copyright page, the notes.
    static readonly HashSet<string> UnreadTitles = ["contents", "table of contents", "copyright", "copyright page", "notes", "endnotes", "footnotes"];
    /// Whole files left out by their epub:type.
    static readonly HashSet<string> UnreadTypes = ["toc", "copyright-page", "footnotes", "endnotes", "rearnotes", "doc-toc", "doc-endnotes", "landmarks"];

    sealed record Start(int File, int Block, string Title);

    sealed class Group(bool fromToc, string title, List<RawBlock> blocks)
    {
        public bool FromToc { get; } = fromToc;
        public string Title { get; } = title;
        public List<RawBlock> Blocks { get; } = blocks;
    }

    /// Builds chapters from the table of contents, or null when it isn't usable (fewer than
    /// two chapter starts found in the text).
    internal static List<Chapter>? ChaptersFromToc(List<Document> documents, List<TocEntry> toc, HashSet<string> skipped)
    {
        var fileIndex = new Dictionary<string, int>();
        for (int i = 0; i < documents.Count; i++) fileIndex.TryAdd(documents[i].Path, i);
        // An entry starts a chapter unless it points inside a file one of its parents already
        // starts (a section of that chapter, not a chapter of its own).
        var starts = new List<Start>();
        foreach (var entry in toc)
        {
            if (!fileIndex.TryGetValue(entry.Path, out int f)) continue;
            var parent = entry.Parent;
            bool inside = false;
            while (parent is { } p)
            {
                if (toc[p].Path == entry.Path) inside = entry.Fragment is not null;
                parent = toc[p].Parent;
            }
            if (inside) continue;
            int block = 0;
            if (entry.Fragment is { Length: > 0 } fragment)
            {
                if (!documents[f].Anchors.TryGetValue(fragment, out int b)) continue;
                block = b;
            }
            var title = string.Join(' ', entry.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            starts.Add(new Start(f, block, title));
        }
        // Reading order wins; two entries at one place (a part and its first chapter) share a title.
        var merged = new List<Start>();
        foreach (var s in starts.OrderBy(s => s.File).ThenBy(s => s.Block))
        {
            if (merged.Count > 0 && merged[^1].File == s.File && merged[^1].Block == s.Block)
            {
                if (s.Title.Length > 0 && s.Title != merged[^1].Title) merged[^1] = merged[^1] with { Title = merged[^1].Title + " · " + s.Title };
            }
            else merged.Add(s);
        }
        if (merged.Count < 2) return null;

        var groups = new List<Group>();
        void Append(IEnumerable<RawBlock> more)
        {
            var list = more.ToList();
            if (list.Count == 0) return;
            if (groups.Count == 0) groups.Add(new Group(false, "", list));
            else groups[^1].Blocks.AddRange(list);
        }
        var startsByFile = merged.GroupBy(s => s.File).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Block).ToList());
        for (int f = 0; f < documents.Count; f++)
        {
            var doc = documents[f];
            // The contents page, the copyright page, a notes file: left out whole.
            if (skipped.Contains(doc.Path) || doc.Types.Overlaps(UnreadTypes)) continue;
            if (!startsByFile.TryGetValue(f, out var here))
            {
                // A file the contents don't list: its own chapter when it opens with a
                // heading, otherwise more of the chapter before (a chapter split in two files).
                if (doc.Blocks.Count > 0 && doc.Blocks[0].HeadingLevel is not null && groups.Count > 0)
                {
                    var chunk = new List<RawBlock>(doc.Blocks);
                    chunk[0] = chunk[0] with { HeadingLevel = 1 };
                    groups.Add(new Group(false, "", chunk));
                }
                else Append(doc.Blocks);
                continue;
            }
            Append(doc.Blocks.Take(here[0].Block));
            for (int k = 0; k < here.Count; k++)
            {
                int end = k + 1 < here.Count ? here[k + 1].Block : doc.Blocks.Count;
                int from = Math.Min(here[k].Block, end);
                groups.Add(new Group(true, here[k].Title, doc.Blocks.GetRange(from, end - from)));
            }
        }

        var chapters = new List<Chapter>();
        string? pendingTitle = null;
        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            if (!group.FromToc)
            {
                var found = ChapterDetector.Chapters(group.Blocks, "Opening");
                // Before the first chapter: a title page or a cover isn't worth a chapter.
                if (g == 0)
                    found.RemoveAll(c => c.WordCount < 40 && c.Paragraphs.All(p => !p.Text.Contains('“') && !p.Text.Contains('"')));
                chapters.AddRange(found);
                continue;
            }
            if (UnreadTitles.Contains(group.Title.ToLowerInvariant())) continue;
            var made = TocChapters(group.Title, group.Blocks);
            if (made.Count == 0)
            {
                // A part title page: its title goes before the next chapter's.
                if (group.Title.Length > 0) pendingTitle = pendingTitle is null ? group.Title : pendingTitle + " · " + group.Title;
                continue;
            }
            if (pendingTitle is { } pt) made[0].Title = made[0].Title.Length == 0 ? pt : pt + " · " + made[0].Title;
            pendingTitle = null;
            chapters.AddRange(made);
        }
        for (int i = 0; i < chapters.Count; i++)
            if (chapters[i].Title.Length == 0) chapters[i].Title = i == 0 ? "Opening" : "Untitled";
        return chapters.Count >= 2 ? chapters : null;
    }

    /// One contents entry's text as a chapter: the heading lines that repeat its title are
    /// dropped; a "Chapter 7" heading the contents left out still starts a chapter.
    internal static List<Chapter> TocChapters(string title, List<RawBlock> blocks)
    {
        var body = blocks.Select(b => b with { Text = b.Text.Trim() }).Where(b => b.Text.Length > 0).ToList();
        var key = Normalized(title);
        int dropped = 0;
        while (dropped < 3 && body.Count > 0 && body[0].Text.Length < 100
               && (body[0].HeadingLevel is not null || (Normalized(body[0].Text) is { Length: > 0 } n && key.Contains(n))))
        {
            body.RemoveAt(0);
            dropped++;
        }
        var chapters = new List<Chapter>();
        var current = new Chapter(title, []);
        bool subtitleOpen = false;
        foreach (var block in body)
        {
            if (block.HeadingLevel == 1 && ChapterDetector.LooksLikeHeading(block.Text) && current.Paragraphs.Count > 0)
            {
                chapters.Add(current);
                current = new Chapter(ChapterDetector.TitleCase(block.Text), []);
                subtitleOpen = true;
                continue;
            }
            if (subtitleOpen && current.Paragraphs.Count == 0 && block.HeadingLevel is not null && block.Text.Length < 60)
            {
                current.Title += ": " + block.Text;
                subtitleOpen = false;
                continue;
            }
            subtitleOpen = false;
            var style = ChapterDetector.IsSceneBreak(block.Text) ? ParagraphStyle.SceneBreak : block.IsBlockQuote ? ParagraphStyle.BlockQuote : ParagraphStyle.Body;
            current.Paragraphs.Add(new Paragraph(block.Text, style, block.Italic));
        }
        if (current.Paragraphs.Count > 0) chapters.Add(current);
        return chapters;
    }

    /// Lowercased letters and digits only, for comparing a heading with its contents title.
    internal static string Normalized(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// "OEBPS/../Text/c1.xhtml" → "Text/c1.xhtml".
    internal static string Normalize(string path)
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

    /// "../Text/ch1.xhtml#p3" read from the file at `doc` → (its path in the zip, "p3").
    internal static (string Path, string? Fragment) Resolve(string href, string doc)
    {
        int hash = href.IndexOf('#');
        var file = Uri.UnescapeDataString(hash >= 0 ? href[..hash] : href);
        var fragment = hash >= 0 ? Uri.UnescapeDataString(href[(hash + 1)..]) : null;
        if (file.Length == 0) return (doc, fragment);
        var dir = doc.Contains('/') ? doc[..(doc.LastIndexOf('/') + 1)] : "";
        return (Normalize(dir + file), fragment);
    }

    static string Local(string name) => name.Contains(':') ? name[(name.LastIndexOf(':') + 1)..] : name;

    static Opf ParseOpf(byte[] data)
    {
        var opf = new Opf();
        string? field = null;
        var buffer = new StringBuilder();
        try
        {
            foreach (var e in XmlEvents.Read(new MemoryStream(data)))
            {
                var local = Local(e.Name);
                switch (e.Kind)
                {
                    case XmlEvents.Kind.Start:
                        var a = e.Attributes;
                        switch (local)
                        {
                            case "item" when a.Get("id") is { } id && a.Get("href") is { } href:
                                opf.Manifest[id] = new ManifestItem(href, a.Get("media-type") ?? "", a.Get("properties") ?? "");
                                break;
                            case "spine": opf.TocID = a.Get("toc"); break;
                            case "itemref" when a.Get("idref") is { } idref && a.Get("linear") != "no": opf.Spine.Add(idref); break;
                            case "reference" when a.Get("type") is { } type && a.Get("href") is { } rhref:
                                opf.Guide.Add((type.ToLowerInvariant(), rhref));
                                break;
                            case "title" or "creator": field = local; buffer.Clear(); break;
                        }
                        break;
                    case XmlEvents.Kind.Text:
                        if (field is not null) buffer.Append(e.Text);
                        break;
                    case XmlEvents.Kind.End:
                        if (local != field) break;
                        var value = buffer.ToString().Trim();
                        if (local == "title" && opf.Title.Length == 0) opf.Title = value;
                        if (local == "creator" && opf.Author.Length == 0) opf.Author = value;
                        field = null;
                        break;
                }
            }
        }
        catch (XmlException) { }
        return opf;
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

    /// The EPUB 3 nav document: the toc nav's links (nested lists give parents) and the
    /// landmarks nav's typed links.
    internal static class NavReader
    {
        enum Kind { Toc, Landmarks, Other }

        public static (List<TocEntry> Toc, List<(string Type, string Path)> Landmarks) Parse(byte[] data, string at)
        {
            var toc = new List<TocEntry>();
            var landmarks = new List<(string Type, string Path)>();
            if (XhtmlBlocks.Prepare(data) is not { } prepared) return (toc, landmarks);
            Kind? nav = null;
            int navDepth = 0, depth = 0;
            // The last entry at each list depth, for parents.
            var lastAt = new Dictionary<int, int>();
            (string Href, string Type)? link = null;
            var buffer = new StringBuilder();
            try
            {
                foreach (var e in XmlEvents.Read(new MemoryStream(prepared)))
                {
                    var tag = e.Name.ToLowerInvariant();
                    switch (e.Kind)
                    {
                        case XmlEvents.Kind.Start:
                            if (tag == "nav")
                            {
                                navDepth++;
                                if (navDepth == 1)
                                {
                                    var types = XhtmlBlocks.Types(e.Attributes);
                                    nav = types.Contains("toc") || types.Contains("doc-toc") ? Kind.Toc : types.Contains("landmarks") ? Kind.Landmarks : Kind.Other;
                                    depth = 0;
                                    lastAt.Clear();
                                }
                                break;
                            }
                            if (nav is null) break;
                            if (tag is "ol" or "ul") depth++;
                            // A list item without a link of its own (a part shown as plain text) is no parent.
                            if (tag == "li") lastAt.Remove(depth);
                            if (tag == "a" && e.Attributes.Get("href") is { } href)
                            {
                                link = (href, XhtmlBlocks.Types(e.Attributes).FirstOrDefault() ?? "");
                                buffer.Clear();
                            }
                            break;
                        case XmlEvents.Kind.Text:
                            if (link is not null) buffer.Append(e.Text);
                            break;
                        case XmlEvents.Kind.End:
                            if (tag == "nav")
                            {
                                navDepth = Math.Max(0, navDepth - 1);
                                if (navDepth == 0) nav = null;
                                break;
                            }
                            if (nav is not { } kind) break;
                            if (tag is "ol" or "ul")
                            {
                                lastAt.Remove(depth);
                                depth = Math.Max(0, depth - 1);
                            }
                            if (tag == "a" && link is { } l)
                            {
                                var target = Resolve(l.Href, at);
                                if (kind == Kind.Toc)
                                {
                                    toc.Add(new TocEntry(buffer.ToString().Trim(), target.Path, target.Fragment,
                                                         lastAt.TryGetValue(depth - 1, out int p) ? p : null));
                                    lastAt[depth] = toc.Count - 1;
                                }
                                else if (kind == Kind.Landmarks) landmarks.Add((l.Type, target.Path));
                                link = null;
                            }
                            break;
                    }
                }
            }
            catch (XmlException) { }
            return (toc, landmarks);
        }
    }

    /// The EPUB 2 toc.ncx: nested navPoints, each a label and a content src.
    internal static class NcxReader
    {
        public static List<TocEntry> Parse(byte[] data, string at)
        {
            var entries = new List<TocEntry>();
            // For each open navPoint: its entry, once its content is read.
            var open = new List<int?>();
            var label = new StringBuilder();
            bool inLabel = false, inMap = false;
            try
            {
                foreach (var e in XmlEvents.Read(new MemoryStream(XhtmlBlocks.Prepare(data) ?? data)))
                {
                    var tag = Local(e.Name).ToLowerInvariant();
                    switch (e.Kind)
                    {
                        case XmlEvents.Kind.Start:
                            switch (tag)
                            {
                                case "navmap": inMap = true; break;
                                case "navpoint" when inMap: open.Add(null); label.Clear(); break;
                                case "navlabel" when inMap: inLabel = true; break;
                                case "content" when inMap:
                                    if (e.Attributes.Get("src") is not { } src || open.Count == 0 || open[^1] is not null) break;
                                    var target = Resolve(src, at);
                                    int? parent = open.Take(open.Count - 1).LastOrDefault(x => x is not null);
                                    entries.Add(new TocEntry(label.ToString().Trim(), target.Path, target.Fragment, parent));
                                    open[^1] = entries.Count - 1;
                                    break;
                            }
                            break;
                        case XmlEvents.Kind.Text:
                            if (inLabel) label.Append(e.Text);
                            break;
                        case XmlEvents.Kind.End:
                            switch (tag)
                            {
                                case "navmap": inMap = false; break;
                                case "navpoint": if (open.Count > 0) open.RemoveAt(open.Count - 1); break;
                                case "navlabel": inLabel = false; break;
                            }
                            break;
                    }
                }
            }
            catch (XmlException) { }
            return entries;
        }
    }
}

/// Block-level reading of well-formed XHTML: p, h1–h6, blockquote, li, and leaf divs.
/// Footnotes and endnotes (epub:type, doc- roles, note classes, asides) and the note
/// markers that link to them are left out.
public static class XhtmlBlocks
{
    static readonly Dictionary<string, string> Entities = MakeEntities();

    static Dictionary<string, string> MakeEntities()
    {
        var map = new Dictionary<string, string>
        {
            ["nbsp"] = " ", ["mdash"] = "—", ["ndash"] = "–", ["hellip"] = "…", ["rsquo"] = "’", ["lsquo"] = "‘",
            ["rdquo"] = "”", ["ldquo"] = "“", ["sbquo"] = "‚", ["bdquo"] = "„", ["shy"] = "", ["thinsp"] = " ", ["emsp"] = " ",
            ["ensp"] = " ", ["bull"] = "•", ["dagger"] = "†", ["Dagger"] = "‡", ["prime"] = "′", ["Prime"] = "″", ["trade"] = "™",
            ["oelig"] = "œ", ["OElig"] = "Œ", ["scaron"] = "š", ["Scaron"] = "Š", ["zwnj"] = "‌", ["zwj"] = "‍",
            ["lsaquo"] = "‹", ["rsaquo"] = "›", ["euro"] = "€", ["minus"] = "−", ["times"] = "×", ["divide"] = "÷",
        };
        // Latin-1, 160–255, in order.
        const string latin1 = "nbsp iexcl cent pound curren yen brvbar sect uml copy ordf laquo not shy reg macr deg plusmn sup2 sup3 acute micro para middot cedil sup1 ordm raquo frac14 frac12 frac34 iquest Agrave Aacute Acirc Atilde Auml Aring AElig Ccedil Egrave Eacute Ecirc Euml Igrave Iacute Icirc Iuml ETH Ntilde Ograve Oacute Ocirc Otilde Ouml times Oslash Ugrave Uacute Ucirc Uuml Yacute THORN szlig agrave aacute acirc atilde auml aring aelig ccedil egrave eacute ecirc euml igrave iacute icirc iuml eth ntilde ograve oacute ocirc otilde ouml divide oslash ugrave uacute ucirc uuml yacute thorn yuml";
        var names = latin1.Split(' ');
        for (int i = 0; i < names.Length; i++) map.TryAdd(names[i], ((char)(160 + i)).ToString());
        return map;
    }

    static readonly Regex EntityPattern = new("&([A-Za-z][A-Za-z0-9]*);", RegexOptions.CultureInvariant);
    static readonly Regex Doctype = new("<!DOCTYPE[^>]*>", RegexOptions.CultureInvariant);

    /// Named entities XML doesn't know become characters, and the DOCTYPE goes.
    public static byte[]? Prepare(byte[] data)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); } catch (DecoderFallbackException) { return null; }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        if (text.Contains('&')) text = EntityPattern.Replace(text, m => Entities.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        text = Doctype.Replace(text, "", 1);
        return Encoding.UTF8.GetBytes(text);
    }

    public static List<RawBlock>? Parse(byte[] data) => Document(data)?.Blocks;

    /// The epub:type words and doc- roles on an element.
    internal static HashSet<string> Types(Dictionary<string, string>? attributes)
    {
        var words = ((attributes.Get("epub:type") ?? "") + " " + (attributes.Get("role") ?? "")).ToLowerInvariant();
        return words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Contains(':') ? w[(w.LastIndexOf(':') + 1)..] : w).ToHashSet();
    }

    /// Notes, note markers and their back links: never read aloud.
    static readonly HashSet<string> NoteTypes = ["footnote", "footnotes", "endnote", "endnotes", "rearnote", "rearnotes", "note",
                                                 "noteref", "annoref", "doc-footnote", "doc-endnote", "doc-endnotes", "doc-noteref", "doc-backlink"];
    static readonly HashSet<string> NoteClasses = ["footnote", "footnotes", "endnote", "endnotes", "rearnote", "rearnotes", "footnote-ref", "noteref", "fnref"];
    /// What a note marker says: "12", "[3]", "*", "†", a single small letter.
    static readonly Regex MarkerPattern = new(@"^[\[(]?(?:\d{1,4}|[*†‡§¶]{1,3}|[a-z])[\])]?$", RegexOptions.CultureInvariant);

    public static bool IsNoteMarker(string text)
    {
        var t = text.Trim();
        return t.Length > 0 && MarkerPattern.IsMatch(t);
    }

    static readonly HashSet<string> BlockTags = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "div", "blockquote", "br", "tr"];
    static readonly HashSet<string> InlineTags = ["a", "span", "i", "em", "b", "strong", "sup", "sub", "small", "cite", "u", "abbr", "code", "q", "mark", "img"];
    static readonly HashSet<string> SkipTags = ["head", "script", "style", "aside", "nav"];

    sealed class Frame
    {
        public bool Italic, Quote, Block, Sup;
        /// A link that may be a note marker: where its text starts and how many superscripts
        /// there were when it opened.
        public (int Start, int Sups, bool Notey)? Link;
    }

    /// The blocks, element id → index of the block it starts (or sits in), and the epub:type
    /// words on the html, body and opening section. Null when it isn't well-formed XHTML.
    internal static EpubReader.Document? Document(byte[] data)
    {
        if (Prepare(data) is not { } prepared) return null;
        var blocks = new List<RawBlock>();
        var anchors = new Dictionary<string, int>();
        var types = new HashSet<string>();
        var text = new StringBuilder();
        var italic = new List<TextSpan>();
        int italicDepth = 0, italicStart = 0, quoteDepth = 0, skipDepth = 0, supCount = 0;
        int? level = null;
        var stack = new List<Frame>();

        void Flush()
        {
            var raw = text.ToString();
            int leading = 0;
            while (leading < raw.Length && char.IsWhiteSpace(raw[leading])) leading++;
            var trimmed = raw.Replace('\n', ' ').Trim();
            if (trimmed.Length > 0)
            {
                var shifted = italic.Select(i => new TextSpan(Math.Max(0, i.Location - leading), i.Length));
                blocks.Add(new RawBlock(trimmed, ManuscriptImporter.MergeRanges(shifted, trimmed.Length), level, quoteDepth > 0));
            }
            text.Clear();
            italic = [];
            level = null;
            if (italicDepth > 0) italicStart = 0;
        }

        try
        {
            foreach (var e in XmlEvents.Read(new MemoryStream(prepared)))
            {
                switch (e.Kind)
                {
                    case XmlEvents.Kind.Start:
                    {
                        var tag = e.Name.ToLowerInvariant();
                        var a = e.Attributes;
                        if (skipDepth > 0) { skipDepth++; break; }
                        var kinds = Types(a);
                        var classes = (a.Get("class") ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                        if (tag is "html" or "body" || (blocks.Count == 0 && text.ToString().All(char.IsWhiteSpace) && tag is "section" or "div" or "article"))
                            types.UnionWith(kinds);
                        if (SkipTags.Contains(tag) || kinds.Overlaps(NoteTypes) || classes.Overlaps(NoteClasses))
                        {
                            skipDepth = 1;
                            break;
                        }
                        var frame = new Frame();
                        if (BlockTags.Contains(tag))
                        {
                            Flush();
                            frame.Block = true;
                            if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1])) level = tag[1] - '0' <= 2 ? 1 : 2;
                            if (tag == "blockquote") { quoteDepth++; frame.Quote = true; }
                            var cls = (a.Get("class") ?? "").ToLowerInvariant();
                            if (cls.Contains("chapter") && (cls.Contains("title") || cls.Contains("head"))) level = 1;
                        }
                        if ((a.Get("id") ?? (tag == "a" ? a.Get("name") : null)) is { Length: > 0 } id)
                        {
                            if (!frame.Block && !InlineTags.Contains(tag)) Flush();
                            anchors.TryAdd(id, blocks.Count);
                        }
                        if (tag == "sup") { supCount++; frame.Sup = true; }
                        if (tag == "a" && a.Get("href") is { } href && href.Contains('#'))
                        {
                            var target = (href + " " + (a.Get("id") ?? "")).ToLowerInvariant();
                            bool notey = target.Contains("note") || target.Contains("fn") || target.Contains("ftn");
                            frame.Link = (text.Length, supCount, notey);
                        }
                        frame.Italic = tag is "i" or "em" or "cite" || classes.Any(c => c.Contains("italic"));
                        if (frame.Italic)
                        {
                            if (italicDepth == 0) italicStart = text.Length;
                            italicDepth++;
                        }
                        stack.Add(frame);
                        break;
                    }
                    case XmlEvents.Kind.Text:
                        if (skipDepth == 0) text.Append(e.Text);
                        break;
                    case XmlEvents.Kind.End:
                    {
                        if (skipDepth > 0) { skipDepth--; break; }
                        if (stack.Count == 0) break;
                        var frame = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        if (frame.Italic && italicDepth > 0)
                        {
                            italicDepth--;
                            if (italicDepth == 0 && text.Length > italicStart) italic.Add(new TextSpan(italicStart, text.Length - italicStart));
                        }
                        // A link that only says "12" or "*", in or around a superscript or
                        // pointing at a note, is a note marker: take it out of the text.
                        if (frame.Link is { } link)
                        {
                            int start = Math.Min(link.Start, text.Length);
                            bool inSup = stack.Any(f => f.Sup) || supCount > link.Sups;
                            if ((inSup || link.Notey) && IsNoteMarker(text.ToString(start, text.Length - start)))
                            {
                                text.Length = start;
                                italic = italic.Where(s => s.Location < start)
                                    .Select(s => new TextSpan(s.Location, Math.Min(s.Length, start - s.Location))).ToList();
                                if (italicDepth > 0 && italicStart > start) italicStart = start;
                            }
                        }
                        if (frame.Block)
                        {
                            Flush();
                            if (frame.Quote) quoteDepth = Math.Max(0, quoteDepth - 1);
                        }
                        break;
                    }
                }
            }
        }
        catch (XmlException) { return null; }
        Flush();
        return new EpubReader.Document("", blocks, anchors, types);
    }
}
