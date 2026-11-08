import AppKit
import InnerVoiceKit
import SwiftUI

/// Pick who says a line: type to filter, arrows and Return, or 1–9.
struct AssignPopover: View {
    let document: InnerVoiceDocument
    let lineID: UUID
    let undoManager: UndoManager?
    var onDone: (_ assigned: UUID?, _ previous: UUID?) -> Void
    var onClose: () -> Void

    @State private var query = ""
    @State private var highlighted = 0
    @FocusState private var focused: Bool

    private var line: Line? { document.index.lineIndexByID[lineID].map { document.project.lines[$0] } }

    /// Most used in this chapter first, then the rest by lines.
    private var ordered: [BookCharacter] {
        let project = document.project
        let index = document.index
        guard let line, let pos = index.positions[line.paragraphID] else { return project.characters }
        let chapterIDs = Set(project.chapters[pos.chapter].paragraphs.map(\.id))
        var inChapter: [UUID: Int] = [:]
        for l in project.lines where chapterIDs.contains(l.paragraphID) {
            if let s = l.speakerID { inChapter[s, default: 0] += 1 }
        }
        return project.characters.sorted {
            let a = inChapter[$0.id] ?? 0, b = inChapter[$1.id] ?? 0
            if a != b { return a > b }
            return (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0)
        }
    }

    private var filtered: [BookCharacter] {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return ordered }
        return ordered.filter { c in ([c.name] + c.aliases).contains { $0.localizedCaseInsensitiveContains(q) } }
    }

    private var canCreate: Bool {
        let q = query.trimmingCharacters(in: .whitespaces)
        return !q.isEmpty && !document.project.characters.contains { $0.name.caseInsensitiveCompare(q) == .orderedSame }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()
            TextField("Find or add a character", text: $query)
                .textFieldStyle(.plain)
                .font(.body)
                .padding(.horizontal, Space.md)
                .padding(.vertical, Space.sm)
                .focused($focused)
                .onSubmit { commit() }
                .onChange(of: query) { highlighted = 0 }
            Divider()
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(spacing: 2) {
                        ForEach(Array(filtered.prefix(60).enumerated()), id: \.element.id) { i, c in
                            row(c, number: i < 9 && query.isEmpty ? i + 1 : nil, highlighted: i == highlighted)
                                .id(i)
                                .onTapGesture { assign(c.id) }
                        }
                        if canCreate {
                            Button {
                                assignNew()
                            } label: {
                                Label("New Character “\(query.trimmingCharacters(in: .whitespaces))”", systemImage: "plus.circle")
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(.horizontal, Space.sm)
                                    .padding(.vertical, Space.xs)
                                    .background(highlighted == filtered.count ? Color.accentColor.opacity(0.18) : .clear,
                                                in: RoundedRectangle(cornerRadius: 6))
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .padding(Space.xs)
                }
                .frame(height: min(300, CGFloat(filtered.count + (canCreate ? 1 : 0)) * 30 + 12))
                .onChange(of: highlighted) { proxy.scrollTo(highlighted) }
            }
            Divider()
            HStack {
                Button("Who's Speaking?") { assign(nil) }
                    .help("Clear the speaker; the line shows in red")
                Spacer()
                Button("Narration") {
                    document.removeLines([lineID], undoManager: undoManager)
                    onClose()
                }
                .help("This text isn't a line; read it in the narrator's voice")
            }
            .controlSize(.small)
            .buttonStyle(.borderless)
            .padding(.horizontal, Space.md)
            .padding(.vertical, Space.sm)
        }
        .frame(width: 300)
        .onAppear { focused = true }
        .onKeyPress(.downArrow) { highlighted = min(highlighted + 1, filtered.count - (canCreate ? 0 : 1)); return .handled }
        .onKeyPress(.upArrow) { highlighted = max(0, highlighted - 1); return .handled }
        .onKeyPress(.escape) { onClose(); return .handled }
        .onKeyPress(characters: .decimalDigits) { press in
            guard query.isEmpty, let n = Int(press.characters), n >= 1, n <= min(9, filtered.count) else { return .ignored }
            assign(filtered[n - 1].id)
            return .handled
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: Space.xs) {
            Text("Assign To")
                .font(.headline)
            if let line {
                Text("“" + document.project.text(of: line, index: document.index) + "”")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Text(line.source == .none ? "Not assigned yet" : line.source.title + (line.source == .ai ? " (\(Int(line.confidence * 100))% sure)" : ""))
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
        }
        .padding(Space.md)
    }

    private func row(_ c: BookCharacter, number: Int?, highlighted: Bool) -> some View {
        let current = line?.speakerID == c.id
        return HStack(spacing: Space.sm) {
            Circle().fill(Color(hexString: c.colorHex)).frame(width: 10, height: 10)
            Text(c.name).lineLimit(1)
            if current { Image(systemName: "checkmark").font(.caption.bold()).foregroundStyle(.secondary) }
            Spacer()
            if let number {
                Text("\(number)")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.tertiary)
            }
        }
        .padding(.horizontal, Space.sm)
        .padding(.vertical, Space.xs)
        .frame(height: 26)
        .contentShape(Rectangle())
        .background(highlighted ? Color.accentColor.opacity(0.18) : .clear, in: RoundedRectangle(cornerRadius: 6))
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
        .accessibilityHint(number.map { "Press \($0)" } ?? "")
    }

    private func commit() {
        if highlighted < filtered.count {
            assign(filtered[highlighted].id)
        } else if canCreate {
            assignNew()
        }
    }

    private func assignNew() {
        let id = document.addCharacter(named: query, undoManager: undoManager)
        assign(id)
    }

    private func assign(_ id: UUID?) {
        let previous = line?.speakerID
        document.assign([lineID], to: id, undoManager: undoManager)
        onDone(id, previous)
    }
}

/// Shows SwiftUI content in an NSPopover anchored to a rect in an AppKit view.
@MainActor
final class PopoverPresenter: NSObject, NSPopoverDelegate {
    static let shared = PopoverPresenter()
    private var popover: NSPopover?

    func show<Content: View>(_ content: Content, at rect: NSRect, in view: NSView) {
        popover?.close()
        let p = NSPopover()
        p.behavior = .transient
        p.animates = true
        p.contentViewController = NSHostingController(rootView: content.themed())
        p.delegate = self
        p.show(relativeTo: rect, of: view, preferredEdge: .maxY)
        popover = p
    }

    func close() {
        popover?.close()
        popover = nil
    }

    nonisolated func popoverDidClose(_ notification: Notification) {
        MainActor.assumeIsolated { popover = nil }
    }
}
