import SwiftUI
import InnerVoiceKit

/// Words and billable characters beside the book's name. Hover for what the whole book
/// would cost with each cloud voice engine; click for Book Stats. Read from `BookIndex`,
/// so it's free on redraw and stays current as text is deleted.
struct BookCountsButton: View {
    @ObservedObject var document: InnerVoiceDocument
    var workspace: Workspace

    var body: some View {
        let words = document.index.bookWords, chars = document.index.billableCharacters
        Button { workspace.showBookStats = true } label: {
            Text(BookCounts.label(words: words, characters: chars))
                .font(.callout).monospacedDigit()
                .foregroundStyle(.secondary)
                .fixedSize()
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 6)
        .help(BookCounts.costHelp(characters: chars, engine: document.project.engineInUse, gemini: GeminiVoices.shared.model, eleven: ElevenLabs.shared.model) + " Click for Book Stats.")
        .accessibilityLabel("\(words) words, \(chars) billable characters. Shows Book Stats.")
    }
}
