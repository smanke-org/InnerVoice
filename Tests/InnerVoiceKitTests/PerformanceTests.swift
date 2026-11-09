import Foundation
import Testing
@testable import InnerVoiceKit

/// A novel-sized book (about 120,000 words, 40 chapters, 30 characters) must import,
/// find its lines and run the rules quickly, and revise in seconds.
@Test func novelSizedBookIsFast() {
    let names = (0..<30).map { "Person\(Character(UnicodeScalar(65 + $0 % 26)!))\($0)" }
    var chapters: [Chapter] = []
    for c in 0..<40 {
        var paragraphs: [Paragraph] = []
        for p in 0..<150 {
            let a = names[(c + p) % names.count], b = names[(c * 3 + p) % names.count]
            switch p % 3 {
            case 0: paragraphs.append(Paragraph(text: "\(a) looked across the room at \(b) and said nothing for a while, thinking about the long road behind them both and the night still ahead."))
            case 1: paragraphs.append(Paragraph(text: "“We should go before the light fails and the tide turns against us,” said \(a), pulling on a coat."))
            default: paragraphs.append(Paragraph(text: "“Not yet. There is still one thing I have to tell you, and you won't like it.”"))
            }
        }
        chapters.append(Chapter(title: "Chapter \(c + 1)", paragraphs: paragraphs))
    }
    let book = ImportedBook(title: "Big", chapters: chapters)
    let words = chapters.reduce(0) { $0 + $1.wordCount }
    var start = Date()
    let project = ProjectBuilder.make(from: book, fileName: "big.txt")
    let build = Date().timeIntervalSince(start)
    start = Date()
    _ = BookIndex(project)
    let index = Date().timeIntervalSince(start)
    start = Date()
    var revised = book
    revised.chapters[5].paragraphs[3].text += " Then she left."
    let result = RevisionMerger.merge(project, with: revised, fileName: "v2")
    let revise = Date().timeIntervalSince(start)
    print("PERF \(words) words, \(project.lines.count) lines, \(project.characters.count) characters: build \(String(format: "%.2f", build)) s, index \(String(format: "%.3f", index)) s, revise \(String(format: "%.2f", revise)) s")
    #expect(words > 100_000)
    #expect(build < 5)
    #expect(index < 0.5)
    #expect(revise < 10)
    #expect(result.record.changed.count == 1)
}

/// What one keystroke in a character's Voice Notes costs on a novel-sized book.
@Test func typingInVoiceNotesIsCheap() {
    let names = (0..<30).map { "Person\(Character(UnicodeScalar(65 + $0 % 26)!))\($0)" }
    var chapters: [Chapter] = []
    for c in 0..<40 {
        var paragraphs: [Paragraph] = []
        for p in 0..<150 {
            let a = names[(c + p) % names.count], b = names[(c * 3 + p) % names.count]
            switch p % 3 {
            case 0: paragraphs.append(Paragraph(text: "\(a) looked across the room at \(b) and said nothing for a while, thinking about the long road behind them both and the night still ahead."))
            case 1: paragraphs.append(Paragraph(text: "“We should go before the light fails and the tide turns against us,” said \(a), pulling on her coat."))
            default: paragraphs.append(Paragraph(text: "“Not yet. There is still one thing I have to tell you, and you won't like it.”"))
            }
        }
        chapters.append(Chapter(title: "Chapter \(c + 1)", paragraphs: paragraphs))
    }
    let project = ProjectBuilder.make(from: ImportedBook(title: "Big", chapters: chapters), fileName: "big.txt")
    let index = BookIndex(project)
    let main = project.characters.max { (index.stats[$0.id]?.lines ?? 0) < (index.stats[$1.id]?.lines ?? 0) }!
    func time(_ label: String, _ n: Int = 5, _ body: () -> Void) -> Double {
        let start = Date()
        for _ in 0..<n { body() }
        let ms = Date().timeIntervalSince(start) / Double(n) * 1000
        print("KEYSTROKE \(label): \(String(format: "%.2f", ms)) ms")
        return ms
    }
    var edited = project
    edited.characters[edited.characterIndex(main.id)!].voiceNotes = "Low and slow."
    _ = time("compare projects") { _ = edited != project }
    _ = time("BookIndex") { _ = BookIndex(edited) }
    _ = time("casting profile, first time", 1) { _ = VoiceCasting.profile(main, project: project, index: index) }
    // Typing in Voice Notes changes nothing the whole-book pronoun scan reads: it's kept.
    let again = time("casting profile after a keystroke (\(index.stats[main.id]?.lines ?? 0) lines)") { _ = VoiceCasting.profile(main, project: edited, index: index) }
    #expect(again < 15)
    let voices = (0..<40).map { VoiceInfo(engine: .apple, voiceID: "v\($0)", name: "V\($0)", gender: $0 % 2 == 0 ? .female : .male, age: .adult, accent: "American") }
    _ = time("suggestions") { _ = VoiceCasting.suggestions(for: main, project: edited, index: index, voices: voices) }
}
