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
                // A real click on a name in the margin: Assign To for the whole paragraph.
                if wants("assign-paragraph") {
                    workspace.page = .manuscript
                    try? await Task.sleep(for: .seconds(1))
                    func find(_ v: NSView) -> ReadingTextView? { (v as? ReadingTextView) ?? v.subviews.lazy.compactMap(find).first }
                    let byParagraph = Dictionary(grouping: document.project.lines, by: \.paragraphID)
                    if let win = NSApp.windows.first(where: { $0.isVisible && $0.contentView.flatMap(find) != nil }),
                       let tv = win.contentView.flatMap(find), let c = tv.coordinator,
                       let para = document.project.chapters.flatMap(\.paragraphs).first(where: { (byParagraph[$0.id] ?? []).count >= 2 && (byParagraph[$0.id] ?? []).contains(where: document.project.needsYou) })
                                  ?? document.project.chapters.flatMap(\.paragraphs).first(where: { (byParagraph[$0.id] ?? []).count >= 2 }),
                       let first = byParagraph[para.id]?.first, let range = c.layout.paragraphRanges[para.id] {
                        tv.scrollRangeToVisible(range)
                        try? await Task.sleep(for: .seconds(0.5))
                        if let p = c.marginPoint(of: first.id) {
                            let at = tv.convert(p, to: nil)
                            // The text view's mouse loop reads the mouse-up from the queue.
                            if let up = NSEvent.mouseEvent(with: .leftMouseUp, location: at, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                                           windowNumber: win.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 0),
                               let down = NSEvent.mouseEvent(with: .leftMouseDown, location: at, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                                             windowNumber: win.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 1) {
                                NSApp.postEvent(up, atStart: false)
                                win.sendEvent(down)
                            }
                            try? await Task.sleep(for: .seconds(1))
                            print("assign-paragraph: \(workspace.selectedLines.count) of \(byParagraph[para.id]?.count ?? 0) lines selected")
                            if let pop = NSApp.windows.first(where: { $0.isVisible && String(describing: type(of: $0)).contains("Popover") }) {
                                capture(pop, "\(dir)/assign-paragraph-\(mode).png")
                            } else {
                                print("assign-paragraph: no popover")
                            }
                            PopoverPresenter.shared.close()
                            workspace.selectedLines = []
                        }
                        // ⌘-click two names: the picked-lines bar, then Assign… for both paragraphs.
                        if let paraB = document.project.chapters.flatMap(\.paragraphs).first(where: { $0.id != para.id && (byParagraph[$0.id] ?? []).count >= 1 && c.layout.paragraphRanges[$0.id].map { $0.location > range.location } == true }),
                           let firstB = byParagraph[paraB.id]?.first {
                            for id in [first.id, firstB.id] {
                                guard let p = c.marginPoint(of: id) else { continue }
                                let at = tv.convert(p, to: nil)
                                if let up = NSEvent.mouseEvent(with: .leftMouseUp, location: at, modifierFlags: .command, timestamp: ProcessInfo.processInfo.systemUptime,
                                                               windowNumber: win.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 0),
                                   let down = NSEvent.mouseEvent(with: .leftMouseDown, location: at, modifierFlags: .command, timestamp: ProcessInfo.processInfo.systemUptime,
                                                                 windowNumber: win.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 1) {
                                    NSApp.postEvent(up, atStart: false)
                                    win.sendEvent(down)
                                }
                                try? await Task.sleep(for: .seconds(0.4))
                            }
                            print("assign-picked: \(workspace.selectedLines.count) lines picked")
                            try? await Task.sleep(for: .seconds(0.6))
                            capture(win, "\(dir)/assign-picked-\(mode).png")
                            workspace.pendingAssign = first.id
                            try? await Task.sleep(for: .seconds(1))
                            if let pop = NSApp.windows.first(where: { $0.isVisible && String(describing: type(of: $0)).contains("Popover") }) {
                                capture(pop, "\(dir)/assign-picked-popover-\(mode).png")
                            }
                            PopoverPresenter.shared.close()
                            workspace.selectedLines = []
                        }
                    }
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
                if wants("color-picker"), let main = document.project.characters.first {
                    let host = NSHostingController(rootView: ColorPickerGrid(selected: main.colorHex, automatic: main.colorIsAutomatic) { _ in })
                    let w = NSWindow(contentViewController: host)
                    w.isReleasedWhenClosed = false
                    w.styleMask = [.titled]
                    w.makeKeyAndOrderFront(nil)
                    try? await Task.sleep(for: .seconds(1))
                    capture(w, "\(dir)/color-picker-\(mode).png")
                    w.close()
                }
                await shot("analyze-again") { workspace.castPath = []; workspace.page = .cast; workspace.askAnalyzeCastAgain = true }
                workspace.askAnalyzeCastAgain = false
                try? await Task.sleep(for: .seconds(0.5))
                // The background AI bar on another page: voice briefs, then a voice model loading.
                await shot("ai-bar-briefs") {
                    workspace.page = .review
                    document.tagging.debugSummaries("Edda Voss", number: 3, count: 4, character: 0.4, overall: 0.85)
                    document.tagging.debugPhase("Writing voice briefs")
                }
                document.tagging.debugSummaries(nil)
                await shot("ai-bar-loading") { workspace.page = .pronunciations; SpeechModelActivity.begin(.qwen) }
                SpeechModelActivity.end(.qwen)
                await shot("cast-analyzing") {
                    workspace.castPath = []; workspace.page = .cast
                    document.tagging.debugSummaries("Tom Hale", number: 2, count: 4, character: 0.5, overall: 0.375)
                }
                document.tagging.debugSummaries(nil)
                if let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }) {
                    await shot("character-detail", settle: 1.2) { workspace.page = .cast; workspace.castPath = [main.id] }
                    await shot("character-analyzing", settle: 1.2) {
                        workspace.page = .cast; workspace.castPath = [main.id]
                        document.tagging.debugSummaries(main.name, character: 0.75, overall: 0.75)
                    }
                    document.tagging.debugSummaries(nil)
                }
                await shot("index") { workspace.page = .index }
                await shot("pronunciations") { workspace.page = .pronunciations }
                await shot("teleprompter") {
                    workspace.page = .teleprompter
                    workspace.teleprompterParagraph = document.project.chapters.first?.paragraphs.dropFirst(1).first?.id
                }
                await shot("review", settle: 1.0) { workspace.reviewFilter = "guesses"; workspace.page = .review }
                // Context at its least: the paragraph alone, − disabled (test runs keep their own store).
                await shot("review-context", settle: 1.0) {
                    ReadingStyle.store.set(0, forKey: "review.context")
                    workspace.reviewFilter = "all"; workspace.page = .review
                }
                ReadingStyle.store.removeObject(forKey: "review.context")
                await shot("review-tones", settle: 1.0) { workspace.reviewFilter = "tones"; workspace.page = .review }
                document.castVoicesAgain(undoManager: nil)
                if let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }) {
                    await shot("voices", settle: 1.2) { workspace.voiceTarget = .character(main.id); workspace.page = .voices }
                }
                await shot("voices-narrator", settle: 1.0) { workspace.voiceTarget = .narrator; workspace.page = .voices }
                // Casting Style: Narrator + Two Voices (a shared voice, then a character in it)
                // and Full Cast before anyone is marked main.
                if let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }) {
                    document.setCastingStyle(.shared, undoManager: nil)
                    await shot("voices-shared", settle: 1.2) { workspace.voiceTarget = .role(.women); workspace.page = .voices }
                    await shot("voices-shared-character", settle: 1.0) { workspace.voiceTarget = .character(main.id); workspace.page = .voices }
                    await shot("review-genders", settle: 1.0) { workspace.reviewFilter = "genders"; workspace.page = .review }
                    // Several characters picked at once: one panel sets all their genders.
                    let several = Set(document.project.characters.prefix(3).map { VoiceTarget.character($0.id) })
                    await shot("voices-picked", settle: 1.0) { workspace.pickedVoices = several; workspace.page = .voices }
                    workspace.pickedVoices = []
                    document.setCastingStyle(.fullCast, undoManager: nil)
                    await shot("voices-fullcast", settle: 1.0) { workspace.voiceTarget = .character(main.id); workspace.page = .voices }
                    document.setCastingStyle(.oneVoice, undoManager: nil)
                    await shot("voices-onevoice", settle: 1.0) { workspace.voiceTarget = .character(main.id); workspace.page = .voices }
                    document.setCastingStyle(nil, undoManager: nil)
                    // Narration Tools: Three Narrators on Cast, then Just Me's cues on Cast, a
                    // character's page, the margin and the Teleprompter.
                    let before = document.project
                    document.change("Narration", undoManager: nil) { $0.settings.purpose = .narration }
                    document.setCastingStyle(.shared, undoManager: nil)
                    document.setUpThreeNarrators(undoManager: nil)
                    await shot("cast-threenarrators", settle: 1.2) { workspace.castPath = []; workspace.page = .cast }
                    document.setCastingStyle(.oneVoice, undoManager: nil)
                    document.setNarratorGender(.female, undoManager: nil)
                    await shot("cast-justme", settle: 1.0) { workspace.castPath = []; workspace.page = .cast }
                    await shot("cast-picked", settle: 1.0) { workspace.pickedCast = Set(document.project.characters.prefix(2).map(\.id)); workspace.castPath = []; workspace.page = .cast }
                    workspace.pickedCast = []
                    if let tom = document.project.characters.first(where: { document.narrationShades()[$0.id] != nil }) {
                        await shot("character-justme", settle: 1.0) { workspace.castPath = [tom.id]; workspace.page = .cast }
                        await shot("manuscript-justme", settle: 1.2) { workspace.castPath = []; workspace.page = .manuscript }
                        if let line = document.project.lines.first(where: { $0.speakerID == tom.id }) {
                            await shot("teleprompter-justme", settle: 1.0) { workspace.teleprompterParagraph = line.paragraphID; workspace.page = .teleprompter }
                        }
                    }
                    document.change("Restore", undoManager: nil) { $0 = before }
                    workspace.castPath = []
                }
                if let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }) {
                    for engine in [VoiceEngine.orpheus, .qwen, .mixed] {
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
                // ElevenLabs, against the self-test's fake server: the Voices page with the
                // cloud notice, and the setup sheet in a window of its own.
                if wants("elevenlabs") {
                    let config = URLSessionConfiguration.ephemeral
                    config.protocolClasses = [FakeElevenLabs.self]
                    ElevenLabs.shared.session = URLSession(configuration: config)
                    ElevenLabs.shared.save(key: "test-key", consent: true)
                    EngineSwitches.shared.set(.elevenlabs, on: true)
                    _ = try? await ElevenLabs.shared.refreshVoices()
                    document.setCloudVoicesAllowed(true, undoManager: nil)
                    if let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }) {
                        await shot("voices-elevenlabs", settle: 1.5) {
                            document.setVoiceEngine(.elevenlabs, undoManager: nil)
                            workspace.voiceTarget = .character(main.id)
                            workspace.page = .voices
                        }
                        // Design a Voice from Voice Notes, on its own so the whole chooser shows.
                        document.setVoiceEngine(.elevenlabs, undoManager: nil)
                        document.change("Voice Notes", undoManager: nil) { p in
                            guard let i = p.characterIndex(main.id) else { return }
                            p.characters[i].voiceNotes = "Warm but clipped; speeds up when she's worried.\n\nVoice brief:\nPitch: Low-middle, steady\nPace: Quick, clipped\nTexture: Dry, a little husky"
                        }
                        for mode in ["light", "dark"] {
                            NSApp.appearance = NSAppearance(named: mode == "light" ? .aqua : .darkAqua)
                            let host = NSHostingController(rootView: ScrollView { VoiceChooser(document: document, characterID: main.id, compact: true).padding(20) }
                                .frame(width: 560, height: 760))
                            let w = NSWindow(contentViewController: host)
                            w.styleMask = [.titled]
                            w.makeKeyAndOrderFront(nil)
                            try? await Task.sleep(for: .seconds(1.5))
                            capture(w, "\(dir)/voices-elevenlabs-design-\(mode).png")
                            w.close()
                        }
                    }
                    for mode in ["light", "dark"] {
                        NSApp.appearance = NSAppearance(named: mode == "light" ? .aqua : .darkAqua)
                        let host = NSHostingController(rootView: ElevenLabsSetupSheet(isPresented: .constant(true)))
                        let w = NSWindow(contentViewController: host)
                        w.styleMask = [.titled]
                        w.makeKeyAndOrderFront(nil)
                        try? await Task.sleep(for: .seconds(1.5))
                        capture(w, "\(dir)/elevenlabs-setup-\(mode).png")
                        w.close()
                    }
                    // Clone a Voice: filled in, and the voice made.
                    let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) })
                    let target: VoiceTarget = main.map { .character($0.id) } ?? .narrator
                    let tmp = FileManager.default.temporaryDirectory
                    let samples = [CloneSample(url: tmp.appending(path: "Ellen reading, take 2.m4a"), seconds: 64, recorded: false),
                                   CloneSample(url: tmp.appending(path: "rec.m4a"), seconds: 31, recorded: true)]
                    let allowance = ElevenLabs.CloneAllowance(canClone: true, slotsUsed: 3, slotLimit: 10, tier: "creator")
                    let made = VoiceInfo(engine: .elevenlabs, voiceID: "clone1", name: "Ellen, warm and low", gender: .neutral, age: .adult, accent: "American", quality: 3, detail: "Cloned")
                    let madeHere = VoiceInfo(engine: .qwen, voiceID: LocalClones.prefix + "x", name: "Ellen, warm and low", gender: .female, accent: "", quality: 3, detail: "Cloned on this Mac")
                    let words = "The train was late again, so I walked. It was the kind of autumn morning that makes you forgive the whole year."
                    let shots: [(String, VoiceInfo?, CloneVoiceSheet.Place)] = [("clone-voice", nil, .elevenlabs), ("clone-voice-done", made, .elevenlabs),
                                                                                ("clone-voice-mac", nil, .mac), ("clone-voice-mac-done", madeHere, .mac)]
                    for (name, result, place) in shots {
                        for mode in ["light", "dark"] {
                            NSApp.appearance = NSAppearance(named: mode == "light" ? .aqua : .darkAqua)
                            let host = NSHostingController(rootView: CloneVoiceSheet(document: document, isPresented: .constant(true), initialTarget: target,
                                                                                     presetSamples: samples, presetAllowance: allowance, presetResult: result,
                                                                                     presetPlace: place, presetClip: (12.4, words)))
                            let w = NSWindow(contentViewController: host)
                            w.styleMask = [.titled]
                            w.makeKeyAndOrderFront(nil)
                            try? await Task.sleep(for: .seconds(1.2))
                            capture(w, "\(dir)/\(name)-\(mode).png")
                            w.close()
                        }
                    }
                    for mode in ["light", "dark"] {
                        NSApp.appearance = NSAppearance(named: mode == "light" ? .aqua : .darkAqua)
                        let host = NSHostingController(rootView: CloneVoiceSheet(document: document, isPresented: .constant(true), initialTarget: target,
                                                                                 presetSamples: [], presetAllowance: ElevenLabs.CloneAllowance(canClone: false, slotsUsed: 0, slotLimit: 3, tier: "free"),
                                                                                 presetPlace: .elevenlabs))
                        let w = NSWindow(contentViewController: host)
                        w.styleMask = [.titled]
                        w.makeKeyAndOrderFront(nil)
                        try? await Task.sleep(for: .seconds(1.2))
                        capture(w, "\(dir)/clone-voice-free-\(mode).png")
                        w.close()
                    }
                    SettingsWindow.shared.show(tab: .voices)
                    try? await Task.sleep(for: .seconds(0.9))
                    if let s = SettingsWindow.shared.window { capture(s, "\(dir)/settings-voices-elevenlabs-dark.png") }
                    SettingsWindow.shared.show(tab: .general)
                    SettingsWindow.shared.window?.close()
                    document.setVoiceEngine(.apple, undoManager: nil)
                    document.setCloudVoicesAllowed(false, undoManager: nil)
                    ElevenLabs.shared.disconnect()
                }
                // The status bar: a line whose engine isn't downloaded (APP_VOICE_MODELS empty).
                if !ModelSpeech.isInstalled(.orpheus), let main = document.project.characters.first {
                    await shot("speech-error", settle: 1.5) {
                        workspace.page = .manuscript
                        PlayCommand.audition(VoiceChoice(engine: .orpheus, voiceID: "tara"), character: main, document: document)
                    }
                    SpeechPlayer.shared.failure = nil
                }
                await shot("review-voices", settle: 1.0) { workspace.reviewFilter = "voices"; workspace.page = .review }
                await shot("audio-export-sheet") { workspace.page = .manuscript; workspace.showAudioExport = true }
                workspace.showAudioExport = false
                try? await Task.sleep(for: .seconds(0.5))
                // Some chapters left out: Select All shows mixed, the count says how many.
                await shot("audio-export-picked") {
                    workspace.audioExportChapters = Set(document.project.chapters.indices.filter { $0 != 1 })
                    workspace.showAudioExport = true
                }
                workspace.showAudioExport = false
                workspace.audioExportChapters = nil
                try? await Task.sleep(for: .seconds(0.5))
                // Project types: the question, the AI voices setup, Project Settings, and the
                // sidebar for each type.
                await shot("project-type") { workspace.showProjectType = true }
                workspace.showProjectType = false
                try? await Task.sleep(for: .seconds(0.5))
                if wants("project-type-ai") {
                    let host = NSHostingController(rootView: ProjectTypeSheet(document: document, workspace: workspace, step: 2))
                    let w = NSWindow(contentViewController: host)
                    w.styleMask = [.titled]
                    w.makeKeyAndOrderFront(nil)
                    try? await Task.sleep(for: .seconds(1.2))
                    capture(w, "\(dir)/project-type-ai-\(mode).png")
                    w.close()
                }
                // Casting Style, the second question: AI voices and Narration Tools wording.
                for (name, purpose) in [("project-type-style", ProjectPurpose.aiVoices), ("project-type-style-narration", .narration)] where wants(name) {
                    let host = NSHostingController(rootView: ProjectTypeSheet(document: document, workspace: workspace, choice: purpose, step: 1))
                    let w = NSWindow(contentViewController: host)
                    w.styleMask = [.titled]
                    w.makeKeyAndOrderFront(nil)
                    try? await Task.sleep(for: .seconds(1.2))
                    capture(w, "\(dir)/\(name)-\(mode).png")
                    w.close()
                }
                // Clicks on a style card's circle choose it (it once sat outside the card's button).
                if wants("project-type-style-click") {
                    let host = NSHostingController(rootView: ProjectTypeSheet(document: document, workspace: workspace, choice: .aiVoices, step: 1))
                    let w = NSWindow(contentViewController: host)
                    w.styleMask = [.titled]
                    w.makeKeyAndOrderFront(nil)
                    try? await Task.sleep(for: .seconds(1.2))
                    @MainActor func click(_ x: CGFloat, _ yFromTop: CGFloat) async {
                        let p = NSPoint(x: x, y: w.frame.height - yFromTop)
                        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
                            if let e = NSEvent.mouseEvent(with: type, location: p, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                                          windowNumber: w.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 1) { w.sendEvent(e) }
                            try? await Task.sleep(for: .seconds(0.05))
                        }
                        try? await Task.sleep(for: .seconds(0.6))
                    }
                    await click(520, 250)   // Narrator + Two Voices: the circle
                    capture(w, "\(dir)/project-type-style-click-\(mode).png")
                    await click(500, 168)   // One Voice: Hear It plays, and Narrator + Two Voices stays chosen
                    capture(w, "\(dir)/project-type-style-click2-\(mode).png")
                    await click(500, 168)   // Stop
                    await click(24, 400)    // Full Cast: the card's padding, left of the icon
                    capture(w, "\(dir)/project-type-style-click3-\(mode).png")
                    w.close()
                }
                await shot("project-settings") { workspace.showProjectSettings = true }
                workspace.showProjectSettings = false
                try? await Task.sleep(for: .seconds(0.5))
                // Use Voices from Another Project, with a made-up earlier book in the series.
                await shot("series-voices", settle: 1.2) {
                    var earlier = document.project
                    earlier.title = "Saltwater Hymns"
                    earlier.importedAt = .distantPast
                    let designs = ["Mara": "A woman in her thirties with a warm, clipped voice.", "Tom": "An older man with a low, gravelly voice speaking English with a Scottish accent.",
                                   "Edda": "A woman with a precise, clipped voice speaking English with a German accent.", "Pip": "A young boy with a bright, breathless voice."]
                    for i in earlier.characters.indices {
                        let d = designs[earlier.characters[i].firstName] ?? "An adult with a clear, natural voice."
                        earlier.characters[i].setVoice(CastVoice(choice: VoiceChoice(engine: .qwen, voiceID: "design", design: d), confidence: 1, source: .user), for: .qwen)
                    }
                    // In this book Tom is just "Tom", and Pip isn't in the earlier one.
                    if let t = earlier.characters.firstIndex(where: { $0.name.hasPrefix("Tom") }) { earlier.characters[t].name = "Tom"; earlier.characters[t].aliases = [] }
                    earlier.characters.removeAll { $0.name.hasPrefix("Pip") }
                    earlier.characters.append(BookCharacter(name: "Old Quinn", colorHex: "#7A8B99"))
                    earlier.characters[earlier.characters.count - 1].setVoice(CastVoice(choice: VoiceInfo.qwenPresets[3].choice, confidence: 1, source: .user), for: .qwen)
                    earlier.settings.voiceEngine = .qwen
                    earlier.settings.setNarratorVoice(CastVoice(choice: VoiceInfo.qwenPresets[0].choice, confidence: 1, source: .user), for: .qwen)
                    workspace.seriesSource = earlier
                    workspace.showSeriesVoices = true
                }
                workspace.showSeriesVoices = false
                workspace.showProjectSettings = false
                try? await Task.sleep(for: .seconds(0.5))
                await shot("book-stats") { workspace.showBookStats = true }
                workspace.showBookStats = false
                try? await Task.sleep(for: .seconds(0.5))
                await shot("claude-review") {
                    let p = document.project
                    let picked = p.lines.filter { $0.speakerID != nil }.prefix(8)
                    var before: [UUID: Line] = [:]
                    let items: [AIReviewItem] = picked.enumerated().map { n, line in
                        var old = line
                        old.speakerID = n % 3 == 0 ? nil : p.characters.first { $0.id != line.speakerID }?.id
                        if n % 4 == 1 { old.tone = .neutral }
                        before[line.id] = old
                        return AIReviewItem(lineID: line.id, speakerBefore: old.speakerID, speakerAfter: line.speakerID,
                                            toneBefore: old.tone, toneAfter: n % 4 == 1 ? .whispered : line.tone)
                    }
                    document.tagging.review = .init(items: items, before: before, addedCharacters: [], model: "Claude Sonnet 5.5")
                }
                document.tagging.review = nil
                try? await Task.sleep(for: .seconds(0.5))
                for purpose in ProjectPurpose.allCases {
                    await shot("type-\(purpose.rawValue)") {
                        document.updateSettings("", undoManager: nil) { $0.purpose = purpose }
                        workspace.page = .cast
                    }
                }
                document.updateSettings("", undoManager: nil) { $0.purpose = nil }
                await shot("revisions") { workspace.page = .revisions }
                await shot("export-sheet") { workspace.page = .manuscript; workspace.showExport = true }
                workspace.showExport = false
                try? await Task.sleep(for: .seconds(0.5))
                await shot("model-sheet") { workspace.showModelSheet = true }
                workspace.showModelSheet = false
                try? await Task.sleep(for: .seconds(0.5))
            }
            if wants("settings") {
                ActivityStore.shared.seedForSnapshot()
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
            if wants("notes-typing") { await notesTypingTest(document: document, workspace: workspace) }
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

    /// Typing in a character's Voice Notes: real key events go into a draft (the book isn't
    /// changed on every keystroke), which is saved when typing pauses, as one undo step.
    @MainActor
    static func notesTypingTest(document: InnerVoiceDocument, workspace: Workspace) async {
        guard let main = document.project.characters.max(by: { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }) else { return }
        workspace.page = .cast
        workspace.castPath = [main.id]
        try? await Task.sleep(for: .seconds(1.2))
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.canBecomeMain && !$0.isSheet }) else { return }
        window.makeKeyAndOrderFront(nil)
        func fields(in v: NSView?) -> [NSTextField] {
            guard let v else { return [] }
            return ((v as? NSTextField).map { [$0] } ?? []) + v.subviews.flatMap { fields(in: $0) }
        }
        guard let field = fields(in: window.contentView).first(where: { $0.placeholderString?.hasPrefix("How you'll voice") == true }) else {
            print("SNAPSHOT notes-typing: FAIL no Voice Notes field"); return
        }
        window.makeFirstResponder(field)
        let before = document.project.character(main.id)?.voiceNotes ?? ""
        let changesBefore = document.tagRevision
        let typed = " Low and slow."
        let start = Date()
        for ch in typed {
            for type in [NSEvent.EventType.keyDown, .keyUp] {
                if let ev = NSEvent.keyEvent(with: type, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                             windowNumber: window.windowNumber, context: nil, characters: String(ch),
                                             charactersIgnoringModifiers: String(ch), isARepeat: false, keyCode: ch == " " ? 49 : 0) {
                    window.sendEvent(ev)
                }
            }
            try? await Task.sleep(for: .milliseconds(20))
        }
        let perKey = (Date().timeIntervalSince(start) / Double(typed.count) - 0.02) * 1000
        let changedWhileTyping = document.tagRevision != changesBefore
        try? await Task.sleep(for: .seconds(1.2))
        let after = document.project.character(main.id)?.voiceNotes ?? ""
        print("SNAPSHOT notes-typing: " + String(format: "%.1f ms a key; ", perKey)
              + (changedWhileTyping ? "FAIL the book changed while typing" : "PASS the book waited while typing") + "; "
              + (after == before + typed ? "PASS saved after the pause" : "FAIL saved “\(after.suffix(30))”"))
        window.makeFirstResponder(nil)
        window.undoManager?.undo()
        let undone = document.project.character(main.id)?.voiceNotes ?? ""
        print("SNAPSHOT notes-typing undo: " + (undone == before ? "PASS one step" : "FAIL left “\(undone.suffix(30))”"))
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
