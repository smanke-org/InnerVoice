import AppKit
import InnerVoiceKit
import SwiftUI

/// The teleprompter for the booth: one paragraph at a time in large type, the speaker's
/// color and voice cue, the next speaker below. Space or → moves on, ← goes back,
/// R marks a retake. Foot pedals that send keys work too.
struct RecordPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @AppStorage("record.fontSize") private var fontSize = 30.0
    @AppStorage("record.markOnAdvance") private var markOnAdvance = true
    @AppStorage("record.nextKey") private var nextKey = "space"
    @State private var retakeNote = ""
    @State private var showRetake = false
    @FocusState private var focused: Bool

    /// Every readable paragraph in book order (headings and scene breaks are skipped).
    private var paragraphs: [(chapter: Int, paragraph: Paragraph)] {
        document.project.chapters.enumerated().flatMap { c, ch in
            ch.paragraphs.filter { $0.style == .body || $0.style == .blockQuote }.map { (c, $0) }
        }
    }

    private var currentIndex: Int {
        let all = paragraphs
        if let id = workspace.recordParagraph, let i = all.firstIndex(where: { $0.paragraph.id == id }) { return i }
        // Start at the first paragraph not yet recorded.
        return all.firstIndex { !document.project.recorded.contains($0.paragraph.id) } ?? 0
    }

    var body: some View {
        let all = paragraphs
        let i = currentIndex
        VStack(spacing: 0) {
            progressHeader(all: all, i: i)
            Divider()
            if all.isEmpty {
                ContentUnavailableView("Nothing to Read", systemImage: "mic.slash")
            } else {
                prompter(all: all, i: i)
            }
            Divider()
            controls(all: all, i: i)
        }
        .background(Color(nsColor: .textBackgroundColor))
        .navigationTitle("Record")
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onAppear { focused = true }
        .onKeyPress(keys: [.space, .rightArrow, .downArrow, .pageDown, .return]) { press in
            guard matchesNext(press.key) || press.key == .rightArrow else { return .ignored }
            move(1, all: all)
            return .handled
        }
        .onKeyPress(keys: [.leftArrow, .upArrow, .pageUp]) { _ in
            move(-1, all: all)
            return .handled
        }
        .onKeyPress(characters: CharacterSet(charactersIn: "rR")) { _ in
            showRetake = true
            return .handled
        }
        .onKeyPress(characters: CharacterSet(charactersIn: "mM")) { _ in
            if all.indices.contains(i) { document.toggleRecorded(all[i].paragraph.id, undoManager: undoManager) }
            return .handled
        }
    }

    private func matchesNext(_ key: KeyEquivalent) -> Bool {
        switch nextKey {
        case "space": key == .space
        case "down": key == .downArrow
        case "pagedown": key == .pageDown
        case "return": key == .return
        default: key == .space
        }
    }

    private func move(_ delta: Int, all: [(chapter: Int, paragraph: Paragraph)]) {
        let i = currentIndex
        if delta > 0, markOnAdvance, all.indices.contains(i) {
            document.setRecorded(all[i].paragraph.id, true, undoManager: undoManager)
        }
        let next = max(0, min(all.count - 1, i + delta))
        withAnimation(.easeOut(duration: 0.18)) { workspace.recordParagraph = all[next].paragraph.id }
    }

    // MARK: Parts

    private func progressHeader(all: [(chapter: Int, paragraph: Paragraph)], i: Int) -> some View {
        let chapterIndex = all.indices.contains(i) ? all[i].chapter : 0
        let inChapter = all.filter { $0.chapter == chapterIndex }
        let done = inChapter.filter { document.project.recorded.contains($0.paragraph.id) }.count
        let total = all.filter { document.project.recorded.contains($0.paragraph.id) }.count
        return HStack(spacing: Space.xl) {
            VStack(alignment: .leading, spacing: 2) {
                Text(document.project.chapters.indices.contains(chapterIndex) ? document.project.chapters[chapterIndex].title : "")
                    .font(.headline)
                Text("Paragraph \(i + 1) of \(all.count)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            VStack(alignment: .trailing, spacing: Space.xs) {
                Text("\(Int(Double(done) / Double(max(1, inChapter.count)) * 100))% of chapter · \(Int(Double(total) / Double(max(1, all.count)) * 100))% of book recorded")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                ProgressView(value: Double(done), total: Double(max(1, inChapter.count)))
                    .frame(width: 220)
            }
        }
        .padding(.horizontal, Space.xl)
        .padding(.vertical, Space.md)
    }

    private func prompter(all: [(chapter: Int, paragraph: Paragraph)], i: Int) -> some View {
        let current = all[i].paragraph
        let lines = (document.index.linesByParagraph[current.id] ?? []).map { document.project.lines[$0] }
        let speakers = uniqueSpeakers(lines)
        let next = all.indices.contains(i + 1) ? all[i + 1].paragraph : nil
        let nextSpeakers = next.map { p in uniqueSpeakers((document.index.linesByParagraph[p.id] ?? []).map { document.project.lines[$0] }) } ?? []
        return ScrollView {
            VStack(alignment: .leading, spacing: Space.xxl) {
                if i > 0 {
                    Text(all[i - 1].paragraph.text)
                        .font(.system(size: fontSize * 0.55, design: ReadingStyle.serif ? .serif : .default))
                        .foregroundStyle(.tertiary)
                        .lineLimit(3)
                }
                if !speakers.isEmpty {
                    HStack(spacing: Space.sm) {
                        ForEach(speakers, id: \.self) { id in speakerChip(id) }
                    }
                }
                Text(attributed(current, lines: lines))
                    .font(.system(size: fontSize, design: ReadingStyle.serif ? .serif : .default))
                    .lineSpacing(fontSize * 0.35)
                    .textSelection(.enabled)
                    .id(current.id)
                    .transition(.opacity)
                if document.project.recorded.contains(current.id) {
                    Label("Recorded", systemImage: "checkmark.circle.fill")
                        .font(.callout)
                        .foregroundStyle(.green)
                }
                if let r = document.project.retakes.first(where: { $0.paragraphID == current.id && !$0.done }) {
                    Label("Retake" + (r.note.isEmpty ? "" : ": \(r.note)"), systemImage: "arrow.counterclockwise.circle.fill")
                        .font(.callout)
                        .foregroundStyle(.orange)
                }
                if let next {
                    VStack(alignment: .leading, spacing: Space.xs) {
                        Text(nextSpeakers.isEmpty ? "Next: narration" : "Next: " + nextSpeakers.compactMap { document.project.character($0)?.name }.joined(separator: ", "))
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.secondary)
                        Text(next.text)
                            .font(.system(size: fontSize * 0.55, design: ReadingStyle.serif ? .serif : .default))
                            .foregroundStyle(.tertiary)
                            .lineLimit(2)
                    }
                    .padding(.top, Space.lg)
                }
            }
            .padding(.horizontal, Space.section * 2)
            .padding(.vertical, Space.section)
            .frame(maxWidth: 960, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
    }

    private func uniqueSpeakers(_ lines: [Line]) -> [UUID] {
        var seen: [UUID] = []
        for l in lines { if let s = l.speakerID, !seen.contains(s) { seen.append(s) } }
        return seen
    }

    private func speakerChip(_ id: UUID) -> some View {
        let c = document.project.character(id)
        let cue = c.map { document.voiceCue(for: $0) } ?? ""
        return HStack(spacing: Space.sm) {
            if let c { Avatar(character: c, size: 26) }
            VStack(alignment: .leading, spacing: 0) {
                Text(c?.name ?? "").font(.headline)
                if !cue.isEmpty { Text(cue).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
            }
        }
        .padding(.horizontal, Space.md)
        .padding(.vertical, Space.sm)
        .background(Color(hexString: c?.colorHex ?? "#888888").opacity(0.14), in: Capsule())
    }

    /// The paragraph with each line tinted in its speaker's color.
    private func attributed(_ p: Paragraph, lines: [Line]) -> AttributedString {
        var s = AttributedString(p.text)
        let chars = Array(p.text.utf16)
        for line in lines {
            guard line.range.end <= chars.count else { continue }
            let color: Color = document.project.needsYou(line) ? Color(nsColor: .attention) : Color(hexString: document.project.character(line.speakerID)?.colorHex ?? "#888888")
            let startIdx = String.Index(utf16Offset: line.range.location, in: p.text)
            let endIdx = String.Index(utf16Offset: line.range.end, in: p.text)
            guard let lo = AttributedString.Index(startIdx, within: s), let hi = AttributedString.Index(endIdx, within: s) else { continue }
            s[lo..<hi].backgroundColor = color.opacity(0.22)
            if line.kind == .thought { s[lo..<hi].underlineStyle = .init(pattern: .dash, color: color) }
        }
        return s
    }

    private func controls(all: [(chapter: Int, paragraph: Paragraph)], i: Int) -> some View {
        HStack(spacing: Space.md) {
            Button("Previous", systemImage: "chevron.left") { move(-1, all: all) }
                .help("Go back a paragraph (←)")
            Button("Next", systemImage: "chevron.right") { move(1, all: all) }
                .help("Move on and mark this paragraph recorded (Space or →)")
                .buttonStyle(.borderedProminent)
            Spacer()
            Button("Retake", systemImage: "arrow.counterclockwise") { showRetake = true }
                .help("Mark this paragraph to read again, with a note (R)")
                .popover(isPresented: $showRetake) {
                    VStack(alignment: .leading, spacing: Space.md) {
                        Text("Mark Retake").font(.headline)
                        TextField("What to fix (optional)", text: $retakeNote)
                            .frame(width: 260)
                            .onSubmit { saveRetake(all: all, i: i) }
                        HStack {
                            Spacer()
                            Button("Cancel") { showRetake = false }.keyboardShortcut(.cancelAction)
                            Button("Mark Retake") { saveRetake(all: all, i: i) }.keyboardShortcut(.defaultAction)
                        }
                    }
                    .padding(Space.lg)
                }
            Toggle("Recorded", isOn: Binding(get: { all.indices.contains(i) && document.project.recorded.contains(all[i].paragraph.id) },
                                             set: { v in if all.indices.contains(i) { document.setRecorded(all[i].paragraph.id, v, undoManager: undoManager) } }))
                .toggleStyle(.button)
                .help("Mark this paragraph recorded (M)")
            Menu("Text Size", systemImage: "textformat.size") {
                Button("Larger") { fontSize = min(64, fontSize + 4) }
                Button("Smaller") { fontSize = max(18, fontSize - 4) }
            }
            .fixedSize()
            .help("Change the size of the text")
        }
        .padding(.horizontal, Space.xl)
        .padding(.vertical, Space.md)
    }

    private func saveRetake(all: [(chapter: Int, paragraph: Paragraph)], i: Int) {
        guard all.indices.contains(i) else { return }
        document.addRetake(all[i].paragraph.id, note: retakeNote, undoManager: undoManager)
        retakeNote = ""
        showRetake = false
        focused = true
    }
}

struct RetakesPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State private var showDone = false

    var body: some View {
        let retakes = document.project.retakes.filter { showDone || !$0.done }
            .sorted { (document.index.positions[$0.paragraphID]?.global ?? 0) < (document.index.positions[$1.paragraphID]?.global ?? 0) }
        Group {
            if retakes.isEmpty {
                ContentUnavailableView("No Retakes", systemImage: "arrow.counterclockwise",
                                       description: Text("Press R in Record to mark a paragraph to read again."))
            } else {
                List {
                    ForEach(retakes) { r in
                        HStack(alignment: .firstTextBaseline, spacing: Space.md) {
                            Toggle("Done", isOn: Binding(get: { r.done }, set: { v in
                                if v, !r.done { ActivityStore.shared.record(ActivityKey.retakesCleared) }
                                document.change(v ? "Finish Retake" : "Reopen Retake", undoManager: undoManager) { p in
                                    if let i = p.retakes.firstIndex(where: { $0.id == r.id }) { p.retakes[i].done = v }
                                }
                            }))
                            .labelsHidden()
                            .toggleStyle(.checkbox)
                            VStack(alignment: .leading, spacing: Space.xs) {
                                Text(document.index.reference(r.paragraphID)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                                Text(r.note.isEmpty ? "No note" : r.note).foregroundStyle(r.note.isEmpty ? .secondary : .primary)
                                if let p = document.project.paragraph(r.paragraphID, index: document.index) {
                                    Text(p.text).font(.callout).foregroundStyle(.secondary).lineLimit(2)
                                }
                            }
                            Spacer()
                            Button("Record") {
                                workspace.recordParagraph = r.paragraphID
                                workspace.page = .record
                            }
                            .controlSize(.small)
                            Button("Show") { workspace.show(paragraph: r.paragraphID) }
                                .controlSize(.small)
                        }
                        .padding(.vertical, Space.xs)
                        .contextMenu {
                            Button("Delete Retake", role: .destructive) {
                                document.change("Delete Retake", undoManager: undoManager) { $0.retakes.removeAll { $0.id == r.id } }
                            }
                        }
                    }
                }
                .listStyle(.inset)
            }
        }
        .navigationTitle("Retakes")
        .toolbar {
            ToolbarItem {
                Toggle("Show Done", systemImage: "checkmark.circle", isOn: $showDone)
                    .help("Show retakes you have finished")
            }
        }
    }
}

/// What a revised manuscript changed, to look through one by one.
struct RevisionsPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        if let record = document.project.revisions.last {
            List {
                Section {
                    HStack(spacing: Space.xxl) {
                        stat(record.unchanged, "unchanged", .secondary)
                        stat(record.changed.count, "changed", .orange)
                        stat(record.added.count, "added", .green)
                        stat(record.removed.count, "removed", Color(nsColor: .attention))
                    }
                    .padding(.vertical, Space.sm)
                    Text("Tags, notes, recorded marks and retakes were kept on unchanged paragraphs. Changed paragraphs keep the speakers of lines whose words still match; their recorded marks were cleared.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    if document.project.untaggedCount > 0 {
                        Button("Tag New Lines") { TagCommand.run(document: document, workspace: workspace, undoManager: undoManager) }
                            .buttonStyle(.borderedProminent)
                    }
                } header: {
                    Text("“\(record.fileName)”, \(record.date.formatted(date: .abbreviated, time: .shortened))")
                }
                paragraphSection("Changed", ids: record.changed, record: record)
                paragraphSection("Added", ids: record.added, record: record)
                if !record.removed.isEmpty {
                    Section("Removed") {
                        ForEach(Array(record.removed.enumerated()), id: \.offset) { _, r in
                            VStack(alignment: .leading, spacing: 2) {
                                Text(r.chapterTitle).font(.caption).foregroundStyle(.secondary)
                                Text(r.text).strikethrough().foregroundStyle(.secondary).lineLimit(3)
                            }
                        }
                    }
                }
            }
            .listStyle(.inset)
            .navigationTitle("Revisions")
        } else {
            ContentUnavailableView("No Revisions", systemImage: "doc.badge.clock",
                                   description: Text("Choose File › Import Revised Manuscript… when the author sends a new draft."))
        }
    }

    private func stat(_ n: Int, _ label: String, _ tint: Color) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("\(n)").font(.title2.weight(.semibold).monospacedDigit()).foregroundStyle(tint)
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private func paragraphSection(_ title: String, ids: [UUID], record: RevisionRecord) -> some View {
        if !ids.isEmpty {
            Section(title) {
                ForEach(ids.filter { document.index.positions[$0] != nil }, id: \.self) { id in
                    Button {
                        markReviewed(id)
                        workspace.show(paragraph: id)
                    } label: {
                        HStack(alignment: .firstTextBaseline, spacing: Space.md) {
                            Image(systemName: record.reviewed.contains(id) ? "checkmark.circle.fill" : "circle")
                                .foregroundStyle(record.reviewed.contains(id) ? Color.green : Color.secondary)
                            Text(document.index.reference(id)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                                .frame(width: 72, alignment: .leading)
                            Text(document.project.paragraph(id, index: document.index)?.text ?? "").lineLimit(2)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private func markReviewed(_ id: UUID) {
        document.change("Review Change", undoManager: undoManager) { p in
            if !p.revisions.isEmpty { p.revisions[p.revisions.count - 1].reviewed.insert(id) }
        }
    }
}
