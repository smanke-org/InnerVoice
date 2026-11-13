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

    private var rows: [Pronunciation] {
        document.project.pronunciations
            .filter { showDismissed || !$0.dismissed }
            .filter { search.isEmpty || $0.term.localizedCaseInsensitiveContains(search) || $0.sayItAs.localizedCaseInsensitiveContains(search) }
            .sorted { a, b in
                if a.addedByUser != b.addedByUser { return a.addedByUser }
                return a.occurrences == b.occurrences ? a.term < b.term : a.occurrences > b.occurrences
            }
    }

    var body: some View {
        Group {
            if rows.isEmpty && search.isEmpty {
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

    /// What this page is for: the AI voices read the page as written, and this is how to correct them.
    private var explainer: some View {
        HStack(alignment: .firstTextBaseline, spacing: Space.sm) {
            Image(systemName: "character.bubble").foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text("Teach the AI voices how to say hard words")
                    .font(.callout.weight(.semibold))
                Text("AI voices guess at names and unusual words from their spelling. Type how a word should sound in Say It As, with capitals for the stressed part (BAR-nuh-bee), and every voice says it that way from then on. One entry also covers plurals and possessives, like Barnaby's. Click the play button to hear it in the narrator's voice.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
            }
            Spacer(minLength: 0)
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
            TableColumn("Term") { p in
                HStack(spacing: Space.xs) {
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
                }
            }
            .width(min: 120, ideal: 160)
            TableColumn("Say It As") { p in
                HStack(spacing: Space.xs) {
                    DraftTextField("e.g. BAR-nuh-bee", text: binding(p, \.sayItAs)).textFieldStyle(.plain)
                    if document.project.showsVoices {
                        Button { PlayCommand.hear(p, document: document) } label: {
                            Image(systemName: "play.circle").foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                        .help(p.sayItAs.isEmpty ? "Hear how the narrator's voice says it now" : "Hear it in the narrator's voice, with this spelling")
                        .accessibilityLabel("Hear it")
                    }
                }
            }
            .width(min: 140, ideal: 190)
            TableColumn("Notes") { p in
                DraftTextField("Origin, stress, source", text: binding(p, \.notes)).textFieldStyle(.plain).foregroundStyle(.secondary)
            }
            .width(min: 140, ideal: 220)
            TableColumn("Count") { p in
                Text(p.occurrences > 0 ? "\(p.occurrences)" : "—").monospacedDigit().foregroundStyle(.secondary)
            }
            .width(48)
            TableColumn("Match Case") { p in
                Toggle("Match Case", isOn: Binding(get: { p.matchCase }, set: { v in
                    update(p.id, "Match Case") { $0.matchCase = v }
                }))
                    .labelsHidden()
                    .toggleStyle(.checkbox)
                    .help("Only when the capitals match, so \"Will\" the name doesn't change \"will\"")
            }
            .width(80)
            TableColumn("Confirmed") { p in
                Toggle("Confirmed", isOn: Binding(get: { p.confirmed }, set: { v in
                    if v, !p.confirmed { ActivityStore.shared.record(ActivityKey.pronunciations) }
                    update(p.id, "Confirm Pronunciation") { $0.confirmed = v }
                }))
                    .labelsHidden()
                    .toggleStyle(.checkbox)
                    .help("Checked with the author or rights holder")
            }
            .width(72)
        }
        .contextMenu(forSelectionType: UUID.self) { ids in
            if let id = ids.first, let p = document.project.pronunciations.first(where: { $0.id == id }), let pid = p.firstParagraphID {
                Button("Show First Use in Manuscript") { workspace.show(paragraph: pid) }
            }
            Button(ids.count > 1 ? "Dismiss \(ids.count) Terms" : "Dismiss") {
                document.change("Dismiss Terms", undoManager: undoManager) { p in
                    for i in p.pronunciations.indices where ids.contains(p.pronunciations[i].id) { p.pronunciations[i].dismissed = true }
                }
            }
            Button("Delete", role: .destructive) {
                document.change("Delete Terms", undoManager: undoManager) { p in p.pronunciations.removeAll { ids.contains($0.id) } }
            }
        }
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

/// Plays a pronunciation sample kept in the project (InnerVoice no longer records them).
@Observable
final class PronunciationSamplePlayer {
    @ObservationIgnored private var player: AVAudioPlayer?

    func play(_ data: Data) {
        player = try? AVAudioPlayer(data: data)
        player?.play()
    }
}
