import InnerVoiceKit
import SwiftUI

/// Every line that needs the narrator, one after another with the text around it: the
/// AI's unsure guesses first. Return confirms the guess, typing or 1–9 picks someone else,
/// 0 makes it narration, ↓ and ↑ move between lines. A settled line leaves the list and
/// the next one comes up. The card is the line's whole paragraph, with a row for each line
/// in it that needs the narrator, and as many paragraphs around it as Context asks for.
struct ReviewPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager

    enum Filter: String, CaseIterable, Identifiable {
        case guesses, noSpeaker, all, tones, voices
        var id: String { rawValue }
        var title: String {
            switch self {
            case .guesses: "Unsure Guesses"
            case .noSpeaker: "Who's Speaking?"
            case .all: "All Speakers"
            case .tones: "Unsure Tones"
            case .voices: "Unsure Voices"
            }
        }
    }

    @SceneStorage("review.filter") private var filterRaw = Filter.guesses.rawValue
    @State private var selection: UUID?
    @State private var query = ""
    @FocusState private var focused: Bool
    @AppStorage("reading.serif", store: ReadingStyle.store) private var serif = true
    /// Paragraphs shown before and after the one under review, for every book (test runs
    /// keep it in their own store, like the reading settings).
    @AppStorage("review.context", store: ReadingStyle.store) private var contextStored = 2
    @Environment(\.colorScheme) private var colorScheme

    static let maxContext = 5
    private var contextAround: Int { min(max(contextStored, 0), Self.maxContext) }

    private var filter: Filter { Filter(rawValue: filterRaw) ?? .guesses }

    private var needing: [Line] { document.project.lines.filter { document.project.needsYou($0) } }

    private func items(_ f: Filter) -> [Line] {
        switch f {
        case .guesses: needing.filter { $0.speakerID != nil }
        case .noSpeaker: needing.filter { $0.speakerID == nil }
        case .all: needing
        case .tones: document.project.lines.filter { document.project.toneNeedsYou($0) && !document.project.needsYou($0) }
        case .voices: []
        }
    }

    /// Characters whose voice casting wasn't sure of, most lines first.
    private var unsureVoices: [BookCharacter] {
        let index = document.index
        return document.project.characters
            .filter { (index.stats[$0.id]?.lines ?? 0) > 0 && document.project.voiceNeedsYou($0, engine: document.project.engineInUse) }
            .sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
    }

    private func count(_ f: Filter) -> Int { f == .voices ? unsureVoices.count : items(f).count }

    private var nothingToReview: Bool { Filter.allCases.allSatisfy { count($0) == 0 || ($0 == .voices && !document.project.showsVoices) } }

    var body: some View {
        let items = items(filter)
        Group {
            if nothingToReview {
                ContentUnavailableView("Nothing to Review", systemImage: "checkmark.circle",
                                       description: Text("Every line has a speaker you confirmed or one the AI is sure of."))
            } else {
                HStack(spacing: 0) {
                    Group {
                        if filter == .voices { voiceList } else { list(items) }
                    }
                    .frame(width: 300)
                    Divider()
                    if filter == .voices, let id = selection, let c = unsureVoices.first(where: { $0.id == id }) {
                        voiceCard(c)
                    } else if filter == .tones, let id = selection, let line = items.first(where: { $0.id == id }) {
                        toneCard(line, items: items)
                    } else if filter != .voices, filter != .tones, let id = selection, let line = items.first(where: { $0.id == id }) {
                        card(line, items: items)
                    } else if count(filter) == 0, let other = Filter.allCases.first(where: { $0 != filter && count($0) > 0 && ($0 != .voices || document.project.showsVoices) }) {
                        ContentUnavailableView {
                            Label(filter == .guesses ? "No Unsure Guesses Left" : "Nothing Here", systemImage: "checkmark.circle")
                        } description: {
                            Text(other == .voices ? (count(other) == 1 ? "1 voice needs you." : "\(count(other)) voices need you.") : (count(other) == 1 ? "1 more line needs you." : "\(count(other)) more lines need you."))
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
        .pageSubtitle(needing.isEmpty ? "" : "\(needing.count) lines need you")
        .toolbar {
            if !nothingToReview, filter != .voices {
                ToolbarItem { contextControl }
            }
        }
        .onAppear {
            if let f = workspace.reviewFilter { filterRaw = f; workspace.reviewFilter = nil }
            selectFirstIfNeeded()
        }
        .onChange(of: workspace.reviewFilter) {
            if let f = workspace.reviewFilter { filterRaw = f; workspace.reviewFilter = nil }
        }
        .onChange(of: filterRaw) { selection = nil; selectFirstIfNeeded() }
        .onChange(of: unsureVoices.map(\.id)) { if filter == .voices { selectFirstIfNeeded() } }
    }

    private func selectFirstIfNeeded() {
        let ids = filter == .voices ? unsureVoices.map(\.id) : items(filter).map(\.id)
        if selection == nil || !ids.contains(where: { $0 == selection }) { selection = ids.first }
        focused = true
    }

    private var filterPicker: some View {
        Picker("Show", selection: $filterRaw) {
            ForEach(Filter.allCases.filter { $0 != .voices || document.project.showsVoices }) { f in
                Text("\(f.title) (\(count(f)))").tag(f.rawValue)
            }
        }
        .labelsHidden()
        .padding(Space.md)
        .help("What to review")
    }

    // MARK: Context

    /// "Context − +  2 paragraphs around": how much of the chapter each card shows.
    private var contextControl: some View {
        let n = contextAround
        return HStack(spacing: Space.sm) {
            Text("Context")
            ControlGroup {
                Button("Less Context", systemImage: "minus") { contextStored = max(0, n - 1) }
                    .disabled(n <= 0)
                    .help("Show fewer paragraphs around each line")
                Button("More Context", systemImage: "plus") { contextStored = min(Self.maxContext, n + 1) }
                    .disabled(n >= Self.maxContext)
                    .help("Show more paragraphs around each line")
            }
            .labelStyle(.iconOnly)
            .fixedSize()
            Text(n == 0 ? "This paragraph only" : n == 1 ? "1 paragraph around" : "\(n) paragraphs around")
                .foregroundStyle(.secondary)
                .frame(minWidth: 130, alignment: .leading)
        }
        .padding(.horizontal, Space.sm)
    }

    // MARK: The list

    private func list(_ items: [Line]) -> some View {
        VStack(spacing: 0) {
            filterPicker
            Divider()
            List(items, selection: $selection) { line in
                (filter == .tones ? AnyView(toneRow(line)) : AnyView(row(line))).tag(line.id)
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
                Text(document.index.titledReference(line.paragraphID, in: project))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Text(project.text(of: line, index: document.index))
                    .lineLimit(2)
            }
        }
        .padding(.vertical, 2)
    }

    // MARK: The paragraph

    /// The lines under review in the same paragraph as `line`, in reading order.
    private func paragraphLines(_ line: Line, items: [Line]) -> [Line] {
        items.filter { $0.paragraphID == line.paragraphID }.sorted { $0.range.location < $1.range.location }
    }

    /// One card per paragraph: the paragraph with the paragraphs around it, a row for each
    /// line in it that needs you, then the speakers for the selected line.
    private func card(_ line: Line, items: [Line]) -> some View {
        let project = document.project
        let guess = project.character(line.speakerID)
        let position = (items.firstIndex { $0.id == line.id } ?? 0) + 1
        let group = paragraphLines(line, items: items)
        return ScrollView {
            VStack(alignment: .leading, spacing: Space.lg) {
                Text("\(document.index.titledReference(line.paragraphID, in: project)) · \(position) of \(items.count)")
                    .font(.callout.monospacedDigit())
                    .foregroundStyle(.secondary)
                context(line.paragraphID, reviewing: Set(group.map(\.id)), selected: line.id)
                VStack(alignment: .leading, spacing: Space.xs) {
                    ForEach(group) { l in lineRow(l, selected: l.id == line.id, items: items) }
                }
                Divider()
                decide(line, guess: guess, items: items)
            }
            .padding(Space.xl)
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        // Moving between lines of one paragraph keeps the card (and where it's scrolled).
        .id(line.paragraphID)
    }

    /// One line to review: who it might be, its words, and what to do with it. Clicking the
    /// row makes it the line the keys and the speakers below act on.
    private func lineRow(_ line: Line, selected: Bool, items: [Line]) -> some View {
        let project = document.project
        let guess = project.character(line.speakerID)
        let red = Color(nsColor: .attention)
        return HStack(spacing: Space.sm) {
            Text(guess.map { "\($0.firstName)?" } ?? "Who's Speaking?")
                .font(.caption.weight(.semibold))
                .foregroundStyle(red)
                .padding(.horizontal, 6)
                .padding(.vertical, 1)
                .background(red.opacity(0.14), in: Capsule())
            Text("“\(project.text(of: line, index: document.index))”")
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.tail)
            if line.kind != .spoken {
                Text(line.kind.title).font(.caption).foregroundStyle(.tertiary)
            }
            Spacer(minLength: Space.sm)
            if let guess {
                let confirm = Button("Confirm \(guess.firstName)") {
                    settle(line, items: items) { document.assign([line.id], to: guess.id, undoManager: undoManager) }
                }
                .help("Give the line to \(guess.name), the best guess, \(Int(line.confidence * 100))% sure" + (selected ? " (Return)" : ""))
                if selected { confirm.buttonStyle(.borderedProminent) } else { confirm }
            }
            ReviewAssignButton(document: document, lineID: line.id, undoManager: undoManager) {
                settle(line, items: items) {}
            }
            Button("Show") { workspace.show(paragraph: line.paragraphID, line: line.id) }
                .help("See this line in the manuscript")
        }
        .controlSize(.small)
        .padding(.horizontal, Space.sm)
        .padding(.vertical, Space.xs)
        .background(selected ? Color.accentColor.opacity(0.12) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        .contentShape(Rectangle())
        .onTapGesture {
            query = ""
            selection = line.id
            focused = true
        }
    }

    /// The paragraph under review with up to `contextAround` paragraphs before and after it,
    /// never past its chapter's edges.
    private func context(_ paragraphID: UUID, reviewing: Set<UUID>, selected: UUID?) -> some View {
        let project = document.project
        let index = document.index
        let pos = index.positions[paragraphID]
        let range = index.contextRange(paragraphID, around: contextAround, in: project)
        let paragraphs = pos.map { project.chapters[$0.chapter].paragraphs } ?? []
        return VStack(alignment: .leading, spacing: Space.md) {
            if let pos, let range {
                ForEach(Array(range), id: \.self) { i in
                    let own = i == pos.paragraph
                    paragraphText(paragraphs[i], reviewing: own ? reviewing : [], selected: own ? selected : nil, isContext: !own)
                }
            }
        }
        .padding(Space.lg)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.separator))
    }

    /// A paragraph with each line in its speaker's color. The lines under review are red,
    /// the selected one bold and underlined. Context paragraphs are smaller and dimmer, and
    /// start with who speaks in them, in their colors, so the turn-taking is easy to follow.
    private func paragraphText(_ paragraph: Paragraph, reviewing: Set<UUID>, selected: UUID?, isContext: Bool) -> some View {
        let project = document.project
        let lines = (document.index.linesByParagraph[paragraph.id] ?? []).map { project.lines[$0] }
            .sorted { $0.range.location < $1.range.location }
        let font = Font.system(size: isContext ? 14 : 16, design: serif ? .serif : .default)
        let red = Color(nsColor: .attention)
        var text = AttributedString()
        if isContext {
            var seen = Set<UUID>()
            for c in lines.compactMap({ project.character($0.speakerID) }) where seen.insert(c.id).inserted {
                var name = AttributedString(c.name)
                name.swiftUI.font = .system(size: 12, weight: .semibold).smallCaps()
                name.swiftUI.foregroundColor = nameColor(c.colorHex)
                text += name
                text += AttributedString("  ")
            }
        }
        let ns = paragraph.text as NSString
        var cursor = 0
        for line in lines {
            let start = min(max(line.range.location, cursor), ns.length)
            let end = min(max(line.range.location + line.range.length, start), ns.length)
            if start > cursor { text += AttributedString(ns.substring(with: NSRange(location: cursor, length: start - cursor))) }
            if end > start {
                var run = AttributedString(ns.substring(with: NSRange(location: start, length: end - start)))
                let under = reviewing.contains(line.id)
                let color = under || project.needsYou(line) ? red
                    : project.character(line.speakerID).map { Color(hexString: $0.colorHex) } ?? Color.clear
                run.swiftUI.backgroundColor = color.opacity(under ? 0.24 : isContext ? 0.12 : 0.16)
                var runFont = font
                if under { run.swiftUI.foregroundColor = .primary; runFont = runFont.weight(.medium) }
                if line.id == selected {
                    runFont = font.weight(.semibold)
                    run.swiftUI.underlineStyle = Text.LineStyle(pattern: .solid, color: red)
                }
                if line.kind == .thought { runFont = runFont.italic() }
                run.swiftUI.font = runFont
                text += run
            }
            cursor = end
        }
        if cursor < ns.length { text += AttributedString(ns.substring(from: cursor)) }
        let centered = paragraph.style == .sceneBreak
        return Text(text)
            .font(font)
            .foregroundStyle(isContext ? .secondary : .primary)
            .lineSpacing(isContext ? 3 : 4)
            .multilineTextAlignment(centered ? .center : .leading)
            .frame(maxWidth: .infinity, alignment: centered ? .center : .leading)
            .padding(.leading, paragraph.style == .blockQuote ? Space.xl : 0)
            .textSelection(.enabled)
    }

    /// A character's color made readable as text: lighter in dark mode, darker in light.
    private func nameColor(_ hex: String) -> Color {
        let base = NSColor(hexString: hex)
        let dark = colorScheme == .dark
        return Color(nsColor: base.blended(withFraction: dark ? 0.35 : 0.25, of: dark ? .white : .black) ?? base)
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

    // MARK: Tones

    private func toneRow(_ line: Line) -> some View {
        let project = document.project
        let tone = line.tone ?? .neutral
        return HStack(alignment: .firstTextBaseline, spacing: Space.sm) {
            Text(tone.title + "?")
                .font(.caption.weight(.semibold))
                .foregroundStyle(Color(nsColor: .attention))
                .padding(.horizontal, 6)
                .padding(.vertical, 1)
                .background(Color(nsColor: .attention).opacity(0.14), in: Capsule())
            VStack(alignment: .leading, spacing: 2) {
                Text(document.index.titledReference(line.paragraphID, in: project) + (project.character(line.speakerID).map { " · " + $0.firstName } ?? ""))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Text(project.text(of: line, index: document.index))
                    .lineLimit(2)
            }
        }
        .padding(.vertical, 2)
    }

    private func toneCard(_ line: Line, items: [Line]) -> some View {
        let project = document.project
        let guess = line.tone ?? .neutral
        let position = (items.firstIndex { $0.id == line.id } ?? 0) + 1
        let who = project.character(line.speakerID)
        return ScrollView {
            VStack(alignment: .leading, spacing: Space.lg) {
                HStack {
                    Text("\(document.index.titledReference(line.paragraphID, in: project)) · \(position) of \(items.count)")
                        .font(.callout.monospacedDigit())
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Show in Manuscript") { workspace.show(paragraph: line.paragraphID, line: line.id) }
                        .help("See this line in the manuscript")
                }
                context(line.paragraphID, reviewing: [line.id], selected: line.id)
                Divider()
                VStack(alignment: .leading, spacing: Space.md) {
                    HStack(spacing: Space.md) {
                        Text("How does \(who?.firstName ?? "this character") say it? Best guess: \(guess.title)").font(.headline)
                        Text("\(Int((line.toneConfidence ?? 0) * 100))% sure").foregroundStyle(.secondary)
                        Spacer()
                        Button("Play", systemImage: "play.fill") { playTone(line, guess) }
                            .help("Hear the line in \(who?.firstName ?? "its")’s voice (Space)")
                        Button("Confirm \(guess.title)") { settleTone(line, guess, items: items) }
                            .buttonStyle(.borderedProminent)
                            .help("Keep this tone (Return)")
                    }
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: Space.sm)], alignment: .leading, spacing: Space.sm) {
                        ForEach(Tone.allCases) { t in
                            Button { settleTone(line, t, items: items) } label: {
                                HStack(spacing: Space.sm) {
                                    Image(systemName: t.symbol).frame(width: 16)
                                    Text(t.title)
                                    Spacer(minLength: 0)
                                    Text("\(t.key)").font(.caption.monospacedDigit()).foregroundStyle(.tertiary)
                                }
                                .padding(.horizontal, Space.sm)
                                .padding(.vertical, Space.xs)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .background(t == guess ? Color.accentColor.opacity(0.15) : Color.secondary.opacity(0.08),
                                            in: RoundedRectangle(cornerRadius: 6))
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .help("The line is \(t.title.lowercased()) (\(t.key))")
                        }
                    }
                    HStack {
                        Button("Skip") { move(+1, items: items) }
                            .help("Leave this line for later (↓)")
                        Spacer()
                        Text("Return confirms · 1–9 picks · Space plays · ↓ ↑ move")
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                    }
                    .controlSize(.small)
                }
                .focusable()
                .focused($focused)
                .focusEffectDisabled()
                .onKeyPress(.downArrow) { move(+1, items: items); return .handled }
                .onKeyPress(.upArrow) { move(-1, items: items); return .handled }
                .onKeyPress(.return) { settleTone(line, guess, items: items); return .handled }
                .onKeyPress(.space) { playTone(line, line.tone ?? .neutral); return .handled }
                .onKeyPress(characters: .decimalDigits) { press in
                    guard let n = Int(press.characters), n >= 1, n <= Tone.allCases.count else { return .ignored }
                    settleTone(line, Tone.allCases[n - 1], items: items)
                    return .handled
                }
            }
            .padding(Space.xl)
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .id(line.id)
        .onAppear { focused = true }
    }

    private func playTone(_ line: Line, _ tone: Tone) {
        VoiceCastingRun.castIfNeeded(document: document)
        let project = document.project
        let index = document.index
        guard let p = project.paragraph(line.paragraphID, index: index) else { return }
        var segs = SpeechScript.segments(for: p, project: project, index: index, includeNarration: false).filter { $0.lineID == line.id }
        for i in segs.indices { segs[i].tone = tone }
        SpeechPlayer.shared.play(segs, resolver: VoiceResolver(project: project))
    }

    private func settleTone(_ line: Line, _ tone: Tone, items: [Line]) {
        settle(line, items: items) { document.setTone(tone, for: [line.id], undoManager: undoManager) }
    }

    // MARK: Voices

    private var voiceList: some View {
        VStack(spacing: 0) {
            filterPicker
            Divider()
            List(unsureVoices, selection: $selection) { c in
                HStack(spacing: Space.sm) {
                    Circle().fill(Color(hexString: c.colorHex)).frame(width: 10, height: 10)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(c.name)
                        Text(c.voice(for: document.project.engineInUse).map { VoiceCatalog.name(of: $0.choice) + "?" } ?? "No voice yet")
                            .font(.caption)
                            .foregroundStyle(Color(nsColor: .attention))
                    }
                    Spacer()
                    Text("\(document.index.stats[c.id]?.lines ?? 0)").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                }
                .tag(c.id)
            }
            .listStyle(.inset)
        }
    }

    private func voiceCard(_ c: BookCharacter) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Space.lg) {
                HStack(spacing: Space.md) {
                    Avatar(character: c, size: 40)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(c.name).font(.title3.weight(.semibold))
                        Text("\(document.index.stats[c.id]?.lines ?? 0) lines").foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("All Voices…") { workspace.showVoice(c.id) }
                        .help("Choose from every voice on the Voices page")
                }
                CastingProfileView(document: document, character: c)
                VoiceChooser(document: document, characterID: c.id, compact: true)
            }
            .padding(Space.xl)
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .id(c.id)
    }
}

/// Assign… on a Review row: the manuscript's Assign To popover for that one line.
private struct ReviewAssignButton: View {
    let document: InnerVoiceDocument
    let lineID: UUID
    let undoManager: UndoManager?
    var assigned: () -> Void
    @State private var shown = false

    var body: some View {
        Button("Assign…") { shown = true }
            .help("Give the line to anyone in the cast, or someone new")
            .popover(isPresented: $shown, arrowEdge: .bottom) {
                AssignPopover(document: document, lineID: lineID, undoManager: undoManager, onDone: { to, previous in
                    shown = false
                    if let to, to != previous { assigned() }
                }, onClose: { shown = false })
                .themed()
            }
    }
}
