import SwiftUI
import InnerVoiceKit

/// A toolbar button right of the book's name; click it for the book's words and billable
/// characters and what the whole book would cost with each cloud voice engine. Read from
/// `BookIndex`, so it's free on redraw and stays current as text is deleted.
struct BookCountsButton: View {
    @ObservedObject var document: InnerVoiceDocument
    var workspace: Workspace
    @State private var shown = false

    var body: some View {
        Button("Word Count", systemImage: "textformat.123") { shown.toggle() }
            .help("Words, billable characters and what cloud voices would cost")
            .popover(isPresented: $shown, arrowEdge: .bottom) { BookCountsPopover(document: document, workspace: workspace, shown: $shown) }
    }
}

struct BookCountsPopover: View {
    @ObservedObject var document: InnerVoiceDocument
    var workspace: Workspace
    @Binding var shown: Bool

    var body: some View {
        let words = document.index.bookWords, chars = document.index.billableCharacters
        VStack(alignment: .leading, spacing: Space.md) {
            Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: Space.md, verticalSpacing: Space.xs) {
                GridRow {
                    Text(words.formatted()).font(.title2.weight(.semibold)).monospacedDigit().gridColumnAlignment(.trailing)
                    Text("words")
                }
                GridRow {
                    Text(chars.formatted()).font(.title2.weight(.semibold)).monospacedDigit()
                    Text("billable characters: letters, numbers, spaces and punctuation").foregroundStyle(.primary)
                }
            }
            Divider()
            Text(BookCounts.costHelp(characters: chars, engine: document.project.engineInUse,
                                     gemini: GeminiVoices.shared.model, eleven: ElevenLabs.shared.model)
                    .components(separatedBy: "\n\n").dropFirst().joined(separator: "\n\n"))
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Spacer()
                Button("Book Stats…") { shown = false; workspace.showBookStats = true }
            }
        }
        .padding(Space.lg)
        .frame(width: 380)
    }
}
