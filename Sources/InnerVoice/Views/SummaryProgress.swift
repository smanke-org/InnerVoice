import InnerVoiceKit
import SwiftUI

/// Whenever the AI is working in the background, a bar at the top of every page (the
/// Manuscript has its own banner for Tag Speakers):
/// - Tag Speakers: its progress and the chapter it's on.
/// - Build Summaries: for one character, a bar and their name; for the whole cast, a bar for
///   the run ("Character 3 of 12") and, below it, a bar for the character being read, then the
///   same while it writes voice briefs.
/// - An AI voice engine loading its models.
/// Stop is there while a run goes; its result shows after, with Dismiss.
struct BackgroundAIBar: View {
    @ObservedObject var document: InnerVoiceDocument
    var page: Workspace.Page
    private var speech: SpeechModelActivity { .shared }

    var body: some View {
        VStack(spacing: 0) {
            if page != .manuscript { runs }
            if let e = speech.current {
                HStack(spacing: Space.md) {
                    Image(systemName: "person.wave.2").foregroundStyle(.tint)
                    VStack(alignment: .leading, spacing: 3) {
                        ProgressView().progressViewStyle(.linear)
                        Text("Getting \(e.title) ready: loading its voice models…").font(.callout).foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: 420, alignment: .leading)
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, Space.md)
                .padding(.vertical, Space.sm)
                .frame(maxWidth: .infinity)
                .background(.bar)
            }
        }
        .animation(.easeOut(duration: 0.2), value: speech.current)
    }

    @ViewBuilder
    private var runs: some View {
        let tagging = document.tagging
        Group {
            if let s = tagging.status, s.job == .tagging {
                HStack(alignment: .center, spacing: Space.md) {
                    Image(systemName: "sparkles").foregroundStyle(.tint)
                    bar(title: s.message, detail: nil, value: s.progress)
                        .frame(maxWidth: 420, alignment: .leading)
                    Spacer(minLength: 0)
                    Button("Stop") { tagging.stop() }
                        .help("Stop Tag Speakers. Lines tagged so far are kept.")
                }
                .padding(Space.md)
                .frame(maxWidth: .infinity)
                .background(.bar)
            } else if let s = tagging.status, s.job == .summaries {
                HStack(alignment: .center, spacing: Space.md) {
                    Image(systemName: "sparkles").foregroundStyle(.tint)
                    VStack(alignment: .leading, spacing: Space.sm) {
                        let briefs = s.phase == "Writing voice briefs"
                        if s.characterCount > 1 {
                            bar(title: briefs ? "Writing voice briefs" : "Analyzing the cast", detail: "Character \(s.characterNumber) of \(s.characterCount)", value: s.progress)
                        }
                        if let name = s.character {
                            bar(title: s.characterCount > 1 ? name : (briefs ? "Voice brief for \(name)" : "Analyzing \(name)"),
                                detail: briefs ? "Pitch, pace, texture, and how they differ from their scene partners" : "Age, accent, voice and manner, for their voice notes",
                                value: s.characterProgress)
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
            } else if tagging.status == nil, let done = tagging.finished {
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
