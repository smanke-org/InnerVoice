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
        .pageSubtitle(subtitle)
        .toolbar {
            ToolbarItem {
                Picker("Engine", selection: Binding(get: { engine }, set: { e in
                    // The first time a book uses ElevenLabs, ask before any of its text is sent.
                    if e.isCloud, document.project.settings.cloudVoicesAllowed == nil {
                        workspace.askCloudEngine = e
                    } else {
                        document.setVoiceEngine(e, undoManager: undoManager)
                    }
                })) {
                    ForEach(EngineSwitches.shared.choices) { e in
                        Text(e.title + (e.isCloud ? " (Cloud)" : ModelSpeech.isInstalled(e) ? "" : " (Not Downloaded)")).tag(e)
                    }
                }
                .help("The speech engine the project’s voices use. Each engine keeps its own casting.")
            }
            ToolbarItem {
                Button("Cast Voices Again", systemImage: "wand.and.stars") { document.castVoicesAgain(undoManager: undoManager) }
                    .help("Choose every voice again from the character summaries. Voices you chose stay.")
            }
            ToolbarItem {
                Button("Use Voices from Another Project", systemImage: "books.vertical") { workspace.showSeriesVoices = true }
                    .help("Give characters the voices they had in an earlier book in the series. You match the characters first.")
            }
            ToolbarItem {
                PlayStopButton(document: document, workspace: workspace)
            }
        }
        .onAppear {
            VoiceCastingRun.castIfNeeded(document: document)
            ModelSpeech.prepare(engine)
            if ElevenLabs.shared.isSetUp, ElevenLabs.shared.voices.isEmpty, engine == .elevenlabs || engine == .mixed {
                Task { try? await ElevenLabs.shared.refreshVoices() }
            }
        }
        .cloudPermissionAlert(document: document, workspace: workspace)
    }

    private var subtitle: String {
        let unsure = cast.filter { document.project.voiceNeedsYou($0, engine: engine) }.count
        return unsure == 1 ? "1 voice needs you" : unsure > 1 ? "\(unsure) voices need you" : engine.title
    }

    private var list: some View {
        List(selection: Binding(get: { workspace.voiceTarget }, set: { workspace.voiceTarget = $0 ?? .narrator })) {
            Section("Narration") {
                row(name: "Narrator", color: nil, voice: document.project.settings.narratorVoice(for: engine), needs: false,
                    pov: VoiceResolver.povNarrator(project: document.project, engine: engine))
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

    private func row(name: String, color: Color?, voice: CastVoice?, needs: Bool, pov: BookCharacter? = nil) -> some View {
        HStack(spacing: Space.sm) {
            if let color {
                Circle().fill(color).frame(width: 10, height: 10)
            } else {
                Image(systemName: "book.closed").foregroundStyle(.secondary).frame(width: 10)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(name).lineLimit(1)
                Text(pov.map { "\($0.firstName)’s voice (first person)" } ?? voice.map { VoiceCatalog.name(of: $0.choice) } ?? "No voice yet")
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
                    CloudNotice(document: document, workspace: workspace)
                } else {
                    VStack(alignment: .leading, spacing: Space.xs) {
                        Text("Narrator").font(.title2.weight(.semibold))
                        Text("Reads the narration in a table read, lines nobody is cast for, and characters set to Same as Narrator. Letters, signs and other written text too, unless Settings › Voices says otherwise.")
                            .foregroundStyle(.secondary)
                        if let pov = VoiceResolver.povNarrator(project: document.project) {
                            Label("This book is told in the first person by \(pov.name), so the narration uses \(pov.firstName)’s voice. Choose a voice below to give the narrator a voice of its own.", systemImage: "person.wave.2")
                                .font(.callout)
                                .padding(.top, Space.xs)
                        }
                    }
                }
                if character == nil { CloudNotice(document: document, workspace: workspace) }
                VoiceChooser(document: document, characterID: character?.id, compact: false,
                             onClone: { workspace.cloneVoice(for: character?.id) })
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
    /// Opens Clone a Voice for this character (only where a window's workspace is at hand).
    var onClone: (() -> Void)? = nil
    @Environment(\.undoManager) private var undoManager
    @State private var filter: VoiceGender?
    @State private var designDraft = ""
    @State private var cloudDesigning = false
    @State private var loadingCloudVoices = false
    @State private var cloudVoicesError: String?
    @State private var cloudDesignError: String?

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
            if ElevenLabs.shared.isSetUp, ElevenLabs.shared.voices.isEmpty, engine == .elevenlabs || engine == .mixed { cloudVoicesMissing }
            suggestions
            if let onClone { cloneOffer(onClone) }
            if engine == .qwen || (engine == .mixed && EngineSwitches.shared.usable(.qwen)) { design }
            if let c = character, showsCloudDesign { cloudDesign(c) }
            if !compact { allVoices }
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
        // Clones are never suggested: a real person's voice goes only where it's put.
        let voices = ElevenLabs.shared.castable(VoiceCatalog.voices(for: engine))
        if let c = character {
            let best = VoiceCasting.suggestions(for: c, project: document.project, index: document.index, voices: voices, limit: 3)
            VStack(alignment: .leading, spacing: Space.sm) {
                Text("Best Fits").font(.headline)
                ForEach(best, id: \.voice.id) { item in
                    voiceRow(item.voice)
                }
                narratorRow
            }
        }
    }

    /// "Same as Narrator": the character speaks in the narrator's voice, whatever it is.
    private var narratorRow: some View {
        let choice = VoiceChoice(engine: engine, voiceID: VoiceChoice.narratorID)
        let isCurrent = voice?.choice.isNarrator == true
        let narrator = VoiceResolver(project: document.project).narrator
        return HStack(spacing: Space.md) {
            Button { audition(choice) } label: {
                Image(systemName: "play.circle").font(.title3)
            }
            .buttonStyle(.plain)
            .help("Hear the narrator’s voice say one of \(character?.firstName ?? "")’s lines")
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: Space.sm) {
                    Image(systemName: "book.closed").foregroundStyle(.secondary)
                    Text("Same as Narrator")
                }
                Text("Uses the narrator’s voice" + (narrator.map { ", now " + VoiceCatalog.name(of: $0) } ?? "") + ", and follows it if it changes")
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
                    .help("Read \(character?.name ?? "")’s lines in the narrator’s voice")
            }
        }
        .padding(.horizontal, Space.sm)
        .padding(.vertical, Space.xs)
        .background(isCurrent ? Color.accentColor.opacity(0.1) : .clear, in: RoundedRectangle(cornerRadius: 6))
    }

    @ViewBuilder
    private var allVoices: some View {
        let voices = VoiceCatalog.voices(for: engine).filter { filter == nil || $0.gender == filter }
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack {
                Text(engine == .mixed ? "All Voices" : "All \(engine.title)").font(.headline)
                Spacer()
                Picker("Show", selection: $filter) {
                    Text("All").tag(VoiceGender?.none)
                    ForEach(VoiceGender.allCases, id: \.self) { Text($0.title).tag(VoiceGender?.some($0)) }
                }
                .pickerStyle(.segmented)
                .fixedSize()
                .help("Show voices by how they sound")
            }
            if engine == .mixed {
                // Grouped by engine, so it's clear where each voice comes from.
                ForEach(VoiceEngine.engines.filter { e in voices.contains { $0.engine == e } }) { e in
                    Text(e.title).font(.subheadline.weight(.semibold)).foregroundStyle(.secondary).padding(.top, Space.xs)
                    ForEach(voices.filter { $0.engine == e }) { v in voiceRow(v) }
                }
            } else {
                ForEach(voices) { v in voiceRow(v) }
            }
            if engine == .apple || (engine == .mixed && EngineSwitches.shared.isOn(.apple)) {
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
        let choice = v.choice
        let isCurrent = voice?.choice.voiceID == v.voiceID && voice?.choice.engine == v.engine
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
        [engine == .mixed ? v.engine.title : nil, v.gender == .neutral ? nil : v.gender.title, v.age == .adult ? nil : v.age.title, v.accent]
            .compactMap { $0 }.joined(separator: " · ")
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
            Text("Describe a Voice").font(.headline)
            Text(character == nil
                 ? "Or describe any voice and Qwen3-TTS makes it. Say who they are and how they sound."
                 : "Or describe any voice and Qwen3-TTS makes it. Say who they are and how they sound. \(character!.firstName)’s Voice Notes are added to the description, so what you write there shapes the voice too.")
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

    // MARK: ElevenLabs voices

    /// No ElevenLabs voices yet (the list never loaded, or loading failed): say so and why,
    /// rather than showing a missing voice.
    private var cloudVoicesMissing: some View {
        HStack(alignment: .top, spacing: Space.sm) {
            Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: Space.xs) {
                Text("ElevenLabs voices haven’t loaded, so there’s no ElevenLabs voice to give.").font(.callout)
                if let cloudVoicesError {
                    Text(cloudVoicesError).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: Space.sm)
            if loadingCloudVoices {
                ProgressView().controlSize(.small)
            } else {
                Button("Load Voices", action: loadCloudVoices)
                    .controlSize(.small)
                    .help("Get the list of voices in your ElevenLabs account (no book text is sent)")
            }
        }
        .padding(Space.md)
        .background(.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
        .task { if cloudVoicesError == nil { loadCloudVoices() } }
    }

    private func loadCloudVoices() {
        guard !loadingCloudVoices else { return }
        loadingCloudVoices = true
        Task {
            defer { loadingCloudVoices = false }
            do {
                let found = try await ElevenLabs.shared.refreshVoices()
                cloudVoicesError = found.isEmpty ? "Your ElevenLabs account has no voices. Add some in the Voice Library at elevenlabs.io." : nil
                if !found.isEmpty { VoiceCastingRun.castIfNeeded(document: document) }
            } catch {
                cloudVoicesError = error.localizedDescription
            }
        }
    }

    // MARK: ElevenLabs designs

    /// ElevenLabs can design a voice from a description, for a book that allows ElevenLabs.
    private var showsCloudDesign: Bool { ElevenLabs.cloningOffered(for: document.project) }

    // MARK: Clones

    private func cloneOffer(_ open: @escaping () -> Void) -> some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Text("Clone a Voice").font(.headline)
            Text("Make a voice from a recording of a real person, with their consent: on this Mac with Qwen3-TTS (free, nothing leaves the Mac), or with ElevenLabs. Every project can use it.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button("Clone a Voice…", action: open)
                .help("Record or add a sample of a voice and clone it (Book › Clone a Voice…)")
        }
    }

    /// What ElevenLabs is sent: who they are, then their Voice Notes.
    private func cloudDescription(_ c: BookCharacter) -> String {
        let p = VoiceCasting.profile(c, project: document.project, index: document.index)
        return VoiceDescription.design(base: VoiceCasting.describe(p, name: c.name), character: c, limit: ElevenLabs.designLimit)
    }

    private func cloudDesign(_ c: BookCharacter) -> some View {
        let text = cloudDescription(c)
        return VStack(alignment: .leading, spacing: Space.sm) {
            HStack(spacing: Space.sm) {
                Text("Design a Voice from Voice Notes").font(.headline)
                CloudBadge()
            }
            Text("ElevenLabs designs a new voice for \(c.firstName) from this description, which includes \(c.firstName)’s Voice Notes, and adds it to your ElevenLabs voices. The description is sent to ElevenLabs; designing uses credits and one of your plan’s voice slots.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Text(text)
                .font(.callout)
                .textSelection(.enabled)
                .padding(Space.sm)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
            if cloudDesigning {
                ProgressView {
                    Text("Designing \(c.firstName)’s voice with ElevenLabs…").font(.callout)
                }
                .progressViewStyle(.linear)
            }
            if let cloudDesignError {
                Label(cloudDesignError, systemImage: "exclamationmark.triangle").font(.callout).foregroundStyle(.secondary)
            }
            HStack {
                Button("Design Voice") { designWithElevenLabs(c, description: text) }
                    .disabled(cloudDesigning || text.count < 20)
                    .help("Send \(c.firstName)’s description to ElevenLabs, design a voice from it, and give it to \(c.firstName)")
                Text("Edit Voice Notes to change what’s sent.").font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private func designWithElevenLabs(_ c: BookCharacter, description: String) {
        let p = VoiceCasting.profile(c, project: document.project, index: document.index)
        var labels: [String: String] = [:]
        if let g = p.gender, g != .neutral { labels["gender"] = g == .female ? "female" : "male" }
        if let a = p.age { labels["age"] = a == .older ? "old" : a == .adult ? "middle_aged" : "young" }
        if let accent = p.accent { labels["accent"] = accent.lowercased() }
        let allowed = document.project.settings.cloudVoicesAllowed != nil
        cloudDesigning = true
        cloudDesignError = nil
        Task {
            defer { cloudDesigning = false }
            do {
                let v = try await ElevenLabs.shared.designVoice(name: "\(c.name) (InnerVoice)", description: description, labels: labels, allowed: allowed)
                document.setVoice(v.choice, for: c.id, undoManager: undoManager)
            } catch is CancellationError {
            } catch {
                cloudDesignError = error.localizedDescription
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

/// Asks before a book's text is first sent to ElevenLabs.
struct CloudPermissionAlert: ViewModifier {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager

    func body(content: Content) -> some View {
        content.alert("Send This Book to ElevenLabs?", isPresented: Binding(get: { workspace.askCloudEngine != nil }, set: { if !$0 { workspace.askCloudEngine = nil } })) {
            Button("Send to ElevenLabs") {
                let e = workspace.askCloudEngine ?? .elevenlabs
                document.setCloudVoicesAllowed(true, undoManager: undoManager)
                document.setVoiceEngine(e, undoManager: undoManager)
                workspace.askCloudEngine = nil
            }
            Button("Cancel", role: .cancel) { workspace.askCloudEngine = nil }
        } message: {
            Text("ElevenLabs makes its voices on its own servers. Every line of “\(document.project.title)” that an ElevenLabs voice speaks is sent there as text; a table read or an exported book sends the whole manuscript. Allow it only if you have the right to share this book. You can stop it at any time on the Voices page.")
        }
    }
}

extension View {
    func cloudPermissionAlert(document: InnerVoiceDocument, workspace: Workspace) -> some View {
        modifier(CloudPermissionAlert(document: document, workspace: workspace))
    }
}

/// On the Voices page while a book uses ElevenLabs: a plain reminder that its text goes to
/// the cloud, and a way to stop. When mixing engines without permission, a way to include it.
struct CloudNotice: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        let project = document.project
        let engine = project.engineInUse
        let allowed = project.settings.cloudVoicesAllowed != nil
        let elevenOn = EngineSwitches.shared.isOn(.elevenlabs)
        if allowed, engine == .elevenlabs || (engine == .mixed && elevenOn) {
            HStack(alignment: .top, spacing: Space.sm) {
                Image(systemName: "icloud.and.arrow.up").foregroundStyle(.orange)
                Text(engine == .mixed
                     ? "ElevenLabs voices are made in the cloud: the lines they speak from this book are sent to ElevenLabs."
                     : "This book uses ElevenLabs, in the cloud: every line its voices speak is sent to ElevenLabs as text.")
                    .font(.callout)
                Spacer(minLength: Space.sm)
                Button("Stop Sending This Book") { document.setCloudVoicesAllowed(false, undoManager: undoManager) }
                    .controlSize(.small)
                    .help("Nothing more from this book goes to ElevenLabs. Its voices switch to an engine on this Mac.")
            }
            .padding(Space.md)
            .background(.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
        } else if engine == .mixed, elevenOn, !allowed {
            HStack(alignment: .top, spacing: Space.sm) {
                Image(systemName: "cloud").foregroundStyle(.secondary)
                Text("ElevenLabs voices aren’t in the mix: this book hasn’t allowed its text to be sent to ElevenLabs.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                Spacer(minLength: Space.sm)
                Button("Include ElevenLabs…") { workspace.askCloudEngine = .mixed }
                    .controlSize(.small)
            }
        }
    }
}
