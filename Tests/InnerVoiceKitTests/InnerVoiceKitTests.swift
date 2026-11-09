import AppKit
import Foundation
import PDFKit
import Testing
@testable import InnerVoiceKit

// MARK: - Helpers

private func paragraph(_ text: String, italic: [TextSpan] = [], style: Paragraph.Style = .body) -> Paragraph {
    Paragraph(text: text, style: style, italic: italic)
}

private func book(_ paragraphs: [String]) -> ImportedBook {
    ImportedBook(title: "Test", chapters: [Chapter(title: "Chapter 1", paragraphs: paragraphs.map { paragraph($0) })])
}

private func lineTexts(_ p: Project) -> [String] {
    let index = BookIndex(p)
    return p.lines.map { p.text(of: $0, index: index) }
}

private func speaker(of prefix: String, in p: Project) -> String? {
    let index = BookIndex(p)
    let line = p.lines.first { p.text(of: $0, index: index).hasPrefix(prefix) }
    return p.character(line?.speakerID)?.name
}

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory.appending(path: "InnerVoiceTests-\(UUID().uuidString)")
    try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

// MARK: - Chapters

@Test func headingsAreRecognized() {
    for h in ["Chapter 1", "CHAPTER TWELVE", "Chapter Twenty-One", "Prologue", "Part II", "Epilogue: After", "7", "ch. 3"] {
        #expect(ChapterDetector.looksLikeHeading(h), "\(h)")
    }
    for h in ["“Chapter one,” she said.", "The chapter was long and the night was longer than any before it.", "I was seven"] {
        #expect(!ChapterDetector.looksLikeHeading(h), "\(h)")
    }
}

@Test func chaptersSplitAtHeadings() {
    let blocks = [RawBlock(text: "My Book"), RawBlock(text: "Chapter 1"), RawBlock(text: "It began."),
                  RawBlock(text: "Chapter 2"), RawBlock(text: "The Storm", headingLevel: 2), RawBlock(text: "It rained.")]
    let chapters = ChapterDetector.chapters(from: blocks, fallbackTitle: "x")
    #expect(chapters.map(\.title) == ["Chapter 1", "Chapter 2: The Storm"])
    #expect(chapters[1].paragraphs.map(\.text) == ["It rained."])
}

@Test func sceneBreaksAreKept() {
    #expect(ChapterDetector.isSceneBreak("* * *"))
    #expect(ChapterDetector.isSceneBreak("⁂"))
    #expect(!ChapterDetector.isSceneBreak("Yes."))
}

// MARK: - Dialogue

@Test func curlyStraightAndUKQuotes() {
    let curly = DialogueFinder.findAll(in: [Chapter(title: "c", paragraphs: [paragraph("“Hello,” she said. “Come in.”")])])
    #expect(curly.count == 2)
    let straight = DialogueFinder.findAll(in: [Chapter(title: "c", paragraphs: [paragraph("\"Hello there,\" he said.")])])
    #expect(straight.count == 1)
    let uk = (0..<12).map { _ in paragraph("‘Don’t go,’ she said. ‘It’s late.’") }
    let ukLines = DialogueFinder.findAll(in: [Chapter(title: "c", paragraphs: uk)])
    #expect(DialogueFinder.detectStyle(uk) == .single)
    #expect(ukLines.count == 24)
}

@Test func nestedQuotesStayInside() {
    let p = paragraph("“She told me, ‘Run,’ and I ran,” he said.")
    let lines = DialogueFinder(style: .double).find(in: p, openFromPrevious: false).lines
    #expect(lines.count == 1)
    #expect(p.substring(lines[0].range) == "She told me, ‘Run,’ and I ran,")
}

@Test func quotesAcrossParagraphsContinue() {
    let chapters = [Chapter(title: "c", paragraphs: [paragraph("“It was long ago. We were young."), paragraph("“Then the war came.”")])]
    let lines = DialogueFinder.findAll(in: chapters)
    #expect(lines.count == 2)
    #expect(lines[1].continuesPrevious)
}

@Test func dashDialogue() {
    let p = paragraph("—Where are you going? —asked Juan.")
    let lines = DialogueFinder(style: .double).find(in: p, openFromPrevious: false).lines
    #expect(lines.count == 1)
    #expect(p.substring(lines[0].range) == "Where are you going?")
}

@Test func italicThoughtsAndWrittenText() {
    let text = "He looked away. This cannot be happening, he thought."
    let p = paragraph(text, italic: [TextSpan(location: 16, length: 28)])
    let lines = DialogueFinder(style: .double).find(in: p, openFromPrevious: false).lines
    #expect(lines.first?.kind == .thought)
    let letter = paragraph("Dear John, I have gone away.", style: .blockQuote)
    #expect(DialogueFinder(style: .double).find(in: letter, openFromPrevious: false).lines.first?.kind == .written)
}

@Test func shortMentionsAreNotLines() {
    let p = paragraph("He used the word “love” too often.")
    #expect(DialogueFinder(style: .double).find(in: p, openFromPrevious: false).lines.isEmpty)
}

// MARK: - Rules

@Test func speechTagsAssignSpeakers() {
    let p = ProjectBuilder.make(from: book([
        "“You're late,” said Tom Hale.",
        "“The path flooded,” Mara replied.",
        "Then Tom Hale sighed. “Tea?”",
        "“Please,” said Mara.",
        "“Milk?”",
        "“No.”",
    ]), fileName: "t.txt")
    #expect(speaker(of: "You're late", in: p) == "Tom Hale")
    #expect(speaker(of: "The path flooded", in: p) == "Mara")
    #expect(speaker(of: "Tea?", in: p) == "Tom Hale")
    #expect(speaker(of: "Please", in: p) == "Mara")
    // The back-and-forth alternates: Tom, then Mara.
    #expect(speaker(of: "Milk?", in: p) == "Tom Hale")
    #expect(speaker(of: "No.", in: p) == "Mara")
    let index = BookIndex(p)
    #expect(p.lines.first { p.text(of: $0, index: index) == "Milk?" }?.source == .exchange)
}

@Test func firstNameMatchesFullName() {
    var p = Project(title: "t")
    p.characters = [BookCharacter(name: "Mara Quinn", colorHex: "#10B981")]
    #expect(RuleAttribution.resolve("Mara", in: &p, create: true) == p.characters[0].id)
    #expect(RuleAttribution.resolve("Then Mara", in: &p, create: true) == p.characters[0].id)
    #expect(RuleAttribution.resolve("He", in: &p, create: true) == nil)
    #expect(p.characters.count == 1)
}

@Test func exchangeProposalsAlternate() {
    var p = ProjectBuilder.make(from: book([
        "“Ready?” asked Ann.", "“Yes,” said Ben.", "“Now?”", "“Now.”", "“Go!”",
    ]), fileName: "t")
    let index = BookIndex(p)
    let now = p.lines.first { p.text(of: $0, index: index) == "Now?" }!
    let ann = p.characters.first { $0.name == "Ann" }!.id
    let ben = p.characters.first { $0.name == "Ben" }!.id
    // The rules said Ann; the narrator says it's Ben.
    let old = now.speakerID
    let i = p.lines.firstIndex { $0.id == now.id }!
    p.lines[i].speakerID = ben
    p.lines[i].source = .user
    let proposals = Exchange.proposals(in: p, after: now.id, previousSpeaker: old)
    #expect(proposals.count == 2)
    #expect(proposals.first?.speakerID == ann)
}

@Test func paletteNeverRed() {
    #expect(CharacterPalette.swatches.allSatisfy { !CharacterPalette.isTooRed($0.hex) })
    #expect(CharacterPalette.isTooRed("#E11D48") || CharacterPalette.isTooRed("#EF4444"))
    let used = CharacterPalette.swatches.prefix(3).map(\.hex)
    #expect(!used.contains(CharacterPalette.next(used: Array(used))))
}

// MARK: - AI replies

@Test func jsonExtractionToleratesChatter() {
    let reply = "<think>hmm</think>Sure! {\"lines\":[{\"id\":\"L1\",\"speaker\":\"Tom\",\"kind\":\"spoken\",\"confidence\":91}]} Done."
    let parsed = AttributionPrompt.parse(reply)
    #expect(parsed?.first?.speaker == "Tom")
    #expect(parsed?.first?.confidence == 91)
}

@Test func attributionPromptLabelsOnlyOpenLines() {
    let p = ProjectBuilder.make(from: book(["“Hi,” said Tom.", "“Who's there?”", "“Me.”"]), fileName: "t")
    let chunks = AttributionPrompt.chunks(project: p, index: BookIndex(p), chapterIndex: 0)
    #expect(chunks.count == 1)
    let user = chunks[0].messages.last!.content
    #expect(user.contains("{Tom}"))
    #expect(!chunks[0].labels.isEmpty)
    #expect(chunks[0].labels.count == p.lines.filter(AttributionPrompt.needsModel).count)
}

@Test func applyingRepliesNeverTouchesTheNarratorsLines() {
    var p = ProjectBuilder.make(from: book(["“Who's there?”", "“Me.”"]), fileName: "t")
    let chunk = AttributionPrompt.chunks(project: p, index: BookIndex(p), chapterIndex: 0)[0]
    let mine = chunk.labels["L1"]!
    let i = p.lines.firstIndex { $0.id == mine }!
    p.lines[i].source = .user
    p.lines[i].speakerID = nil
    AttributionPrompt.apply([AttributionReply(id: "L1", speaker: "Ghost", kind: "spoken", confidence: 99),
                             AttributionReply(id: "L2", speaker: "Ann", kind: "spoken", confidence: 80)], chunk: chunk, to: &p)
    #expect(p.lines[i].speakerID == nil)
    #expect(p.characters.map(\.name) == ["Ann"])
}

@Test func factsNeedTheirQuote() {
    var p = ProjectBuilder.make(from: book(["Tom was sixty if he was a day, with a Glasgow growl.", "“Aye,” said Tom."]), fileName: "t")
    let tom = p.characters.first { $0.name == "Tom" }!
    let chunk = SummaryPrompt.chunks(for: tom, project: p, index: BookIndex(p))[0]
    let added = SummaryPrompt.merge([
        FactReply(kind: "age", text: "About sixty", paragraph: "P1", quote: "sixty if he was a day"),
        FactReply(kind: "accent", text: "Welsh", paragraph: "P1", quote: "a lilting Welsh voice"),
        FactReply(kind: "age", text: "about sixty", paragraph: "P1", quote: "sixty if he was a day"),
    ], chunk: chunk, characterID: tom.id, into: &p, index: BookIndex(p))
    #expect(added == 1)
    #expect(p.character(tom.id)?.facts.first?.text == "About sixty")
}

// MARK: - Revisions

@Test func revisionKeepsTagsAndFindsChanges() {
    var old = ProjectBuilder.make(from: book(["Intro text here.", "“Hello there,” said Ann.", "“Goodbye now,” said Ben.", "A closing line."]), fileName: "v1")
    let index = BookIndex(old)
    let hello = old.lines.first { old.text(of: $0, index: index).hasPrefix("Hello") }!
    let ben = old.characters.first { $0.name == "Ben" }!.id
    let i = old.lines.firstIndex { $0.id == hello.id }!
    old.lines[i].speakerID = ben // the narrator's correction
    old.lines[i].source = .user
    old.recorded = Set(old.chapters[0].paragraphs.map(\.id))

    let new = book(["A new opening.", "Intro text here.", "“Hello there, friend,” said Ann.", "“Goodbye now,” said Ben."])
    let result = RevisionMerger.merge(old, with: new, fileName: "v2")
    #expect(result.record.added.count == 1)
    #expect(result.record.changed.count == 1)
    #expect(result.record.removed.map(\.text) == ["A closing line."])
    let newIndex = BookIndex(result.project)
    let kept = result.project.lines.first { result.project.text(of: $0, index: newIndex).hasPrefix("Hello there, friend") }
    #expect(kept?.speakerID == ben)
    #expect(kept?.source == .user)
    // Recorded stays on unchanged paragraphs, clears on the edited one.
    #expect(result.project.recorded.contains(old.chapters[0].paragraphs[0].id))
    #expect(!result.project.recorded.contains(old.chapters[0].paragraphs[1].id))
}

// MARK: - Index and models

@Test func mainCharactersStartHiddenInTheIndex() {
    var lines: [String] = []
    for _ in 0..<10 { lines.append("“Yes,” said Ann.") }
    lines.append("“No,” said Ben.")
    let p = ProjectBuilder.make(from: book(lines), fileName: "t")
    let index = BookIndex(p)
    let ann = p.characters.first { $0.name == "Ann" }!
    let ben = p.characters.first { $0.name == "Ben" }!
    #expect(!index.showsInIndex(ann))
    #expect(index.showsInIndex(ben))
}

@Test func modelAdviceFollowsMemory() {
    #expect(ModelCatalog.recommendedID(memoryGB: 8) == "qwen3-4b")
    #expect(ModelCatalog.recommendedID(memoryGB: 16) == "gemma-4-e4b")
    #expect(ModelCatalog.recommendedID(memoryGB: 32) == "qwen3-14b")
    #expect(ModelCatalog.recommendedID(memoryGB: 48) == "gemma-4-26b-a4b")
    let big = ModelCatalog.model(id: "gemma-4-31b")!
    #expect(ModelCatalog.advice(for: big, memoryGB: 16).fit == .tooLarge)
    #expect(ModelCatalog.advice(for: big, memoryGB: 16).note.contains("16 GB"))
    let small = ModelCatalog.model(id: "qwen3-4b")!
    #expect(ModelCatalog.advice(for: small, memoryGB: 64).fit == .good)
}

@Test func duplicatesAreSuggested() {
    var p = Project(title: "t")
    p.characters = [BookCharacter(name: "Mara", colorHex: "#111111"), BookCharacter(name: "Mara Quinn", colorHex: "#222222"),
                    BookCharacter(name: "Tom", colorHex: "#333333")]
    let pairs = Duplicates.pairs(in: p)
    #expect(pairs.count == 1)
    #expect(pairs.first?.merge == p.characters[0].id)
}

// MARK: - Importers (real files)

private let sampleHTML = """
<html><body><h1>Chapter 1</h1><p>The wind rose.</p><p>“Close the door,” said <b>Tom</b>.</p>
<p>She thought: <i>this cannot last much longer</i>.</p><h1>Chapter 2</h1><p>“Morning,” said Mara.</p></body></html>
"""

private func convert(_ html: String, to format: String, in dir: URL) throws -> URL {
    let src = dir.appending(path: "src.html")
    try html.write(to: src, atomically: true, encoding: .utf8)
    let out = dir.appending(path: "book.\(format)")
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/textutil")
    p.arguments = ["-convert", format, src.path, "-output", out.path]
    try p.run()
    p.waitUntilExit()
    return out
}

@Test(arguments: ["docx", "odt", "rtf", "html", "txt"])
func importsEveryFormat(_ format: String) throws {
    let dir = tempDir()
    let url = try convert(sampleHTML, to: format, in: dir)
    let imported = try ManuscriptImporter.read(url)
    let all = imported.chapters.flatMap(\.paragraphs).map(\.text).joined(separator: "\n")
    #expect(all.contains("Close the door"))
    #expect(all.contains("Morning"))
    #expect(imported.chapters.count == 2, "\(format): \(imported.chapters.map(\.title))")
    if format != "txt" {
        let thought = imported.chapters[0].paragraphs.first { $0.text.contains("cannot last") }
        #expect(thought?.italic.isEmpty == false, "\(format) lost italics")
    }
}

@Test func importsMarkdown() throws {
    let dir = tempDir()
    let url = dir.appending(path: "book.md")
    try "# Chapter 1\n\nThe wind rose.\n\n“Close the door,” said Tom.\n\n*This cannot last much longer,* she thought.\n\n# Chapter 2\n\n“Morning.”\n".write(to: url, atomically: true, encoding: .utf8)
    let imported = try ManuscriptImporter.read(url)
    #expect(imported.chapters.count == 2)
    #expect(imported.chapters[0].paragraphs[2].italic.count == 1)
    #expect(!imported.chapters[0].paragraphs[2].text.contains("*"))
}

@Test func importsEPUB() throws {
    let dir = tempDir()
    let root = dir.appending(path: "epub")
    try FileManager.default.createDirectory(at: root.appending(path: "META-INF"), withIntermediateDirectories: true)
    try FileManager.default.createDirectory(at: root.appending(path: "OEBPS"), withIntermediateDirectories: true)
    try "application/epub+zip".write(to: root.appending(path: "mimetype"), atomically: true, encoding: .utf8)
    try """
    <?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
    """.write(to: root.appending(path: "META-INF/container.xml"), atomically: true, encoding: .utf8)
    try """
    <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="3.0"><metadata><dc:title>The Test Book</dc:title><dc:creator>A. Writer</dc:creator></metadata><manifest><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/><item id="c2" href="c2.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/><itemref idref="c2"/></spine></package>
    """.write(to: root.appending(path: "OEBPS/content.opf"), atomically: true, encoding: .utf8)
    for (n, body) in [(1, "<h2>Chapter 1</h2><p>The wind rose.</p><p>&ldquo;Close the door,&rdquo; said Tom.</p><p><em>It cannot last long now,</em> she thought.</p>"),
                      (2, "<h2>Chapter 2</h2><p>&ldquo;Morning,&rdquo; said Mara.</p>")] {
        try "<?xml version=\"1.0\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><body>\(body)</body></html>"
            .write(to: root.appending(path: "OEBPS/c\(n).xhtml"), atomically: true, encoding: .utf8)
    }
    let epub = dir.appending(path: "book.epub")
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
    p.arguments = ["-c", "-k", "--sequesterRsrc", root.path, epub.path]
    try p.run()
    p.waitUntilExit()
    let imported = try ManuscriptImporter.read(epub)
    #expect(imported.title == "The Test Book")
    #expect(imported.author == "A. Writer")
    #expect(imported.chapters.map(\.title) == ["Chapter 1", "Chapter 2"])
    #expect(imported.chapters[0].paragraphs.contains { !$0.italic.isEmpty })
}

@Test func importsPDFAndDropsPageNumbers() throws {
    let dir = tempDir()
    let url = dir.appending(path: "book.pdf")
    var box = CGRect(x: 0, y: 0, width: 612, height: 792)
    let ctx = CGContext(url as CFURL, mediaBox: &box, nil)!
    let font = NSFont(name: "Times-Roman", size: 12)!
    let paragraphs = (1...6).map { "“Paragraph \($0) begins here and keeps going for a while so that it wraps onto a second line of text in the page,” said Tom to Mara on page one." }
    for page in 1...3 {
        ctx.beginPDFPage(nil)
        var y: CGFloat = 720
        for (i, text) in paragraphs.enumerated() {
            let framesetter = CTFramesetterCreateWithAttributedString(NSAttributedString(string: "    " + text + " (\(page).\(i))", attributes: [.font: font]))
            let path = CGPath(rect: CGRect(x: 72, y: y - 60, width: 468, height: 60), transform: nil)
            CTFrameDraw(CTFramesetterCreateFrame(framesetter, CFRange(location: 0, length: 0), path, nil), ctx)
            y -= 70
        }
        // A page number at the bottom.
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: "\(page)", attributes: [.font: font]))
        ctx.textPosition = CGPoint(x: 300, y: 36)
        CTLineDraw(line, ctx)
        ctx.endPDFPage()
    }
    ctx.closePDF()
    let blocks = try PDFReader.read(url)
    #expect(!blocks.contains { $0.text.trimmingCharacters(in: .whitespaces) == "2" })
    #expect(blocks.count >= 15, "\(blocks.count) blocks")
    #expect(blocks.contains { $0.text.contains("wraps onto a second line") && $0.text.contains("on page one") })
}

@Test func scannedPDFSaysSo() throws {
    let dir = tempDir()
    let url = dir.appending(path: "scan.pdf")
    var box = CGRect(x: 0, y: 0, width: 612, height: 792)
    let ctx = CGContext(url as CFURL, mediaBox: &box, nil)!
    ctx.beginPDFPage(nil)
    ctx.setFillColor(gray: 0.5, alpha: 1)
    ctx.fill(CGRect(x: 72, y: 72, width: 200, height: 200))
    ctx.endPDFPage()
    ctx.closePDF()
    #expect(throws: ImportError.scannedPDF("scan.pdf")) { try ManuscriptImporter.read(url) }
}

@Test func unsupportedFilesSayWhatToDo() {
    let url = URL(fileURLWithPath: "/tmp/book.xyz")
    #expect(throws: ImportError.unsupported("book.xyz")) { try ManuscriptImporter.read(url) }
}

@Test func pronunciationCandidates() {
    let p = ProjectBuilder.make(from: book(["They walked to Penhallow with Siobhan.", "At Penhallow the gwyllion waited.", "The gwyllion howled."]), fileName: "t")
    let found = PronunciationFinder.candidates(in: p, isKnownWord: { ["walked", "they", "with", "waited", "the", "howled"].contains($0) })
    let terms = Set(found.map(\.term))
    #expect(terms.contains("Penhallow"))
    #expect(terms.contains("Siobhan"))
    #expect(terms.contains("gwyllion"))
    #expect(!terms.contains("They"))
}

// MARK: - Project file

private func ditto(_ args: [String]) throws {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
    p.arguments = args
    try p.run()
    p.waitUntilExit()
    #expect(p.terminationStatus == 0)
}

private func sampleContents() -> ProjectArchive.Contents {
    let json = Data(String(repeating: "{\"line\":\"“You’re late,” said Tom Hale.\"},", count: 2000).utf8)
    let pdf = Data((0..<50_000).map { UInt8(truncatingIfNeeded: $0 &* 2654435761 >> 13) })
    return .init(projectJSON: json, original: .init(name: "Book 1 – draft.pdf", data: pdf),
                 audio: ["a.m4a": Data([1, 2, 3]), "Penhallow.m4a": Data()])
}

@Test func projectArchiveRoundTrips() throws {
    let contents = sampleContents()
    let data = try ProjectArchive.write(contents)
    #expect(ProjectArchive.looksLikeArchive(data))
    #expect(data.count < contents.projectJSON.count + 60_000)   // the JSON is compressed
    #expect(try ProjectArchive.read(data) == contents)
}

@Test func projectArchiveOpensInOtherZipTools() throws {
    let dir = FileManager.default.temporaryDirectory.appending(path: "iv-archive-\(UUID().uuidString)")
    defer { try? FileManager.default.removeItem(at: dir) }
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    let contents = sampleContents()
    // Ours, unpacked by ditto.
    let file = dir.appending(path: "Book.innervoice")
    try ProjectArchive.write(contents).write(to: file)
    try ditto(["-x", "-k", file.path, dir.appending(path: "out").path])
    #expect(try Data(contentsOf: dir.appending(path: "out/project.json")) == contents.projectJSON)
    #expect(try Data(contentsOf: dir.appending(path: "out/Original/Book 1 – draft.pdf")) == contents.original?.data)
    #expect(try Data(contentsOf: dir.appending(path: "out/Audio/a.m4a")) == Data([1, 2, 3]))
    // A zip made by ditto from a project folder, read by us.
    let zipped = dir.appending(path: "byditto.zip")
    try ditto(["-c", "-k", "--sequesterRsrc", dir.appending(path: "out").path, zipped.path])
    #expect(try ProjectArchive.read(Data(contentsOf: zipped)) == contents)
}

@Test func projectArchiveRejectsDamagedFiles() throws {
    #expect(throws: ProjectArchive.ArchiveError.unreadable) { try ProjectArchive.read(Data("not a zip".utf8)) }
    var data = try ProjectArchive.write(sampleContents())
    data[60] ^= 0xFF
    #expect(throws: ProjectArchive.ArchiveError.unreadable) { try ProjectArchive.read(data) }
    let noProject = try ZipFile.make([("notes.txt", Data("hi".utf8))])
    #expect(throws: ProjectArchive.ArchiveError.missingProject) { try ProjectArchive.read(noProject) }
    #expect(ProjectArchive.safeName("../../etc/passwd") == "passwd")
}
