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
/// real person's voice, name it, confirm the right to clone it, and make a voice every
/// project can use. Two ways:
/// - On This Mac: Chatterbox speaks from a clip of up to 15 seconds (its words, found
///   on this Mac); nothing leaves the Mac; free. Saved in Cloned Voices (`LocalClones`).
/// - ElevenLabs: the recordings and the name go to ElevenLabs and the voice is kept in the
///   narrator's account. Needs ElevenLabs set up, the book's OK, and a paid plan. No book text.
/// Nothing else is kept: added files stay where they are, recordings are deleted when the
/// sheet closes unless saved.
struct CloneVoiceSheet: View {
    enum Place: String, CaseIterable, Identifiable {
        case mac, elevenlabs
        var id: String { rawValue }
        var title: String { self == .mac ? "On This Mac" : "ElevenLabs (Cloud)" }
    }

    @ObservedObject var document: InnerVoiceDocument
    @Binding var isPresented: Bool
    let initialTarget: VoiceTarget
    /// Snapshots: samples, the plan and the clip, without a microphone, a network or a model.
    var presetSamples: [CloneSample]? = nil
    var presetAllowance: ElevenLabs.CloneAllowance? = nil
    var presetResult: VoiceInfo? = nil
    var presetPlace: Place? = nil
    var presetClip: (seconds: Double, text: String)? = nil

    @Environment(\.undoManager) private var undoManager
    @State private var place: Place = .mac
    @State private var name = ""
    /// nil: no one yet, just save the voice.
    @State private var target: VoiceTarget?
    @State private var gender: VoiceGender = .neutral
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
    // On This Mac: the clip and what it says.
    @State private var clip: [Float] = []
    @State private var clipSeconds: Double = 0
    @State private var clipText = ""
    @State private var clipFile: URL?
    @State private var preparing = false
    @State private var recognized = true
    @State private var prepareTask: Task<Void, Never>?
    @FocusState private var nameFocused: Bool

    private var totalSeconds: Double { samples.reduce(0) { $0 + $1.seconds } }
    private var chatterboxReady: Bool { ModelSpeech.isInstalled(.chatterbox) }
    private var elevenReady: Bool { ElevenLabs.cloningOffered(for: document.project) }

    private var cast: [BookCharacter] {
        let index = document.index
        return document.project.characters.filter { (index.stats[$0.id]?.lines ?? 0) > 0 }
            .sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
    }

    private func targetName(_ t: VoiceTarget?) -> String? {
        switch t {
        case .narrator?: "the Narrator"
        case .character(let id)?: document.project.character(id)?.name
        case .role(let r)?: "the " + r.voiceTitle
        case nil: nil
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            HStack(spacing: Space.sm) {
                Text("Clone a Voice").font(.title2.weight(.semibold))
                if place == .elevenlabs { CloudBadge() }
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
            gender = Self.gender(for: initialTarget, document: document)
            // ElevenLabs first only for a book that speaks with it; otherwise this Mac.
            place = presetPlace ?? (elevenReady && document.project.engineInUse == .elevenlabs ? .elevenlabs : .mac)
            if let presetSamples { samples = presetSamples }
            if let presetAllowance { allowance = presetAllowance } else if elevenReady { loadAllowance() }
            if let presetResult { result = presetResult }
            if let presetClip { clipSeconds = presetClip.seconds; clipText = presetClip.text }
            nameFocused = true
        }
        .onDisappear {
            prepareTask?.cancel()
            recorder.cancel()
            player.stop()
            for s in samples where s.recorded { try? FileManager.default.removeItem(at: s.url) }
            if let clipFile { try? FileManager.default.removeItem(at: clipFile) }
        }
        .onChange(of: samples) { if presetClip == nil { prepareClip() } }
        .onChange(of: target) { _, t in gender = Self.gender(for: t, document: document) }
    }

    /// The gender the book gives the character the voice is for, as a starting point.
    static func gender(for target: VoiceTarget?, document: InnerVoiceDocument) -> VoiceGender {
        guard let id = target?.characterID, let c = document.project.character(id) else { return .neutral }
        return VoiceCasting.profile(c, project: document.project, index: document.index).gender ?? .neutral
    }

    // MARK: The form

    private var form: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            Picker("Clone", selection: $place) {
                ForEach(Place.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .help("Where the voice is made and kept")

            Text(place == .mac
                 ? "Chatterbox learns the voice from up to 15 seconds of a recording and speaks any line in it. The recording and the voice stay on this Mac, and every InnerVoice project can use it. Free."
                 : "ElevenLabs makes a voice from a recording of one person and saves it in your ElevenLabs voices under the name you choose, so every InnerVoice project (and elevenlabs.io) can use it.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if place == .mac { macNotice } else { cloudNotice }

            Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: Space.md, verticalSpacing: Space.md) {
                GridRow {
                    Text("Name:").gridColumnAlignment(.trailing)
                    TextField("Name", text: $name, prompt: Text("Ellen, warm and low"))
                        .textFieldStyle(.roundedBorder)
                        .focused($nameFocused)
                        .help(place == .mac ? "The name the voice has in every project’s voice list"
                                            : "The name the voice has in every project’s voice list and at elevenlabs.io")
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
                if place == .mac {
                    GridRow {
                        Text("Voice:")
                        Picker("Voice", selection: $gender) {
                            ForEach(VoiceGender.allCases, id: \.self) { Text($0.title).tag($0) }
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                        .fixedSize()
                        .help("Lists the voice with the female, male or neutral voices")
                    }
                }
            }

            samplesSection
            if place == .mac, !samples.isEmpty { clipSection }

            VStack(alignment: .leading, spacing: Space.sm) {
                if place == .elevenlabs {
                    Toggle("Remove background noise", isOn: $removeNoise)
                        .help("ElevenLabs cleans up the recordings first. Leave it off for clean recordings: it can make them sound worse.")
                }
                Toggle(isOn: $rightsConfirmed) {
                    Text(place == .mac
                         ? "I have the right and the consent of the person whose voice this is to clone it."
                         : "I have the right and the consent of the person whose voice this is to clone it, and I accept ElevenLabs’ terms for voice cloning.")
                        .fixedSize(horizontal: false, vertical: true)
                }
                Text(place == .mac
                     ? "The recordings never leave this Mac. The words in them are found on this Mac too."
                     : "The recordings and the name are sent to ElevenLabs and kept in your ElevenLabs account. Cloning uses one of your plan’s voice slots. No text from your book is sent.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if cloning {
                ProgressView { Text(place == .mac ? "Saving the voice…" : "Cloning the voice with ElevenLabs…").font(.callout) }
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

    private var minimumSeconds: Double { place == .mac ? CloneClip.shortest : ElevenLabs.cloneMinimumSeconds }

    private var canClone: Bool {
        guard !cloning, !recorder.isRecording, rightsConfirmed, !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        switch place {
        case .mac:
            return chatterboxReady && !preparing && clipSeconds >= CloneClip.shortest
                && !clipText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        case .elevenlabs:
            return elevenReady && totalSeconds >= ElevenLabs.cloneMinimumSeconds
                && allowance?.canClone != false && allowance?.slotsFull != true
        }
    }

    private var cloneHelp: String {
        if place == .mac, !chatterboxReady { return "Download Chatterbox first" }
        if place == .elevenlabs, !elevenReady { return "Set up ElevenLabs for this book first" }
        if name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "Name the voice first" }
        if place == .mac, preparing { return "Finding the words in the recording…" }
        if place == .mac, clipSeconds < CloneClip.shortest { return "Add at least \(Int(CloneClip.shortest)) seconds of the voice" }
        if place == .mac, clipText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "Type what the clip says" }
        if place == .elevenlabs, totalSeconds < ElevenLabs.cloneMinimumSeconds { return "Add at least 10 seconds of the voice" }
        if !rightsConfirmed { return "Confirm you have the right to clone this voice" }
        return place == .mac ? "Save the voice as “\(name)” on this Mac" : "Send the recordings to ElevenLabs and save the voice as “\(name)”"
    }

    /// On This Mac needs Chatterbox downloaded (about 3.2 GB, once).
    @ViewBuilder
    private var macNotice: some View {
        if !chatterboxReady, let pack = VoicePack.pack(for: .chatterbox) {
            VStack(alignment: .leading, spacing: Space.sm) {
                Label("Cloning on this Mac uses Chatterbox, a free download (\(pack.sizeDescription), once).", systemImage: "arrow.down.circle")
                    .font(.callout)
                VoicePackRow(pack: pack)
            }
            .padding(Space.md)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10))
        } else if !(document.project.engineInUse == .chatterbox || document.project.engineInUse == .mixed) {
            Text("This book speaks with \(document.project.engineInUse.title). Voices cloned on this Mac speak with Chatterbox: to hear one in this book, choose Chatterbox or Mix Engines in the toolbar.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// ElevenLabs needs a key and the narrator's yes (Settings), this book's OK, and a paid plan.
    @ViewBuilder
    private var cloudNotice: some View {
        if !ElevenLabs.shared.isSetUp {
            notice("Set up ElevenLabs in Settings › Voices first: your API key and your OK to send text to ElevenLabs.",
                   button: "Open Settings") { SystemHooks.openSettings() }
        } else if document.project.settings.cloudVoicesAllowed == nil {
            notice("This book hasn’t allowed ElevenLabs yet. Allowing it sends the lines its ElevenLabs voices speak, as text.",
                   button: "Allow for This Book") {
                document.setCloudVoicesAllowed(true, undoManager: undoManager)
                loadAllowance()
            }
        } else if !elevenReady {
            Text("This book speaks with \(document.project.engineInUse.title). You can still clone a voice with ElevenLabs; to hear it in this book, choose ElevenLabs or Mix Engines in the toolbar.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        } else {
            planNotice
        }
    }

    @ViewBuilder
    private var planNotice: some View {
        if let a = allowance, !a.canClone {
            notice("Voice cloning needs a paid ElevenLabs plan (Starter or above). Your plan doesn’t include it. Cloning On This Mac is free.",
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
                    .foregroundStyle(totalSeconds > 0 && totalSeconds < minimumSeconds ? Color(nsColor: .attention) : .secondary)
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
            Text(place == .mac
                 ? "15 to 30 seconds of one person speaking clearly is plenty, with no music, echo or other voices. The first 15 seconds of speech are used."
                 : "1 to 2 minutes of one person speaking clearly works best, with no music, echo or other voices. Keep the same tone and distance from the microphone throughout.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var durationSummary: String {
        let t = totalSeconds
        let least = Int(minimumSeconds)
        if t == 0 { return "At least \(least) seconds" }
        let time = Self.clock(t)
        if t < minimumSeconds { return "\(time) in all. At least \(Self.clock(minimumSeconds)) is needed." }
        if place == .mac { return t > CloneClip.longest ? "\(time) in all. The first 0:15 is used." : "\(time) in all" }
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

    // MARK: The clip (On This Mac)

    /// The part the voice is learned from, and what it says (editable: the clone copies the
    /// voice best when the words match exactly).
    private var clipSection: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack {
                Text("The Clip").font(.headline)
                Spacer()
                if preparing {
                    ProgressView().controlSize(.small)
                    Text("Finding the words…").font(.callout).foregroundStyle(.secondary)
                } else if clipSeconds > 0 {
                    if let clipFile {
                        Button { player.toggle(clipFile) } label: {
                            Label(player.playing == clipFile ? "Stop" : "Play Clip", systemImage: player.playing == clipFile ? "stop.fill" : "play.fill")
                        }
                        .controlSize(.small)
                        .help("Hear the part the voice is learned from")
                    }
                    Text(Self.clock(clipSeconds)).monospacedDigit().foregroundStyle(.secondary)
                }
            }
            Text(recognized ? "What the clip says. Fix any word that’s wrong: the clone copies the voice best when every word matches."
                            : "InnerVoice couldn’t find the words on this Mac. Type exactly what the clip says.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            TextEditor(text: $clipText)
                .font(.callout)
                .frame(height: 64)
                .scrollContentBackground(.hidden)
                .padding(Space.xs)
                .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 6))
                .disabled(preparing)
                .accessibilityLabel("What the clip says")
        }
    }

    /// Joins the recordings, finds the words on this Mac, and cuts the clip at a pause.
    private func prepareClip() {
        prepareTask?.cancel()
        let urls = samples.map(\.url)
        guard !urls.isEmpty else {
            clip = []; clipSeconds = 0; clipText = ""
            return
        }
        preparing = true
        prepareTask = Task {
            defer { if !Task.isCancelled { preparing = false } }
            do {
                let audio = try await Task.detached { try CloneAudio.load(urls) }.value
                guard !Task.isCancelled else { return }
                let rate = SpeechAudio.format.sampleRate
                var piece = audio
                var text = ""
                recognized = false
                if let found = await CloneAudio.transcribe(audio), let span = CloneClip.span(found.words) {
                    let from = Int(span.start * rate), to = min(audio.count, Int(span.end * rate))
                    if from < to { piece = Array(audio[from..<to]) }
                    text = CloneClip.text(found.text, upTo: span.textEnd)
                    recognized = true
                } else {
                    piece = Array(audio.prefix(Int(CloneClip.longest * rate)))
                }
                guard !Task.isCancelled else { return }
                clip = ModelSpeech.level(piece)
                clipSeconds = Double(clip.count) / rate
                clipText = text
                if let old = clipFile { try? FileManager.default.removeItem(at: old) }
                let file = FileManager.default.temporaryDirectory.appending(path: "InnerVoice-clip-\(UUID().uuidString).caf")
                SpeechRenderer.write(try ModelSpeech.buffer(clip), to: file)
                clipFile = file
                if clipSeconds < CloneClip.shortest { error = LocalClones.CloneError.tooShort.localizedDescription } else { error = nil }
            } catch {
                self.error = error.localizedDescription
            }
        }
    }

    // MARK: Done

    private func done(_ v: VoiceInfo) -> some View {
        let character: BookCharacter? = target?.characterID.flatMap { document.project.character($0) }
        let engine = document.project.engineInUse
        // A voice is heard in a book that speaks with its engine, or mixes engines.
        let fits = engine == .mixed || engine == v.engine
        return VStack(alignment: .leading, spacing: Space.lg) {
            Label(v.engine == .chatterbox ? "“\(v.name)” is saved on this Mac." : "“\(v.name)” is saved in your ElevenLabs voices.",
                  systemImage: "checkmark.circle.fill")
                .font(.headline)
            Text(v.engine == .chatterbox
                 ? "It’s listed first among the Chatterbox voices in every project, marked Cloned. Casting never chooses it on its own: it goes only where you put it. Rename or delete it in Settings › Voices."
                 : "It’s listed first among the ElevenLabs voices in every project, marked Cloned. Casting never chooses it on its own: it goes only where you put it. Rename or delete it in Settings › Voices.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if !fits {
                Text("This book speaks with \(engine.title). To hear “\(v.name)” in it, choose \(v.engine.title) or Mix Engines in the toolbar, then pick it on the Voices page.")
                    .font(.callout)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Button("Play", systemImage: "play.fill") {
                PlayCommand.audition(v.choice, character: character, document: document)
            }
            .help("Hear the new voice say \(character.map { "one of \($0.firstName)’s lines" } ?? "a line")")
            HStack {
                Spacer()
                if fits, let who = targetName(target) {
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
        error = nil
        switch place {
        case .mac:
            do {
                let c = try LocalClones.shared.add(name: name, gender: gender, audio: clip, text: clipText)
                result = LocalClones.info(c)
            } catch {
                self.error = error.localizedDescription
            }
        case .elevenlabs:
            cloning = true
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
