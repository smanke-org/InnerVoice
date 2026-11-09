import InnerVoiceKit
import SwiftUI

/// What each project type shows. A project that hasn't been asked yet shows everything.
extension Project {
    /// Voices, Play, Export Audio: everything for producing with AI voices.
    var showsVoices: Bool { settings.purpose != .narration }
    /// Index, Rehearse, Record, Retakes, narrators and Narrator Scripts.
    var showsNarrationTools: Bool { settings.purpose != .aiVoices }
}

extension Workspace.Page {
    func isShown(in project: Project) -> Bool {
        switch self {
        case .voices: project.showsVoices
        case .index, .record, .retakes: project.showsNarrationTools
        case .revisions: !project.revisions.isEmpty
        case .manuscript, .cast, .pronunciations, .review: true
        }
    }
}

/// Asked once per project: what it's for. New projects ask right away; projects made before
/// the question existed ask the first time they're opened. Producing with AI voices goes on
/// to a short guided setup: choose an engine, then Tag Speakers reads speakers and tones,
/// voices are cast, and the project opens on the Voices page.
struct ProjectTypeSheet: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State private var choice: ProjectPurpose = UserDefaults.standard.string(forKey: "project.lastPurpose").flatMap(ProjectPurpose.init) ?? .narration
    @State var step = 0
    @State private var engine: VoiceEngine = .apple
    @State private var allowCloud = false

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            if step == 0 { typeStep } else { setupStep }
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
                Button(choice == .aiVoices ? "Continue" : "Start") {
                    if choice == .aiVoices { step = 1 } else { finish(guided: false) }
                }
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
            if !EngineSwitches.shared.usable(.qwen) || !EngineSwitches.shared.usable(.orpheus) {
                Text("More natural voices (Qwen3-TTS, Orpheus) are a free download in Settings › Voices, and you can switch engines later on the Voices page.")
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
                Button("Back") { step = 0 }
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
            engine = [.qwen, .orpheus, .apple].first { EngineSwitches.shared.usable($0) } ?? .apple
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
        case .qwen: "16 ready-made voices, or voices designed from each character’s summary."
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
        UserDefaults.standard.set(choice.rawValue, forKey: "project.lastPurpose")
        let purpose = choice
        document.updateSettings("Set Project Type", undoManager: undoManager) { $0.purpose = purpose }
        workspace.showProjectType = false
        guard purpose == .aiVoices else { return }
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
