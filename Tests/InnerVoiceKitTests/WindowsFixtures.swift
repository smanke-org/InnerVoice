import AppKit
import CoreText
import Foundation
import Testing
@testable import InnerVoiceKit

// Fixtures for the Windows version (windows/Fixtures): what this Kit produces from the
// placeholder sample story, so the C# port can prove it gets the same results and opens
// and saves a Mac project without losing anything. Placeholder text only, never a real
// manuscript.
//
// Writes only when asked, so normal test runs leave the tree clean:
//   INNERVOICE_WRITE_FIXTURES="$PWD/windows/Fixtures" swift test --filter WindowsFixtures

@Suite struct WindowsFixtures {
    /// A short original story; every name and place is made up (the same story as the app's
    /// SeedData, so the two apps' demos match).
    static let markdown = """
    # Chapter 1: The Storm

    The wind came off the sea like a held breath finally let go. Mara Quinn pulled the door shut behind her and leaned on it until the latch caught.

    “You're late,” said Tom Hale, without looking up from the lamp.

    “The path was underwater,” Mara said. “I had to go round by the chapel.”

    “Then you'll be wanting tea.”

    “I'll be wanting a towel first.”

    “Towels are in the chest. Same as always.”

    “Same as always,” she repeated, and almost smiled.

    *He hasn't asked about the letter,* Mara thought. *Maybe he doesn't know.*

    Tom set down the wick trimmer. He was a broad man gone narrow with age, sixty if he was a day, and when he spoke again his voice had the gravel of a man who had shouted over gales for forty years. “Doctor Voss came by. Asked after you.”

    “What did you tell her?”

    “That you'd be back when the sea let you.”

    ⁂

    A knock shook the door. Pip Aelwyn stood on the step, soaked to the skin, a lantern swinging from one small fist.

    “Miss Quinn! Miss Quinn, the boat's come loose from the jetty and Da says—”

    “Slow down, Pip.”

    “—Da says if it goes on the rocks we'll none of us eat till spring!”

    # Chapter 2: The Letter

    Doctor Edda Voss arrived the next morning with her bag and her clipped, careful vowels, every word set down as if it might break. She had come from Hamburg twenty years ago and never quite let the north of Germany go.

    “You have been avoiding me, Mara.”

    “I've been busy.”

    “Busy.” The doctor folded her gloves. “Your father wrote to me before he died. I think you should read what he wrote.”

    She laid an envelope on the table. The handwriting on it was small and slanted.

    > My dear girl — if Edda has given you this, then I was too much of a coward to say it myself. The light was never mine to leave you. It belongs to Kerensa.

    “Who is Kerensa?” Mara whispered.

    “Read the rest.”

    “I can't.”

    “You can. You are your father's daughter.”

    “That's what frightens me.”

    Pip had crept in behind them, dripping on the flagstones. “Is it treasure? Da says old Quinn had treasure.”

    “Out,” said the doctor, not unkindly.

    # Chapter 3: The Keeper

    That night Tom told her the story. He told it the way he told everything, in the soft burr of the Highlands he had never lost, though he'd left Inverness as a boy of twelve.

    “Your father and I kept this light together for thirty years,” he said. “And in all that time he never once lied to me. Except about Kerensa.

    “She was his sister. Not dead, as he told the village. Gone to sea, and never written, and him too proud to look for her.”

    “Is she alive?”

    “Aye. In Penzance. She wrote to me last spring.”

    “And you didn't tell me.”

    “I'm telling you now, lass.”
    """

    static var outputFolder: URL? {
        ProcessInfo.processInfo.environment["INNERVOICE_WRITE_FIXTURES"].map { URL(fileURLWithPath: $0) }
    }

    static func sampleProject() -> Project {
        let blocks = PlainTextReader.blocks(from: markdown, markdown: true)
        let chapters = ChapterDetector.chapters(from: blocks, fallbackTitle: "Sample Story")
        return ProjectBuilder.make(from: ImportedBook(title: "Sample Story", author: "A. N. Example", chapters: chapters),
                                   fileName: "Sample Story.md")
    }

    @Test func writeFixtures() throws {
        guard let folder = Self.outputFolder else { return }
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .prettyPrinted]

        // 1. What the Kit makes of the sample: blocks, chapters, lines and the rules' speakers.
        let blocks = PlainTextReader.blocks(from: Self.markdown, markdown: true)
        var p = Self.sampleProject()
        let index = BookIndex(p)
        struct Golden: Encodable {
            struct Block: Encodable { var text: String; var italic: [TextSpan]; var headingLevel: Int?; var isBlockQuote: Bool }
            struct ChapterOut: Encodable { var title: String; var paragraphs: [ParagraphOut] }
            struct ParagraphOut: Encodable { var text: String; var style: String; var italic: [TextSpan] }
            struct LineOut: Encodable {
                var paragraph: Int; var range: TextSpan; var kind: String; var continuesPrevious: Bool
                var speaker: String?; var source: String; var confidence: Double
            }
            struct CharacterOut: Encodable { var name: String; var colorHex: String }
            var blocks: [Block]
            var chapters: [ChapterOut]
            var lines: [LineOut]
            var characters: [CharacterOut]
            var quoteStyle: String
        }
        let golden = Golden(
            blocks: blocks.map { .init(text: $0.text, italic: $0.italic, headingLevel: $0.headingLevel, isBlockQuote: $0.isBlockQuote) },
            chapters: p.chapters.map { c in .init(title: c.title, paragraphs: c.paragraphs.map { .init(text: $0.text, style: $0.style.rawValue, italic: $0.italic) }) },
            lines: p.lines.map { l in
                .init(paragraph: index.positions[l.paragraphID]!.global, range: l.range, kind: l.kind.rawValue,
                      continuesPrevious: l.continuesPrevious, speaker: p.character(l.speakerID)?.name,
                      source: l.source.rawValue, confidence: l.confidence)
            },
            characters: p.characters.map { .init(name: $0.name, colorHex: $0.colorHex) },
            quoteStyle: DialogueFinder.detectStyle(p.chapters.flatMap(\.paragraphs)).rawValue)
        try encoder.encode(golden).write(to: folder.appending(path: "sample-rules.json"))

        // 2. A Mac project with everything the Windows app must keep: tones, voices, an AI
        // voices purpose, cloud permission, plus narrators, retakes, a sample and a revision.
        let tom = p.characters.first { $0.name.hasPrefix("Tom") }!.id
        let narrator = Narrator(name: "Sample Narrator", notes: "Reads the men.")
        p.narrators = [narrator]
        if let i = p.characters.firstIndex(where: { $0.id == tom }) {
            p.characters[i].narratorID = narrator.id
            p.characters[i].facts = [Fact(kind: .age, text: "About sixty", sourceParagraphID: p.chapters[0].paragraphs[8].id,
                                          quote: "sixty if he was a day", fromAI: true)]
            p.characters[i].voiceNotes = "Gravel, Highland burr."
            p.characters[i].setVoice(CastVoice(choice: VoiceChoice(engine: .qwen, voiceID: "design", design: "An older man."),
                                               confidence: 0.8, source: .user), for: .qwen)
            p.characters[i].pinned = true
        }
        p.lines[0].aiModel = "gemma-4-e2b"
        p.settings.voiceEngine = .qwen
        p.settings.purpose = .aiVoices
        p.settings.cloudVoicesAllowed = Date(timeIntervalSinceReferenceDate: 800_000_000.25)
        p.settings.stepsDone = ["manuscript"]
        p.settings.setNarratorVoice(CastVoice(choice: VoiceChoice(engine: .apple, voiceID: "com.apple.voice.premium.en-US.Zoe"),
                                              confidence: 1, source: .user), for: .apple)
        p.tonesTagged = Date(timeIntervalSinceReferenceDate: 800_000_100.5)
        p.voicesCast = Date(timeIntervalSinceReferenceDate: 800_000_200.125)
        p.importedAt = Date(timeIntervalSinceReferenceDate: 799_999_999.123456)
        p.recorded = [p.chapters[0].paragraphs[1].id, p.chapters[0].paragraphs[2].id]
        p.retakes = [Retake(paragraphID: p.chapters[0].paragraphs[3].id, note: "Breath", created: Date(timeIntervalSinceReferenceDate: 800_000_300))]
        p.pronunciations = [Pronunciation(term: "Kerensa", sayItAs: "keh-REN-sah", audioFile: "Kerensa.m4a", occurrences: 3,
                                          firstParagraphID: p.chapters[1].paragraphs[5].id)]
        p.revisions = [RevisionRecord(date: Date(timeIntervalSinceReferenceDate: 800_000_400), fileName: "Sample Story v2.md",
                                      added: [], changed: [p.chapters[1].paragraphs[0].id],
                                      removed: [.init(text: "An old line.", chapterTitle: "Chapter 1: The Storm")], unchanged: 40)]
        p.lastTagged = Date(timeIntervalSinceReferenceDate: 800_000_500)
        let appEncoder = JSONEncoder()
        appEncoder.outputFormatting = [.sortedKeys]
        let archive = try ProjectArchive.write(.init(
            projectJSON: try appEncoder.encode(p),
            original: .init(name: "Sample Story.md", data: Data(Self.markdown.utf8)),
            audio: ["Kerensa.m4a": Data([0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70])]))
        try archive.write(to: folder.appending(path: "sample-mac.innervoice"))
    }


    /// The sample story in each manuscript format, with what the Kit imports from it, so the
    /// C# readers can be checked against the Swift ones.
    @Test func writeImportFixtures() throws {
        guard let root = Self.outputFolder else { return }
        let folder = root.appending(path: "import")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let blocks = PlainTextReader.blocks(from: Self.markdown, markdown: true)

        // HTML from the sample's blocks: headings, paragraphs, block quotes, italics.
        func escape(_ s: String) -> String { s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;").replacingOccurrences(of: ">", with: "&gt;") }
        func inline(_ b: RawBlock) -> String {
            let ns = b.text as NSString
            var out = ""
            var cursor = 0
            for r in b.italic.sorted(by: { $0.location < $1.location }) where r.location >= cursor {
                out += escape(ns.substring(with: NSRange(location: cursor, length: r.location - cursor)))
                out += "<i>" + escape(ns.substring(with: r.ns)) + "</i>"
                cursor = r.end
            }
            return out + escape(ns.substring(from: cursor))
        }
        func body(_ bs: [RawBlock]) -> String {
            bs.map { b in
                if b.headingLevel != nil { return "<h1>\(inline(b))</h1>" }
                if b.isBlockQuote { return "<blockquote><p>\(inline(b))</p></blockquote>" }
                return "<p>\(inline(b))</p>"
            }.joined(separator: "\n")
        }
        let html = "<html><head><meta charset=\"utf-8\"><title>Sample Story</title></head><body>\n\(body(blocks))\n</body></html>"
        let src = folder.appending(path: "sample.html")
        try html.write(to: src, atomically: true, encoding: .utf8)
        for format in ["docx", "odt", "rtf"] {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/textutil")
            p.arguments = ["-convert", format, src.path, "-output", folder.appending(path: "sample.\(format)").path]
            try p.run()
            p.waitUntilExit()
        }

        // EPUB: one spine file per chapter.
        let tmp = FileManager.default.temporaryDirectory.appending(path: "iv-epub-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: tmp.appending(path: "META-INF"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: tmp.appending(path: "OEBPS"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tmp) }
        try "application/epub+zip".write(to: tmp.appending(path: "mimetype"), atomically: true, encoding: .utf8)
        try #"<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>"#
            .write(to: tmp.appending(path: "META-INF/container.xml"), atomically: true, encoding: .utf8)
        var chapters: [[RawBlock]] = []
        for b in blocks { if b.headingLevel != nil || chapters.isEmpty { chapters.append([b]) } else { chapters[chapters.count - 1].append(b) } }
        var items = "", spine = ""
        for (i, ch) in chapters.enumerated() {
            let xhtml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>c</title></head><body>\(body(ch).replacingOccurrences(of: "<h1>", with: "<h2>").replacingOccurrences(of: "</h1>", with: "</h2>").replacingOccurrences(of: "<i>", with: "<em>").replacingOccurrences(of: "</i>", with: "</em>"))</body></html>"
            try xhtml.write(to: tmp.appending(path: "OEBPS/c\(i + 1).xhtml"), atomically: true, encoding: .utf8)
            items += "<item id=\"c\(i + 1)\" href=\"c\(i + 1).xhtml\" media-type=\"application/xhtml+xml\"/>"
            spine += "<itemref idref=\"c\(i + 1)\"/>"
        }
        try "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" version=\"3.0\"><metadata><dc:title>Sample Story</dc:title><dc:creator>A. N. Example</dc:creator></metadata><manifest>\(items)</manifest><spine>\(spine)</spine></package>"
            .write(to: tmp.appending(path: "OEBPS/content.opf"), atomically: true, encoding: .utf8)
        let epub = folder.appending(path: "sample.epub")
        try? FileManager.default.removeItem(at: epub)
        let d = Process()
        d.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        d.arguments = ["-c", "-k", "--sequesterRsrc", tmp.path, epub.path]
        try d.run()
        d.waitUntilExit()

        // PDF: a printed-book layout with mirrored margins, indents, headings and page numbers.
        let pdf = folder.appending(path: "sample.pdf")
        var box = CGRect(x: 0, y: 0, width: 432, height: 648)
        let ctx = CGContext(pdf as CFURL, mediaBox: &box, nil)!
        let font = NSFont(name: "Times-Roman", size: 11)!
        let italicFont = NSFont(name: "Times-Italic", size: 11)!
        let headFont = NSFont(name: "Times-Bold", size: 18)!
        var page = 0
        var y: CGFloat = 0
        func newPage() {
            if page > 0 {
                let num = CTLineCreateWithAttributedString(NSAttributedString(string: "\(page)", attributes: [.font: font]))
                ctx.textPosition = CGPoint(x: 210, y: 30)
                CTLineDraw(num, ctx)
                ctx.endPDFPage()
            }
            ctx.beginPDFPage(nil)
            page += 1
            y = 590
        }
        newPage()
        for (n, b) in blocks.enumerated() {
            let left: CGFloat = page % 2 == 1 ? 63 : 54
            let width: CGFloat = 315
            let s = NSMutableAttributedString(string: b.text, attributes: [.font: b.headingLevel != nil ? headFont : font])
            for r in b.italic where r.end <= s.length { s.addAttribute(.font, value: italicFont, range: r.ns) }
            let style = NSMutableParagraphStyle()
            style.firstLineHeadIndent = b.headingLevel != nil || n == 0 ? 0 : 14
            if b.isBlockQuote { style.headIndent = 20; style.firstLineHeadIndent = 20 }
            s.addAttribute(.paragraphStyle, value: style, range: NSRange(location: 0, length: s.length))
            let framesetter = CTFramesetterCreateWithAttributedString(s)
            let size = CTFramesetterSuggestFrameSizeWithConstraints(framesetter, CFRange(location: 0, length: 0), nil, CGSize(width: width, height: 2000), nil)
            if b.headingLevel != nil && page > 0 && y < 590 { newPage() }
            if y - size.height < 60 { newPage() }
            let x: CGFloat = page % 2 == 1 ? 63 : 54
            _ = left
            let path = CGPath(rect: CGRect(x: x, y: y - size.height, width: width, height: size.height + 1), transform: nil)
            CTFrameDraw(CTFramesetterCreateFrame(framesetter, CFRange(location: 0, length: 0), path, nil), ctx)
            y -= size.height + (b.headingLevel != nil ? 18 : 2)
        }
        let num = CTLineCreateWithAttributedString(NSAttributedString(string: "\(page)", attributes: [.font: font]))
        ctx.textPosition = CGPoint(x: 210, y: 30)
        CTLineDraw(num, ctx)
        ctx.endPDFPage()
        ctx.closePDF()

        // What the Kit makes of each.
        struct Expected: Encodable {
            struct ChapterOut: Encodable { var title: String; var paragraphs: [ParagraphOut] }
            struct ParagraphOut: Encodable { var text: String; var style: String; var italic: [TextSpan] }
            var title: String
            var author: String
            var chapters: [ChapterOut]
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .prettyPrinted]
        for name in ["sample.docx", "sample.odt", "sample.rtf", "sample.html", "sample.epub", "sample.pdf"] {
            let book = try ManuscriptImporter.read(folder.appending(path: name))
            let e = Expected(title: book.title, author: book.author, chapters: book.chapters.map { c in
                .init(title: c.title, paragraphs: c.paragraphs.map { .init(text: $0.text, style: $0.style.rawValue, italic: $0.italic) })
            })
            try encoder.encode(e).write(to: folder.appending(path: name + ".expected.json"))
        }
    }

    /// Reads back a project the Windows app saved (if one is there) and checks it decodes and
    /// still carries the Mac-only fields.
    @Test func readWindowsSavedProject() throws {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appending(path: "windows/Fixtures/sample-windows.innervoice")
        guard let data = try? Data(contentsOf: url) else { return }
        let contents = try ProjectArchive.read(data)
        let p = try JSONDecoder().decode(Project.self, from: contents.projectJSON)
        #expect(p.settings.purpose == .aiVoices)
        #expect(p.settings.voiceEngine == .qwen)
        #expect(p.characters.contains { $0.voice(for: .qwen)?.choice.voiceID == "design" })
        #expect(p.lines.contains { $0.tone != nil })
        #expect(contents.audio["Kerensa.m4a"] != nil)
        print("WindowsFixtures: read the Windows-saved project, \(p.lines.count) lines")
        #expect(p.retakes.count == 1)
    }
}
