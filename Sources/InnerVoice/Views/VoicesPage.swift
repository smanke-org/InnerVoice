import AppKit
import InnerVoiceKit
import SwiftUI

/// Who reads what: the narrator and every character with lines on the left, and on the
/// right what the book says they sound like and the voices to choose from, each one
/// playable with that character's own words.
struct VoicesPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State private var query = ""

    private var engine: VoiceEngine { document.project.engineInUse }

    private var cast: [BookCharacter] {
        let index = document.index
        return document.project.characters.filter { (index.stats[$0.id]?.lines ?? 0) > 0 }
            .sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
    }

    var body: some View {
        HStack(spacing: 0) {
            list
                .frame(width: 280)
            Divider()
            VoiceTargetDetail(document: document, workspace: workspace, target: workspace.voiceTarget)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .navigationTitle("Voices")
        .navigationSubtitle(subtitle)
        .toolbar {
            ToolbarItem {
                Picker("Engine", selection: Binding(get: { engine }, set: { document.setVoiceEngine($0, undoManager: undoManager) })) {
                    ForEach(EngineSwitches.shared.enabled) { e in
                        Text(e.title + (ModelSpeech.isInstalled(e) ? "" : " (Not Downloaded)")).tag(e)
                    }
                }
                .help("The speech engine the project’s voices use. Each engine keeps its own casting.")
            }
            ToolbarItem {
                Button("Cast Voices Again", systemImage: "wand.and.stars") { document.castVoicesAgain(undoManager: undoManager) }
                    .help("Choose every voice again from the character summaries. Voices you chose stay.")
            }
            ToolbarItem {
                PlayStopButton(document: document, workspace: workspace)
            }
        }
        .onAppear {
            VoiceCastingRun.castIfNeeded(document: document)
            ModelSpeech.prepare(engine)
        }
    }

    private var subtitle: String {
        let unsure = cast.filter { document.project.voiceNeedsYou($0, engine: engine) }.count
        return unsure == 1 ? "1 voice needs you" : unsure > 1 ? "\(unsure) voices need you" : engine.title
    }

    private var list: some View {
        List(selection: Binding(get: { workspace.voiceTarget }, set: { workspace.voiceTarget = $0 ?? .narrator })) {
            Section("Narration") {
                row(name: "Narrator", color: nil, voice: document.project.settings.narratorVoice(for: engine), needs: false)
                    .tag(VoiceTarget.narrator)
            }
            Section("Cast") {
                ForEach(cast.filter { query.isEmpty || $0.matches(query) }) { c in
                    row(name: c.name, color: Color(hexString: c.colorHex), voice: c.voice(for: engine), needs: document.project.voiceNeedsYou(c, engine: engine))
                        .tag(VoiceTarget.character(c.id))
                }
            }
        }
        .listStyle(.inset)
        .searchable(text: $query, placement: .sidebar, prompt: "Find a character")
    }

    private func row(name: String, color: Color?, voice: CastVoice?, needs: Bool) -> some View {
        HStack(spacing: Space.sm) {
            if let color {
                Circle().fill(color).frame(width: 10, height: 10)
            } else {
                Image(systemName: "book.closed").foregroundStyle(.secondary).frame(width: 10)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(name).lineLimit(1)
                Text(voice.map { VoiceCatalog.name(of: $0.choice) } ?? "No voice yet")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            if needs {
                Text("?")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(Color(nsColor: .attention))
                    .help("Casting isn’t sure of this voice. Listen, then confirm it or choose another.")
            } else if voice?.source == .user {
                Image(systemName: "checkmark").font(.caption).foregroundStyle(.tertiary)
                    .help("You chose this voice")
            }
        }
        .padding(.vertical, 2)
    }
}

/// The narrator or a character, on the Voices page.
enum VoiceTarget: Hashable {
    case narrator
    case character(UUID)

    var characterID: UUID? {
        if case .character(let id) = self { return id }
        return nil
    }
}

/// One character's (or the narrator's) voice: what the book says, the voice now, the best
/// fits and every voice.
struct VoiceTargetDetail: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    let target: VoiceTarget

    var body: some View {
        let character = target.characterID.flatMap { document.project.character($0) }
        ScrollView {
            VStack(alignment: .leading, spacing: Space.xl) {
                if let character {
                    HStack(spacing: Space.md) {
                        Avatar(character: character, size: 44)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(character.name).font(.title2.weight(.semibold))
                            Text("\(document.index.stats[character.id]?.lines ?? 0) lines")
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Open \(character.firstName)") { workspace.showCharacter(character.id) }
                            .help("Open \(character.name)’s page in Cast")
                    }
                    CastingProfileView(document: document, character: character)
                } else {
                    VStack(alignment: .leading, spacing: Space.xs) {
                        Text("Narrator").font(.title2.weight(.semibold))
                        Text("Reads the narration in a table read, and lines nobody is cast for.")
                            .foregroundStyle(.secondary)
                    }
                }
                VoiceChooser(document: document, characterID: character?.id, compact: false)
            }
            .padding(Space.margin)
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .id(target)
    }
}

/// What casting read about a character: gender, age, accent and voice, and where from.
struct CastingProfileView: View {
    @ObservedObject var document: InnerVoiceDocument
    let character: BookCharacter

    var body: some View {
        let p = VoiceCasting.profile(character, project: document.project, index: document.index)
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack(spacing: Space.xl) {
                trait("Gender", p.gender?.title)
                trait("Age", p.age?.title)
                trait("Accent", p.accent)
                trait("Voice", p.timbre.isEmpty ? nil : p.timbre.joined(separator: ", "))
            }
            if p.gender == nil {
                Text("The book hasn’t said whether \(character.firstName) is a woman or a man. Build Summaries reads it for details, or choose a voice below.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else if !p.evidence.isEmpty {
                Text(p.evidence.joined(separator: ". ") + ".")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(Space.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
    }

    private func trait(_ label: String, _ value: String?) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            Text(value ?? "Unknown").foregroundStyle(value == nil ? .tertiary : .primary)
        }
    }
}

/// The voice now, the best fits, and (when not compact) every voice of the engine. Every
/// voice can be heard with the character's own words before choosing it.
struct VoiceChooser: View {
    @ObservedObject var document: InnerVoiceDocument
    /// nil is the narrator.
    let characterID: UUID?
    var compact = false
    @Environment(\.undoManager) private var undoManager
    @State private var filter: VoiceGender?
    @State private var designDraft = ""

    private var engine: VoiceEngine { document.project.engineInUse }
    private var character: BookCharacter? { characterID.flatMap { document.project.character($0) } }
    private var voice: CastVoice? {
        characterID == nil ? document.project.settings.narratorVoice(for: engine) : character?.voice(for: engine)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            current
            if document.project.settings.engine != engine {
                Label("\(document.project.settings.engine.title) is turned off in Settings › Voices, so this project uses \(engine.title).", systemImage: "info.circle")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            if !ModelSpeech.isInstalled(engine), let pack = VoicePack.pack(for: engine) {
                VStack(alignment: .leading, spacing: Space.sm) {
                    Label("\(engine.title) isn’t downloaded yet, so these voices can’t be heard.", systemImage: "arrow.down.circle")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    VoicePackRow(pack: pack)
                }
            }
            if engine == .qwen {
                design
            } else {
                suggestions
                if !compact { allVoices }
            }
        }
    }

    // MARK: The voice now

    private var current: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack(spacing: Space.md) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Voice").font(.headline)
                    if let v = voice {
                        HStack(spacing: Space.sm) {
                            Text(VoiceCatalog.name(of: v.choice))
                            if let info = VoiceCatalog.info(for: v.choice) {
                                Text(describe(info)).foregroundStyle(.secondary)
                            }
                        }
                        if v.source == .auto {
                            Text(document.project.settings.unsureBelow > v.confidence
                                 ? "Chosen by casting, \(Int(v.confidence * 100))% sure. Listen, then confirm it or choose another."
                                 : "Chosen by casting, \(Int(v.confidence * 100))% sure.")
                                .font(.caption)
                                .foregroundStyle(v.confidence < document.project.settings.unsureBelow ? Color(nsColor: .attention) : .secondary)
                        }
                    } else {
                        Text("No voice yet. Choose one below.").foregroundStyle(.secondary)
                    }
                }
                Spacer()
                if let v = voice {
                    Button("Play", systemImage: "play.fill") { audition(v.choice) }
                        .help("Hear this voice say one of \(character?.firstName ?? "the narrator")’s lines")
                    if v.source == .auto, let id = characterID {
                        Button("Confirm") { document.confirmVoice(for: id, undoManager: undoManager) }
                            .buttonStyle(.borderedProminent)
                            .help("Keep this voice. Casting won’t change it.")
                    }
                }
            }
            if let v = voice, engine == .apple {
                adjustments(v.choice)
            }
        }
    }

    /// Speed and pitch for Apple's voices: an older character a little lower and slower.
    private func adjustments(_ choice: VoiceChoice) -> some View {
        HStack(spacing: Space.xl) {
            slider("Speed", value: choice.rate ?? 1, range: 0.7...1.3) { var c = choice; c.rate = $0 == 1 ? nil : $0; choose(c) }
            slider("Pitch", value: choice.pitch ?? 1, range: 0.7...1.4) { var c = choice; c.pitch = $0 == 1 ? nil : $0; choose(c) }
        }
        .controlSize(.small)
    }

    private func slider(_ label: String, value: Double, range: ClosedRange<Double>, set: @escaping (Double) -> Void) -> some View {
        HStack(spacing: Space.sm) {
            Text(label).font(.callout).foregroundStyle(.secondary)
            Slider(value: Binding(get: { value }, set: { set(($0 * 20).rounded() / 20) }), in: range)
                .frame(width: 140)
            Text(String(format: "%.2f×", value)).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 40)
        }
    }

    // MARK: Best fits

    @ViewBuilder
    private var suggestions: some View {
        let voices = VoiceCatalog.voices(for: engine)
        if let c = character {
            let best = VoiceCasting.suggestions(for: c, project: document.project, index: document.index, voices: voices, limit: 3)
            if !best.isEmpty {
                VStack(alignment: .leading, spacing: Space.sm) {
                    Text("Best Fits").font(.headline)
                    ForEach(best, id: \.voice.id) { item in
                        voiceRow(item.voice)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var allVoices: some View {
        let voices = VoiceCatalog.voices(for: engine).filter { filter == nil || $0.gender == filter }
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack {
                Text("All \(engine.title)").font(.headline)
                Spacer()
                Picker("Show", selection: $filter) {
                    Text("All").tag(VoiceGender?.none)
                    ForEach(VoiceGender.allCases, id: \.self) { Text($0.title).tag(VoiceGender?.some($0)) }
                }
                .pickerStyle(.segmented)
                .fixedSize()
                .help("Show voices by how they sound")
            }
            ForEach(voices) { v in voiceRow(v) }
            if engine == .apple {
                HStack(spacing: Space.sm) {
                    Text("More natural voices are free to download in System Settings.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    Button("Get More Voices…") {
                        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.universalaccess?SpokenContent")!)
                    }
                    .help("Open System Settings › Accessibility › Spoken Content › System Voice › Manage Voices, and download Premium voices")
                }
                .padding(.top, Space.sm)
            }
        }
    }

    private func voiceRow(_ v: VoiceInfo) -> some View {
        let choice = VoiceChoice(engine: v.engine, voiceID: v.voiceID)
        let isCurrent = voice?.choice.voiceID == v.voiceID
        let users = usedBy(v.voiceID)
        return HStack(spacing: Space.md) {
            Button { audition(choice) } label: {
                Image(systemName: "play.circle").font(.title3)
            }
            .buttonStyle(.plain)
            .help("Hear \(v.name) say one of \(character?.firstName ?? "the narrator")’s lines")
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: Space.sm) {
                    Text(v.name)
                    if !v.detail.isEmpty { Text(v.detail).font(.caption).foregroundStyle(.secondary) }
                }
                Text(describe(v) + (users.isEmpty ? "" : " · Also " + users.joined(separator: ", ")))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer()
            if isCurrent {
                Image(systemName: "checkmark").foregroundStyle(Color.accentColor)
            } else {
                Button("Use") { choose(choice) }
                    .controlSize(.small)
                    .help("Give \(character?.name ?? "the narrator") this voice")
            }
        }
        .padding(.horizontal, Space.sm)
        .padding(.vertical, Space.xs)
        .background(isCurrent ? Color.accentColor.opacity(0.1) : .clear, in: RoundedRectangle(cornerRadius: 6))
    }

    private func describe(_ v: VoiceInfo) -> String {
        [v.gender == .neutral ? nil : v.gender.title, v.age == .adult ? nil : v.age.title, v.accent].compactMap { $0 }.joined(separator: " · ")
    }

    /// Who else has this voice, so two characters in a scene don't sound alike.
    private func usedBy(_ voiceID: String) -> [String] {
        var names = document.project.characters.filter { $0.id != characterID && $0.voice(for: engine)?.choice.voiceID == voiceID }.map(\.firstName)
        if characterID != nil, document.project.settings.narratorVoice(for: engine)?.choice.voiceID == voiceID { names.insert("the narrator", at: 0) }
        return names
    }

    // MARK: Qwen3-TTS designs

    private var design: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Text("Voice Description").font(.headline)
            Text("Qwen3-TTS makes a voice from a description. Say who they are and how they sound.")
                .font(.callout)
                .foregroundStyle(.secondary)
            TextField("A woman in her sixties with a warm, low voice and a soft Scottish accent.", text: $designDraft, axis: .vertical)
                .lineLimit(2...5)
                .textFieldStyle(.roundedBorder)
                .onAppear { designDraft = voice?.choice.design ?? "" }
                .onSubmit(saveDesign)
            HStack {
                Button("Save Description", action: saveDesign)
                    .disabled(designDraft.trimmingCharacters(in: .whitespaces).isEmpty || designDraft == voice?.choice.design)
                if let c = character {
                    Button("Describe from Summary") {
                        let p = VoiceCasting.profile(c, project: document.project, index: document.index)
                        designDraft = VoiceCasting.describe(p, name: c.name)
                    }
                    .help("Write the description from what the book says about \(c.firstName)")
                }
                Spacer()
                Button("Play", systemImage: "play.fill") {
                    audition(VoiceChoice(engine: .qwen, voiceID: "design", design: designDraft))
                }
                .disabled(designDraft.isEmpty)
            }
        }
    }

    private func saveDesign() {
        let text = designDraft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        choose(VoiceChoice(engine: .qwen, voiceID: "design", design: text))
    }

    // MARK: Actions

    private func choose(_ choice: VoiceChoice) {
        document.setVoice(choice, for: characterID, undoManager: undoManager)
    }

    private func audition(_ choice: VoiceChoice) {
        PlayCommand.audition(choice, character: character, document: document)
    }
}

/// Play the selected line (or a table read from it), or Stop while something plays.
struct PlayStopButton: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    private var player: SpeechPlayer { SpeechPlayer.shared }

    var body: some View {
        if player.isPlaying {
            Button("Stop Playing", systemImage: "stop.fill") { player.stop() }
                .help("Stop reading aloud (⌥Esc)")
        } else {
            Menu("Play", systemImage: "play.fill") {
                Button("Play Line") { PlayCommand.playLine(document: document, workspace: workspace) }
                    .disabled(workspace.selectedLines.isEmpty)
                Button("Play from Here") { PlayCommand.playFromHere(document: document, workspace: workspace) }
            } primaryAction: {
                if workspace.selectedLines.isEmpty {
                    PlayCommand.playFromHere(document: document, workspace: workspace)
                } else {
                    PlayCommand.playLine(document: document, workspace: workspace)
                }
            }
            .help("Hear the selected line in its character’s voice (⌥Space). Click the arrow for Play from Here, a table read (⌥⇧Space).")
        }
    }
}
