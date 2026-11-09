import AppKit
import IOKit.pwr_mgt
import InnerVoiceKit
import SwiftUI

/// Screenshot mode for agents.
///   APP_SNAPSHOT=/tmp/InnerVoice-shots "<app>/Contents/MacOS/InnerVoice"
/// opens the sample book in a new window and writes <page>-light.png and <page>-dark.png
/// for every page, popover, sheet and Settings tab, then quits. It never opens or saves
/// a real project. APP_SNAPSHOT_PAGES=manuscript,cast limits the pages.
enum Snapshot {
    static var requested: Bool { !(ProcessInfo.processInfo.environment["APP_SNAPSHOT"] ?? "").isEmpty }
    private static var attached: (InnerVoiceDocument, Workspace)?
    static var newDocument: NewDocumentAction?

    /// The sample book this run created. Only its window is ever driven or photographed.
    private static var sample: InnerVoiceDocument?

    /// Called by every project window; snapshot mode drives the sample book's, never a real
    /// project that macOS reopened.
    static func attach(document: InnerVoiceDocument, workspace: Workspace) {
        guard requested, attached == nil, let sample, document === sample else { return }
        attached = (document, workspace)
    }

    static func runIfRequested() {
        guard requested, let dir = ProcessInfo.processInfo.environment["APP_SNAPSHOT"] else { return }
        let only = ProcessInfo.processInfo.environment["APP_SNAPSHOT_PAGES"].map { Set($0.split(separator: ",").map(String.init)) }
        func wants(_ page: String) -> Bool { only?.contains(page) ?? true }
        Task {
            var activity: IOPMAssertionID = 0
            IOPMAssertionDeclareUserActivity("Snapshots" as CFString, kIOPMUserActiveLocal, &activity)
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
            try? await Task.sleep(for: .seconds(1.5))
            for mode in ["light", "dark"] {
                NSApp.appearance = NSAppearance(named: mode == "light" ? .aqua : .darkAqua)
                if wants("start"), let start = NSApp.windows.first(where: { $0.title == "Welcome to Inner Voice" && $0.isVisible }) {
                    try? await Task.sleep(for: .seconds(0.6))
                    capture(start, "\(dir)/start-\(mode).png")
                }
            }
            guard let newDocument else { print("SNAPSHOT no newDocument action"); finish(); return }
            // Anything else open came from outside this run: close it unsaved, untouched.
            for doc in NSDocumentController.shared.documents { doc.close() }
            let seed = InnerVoiceDocument(project: SeedData.project())
            sample = seed
            newDocument { seed }
            for _ in 0..<40 where attached == nil { try? await Task.sleep(for: .seconds(0.25)) }
            guard let (document, workspace) = attached else { print("SNAPSHOT no project window"); finish(); return }
            NSApp.windows.first { $0.title == "Welcome to Inner Voice" }?.close()
            try? await Task.sleep(for: .seconds(1))
            let window = { NSApp.windows.first { $0.isVisible && $0.canBecomeMain && !$0.isSheet && $0.title != "Welcome to Inner Voice" } }
            // The same size on a Retina screen for every page, set again before each shot:
            // SwiftUI restores the window frame the person's own copy saved (the preferences
            // are shared), which made a run come out tall and at 1×.
            let place = {
                guard let w = window() else { return }
                let screen = NSScreen.screens.first { $0.backingScaleFactor >= 2 } ?? w.screen ?? NSScreen.main
                guard let area = screen?.visibleFrame else { return }
                let size = NSSize(width: min(1280, area.width - 80), height: min(800, area.height - 80))
                let frame = NSRect(x: area.midX - size.width / 2, y: area.midY - size.height / 2, width: size.width, height: size.height)
                if w.frame != frame { w.setFrame(frame, display: true) }
            }
            place()
            // A project window's title is its file name; the sample has none.
            for doc in NSDocumentController.shared.documents {
                doc.displayName = SeedData.project().title
                doc.windowControllers.forEach { $0.synchronizeWindowTitleWithDocumentName() }
            }
            let lines = document.project.lines
            let red = lines.first { document.project.needsYou($0) }

            for mode in ["light", "dark"] {
                NSApp.appearance = NSAppearance(named: mode == "light" ? .aqua : .darkAqua)
                @MainActor func shot(_ name: String, settle: Double = 0.8, _ setup: () -> Void) async {
                    guard wants(name) else { return }
                    place()
                    setup()
                    try? await Task.sleep(for: .seconds(settle))
                    if let w = window() { capture(w, "\(dir)/\(name)-\(mode).png") }
                }
                await shot("manuscript") {
                    workspace.page = .manuscript
                    document.updateSettings("", undoManager: nil) { $0.style = .highlight }
                    if let red { workspace.show(paragraph: red.paragraphID, line: red.id) }
                }
                await shot("manuscript-outline") { document.updateSettings("", undoManager: nil) { $0.style = .outline } }
                await shot("manuscript-underline") { document.updateSettings("", undoManager: nil) { $0.style = .underline } }
                // Reading preferences live in memory only here: the snapshot copy shares the
                // person's real preferences.
                await shot("manuscript-names", settle: 1.2) {
                    reading("reading.marginNames", MarginNames.fullName.rawValue)
                    reading("reading.lineSpacing", LineSpacing.double.rawValue)
                    document.updateSettings("", undoManager: nil) { $0.style = .highlight }
                    document.objectWillChange.send()
                }
                await shot("manuscript-first-names", settle: 1.2) {
                    reading("reading.marginNames", MarginNames.firstName.rawValue)
                    reading("reading.lineSpacing", nil)
                    document.updateSettings("", undoManager: nil) { $0.style = .outline }
                    document.objectWillChange.send()
                }
                reading("reading.marginNames", nil)
                document.updateSettings("", undoManager: nil) { $0.style = .highlight }
                document.objectWillChange.send()
                if wants("assign-popover"), let red {
                    workspace.page = .manuscript
                    workspace.pendingAssign = red.id
                    try? await Task.sleep(for: .seconds(1))
                    if let pop = NSApp.windows.first(where: { $0.isVisible && String(describing: type(of: $0)).contains("Popover") }) {
                        capture(pop, "\(dir)/assign-popover-\(mode).png")
                    }
                    PopoverPresenter.shared.close()
                }
                await shot("exchange-banner") {
                    workspace.exchange = lines.prefix(4).compactMap { l in l.speakerID.map { Exchange.Proposal(lineID: l.id, speakerID: $0) } }
                }
                workspace.exchange = []
                await shot("manuscript-tagging") {
                    document.tagging.debugStatus("Tagging Chapter 2: The Letter (2 of 3)…", 0.45)
                }
                document.tagging.debugStatus(nil, 0)
                await shot("cast") { workspace.castPath = []; workspace.page = .cast }
                if let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }) {
                    await shot("character-detail", settle: 1.2) { workspace.page = .cast; workspace.castPath = [main.id] }
                    await shot("rehearse") { workspace.rehearse = main.id }
                    workspace.rehearse = nil
                    try? await Task.sleep(for: .seconds(0.5))
                }
                await shot("index") { workspace.page = .index }
                await shot("pronunciations") { workspace.page = .pronunciations }
                await shot("record") {
                    workspace.page = .record
                    workspace.recordParagraph = document.project.chapters.first?.paragraphs.dropFirst(1).first?.id
                }
                await shot("retakes") { workspace.page = .retakes }
                await shot("revisions") { workspace.page = .revisions }
                await shot("export-sheet") { workspace.page = .manuscript; workspace.showExport = true }
                workspace.showExport = false
                try? await Task.sleep(for: .seconds(0.5))
                await shot("model-sheet") { workspace.showModelSheet = true }
                workspace.showModelSheet = false
                try? await Task.sleep(for: .seconds(0.5))
            }
            if wants("settings") {
                for mode in ["light", "dark"] {
                    NSApp.appearance = NSAppearance(named: mode == "light" ? .aqua : .darkAqua)
                    for tab in SettingsTab.allCases {
                        SettingsWindow.shared.show(tab: tab)
                        try? await Task.sleep(for: .seconds(0.9))
                        if let s = SettingsWindow.shared.window { capture(s, "\(dir)/settings-\(tab.rawValue)-\(mode).png") }
                    }
                }
                SettingsWindow.shared.window?.close()
            }
            print("SNAPSHOT done \(dir)")
            finish()
        }
    }

    /// Sets a preference for this run only (the argument domain is never saved).
    static func reading(_ key: String, _ value: Any?) {
        var arguments = UserDefaults.standard.volatileDomain(forName: UserDefaults.argumentDomain)
        arguments[key] = value
        UserDefaults.standard.setVolatileDomain(arguments, forName: UserDefaults.argumentDomain)
    }

    /// Quits without asking to save the sample book.
    static func finish() {
        for w in NSApp.windows { if let s = w.attachedSheet { w.endSheet(s) } }
        for doc in NSDocumentController.shared.documents { doc.updateChangeCount(.changeCleared); doc.close() }
        NSApp.terminate(nil)
    }

    /// Captures a window through the window server, so glass and sidebars draw as on screen.
    static func capture(_ window: NSWindow, _ path: String, includePopovers: Bool = false) {
        if NSApp.isHidden { NSApp.unhideWithoutActivation() }
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
        try? FileManager.default.removeItem(atPath: path)
        var args = ["-x", "-o", "-l", String(window.windowNumber), path]
        if includePopovers {
            // A popover is its own window: capture the screen area around the main window instead.
            let f = window.frame
            let screenH = NSScreen.screens.first?.frame.height ?? f.maxY
            args = ["-x", "-R", "\(Int(f.minX)),\(Int(screenH - f.maxY)),\(Int(f.width)),\(Int(f.height))", path]
        }
        for _ in 1...3 {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
            p.arguments = args
            try? p.run()
            p.waitUntilExit()
            if FileManager.default.fileExists(atPath: path) { print("SNAPSHOT \(path)"); return }
            Thread.sleep(forTimeInterval: 0.5)
        }
        print("SNAPSHOT failed \(path)")
    }
}
