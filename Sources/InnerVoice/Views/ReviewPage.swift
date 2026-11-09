import InnerVoiceKit
import SwiftUI

/// Every line that needs the narrator, one after another with the text around it: the
/// AI's unsure guesses first. Return confirms the guess, typing or 1–9 picks someone else,
/// 0 makes it narration, ↓ and ↑ move between lines. A settled line leaves the list and
/// the next one comes up.
struct ReviewPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager

    enum Filter: String, CaseIterable, Identifiable {
        case guesses, noSpeaker, all
        var id: String { rawValue }
        var title: String {
            switch self {
            case .guesses: "Unsure Guesses"
            case .noSpeaker: "Who's Speaking?"
            case .all: "All"
            }
        }
    }

    @SceneStorage("review.filter") private var filterRaw = Filter.guesses.rawValue
    @State private var selection: UUID?
    @State private var query = ""
    @FocusState private var focused: Bool
    @AppStorage("reading.serif", store: ReadingStyle.store) private var serif = true

    private var filter: Filter { Filter(rawValue: filterRaw) ?? .guesses }

    private var needing: [Line] { document.project.lines.filter { document.project.needsYou($0) } }

    private func items(_ f: Filter) -> [Line] {
        switch f {
        case .guesses: needing.filter { $0.speakerID != nil }
        case .noSpeaker: needing.filter { $0.speakerID == nil }
        case .all: needing
        }
    }

    var body: some View {
        let items = items(filter)
        Group {
            if needing.isEmpty {
                ContentUnavailableView("Nothing to Review", systemImage: "checkmark.circle",
                                       description: Text("Every line has a speaker you confirmed or one the AI is sure of."))
            } else {
                HStack(spacing: 0) {
                    list(items)
                        .frame(width: 300)
                    Divider()
                    if let id = selection, let line = items.first(where: { $0.id == id }) {
                        card(line, items: items)
                    } else if items.isEmpty, let other = Filter.allCases.first(where: { $0 != filter && !self.items($0).isEmpty }) {
                        ContentUnavailableView {
                            Label(filter == .guesses ? "No Unsure Guesses Left" : "No Lines Here", systemImage: "checkmark.circle")
                        } description: {
                            Text("\(self.items(other).count) more lines need you.")
                        } actions: {
                            Button("Review \(other.title)") { filterRaw = other.rawValue }
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        ContentUnavailableView("Choose a Line", systemImage: "line.3.horizontal.decrease",
                                               description: Text("Pick a line in the list to review it."))
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    }
                }
            }
        }
        .navigationTitle("Review")
        .navigationSubtitle(needing.isEmpty ? "" : "\(needing.count) lines need you")
        .onAppear { selectFirstIfNeeded(items) }
        .onChange(of: filterRaw) { selection = nil; selectFirstIfNeeded(self.items(filter)) }
    }

    private func selectFirstIfNeeded(_ items: [Line]) {
        if selection == nil || !items.contains(where: { $0.id == selection }) { selection = items.first?.id }
        focused = true
    }

    // MARK: The list

    private func list(_ items: [Line]) -> some View {
        VStack(spacing: 0) {
            Picker("Show", selection: $filterRaw) {
                ForEach(Filter.allCases) { f in
                    Text("\(f.title) (\(self.items(f).count))").tag(f.rawValue)
                }
            }
            .labelsHidden()
            .padding(Space.md)
            .help("Which lines to review")
            Divider()
            List(items, selection: $selection) { line in
                row(line).tag(line.id)
            }
            .listStyle(.inset)
        }
    }

    private func row(_ line: Line) -> some View {
        let project = document.project
        let guess = project.character(line.speakerID)
        return HStack(alignment: .firstTextBaseline, spacing: Space.sm) {
            Text(guess.map { "\($0.firstName)?" } ?? "?")
                .font(.caption.weight(.semibold))
                .foregroundStyle(Color(nsColor: .attention))
                .padding(.horizontal, 6)
                .padding(.vertical, 1)
                .background(Color(nsColor: .attention).opacity(0.14), in: Capsule())
            VStack(alignment: .leading, spacing: 2) {
                Text(document.index.reference(line.paragraphID))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                Text(project.text(of: line, index: document.index))
                    .lineLimit(2)
            }
        }
        .padding(.vertical, 2)
    }

    // MARK: The line

    private func card(_ line: Line, items: [Line]) -> some View {
        let project = document.project
        let guess = project.character(line.speakerID)
        let position = (items.firstIndex { $0.id == line.id } ?? 0) + 1
        return ScrollView {
            VStack(alignment: .leading, spacing: Space.lg) {
                HStack {
                    Text("\(document.index.reference(line.paragraphID)) · \(position) of \(items.count)")
                        .font(.callout.monospacedDigit())
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Show in Manuscript") { workspace.show(paragraph: line.paragraphID, line: line.id) }
                        .help("See this line in the manuscript")
                }
                context(line)
                Divider()
                decide(line, guess: guess, items: items)
            }
            .padding(Space.xl)
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .id(line.id)
    }

    /// The paragraph before, the line's own paragraph with the line marked, and the one after.
    private func context(_ line: Line) -> some View {
        let project = document.project
        let index = document.index
        let pos = index.positions[line.paragraphID]
        let paragraphs = pos.map { project.chapters[$0.chapter].paragraphs } ?? []
        let p = pos?.paragraph ?? 0
        let before = p > 0 ? paragraphs[p - 1] : nil
        let after = p + 1 < paragraphs.count ? paragraphs[p + 1] : nil
        let font = Font.system(size: 16, design: serif ? .serif : .default)
        return VStack(alignment: .leading, spacing: Space.md) {
            if let before { neighbor(before, font: font) }
            if let own = project.paragraph(line.paragraphID, index: index) {
                marked(own, line: line)
                    .font(font)
                    .lineSpacing(4)
                    .textSelection(.enabled)
            }
            if let after { neighbor(after, font: font) }
        }
        .padding(Space.lg)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.separator))
    }

    /// A paragraph next to the line, dimmed, with who speaks in it.
    private func neighbor(_ paragraph: Paragraph, font: Font) -> some View {
        let project = document.project
        let speakers = (document.index.linesByParagraph[paragraph.id] ?? []).compactMap { project.character(project.lines[$0].speakerID) }
        var seen = Set<UUID>()
        let unique = speakers.filter { seen.insert($0.id).inserted }
        return VStack(alignment: .leading, spacing: 2) {
            if !unique.isEmpty {
                HStack(spacing: Space.sm) {
                    ForEach(unique) { c in
                        Label { Text(c.name) } icon: { Circle().fill(Color(hexString: c.colorHex)).frame(width: 7, height: 7) }
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            Text(paragraph.text)
                .font(font)
                .foregroundStyle(.secondary)
                .lineLimit(8)
                .lineSpacing(3)
        }
    }

    /// The line's paragraph with the line itself bold and underlined in red.
    private func marked(_ paragraph: Paragraph, line: Line) -> Text {
        let ns = paragraph.text as NSString
        let r = NSIntersectionRange(line.range.ns, NSRange(location: 0, length: ns.length))
        let head = ns.substring(to: r.location)
        let body = ns.substring(with: r)
        let tail = ns.substring(from: NSMaxRange(r))
        let red = Color(nsColor: .attention)
        return Text(head) + Text(body).bold().underline(color: red) + Text(tail)
    }

    // MARK: Deciding

    private func speakers(_ guess: BookCharacter?) -> [BookCharacter] {
        let order = document.project.speakerOrder
        let all = order.pinned + order.others
        let q = query.trimmingCharacters(in: .whitespaces)
        guard q.isEmpty else { return all.filter { $0.matches(q) } }
        // The guess leads, so 1 picks it too.
        guard let guess else { return all }
        return [guess] + all.filter { $0.id != guess.id }
    }

    private func decide(_ line: Line, guess: BookCharacter?, items: [Line]) -> some View {
        let list = speakers(guess)
        return VStack(alignment: .leading, spacing: Space.md) {
            if let guess {
                HStack(spacing: Space.md) {
                    Text("Best guess: \(guess.name)").font(.headline)
                    Text("\(Int(line.confidence * 100))% sure").foregroundStyle(.secondary)
                    Spacer()
                    Button("Confirm \(guess.firstName)") { settle(line, items: items) { document.assign([line.id], to: guess.id, undoManager: undoManager) } }
                        .buttonStyle(.borderedProminent)
                        .help("Give the line to \(guess.name) (Return)")
                }
            } else {
                Text("Who says this?").font(.headline)
            }
            TextField("Find a speaker", text: $query)
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .onSubmit { commit(line, guess: guess, items: items) }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 170), spacing: Space.sm)], alignment: .leading, spacing: Space.sm) {
                ForEach(Array(list.prefix(40).enumerated()), id: \.element.id) { n, c in
                    Button {
                        settle(line, items: items) { document.assign([line.id], to: c.id, undoManager: undoManager) }
                    } label: {
                        HStack(spacing: Space.sm) {
                            Circle().fill(Color(hexString: c.colorHex)).frame(width: 9, height: 9)
                            Text(c.name).lineLimit(1)
                            if c.isPinned { Image(systemName: "pin.fill").font(.caption2).foregroundStyle(.secondary) }
                            Spacer(minLength: 0)
                            if n < 9, query.isEmpty {
                                Text("\(n + 1)").font(.caption.monospacedDigit()).foregroundStyle(.tertiary)
                            }
                        }
                        .padding(.horizontal, Space.sm)
                        .padding(.vertical, Space.xs)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(c.id == guess?.id ? Color.accentColor.opacity(0.15) : Color.secondary.opacity(0.08),
                                    in: RoundedRectangle(cornerRadius: 6))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help(c.id == guess?.id ? "The best guess: give the line to \(c.name)" : "Give the line to \(c.name)")
                }
            }
            HStack(spacing: Space.md) {
                Button("Narration (Not Spoken)") { settle(line, items: items) { document.removeLines([line.id], undoManager: undoManager) } }
                    .help("No one says this; read it as narration (0)")
                Button("Skip") { move(+1, items: items) }
                    .help("Leave this line for later (↓)")
                Spacer()
                Text("Return confirms · type or 1–9 picks · 0 narration · ↓ ↑ move")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
            .controlSize(.small)
        }
        .onKeyPress(.downArrow) { move(+1, items: items); return .handled }
        .onKeyPress(.upArrow) { move(-1, items: items); return .handled }
        .onKeyPress(.escape) {
            guard !query.isEmpty else { return .ignored }
            query = ""
            return .handled
        }
        .onKeyPress(characters: .decimalDigits) { press in
            guard query.isEmpty, let n = Int(press.characters) else { return .ignored }
            if n == 0 {
                settle(line, items: items) { document.removeLines([line.id], undoManager: undoManager) }
                return .handled
            }
            guard n <= min(9, list.count) else { return .ignored }
            let c = list[n - 1]
            settle(line, items: items) { document.assign([line.id], to: c.id, undoManager: undoManager) }
            return .handled
        }
    }

    private func commit(_ line: Line, guess: BookCharacter?, items: [Line]) {
        if !query.trimmingCharacters(in: .whitespaces).isEmpty {
            guard let first = speakers(guess).first else { return }
            settle(line, items: items) { document.assign([line.id], to: first.id, undoManager: undoManager) }
        } else if let guess {
            settle(line, items: items) { document.assign([line.id], to: guess.id, undoManager: undoManager) }
        }
    }

    /// Makes the change, then shows the next line (the settled one leaves the list).
    private func settle(_ line: Line, items: [Line], _ change: () -> Void) {
        let ids = items.map(\.id)
        let i = ids.firstIndex(of: line.id) ?? 0
        let next = i + 1 < ids.count ? ids[i + 1] : (i > 0 ? ids[i - 1] : nil)
        change()
        query = ""
        selection = next
        focused = true
    }

    private func move(_ step: Int, items: [Line]) {
        guard let id = selection, let i = items.firstIndex(where: { $0.id == id }) else { return }
        let j = i + step
        guard items.indices.contains(j) else { return }
        query = ""
        selection = items[j].id
    }
}
