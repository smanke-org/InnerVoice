import InnerVoiceKit
import SwiftUI

/// Build Summaries' progress on the Cast page and a character's page: for one character, a
/// bar and their name; for the whole cast, a bar for the run ("Character 3 of 12") and,
/// below it, a bar for the character being read now. When it's done, what it found, with
/// Dismiss. Stop is always there while it runs.
struct SummaryProgress: View {
    @ObservedObject var document: InnerVoiceDocument

    var body: some View {
        let tagging = document.tagging
        Group {
            if let s = tagging.status, s.job == .summaries {
                HStack(alignment: .center, spacing: Space.md) {
                    Image(systemName: "sparkles").foregroundStyle(.tint)
                    VStack(alignment: .leading, spacing: Space.sm) {
                        if s.characterCount > 1 {
                            bar(title: "Analyzing the cast", detail: "Character \(s.characterNumber) of \(s.characterCount)", value: s.progress)
                        }
                        if let name = s.character {
                            bar(title: s.characterCount > 1 ? name : "Analyzing \(name)",
                                detail: "Age, accent, voice and manner, for their voice notes", value: s.characterProgress)
                        } else {
                            bar(title: s.message, detail: nil, value: s.progress)
                        }
                    }
                    .frame(maxWidth: 420, alignment: .leading)
                    Spacer(minLength: 0)
                    Button("Stop") { tagging.stop() }
                        .keyboardShortcut(".", modifiers: .command)
                        .help("Stop. Facts and voice notes found so far are kept.")
                }
                .padding(Space.md)
                .frame(maxWidth: .infinity)
                .background(.bar)
            } else if tagging.status == nil, tagging.lastRun == .summaries, let done = tagging.finished {
                HStack(spacing: Space.md) {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    Text(done)
                    Spacer(minLength: 0)
                    Button("Dismiss") { tagging.finished = nil }
                }
                .padding(Space.md)
                .frame(maxWidth: .infinity)
                .background(.bar)
            } else if tagging.status == nil, let failure = tagging.failure {
                HStack(spacing: Space.md) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                    Text(failure).lineLimit(2)
                    Spacer(minLength: 0)
                    Button("Dismiss") { tagging.failure = nil }
                }
                .padding(Space.md)
                .frame(maxWidth: .infinity)
                .background(.bar)
            }
        }
        .animation(.easeOut(duration: 0.2), value: tagging.status?.characterNumber)
    }

    private func bar(title: String, detail: String?, value: Double) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            ProgressView(value: min(1, max(0, value)))
                .progressViewStyle(.linear)
            HStack(spacing: Space.sm) {
                Text(title).font(.callout.weight(.medium)).lineLimit(1)
                if let detail { Text(detail).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
                Spacer(minLength: 0)
                Text("\(Int((min(1, value) * 100).rounded()))%").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            }
        }
    }
}
