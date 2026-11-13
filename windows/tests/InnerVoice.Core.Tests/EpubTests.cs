using System.IO.Compression;
using System.Text;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// Ports of the Kit's EPUB reading tests (Tests/InnerVoiceKitTests/EpubTests.swift): chapters
/// from the table of contents, footnotes left out. (EPUB with Audio isn't on Windows.)
public class EpubTests
{
    static string Page(string body, string types = "") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
        <head><title>t</title></head><body{(types.Length == 0 ? "" : $" epub:type=\"{types}\"")}>{body}</body></html>
        """;

    /// Zips `files` (path → text) into an EPUB in a temp folder.
    static string MakeEpub(Dictionary<string, string> files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "InnerVoiceEpubTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "book.epub");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var all = new Dictionary<string, string>(files)
            {
                ["mimetype"] = "application/epub+zip",
                ["META-INF/container.xml"] = """<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""",
            };
            foreach (var (name, text) in all)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return path;
    }

    const string Prose = "The road ran on past the mill and the river, and the light went long and gold across the fields while the cart rolled on.";

    // MARK: - Table of contents

    [Fact]
    public void Epub3NavGivesChaptersWithFragmentsAndParts()
    {
        const string opf = """
            <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="3.0"><metadata><dc:title>Placeholder Book</dc:title></metadata>
            <manifest>
            <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            <item id="title" href="Text/title.xhtml" media-type="application/xhtml+xml"/>
            <item id="copy" href="Text/copyright.xhtml" media-type="application/xhtml+xml"/>
            <item id="part1" href="Text/part1.xhtml" media-type="application/xhtml+xml"/>
            <item id="body" href="Text/body.xhtml" media-type="application/xhtml+xml"/>
            </manifest>
            <spine><itemref idref="title"/><itemref idref="copy"/><itemref idref="nav" linear="no"/><itemref idref="part1"/><itemref idref="body"/></spine></package>
            """;
        var nav = Page("""
            <nav epub:type="toc"><h1>Contents</h1><ol>
              <li><a href="Text/copyright.xhtml">Copyright</a></li>
              <li><a href="Text/part1.xhtml">Part One</a><ol>
                <li><a href="Text/body.xhtml#ch1">The Mill</a><ol><li><a href="Text/body.xhtml#ch1-s2">A Section</a></li></ol></li>
                <li><a href="Text/body.xhtml#ch2">The River</a></li>
              </ol></li>
              <li><a href="Text/body.xhtml#ch3">Chapter Three: Home</a></li>
            </ol></nav>
            <nav epub:type="landmarks"><ol><li><a epub:type="copyright-page" href="Text/copyright.xhtml">Copyright</a></li></ol></nav>
            """);
        var body = Page($"""
            <section id="ch1"><h2>The Mill</h2><p>{Prose}</p><h3 id="ch1-s2">A Section</h3><p>“Still here,” said Tom.</p></section>
            <section id="ch2"><h2>The River</h2><p>{Prose}</p></section>
            <h2 id="ch3">Chapter Three</h2><h3>Home</h3><p>{Prose}</p><h1>Chapter 4</h1><p>{Prose}</p>
            """);
        var epub = MakeEpub(new()
        {
            ["OEBPS/content.opf"] = opf,
            ["OEBPS/nav.xhtml"] = nav,
            ["OEBPS/Text/title.xhtml"] = Page("<h1>Placeholder Book</h1><p>A. Writer</p>"),
            ["OEBPS/Text/copyright.xhtml"] = Page($"<p>Copyright © 2026 A. Writer. All rights reserved. {Prose}</p>"),
            ["OEBPS/Text/part1.xhtml"] = Page("<h1>Part One</h1>"),
            ["OEBPS/Text/body.xhtml"] = body,
        });
        var book = ManuscriptImporter.Read(epub);
        Assert.Equal(["Part One · The Mill", "The River", "Chapter Three: Home", "Chapter 4"], book.Chapters.Select(c => c.Title));
        // The section inside chapter 1 stays in it; heading lines that repeat the title are gone.
        Assert.Equal([Prose, "A Section", "“Still here,” said Tom."], book.Chapters[0].Paragraphs.Select(p => p.Text));
        Assert.Equal([Prose], book.Chapters[2].Paragraphs.Select(p => p.Text));
        // The copyright page and the title page aren't narrated.
        Assert.DoesNotContain(book.Chapters.SelectMany(c => c.Paragraphs), p => p.Text.Contains("All rights reserved"));
        Assert.DoesNotContain(book.Chapters, c => c.Title == "Placeholder Book");
    }

    [Fact]
    public void Epub2NcxGivesChaptersFromFragments()
    {
        const string opf = """
            <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="2.0"><metadata><dc:title>Placeholder Two</dc:title></metadata>
            <manifest><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/><item id="all" href="all.html" media-type="application/xhtml+xml"/></manifest>
            <spine toc="ncx"><itemref idref="all"/></spine></package>
            """;
        const string ncx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap>
            <navPoint id="n1" playOrder="1"><navLabel><text>Prologue</text></navLabel><content src="all.html#pro"/></navPoint>
            <navPoint id="n2" playOrder="2"><navLabel><text>Arrival</text></navLabel><content src="all.html#a1"/></navPoint>
            <navPoint id="n3" playOrder="3"><navLabel><text>Departure</text></navLabel><content src="all.html#a2"/></navPoint>
            </navMap></ncx>
            """;
        // No headings at all: only the anchors say where chapters start.
        var all = Page($"""
            <p class="x" id="pro">{Prose}</p><p>Before dawn.</p>
            <a id="a1"/><p>{Prose}</p>
            <p><a name="a2"/>{Prose}</p><p>Last words.</p>
            """);
        var book = ManuscriptImporter.Read(MakeEpub(new() { ["OEBPS/content.opf"] = opf, ["OEBPS/toc.ncx"] = ncx, ["OEBPS/all.html"] = all }));
        Assert.Equal(["Prologue", "Arrival", "Departure"], book.Chapters.Select(c => c.Title));
        Assert.Equal([2, 1, 2], book.Chapters.Select(c => c.Paragraphs.Count));
    }

    [Fact]
    public void EpubWithoutUsableContentsFallsBackToHeadings()
    {
        const string opf = """
            <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="3.0"><metadata><dc:title>T</dc:title></metadata>
            <manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="c" href="c.xhtml" media-type="application/xhtml+xml"/></manifest>
            <spine><itemref idref="c"/></spine></package>
            """;
        var nav = Page("<nav epub:type=\"toc\"><ol><li><a href=\"c.xhtml\">Start</a></li></ol></nav>");
        var c = Page($"<h2>Chapter 1</h2><p>{Prose}</p><h2>Chapter 2</h2><p>{Prose}</p>");
        var book = ManuscriptImporter.Read(MakeEpub(new() { ["OEBPS/content.opf"] = opf, ["OEBPS/nav.xhtml"] = nav, ["OEBPS/c.xhtml"] = c }));
        Assert.Equal(["Chapter 1", "Chapter 2"], book.Chapters.Select(ch => ch.Title));
    }

    // MARK: - Footnotes

    [Fact]
    public void FootnotesAndMarkersAreLeftOut()
    {
        var xhtml = Page("""
            <h1>Chapter 1</h1>
            <p>The tide came in.<a epub:type="noteref" href="#fn1">1</a> Then it went out.<sup><a href="#n2">2</a></sup></p>
            <p>See <a href="#c3">chapter three</a> and page <a href="#p3">3</a> for the rest.</p>
            <p>A marker<a href="notes.xhtml#ftn4" id="ref4">[4]</a> in brackets, one in <i>italics<a role="doc-noteref" href="#x">*</a> here</i>.</p>
            <aside epub:type="footnote" id="fn1"><p>1. A note about tides.</p></aside>
            <p epub:type="footnote" id="n2">2. A note in a paragraph.</p>
            <div class="footnote"><p>Another note.</p></div>
            <p>After the notes, the story goes on.</p>
            <section epub:type="endnotes"><ol><li epub:type="endnote" id="e1"><p>An endnote.</p></li></ol></section>
            """);
        var blocks = XhtmlBlocks.Parse(Encoding.UTF8.GetBytes(xhtml));
        Assert.NotNull(blocks);
        Assert.Equal(["Chapter 1", "The tide came in. Then it went out.",
                      "See chapter three and page 3 for the rest.",
                      "A marker in brackets, one in italics here.",
                      "After the notes, the story goes on."], blocks.Select(b => b.Text));
        var span = Assert.Single(blocks[3].Italic);
        Assert.Equal("italics here", blocks[3].Text.Substring(span.Location, span.Length));
    }

    [Fact]
    public void XhtmlKnowsLatinEntities()
    {
        var blocks = XhtmlBlocks.Parse(Encoding.UTF8.GetBytes(Page("<p>Caf&eacute; &ntilde; &uuml;ber &sect;2</p>")));
        Assert.Equal("Café ñ über §2", blocks?.FirstOrDefault()?.Text);
    }
}
