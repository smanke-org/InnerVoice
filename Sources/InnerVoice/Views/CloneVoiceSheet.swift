import AppKit
import AVFoundation
import InnerVoiceKit
import SwiftUI
import UniformTypeIdentifiers

/// One recording for a clone: recorded here (a temporary file, deleted when the sheet
/// closes unless saved) or a file the narrator added (left where it is).
struct CloneSample: Identifiable, Equatable {
    let id = UUID()
    var url: URL
    var seconds: Double
    var recorded: Bool

    var name: String { recorded ? "Recording" : url.lastPathComponent }
}

/// Clone a Voice (Book › Clone a Voice…, or the Voices page): record or add a sample of a
/// real person's voice, name it, confirm the right to clone it, and ElevenLabs makes a voice
/// that's saved in the narrator's ElevenLabs account, so every project can use it. Offered
/// only to a book that uses ElevenLabs. The recordings and the name are all that's sent;
/// no book text. Nothing is kept here: added files stay where they are, recordings are
/// deleted when the sheet closes unless saved.
struct CloneVoiceSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    @Binding var isPresented: Bool
    let initialTarget: VoiceTarget
    /// Snapshots: samples and the plan, without a microphone or a network.
    var presetSamples: [CloneSample]? = nil
    var presetAllowance: ElevenLabs.CloneAllowance? = nil
    var presetResult: VoiceInfo? = nil

    @Environment(\.undoManager) private var undoManager
    @State private var name = ""
    /// nil: no one yet, just save the voice.
    @State private var target: VoiceTarget?
    @State private var samples: [CloneSample] = []
    @State private var removeNoise = false
    @State private var rightsConfirmed = false
    @State private var allowance: ElevenLabs.CloneAllowance?
    @State private var cloning = false
    @State private var error: String?
    @State private var result: VoiceInfo?
    @State private var recorder = CloneRecorder()
    @State private var player = SamplePlayer()
    @State private var dropping = false
    @FocusState private var nameFocused: Bool

    private var totalSeconds: Double { samples.reduce(0) { $0 + $1.seconds } }

    private var cast: [BookCharacter] {
        let index = document.index
        return document.project.characters.filter { (index.stats[$0.id]?.lines ?? 0) > 0 }
            .sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
    }

    private func targetName(_ t: VoiceTarget?) -> String? {
        switch t {
        case .narrator?: "the Narrator"
        case .character(let id)?: document.project.character(id)?.name
        case nil: nil
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            HStack(spacing: Space.sm) {
                Text("Clone a Voice").font(.title2.weight(.semibold))
                CloudBadge()
            }
            if let result {
                done(result)
            } else {
                form
            }
        }
        .padding(Space.xl)
        .frame(width: 600)
        .onAppear {
            target = initialTarget
            if let presetSamples { samples = presetSamples }
            if let presetAllowance { allowance = presetAllowance } else { loadAllowance() }
            if let presetResult { result = presetResult }
            nameFocused = true
        }
        .onDisappear {
            recorder.cancel()
            player.stop()
            for s in samples where s.recorded { try? FileManager.default.removeItem(at: s.url) }
        }
    }

    // MARK: The form

    private var form: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            Text("ElevenLabs makes a voice from a recording of one person and saves it in your ElevenLabs voices under the name you choose, so every InnerVoice project (and elevenlabs.io) can use it.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            planNotice

            Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: Space.md, verticalSpacing: Space.md) {
                GridRow {
                    Text("Name:").gridColumnAlignment(.trailing)
                    TextField("Name", text: $name, prompt: Text("Ellen, warm and low"))
                        .textFieldStyle(.roundedBorder)
                        .focused($nameFocused)
                        .help("The name the voice has in every project’s voice list and at elevenlabs.io")
                }
                GridRow {
                    Text("Use for:")
                    Picker("Use for", selection: $target) {
                        Text("No One Yet").tag(VoiceTarget?.none)
                        Divider()
                        Text("Narrator").tag(VoiceTarget?.some(.narrator))
                        ForEach(cast) { c in Text(c.name).tag(VoiceTarget?.some(.character(c.id))) }
                    }
                    .labelsHidden()
                    .fixedSize()
                    .help("Who gets the new voice in this book once it’s made. You can give it to anyone later.")
                }
            }

            samplesSection

            VStack(alignment: .leading, spacing: Space.sm) {
                Toggle("Remove background noise", isOn: $removeNoise)
                    .help("ElevenLabs cleans up the recordings first. Leave it off for clean recordings: it can make them sound worse.")
                Toggle(isOn: $rightsConfirmed) {
                    Text("I have the right and the consent of the person whose voice this is to clone it, and I accept ElevenLabs’ terms for voice cloning.")
                        .fixedSize(horizontal: false, vertical: true)
                }
                Text("The recordings and the name are sent to ElevenLabs and kept in your ElevenLabs account. Cloning uses one of your plan’s voice slots. No text from your book is sent.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if cloning {
                ProgressView { Text("Cloning the voice with ElevenLabs…").font(.callout) }
                    .progressViewStyle(.linear)
            }
            if let error {
                Label(error, systemImage: "exclamationmark.triangle")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Spacer()
                Button("Cancel") { isPresented = false }
                    .keyboardShortcut(.cancelAction)
                Button("Clone Voice", action: clone)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canClone)
                    .help(cloneHelp)
            }
        }
    }

    private var canClone: Bool {
        !cloning && !recorder.isRecording && rightsConfirmed
            && !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && totalSeconds >= ElevenLabs.cloneMinimumSeconds
            && allowance?.canClone != false && allowance?.slotsFull != true
    }

    private var cloneHelp: String {
        if name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "Name the voice first" }
        if totalSeconds < ElevenLabs.cloneMinimumSeconds { return "Add at least 10 seconds of the voice" }
        if !rightsConfirmed { return "Confirm you have the right to clone this voice" }
        return "Send the recordings to ElevenLabs and save the voice as “\(name)”"
    }

    @ViewBuilder
    private var planNotice: some View {
        if let a = allowance, !a.canClone {
            notice("Voice cloning needs a paid ElevenLabs plan (Starter or above). Your plan doesn’t include it.",
                   button: "See Plans") { NSWorkspace.shared.open(ElevenLabs.plansPage) }
        } else if let a = allowance, a.slotsFull {
            notice("All \(a.slotLimit) of your plan’s voice slots are in use. Delete a voice you no longer need in Settings › Voices, or at elevenlabs.io.",
                   button: "Open Settings") { SystemHooks.openSettings() }
        } else if let a = allowance, a.slotLimit > 0 {
            Text("Uses 1 of your plan’s voice slots (\(a.slotsUsed) of \(a.slotLimit) in use).")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
    }

    private func notice(_ text: String, button: String, action: @escaping () -> Void) -> some View {
        HStack(alignment: .top, spacing: Space.sm) {
            Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange)
            Text(text).font(.callout).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: Space.sm)
            Button(button, action: action).controlSize(.small)
        }
        .padding(Space.md)
        .background(.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
    }

    // MARK: Samples

    private var samplesSection: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack {
                Text("Recordings").font(.headline)
                Spacer()
                Text(durationSummary)
                    .font(.callout)
                    .monospacedDigit()
                    .foregroundStyle(totalSeconds > 0 && totalSeconds < ElevenLabs.cloneMinimumSeconds ? Color(nsColor: .attention) : .secondary)
            }
            VStack(spacing: 0) {
                if samples.isEmpty, !recorder.isRecording {
                    Text("Record the voice here, or add audio files (MP3, WAV, M4A…). Drop files here too.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, minHeight: 44)
                }
                ForEach(samples) { s in sampleRow(s) }
                if recorder.isRecording { recordingRow }
            }
            .padding(Space.xs)
            .background(.quaternary.opacity(dropping ? 0.9 : 0.4), in: RoundedRectangle(cornerRadius: 8))
            .dropDestination(for: URL.self) { urls, _ in
                add(urls.filter { UTType(filenameExtension: $0.pathExtension)?.conforms(to: .audio) == true })
                return true
            } isTargeted: { dropping = $0 }
            HStack {
                Button(recorder.isRecording ? "Stop Recording" : "Record…", systemImage: recorder.isRecording ? "stop.circle.fill" : "record.circle") {
                    if recorder.isRecording { finishRecording() } else { recorder.start() }
                }
                .help(recorder.isRecording ? "Stop and keep this recording" : "Record the voice with this Mac’s microphone (up to 3 minutes)")
                Button("Add Files…", systemImage: "plus", action: chooseFiles)
                    .disabled(recorder.isRecording)
                    .help("Add recordings of the voice")
                Spacer()
            }
            if recorder.isRecording || samples.isEmpty { readAloud }
            Text("1 to 2 minutes of one person speaking clearly works best, with no music, echo or other voices. Keep the same tone and distance from the microphone throughout.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var durationSummary: String {
        let t = totalSeconds
        if t == 0 { return "At least 10 seconds" }
        let time = Self.clock(t)
        if t < ElevenLabs.cloneMinimumSeconds { return "\(time) in all. At least 0:10 is needed." }
        if t < ElevenLabs.cloneBestSeconds.lowerBound { return "\(time) in all. A minute or more sounds better." }
        if t > ElevenLabs.cloneMostSeconds { return "\(time) in all. Past 3:00 adds little." }
        return "\(time) in all, a good length"
    }

    static func clock(_ seconds: Double) -> String {
        let s = Int(seconds.rounded())
        return String(format: "%d:%02d", s / 60, s % 60)
    }

    private func sampleRow(_ s: CloneSample) -> some View {
        HStack(spacing: Space.sm) {
            Button { player.toggle(s.url) } label: {
                Image(systemName: player.playing == s.url ? "stop.circle" : "play.circle").font(.title3)
            }
            .buttonStyle(.plain)
            .help(player.playing == s.url ? "Stop" : "Listen to this recording")
            Image(systemName: s.recorded ? "mic" : "waveform").foregroundStyle(.secondary)
            Text(s.name).lineLimit(1).truncationMode(.middle)
            Spacer()
            Text(Self.clock(s.seconds)).monospacedDigit().foregroundStyle(.secondary)
            if s.recorded {
                Button("Save…") { save(s) }
                    .controlSize(.small)
                    .help("Keep a copy of this recording. Otherwise it’s deleted when this closes.")
            }
            Button { remove(s) } label: { Image(systemName: "minus.circle") }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .help("Remove this recording")
        }
        .padding(.horizontal, Space.sm)
        .padding(.vertical, Space.xs)
    }

    private var recordingRow: some View {
        HStack(spacing: Space.sm) {
            Image(systemName: "record.circle.fill")
                .foregroundStyle(Color(nsColor: .attention))
                .symbolEffect(.pulse, isActive: true)
            Text("Recording…")
            ProgressView(value: recorder.level).frame(width: 120).help("Microphone level")
            Spacer()
            Text("\(Self.clock(recorder.elapsed)) of 3:00").monospacedDigit().foregroundStyle(.secondary)
        }
        .padding(.horizontal, Space.sm)
        .padding(.vertical, Space.xs)
        .onChange(of: recorder.finishedURL) { _, url in if url != nil { finishRecording() } }
    }

    /// Something to read while recording: varied sentences, a question, some feeling.
    private var readAloud: some View {
        DisclosureGroup("Something to read aloud") {
            Text(Self.passage)
                .font(.callout)
                .textSelection(.enabled)
                .padding(Space.sm)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 6))
        }
        .font(.callout)
    }

    static let passage = """
    The train was late again, so I walked. It was the kind of autumn morning that makes you forgive the whole year: cold air, warm light, and leaves piled along the curb like they'd been swept there on purpose.

    "You're not seriously going in there," my brother said when he caught up with me. "Not after last time."

    I told him I was. I told him a lot of things, most of them true. Have you ever noticed how easy it is to be brave when someone is watching? It's the quiet afterward that's hard.

    We stood at the door for a long moment. Then I laughed, because what else could I do, and I knocked three times.
    """

    // MARK: Done

    private func done(_ v: VoiceInfo) -> some View {
        let character: BookCharacter? = target?.characterID.flatMap { document.project.character($0) }
        return VStack(alignment: .leading, spacing: Space.lg) {
            Label("“\(v.name)” is saved in your ElevenLabs voices.", systemImage: "checkmark.circle.fill")
                .font(.headline)
            Text("It’s listed first among the ElevenLabs voices in every project, marked Cloned. Casting never chooses it on its own: it goes only where you put it. Rename or delete it in Settings › Voices.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button("Play", systemImage: "play.fill") {
                PlayCommand.audition(v.choice, character: character, document: document)
            }
            .help("Hear the new voice say \(character.map { "one of \($0.firstName)’s lines" } ?? "a line")")
            HStack {
                Spacer()
                if let who = targetName(target) {
                    Button("Done") { isPresented = false }
                        .keyboardShortcut(.cancelAction)
                    Button("Use for \(who)") {
                        document.setVoice(v.choice, for: target?.characterID, undoManager: undoManager)
                        isPresented = false
                    }
                    .keyboardShortcut(.defaultAction)
                    .help("Give \(who) this voice in this book")
                } else {
                    Button("Done") { isPresented = false }
                        .keyboardShortcut(.defaultAction)
                }
            }
        }
    }

    // MARK: Actions

    private func loadAllowance() {
        Task {
            allowance = try? await ElevenLabs.shared.cloneAllowance()
        }
    }

    private func finishRecording() {
        guard let url = recorder.stop() else { return }
        Task {
            let seconds = await Self.duration(of: url)
            if seconds > 0.5 { samples.append(CloneSample(url: url, seconds: seconds, recorded: true)) } else { try? FileManager.default.removeItem(at: url) }
        }
    }

    private func chooseFiles() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.audio]
        panel.allowsMultipleSelection = true
        panel.message = "Choose recordings of one person’s voice"
        panel.prompt = "Add"
        guard panel.runModal() == .OK else { return }
        add(panel.urls)
    }

    private func add(_ urls: [URL]) {
        Task {
            var unreadable: [String] = []
            for url in urls where !samples.contains(where: { $0.url == url }) {
                let seconds = await Self.duration(of: url)
                if seconds > 0 { samples.append(CloneSample(url: url, seconds: seconds, recorded: false)) } else { unreadable.append(url.lastPathComponent) }
            }
            error = unreadable.isEmpty ? nil : "InnerVoice can’t read \(unreadable.joined(separator: ", ")) as audio."
        }
    }

    private func remove(_ s: CloneSample) {
        if player.playing == s.url { player.stop() }
        samples.removeAll { $0.id == s.id }
        if s.recorded { try? FileManager.default.removeItem(at: s.url) }
    }

    private func save(_ s: CloneSample) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.mpeg4Audio]
        panel.nameFieldStringValue = (name.isEmpty ? "Voice Recording" : name) + ".m4a"
        guard panel.runModal() == .OK, let dest = panel.url else { return }
        try? FileManager.default.removeItem(at: dest)
        do { try FileManager.default.copyItem(at: s.url, to: dest) } catch { self.error = error.localizedDescription }
    }

    private func clone() {
        guard canClone else { return }
        player.stop()
        cloning = true
        error = nil
        let urls = samples.map(\.url)
        Task {
            defer { cloning = false }
            do {
                result = try await ElevenLabs.shared.cloneVoice(name: name, samples: urls, removeBackgroundNoise: removeNoise,
                                                                labels: [:], rightsConfirmed: rightsConfirmed)
            } catch is CancellationError {
            } catch {
                self.error = error.localizedDescription
                if case ElevenLabsError.voiceSlotsFull = error { allowance?.slotsUsed = allowance?.slotLimit ?? 0 }
            }
        }
    }

    static func duration(of url: URL) async -> Double {
        guard let d = try? await AVURLAsset(url: url).load(.duration), d.isNumeric else { return 0 }
        return d.seconds
    }
}

/// Records a clone sample: AAC at a high bit rate, up to three minutes, with a level meter.
@Observable
@MainActor
final class CloneRecorder {
    private(set) var isRecording = false
    private(set) var elapsed: Double = 0
    private(set) var level: Double = 0
    /// Set when the three minutes run out, so the sheet keeps the recording.
    private(set) var finishedURL: URL?
    @ObservationIgnored private var recorder: AVAudioRecorder?
    @ObservationIgnored private var url: URL?
    @ObservationIgnored private var ticker: Task<Void, Never>?

    func start() {
        // The microphone is asked for only now, on the first Record click.
        AVCaptureDevice.requestAccess(for: .audio) { granted in
            Task { @MainActor in
                guard granted else {
                    let alert = NSAlert()
                    alert.messageText = "InnerVoice Can't Use the Microphone"
                    alert.informativeText = "Turn on InnerVoice in System Settings › Privacy & Security › Microphone to record a voice."
                    alert.runModal()
                    return
                }
                self.begin()
            }
        }
    }

    private func begin() {
        let url = FileManager.default.temporaryDirectory.appending(path: "InnerVoice-clone-\(UUID().uuidString).m4a")
        let settings: [String: Any] = [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 44_100, AVNumberOfChannelsKey: 1,
                                       AVEncoderBitRateKey: 192_000, AVEncoderAudioQualityKey: AVAudioQuality.max.rawValue]
        guard let r = try? AVAudioRecorder(url: url, settings: settings) else { return }
        r.isMeteringEnabled = true
        guard r.record(forDuration: ElevenLabs.cloneMostSeconds) else { return }
        recorder = r
        self.url = url
        finishedURL = nil
        elapsed = 0
        isRecording = true
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(100))
                guard let self, let r = self.recorder else { return }
                if !r.isRecording {
                    self.finishedURL = self.url
                    return
                }
                r.updateMeters()
                self.elapsed = r.currentTime
                // -50 dB and below reads as silence.
                self.level = max(0, min(1, (Double(r.averagePower(forChannel: 0)) + 50) / 50))
            }
        }
    }

    /// Stops and hands back the recording's file (the caller owns it now).
    func stop() -> URL? {
        ticker?.cancel()
        recorder?.stop()
        recorder = nil
        isRecording = false
        level = 0
        defer { url = nil }
        return url
    }

    /// Stops and throws the recording away.
    func cancel() {
        if let u = stop() { try? FileManager.default.removeItem(at: u) }
    }
}

/// Plays one sample at a time.
@Observable
@MainActor
final class SamplePlayer: NSObject, AVAudioPlayerDelegate {
    private(set) var playing: URL?
    @ObservationIgnored private var player: AVAudioPlayer?

    func toggle(_ url: URL) {
        if playing == url { stop(); return }
        stop()
        guard let p = try? AVAudioPlayer(contentsOf: url) else { return }
        p.delegate = self
        p.play()
        player = p
        playing = url
    }

    func stop() {
        player?.stop()
        player = nil
        playing = nil
    }

    nonisolated func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        Task { @MainActor in self.stop() }
    }
}
