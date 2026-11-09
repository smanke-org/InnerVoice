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
                if wants("start"), let start = NSApp.windows.first(where: { $0.title == "Welcome to InnerVoice" && $0.isVisible }) {
                    try? await Task.sleep(for: .seconds(0.6))
                    capture(start, "\(dir)/start-\(mode).png")
                }
            }
            // The usual reading look, whatever the person chose (in memory only): pictures
            // stay the same from run to run.
            reading("reading.fontSize", ReadingStyle.standardSize)
            reading("reading.serif", true)
            reading("reading.lineSpacing", LineSpacing.single.rawValue)
            reading("reading.marginNames", MarginNames.initials.rawValue)
            guard let newDocument else { print("SNAPSHOT no newDocument action"); finish(); return }
            // Anything else open came from outside this run: close it unsaved, untouched.
            for doc in NSDocumentController.shared.documents { doc.close() }
            let seed = InnerVoiceDocument(project: SeedData.project())
            sample = seed
            newDocument { seed }
            for _ in 0..<40 where attached == nil { try? await Task.sleep(for: .seconds(0.25)) }
            guard let (document, workspace) = attached else { print("SNAPSHOT no project window"); finish(); return }
            NSApp.windows.first { $0.title == "Welcome to InnerVoice" }?.close()
            try? await Task.sleep(for: .seconds(1))
            let window = { NSApp.windows.first { $0.isVisible && $0.canBecomeMain && !$0.isSheet && $0.title != "Welcome to InnerVoice" } }
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
                }
                await shot("manuscript-first-names", settle: 1.2) {
                    reading("reading.marginNames", MarginNames.firstName.rawValue)
                    reading("reading.lineSpacing", LineSpacing.single.rawValue)
                    document.updateSettings("", undoManager: nil) { $0.style = .outline }
                }
                await shot("manuscript-bigger", settle: 1.2) {
                    ReadingStyle.setFontSize(21)
                    ReadingStyle.setSpacing(.single)
                }
                ReadingStyle.setFontSize(ReadingStyle.standardSize)
                reading("reading.marginNames", MarginNames.initials.rawValue)
                document.updateSettings("", undoManager: nil) { $0.style = .highlight }
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
                await shot("review", settle: 1.0) { workspace.reviewFilter = "guesses"; workspace.page = .review }
                await shot("review-tones", settle: 1.0) { workspace.reviewFilter = "tones"; workspace.page = .review }
                document.castVoicesAgain(undoManager: nil)
                if let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }) {
                    await shot("voices", settle: 1.2) { workspace.voiceTarget = .character(main.id); workspace.page = .voices }
                }
                await shot("voices-narrator", settle: 1.0) { workspace.voiceTarget = .narrator; workspace.page = .voices }
                if let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }) {
                    for engine in [VoiceEngine.orpheus, .qwen] {
                        await shot("voices-\(engine.rawValue)", settle: 1.2) {
                            document.setVoiceEngine(engine, undoManager: nil)
                            // Qwen3-TTS: the character with the fullest description shows it best.
                            let pick = engine == .qwen
                                ? document.project.characters.max { ($0.voice(for: .qwen)?.choice.design?.count ?? 0) < ($1.voice(for: .qwen)?.choice.design?.count ?? 0) } ?? main
                                : main
                            workspace.voiceTarget = .character(pick.id)
                            workspace.page = .voices
                        }
                    }
                    document.setVoiceEngine(.apple, undoManager: nil)
                }
                await shot("review-voices", settle: 1.0) { workspace.reviewFilter = "voices"; workspace.page = .review }
                await shot("audio-export-sheet") { workspace.page = .manuscript; workspace.showAudioExport = true }
                workspace.showAudioExport = false
                try? await Task.sleep(for: .seconds(0.5))
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
                // Orpheus turned off (test runs never save the switch).
                EngineSwitches.shared.set(.orpheus, on: false)
                SettingsWindow.shared.show(tab: .voices)
                try? await Task.sleep(for: .seconds(0.9))
                if let s = SettingsWindow.shared.window { capture(s, "\(dir)/settings-voices-orpheus-off-dark.png") }
                EngineSwitches.shared.set(.orpheus, on: true)
                // The tab is remembered in the person's real preferences: leave it on General.
                SettingsWindow.shared.show(tab: .general)
                SettingsWindow.shared.window?.close()
            }
            if wants("review-keys") { await reviewKeysTest(document: document, workspace: workspace) }
            if wants("cast-click") { await castClickTest(document: document, workspace: workspace) }
            // Last: driving real menus leaves the app inactive, which would gray later shots.
            if wants("speaker-menu") {
                NSApp.appearance = NSAppearance(named: .aqua)
                workspace.page = .manuscript
                try? await Task.sleep(for: .seconds(0.8))
                await speakerMenuTest(document: document, dir: dir, submenu: false)
                await speakerMenuTest(document: document, dir: dir, submenu: true)
            }
            print("SNAPSHOT done \(dir)")
            finish()
        }
    }

    /// Opens the margin's speaker menu on a line, types part of another speaker's surname
    /// (a search match, not a menu type-select match), photographs the menu before and after,
    /// presses Return and checks the paragraph's lines went to that speaker.
    @MainActor
    static func speakerMenuTest(document: InnerVoiceDocument, dir: String, submenu: Bool) async {
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.canBecomeMain && !$0.isSheet }),
              let tv = find(ReadingTextView.self, in: window.contentView), let c = tv.coordinator else {
            print("SNAPSHOT speaker-menu: no reading view"); return
        }
        let project = document.project
        guard let line = project.lines.first(where: { $0.speakerID != nil && !project.needsYou($0) }),
              let target = project.characters.first(where: { $0.id != line.speakerID && $0.name.split(separator: " ").count > 1 }) else {
            print("SNAPSHOT speaker-menu: no suitable line"); return
        }
        c.reveal(Workspace.Reveal(paragraphID: line.paragraphID, lineID: line.id))
        try? await Task.sleep(for: .seconds(0.6))
        guard let e = c.layout.entries.first(where: { $0.lineID == line.id }), let rect = c.rects(for: e.range).first else { return }
        let point = submenu ? NSPoint(x: rect.midX, y: rect.midY) : NSPoint(x: tv.textContainerOrigin.x - 30, y: rect.midY)
        tv.setSelectedRange(NSRange(location: e.range.location, length: 0))
        guard let menu = submenu ? c.menu(for: e.range.location, selection: NSRange(location: e.range.location, length: 0)) : c.marginMenu(at: point) else {
            print("SNAPSHOT speaker-menu: no margin menu at \(point)"); return
        }
        let tag = submenu ? "speaker-submenu" : "speaker-menu"
        let surname = String(target.name.split(separator: " ").last!)
        let query = String(surname.dropFirst().prefix(3)).lowercased()
        let pid = getpid()
        func key(_ code: CGKeyCode, _ text: String?) {
            for down in [true, false] {
                let ev = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: down)
                if let text { let u = Array(text.utf16); ev?.keyboardSetUnicodeString(stringLength: u.count, unicodeString: u) }
                ev?.postToPid(pid)
            }
        }
        func shootMenu(_ name: String) {
            let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] ?? []
            let mine = list.filter { ($0[kCGWindowOwnerPID as String] as? Int32) == pid && ($0[kCGWindowLayer as String] as? Int ?? 0) >= 101 }
            guard !mine.isEmpty else { print("SNAPSHOT speaker-menu: no menu window"); return }
            for (n, w) in mine.enumerated() {
                guard let id = w[kCGWindowNumber as String] as? Int else { continue }
                let p = Process()
                p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
                p.arguments = ["-x", "-o", "-l", String(id), "\(dir)/\(name)-\(n).png"]
                try? p.run(); p.waitUntilExit()
                print("SNAPSHOT \(dir)/\(name)-\(n).png")
            }
        }
        // Run-loop timers, not the main queue: this runs inside a main-queue job, and the
        // queue can't run another while popUp spins the run loop under it.
        func after(_ seconds: Double, _ body: @escaping @MainActor () -> Void) {
            let t = Timer(timeInterval: seconds, repeats: false) { _ in MainActor.assumeIsolated { body() } }
            RunLoop.main.add(t, forMode: .common)
        }
        // In the line's menu: Down highlights Assign To, Right opens it (as hovering does).
        if submenu { after(0.8) { key(125, nil); key(124, nil) } }
        after(submenu ? 1.8 : 1.0) {
            shootMenu("\(tag)-open")
            for ch in query { key(0, String(ch)) }
            after(0.8) {
                shootMenu("\(tag)-typed")
                key(36, "\r")
            }
        }
        // Safety net: never leave a menu open if the keys didn't land.
        after(6) { menu.cancelTracking() }
        menu.popUp(positioning: nil, at: point, in: tv)
        try? await Task.sleep(for: .seconds(0.5))
        let assigned = document.project.lines.first { $0.id == line.id }?.speakerID
        print("SNAPSHOT \(tag) typed “\(query)” for \(target.name): " + (assigned == target.id ? "PASS assigned" : "FAIL got \(document.project.character(assigned)?.name ?? "nobody")"))
        c.parent.undoManager?.undo()
    }

    /// On the Cast page, a real mouse click on a character's name opens their page.
    @MainActor
    static func castClickTest(document: InnerVoiceDocument, workspace: Workspace) async {
        guard let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }) else { return }
        workspace.castPath = []
        workspace.page = .cast
        try? await Task.sleep(for: .seconds(1.2))
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.canBecomeMain && !$0.isSheet }), let content = window.contentView,
              let frame = CastCard.nameFrames[main.id] else {
            print("SNAPSHOT cast-click: FAIL no name frame"); return
        }
        window.makeKeyAndOrderFront(nil)
        // SwiftUI's global space is the window's content, top-left origin.
        let inWindow = NSPoint(x: frame.midX, y: content.bounds.height - frame.midY)
        let hit = content.hitTest(content.convert(inWindow, from: nil))
        print("SNAPSHOT cast-click: name at \(frame), window point \(inWindow), hits \(hit.map { String(describing: type(of: $0)) } ?? "nothing")")
        // The click goes through the window's own event path (posting to the process doesn't
        // reach a test copy launched from the shell).
        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            if let ev = NSEvent.mouseEvent(with: type, location: inWindow, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                           windowNumber: window.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: type == .leftMouseDown ? 1 : 0) {
                window.sendEvent(ev)
            }
            try? await Task.sleep(for: .milliseconds(60))
        }
        try? await Task.sleep(for: .seconds(1.0))
        print("SNAPSHOT cast-click on \(main.name): " + (workspace.castPath == [main.id] ? "PASS opened their page" : "FAIL path \(workspace.castPath)"))
        workspace.castPath = []
    }

    /// On the Review page: Return confirms the guess; then for a line with no speaker,
    /// typing "ed" and Return gives it to Edda. Real key events, sent to this app.
    @MainActor
    static func reviewKeysTest(document: InnerVoiceDocument, workspace: Workspace) async {
        let project = document.project
        guard let guessLine = project.lines.first(where: { project.needsYou($0) && $0.speakerID != nil }) else {
            print("SNAPSHOT review-keys: no guess in the sample"); return
        }
        let guess = guessLine.speakerID
        let undo = NSApp.windows.compactMap { find(ReadingTextView.self, in: $0.contentView)?.coordinator?.parent.undoManager }.first
            ?? NSApp.windows.first { $0.isVisible && $0.canBecomeMain }?.undoManager
        workspace.reviewFilter = "guesses"
        workspace.page = .review
        try? await Task.sleep(for: .seconds(1.2))
        NSApp.windows.first { $0.isVisible && $0.canBecomeMain }?.makeKeyAndOrderFront(nil)
        let pid = getpid()
        func key(_ code: CGKeyCode, _ text: String?) {
            for down in [true, false] {
                let ev = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: down)
                if let text { let u = Array(text.utf16); ev?.keyboardSetUnicodeString(stringLength: u.count, unicodeString: u) }
                ev?.postToPid(pid)
            }
        }
        // Typing narrows the speakers; Return gives the line to the first match.
        let edda = document.project.characters.first { $0.name.hasPrefix("Edda") }
        for ch in "ed" { key(0, String(ch)) }
        try? await Task.sleep(for: .seconds(0.4))
        key(36, "\r")
        try? await Task.sleep(for: .seconds(0.8))
        let typed = document.project.lines.first { $0.id == guessLine.id }
        print("SNAPSHOT review-keys typed: " + (typed?.speakerID == edda?.id && typed?.source == .user ? "PASS gave it to Edda" : "FAIL got \(document.project.character(typed?.speakerID)?.name ?? "nobody")"))
        undo?.undo()
        workspace.page = .manuscript
        try? await Task.sleep(for: .seconds(0.4))
        workspace.reviewFilter = "guesses"
        workspace.page = .review
        try? await Task.sleep(for: .seconds(1.2))
        // Return with nothing typed confirms the guess.
        key(36, "\r")
        try? await Task.sleep(for: .seconds(0.8))
        let after = document.project.lines.first { $0.id == guessLine.id }
        print("SNAPSHOT review-keys Return: " + (after?.source == .user && after?.speakerID == guess ? "PASS confirmed the guess" : "FAIL source \(after?.source.rawValue ?? "-")"))
        undo?.undo()
        // In the manuscript: ⌘↓'s jump opens Assign To with the guess picked; Return confirms.
        workspace.page = .manuscript
        workspace.selectedLines = []
        try? await Task.sleep(for: .seconds(0.8))
        var tries = 0
        while tries < 5, document.project.lines.first(where: { $0.id == guessLine.id }).map({ !$0.isConfirmed }) == true {
            NextLine.go(document: document, workspace: workspace, forward: true)
            try? await Task.sleep(for: .seconds(1.2))
            if workspace.selectedLines == [guessLine.id] { key(36, "\r") } else { key(53, nil) }
            try? await Task.sleep(for: .seconds(0.8))
            tries += 1
        }
        let jumped = document.project.lines.first { $0.id == guessLine.id }
        print("SNAPSHOT review-keys ⌘↓ then Return: " + (jumped?.source == .user && jumped?.speakerID == guess ? "PASS confirmed in the popover" : "FAIL"))
        PopoverPresenter.shared.close()
        while undo?.canUndo == true { undo?.undo() }

        // ⌥Space (Play Line) plays the selected line in its voice.
        if let spoken = document.project.lines.first(where: { $0.speakerID != nil && !document.project.needsYou($0) }) {
            workspace.page = .manuscript
            workspace.show(paragraph: spoken.paragraphID, line: spoken.id)
            try? await Task.sleep(for: .seconds(1.0))
            // As after clicking the line: the reading view has the keyboard.
            if let w = NSApp.windows.first(where: { find(ReadingTextView.self, in: $0.contentView) != nil }), let tv = find(ReadingTextView.self, in: w.contentView) {
                w.makeKeyAndOrderFront(nil)
                w.makeFirstResponder(tv)
            }
            for down in [true, false] {
                let ev = CGEvent(keyboardEventSource: nil, virtualKey: 49, keyDown: down)
                ev?.flags = .maskAlternate
                ev?.postToPid(pid)
            }
            var waited = 0
            while SpeechPlayer.shared.lastStarted?.lineID != spoken.id, waited < 40 { try? await Task.sleep(for: .milliseconds(100)); waited += 1 }
            print("SNAPSHOT play-keys ⌥Space: " + (SpeechPlayer.shared.lastStarted?.lineID == spoken.id ? "PASS played the selected line" : "FAIL nothing played"))
            SpeechPlayer.shared.stop()
        }
        // Review › Unsure Tones: a number key sets the tone and the next line comes up.
        if let unsure = document.project.lines.first(where: { document.project.toneNeedsYou($0) && !document.project.needsYou($0) }) {
            workspace.reviewFilter = "tones"
            workspace.page = .review
            try? await Task.sleep(for: .seconds(1.2))
            key(23, "5")   // 5: Angry
            try? await Task.sleep(for: .seconds(0.8))
            let after = document.project.lines.first { $0.id == unsure.id }
            print("SNAPSHOT tone-keys 5: " + (after?.tone == .angry && after?.toneSource == .user ? "PASS set Angry" : "FAIL tone \(after?.tone?.rawValue ?? "-")"))
            while undo?.canUndo == true { undo?.undo() }
            workspace.reviewFilter = "guesses"
        }
    }

    static func find<T: NSView>(_ type: T.Type, in view: NSView?) -> T? {
        guard let view else { return nil }
        if let hit = view as? T { return hit }
        for sub in view.subviews { if let hit = find(type, in: sub) { return hit } }
        return nil
    }

    /// Sets a reading preference in the test run’s own store (never the person’s).
    static func reading(_ key: String, _ value: Any?) {
        ReadingStyle.store.set(value, forKey: key)
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
