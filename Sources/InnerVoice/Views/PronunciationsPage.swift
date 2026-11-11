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
                    Text("InnerVoice lists names, places and made-up words it finds. Add your own with Add Term.")
                } actions: {
                    Button("Add Term") { add() }
                    Button("Find Terms") { refresh() }
                }
            } else {
                table
            }
        }
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
            }
        }
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
                DraftTextField("e.g. SHEV-on", text: binding(p, \.sayItAs)).textFieldStyle(.plain)
            }
            .width(min: 120, ideal: 170)
            TableColumn("Notes") { p in
                DraftTextField("Origin, stress, source", text: binding(p, \.notes)).textFieldStyle(.plain).foregroundStyle(.secondary)
            }
            .width(min: 140, ideal: 220)
            TableColumn("Count") { p in
                Text(p.occurrences > 0 ? "\(p.occurrences)" : "—").monospacedDigit().foregroundStyle(.secondary)
            }
            .width(48)
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
