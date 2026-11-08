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
    @State private var recorder = SampleRecorder()

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
                    Text("Inner Voice lists names, places and made-up words it finds. Add your own with Add Term.")
                } actions: {
                    Button("Add Term") { add() }
                    Button("Find Terms") { refresh() }
                }
            } else {
                table
            }
        }
        .navigationTitle("Pronunciations")
        .navigationSubtitle("\(document.project.pronunciations.filter { !$0.sayItAs.isEmpty }.count) of \(document.project.pronunciations.filter { !$0.dismissed }.count) done")
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
                TextField("Term", text: binding(p, \.term)).textFieldStyle(.plain).font(.body.weight(.medium))
            }
            .width(min: 120, ideal: 160)
            TableColumn("Say It As") { p in
                TextField("e.g. SHEV-on", text: binding(p, \.sayItAs)).textFieldStyle(.plain)
            }
            .width(min: 120, ideal: 170)
            TableColumn("Notes") { p in
                TextField("Origin, stress, source", text: binding(p, \.notes)).textFieldStyle(.plain).foregroundStyle(.secondary)
            }
            .width(min: 140, ideal: 220)
            TableColumn("Sample") { p in
                SampleControls(document: document, pronunciation: p, recorder: recorder)
            }
            .width(78)
            TableColumn("Count") { p in
                Text(p.occurrences > 0 ? "\(p.occurrences)" : "—").monospacedDigit().foregroundStyle(.secondary)
            }
            .width(48)
            TableColumn("Confirmed") { p in
                Toggle("Confirmed", isOn: Binding(get: { p.confirmed }, set: { v in update(p.id, "Confirm Pronunciation") { $0.confirmed = v } }))
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

/// Record and play a short sample, stored in the project's Audio folder.
struct SampleControls: View {
    @ObservedObject var document: InnerVoiceDocument
    let pronunciation: Pronunciation
    var recorder: SampleRecorder
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        let recording = recorder.recordingID == pronunciation.id
        HStack(spacing: Space.xs) {
            Button {
                if recording {
                    if let data = recorder.stop() {
                        let name = "\(pronunciation.id.uuidString).m4a"
                        document.setAudio(data, name: name)
                        document.change("Record Sample", undoManager: undoManager) { p in
                            if let i = p.pronunciations.firstIndex(where: { $0.id == pronunciation.id }) { p.pronunciations[i].audioFile = name }
                        }
                    }
                } else {
                    recorder.start(id: pronunciation.id)
                }
            } label: {
                Image(systemName: recording ? "stop.circle.fill" : "record.circle")
                    .foregroundStyle(recording ? Color(nsColor: .attention) : .secondary)
                    .symbolEffect(.pulse, isActive: recording)
            }
            .buttonStyle(.plain)
            .help(recording ? "Stop recording" : "Record a short sample of how to say it")
            .accessibilityLabel(recording ? "Stop recording" : "Record sample")
            if let file = pronunciation.audioFile, let data = document.audio[file] {
                Button {
                    recorder.play(data)
                } label: {
                    Image(systemName: "play.circle").foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("Play the sample")
                .accessibilityLabel("Play sample")
            }
        }
    }
}

@Observable
final class SampleRecorder: NSObject {
    private(set) var recordingID: UUID?
    @ObservationIgnored private var recorder: AVAudioRecorder?
    @ObservationIgnored private var player: AVAudioPlayer?
    @ObservationIgnored private var url: URL?

    func start(id: UUID) {
        // The microphone is asked for only now, on the first Record click.
        AVCaptureDevice.requestAccess(for: .audio) { granted in
            Task { @MainActor in
                guard granted else {
                    let alert = NSAlert()
                    alert.messageText = "Inner Voice Can't Use the Microphone"
                    alert.informativeText = "Turn on Inner Voice in System Settings › Privacy & Security › Microphone to record samples."
                    alert.runModal()
                    return
                }
                self.begin(id: id)
            }
        }
    }

    private func begin(id: UUID) {
        let url = FileManager.default.temporaryDirectory.appending(path: "InnerVoice-sample-\(UUID().uuidString).m4a")
        let settings: [String: Any] = [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 44_100, AVNumberOfChannelsKey: 1,
                                       AVEncoderAudioQualityKey: AVAudioQuality.high.rawValue]
        guard let r = try? AVAudioRecorder(url: url, settings: settings), r.record(forDuration: 15) else { return }
        recorder = r
        self.url = url
        recordingID = id
    }

    func stop() -> Data? {
        recorder?.stop()
        recorder = nil
        recordingID = nil
        guard let url else { return nil }
        defer { try? FileManager.default.removeItem(at: url) }
        return try? Data(contentsOf: url)
    }

    func play(_ data: Data) {
        player = try? AVAudioPlayer(data: data)
        player?.play()
    }
}
