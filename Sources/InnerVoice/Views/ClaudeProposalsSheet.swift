import InnerVoiceKit
import SwiftUI

/// What Claude proposed in Clean Up Text, Write Chapter Recaps, Suggest Delivery Hints or
/// Suggest Spellings, to keep or leave out one by one. Nothing changes until Apply, which is
/// one Undo step. Spellings start unchecked: the narrator confirms each one.
struct ClaudeProposalsSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    let pending: ClaudeFeatures.Pending
    @Environment(\.undoManager) private var undoManager
    /// Ids left out (or, for spellings, the ones kept).
    @State private var marked: Set<UUID> = []
    /// Edited text: recaps, hints and spellings can be changed before they're used.
    @State private var edits: [UUID: String] = [:]

    private var confirmEach: Bool { if case .spellings = pending.proposals { true } else { false } }

    private var ids: [UUID] {
        switch pending.proposals {
        case .cleanup(let a): a.map(\.id)
        case .recaps(let a): a.map(\.id)
        case .delivery(let a): a.map(\.id)
        case .spellings(let a): a.map(\.id)
        }
    }

    private func kept(_ id: UUID) -> Bool { confirmEach ? marked.contains(id) : !marked.contains(id) }
    private var keptCount: Int { ids.filter(kept).count }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.md) {
            HStack(spacing: Space.sm) {
                Text(title).font(.title2.weight(.semibold))
                CloudBadge(service: "Anthropic")
                Spacer()
            }
            Text(explanation)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            list.frame(minHeight: 300)
            HStack {
                Button(allKept ? "Uncheck All" : "Check All") { toggleAll() }
                Spacer()
                Button("Cancel") { close() }
                    .keyboardShortcut(.cancelAction)
                Button(applyTitle) { apply() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(keptCount == 0)
            }
        }
        .padding(Space.xl)
        .frame(width: 720, height: 580)
    }

    private var allKept: Bool { keptCount == ids.count }

    private func toggleAll() {
        if confirmEach { marked = allKept ? [] : Set(ids) } else { marked = allKept ? Set(ids) : [] }
    }

    private var title: String {
        switch pending.proposals {
        case .cleanup: "Review Text Fixes"
        case .recaps: "Review Chapter Recaps"
        case .delivery: "Review Delivery Hints"
        case .spellings: "Suggested Spellings"
        }
    }

    private var explanation: String {
        let n = pending.proposals.count.formatted()
        return switch pending.proposals {
        case .cleanup: "\(pending.model) found \(n) import problems. Uncheck any fix you don’t want. Only the project changes; your manuscript file never does, and lines you assigned keep their speakers."
        case .recaps: "\(pending.model) wrote \(n) “Previously…” recaps. Edit or uncheck any you don’t want. Recaps you wrote yourself were kept."
        case .delivery: "\(pending.model) suggested how \(n) lines are performed, beyond their tone. Hints go to ElevenLabs only, as audio tags. Edit or uncheck any you don’t want; your own hints were kept."
        case .spellings: "\(pending.model) suggested \(n) Say It As spellings. Check each one you want to use, after hearing or checking it. Confirmed (checked with the author) stays yours to tick."
        }
    }

    private var applyTitle: String {
        let n = keptCount.formatted()
        return switch pending.proposals {
        case .cleanup: keptCount == 1 ? "Apply 1 Fix" : "Apply \(n) Fixes"
        case .recaps: keptCount == 1 ? "Use 1 Recap" : "Use \(n) Recaps"
        case .delivery: keptCount == 1 ? "Use 1 Hint" : "Use \(n) Hints"
        case .spellings: keptCount == 1 ? "Use 1 Spelling" : "Use \(n) Spellings"
        }
    }

    private func toggle(_ id: UUID) -> Binding<Bool> {
        Binding(get: { kept(id) }, set: { on in
            if on == confirmEach { marked.insert(id) } else { marked.remove(id) }
        })
    }

    private func editable(_ id: UUID, _ original: String) -> Binding<String> {
        Binding(get: { edits[id] ?? original }, set: { edits[id] = $0 })
    }

    @ViewBuilder private var list: some View {
        let p = document.project
        let index = document.index
        switch pending.proposals {
        case .cleanup(let fixes):
            List(fixes) { fix in
                row(fix.id) {
                    HStack(spacing: Space.sm) {
                        Text(index.reference(fix.paragraphID)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        Text(fix.issue.title).font(.caption.weight(.medium))
                        if !fix.note.isEmpty { Text(fix.note).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
                    }
                    let text = p.paragraph(fix.paragraphID, index: index)?.text ?? fix.original
                    let shown = TextCleanup.preview(fix, in: text)
                    Text(shown.before).strikethrough().foregroundStyle(.secondary).font(.callout).lineLimit(3)
                    Text(shown.after.isEmpty ? "(removed)" : shown.after).font(.callout).lineLimit(3)
                }
            }
        case .recaps(let recaps):
            List(recaps) { r in
                row(r.id) {
                    Text(p.chapters.first { $0.id == r.chapterID }?.title ?? "Chapter").font(.caption.weight(.medium))
                    if let before = r.before {
                        Text(before).strikethrough().foregroundStyle(.secondary).font(.callout).lineLimit(2)
                    }
                    TextField("Recap", text: editable(r.id, r.text), axis: .vertical)
                        .textFieldStyle(.plain)
                        .font(.callout)
                        .lineLimit(2...5)
                }
            }
        case .delivery(let hints):
            List(hints) { h in
                let line = index.lineIndexByID[h.lineID].map { p.lines[$0] }
                row(h.id) {
                    HStack(spacing: Space.sm) {
                        if let line { Text(index.reference(line.paragraphID)).font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
                        Text(p.character(line?.speakerID)?.name ?? "").font(.caption.weight(.medium))
                        if let t = line?.tone, t != .neutral { Text(t.title).font(.caption).foregroundStyle(.secondary) }
                    }
                    if let line { Text(p.text(of: line, index: index)).font(.callout).lineLimit(2) }
                    HStack(spacing: Space.xs) {
                        Image(systemName: "theatermasks").foregroundStyle(.secondary).font(.caption)
                        TextField("Delivery", text: editable(h.id, h.hint))
                            .textFieldStyle(.roundedBorder)
                            .frame(maxWidth: 260)
                        if let before = h.before { Text("was “\(before)”").font(.caption).foregroundStyle(.secondary) }
                    }
                }
            }
        case .spellings(let spellings):
            List(spellings) { s in
                row(s.id) {
                    HStack(spacing: Space.sm) {
                        Text(s.term).font(.body.weight(.medium))
                        Image(systemName: "arrow.right").font(.caption2).foregroundStyle(.secondary)
                        TextField("Say It As", text: editable(s.id, s.sayItAs))
                            .textFieldStyle(.roundedBorder)
                            .frame(maxWidth: 200)
                        Text("\(s.confidence)% sure").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                    }
                    if !s.note.isEmpty { Text(s.note).font(.caption).foregroundStyle(.secondary) }
                }
            }
        }
    }

    private func row<Content: View>(_ id: UUID, @ViewBuilder content: () -> Content) -> some View {
        HStack(alignment: .top, spacing: Space.md) {
            Toggle(confirmEach ? "Use" : "Keep", isOn: toggle(id)).labelsHidden()
            VStack(alignment: .leading, spacing: 3) { content() }
        }
        .padding(.vertical, 2)
    }

    private func apply() {
        let take: ClaudeFeatures.Proposals = switch pending.proposals {
        case .cleanup(let a): .cleanup(a.filter { kept($0.id) })
        case .recaps(let a): .recaps(a.filter { kept($0.id) }.map { var r = $0; r.text = edits[r.id] ?? r.text; return r }
            .filter { !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty })
        case .delivery(let a): .delivery(a.filter { kept($0.id) }.compactMap { var h = $0; guard let c = DeliveryPrompt.clean(edits[h.id] ?? h.hint) else { return nil }; h.hint = c; return h })
        case .spellings(let a): .spellings(a.filter { kept($0.id) }.map { var s = $0; s.sayItAs = (edits[s.id] ?? s.sayItAs).trimmingCharacters(in: .whitespaces); return s }
            .filter { !$0.sayItAs.isEmpty })
        }
        document.claude.apply(take, undoManager: undoManager)
        close()
    }

    private func close() { document.claude.pending = nil }
}
