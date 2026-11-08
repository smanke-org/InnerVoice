import AppKit
import InnerVoiceKit
import SwiftUI

// Inner Voice
//   Inner Voice                        the app
//   APP_SNAPSHOT=<dir> InnerVoice      open the sample book, save a PNG of every page, panel and Settings tab, quit
//   InnerVoice --selftest              run the self-test on the sample book and quit with 0 or 1 (tools/selftest.sh)
//   APP_MODELS=<dir>                   keep downloaded models somewhere else (tests)
//   APP_ACCENT=<id>                    run with another accent from Theme.all (UI/Theme.swift)

@main
struct InnerVoiceApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        DocumentGroup(newDocument: { InnerVoiceDocument() }) { file in
            ProjectWindow(document: file.document, fileURL: file.fileURL)
                .themed()
                .overlay { if GridOverlay.enabled { GridOverlay() } }
                .background(WindowOpener())
        }
        .defaultSize(width: 1280, height: 820)
        .windowToolbarStyle(.unified)
        .commands {
            SystemAppCommands()
            SystemHelpCommands()
            SidebarCommands()
            ToolbarCommands()
            InspectorCommands()
            FileCommands()
            BookCommands()
            ViewCommands()
        }

        Window("Welcome to Inner Voice", id: "start") {
            StartWindow()
                .themed()
                .background(WindowOpener())
        }
        .windowResizability(.contentSize)
        .windowStyle(.hiddenTitleBar)
        .defaultLaunchBehavior(.presented)
        .restorationBehavior(.disabled)

        Settings {
            SettingsView()
                .themed()
        }
        .windowResizability(.contentMinSize)
    }
}

/// Keeps SwiftUI's openWindow so AppKit code (Dock reopen) can show the Welcome window.
struct WindowOpener: View {
    @Environment(\.openWindow) private var openWindow
    @Environment(\.newDocument) private var newDocument
    @Environment(\.openSettings) private var openSettings
    var body: some View {
        Color.clear.frame(width: 0, height: 0).onAppear {
            AppState.openWindow = openWindow
            AppState.newDocument = newDocument
            Snapshot.newDocument = newDocument
            Snapshot.openSettings = openSettings
            SystemHooks.settingsOpener = { openSettings() }
        }
    }
}

enum AppState {
    static var openWindow: OpenWindowAction?
    static var newDocument: NewDocumentAction?

    static func showStart() {
        openWindow?(id: "start")
    }

    static var hasDocumentWindows: Bool { !NSDocumentController.shared.documents.isEmpty }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillFinishLaunching(_ notification: Notification) {
        // The Welcome window replaces the open panel macOS shows at launch for document apps.
        UserDefaults.standard.register(defaults: ["NSShowAppCentricOpenPanelInsteadOfUntitledFile": false])
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        SystemHooks.busyReason = { TaggingController.busyReason }
        SystemHooks.didFinishLaunching()
        NotificationCenter.default.addObserver(forName: SystemHooks.newFromManuscript, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated {
                if let new = AppState.newDocument { Importer.newFromManuscript(newDocument: new) }
            }
        }
        if Snapshot.requested || SelfTest.requested {
            NSApp.setActivationPolicy(.regular)
            NSApp.activate()
            Snapshot.runIfRequested()
            SelfTest.runIfRequested()
        }
    }

    func applicationShouldOpenUntitledFile(_ sender: NSApplication) -> Bool { false }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        SystemHooks.shouldTerminate(sender)
    }

    func applicationDockMenu(_ sender: NSApplication) -> NSMenu? { SystemHooks.dockMenu() }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        if !hasVisibleWindows { AppState.showStart() }
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        SystemHooks.willTerminate()
        ModelManager.shared.unloadNow()
        if SelfTest.requested { exit(SelfTest.exitCode) }
    }
}

// MARK: - Menus

struct FileCommands: Commands {
    @Environment(\.newDocument) private var newDocument
    @FocusedValue(\.project) private var focus

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New from Manuscript…") { Importer.newFromManuscript(newDocument: newDocument) }
                .keyboardShortcut("n")
            Button("Open…") { NSDocumentController.shared.openDocument(nil) }
                .keyboardShortcut("o")
            Menu("Open Recent") {
                ForEach(NSDocumentController.shared.recentDocumentURLs, id: \.self) { url in
                    Button(url.deletingPathExtension().lastPathComponent) {
                        NSDocumentController.shared.openDocument(withContentsOf: url, display: true) { _, _, _ in }
                    }
                }
                Divider()
                Button("Clear Menu") { NSDocumentController.shared.clearRecentDocuments(nil) }
            }
        }
        CommandGroup(after: .saveItem) {
            Divider()
            Button("Import Revised Manuscript…") {
                if let focus { RevisionImport.run(focus) }
            }
            .keyboardShortcut("i", modifiers: [.command, .shift])
            .disabled(focus == nil)
            Button("Export…") { focus?.workspace.showExport = true }
                .keyboardShortcut("e", modifiers: [.command, .shift])
                .disabled(focus == nil)
        }
    }
}

struct BookCommands: Commands {
    @FocusedValue(\.project) private var focus

    var body: some Commands {
        CommandMenu("Book") {
            let running = focus?.document.tagging.isRunning ?? false
            Button("Tag Speakers") {
                if let f = focus { TagCommand.run(document: f.document, workspace: f.workspace, undoManager: f.undoManager) }
            }
            .keyboardShortcut("t", modifiers: [.command, .shift])
            .disabled(focus == nil || running)
            Button("Build Summaries") {
                if let f = focus { TagCommand.summaries(document: f.document, workspace: f.workspace, undoManager: f.undoManager) }
            }
            .keyboardShortcut("b", modifiers: [.command, .shift])
            .disabled(focus == nil || running)
            Button("Stop") { focus?.document.tagging.stop() }
                .keyboardShortcut(".")
                .disabled(!running)
            Divider()
            Button("Next Line That Needs You") {
                if let f = focus { NextLine.go(document: f.document, workspace: f.workspace, forward: true) }
            }
            .keyboardShortcut(.downArrow)
            .disabled(focus == nil)
            Button("Previous Line That Needs You") {
                if let f = focus { NextLine.go(document: f.document, workspace: f.workspace, forward: false) }
            }
            .keyboardShortcut(.upArrow)
            .disabled(focus == nil)
            Divider()
            Button("Find Pronunciations") {
                if let f = focus {
                    f.document.change("Find Terms", undoManager: f.undoManager) { PronunciationFinder.refresh(&$0, isKnownWord: SpellCheck.isKnown) }
                    f.workspace.page = .pronunciations
                }
            }
            .disabled(focus == nil)
            Button("Choose Model…") { focus?.workspace.showModelSheet = true }
                .disabled(focus == nil)
        }
    }
}

struct ViewCommands: Commands {
    @FocusedValue(\.project) private var focus

    var body: some Commands {
        CommandGroup(before: .sidebar) {
            ForEach(Workspace.Page.allCases.filter { $0.shortcut != nil }) { page in
                Button(page.title) { focus?.workspace.page = page }
                    .keyboardShortcut(page.shortcut!)
                    .disabled(focus == nil)
            }
            Button("Start Recording") { focus?.workspace.page = .record }
                .keyboardShortcut("r", modifiers: [.command, .shift])
                .disabled(focus == nil)
            Divider()
            Button("Highlight Lines") { setStyle(.highlight) }
                .keyboardShortcut("h", modifiers: [.command, .option])
                .disabled(focus == nil)
            Button("Outline Lines") { setStyle(.outline) }
                .keyboardShortcut("o", modifiers: [.command, .option])
                .disabled(focus == nil)
            Toggle("Show Initials", isOn: Binding(get: { focus?.document.project.settings.showInitials ?? true }, set: { v in
                focus?.document.updateSettings("Show Initials", undoManager: focus?.undoManager) { $0.showInitials = v }
            }))
            .disabled(focus == nil)
            Divider()
        }
    }

    private func setStyle(_ s: ColorStyle) {
        focus?.document.updateSettings("Change Color Style", undoManager: focus?.undoManager) { $0.style = s }
    }
}

/// File › Import Revised Manuscript…
enum RevisionImport {
    static func run(_ focus: ProjectFocus) {
        let panel = NSOpenPanel()
        panel.title = "Import Revised Manuscript"
        panel.message = "Choose the author's new draft. Your tags, notes and recorded marks stay on the paragraphs that didn't change."
        panel.prompt = "Import"
        panel.allowedContentTypes = Importer.contentTypes
        guard panel.runModal() == .OK, let url = panel.url else { return }
        Task {
            do {
                let book = try await Task.detached { try ManuscriptImporter.read(url) }.value
                let result = RevisionMerger.merge(focus.document.project, with: book, fileName: url.lastPathComponent)
                focus.document.change("Import Revised Manuscript", undoManager: focus.undoManager, textChanged: true) { $0 = result.project }
                if let data = try? Data(contentsOf: url) { focus.document.original = (url.lastPathComponent, data) }
                focus.workspace.page = .revisions
            } catch {
                let alert = NSAlert()
                alert.messageText = "Unable to Import Revised Manuscript"
                alert.informativeText = error.localizedDescription
                alert.runModal()
            }
        }
    }
}
