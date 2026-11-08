import AppKit
import InnerVoiceKit
import SwiftUI
import UniformTypeIdentifiers

/// One book's window: sidebar, page, inspector.
struct ProjectWindow: View {
    @ObservedObject var document: InnerVoiceDocument
    let fileURL: URL?
    @State private var workspace = Workspace()
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        Group {
            if document.project.isEmpty {
                EmptyProjectView(document: document)
            } else {
                split
            }
        }
        .frame(minWidth: 900, minHeight: 560)
        .focusedSceneValue(\.project, ProjectFocus(document: document, workspace: workspace, undoManager: undoManager))
        .sheet(isPresented: $workspace.showModelSheet) {
            ModelSheet { ready in
                workspace.showModelSheet = false
                if ready { TagCommand.run(document: document, workspace: workspace, undoManager: undoManager) }
            }
        }
        .sheet(isPresented: $workspace.showExport) {
            ExportSheet(document: document, isPresented: $workspace.showExport)
        }
        .sheet(item: Binding(get: { workspace.rehearse.map(RehearseTarget.init) }, set: { workspace.rehearse = $0?.id })) { target in
            RehearseView(document: document, characterID: target.id, workspace: workspace)
        }
        .onReceive(NotificationCenter.default.publisher(for: SystemHooks.tagSpeakers)) { _ in
            if isFrontProject { TagCommand.run(document: document, workspace: workspace, undoManager: undoManager) }
        }
        .onReceive(NotificationCenter.default.publisher(for: SystemHooks.record)) { _ in
            if isFrontProject { workspace.page = .record }
        }
        .task { Snapshot.attach(document: document, workspace: workspace) }
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
        case .index: IndexPage(document: document, workspace: workspace)
        case .pronunciations: PronunciationsPage(document: document, workspace: workspace)
        case .record: RecordPage(document: document, workspace: workspace)
        case .retakes: RetakesPage(document: document, workspace: workspace)
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
                }
                .pickerStyle(.segmented)
                .help("Highlight lines with their color, or outline them (⌥⌘H, ⌥⌘O)")
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
            ToolbarSpacer(.fixed)
        }
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

struct RehearseTarget: Identifiable {
    var id: UUID
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
    /// Actions from the menu bar item and ToolHUD: object is "tag", "record", "newFromManuscript".
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
