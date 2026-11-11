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
