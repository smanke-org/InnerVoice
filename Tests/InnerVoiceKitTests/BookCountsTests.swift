import Foundation
import Testing
@testable import InnerVoiceKit

@Test func bookCountsLeaveOutSceneBreaksAndCountSpacesAndPunctuation() {
    let chapter = Chapter(title: "One", paragraphs: [
        Paragraph(text: "Chapter One", style: .heading, italic: []),
        Paragraph(text: "  “Hello, Tom,” said Mara.  ", style: .body, italic: []),
        Paragraph(text: "* * *", style: .sceneBreak, italic: []),
        Paragraph(text: "It's 9 o'clock.", style: .body, italic: []),
    ])
    let p = ProjectBuilder.make(from: ImportedBook(title: "T", chapters: [chapter]), fileName: "t.txt")
    let index = BookIndex(p)
    #expect(index.bookWords == 2 + 4 + 3)
    // Trimmed, but spaces and punctuation inside count: 11 + 24 + 15.
    #expect(index.billableCharacters == 11 + 24 + 15)
    #expect(BookCounts.label(words: 12345, characters: 67890) == "12,345 words · 67,890 characters")
}

@Test func costHelpPutsTheBooksEngineFirst() {
    let date = DateComponents(calendar: Calendar(identifier: .gregorian), year: 2026, month: 11, day: 26).date!
    let help = BookCounts.costHelp(characters: 500_000, engine: .speechify, gemini: .flash, on: date)
    #expect(help.hasPrefix("500,000 billable characters"))
    let speechify = help.range(of: "• Speechify: about $10.00 at pay-as-you-go (this book’s engine)")
    let eleven = help.range(of: "• ElevenLabs: about 500,000 credits")
    #expect(speechify != nil && eleven != nil && speechify!.lowerBound < eleven!.lowerBound)
    // 500,000 / 15 s × 25 tokens × $9/M = $7.50.
    #expect(help.contains("Gemini TTS: about $7.50 (Flash, paid tier)"))
    #expect(!help.contains("no charge"))
    #expect(BookCounts.costHelp(characters: 10, engine: .pocket, gemini: .flashLite, on: date).contains("runs on this computer: no charge"))
}

@Test func costHelpUsesTheChosenElevenLabsModel() {
    let flash = ElevenLabsModel.known.first { $0.id == "eleven_flash_v2_5" }!
    #expect(BookCounts.costHelp(characters: 1000, engine: .elevenlabs, gemini: .flash, eleven: flash).contains("ElevenLabs: about 500 credits (Eleven Flash v2.5)"))
}
