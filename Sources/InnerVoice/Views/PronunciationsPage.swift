import AVFoundation
import AppKit
import InnerVoiceKit
import SwiftUI

struct PronunciationsPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State private var selection: Set<UUID> = []
    @State private var showDismissed = false
    @State private var search = ""
    @State private var player = PronunciationSamplePlayer()
    @State private var show: PronunciationFilter = .all
    private var computer: ComputerPronunciations { .shared }

    private var bookRows: [PronunciationRow] {
        document.project.pronunciations
            .filter { showDismissed || !$0.dismissed }
            .sorted { a, b in
                if a.addedByUser != b.addedByUser { return a.addedByUser }
                return a.occurrences == b.occurrences ? a.term < b.term : a.occurrences > b.occurrences
            }
            .map { PronunciationRow(id: $0.id, kind: .book, book: $0, listed: nil) }
    }

    /// This Mac's entries as this book sees them: used, from another computer (the book's
    /// copy), replaced by the book's own entry, or not used (the book turned the list off).
    private var computerRows: [PronunciationRow] {
        let project = document.project
        let own = PronunciationLists.bookKeys(project)
        let on = project.settings.usesComputerPronunciations
        var rows: [PronunciationRow] = []
        for e in computer.list.sorted {
            let kind: PronunciationRow.Kind = !on ? .notUsed : own.contains(e.key) ? .replaced : .computer
            rows.append(PronunciationRow(id: e.id, kind: kind, book: nil, listed: e))
        }
        if on {
            for (e, source) in PronunciationLists.computerEntries(project, computer: computer.list) where source == .bookCopy {
                rows.append(PronunciationRow(id: e.id, kind: own.contains(e.key) ? .replaced : .bookCopy, book: nil, listed: e))
            }
        }
        return rows
    }

    private var rows: [PronunciationRow] {
        let all: [PronunciationRow]
        switch show {
        case .all: all = bookRows + computerRows.filter { $0.kind == .computer || $0.kind == .bookCopy }
        case .book: all = bookRows
        case .computer: all = computerRows
        }
        guard !search.isEmpty else { return all }
        return all.filter { $0.term.localizedCaseInsensitiveContains(search) || $0.sayItAs.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        Group {
            if document.project.pronunciations.isEmpty && computer.list.entries.isEmpty && search.isEmpty {
                ContentUnavailableView {
                    Label("No Pronunciations", systemImage: "character.bubble")
                } description: {
                    Text("When an AI voice says a word wrong, add it here with a spelling of how it should sound. Every voice uses it from then on. InnerVoice also lists names, places and made-up words it finds.")
                } actions: {
                    Button("Add Term") { add() }
                    Button("Find Terms") { refresh() }
                }
            } else {
                table.safeAreaInset(edge: .top, spacing: 0) {
                    VStack(spacing: 0) { explainer; Divider() }.background(.bar)
                }
            }
        }
        .onAppear(perform: takeFocus)
        .onChange(of: workspace.focusPronunciation) { takeFocus() }
        .navigationTitle("Pronunciations")
        .pageSubtitle("\(document.project.pronunciations.filter { !$0.sayItAs.isEmpty }.count) of \(document.project.pronunciations.filter { !$0.dismissed }.count) done")
        .searchable(text: $search, placement: .toolbar, prompt: "Find a term")
        .toolbar {
            ToolbarItemGroup {
                Toggle("Show Dismissed", systemImage: "eye.slash", isOn: $showDismissed)
                    .help("Show terms you said need no entry")
                Button("Find Terms", systemImage: "text.magnifyingglass") { refresh() }
                    .help("Look through the manuscript for unusual names and words again")
                Button("Add Term", systemImage: "plus") { add() }
                    .help("Add a term to the guide")
                if ClaudeAccount.shared.isSetUp {
                    Button("Suggest Spellings (Cloud)", systemImage: "sparkles") {
                        document.claude.run(.spellings, undoManager: undoManager)
                    }
                    .help("Cloud: Claude suggests a Say It As for each term without one. Sends each term and the sentence it first appears in to Anthropic. You confirm each spelling.")
                    .disabled(document.claude.isRunning || document.tagging.isRunning)
                }
            }
        }
    }

    /// What this page is for, and the two lists: This Book's and This Mac's.
    private var explainer: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack(alignment: .firstTextBaseline, spacing: Space.sm) {
                Image(systemName: "character.bubble").foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 2) {
                    Text(document.project.showsVoices ? "Teach the AI voices how to say hard words" : "How to say the hard words")
                        .font(.callout.weight(.semibold))
                    Text(document.project.showsVoices
                         ? "Type how a word should sound in Say It As, with capitals for the stressed part (BAR-nuh-bee), and every voice says it that way. One entry also covers plurals and possessives, like Barnaby's. Click the play button to hear it."
                         : "Keep how each name and unusual word is said, with capitals for the stressed part (BAR-nuh-bee). Click the play button to hear it.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(3)
                    Text("This Book’s entries are saved in the book. This Mac’s are used by every book on this Mac. When both have a word, this book’s wins.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                }
                Spacer(minLength: 0)
            }
            HStack(spacing: Space.md) {
                Picker("Show", selection: $show) {
                    Text("All").tag(PronunciationFilter.all)
                    Text("This Book (\(document.project.pronunciations.filter { !$0.dismissed }.count))").tag(PronunciationFilter.book)
                    Text("This Mac (\(computer.list.entries.count))").tag(PronunciationFilter.computer)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                Toggle("Use This Mac’s Pronunciations", isOn: Binding(
                    get: { document.project.settings.usesComputerPronunciations },
                    set: { v in
                        document.updateSettings(v ? "Use This Mac’s Pronunciations" : "Don’t Use This Mac’s Pronunciations",
                                                undoManager: undoManager) { $0.usesComputerPronunciations = v }
                    }))
                    .toggleStyle(.checkbox)
                    .help("On: this book uses This Mac’s list for words it has no entry of its own for. Turn it off for a book whose names are said differently.")
                Spacer(minLength: 0)
                Button("Add This Book’s to This Mac…") { ComputerPronunciations.addBook(document) }
                    .help("Put this book’s entries in This Mac’s list, so every book on this Mac uses them")
                    .disabled(PronunciationLists.bookEntries(document.project).isEmpty)
                Button("Edit This Mac’s List…") { SettingsWindow.shared.show(tab: .pronunciations) }
                    .help("Add, change, import or export This Mac’s pronunciations in Settings")
            }
            .controlSize(.small)
        }
        .padding(.horizontal, Space.lg)
        .padding(.vertical, Space.sm)
    }

    private func takeFocus() {
        guard let id = workspace.focusPronunciation else { return }
        workspace.focusPronunciation = nil
        search = ""
        showDismissed = showDismissed || document.project.pronunciations.first { $0.id == id }?.dismissed == true
        selection = [id]
    }

    private var table: some View {
        Table(rows, selection: $selection) {
            TableColumn("Term") { row in
                HStack(spacing: Space.xs) {
                    if let p = row.book {
                        DraftTextField("Term", text: binding(p, \.term)).textFieldStyle(.plain).font(.body.weight(.medium))
                        // Samples recorded with an earlier InnerVoice still play.
                        if let file = p.audioFile, let data = document.audio[file] {
                            Button { player.play(data) } label: {
                                Image(systemName: "play.circle").foregroundStyle(.secondary)
                            }
                            .buttonStyle(.plain)
                            .help("Play the sample you recorded")
                            .accessibilityLabel("Play sample")
                        }
                    } else {
                        Text(row.term).font(.body.weight(.medium)).foregroundStyle(row.isUsed ? .primary : .secondary)
                            .strikethrough(row.kind == .replaced)
                    }
                }
            }
            .width(min: 120, ideal: 160)
            TableColumn("List") { row in PronunciationSourceTag(kind: row.kind) }
                .width(min: 90, ideal: 120)
            TableColumn("Say It As") { row in
                HStack(spacing: Space.xs) {
                    if let p = row.book {
                        DraftTextField("e.g. BAR-nuh-bee", text: binding(p, \.sayItAs)).textFieldStyle(.plain)
                    } else {
                        Text(row.sayItAs).foregroundStyle(row.isUsed ? .primary : .secondary)
                    }
                    if document.project.showsVoices || document.project.showsNarrationTools {
                        Button { PlayCommand.hear(row.asPronunciation, document: document) } label: {
                            Image(systemName: "play.circle").foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                        .help(row.sayItAs.isEmpty ? "Hear how the narrator's voice says it now" : "Hear it in the narrator's voice, with this spelling")
                        .accessibilityLabel("Hear it")
                    }
                }
            }
            .width(min: 140, ideal: 190)
            TableColumn("Notes") { row in
                if let p = row.book {
                    DraftTextField("Origin, stress, source", text: binding(p, \.notes)).textFieldStyle(.plain).foregroundStyle(.secondary)
                } else {
                    Text(row.notes).foregroundStyle(.secondary)
                }
            }
            .width(min: 140, ideal: 220)
            TableColumn("Count") { row in
                Text((row.book?.occurrences ?? 0) > 0 ? "\(row.book!.occurrences)" : "—").monospacedDigit().foregroundStyle(.secondary)
            }
            .width(48)
            TableColumn("Match Case") { row in
                if let p = row.book {
                    Toggle("Match Case", isOn: Binding(get: { p.matchCase }, set: { v in
                        update(p.id, "Match Case") { $0.matchCase = v }
                    }))
                        .labelsHidden()
                        .toggleStyle(.checkbox)
                        .help("Only when the capitals match, so \"Will\" the name doesn't change \"will\"")
                } else {
                    Toggle("Match Case", isOn: .constant(row.listed?.matchCase ?? false))
                        .labelsHidden()
                        .toggleStyle(.checkbox)
                        .disabled(true)
                }
            }
            .width(80)
            TableColumn("Confirmed") { row in
                if let p = row.book {
                    Toggle("Confirmed", isOn: Binding(get: { p.confirmed }, set: { v in
                        if v, !p.confirmed { ActivityStore.shared.record(ActivityKey.pronunciations) }
                        update(p.id, "Confirm Pronunciation") { $0.confirmed = v }
                    }))
                        .labelsHidden()
                        .toggleStyle(.checkbox)
                        .help("Checked with the author or rights holder")
                }
            }
            .width(72)
        }
        .contextMenu(forSelectionType: UUID.self) { ids in
            let picked = rows.filter { ids.contains($0.id) }
            let book = picked.compactMap(\.book)
            let listed = picked.filter { $0.book == nil }
            if book.count == 1, let pid = book[0].firstParagraphID {
                Button("Show First Use in Manuscript") { workspace.show(paragraph: pid) }
            }
            if !book.isEmpty {
                Button(book.count > 1 ? "Add \(book.count) to This Mac’s List" : "Add to This Mac’s List") {
                    let entries = book.map { ListedPronunciation(term: $0.term.trimmingCharacters(in: .whitespaces), sayItAs: $0.sayItAs,
                                                                 notes: $0.notes, matchCase: $0.matchCase) }
                    _ = computer.addAsking(entries, incoming: "This Book’s")
                }
                .disabled(book.allSatisfy { $0.sayItAs.trimmingCharacters(in: .whitespaces).isEmpty })
                Divider()
                Button(book.count > 1 ? "Dismiss \(book.count) Terms" : "Dismiss") {
                    let ids = Set(book.map(\.id))
                    document.change("Dismiss Terms", undoManager: undoManager) { p in
                        for i in p.pronunciations.indices where ids.contains(p.pronunciations[i].id) { p.pronunciations[i].dismissed = true }
                    }
                }
                Button("Delete", role: .destructive) {
                    let ids = Set(book.map(\.id))
                    document.change("Delete Terms", undoManager: undoManager) { p in p.pronunciations.removeAll { ids.contains($0.id) } }
                }
            }
            if !listed.isEmpty {
                if listed.count == 1, listed[0].kind != .replaced {
                    Button("Change for This Book Only") { overrideInBook(listed[0]) }
                }
                if listed.contains(where: { $0.kind == .bookCopy }) {
                    Button("Add to This Mac’s List") {
                        _ = computer.addAsking(listed.filter { $0.kind == .bookCopy }.compactMap(\.listed), incoming: "This Book’s")
                    }
                }
                let mine = listed.filter { $0.kind != .bookCopy }
                if !mine.isEmpty {
                    Button(mine.count > 1 ? "Remove \(mine.count) from This Mac’s List…" : "Remove from This Mac’s List…", role: .destructive) {
                        removeFromComputer(mine)
                    }
                }
            }
        }
    }

    /// A This Mac word this book should say its own way: copied into the book's list, where
    /// it can be changed (and wins from then on).
    private func overrideInBook(_ row: PronunciationRow) {
        guard let e = row.listed else { return }
        let p = Pronunciation(term: e.term, sayItAs: e.sayItAs, notes: e.notes, addedByUser: true, matchCase: e.matchCase)
        document.change("Change Pronunciation for This Book", undoManager: undoManager) { $0.pronunciations.append(p) }
        show = .book
        selection = [p.id]
    }

    private func removeFromComputer(_ rows: [PronunciationRow]) {
        let alert = NSAlert()
        alert.messageText = rows.count == 1 ? "Remove “\(rows[0].term)” from This Mac’s list?" : "Remove \(rows.count) words from This Mac’s list?"
        alert.informativeText = "Every book on this Mac stops using \(rows.count == 1 ? "it" : "them"). Books keep their own entries."
        alert.addButton(withTitle: "Remove")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        computer.remove(Set(rows.map(\.id)))
    }

    private func binding(_ p: Pronunciation, _ key: WritableKeyPath<Pronunciation, String>) -> Binding<String> {
        Binding(get: { document.project.pronunciations.first { $0.id == p.id }?[keyPath: key] ?? "" },
                set: { v in update(p.id, "Edit Pronunciation") { $0[keyPath: key] = v } })
    }

    private func update(_ id: UUID, _ name: String, _ body: @escaping (inout Pronunciation) -> Void) {
        document.change(name, undoManager: undoManager) { p in
            if let i = p.pronunciations.firstIndex(where: { $0.id == id }) { body(&p.pronunciations[i]) }
        }
    }

    private func add() {
        let p = Pronunciation(term: "New Term", addedByUser: true)
        document.change("Add Term", undoManager: undoManager) { $0.pronunciations.append(p) }
        selection = [p.id]
    }

    private func refresh() {
        document.change("Find Terms", undoManager: undoManager) { PronunciationFinder.refresh(&$0, isKnownWord: SpellCheck.isKnown) }
    }
}

enum PronunciationFilter: Hashable { case all, book, computer }

/// A row on the Pronunciations page: one of the book's entries, or one of This Mac's.
struct PronunciationRow: Identifiable {
    enum Kind {
        /// The book's own entry.
        case book
        /// This Mac's, used by this book.
        case computer
        /// From another computer: kept in the book's copy, not in This Mac's list.
        case bookCopy
        /// This Mac's, but the book has its own entry for the word.
        case replaced
        /// This Mac's, but the book has This Mac's list turned off.
        case notUsed
    }
    var id: UUID
    var kind: Kind
    var book: Pronunciation?
    var listed: ListedPronunciation?

    var term: String { book?.term ?? listed?.term ?? "" }
    var sayItAs: String { book?.sayItAs ?? listed?.sayItAs ?? "" }
    var notes: String { book?.notes ?? listed?.notes ?? "" }
    var isUsed: Bool { kind == .book || kind == .computer || kind == .bookCopy }
    var asPronunciation: Pronunciation {
        book ?? Pronunciation(term: term, sayItAs: sayItAs, matchCase: listed?.matchCase ?? false)
    }
}

/// Which list a row is from: This Book, This Mac, or another computer's (kept in the book).
struct PronunciationSourceTag: View {
    let kind: PronunciationRow.Kind
    var body: some View {
        switch kind {
        case .book: tag("This Book", "book.closed", .accentColor, help: "Saved in this book")
        case .computer: tag("This Mac", "desktopcomputer", .secondary, help: "From This Mac’s list, used by every book on this Mac")
        case .bookCopy: tag("Another Computer", "arrow.down.doc", .secondary,
                            help: "From the list on the computer this book was saved on. This book keeps a copy, so it sounds the same here. Right-click to add it to This Mac’s list.")
        case .replaced: tag("This Mac · Not Used", "desktopcomputer", .secondary, help: "This book has its own entry for this word, and the book’s wins")
        case .notUsed: tag("This Mac · Off", "desktopcomputer", .secondary, help: "This book has Use This Mac’s Pronunciations turned off")
        }
    }

    private func tag(_ text: String, _ symbol: String, _ color: Color, help: String) -> some View {
        Label(text, systemImage: symbol)
            .font(.caption)
            .labelStyle(.titleAndIcon)
            .foregroundStyle(color)
            .lineLimit(1)
            .help(help)
    }
}

/// Plays a pronunciation sample kept in the project (InnerVoice no longer records them).
@Observable
final class PronunciationSamplePlayer {
    @ObservationIgnored private var player: AVAudioPlayer?

    func play(_ data: Data) {
        player = try? AVAudioPlayer(data: data)
        player?.play()
    }
}

/// Settings › Pronunciations: This Mac's list, to edit, import and export.
struct ComputerPronunciationsSettings: View {
    private var store: ComputerPronunciations { .shared }
    @State private var selection: Set<UUID> = []
    @State private var search = ""

    private var rows: [ListedPronunciation] {
        store.list.sorted.filter { search.isEmpty || $0.term.localizedCaseInsensitiveContains(search) || $0.sayItAs.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.md) {
            VStack(alignment: .leading, spacing: Space.xs) {
                Text("This Mac’s Pronunciations").font(.headline)
                Text("Every book on this Mac uses these, unless the book has Use This Mac’s Pronunciations turned off. Each book keeps a copy of the ones it uses, so it sounds the same on another computer. When a book has its own entry for a word, the book’s wins. With Sync settings on, this list follows you to your other Macs.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: Space.sm) {
                TextField("Find a word", text: $search)
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 220)
                Spacer()
                Button("Import…") { store.importFile() }
                    .help("Add words from a CSV, text (word = how to say it) or InnerVoice JSON file")
                Button("Export…") { store.exportFile() }
                    .help("Save This Mac’s list as CSV (opens in Numbers and Excel) or JSON")
                    .disabled(store.list.entries.isEmpty)
            }
            Table(rows, selection: $selection) {
                TableColumn("Word") { e in
                    DraftTextField("Word", text: binding(e, \.term)).textFieldStyle(.plain).font(.body.weight(.medium))
                }
                .width(min: 90, ideal: 120)
                TableColumn("Say It As") { e in
                    DraftTextField("e.g. BAR-nuh-bee", text: binding(e, \.sayItAs)).textFieldStyle(.plain)
                }
                .width(min: 100, ideal: 140)
                TableColumn("Match Case") { e in
                    Toggle("Match Case", isOn: Binding(get: { e.matchCase }, set: { v in store.update(e.id) { $0.matchCase = v } }))
                        .labelsHidden()
                        .toggleStyle(.checkbox)
                        .help("Only when the capitals match, so \"Will\" the name doesn't change \"will\"")
                }
                .width(72)
                TableColumn("Notes") { e in
                    DraftTextField("Origin, stress, source", text: binding(e, \.notes)).textFieldStyle(.plain).foregroundStyle(.secondary)
                }
                .width(min: 80, ideal: 130)
            }
            .contextMenu(forSelectionType: UUID.self) { ids in
                Button(ids.count > 1 ? "Delete \(ids.count) Words" : "Delete", role: .destructive) { store.remove(ids) }
            }
            .onDeleteCommand { store.remove(selection) }
            .frame(minHeight: 300)
            .overlay {
                if store.list.entries.isEmpty {
                    Text("No words yet. Click + to add one, import a list, or choose Book › Add This Book’s Pronunciations to This Mac… in a book.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .padding(Space.xl)
                }
            }
            HStack(spacing: 0) {
                Button { selection = [store.add()] } label: { Image(systemName: "plus").frame(width: 20, height: 16) }
                    .help("Add a word")
                Button { store.remove(selection); selection = [] } label: { Image(systemName: "minus").frame(width: 20, height: 16) }
                    .help("Delete the selected words")
                    .disabled(selection.isEmpty)
                Spacer()
                Text("\(store.list.entries.count) words").font(.caption).foregroundStyle(.secondary)
            }
            .buttonStyle(.borderless)
        }
    }

    private func binding(_ e: ListedPronunciation, _ key: WritableKeyPath<ListedPronunciation, String>) -> Binding<String> {
        Binding(get: { store.list.entries.first { $0.id == e.id }?[keyPath: key] ?? "" },
                set: { v in store.update(e.id) { $0[keyPath: key] = v } })
    }
}
