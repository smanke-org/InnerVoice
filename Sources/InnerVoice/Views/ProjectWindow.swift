import AppKit
import InnerVoiceKit
import SwiftUI
import UniformTypeIdentifiers

/// One book's window: sidebar, page, inspector.
struct ProjectWindow: View {
    private func askProjectTypeIfNeeded() {
        // The project type, then the Casting Style (books made before it are asked once).
        guard !SystemHooks.isTestRun, !document.project.isEmpty,
              document.project.settings.purpose == nil || document.project.settings.castingStyle == nil else { return }
        workspace.showProjectType = true
    }

    @ObservedObject var document: InnerVoiceDocument
    let fileURL: URL?
    @State private var workspace = Workspace()
    @Environment(\.undoManager) private var undoManager
    @AppStorage(GuidedStepsBar.hiddenKey) private var stepsHidden = false

    var body: some View {
        Group {
            if document.project.isEmpty {
                EmptyProjectView(document: document)
            } else {
                split
                    .overlay(alignment: .bottom) { SpeechStatusBar() }
            }
        }
        .frame(minWidth: 900, minHeight: 560)
        .focusedSceneValue(\.project, ProjectFocus(document: document, workspace: workspace, undoManager: undoManager))
        .sheet(isPresented: $workspace.showModelSheet) {
            ModelSheet { ready in
                workspace.showModelSheet = false
                if !ready { workspace.afterTagging = nil }
                if ready { TagCommand.run(document: document, workspace: workspace, undoManager: undoManager) }
            }
        }
        .sheet(isPresented: $workspace.showExport) {
            ExportSheet(document: document, isPresented: $workspace.showExport)
        }
        .sheet(isPresented: $workspace.showAudioExport) {
            AudioExportSheet(document: document, workspace: workspace, isPresented: $workspace.showAudioExport)
        }
        .sheet(isPresented: $workspace.showProjectType) {
            ProjectTypeSheet(document: document, workspace: workspace, step: document.project.settings.purpose == nil ? 0 : 1)
        }
        .sheet(isPresented: $workspace.showProjectSettings) {
            ProjectSettingsSheet(document: document, isPresented: $workspace.showProjectSettings)
        }
        .alert("Analyze the Whole Cast Again?", isPresented: $workspace.askAnalyzeCastAgain) {
            Button("Analyze Again") {
                TagCommand.summaries(document: document, workspace: workspace, undoManager: undoManager, confirmed: true)
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("InnerVoice has already read the book for every character. Running it again adds what it finds to their summaries and updates the “From the book” part of each character’s voice notes.\n\nVoices that casting picked may change to fit the new notes; voices you chose stay as they are. To fine-tune how a character sounds, edit their voice notes on their page: casting and the AI voices read them.")
        }
        .sheet(isPresented: $workspace.showSeriesVoices, onDismiss: { workspace.seriesSource = nil }) {
            SeriesVoicesSheet(document: document, isPresented: $workspace.showSeriesVoices, preset: workspace.seriesSource)
        }
        .sheet(isPresented: $workspace.showCloneVoice) {
            CloneVoiceSheet(document: document, isPresented: $workspace.showCloneVoice, initialTarget: workspace.cloneFor)
        }
        .sheet(isPresented: Binding(get: { document.tagging.review != nil }, set: { if !$0 { document.tagging.review = nil } })) {
            if let review = document.tagging.review { AIReviewSheet(document: document, review: review) }
        }
        .sheet(isPresented: $workspace.showBookStats) {
            BookStatsSheet(document: document, isPresented: $workspace.showBookStats)
        }
        // Asked once per project, new or made before the question existed. Test runs don't
        // ask (snapshots show the sheet on purpose).
        .onAppear { askProjectTypeIfNeeded() }
        .onChange(of: document.project.isEmpty) { askProjectTypeIfNeeded() }
        .onChange(of: document.project.settings.purpose) {
            if !workspace.page.isShown(in: document.project) { workspace.page = .manuscript }
        }
        .onChange(of: document.tagging.isRunning) { _, running in
            guard !running, let next = workspace.afterTagging else { return }
            workspace.afterTagging = nil
            if document.tagging.failure == nil { workspace.page = next }
        }
        .onReceive(NotificationCenter.default.publisher(for: SystemHooks.tagSpeakers)) { _ in
            if isFrontProject { TagCommand.run(document: document, workspace: workspace, undoManager: undoManager) }
        }
        .onReceive(NotificationCenter.default.publisher(for: SystemHooks.teleprompter)) { _ in
            if isFrontProject, Workspace.Page.teleprompter.isShown(in: document.project) { workspace.page = .teleprompter }
        }
        .task { Snapshot.attach(document: document, workspace: workspace) }
        // Automatic colors follow what the book says about each character (and bring
        // projects from before color families up to date).
        .task { document.recolorAutomatic() }
        // The agent CLI finds open projects through OpenProjects (Agent/AgentCommands.swift).
        .task(id: fileURL) { OpenProjects.register(document, workspace: workspace, undoManager: undoManager, fileURL: fileURL) }
        .onChange(of: undoManager) { OpenProjects.register(document, workspace: workspace, undoManager: undoManager, fileURL: fileURL) }
        .onDisappear { OpenProjects.unregister(workspace) }
        .noInitialFocus()
    }

    /// The project whose window is in front, for actions from ToolHUD's menu.
    private var isFrontProject: Bool {
        guard let doc = NSDocumentController.shared.currentDocument ?? NSDocumentController.shared.documents.first else { return false }
        return doc.fileURL == fileURL || NSDocumentController.shared.documents.count == 1
    }

    private var split: some View {
        NavigationSplitView {
            ProjectSidebar(document: document, workspace: workspace)
                .navigationSplitViewColumnWidth(min: 200, ideal: 236, max: 320)
        } detail: {
            detail
                // The version under the book's name on pages that don't set their own line.
                .pageSubtitle()
                // The AI working in the background, on every page.
                // (The Cast page shows it inside its navigation stack.)
                .safeAreaInset(edge: .top, spacing: 0) {
                    VStack(spacing: 0) {
                        // Where the book stands and the next step (View › Hide Steps).
                        if !stepsHidden { GuidedStepsBar(document: document, workspace: workspace, undoManager: undoManager) }
                        if workspace.page != .cast { BackgroundAIBar(document: document, page: workspace.page) }
                    }
                }
                .toolbar { toolbar }
        }
        .inspector(isPresented: Binding(get: { workspace.showInspector && workspace.page == .manuscript },
                                        set: { workspace.showInspector = $0 })) {
            ProjectInspector(document: document, workspace: workspace)
                .inspectorColumnWidth(min: 260, ideal: 300, max: 400)
                .toolbar {
                    ToolbarSpacer(.flexible)
                    ToolbarItem {
                        Button("Inspector", systemImage: "sidebar.trailing") { workspace.showInspector.toggle() }
                            .help("Show or hide the inspector (⌃⌘I)")
                    }
                }
        }
    }

    @ViewBuilder
    private var detail: some View {
        switch workspace.page {
        case .manuscript: ManuscriptPage(document: document, workspace: workspace)
        case .cast: CastPage(document: document, workspace: workspace)
        case .voices: VoicesPage(document: document, workspace: workspace)
        case .index: IndexPage(document: document, workspace: workspace)
        case .pronunciations: PronunciationsPage(document: document, workspace: workspace)
        case .teleprompter: TeleprompterPage(document: document, workspace: workspace)
        case .review: ReviewPage(document: document, workspace: workspace)
        case .revisions: RevisionsPage(document: document, workspace: workspace)
        }
    }

    @ToolbarContentBuilder
    private var toolbar: some ToolbarContent {
        if workspace.page == .manuscript {
            ToolbarItem {
                Picker("Color Style", selection: Binding(
                    get: { document.project.settings.style },
                    set: { s in document.updateSettings("Change Color Style", undoManager: undoManager) { $0.style = s } })) {
                    Label("Highlight", systemImage: "highlighter").tag(ColorStyle.highlight)
                    Label("Outline", systemImage: "rectangle.dashed").tag(ColorStyle.outline)
                    Label("Underline", systemImage: "underline").tag(ColorStyle.underline)
                }
                .pickerStyle(.segmented)
                .help("Highlight lines with their color, outline them or underline them (⌥⌘H, ⌥⌘O, ⌥⌘U)")
            }
            ToolbarItem {
                TextSizeControl()
            }
            ToolbarSpacer(.fixed)
            ToolbarItemGroup {
                let needs = document.project.linesNeedingYou
                Button("Previous Line That Needs You", systemImage: "chevron.up") {
                    NextLine.go(document: document, workspace: workspace, forward: false)
                }
                .help("Go to the previous red line (⌘↑)")
                .disabled(needs == 0)
                Button("Next Line That Needs You", systemImage: "chevron.down") {
                    NextLine.go(document: document, workspace: workspace, forward: true)
                }
                .help("Go to the next red line (⌘↓). \(needs) lines need you.")
                .disabled(needs == 0)
            }
            if document.project.showsVoices {
                ToolbarSpacer(.fixed)
                ToolbarItem {
                    PlayStopButton(document: document, workspace: workspace)
                }
            }
        }
        ToolbarItem {
            HelpButton(topic: workspace.page.helpTopic)
                .help("InnerVoice Help for \(workspace.page.title)")
        }
        .sharedBackgroundVisibility(.hidden)
        ToolbarItem {
            if document.tagging.isRunning {
                Button("Stop", systemImage: "stop.circle") { document.tagging.stop() }
                    .help("Stop the AI; what it has done so far is kept (⌘.)")
            } else {
                Button("Tag Speakers", systemImage: "sparkles") {
                    TagCommand.run(document: document, workspace: workspace, undoManager: undoManager)
                }
                .help("Use the on-device AI to find who speaks each line (⇧⌘T)")
            }
        }
    }
}

/// What menu commands act on: the frontmost book window.
struct ProjectFocus {
    var document: InnerVoiceDocument
    var workspace: Workspace
    var undoManager: UndoManager?
}

extension FocusedValues {
    @Entry var project: ProjectFocus?
}

extension Notification.Name {
    /// Actions from the menu bar item and ToolHUD: object is "tag", "teleprompter", "newFromManuscript".
    static let innerVoiceAction = Notification.Name("InnerVoice.action")
}

/// A document with no manuscript yet (File › New on its own).
struct EmptyProjectView: View {
    @ObservedObject var document: InnerVoiceDocument
    @Environment(\.undoManager) private var undoManager
    @State private var error: String?
    @State private var busy = false

    var body: some View {
        VStack(spacing: Space.xl) {
            DropArea(busy: busy, error: error) { url in Task { await load(url) } }
                .frame(maxWidth: 520, maxHeight: 300)
        }
        .padding(Space.section)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func load(_ url: URL) async {
        busy = true
        defer { busy = false }
        do {
            let (project, original) = try await Importer.project(from: url)
            document.change("Open Manuscript", undoManager: undoManager, textChanged: true) { $0 = project }
            document.original = original
        } catch {
            self.error = error.localizedDescription
        }
    }
}

/// Reads a manuscript off the main thread.
enum Importer {
    static func project(from url: URL) async throws -> (Project, (name: String, data: Data)?) {
        let accessing = url.startAccessingSecurityScopedResource()
        defer { if accessing { url.stopAccessingSecurityScopedResource() } }
        let book = try await Task.detached(priority: .userInitiated) { try ManuscriptImporter.read(url) }.value
        var project = ProjectBuilder.make(from: book, fileName: url.lastPathComponent)
        PronunciationFinder.refresh(&project, isKnownWord: SpellCheck.isKnown)
        // Activity: lines the rules tagged on import.
        let byRules = project.lines.filter { $0.speakerID != nil && ($0.source == .rule || $0.source == .exchange) }.count
        await MainActor.run { ActivityStore.shared.record(ActivityKey.linesRules, count: byRules) }
        var original: (String, Data)?
        if let data = try? Data(contentsOf: url), data.count < 200_000_000 { original = (url.lastPathComponent, data) }
        return (project, original)
    }

    static var contentTypes: [UTType] {
        ManuscriptImporter.fileExtensions.compactMap { UTType(filenameExtension: $0) }
    }

    /// File › New from Manuscript…: pick a file, read it, open a new project window.
    static func newFromManuscript(newDocument: NewDocumentAction, url: URL? = nil) {
        let chosen: URL?
        if let url {
            chosen = url
        } else {
            let panel = NSOpenPanel()
            panel.title = "Open Manuscript"
            panel.message = "Choose a Word, PDF, RTF, text, Markdown, OpenDocument, Pages, EPUB or HTML file."
            panel.prompt = "Open"
            panel.allowedContentTypes = contentTypes
            panel.allowsMultipleSelection = false
            chosen = panel.runModal() == .OK ? panel.url : nil
        }
        guard let chosen else { return }
        Task {
            do {
                let (project, original) = try await project(from: chosen)
                newDocument { InnerVoiceDocument(project: project, original: original) }
                StartWindowState.shared.lastError = nil
            } catch {
                StartWindowState.shared.lastError = error.localizedDescription
                let alert = NSAlert()
                alert.messageText = "Unable to Open Manuscript"
                alert.informativeText = error.localizedDescription
                alert.runModal()
            }
        }
    }
}

enum SpellCheck {
    /// The system dictionary, for finding unusual words.
    static func isKnown(_ word: String) -> Bool {
        let checker = NSSpellChecker.shared
        let range = checker.checkSpelling(of: word, startingAt: 0, language: "en", wrap: false, inSpellDocumentWithTag: 0, wordCount: nil)
        return range.location == NSNotFound
    }
}

/// Smaller and bigger text, one point a step, beside the color style in the toolbar.
/// The same as View › Bigger Text and Smaller Text, and the size in Settings › Manuscript.
struct TextSizeControl: View {
    @AppStorage("reading.fontSize", store: ReadingStyle.store) private var size = ReadingStyle.standardSize
    @AppStorage("reading.lineSpacing", store: ReadingStyle.store) private var spacing = LineSpacing.single.rawValue

    private var isDouble: Bool { spacing == LineSpacing.double.rawValue }

    var body: some View {
        ControlGroup {
            Button("Smaller Text", systemImage: "textformat.size.smaller") { ReadingStyle.setFontSize(size - 1) }
                .help("Make the manuscript text smaller (⌘−). Now \(Int(size)) pt.")
                .disabled(size <= ReadingStyle.sizes.lowerBound)
            Button("Bigger Text", systemImage: "textformat.size.larger") { ReadingStyle.setFontSize(size + 1) }
                .help("Make the manuscript text bigger (⌘+). Now \(Int(size)) pt.")
                .disabled(size >= ReadingStyle.sizes.upperBound)
        } label: {
            Label("Text Size", systemImage: "textformat.size")
        }
        Toggle(isOn: Binding(get: { isDouble }, set: { ReadingStyle.setSpacing($0 ? .double : .single) })) {
            Label("Double Spacing", systemImage: "arrow.up.and.down.text.horizontal")
        }
        .help(isDouble ? "Switch to single line spacing (⌥⌘L). Now double spaced."
                       : "Switch to double line spacing (⌥⌘L). Now single spaced.")
    }
}

/// While speech is being made for the first time, and when it can't play: a small bar at
/// the bottom of the window. Without it a model loading for several seconds, or an error,
/// looked like nothing happening.
struct SpeechStatusBar: View {
    private var player: SpeechPlayer { .shared }

    var body: some View {
        Group {
            if let failure = player.failure {
                bar {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                    Text(failure).lineLimit(2)
                    Button("Dismiss") { player.failure = nil }.controlSize(.small)
                }
            } else if player.isPlaying, player.preparing {
                bar {
                    ProgressView().controlSize(.small)
                    Text(message)
                    Button("Stop") { player.stop() }.controlSize(.small)
                }
            }
        }
        .animation(.easeOut(duration: 0.2), value: player.preparing)
        .animation(.easeOut(duration: 0.2), value: player.failure)
    }

    private var message: String {
        switch player.preparingEngine {
        case .qwen?, .orpheus?: "Getting \(player.preparingEngine!.title) ready… The first line takes a few seconds."
        case .elevenlabs?: "Sending the line to ElevenLabs…"
        default: "Making the audio…"
        }
    }

    private func bar<C: View>(@ViewBuilder _ content: () -> C) -> some View {
        HStack(spacing: Space.sm) { content() }
            .font(.callout)
            .padding(.horizontal, Space.md)
            .padding(.vertical, Space.sm)
            .glassEffect(.regular, in: Capsule())
            .padding(.bottom, Space.lg)
            .transition(.move(edge: .bottom).combined(with: .opacity))
    }
}
