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
