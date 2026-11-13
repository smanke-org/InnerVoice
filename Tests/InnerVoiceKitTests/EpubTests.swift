import Foundation
import Testing
@testable import InnerVoiceKit

// MARK: - Helpers

private func tempFolder() -> URL {
    let url = FileManager.default.temporaryDirectory.appending(path: "InnerVoiceEpubTests-\(UUID().uuidString)")
    try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

private func page(_ body: String, types: String = "") -> String {
    """
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE html>
    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
    <head><title>t</title></head><body\(types.isEmpty ? "" : " epub:type=\"\(types)\"")>\(body)</body></html>
    """
}

/// Zips `files` (path → text) into an EPUB with ditto, as the existing import tests do.
private func makeEpub(_ files: [String: String]) throws -> URL {
    let dir = tempFolder()
    let root = dir.appending(path: "book")
    var all = files
    all["mimetype"] = "application/epub+zip"
    all["META-INF/container.xml"] = """
    <?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
    """
    for (path, text) in all {
        let url = root.appending(path: path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try text.write(to: url, atomically: true, encoding: .utf8)
    }
    let epub = dir.appending(path: "book.epub")
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
    p.arguments = ["-c", "-k", "--sequesterRsrc", root.path, epub.path]
    try p.run()
    p.waitUntilExit()
    return epub
}

private let prose = "The road ran on past the mill and the river, and the light went long and gold across the fields while the cart rolled on."

// MARK: - Table of contents

@Test func epub3NavGivesChaptersWithFragmentsAndParts() throws {
    let opf = """
    <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="3.0"><metadata><dc:title>Placeholder Book</dc:title></metadata>
    <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="title" href="Text/title.xhtml" media-type="application/xhtml+xml"/>
    <item id="copy" href="Text/copyright.xhtml" media-type="application/xhtml+xml"/>
    <item id="part1" href="Text/part1.xhtml" media-type="application/xhtml+xml"/>
    <item id="body" href="Text/body.xhtml" media-type="application/xhtml+xml"/>
    </manifest>
    <spine><itemref idref="title"/><itemref idref="copy"/><itemref idref="nav" linear="no"/><itemref idref="part1"/><itemref idref="body"/></spine></package>
    """
    let nav = page("""
    <nav epub:type="toc"><h1>Contents</h1><ol>
      <li><a href="Text/copyright.xhtml">Copyright</a></li>
      <li><a href="Text/part1.xhtml">Part One</a><ol>
        <li><a href="Text/body.xhtml#ch1">The Mill</a><ol><li><a href="Text/body.xhtml#ch1-s2">A Section</a></li></ol></li>
        <li><a href="Text/body.xhtml#ch2">The River</a></li>
      </ol></li>
      <li><a href="Text/body.xhtml#ch3">Chapter Three: Home</a></li>
    </ol></nav>
    <nav epub:type="landmarks"><ol><li><a epub:type="copyright-page" href="Text/copyright.xhtml">Copyright</a></li></ol></nav>
    """)
    let body = page("""
    <section id="ch1"><h2>The Mill</h2><p>\(prose)</p><h3 id="ch1-s2">A Section</h3><p>“Still here,” said Tom.</p></section>
    <section id="ch2"><h2>The River</h2><p>\(prose)</p></section>
    <h2 id="ch3">Chapter Three</h2><h3>Home</h3><p>\(prose)</p><h1>Chapter 4</h1><p>\(prose)</p>
    """)
    let epub = try makeEpub([
        "OEBPS/content.opf": opf,
        "OEBPS/nav.xhtml": nav,
        "OEBPS/Text/title.xhtml": page("<h1>Placeholder Book</h1><p>A. Writer</p>"),
        "OEBPS/Text/copyright.xhtml": page("<p>Copyright © 2026 A. Writer. All rights reserved. \(prose)</p>"),
        "OEBPS/Text/part1.xhtml": page("<h1>Part One</h1>"),
        "OEBPS/Text/body.xhtml": body,
    ])
    let book = try ManuscriptImporter.read(epub)
    #expect(book.chapters.map(\.title) == ["Part One · The Mill", "The River", "Chapter Three: Home", "Chapter 4"])
    // The section inside chapter 1 stays in it; heading lines that repeat the title are gone.
    #expect(book.chapters[0].paragraphs.map(\.text) == [prose, "A Section", "“Still here,” said Tom."])
    #expect(book.chapters[2].paragraphs.map(\.text) == [prose])
    // The copyright page and the title page aren't narrated.
    #expect(!book.chapters.flatMap(\.paragraphs).contains { $0.text.contains("All rights reserved") })
    #expect(!book.chapters.contains { $0.title == "Placeholder Book" })
}

@Test func epub2NcxGivesChaptersFromFragments() throws {
    let opf = """
    <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="2.0"><metadata><dc:title>Placeholder Two</dc:title></metadata>
    <manifest><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/><item id="all" href="all.html" media-type="application/xhtml+xml"/></manifest>
    <spine toc="ncx"><itemref idref="all"/></spine></package>
    """
    let ncx = """
    <?xml version="1.0" encoding="UTF-8"?>
    <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap>
    <navPoint id="n1" playOrder="1"><navLabel><text>Prologue</text></navLabel><content src="all.html#pro"/></navPoint>
    <navPoint id="n2" playOrder="2"><navLabel><text>Arrival</text></navLabel><content src="all.html#a1"/></navPoint>
    <navPoint id="n3" playOrder="3"><navLabel><text>Departure</text></navLabel><content src="all.html#a2"/></navPoint>
    </navMap></ncx>
    """
    // No headings at all: only the anchors say where chapters start.
    let all = page("""
    <p class="x" id="pro">\(prose)</p><p>Before dawn.</p>
    <a id="a1"/><p>\(prose)</p>
    <p><a name="a2"/>\(prose)</p><p>Last words.</p>
    """)
    let epub = try makeEpub(["OEBPS/content.opf": opf, "OEBPS/toc.ncx": ncx, "OEBPS/all.html": all])
    let book = try ManuscriptImporter.read(epub)
    #expect(book.chapters.map(\.title) == ["Prologue", "Arrival", "Departure"])
    #expect(book.chapters.map(\.paragraphs.count) == [2, 1, 2])
}

@Test func epubWithoutUsableContentsFallsBackToHeadings() throws {
    let opf = """
    <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="3.0"><metadata><dc:title>T</dc:title></metadata>
    <manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="c" href="c.xhtml" media-type="application/xhtml+xml"/></manifest>
    <spine><itemref idref="c"/></spine></package>
    """
    let nav = page("<nav epub:type=\"toc\"><ol><li><a href=\"c.xhtml\">Start</a></li></ol></nav>")
    let c = page("<h2>Chapter 1</h2><p>\(prose)</p><h2>Chapter 2</h2><p>\(prose)</p>")
    let book = try ManuscriptImporter.read(try makeEpub(["OEBPS/content.opf": opf, "OEBPS/nav.xhtml": nav, "OEBPS/c.xhtml": c]))
    #expect(book.chapters.map(\.title) == ["Chapter 1", "Chapter 2"])
}

// MARK: - Footnotes

@Test func footnotesAndMarkersAreLeftOut() throws {
    let xhtml = page("""
    <h1>Chapter 1</h1>
    <p>The tide came in.<a epub:type="noteref" href="#fn1">1</a> Then it went out.<sup><a href="#n2">2</a></sup></p>
    <p>See <a href="#c3">chapter three</a> and page <a href="#p3">3</a> for the rest.</p>
    <p>A marker<a href="notes.xhtml#ftn4" id="ref4">[4]</a> in brackets, one in <i>italics<a role="doc-noteref" href="#x">*</a> here</i>.</p>
    <aside epub:type="footnote" id="fn1"><p>1. A note about tides.</p></aside>
    <p epub:type="footnote" id="n2">2. A note in a paragraph.</p>
    <div class="footnote"><p>Another note.</p></div>
    <p>After the notes, the story goes on.</p>
    <section epub:type="endnotes"><ol><li epub:type="endnote" id="e1"><p>An endnote.</p></li></ol></section>
    """)
    let blocks = try #require(XHTMLBlocks.parse(Data(xhtml.utf8)))
    let texts = blocks.map(\.text)
    #expect(texts == ["Chapter 1", "The tide came in. Then it went out.",
                      "See chapter three and page 3 for the rest.",
                      "A marker in brackets, one in italics here.",
                      "After the notes, the story goes on."])
    let italic = blocks[3].italic
    #expect(italic.count == 1)
    if let span = italic.first {
        #expect((blocks[3].text as NSString).substring(with: NSRange(location: span.location, length: span.length)) == "italics here")
    }
}

@Test func xhtmlKnowsLatinEntities() {
    let blocks = XHTMLBlocks.parse(Data(page("<p>Caf&eacute; &ntilde; &uuml;ber &sect;2</p>").utf8))
    #expect(blocks?.first?.text == "Café ñ über §2")
}

// MARK: - EPUB with Audio

private func project() -> Project {
    let chapter = Chapter(title: "Chapter 1 & More", paragraphs: [
        Paragraph(text: "“Busy.” The doctor folded her gloves. “Your father wrote to Kerensa.”"),
        Paragraph(text: "She laid an envelope on the table. It was sealed.", italic: [TextSpan(location: 10, length: 4)]),
        Paragraph(text: "* * *", style: .sceneBreak),
    ])
    var p = ProjectBuilder.make(from: ImportedBook(title: "Placeholder <Book>", author: "A. Writer", chapters: [chapter]), fileName: "t.txt")
    p.pronunciations = [Pronunciation(term: "Kerensa", sayItAs: "keh-REN-sa")]
    return p
}

@Test func speechSegmentsKnowWhereTheirWordsAre() {
    let p = project()
    let segs = SpeechScript.segments(chapter: 0, project: p, index: BookIndex(p), includeNarration: true, pauses: PauseSettings())
    for seg in segs where !seg.text.isEmpty {
        let para = p.chapters[0].paragraphs.first { $0.id == seg.paragraphID }!
        let source = try! #require(seg.source)
        let words = (para.text as NSString).substring(with: source)
        // The words match what's said, before pronunciations.
        #expect(SpeechScript.apply(SpeechScript.sayings(p), to: words) == seg.text, "\(words) vs \(seg.text)")
    }
    #expect(segs.contains { $0.text == "It was sealed." })
}

@Test func readAlongPageWrapsEachSentence() throws {
    let p = project()
    let segs = SpeechScript.segments(chapter: 0, project: p, index: BookIndex(p), includeNarration: true, pauses: PauseSettings())
    let page = ReadAlongEpub.page(chapter: p.chapters[0], number: 1, segments: segs, language: "en")
    let spoken = segs.filter { !$0.text.isEmpty }
    #expect(Set(spoken.map(\.id)).isSubset(of: Set(page.fragments.keys)))
    #expect(page.xhtml.contains("<h1>Chapter 1 &amp; More</h1>"))
    #expect(page.xhtml.contains("<span id=\"c1-1\">Busy.</span>"))
    #expect(page.xhtml.contains("an <i>enve</i>") || page.xhtml.contains("<i>"))
    #expect(page.xhtml.contains("<p class=\"break\">* * *</p>"))
    // Well-formed XML.
    #expect(XMLParser(data: Data(page.xhtml.replacingOccurrences(of: "<!DOCTYPE html>", with: "").utf8)).parse())
}

@Test func readAlongEpubPackageAndOverlays() throws {
    let p = project()
    let segs = SpeechScript.segments(chapter: 0, project: p, index: BookIndex(p), includeNarration: true, pauses: PauseSettings())
    let page = ReadAlongEpub.page(chapter: p.chapters[0], number: 1, segments: segs, language: "en")
    // Each segment a second long, back to back.
    var timing: [String: (begin: Double, end: Double)] = [:]
    for (i, seg) in segs.enumerated() { timing[seg.id] = (Double(i), Double(i) + 1) }
    let clips = ReadAlongEpub.clips(fragments: page.fragments, timing: timing, duration: Double(segs.count))
    #expect(clips.count == Set(page.fragments.values).count)
    #expect(clips.map(\.begin) == clips.map(\.begin).sorted())

    let dir = tempFolder()
    let audio = dir.appending(path: "a.m4a")
    try Data(repeating: 7, count: 5000).write(to: audio)
    let book = ReadAlongEpub.Book(title: p.title, author: p.author, language: "en",
                                  identifier: ReadAlongEpub.identifier(title: p.title, author: p.author),
                                  chapters: [
                                      .init(title: p.chapters[0].title, xhtml: page.xhtml, audio: audio, duration: 61.5, clips: clips),
                                      .init(title: "Silent", xhtml: page.xhtml, audio: nil, duration: 0, clips: []),
                                  ])
    let url = dir.appending(path: "book.epub")
    try ReadAlongEpub.write(book, to: url)
    let data = try Data(contentsOf: url)
    // mimetype first, stored, no extra field.
    #expect(data.prefix(4) == Data([0x50, 0x4B, 0x03, 0x04]))
    #expect(data[8] == 0 && data[9] == 0)
    #expect(data[28] == 0 && data[29] == 0)
    #expect(String(data: data[30..<38], encoding: .utf8) == "mimetype")
    #expect(String(data: data[38..<58], encoding: .utf8) == "application/epub+zip")

    let files = try ZipFile.entries(in: data)
    #expect(files["OEBPS/audio/c001.m4a"] == Data(repeating: 7, count: 5000))
    #expect(files["OEBPS/audio/c002.m4a"] == nil)
    let opf = String(decoding: try #require(files["OEBPS/content.opf"]), as: UTF8.self)
    #expect(opf.contains("media-overlay=\"mo-c001\""))
    #expect(!opf.contains("media-overlay=\"mo-c002\""))
    #expect(opf.contains("<meta property=\"media:duration\" refines=\"#mo-c001\">0:01:01.500</meta>"))
    #expect(opf.contains("<meta property=\"media:duration\">0:01:01.500</meta>"))
    #expect(opf.contains("media:active-class"))
    #expect(opf.contains("Placeholder &lt;Book&gt;"))
    #expect(opf.contains("properties=\"nav\""))
    let smil = String(decoding: try #require(files["OEBPS/smil/c001.smil"]), as: UTF8.self)
    #expect(smil.components(separatedBy: "<par ").count - 1 == clips.count)
    #expect(smil.contains("<text src=\"../text/c001.xhtml#c1-1\"/><audio src=\"../audio/c001.m4a\" clipBegin=\"0:00:00.000\""))
    for (path, content) in files where path.hasSuffix(".xhtml") || path.hasSuffix(".opf") || path.hasSuffix(".smil") || path.hasSuffix(".xml") {
        let text = String(decoding: content, as: UTF8.self).replacingOccurrences(of: "<!DOCTYPE html>", with: "")
        #expect(XMLParser(data: Data(text.utf8)).parse(), "\(path) is well-formed")
    }

    // Reading it back: the nav gives the chapters.
    let back = try ManuscriptImporter.read(url)
    #expect(back.chapters.map(\.title) == ["Chapter 1 & More", "Silent"])
    #expect(back.chapters[0].paragraphs.first?.text == p.chapters[0].paragraphs[0].text)
}

@Test func readAlongClockAndIdentifier() {
    #expect(ReadAlongEpub.clock(0) == "0:00:00.000")
    #expect(ReadAlongEpub.clock(3725.0456) == "1:02:05.046")
    #expect(ReadAlongEpub.identifier(title: "A", author: "B") == ReadAlongEpub.identifier(title: "A", author: "B"))
    #expect(ReadAlongEpub.identifier(title: "A", author: "B") != ReadAlongEpub.identifier(title: "A", author: "C"))
}
