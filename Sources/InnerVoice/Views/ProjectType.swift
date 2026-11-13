import AVFoundation
import InnerVoiceKit
import SwiftUI

/// What each project type shows. A project that hasn't been asked yet shows everything.
extension Project {
    /// Voices, Play, Export Audio: everything for producing with AI voices.
    var showsVoices: Bool { settings.purpose != .narration }
    /// Index, the Teleprompter, narrators and Narrator Scripts.
    var showsNarrationTools: Bool { settings.purpose != .aiVoices }
    /// Pronunciations: they tell the AI voices how to say names, so a narrator (who says
    /// them in their own voice) doesn't see them. The terms stay in the project.
    var showsPronunciations: Bool { settings.purpose != .narration }
}

extension Workspace.Page {
    func isShown(in project: Project) -> Bool {
        switch self {
        case .voices: project.showsVoices
        case .index, .teleprompter: project.showsNarrationTools
        case .pronunciations: project.showsPronunciations
        case .revisions: !project.revisions.isEmpty
        case .manuscript, .cast, .review: true
        }
    }
}

/// Asked once per project: what it's for, then how its voices are shared (Casting Style,
/// docs/CASTING-STYLE.md; nothing is chosen for the narrator). New projects ask right away;
/// projects made before a question existed ask it the first time they're opened. Producing
/// with AI voices goes on to a short guided setup: choose an engine, then Tag Speakers reads
/// speakers and tones, voices are cast, and the project opens on the Voices page.
struct ProjectTypeSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State var choice: ProjectPurpose = UserDefaults.standard.string(forKey: "project.lastPurpose").flatMap(ProjectPurpose.init) ?? .narration
    /// 0: what it's for, 1: Casting Style, 2: the engine (AI voices).
    @State var step = 0
    @State private var engine: VoiceEngine = .apple
    @State private var allowCloud = false
    /// Casting Style: no default, the narrator always picks.
    @State private var style: CastingStyle?
    @State private var strength: ShadeStrength = .clear
    @State private var narrationRole: VoiceRole = .narrator
    @State private var minor: MinorVoices = .narrator
    @State private var yourVoice: CharacterGender?
    /// The book already had a type (made before Casting Style): only the style is asked.
    @State private var existing = false
    @State private var player: AVAudioPlayer?
    @State private var playing: CastingStyle?

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            switch step {
            case 0: typeStep
            case 1: styleStep
            default: setupStep
            }
        }
        .onAppear {
            if let p = document.project.settings.purpose {
                choice = p
                existing = true
            }
        }
        .padding(Space.xl)
        .frame(width: 560)
        .fixedSize(horizontal: false, vertical: true)
        .noInitialFocus()
        .interactiveDismissDisabled()
    }

    // MARK: What it's for

    private var typeStep: some View {
        Group {
            VStack(alignment: .leading, spacing: Space.xs) {
                Text("What is this project for?").font(.title2.weight(.semibold))
                Text("InnerVoice shows the tools for the job. You can change it later in Book › Project Settings; nothing is lost when you do.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            ForEach(ProjectPurpose.allCases) { p in
                PurposeCard(purpose: p, selected: choice == p) { choice = p }
            }
            HStack {
                HelpButton(topic: .gettingStarted)
                Spacer()
                Button("Continue") { step = 1 }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
            }
        }
    }

    // MARK: Guided setup for AI voices

    private var setupStep: some View {
        Group {
            VStack(alignment: .leading, spacing: Space.xs) {
                Text("Choose the Voices").font(.title2.weight(.semibold))
                Text(needsTagging
                     ? "InnerVoice will find who says every line and how they say it, give each character a voice, and open the Voices page so you can listen and change any of them."
                     : "InnerVoice will give each character a voice and open the Voices page so you can listen and change any of them.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            VStack(alignment: .leading, spacing: Space.sm) {
                ForEach(EngineSwitches.shared.enabled) { e in
                    engineRow(e)
                }
            }
            if !EngineSwitches.shared.usable(.chatterbox) || !EngineSwitches.shared.usable(.orpheus) {
                Text("More natural voices (Chatterbox, Orpheus) are a free download in Settings › Voices, and you can switch engines later on the Voices page.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if engine.isCloud {
                Toggle(isOn: $allowCloud) {
                    Text("Send this book’s text to ElevenLabs. Its voices are made in the cloud, so every line they speak goes to ElevenLabs; a table read or an export sends the whole manuscript.")
                        .fixedSize(horizontal: false, vertical: true)
                }
                .toggleStyle(.checkbox)
                .padding(Space.md)
                .background(.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
            }
            HStack {
                Button("Back") { step = 1 }
                Spacer()
                Button("Skip") { finish(guided: false) }
                    .help("Use Produce with AI Voices without setting anything up now")
                Button(needsTagging ? "Tag Speakers and Cast Voices" : "Cast Voices") { finish(guided: true) }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
                    .disabled(engine.isCloud && !allowCloud)
            }
        }
        .onAppear {
            // Start from the best engine that's ready on this Mac.
            engine = [.chatterbox, .orpheus, .apple].first { EngineSwitches.shared.usable($0) } ?? .apple
        }
    }

    private func engineRow(_ e: VoiceEngine) -> some View {
        let ready = EngineSwitches.shared.usable(e)
        return Button { if ready { engine = e } } label: {
            HStack(spacing: Space.md) {
                Image(systemName: engine == e ? "largecircle.fill.circle" : "circle")
                    .foregroundStyle(engine == e ? Color.accentColor : .secondary)
                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: Space.xs) {
                        Text(e.title)
                        if e.isCloud { CloudBadge() }
                    }
                    Text(engineDetail(e)).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                if !ready { Text("Not downloaded").font(.caption).foregroundStyle(.tertiary) }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!ready)
    }

    private func engineDetail(_ e: VoiceEngine) -> String {
        switch e {
        case .apple: "Built in, ready now. Can’t act out tones."
        case .chatterbox: "Nine stock voices and the voices you clone, acted in each tone."
        case .orpheus: "Eight natural voices that act out the words."
        case .elevenlabs: "Natural voices in the cloud, from your ElevenLabs account."
        case .mixed: ""
        }
    }

    private var needsTagging: Bool {
        let p = document.project
        return p.tonesTagged == nil || p.lines.contains { p.needsYou($0) && $0.speakerID == nil }
    }

    private func finish(guided: Bool) {
        player?.stop()
        UserDefaults.standard.set(choice.rawValue, forKey: "project.lastPurpose")
        let purpose = choice
        let (style, strength, narrationRole, minor, yourVoice) = (style, strength, narrationRole, minor, yourVoice)
        document.updateSettings("Set Project Type", undoManager: undoManager) { s in
            s.purpose = purpose
            s.castingStyle = style
            s.shadeStrength = strength == .clear ? nil : strength
            s.narrationRole = narrationRole == .narrator ? nil : narrationRole
            s.minorVoices = minor == .narrator ? nil : minor
            if purpose == .narration { s.narratorGender = yourVoice }
        }
        if purpose == .narration, style == .shared { document.setUpThreeNarrators(undoManager: undoManager) }
        workspace.showProjectType = false
        guard purpose == .aiVoices else { return }
        // A book that already had its type and voices keeps them; the style is all that changed.
        if existing {
            VoiceCastingRun.castIfNeeded(document: document)
            return
        }
        if guided {
            if engine.isCloud { document.setCloudVoicesAllowed(true, undoManager: undoManager) }
            document.setVoiceEngine(engine, undoManager: undoManager)
            if needsTagging {
                workspace.afterTagging = .voices
                TagCommand.run(document: document, workspace: workspace, undoManager: undoManager)
                return
            }
            VoiceCastingRun.castIfNeeded(document: document)
        }
        workspace.page = .voices
    }
}

extension ProjectTypeSheet {
    // MARK: Casting Style

    private var narration: Bool { choice == .narration }

    var styleStep: some View {
        Group {
            VStack(alignment: .leading, spacing: Space.xs) {
                Text(narration ? "How many narrators will read it?" : "How will the voices be shared?").font(.title2.weight(.semibold))
                Text(narration
                     ? "InnerVoice prepares your cues and scripts for the way you’ll record. Click Hear It for an example."
                     : "Pick the kind of audiobook you want to make. Click Hear It to hear the same passage in each style.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            ForEach(CastingStyle.allCases) { s in
                CastingStyleCard(style: s, purpose: choice, selected: style == s, playing: playing == s,
                                 hasSample: Self.sample(s) != nil, choose: { style = s }, play: { hear(s) })
            }
            if let style { followUp(style) }
            HStack {
                HelpButton(topic: .castingStyle)
                Text(narration ? "You can change it later on the Cast page." : "You can change it later on the Voices page.")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                if !existing { Button("Back") { step = 0 } }
                Button(existing || narration ? "Done" : "Continue") {
                    if existing || narration { finish(guided: false) } else { step = 2 }
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
                .disabled(style == nil)
                .help(style == nil ? "Choose one first" : "")
            }
        }
    }

    /// The one question each style needs.
    @ViewBuilder
    private func followUp(_ s: CastingStyle) -> some View {
        Group {
            switch s {
            case .oneVoice:
                if narration {
                    Picker("Your voice", selection: $yourVoice) {
                        Text("Not Set").tag(CharacterGender?.none)
                        Text("Male").tag(CharacterGender?.some(.male))
                        Text("Female").tag(CharacterGender?.some(.female))
                        Text("Other").tag(CharacterGender?.some(.other))
                    }
                    .help("Characters of your own gender are read in your voice; others get a cue to go lower or higher")
                } else {
                    Picker("Shading", selection: $strength) {
                        ForEach(ShadeStrength.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .help("How much the voice changes for a character of another gender")
                }
            case .shared:
                Picker("Who reads the narration?", selection: $narrationRole) {
                    Text(narration ? "A narrator of its own" : "Its own voice").tag(VoiceRole.narrator)
                    Text(narration ? "The men’s narrator" : "The men’s voice").tag(VoiceRole.men)
                    Text(narration ? "The women’s narrator" : "The women’s voice").tag(VoiceRole.women)
                }
                .help("Choose the men’s or women’s for a book read by two people")
            case .fullCast:
                Picker("Minor characters", selection: $minor) {
                    Text(narration ? "The narrator" : "The narrator’s voice").tag(MinorVoices.narrator)
                    Text(narration ? "Men’s, women’s or other narrator" : "Men’s, women’s or other voice").tag(MinorVoices.byGender)
                    Text(narration ? "A narrator of their own" : "A voice of their own").tag(MinorVoices.own)
                }
                .help("Characters you don’t mark as main. Main characters always have their own.")
            }
        }
        .padding(Space.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
    }

    /// A short passage of the sample book in each style (Chatterbox), in the app.
    static func sample(_ s: CastingStyle) -> URL? {
        Bundle.main.url(forResource: "casting-" + s.rawValue, withExtension: "m4a", subdirectory: "Samples")
    }

    private func hear(_ s: CastingStyle) {
        player?.stop()
        if playing == s { playing = nil; return }
        guard let url = Self.sample(s), let p = try? AVAudioPlayer(contentsOf: url) else { return }
        player = p
        playing = s
        p.play()
        let length = p.duration
        Task {
            try? await Task.sleep(for: .seconds(length + 0.2))
            if playing == s, player === p { playing = nil }
        }
    }
}

/// One Casting Style to choose, with who reads what and a sample to hear.
private struct CastingStyleCard: View {
    let style: CastingStyle
    let purpose: ProjectPurpose
    let selected: Bool
    let playing: Bool
    let hasSample: Bool
    let choose: () -> Void
    let play: () -> Void

    private var narration: Bool { purpose == .narration }

    private var symbol: String {
        switch style {
        case .oneVoice: narration ? "mic" : "person.wave.2"
        case .shared: "person.3"
        case .fullCast: "person.3.sequence"
        }
    }

    private var detail: String {
        switch (style, narration) {
        case (.oneVoice, true): "You read everything and shade each character a little lower or higher. InnerVoice shows how in the margin and the Teleprompter."
        case (.oneVoice, false): "One AI voice reads everything and shades each character a little lower or higher, like most audiobooks."
        case (.shared, true): "A narrator for the narration, one for every man and one for every woman, each with a Narrator Script of their own."
        case (.shared, false): "A narrator voice, one voice for every man and one for every woman. InnerVoice sorts the characters by gender."
        case (.fullCast, true): "Every main character has a narrator of their own, plus the narrator."
        case (.fullCast, false): "Every main character gets a voice of their own, plus the narrator."
        }
    }

    /// Who reads what, in a few words.
    private var map: String {
        switch (style, narration) {
        case (.oneVoice, true): "Narration: you · Men: you, lower · Women: you, higher"
        case (.oneVoice, false): "Narration: the voice · Men: a little lower · Women: a little higher"
        case (.shared, true): "Narration: a narrator · Men: the men’s narrator · Women: the women’s"
        case (.shared, false): "Narration: the narrator · Men: the men’s voice · Women: the women’s"
        case (.fullCast, true): "Narration: a narrator · Each main character: their own narrator"
        case (.fullCast, false): "Narration: the narrator · Each main character: their own voice"
        }
    }

    /// The circle, then Hear It. The card draws it with Hear It hidden (a button can't hold
    /// another button), and the real Hear It lies over that spot.
    private func trailing(circle: Bool, hearIt: Bool) -> some View {
        VStack(alignment: .trailing, spacing: Space.sm) {
            Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                .foregroundStyle(selected ? Color.accentColor : .secondary)
                .opacity(circle ? 1 : 0)
                .allowsHitTesting(circle)
            if hasSample {
                Button(playing ? "Stop" : "Hear It", systemImage: playing ? "stop.fill" : "play.fill", action: play)
                    .controlSize(.small)
                    .help(narration ? "An example of this kind of audiobook (AI voices)" : "The opening of a sample story read this way")
                    .opacity(hearIt ? 1 : 0)
                    .allowsHitTesting(hearIt)
                    .accessibilityHidden(!hearIt)
            }
        }
    }

    var body: some View {
        // The whole card is one button, the circle and the padding included.
        Button(action: choose) {
            HStack(alignment: .top, spacing: Space.md) {
                Image(systemName: symbol)
                    .font(.title2)
                    .foregroundStyle(selected ? Color.accentColor : .secondary)
                    .frame(width: 32)
                VStack(alignment: .leading, spacing: Space.xs) {
                    Text(style.title(for: purpose)).font(.headline)
                    Text(detail).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    Text(map).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
                trailing(circle: true, hearIt: false)
            }
            .padding(Space.md)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? Color.accentColor.opacity(0.1) : Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(selected ? Color.accentColor : Color(nsColor: .separatorColor)))
            .contentShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .overlay(alignment: .topTrailing) {
            trailing(circle: false, hearIt: true).padding(Space.md)
        }
    }
}

/// One project type to choose: a big, clear card.
private struct PurposeCard: View {
    let purpose: ProjectPurpose
    let selected: Bool
    let choose: () -> Void

    var body: some View {
        Button(action: choose) {
            HStack(alignment: .top, spacing: Space.md) {
                Image(systemName: purpose.symbol)
                    .font(.title2)
                    .foregroundStyle(selected ? Color.accentColor : .secondary)
                    .frame(width: 32)
                VStack(alignment: .leading, spacing: Space.xs) {
                    Text(purpose.title).font(.headline)
                    Text(purpose.detail)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
                Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(selected ? Color.accentColor : .secondary)
            }
            .padding(Space.md)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? Color.accentColor.opacity(0.1) : Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(selected ? Color.accentColor : Color(nsColor: .separatorColor)))
            .contentShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// Book › Project Settings: settings that belong to this book, starting with its type.
struct ProjectSettingsSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    @Binding var isPresented: Bool
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            Text("Project Settings").font(.title2.weight(.semibold))
            VStack(alignment: .leading, spacing: Space.sm) {
                Text("What this project is for").font(.headline)
                ForEach(ProjectPurpose.allCases) { p in
                    PurposeCard(purpose: p, selected: document.project.settings.purpose == p) {
                        document.updateSettings("Change Project Type", undoManager: undoManager) { $0.purpose = p }
                        UserDefaults.standard.set(p.rawValue, forKey: "project.lastPurpose")
                    }
                }
                Text("Switching hides the other type’s pages and commands. Nothing is deleted: switch back and they return as you left them.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            VStack(alignment: .leading, spacing: Space.sm) {
                HStack(spacing: Space.sm) {
                    Text("Claude").font(.headline)
                    CloudBadge(service: "Anthropic")
                }
                Toggle("Review Claude’s changes before keeping them", isOn: Binding(
                    get: { document.project.settings.reviewsCloudAI },
                    set: { on in document.updateSettings("Review Claude’s Changes", undoManager: undoManager) { $0.reviewCloudAI = on ? nil : false } }
                ))
                Text("After Tag Speakers runs with Claude, a list shows every line it changed, so you can keep or undo each one; Clean Up Text, chapter recaps and delivery hints wait in a list too. Turn this off to keep Claude’s changes straight away, as with a model on your Mac. Suggested spellings always wait for you.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if document.project.settings.cloudTextAllowed != nil {
                    Button("Stop Sending This Book to Claude") { document.setCloudTextAllowed(false, undoManager: undoManager) }
                        .controlSize(.small)
                }
            }
            HStack {
                HelpButton(topic: .gettingStarted)
                Spacer()
                Button("Done") { isPresented = false }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(Space.xl)
        .frame(width: 560)
        .fixedSize(horizontal: false, vertical: true)
        .noInitialFocus()
    }
}

/// Book › Book Stats: this book's numbers, for its project type. Worked out from the book
/// itself, so they're always current.
struct BookStatsSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    @Binding var isPresented: Bool

    var body: some View {
        let p = document.project
        let index = document.index
        let lines = p.lines
        let total = lines.count
        let auto = lines.filter { $0.speakerID != nil && ($0.source == .ai || $0.source == .rule || $0.source == .exchange) }.count
        let byYou = lines.filter { $0.source == .user }.count
        let needs = p.linesNeedingYou
        let chaptersDone = p.chapters.filter { c in
            c.paragraphs.allSatisfy { para in (index.linesByParagraph[para.id] ?? []).allSatisfy { !p.needsYou(p.lines[$0]) } }
        }.count
        let speaking = p.characters.filter { (index.stats[$0.id]?.lines ?? 0) > 0 }
        let paragraphs = p.chapters.reduce(0) { $0 + $1.paragraphs.count }
        VStack(alignment: .leading, spacing: Space.lg) {
            HStack(spacing: Space.sm) {
                Text("Book Stats").font(.title2.weight(.semibold))
                Spacer()
                if let purpose = p.settings.purpose {
                    Label(purpose.title, systemImage: purpose.symbol).font(.callout).foregroundStyle(.secondary)
                }
            }
            Text(p.title).foregroundStyle(.secondary)
            HStack(alignment: .top, spacing: Space.lg) {
                stat(total == 0 ? "—" : "\(Int((Double(auto) / Double(total) * 100).rounded()))%", "of lines tagged for you")
                stat("\(chaptersDone) of \(p.chapters.count)", "chapters with nothing to review")
                stat(needs.formatted(), needs == 1 ? "line needs you" : "lines need you")
            }
            Form {
                Section("Speakers") {
                    row("Lines", total.formatted())
                    row("Tagged by the rules or the AI", auto.formatted())
                    row("Assigned or confirmed by you", byYou.formatted())
                    row("Speaking characters", speaking.count.formatted())
                    row("Character facts", p.characters.reduce(0) { $0 + $1.facts.count }.formatted())
                    let toned = lines.filter { $0.tone != nil && $0.tone != .neutral }.count
                    row("Lines with a tone other than neutral", toned.formatted())
                }
                if p.showsPronunciations {
                    Section("Pronunciations") {
                        row("Pronunciations confirmed", "\(p.pronunciations.filter(\.confirmed).count) of \(p.pronunciations.count)")
                    }
                }
                if p.showsVoices {
                    let engine = p.engineInUse
                    Section("AI Voices") {
                        row("Engine", engine.title)
                        row("Characters with a voice", "\(speaking.filter { $0.voice(for: engine) != nil }.count) of \(speaking.count)")
                        row("Voices you chose or confirmed", speaking.filter { $0.voice(for: engine)?.source == .user }.count.formatted())
                        row("Tones you set", lines.filter { $0.toneSource == .user }.count.formatted())
                    }
                }
            }
            .formStyle(.grouped)
            // Scrolls on short screens instead of running past the window.
            .frame(height: min(440, (NSScreen.main?.visibleFrame.height ?? 900) - 360))
            HStack {
                Button("All Activity…") {
                    isPresented = false
                    SettingsWindow.shared.show(tab: .activity)
                }
                .help("What InnerVoice has done across all your books (Settings › Activity)")
                Spacer()
                Button("Done") { isPresented = false }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(Space.xl)
        .frame(width: 560)
        .fixedSize(horizontal: false, vertical: true)
        .noInitialFocus()
    }

    private func stat(_ value: String, _ label: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(value).font(.title2.weight(.semibold)).monospacedDigit()
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func row(_ label: String, _ value: String) -> some View {
        LabeledContent(label) { Text(value).monospacedDigit().foregroundStyle(.secondary) }
    }
}
