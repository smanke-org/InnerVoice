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

// MARK: - Margin names

@Test func characterFirstNamesSkipTitles() {
    func first(_ n: String) -> String { BookCharacter(name: n, colorHex: "#0EA5E9").firstName }
    #expect(first("Mara Quinn") == "Mara")
    #expect(first("Dr. Edda Voss") == "Edda")
    #expect(first("Captain") == "Captain")
    #expect(first("Mr. Hale") == "Mr. Hale")
    #expect(first("Mister Waterford") == "Mister Waterford")
    #expect(first("Grey-hair") == "Grey-hair")
    #expect(BookCharacter(name: "Mister Waterford", colorHex: "#0EA5E9").initials == "W")
}

// MARK: - Speaker order and pins

@Test func speakerOrderPutsPinsFirstByFirstName() {
    var p = Project(title: "T")
    p.characters = [
        BookCharacter(name: "Zed Arlo", colorHex: "#0EA5E9"),
        BookCharacter(name: "Dr. Edda Voss", colorHex: "#0EA5E9", pinned: true),
        BookCharacter(name: "Mara Quinn", colorHex: "#0EA5E9"),
        BookCharacter(name: "Bram Toll", colorHex: "#0EA5E9", pinned: true),
        BookCharacter(name: "Ada Wren", colorHex: "#0EA5E9"),
    ]
    let order = p.speakerOrder
    #expect(order.pinned.map(\.name) == ["Bram Toll", "Dr. Edda Voss"])
    #expect(order.others.map(\.name) == ["Ada Wren", "Mara Quinn", "Zed Arlo"])
    let edda = p.characters[1]
    #expect(edda.matches("edd"))
    #expect(edda.matches("VOSS"))
    #expect(edda.matches("ev"))
    #expect(!edda.matches("mara"))
}

@Test func oldCharactersDecodeUnpinned() throws {
    let json = ##"{"id":"6F1E3C2A-0000-4000-8000-000000000001","name":"Ada","aliases":[],"colorHex":"#0EA5E9","facts":[],"notes":"","voiceNotes":""}"##
    let c = try JSONDecoder().decode(BookCharacter.self, from: Data(json.utf8))
    #expect(!c.isPinned)
}

// MARK: - Deleting text

private func deletionProject() -> (Project, UUID, UUID, UUID) {
    let keep = Paragraph(text: "“Hello,” said Ada. ISBN 978-0-00-000000-0 “Bye.”", italic: [TextSpan(location: 19, length: 4)])
    let gone = Paragraph(text: "Also by this author: Another Book.")
    let next = Paragraph(text: "“Next,” said Bram.")
    var p = Project(title: "T", chapters: [
        Chapter(title: "One", paragraphs: [keep, gone, next]),
        Chapter(title: "Ads", paragraphs: [Paragraph(text: "Read the next book!")]),
    ])
    let ada = BookCharacter(name: "Ada", colorHex: "#0EA5E9")
    p.characters = [ada]
    p.lines = [
        Line(paragraphID: keep.id, range: TextSpan(location: 0, length: 8), speakerID: ada.id),
        Line(paragraphID: keep.id, range: TextSpan((keep.text as NSString).range(of: "“Bye.”")), speakerID: ada.id),
        Line(paragraphID: next.id, range: TextSpan(location: 0, length: 7)),
    ]
    p.recorded = [gone.id]
    p.retakes = [Retake(paragraphID: gone.id, note: "x")]
    return (p, keep.id, gone.id, next.id)
}

@Test func deletingTextMovesLaterLines() {
    var (p, keep, _, _) = deletionProject()
    let text = p.chapters[0].paragraphs[0].text as NSString
    let isbn = text.range(of: "ISBN 978-0-00-000000-0")
    p.deleteText(cuts: [keep: [TextSpan(isbn)]])
    let after = p.chapters[0].paragraphs[0]
    #expect(after.text == "“Hello,” said Ada. “Bye.”")
    #expect(after.substring(p.lines[0].range) == "“Hello,”")
    #expect(after.substring(p.lines[1].range) == "“Bye.”")
    #expect(after.italic.isEmpty)   // the italic run was the word ISBN
}

@Test func deletingTextTrimsAndDropsLines() {
    var (p, keep, _, _) = deletionProject()
    // Cut from inside the first line to just before the second: the first is trimmed.
    p.deleteText(cuts: [keep: [TextSpan(location: 7, length: 12)]])
    let after = p.chapters[0].paragraphs[0]
    #expect(after.substring(p.lines[0].range) == "“Hello,")
    #expect(after.text.hasPrefix("“Hello,ISBN"))
    #expect(p.lines.count == 3)
    // A cut that swallows a whole line drops it.
    let bye = (after.text as NSString).range(of: "“Bye.”")
    p.deleteText(cuts: [keep: [TextSpan(bye)]])
    #expect(p.lines.count == 2)
}

@Test func deletingWholeParagraphsAndChapters() {
    var (p, _, gone, next) = deletionProject()
    let ads = p.chapters[1].id
    p.deleteText(cuts: [gone: [TextSpan(location: 0, length: 999)]], chapters: [ads])
    #expect(p.chapters.count == 1)
    #expect(p.chapters[0].paragraphs.count == 2)
    #expect(p.recorded.isEmpty)
    #expect(p.retakes.isEmpty)
    #expect(p.lines.last?.paragraphID == next)
}

// MARK: - Unsure guesses

@Test func pronounTagGuessesTheSentenceSubject() {
    let p = ProjectBuilder.make(from: book([
        "“Sit,” said Cyrus Vane.",
        "“Thank you,” said Barnabe.",
        "Cyrus flipped to the second and third pages of the book and passed the volume to Barnabe. “You can,” he said, “if you can explain why some of the words have changed.”",
        "Barnabe looked up at Cyrus and frowned. “Changed?” he asked.",
    ]), fileName: "t.txt")
    let index = BookIndex(p)
    let you = p.lines.first { p.text(of: $0, index: index).hasPrefix("You can") }
    #expect(p.character(you?.speakerID)?.name == "Cyrus Vane")
    #expect(you.map { p.needsYou($0) } == true)   // a guess: still for the narrator to check
    let rest = p.lines.first { p.text(of: $0, index: index).hasPrefix("if you can") }
    #expect(p.character(rest?.speakerID)?.name == "Cyrus Vane")
    let changed = p.lines.first { p.text(of: $0, index: index).hasPrefix("Changed") }
    #expect(p.character(changed?.speakerID)?.name == "Barnabe")
}

@Test func cutOffRepliesKeepTheirFinishedAnswers() {
    let cut = "{\"lines\": [\n  {\n    \"id\": \"L1\",\n    \"speaker\": \"Mara Quinn\",\n    \"kind\": \"spoken\",\n    \"confidence\": 100\n  },\n  {\n    \"id\": \"L2\",\n    \"speaker\": \"Tom"
    let replies = AttributionPrompt.parse(cut)
    #expect(replies?.count == 1)
    #expect(replies?.first?.speaker == "Mara Quinn")
    #expect(AttributionPrompt.parse("{\"lines\":[{\"id\":\"L1\",\"speaker\":\"A\",\"kind\":\"spoken\",\"confidence\":9}]}")?.count == 1)
    #expect(AttributionPrompt.grammar.contains("ws ::= [ ]?"))
}

// MARK: Voices and tones

@Test func speechTagsGiveTones() {
    let p = ProjectBuilder.make(from: book([
        "“Get out,” Tom snapped.",
        "“I can’t,” Mara whispered.",
        "“We found it!” said Pip.",
        "“Fine,” said Tom.",
        "Edda wiped her eyes. “He was my brother.”",
    ]), fileName: "t.txt")
    let index = BookIndex(p)
    func tone(_ prefix: String) -> Line? { p.lines.first { p.text(of: $0, index: index).hasPrefix(prefix) } }
    #expect(tone("Get out")?.tone == .angry)
    #expect(tone("Get out")?.toneConfidence == ToneRules.sureCue)
    #expect(tone("I can")?.tone == .whispered)
    // An exclamation mark alone is a guess for the AI; it shows as unsure.
    #expect(tone("We found")?.tone == .excited)
    #expect(tone("We found").map { p.toneNeedsYou($0) } == true)
    #expect(tone("Fine")?.tone == .neutral)
    #expect(tone("Fine").map { p.toneNeedsYou($0) } == false)
    #expect(tone("He was")?.tone == .sad)
}

@Test func speechTagsGiveSounds() {
    let p = ProjectBuilder.make(from: book([
        "“Not again,” Tom sighed.",
        "Pip laughed. “You fell in!”",
        "“Look out!” Mara gasped, and then said nothing.",
        "“Fine,” said Tom.",
    ]), fileName: "t.txt")
    let index = BookIndex(p)
    let segs = SpeechScript.segments(chapter: 0, project: p, index: index, includeNarration: false)
    func sound(_ prefix: String) -> VocalSound? { segs.first { $0.text.hasPrefix(prefix) }?.sound }
    #expect(sound("Not again") == .sigh)
    #expect(sound("You fell") == .laugh)
    #expect(sound("Look out") == .gasp)
    #expect(sound("Fine") == nil)
}

@Test func tonePromptAgreementAndNarratorTones() {
    var p = ProjectBuilder.make(from: book(["“We found it!” said Pip.", "“Stop,” said Tom."]), fileName: "t.txt")
    let index = BookIndex(p)
    let chunks = TonePrompt.chunks(project: p, index: index, chapterIndex: 0)
    #expect(chunks.count == 1)
    #expect(chunks[0].labels.count == 2)          // the guess and the plain line; nothing sure
    let found = p.lines[0].id
    let label = chunks[0].labels.first { $0.value == found }!.key
    TonePrompt.apply([TonePrompt.Reply(id: label, tone: "excited", confidence: 50)], chunk: chunks[0], to: &p)
    #expect(p.lines[0].toneSource == .ai)
    #expect((p.lines[0].toneConfidence ?? 0) > 0.6)   // the rule and the AI agree
    // The narrator's tone is never asked about or changed.
    p.lines[1].tone = .tender
    p.lines[1].toneSource = .user
    #expect(!TonePrompt.needsModel(p.lines[1]))
    ToneRules.apply(to: &p)
    #expect(p.lines[1].tone == .tender)
    #expect(TonePrompt.parse("{\"lines\":[{\"id\":\"T1\",\"tone\":\"sad\",\"confidence\":70}]}")?.first?.tone == "sad")
    // The grammar needs every line answered, by label.
    #expect(chunks[0].grammar.contains(#"\"T1\""#) && chunks[0].grammar.contains(#"\"T2\""#))
    #expect(chunks[0].grammar.hasPrefix("root ::="))
    // A reply that copies the speaker into the id still lands on its line.
    var q = ProjectBuilder.make(from: book(["“Stop,” said Tom."]), fileName: "t.txt")
    let qc = TonePrompt.chunks(project: q, index: BookIndex(q), chapterIndex: 0)
    TonePrompt.apply([TonePrompt.Reply(id: "T1: Tom", tone: "angry", confidence: 80)], chunk: qc[0], to: &q)
    #expect(q.lines[0].tone == .angry)
}

@Test func oldLinesDecodeWithoutTones() throws {
    let json = ##"{"id":"6A4F1C2E-1111-4222-8333-944455556666","paragraphID":"6A4F1C2E-1111-4222-8333-944455556667","range":{"location":0,"length":4},"kind":"spoken","confidence":0.9,"source":"rule","continuesPrevious":false}"##
    let line = try JSONDecoder().decode(Line.self, from: Data(json.utf8))
    #expect(line.tone == nil)
    #expect(line.toneSource == nil)
}

private func castingProject() -> Project {
    var p = ProjectBuilder.make(from: book([
        "“Morning,” said Edda Voss.",
        "“Morning,” said Mara Quinn.",
        "“Tea?” said Tom Hale.",
        "“Please,” Mara said.",
        "“Two sugars,” said Edda.",
    ]), fileName: "t.txt")
    for i in p.characters.indices {
        switch p.characters[i].name {
        case "Edda Voss":
            p.characters[i].facts = [Fact(kind: .gender, text: "Woman"), Fact(kind: .age, text: "About 60"),
                                     Fact(kind: .accent, text: "Soft Scottish accent")]
        case "Mara Quinn":
            p.characters[i].facts = [Fact(kind: .relationships, text: "Daughter of the late keeper")]
        default: break
        }
    }
    return p
}

private let appleVoices = [
    VoiceInfo(engine: .apple, voiceID: "f-us", name: "Ava", gender: .female, accent: "American", quality: 2),
    VoiceInfo(engine: .apple, voiceID: "f-sc", name: "Fiona", gender: .female, accent: "Scottish", quality: 1),
    VoiceInfo(engine: .apple, voiceID: "m-gb", name: "Daniel", gender: .male, accent: "British", quality: 1),
    VoiceInfo(engine: .apple, voiceID: "m-us", name: "Evan", gender: .male, accent: "American", quality: 2),
    VoiceInfo(engine: .apple, voiceID: "n-us", name: "Sky", gender: .neutral, accent: "American", quality: 2),
]

@Test func castingReadsTheSummary() {
    var p = castingProject()
    let index = BookIndex(p)
    let edda = p.characters.first { $0.name == "Edda Voss" }!
    let profile = VoiceCasting.profile(edda, project: p, index: index)
    #expect(profile.gender == .female)
    #expect(profile.age == .older)
    #expect(profile.accent == "Scottish")
    let mara = p.characters.first { $0.name == "Mara Quinn" }!
    #expect(VoiceCasting.profile(mara, project: p, index: index).gender == .female)
    let tom = p.characters.first { $0.name == "Tom Hale" }!
    #expect(VoiceCasting.profile(tom, project: p, index: index).gender == nil)   // nothing says

    VoiceCasting.cast(&p, index: index, engine: .apple, voices: appleVoices)
    let cast = { (name: String) in p.characters.first { $0.name == name }!.voice(for: .apple)! }
    #expect(cast("Edda Voss").choice.voiceID == "f-sc")
    #expect(cast("Edda Voss").choice.pitch ?? 1 < 1)        // older: a little lower
    // Two women in the same scene get different voices.
    #expect(cast("Mara Quinn").choice.voiceID == "f-us")
    #expect(!p.voiceNeedsYou(p.characters.first { $0.name == "Edda Voss" }!))
    // Tom: no gender known, so casting is unsure and asks.
    #expect(p.voiceNeedsYou(p.characters.first { $0.name == "Tom Hale" }!))
    #expect(p.settings.narratorVoice(for: .apple) != nil)
}

@Test func castingKeepsTheNarratorsChoice() {
    var p = castingProject()
    let index = BookIndex(p)
    let i = p.characters.firstIndex { $0.name == "Edda Voss" }!
    p.characters[i].setVoice(CastVoice(choice: VoiceChoice(engine: .apple, voiceID: "f-us"), confidence: 1, source: .user), for: .apple)
    VoiceCasting.cast(&p, index: index, engine: .apple, voices: appleVoices)
    #expect(p.characters[i].voice(for: .apple)?.choice.voiceID == "f-us")
    // Mara can't have Edda's voice in the same scene, so she gets another.
    #expect(p.characters.first { $0.name == "Mara Quinn" }!.voice(for: .apple)?.choice.voiceID != "f-us")
    // Each engine keeps its own casting.
    VoiceCasting.cast(&p, index: index, engine: .orpheus, voices: VoiceInfo.orpheus)
    #expect(p.characters[i].voice(for: .apple)?.choice.voiceID == "f-us")
    #expect(VoiceInfo.orpheus.first { $0.voiceID == p.characters[i].voice(for: .orpheus)?.choice.voiceID }?.gender == .female)
}

@Test func qwenVoicesAreDesignedFromTheSummary() {
    var p = castingProject()
    VoiceCasting.cast(&p, index: BookIndex(p), engine: .qwen, voices: [])
    let design = p.characters.first { $0.name == "Edda Voss" }!.voice(for: .qwen)?.choice.design ?? ""
    #expect(design.contains("older woman"))
    #expect(design.contains("Scottish"))
    #expect(p.settings.narratorVoice(for: .qwen)?.choice.design != nil)
}

@Test func speechScriptSplitsNarrationAndAppliesPronunciations() {
    var p = ProjectBuilder.make(from: book([
        "“Busy.” The doctor folded her gloves. “Your father wrote to Kerensa.”",
        "She laid an envelope on the table.",
    ]), fileName: "t.txt")
    p.pronunciations = [Pronunciation(term: "Kerensa", sayItAs: "keh-REN-sa")]
    let index = BookIndex(p)
    let all = SpeechScript.segments(chapter: 0, project: p, index: index, includeNarration: true)
    #expect(all.map(\.text) == ["Chapter 1", "Busy.", "The doctor folded her gloves.", "Your father wrote to keh-REN-sa.", "She laid an envelope on the table."]
            || all.map(\.text) == ["Busy.", "The doctor folded her gloves.", "Your father wrote to keh-REN-sa.", "She laid an envelope on the table."])
    #expect(all.filter { $0.lineID != nil }.count == 2)
    let dialogue = SpeechScript.segments(chapter: 0, project: p, index: index, includeNarration: false)
    #expect(dialogue.map(\.text) == ["Busy.", "Your father wrote to keh-REN-sa."])
    #expect(dialogue.last?.pauseAfter == SpeechScript.chapterPause)
}


@Test func qwenGivesThinCharactersDistinctPresets() {
    var p = ProjectBuilder.make(from: book([
        "“Where is it?” asked Ann.", "“Gone,” said Bea.", "“Look again,” said Ann.", "“I did,” said Bea.",
    ]), fileName: "t.txt")
    let index = BookIndex(p)
    VoiceCasting.cast(&p, index: index, engine: .qwen, voices: VoiceInfo.qwenPresets)
    let speaking = p.characters.filter { (index.stats[$0.id]?.lines ?? 0) > 0 }
    let ids = speaking.compactMap { $0.voice(for: .qwen)?.choice.voiceID }
    #expect(ids.count == speaking.count)
    // The book says nothing about how they sound: ready-made voices, and not the same one.
    #expect(ids.allSatisfy { $0.hasPrefix("preset:") })
    #expect(Set(ids).count == ids.count)
    #expect(speaking.allSatisfy { $0.voice(for: .qwen)?.choice.design?.isEmpty == false })
}

@Test func mixedCastingUsesRealEngines() {
    var p = ProjectBuilder.make(from: book(["“Where is it?” asked Ann.", "“Gone,” said Bea.", "“Hush,” said Cal."]), fileName: "t.txt")
    let index = BookIndex(p)
    VoiceCasting.cast(&p, index: index, engine: .mixed, voices: VoiceInfo.qwenPresets + VoiceInfo.orpheus)
    let choices = p.characters.compactMap { $0.voice(for: .mixed)?.choice }
    #expect(!choices.isEmpty)
    #expect(choices.allSatisfy { $0.engine == .qwen || $0.engine == .orpheus })
    #expect(p.settings.narratorVoice(for: .mixed).map { $0.choice.engine != .mixed } == true)
}


@Test func activityLogAddsUpAndCombinesMacs() {
    var a = ActivityLog(), b = ActivityLog()
    let cal = Calendar(identifier: .gregorian)
    let day1 = cal.date(from: DateComponents(year: 2026, month: 11, day: 1))!
    let day2 = cal.date(from: DateComponents(year: 2026, month: 11, day: 2))!
    a.record(ActivityKey.linesAI, count: 10, at: day1, calendar: cal)
    a.record(ActivityKey.aiKept("M"), count: 9, at: day1, calendar: cal)
    a.record(ActivityKey.aiChanged("M"), count: 1, at: day2, calendar: cal)
    b.record(ActivityKey.linesAI, count: 5, at: day2, calendar: cal)
    let both = ActivityLog.combined([a, b])
    #expect(both.lifetime[ActivityKey.linesAI] == 15)
    #expect(both.firstDay == "2026-11-01")
    #expect(both.totals(since: day2, calendar: cal)[ActivityKey.linesAI] == 5)
    let m = ModelAccuracy.all(in: both.lifetime)
    #expect(m.count == 1 && m[0].reviewed == 10 && m[0].rate == 0.9)
    var old = a
    old.trim(keepDays: 1, now: cal.date(from: DateComponents(year: 2026, month: 12, day: 1))!, calendar: cal)
    #expect(old.days.isEmpty && old.lifetime[ActivityKey.linesAI] == 10)
}


@Test func sentencesGetTheirOwnPauses() {
    let p = ProjectBuilder.make(from: book([
        "The tide was in. The boat was gone. “Stay here. Don’t move,” said Tom. Nobody answered.",
    ]), fileName: "t.txt")
    let index = BookIndex(p)
    var settings = PauseSettings.standard
    let split = SpeechScript.segments(chapter: 0, project: p, index: index, includeNarration: true, pauses: settings)
    #expect(split.map(\.text).prefix(4) == ["The tide was in.", "The boat was gone.", "Stay here.", "Don’t move"])
    #expect(split[0].pause == .sentence && split[1].pause == .line)   // narration, then the line starts
    #expect(split[2].pause == .sentence && split[2].lineID != nil && split[3].lineID == split[2].lineID)
    #expect(split.last?.pause == .chapter)
    settings.splitSentences = false
    let whole = SpeechScript.segments(chapter: 0, project: p, index: index, includeNarration: true, pauses: settings)
    #expect(whole.count < split.count && whole.first?.text == "The tide was in. The boat was gone.")
    #expect(PauseSettings.tight.paragraph < PauseSettings.standard.paragraph && PauseSettings.standard.paragraph < PauseSettings.relaxed.paragraph)
}


@Test func buildSummaryWritesVoiceNotesWithoutTouchingTheNarrators() {
    var c = BookCharacter(name: "Tom Hale", colorHex: "#336699", facts: [
        Fact(kind: .age, text: "about sixty"), Fact(kind: .accent, text: "soft Highland burr"),
        Fact(kind: .voice, text: "low and slow, chest voice"), Fact(kind: .personality, text: "gruff"),
        Fact(kind: .personality, text: "kind to children"), Fact(kind: .personality, text: "stubborn"),
    ], voiceNotes: "Keep him warm under the gruffness.")
    #expect(VoiceNotesFromBook.merge(into: &c))
    #expect(c.voiceNotes.hasPrefix("Keep him warm under the gruffness.\n\nFrom the book:"))
    #expect(c.voiceNotes.contains("Accent: Soft Highland burr") && c.voiceNotes.contains("Manner: Gruff; kind to children"))
    #expect(!c.voiceNotes.contains("stubborn"))
    // A new fact updates the block InnerVoice wrote.
    c.facts.append(Fact(kind: .voice, text: "rasps when angry"))
    #expect(VoiceNotesFromBook.merge(into: &c))
    #expect(c.voiceNotes.contains("Low and slow, chest voice; rasps when angry") && c.voiceNotes.components(separatedBy: "From the book:").count == 2)
    // Once the narrator edits the block, it's theirs.
    c.voiceNotes = c.voiceNotes.replacingOccurrences(of: "rasps when angry", with: "rasps, then whispers")
    c.facts.append(Fact(kind: .accent, text: "Inverness"))
    #expect(!VoiceNotesFromBook.merge(into: &c))
    #expect(c.voiceNotes.contains("rasps, then whispers") && !c.voiceNotes.contains("Inverness"))
    // Junk the model sometimes leaves is not written.
    var junk = BookCharacter(name: "Pip", colorHex: "#000000", facts: [Fact(kind: .gender, text: "Male; //"), Fact(kind: .gender, text: "//"),
                                                                      Fact(kind: .voice, text: "Breathless")])
    #expect(VoiceNotesFromBook.merge(into: &junk) && junk.voiceNotes.contains("Gender: Male\n") && !junk.voiceNotes.contains("//"))
    // Nothing about the voice: no block.
    var quiet = BookCharacter(name: "Ann", colorHex: "#000000", facts: [Fact(kind: .age, text: "nine")])
    #expect(!VoiceNotesFromBook.merge(into: &quiet) && quiet.voiceNotes.isEmpty)
}

@Test func castingReadsGenderAndAgeFromVoiceNotes() {
    let p = ProjectBuilder.make(from: book(["“Who’s there?” asked Robin."]), fileName: "t.txt")
    var c = p.characters.first { $0.name == "Robin" }!
    c.voiceNotes = "An elderly woman, papery and quick."
    let profile = VoiceCasting.profile(c, project: p, index: BookIndex(p))
    #expect(profile.gender == .female && profile.age == .older)
    #expect(profile.evidence.contains("Gender: your voice notes"))
}


@Test func voiceBriefsGoInTheNotesAndShapeCasting() {
    let reply = #"{"pitch":"Low, chest voice","pace":"Slow and deliberate","texture":"Gravelly","attitude":"Gruff but warm","contrast":"Lower and slower than Mara"}"#
    let brief = VoiceBriefPrompt.parse(reply)
    #expect(brief?.pitch == "Low, chest voice")
    var p = ProjectBuilder.make(from: book(["“Who’s there?” asked Tom.", "“Only me,” said Mara."]), fileName: "t.txt")
    let i = p.characters.firstIndex { $0.name == "Tom" }!
    p.characters[i].voiceNotes = "Warm underneath."
    #expect(brief!.merge(into: &p.characters[i]))
    #expect(p.characters[i].voiceNotes.hasPrefix("Warm underneath.\n\nVoice brief:\nPitch: Low, chest voice"))
    #expect(GeneratedNotes.narratorsOwn(p.characters[i]) == "Warm underneath.")
    let index = BookIndex(p)
    let profile = VoiceCasting.profile(p.characters[i], project: p, index: index)
    #expect(profile.pitchLean == -1 && profile.paceLean == -1)
    #expect(VoiceCasting.describe(profile, name: "Tom").contains("low-pitched") && VoiceCasting.describe(profile, name: "Tom").contains("speaking slowly"))
    // The prompt names the scene partner and asks for the contrast.
    let msgs = VoiceBriefPrompt.messages(for: p.characters[i], project: p, index: index)
    #expect(msgs.last?.content.contains("- Mara") == true && msgs.last?.content.contains("Warm underneath.") == true)
    #expect(VoiceBriefPrompt.parse("no json here") == nil)
}

@Test func voiceNotesBecomeAVoiceDescription() {
    var c = BookCharacter(name: "Mara Quinn", colorHex: "#10B981")
    #expect(VoiceDescription.notes(c) == nil)
    #expect(VoiceDescription.design(base: "A woman.", character: c) == "A woman.")
    c.voiceNotes = "Warm underneath\n\nVoice brief:\nPitch: Low\nPace: Unhurried\nContrast: Lower than Tom"
    c.voiceBriefGenerated = "Voice brief:\nPitch: Low\nPace: Unhurried\nContrast: Lower than Tom"
    let notes = VoiceDescription.notes(c) ?? ""
    // The narrator's own words first; headings and who they contrast with are left out.
    #expect(notes == "Warm underneath. Pitch: Low. Pace: Unhurried.")
    #expect(VoiceDescription.design(base: "A woman.", character: c) == "A woman. Voice notes: " + notes)
    // Cut at a whole line to fit.
    c.voiceNotes = String(repeating: "A long note about her voice.\n", count: 3) + (1...40).map { "Line \($0) about the rasp." }.joined(separator: "\n")
    let short = VoiceDescription.design(base: "A woman.", character: c, limit: 200)
    #expect(short.count <= 200 && short.hasSuffix("."))
}

@Test func seriesVoicesMatchAndCarryOver() {
    // The earlier book: three voiced characters and a narrator voice.
    var old = Project.empty
    let mara = BookCharacter(name: "Mara Quinn", aliases: ["Miss Quinn"], colorHex: "#111111", voiceNotes: "Warm, clipped.")
    let tom = BookCharacter(name: "Tom Hale", colorHex: "#222222")
    let edda = BookCharacter(name: "Doctor Edda Voss", colorHex: "#333333")
    let nobody = BookCharacter(name: "Silent Sam", colorHex: "#444444")
    old.characters = [mara, tom, edda, nobody]
    let qwen = VoiceChoice(engine: .qwen, voiceID: "design", design: "A woman with a warm voice.")
    old.characters[0].setVoice(CastVoice(choice: qwen, confidence: 0.6, source: .auto), for: .qwen)
    old.characters[0].setVoice(CastVoice(choice: VoiceChoice(engine: .apple, voiceID: "karen"), confidence: 0.6, source: .auto), for: .apple)
    old.characters[1].setVoice(CastVoice(choice: VoiceChoice(engine: .orpheus, voiceID: "leo"), confidence: 1, source: .user), for: .orpheus)
    old.characters[2].setVoice(CastVoice(choice: VoiceChoice(engine: .orpheus, voiceID: "zac"), confidence: 1, source: .user), for: .orpheus)
    old.settings.voiceEngine = .qwen
    old.settings.setNarratorVoice(CastVoice(choice: VoiceInfo.qwenPresets[0].choice, confidence: 1, source: .auto), for: .qwen)

    // This book: Mara goes by "Miss Quinn" as her name now, Tom keeps his, Edda is just "Edda".
    var new = Project.empty
    new.characters = [BookCharacter(name: "Miss Quinn", colorHex: "#aaaaaa"), BookCharacter(name: "Tom Hale", colorHex: "#bbbbbb"),
                      BookCharacter(name: "Edda", colorHex: "#cccccc"), BookCharacter(name: "Pip", colorHex: "#dddddd")]
    let oldIndex = BookIndex(old)
    // Only characters with a voice are offered.
    #expect(SeriesVoices.voiced(old, index: oldIndex).count == 3)
    let matches = SeriesVoices.suggest(from: old, oldIndex: oldIndex, to: new)
    let byName = Dictionary(uniqueKeysWithValues: matches.map { ($0.source, $0) })
    #expect(byName[mara.id]?.target == new.characters[0].id && byName[mara.id]?.how == .name)
    #expect(byName[tom.id]?.target == new.characters[1].id && byName[tom.id]?.how == .name)
    #expect(byName[edda.id]?.target == new.characters[2].id && byName[edda.id]?.how == .firstName)

    // Leave Tom out, then apply.
    let chosen = matches.map { $0.source == tom.id ? SeriesVoices.Match(source: tom.id, target: nil, how: nil) : $0 }
    let n = SeriesVoices.apply(chosen, from: old, to: &new, options: .init())
    #expect(n == 2)
    let q = new.characters[0]
    #expect(q.voice(for: .qwen)?.choice == qwen && q.voice(for: .qwen)?.source == .user && q.voice(for: .apple)?.choice.voiceID == "karen")
    #expect(q.voiceNotes == "Warm, clipped.")
    #expect(new.characters[1].voices == nil)
    #expect(new.characters[2].voice(for: .orpheus)?.choice.voiceID == "zac")
    #expect(new.settings.voiceEngine == .qwen && new.settings.narratorVoice(for: .qwen)?.source == .user)

    // Casting keeps carried-over voices.
    VoiceCasting.cast(&new, index: BookIndex(new), engine: .orpheus, voices: VoiceInfo.orpheus)
    #expect(new.characters[2].voice(for: .orpheus)?.choice.voiceID == "zac")
}

@Test func deletingChapterHeadings() {
    var (p, _, _, _) = deletionProject()
    let first = p.chapters[0], second = p.chapters[1]
    let lines = p.lines
    // Part of a heading: the rest stays.
    var q = p
    q.deleteText(cuts: [:], headings: [first.id: TextSpan(location: 0, length: (first.title as NSString).length - 1)])
    #expect(q.chapters[0].title.count == 1)
    // A later heading, all of it: its text joins the chapter before, lines untouched.
    q = p
    q.deleteText(cuts: [:], headings: [second.id: TextSpan(location: 0, length: (second.title as NSString).length)])
    #expect(q.chapters.count == p.chapters.count - 1)
    #expect(q.chapters[0].paragraphs.map(\.id) == first.paragraphs.map(\.id) + second.paragraphs.map(\.id))
    #expect(q.lines == lines)
    // The first heading: its text stays, with no heading.
    p.deleteText(cuts: [:], headings: [first.id: TextSpan(location: 0, length: 999)])
    #expect(p.chapters[0].title.isEmpty)
    #expect(p.chapters[0].shownTitle == "Opening")
    #expect(p.chapters[0].paragraphs.map(\.id) == first.paragraphs.map(\.id))
}

@Test func guidedStepsFollowTheBook() {
    var p = Project(title: "t", chapters: [Chapter(title: "One", paragraphs: [Paragraph(text: "“Hi,” said Ada.")])])
    let para = p.chapters[0].paragraphs[0].id
    p.lines = [Line(paragraphID: para, range: TextSpan(location: 0, length: 6), kind: .spoken, speakerID: nil, confidence: 0, source: .none)]
    var steps = GuidedSteps(project: p, narration: false, speaking: 0, voicesNeeded: 0)
    #expect(steps.all.map(\.step) == [.manuscript, .speakers, .review, .characters, .voices, .listen])
    #expect(steps.current?.step == .manuscript)
    // Marking the manuscript ready moves on to Tag Speakers.
    p.settings.markStep(.manuscript, done: true)
    steps = GuidedSteps(project: p, narration: false, speaking: 0, voicesNeeded: 0)
    #expect(steps.current?.step == .speakers)
    // Tagged with a line that needs the narrator: Review is next and says how many.
    p.lastTagged = .now
    steps = GuidedSteps(project: p, narration: false, speaking: 0, voicesNeeded: 0)
    #expect(steps.current?.step == .review)
    #expect(steps.current?.detail == "1 to review")
    // Skipping review moves on; the characters haven't been read.
    p.settings.markStep(.review, done: true)
    steps = GuidedSteps(project: p, narration: false, speaking: 1, voicesNeeded: 0)
    #expect(steps.current?.step == .characters)
    p.summariesBuilt = .now
    p.voicesCast = .now
    steps = GuidedSteps(project: p, narration: false, speaking: 1, voicesNeeded: 2)
    #expect(steps.current?.step == .voices)
    #expect(steps.current?.detail == "2 need a voice")
    steps = GuidedSteps(project: p, narration: false, speaking: 1, voicesNeeded: 0)
    #expect(steps.current?.step == .listen)
    p.settings.markStep(.listen, done: true)
    #expect(GuidedSteps(project: p, narration: false, speaking: 1, voicesNeeded: 0).allDone)
    // Taking a mark off brings the step back.
    p.settings.markStep(.review, done: false)
    #expect(GuidedSteps(project: p, narration: false, speaking: 1, voicesNeeded: 0).current?.step == .review)
    // Narration has its own last steps.
    let n = GuidedSteps(project: p, narration: true, speaking: 1, voicesNeeded: 0)
    #expect(n.all.map(\.step) == [.manuscript, .speakers, .review, .characters, .pronunciations, .record])
    #expect(n.all.last?.detail == "0 of 1 recorded")
}
