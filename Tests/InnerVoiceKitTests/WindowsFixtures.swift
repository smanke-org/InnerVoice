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
        let narrator = Narrator(name: "Sample Narrator", notes: "Reads the men.", role: .men)
        p.narrators = [narrator]
        if let i = p.characters.firstIndex(where: { $0.id == tom }) {
            p.characters[i].narratorID = narrator.id
            p.characters[i].facts = [Fact(kind: .age, text: "About sixty", sourceParagraphID: p.chapters[0].paragraphs[8].id,
                                          quote: "sixty if he was a day", fromAI: true)]
            p.characters[i].voiceNotes = "Gravel, Highland burr."
            p.characters[i].setVoice(CastVoice(choice: VoiceChoice(engine: .pocket, voiceID: "stock:dan", pitch: 0.89),
                                               confidence: 0.8, source: .user), for: .pocket)
            p.characters[i].pinned = true
            // Casting Style (docs/CASTING-STYLE.md): the narrator's gender, main mark, role
            // and shade.
            p.characters[i].gender = .male
            p.characters[i].isMain = true
            p.characters[i].role = .own
            p.characters[i].shade = Shade(pitch: -2, pace: -1, words: "gravelly")
        }
        p.settings.castingStyle = .shared
        p.settings.narrationRole = .narrator
        p.settings.minorVoices = .byGender
        p.settings.shadeStrength = .subtle
        p.settings.setRoleVoice(CastVoice(choice: VoiceChoice(engine: .pocket, voiceID: "stock:leo"), confidence: 0.9, source: .auto),
                                .men, for: .pocket)
        p.lines[0].aiModel = "gemma-4-e2b"
        p.settings.voiceEngine = .pocket
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


    /// The sample with what Casting Style needs (an age, a voice, a brief), as both sides make it.
    static func castingProject() -> Project {
        var p = sampleProject()
        func edit(_ prefix: String, _ body: (inout BookCharacter) -> Void) {
            if let i = p.characters.firstIndex(where: { $0.name.hasPrefix(prefix) }) { body(&p.characters[i]) }
        }
        edit("Tom") { c in
            c.facts = [Fact(kind: .age, text: "About sixty"), Fact(kind: .voice, text: "A gravelly, deep voice, a little husky")]
        }
        edit("Mara") { c in c.voiceNotes = "Steady.\n\nVoice brief:\nPitch: high and bright\nPace: quick, clipped" }
        edit("Pip") { c in c.facts = [Fact(kind: .appearance, text: "A small boy")] }
        return p
    }

    /// Casting Style (docs/CASTING-STYLE.md): who reads each character, their shades and
    /// narrators in six scenarios, for the C# port's golden test.
    @Test func writeCastingFixture() throws {
        guard let folder = Self.outputFolder else { return }
        let base = Self.castingProject()
        let index = BookIndex(base)
        func names(_ ids: [UUID], _ p: Project) -> [String] { ids.compactMap { p.character($0)?.name } }
        struct ShadeOut: Encodable { var pitch: Int; var pace: Int; var words: String? }
        struct Scenario: Encodable {
            var name: String
            var readers: [String: String]
            var shades: [String: ShadeOut]
            var narrators: [String: String]
        }
        struct Golden: Encodable { var genderToReview: [String]; var suggestedMain: [String]; var scenarios: [Scenario] }
        func scenario(_ name: String, _ change: (inout Project) -> Void) -> Scenario {
            var p = base
            change(&p)
            let idx = BookIndex(p)
            let readers = CastingStyleRules.readers(p, index: idx)
            let shades = CastingStyleRules.shades(p, index: idx, narratorGender: p.settings.narratorGender)
            var narrators: [String: String] = [:]
            for c in p.characters {
                let g = c.gender ?? VoiceCasting.profile(c, project: p, index: idx).gender.map(CharacterGender.init)
                if let n = CastingStyleRules.narrator(for: c, in: p, gender: g) { narrators[c.name] = n.name }
            }
            return Scenario(name: name,
                            readers: Dictionary(uniqueKeysWithValues: p.characters.map { ($0.name, readers[$0.id]?.rawValue ?? "own") }),
                            shades: Dictionary(uniqueKeysWithValues: shades.compactMap { id, s in p.character(id).map { ($0.name, ShadeOut(pitch: s.pitch, pace: s.pace, words: s.words)) } }),
                            narrators: narrators)
        }
        let golden = Golden(
            genderToReview: names(CastingStyleRules.genderToReview(base, index: index), base),
            suggestedMain: names(CastingStyleRules.suggestedMain(base, index: index), base),
            scenarios: [
                scenario("just me, a woman narrating") { $0.settings.castingStyle = .oneVoice; $0.settings.narratorGender = .female },
                scenario("just me, a man narrating, strong") { $0.settings.castingStyle = .oneVoice; $0.settings.narratorGender = .male; $0.settings.shadeStrength = .strong },
                scenario("just me, not said, subtle, Tom's own shade") { p in
                    p.settings.castingStyle = .oneVoice
                    p.settings.shadeStrength = .subtle
                    if let i = p.characters.firstIndex(where: { $0.name.hasPrefix("Tom") }) { p.characters[i].shade = Shade(pitch: 1, pace: 0, words: "warm") }
                },
                scenario("three narrators, a two-person book") { p in
                    p.settings.castingStyle = .shared
                    p.settings.narrationRole = .women
                    p.narrators = [Narrator(name: "Alex", role: .men), Narrator(name: "Jo", role: .women)]
                    for i in p.characters.indices { p.characters[i].narratorID = nil }
                    if let i = p.characters.firstIndex(where: { $0.name.hasPrefix("Pip") }) { p.characters[i].gender = .other }
                },
                scenario("full cast, Mara main, minor by gender") { p in
                    p.settings.castingStyle = .fullCast
                    p.settings.minorVoices = .byGender
                    p.narrators = [Narrator(name: "Sam", role: .narrator), Narrator(name: "Alex", role: .men)]
                    if let i = p.characters.firstIndex(where: { $0.name.hasPrefix("Mara") }) { p.characters[i].isMain = true }
                },
                scenario("full cast, nobody main") { $0.settings.castingStyle = .fullCast },
                scenario("just me, a woman narrating, Tom set male") { p in
                    p.settings.castingStyle = .oneVoice
                    p.settings.narratorGender = .female
                    if let i = p.characters.firstIndex(where: { $0.name.hasPrefix("Tom") }) { p.characters[i].gender = .male }
                },
            ])
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .prettyPrinted]
        try encoder.encode(golden).write(to: folder.appending(path: "casting-style.json"))
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

    /// A revised manuscript merged into the sample project, so the C# RevisionMerger can
    /// prove it keeps the same paragraphs, lines, speakers and marks. Also a set of small
    /// diffs, so the C# Myers diff is shown to break ties the way CollectionDifference does.
    @Test func writeRevisionFixture() throws {
        guard let folder = Self.outputFolder else { return }
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        var old = Self.sampleProject()
        let all = old.chapters.flatMap(\.paragraphs)
        func para(_ prefix: String) throws -> Paragraph { try #require(all.first { $0.text.hasPrefix(prefix) }) }
        func person(_ prefix: String) throws -> UUID { try #require(old.characters.first { $0.name.hasPrefix(prefix) }).id }
        func setUser(_ p: Paragraph, _ speaker: UUID) throws {
            let i = try #require(old.lines.firstIndex { $0.paragraphID == p.id })
            old.lines[i].speakerID = speaker
            old.lines[i].source = .user
            old.lines[i].confidence = 1
        }
        let late = try para("“You're late,”")           // unchanged
        let path = try para("“The path was underwater,”") // unchanged
        let tea = try para("“Then you'll be wanting tea.”") // deleted
        let wick = try para("Tom set down the wick trimmer.") // a word changed inside its quote
        let rest = try para("“Read the rest.”")          // moved (swapped with the next one)
        let tom = try person("Tom"), mara = try person("Mara")
        try setUser(late, tom)
        try setUser(tea, tom)
        try setUser(wick, mara)
        try setUser(rest, tom)
        old.recorded = [late.id, wick.id, tea.id]
        old.retakes = [Retake(paragraphID: tea.id, note: "Gone", created: Date(timeIntervalSinceReferenceDate: 800_000_000)),
                       Retake(paragraphID: path.id, note: "Breath", created: Date(timeIntervalSinceReferenceDate: 800_000_001))]
        let ti = try #require(old.characters.firstIndex { $0.id == tom })
        old.characters[ti].facts = [Fact(kind: .age, text: "About sixty", sourceParagraphID: wick.id, quote: "sixty if he was a day"),
                                    Fact(kind: .other, text: "Makes tea", sourceParagraphID: tea.id, quote: "wanting tea")]
        old.pronunciations = [Pronunciation(term: "Kerensa", sayItAs: "keh-REN-sah", firstParagraphID: rest.id),
                              Pronunciation(term: "Voss", sayItAs: "voss", firstParagraphID: late.id)]

        func replacing(_ text: String, _ a: String, _ b: String) throws -> String {
            #expect(text.contains(a), "revision edit not found: \(a)")
            return text.replacingOccurrences(of: a, with: b)
        }
        var revised = Self.markdown
        revised = try replacing(revised, "“Doctor Voss came by. Asked after you.”", "“Doctor Voss called by. Asked after you.”")
        revised = try replacing(revised, "“Then you'll be wanting tea.”\n\n", "")
        revised = try replacing(revised, "“I've been busy.”\n\n", "“I've been busy.”\n\n“Too busy for an old friend?” Doctor Voss asked.\n\n")
        revised = try replacing(revised, "“Read the rest.”\n\n“I can't.”", "“I can't.”\n\n“Read the rest.”")

        let blocks = PlainTextReader.blocks(from: revised, markdown: true)
        let book = ImportedBook(title: "Sample Story", author: "A. N. Example",
                                chapters: ChapterDetector.chapters(from: blocks, fallbackTitle: "Sample Story"))
        let result = RevisionMerger.merge(old, with: book, fileName: "Sample Story v2.md")
        let p = result.project
        let index = BookIndex(p)
        let oldIndex = BookIndex(old)
        let oldLineIndex = Dictionary(old.lines.enumerated().map { ($0.element.id, $0.offset) }, uniquingKeysWith: { a, _ in a })
        func pos(_ id: UUID?) -> Int? { id.flatMap { index.positions[$0]?.global } }

        struct Out: Encodable {
            struct Record: Encodable {
                var fileName: String; var added: [Int]; var changed: [Int]
                var removed: [RevisionRecord.Removed]; var unchanged: Int
            }
            struct ChapterOut: Encodable { var title: String; var oldChapter: Int? }
            struct ParagraphOut: Encodable { var text: String; var oldParagraph: Int? }
            struct LineOut: Encodable {
                var paragraph: Int; var range: TextSpan; var kind: String; var continuesPrevious: Bool
                var speaker: String?; var source: String; var confidence: Double; var oldLine: Int?
            }
            struct FactOut: Encodable { var character: String; var text: String; var sourceParagraph: Int? }
            struct PronunciationOut: Encodable { var term: String; var firstParagraph: Int? }
            struct DiffCase: Encodable { var old: [String]; var new: [String]; var removed: [Int]; var inserted: [Int] }
            var oldProject: String
            var revisedMarkdown: String
            var record: Record
            var chapters: [ChapterOut]
            var paragraphs: [ParagraphOut]
            var lines: [LineOut]
            var characters: [String]
            var recorded: [Int]
            var retakes: [Int]
            var facts: [FactOut]
            var pronunciations: [PronunciationOut]
            var sourceFileName: String
            var revisionCount: Int
            var diffCases: [DiffCase]
        }

        // Small diffs over a tiny alphabet (lots of ties), from a fixed pseudo-random seed.
        var seed: UInt64 = 0x1234_5678
        func next(_ n: Int) -> Int {
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            return Int((seed >> 33) % UInt64(n))
        }
        var cases: [([String], [String])] = [([], []), ([], ["a"]), (["a"], []), (["a", "b"], ["b", "a"]),
                                             (["a", "b", "c", "a", "b", "b", "a"], ["c", "b", "a", "b", "a", "c"])]
        for _ in 0..<60 {
            let letters = ["a", "b", "c", "d"].prefix(2 + next(3))
            let o = (0..<next(14)).map { _ in letters[next(letters.count)] }
            var n = o
            for _ in 0..<(1 + next(5)) {
                switch next(3) {
                case 0 where !n.isEmpty: n.remove(at: next(n.count))
                case 1: n.insert(letters[next(letters.count)], at: next(n.count + 1))
                default: if !n.isEmpty { n[next(n.count)] = letters[next(letters.count)] }
                }
            }
            cases.append((o, n))
        }
        let diffCases = cases.map { o, n in
            var removed: [Int] = [], inserted: [Int] = []
            for change in n.difference(from: o) {
                switch change {
                case .remove(let offset, _, _): removed.append(offset)
                case .insert(let offset, _, _): inserted.append(offset)
                }
            }
            return Out.DiffCase(old: o, new: n, removed: removed.sorted(), inserted: inserted.sorted())
        }

        let appEncoder = JSONEncoder()
        appEncoder.outputFormatting = [.sortedKeys]
        let out = Out(
            oldProject: String(decoding: try appEncoder.encode(old), as: UTF8.self),
            revisedMarkdown: revised,
            record: .init(fileName: result.record.fileName, added: result.record.added.map { pos($0)! },
                          changed: result.record.changed.map { pos($0)! }, removed: result.record.removed,
                          unchanged: result.record.unchanged),
            chapters: p.chapters.map { c in .init(title: c.title, oldChapter: old.chapters.firstIndex { $0.id == c.id }) },
            paragraphs: p.chapters.flatMap(\.paragraphs).map { .init(text: $0.text, oldParagraph: oldIndex.positions[$0.id]?.global) },
            lines: p.lines.map { l in
                .init(paragraph: pos(l.paragraphID)!, range: l.range, kind: l.kind.rawValue, continuesPrevious: l.continuesPrevious,
                      speaker: p.character(l.speakerID)?.name, source: l.source.rawValue, confidence: l.confidence,
                      oldLine: oldLineIndex[l.id])
            },
            characters: p.characters.map(\.name),
            recorded: p.recorded.map { pos($0)! }.sorted(),
            retakes: p.retakes.map { pos($0.paragraphID)! },
            facts: p.characters.flatMap { c in c.facts.map { .init(character: c.name, text: $0.text, sourceParagraph: pos($0.sourceParagraphID)) } },
            pronunciations: p.pronunciations.map { .init(term: $0.term, firstParagraph: pos($0.firstParagraphID)) },
            sourceFileName: p.sourceFileName,
            revisionCount: p.revisions.count,
            diffCases: diffCases)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .prettyPrinted]
        try encoder.encode(out).write(to: folder.appending(path: "revision.json"))
        print("WindowsFixtures: revision added \(out.record.added) changed \(out.record.changed) removed \(out.record.removed.count) unchanged \(out.record.unchanged)")
    }

    /// Reads back a project the Windows app saved (if one is there) and checks it decodes and
    /// still carries the Mac-only fields.
    @Test func readWindowsSavedProject() throws {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appending(path: "windows/Fixtures/sample-windows.innervoice")
        guard let data = try? Data(contentsOf: url) else { return }
        let contents = try ProjectArchive.read(data)
        var p = try JSONDecoder().decode(Project.self, from: contents.projectJSON)
        #expect(p.settings.purpose == .aiVoices)
        // Saved with Qwen3-TTS: the engine reads as Pocket TTS, and opening it (migrate)
        // turns the designed voice, the narrator's own choice, into a stock voice that stays his.
        #expect(p.settings.voiceEngine == .pocket)
        #expect(p.characters.contains { $0.voices?[VoiceEngine.retiredQwen]?.choice.voiceID == "design" })
        #expect(LegacyVoices.migrate(&p))
        #expect(!LegacyVoices.needsMigration(p))
        #expect(p.characters.contains { $0.voice(for: .pocket).map { $0.source == .user && $0.choice.voiceID.hasPrefix("stock:") } == true })
        #expect(p.lines.contains { $0.tone != nil })
        #expect(contents.audio["Kerensa.m4a"] != nil)
        print("WindowsFixtures: read the Windows-saved project, \(p.lines.count) lines")
        #expect(p.retakes.count == 1)
    }
}
