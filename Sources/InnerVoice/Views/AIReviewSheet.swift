import InnerVoiceKit
import SwiftUI

/// After Tag Speakers with Claude: every line it changed, checked to keep. Unchecked lines go
/// back to how they were, in one Undo step. Closing with Keep All changes nothing.
struct AIReviewSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    let review: TaggingController.PendingReview
    @Environment(\.undoManager) private var undoManager
    @State private var undone: Set<UUID> = []
    @State private var filter = Filter.all

    enum Filter: String, CaseIterable, Identifiable {
        case all = "All", speakers = "Speakers", tones = "Tones"
        var id: String { rawValue }
    }

    var body: some View {
        let p = document.project
        let index = document.index
        let names = Dictionary(uniqueKeysWithValues: p.characters.map { ($0.id, $0.name) })
        let lines = Dictionary(uniqueKeysWithValues: p.lines.map { ($0.id, $0) })
        let shown = review.items.filter {
            switch filter {
            case .all: true
            case .speakers: $0.speakerChanged
            case .tones: $0.toneChanged
            }
        }
        VStack(alignment: .leading, spacing: Space.md) {
            HStack(spacing: Space.sm) {
                Text("Review Claude’s Changes").font(.title2.weight(.semibold))
                CloudBadge(service: "Anthropic")
                Spacer()
                Picker("Show", selection: $filter) {
                    ForEach(Filter.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
            }
            Text("\(review.model) changed \(review.items.count.formatted()) lines. Uncheck any you don’t want, and they go back to how they were. Lines you assigned yourself were not touched.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            List(shown) { item in
                let line = lines[item.lineID]
                HStack(alignment: .top, spacing: Space.md) {
                    Toggle("Keep", isOn: Binding(
                        get: { !undone.contains(item.lineID) },
                        set: { keep in if keep { undone.remove(item.lineID) } else { undone.insert(item.lineID) } }
                    ))
                    .labelsHidden()
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: Space.sm) {
                            if let line { Text(index.reference(line.paragraphID)).font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
                            if item.speakerChanged {
                                change(item.speakerBefore.flatMap { names[$0] } ?? "Who’s Speaking?",
                                       item.speakerAfter.flatMap { names[$0] } ?? "Who’s Speaking?")
                            }
                            if item.toneChanged {
                                change((item.toneBefore ?? .neutral).title, (item.toneAfter ?? .neutral).title)
                            }
                        }
                        if let line {
                            Text(p.text(of: line, index: index)).lineLimit(3).font(.callout)
                        }
                    }
                }
                .padding(.vertical, 2)
            }
            .frame(minHeight: 280)
            HStack {
                Button(undone.count == review.items.count ? "Check All" : "Uncheck All") {
                    undone = undone.count == review.items.count ? [] : Set(review.items.map(\.lineID))
                }
                Spacer()
                Button("Keep All") { close() }
                    .keyboardShortcut(.cancelAction)
                Button(undone.isEmpty ? "Done" : "Undo \(undone.count.formatted()) Unchecked") { apply() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(Space.xl)
        .frame(width: 680, height: 560)
    }

    private func change(_ from: String, _ to: String) -> some View {
        HStack(spacing: 4) {
            Text(from).strikethrough().foregroundStyle(.secondary)
            Image(systemName: "arrow.right").font(.caption2).foregroundStyle(.secondary)
            Text(to).fontWeight(.medium)
        }
        .font(.caption)
    }

    private func apply() {
        if !undone.isEmpty {
            let ids = undone
            let before = review.before
            let added = review.addedCharacters
            document.change("Undo Claude’s Changes", undoManager: undoManager) { p in
                AIReview.undo(ids, before: before, addedCharacters: added, in: &p)
            }
        }
        close()
    }

    private func close() { document.tagging.review = nil }
}
