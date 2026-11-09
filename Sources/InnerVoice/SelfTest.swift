import AppKit
import InnerVoiceKit

/// Self-test for agents: runs the app's real functions on the sample book (never a real
/// project), prints PASS or FAIL per check, and quits with exit code 0 or 1.
///   "<app>/Contents/MacOS/InnerVoice" --selftest           (tools/selftest.sh)
///   APP_SELFTEST_MODEL=<model.gguf> … --selftest          also tags the sample with a real model and scores it
enum SelfTest {
    static var requested: Bool { CommandLine.arguments.contains("--selftest") }
    static var exitCode: Int32 = 0
    private static var failures = 0
    private static var count = 0

    static func runIfRequested() {
        guard requested else { return }
        setvbuf(stdout, nil, _IOLBF, 0)
        Task {
            try? await Task.sleep(for: .seconds(0.3))
            await run()
            print(failures == 0 ? "SELFTEST PASS \(count) checks" : "SELFTEST FAIL \(failures) of \(count) checks")
            exitCode = failures == 0 ? 0 : 1
            for doc in NSDocumentController.shared.documents { doc.updateChangeCount(.changeCleared); doc.close() }
            NSApp.terminate(nil)
        }
    }

    private static func check(_ name: String, _ ok: Bool, _ detail: String = "") {
        count += 1
        if !ok { failures += 1 }
        print(ok ? "PASS \(name)" : "FAIL \(name)\(detail.isEmpty ? "" : ": \(detail)")")
    }

    /// Tones, casting, Apple speech, the table read and audio export, on the sample.
    private static func voices(_ doc: InnerVoiceDocument, undo: UndoManager) async {
        let p = doc.project
        let toned = p.lines.filter { $0.tone != nil }.count
        check("every line has a tone after import", toned == p.lines.count, "\(toned) of \(p.lines.count)")
        check("the sample has some lines that aren't neutral", p.lines.contains { ($0.tone ?? .neutral) != .neutral })

        let apple = VoiceCatalog.appleVoices()
        check("Apple voices are found", !apple.isEmpty, "\(apple.count)")
        doc.castVoicesAgain(undoManager: undo)
        let speaking = doc.project.characters.filter { (doc.index.stats[$0.id]?.lines ?? 0) > 0 }
        check("casting gives every speaking character a voice", speaking.allSatisfy { $0.voice(for: .apple) != nil })
        check("casting gives the narrator a voice", doc.project.settings.narratorVoice(for: .apple) != nil)
        undo.undo()
        check("Undo Cast Voices takes the voices back", doc.project.characters.allSatisfy { $0.voice(for: .apple) == nil })
        undo.redo()

        if let mara = doc.project.characters.first(where: { $0.name == "Mara Quinn" }), let v = apple.first(where: { $0.gender == .female }) {
            doc.setVoice(VoiceChoice(engine: .apple, voiceID: v.voiceID), for: mara.id, undoManager: undo)
            doc.castVoicesAgain(undoManager: undo)
            check("casting again keeps a voice you chose", doc.project.character(mara.id)?.voice(for: .apple)?.choice.voiceID == v.voiceID)
        }

        if let l = doc.project.lines.first {
            doc.setTone(.angry, for: [l.id], undoManager: undo)
            var copy = doc.project
            ToneRules.apply(to: &copy)
            check("the rules never change a tone you set", copy.lines.first?.tone == .angry && copy.lines.first?.toneSource == .user)
            undo.undo()
        }

        // The narrator: Same as Narrator, written text, first-person narration.
        do {
            let r0 = VoiceResolver(project: doc.project)
            let segs = SpeechScript.segments(chapter: 0, chapters: doc.project.chapters.count, project: doc.project, index: doc.index, includeNarration: true)
            check("the narrator always has a voice", r0.narrator != nil)
            if let written = segs.first(where: { $0.kind == .written && $0.lineID != nil }) {
                check("written text is read by the narrator by default", r0.choice(for: written) == r0.narrator)
            }
            if let pip = doc.project.characters.first(where: { $0.name.hasPrefix("Pip") }), let line = segs.first(where: { $0.speakerID == pip.id }) {
                doc.setVoice(VoiceChoice(engine: .apple, voiceID: VoiceChoice.narratorID), for: pip.id, undoManager: undo)
                let r = VoiceResolver(project: doc.project)
                check("Same as Narrator reads a character's lines in the narrator's voice", r.choice(for: line) == r.narrator && r.narrator?.isNarrator == false)
                undo.undo()
            }
            if let mara = doc.project.characters.first(where: { $0.name.hasPrefix("Mara") }), let maraVoice = mara.voice(for: .apple)?.choice {
                doc.change("POV", undoManager: undo) { $0.settings.povCharacterID = mara.id }
                check("a first-person book's narration uses the POV character's voice", VoiceResolver(project: doc.project).narrator == maraVoice)
                if let other = VoiceCatalog.appleVoices().first(where: { $0.voiceID != maraVoice.voiceID }) {
                    doc.setVoice(other.choice, for: nil, undoManager: undo)
                    check("a narrator voice you choose wins over the POV character's", VoiceResolver(project: doc.project).narrator?.voiceID == other.voiceID)
                    undo.undo()
                }
                undo.undo()
            }
        }

        // Engine switches (Settings › Voices); test runs never save them.
        let switches = EngineSwitches.shared
        doc.setVoiceEngine(.orpheus, undoManager: undo)
        switches.set(.orpheus, on: false)
        check("a turned-off engine isn't used: the project falls back to one that's on",
              doc.project.engineInUse == .apple && doc.project.settings.engine == .orpheus && VoiceResolver(project: doc.project).engine == .apple)
        switches.set(.orpheus, on: true)
        check("turning it back on brings the project's own engine back", doc.project.engineInUse == .orpheus)
        switches.set(.apple, on: false)
        switches.set(.qwen, on: false)
        switches.set(.orpheus, on: false)
        check("the last engine that's on stays on", switches.isOn(.orpheus) && !switches.canTurnOff(.orpheus) && switches.enabled == [.orpheus])
        for e in VoiceEngine.engines { switches.set(e, on: true) }
        undo.undo()
        check("every engine is on again after the test", switches.enabled.count == VoiceEngine.engines.filter { !$0.isCloud }.count && doc.project.engineInUse == .apple)
        check("Mix Engines is offered when two or more engines are on", switches.choices.contains(.mixed))

        // Real speech: one line in its voice, cached the second time.
        let resolver = VoiceResolver(project: doc.project)
        let segs = SpeechScript.segments(chapter: 0, project: doc.project, index: doc.index, includeNarration: true)
        check("the table read has narration and lines", segs.contains { $0.lineID == nil } && segs.contains { $0.lineID != nil })
        if let seg = segs.first(where: { $0.lineID != nil }), let request = resolver.request(for: seg) {
            do {
                SpeechRenderer.shared.clearCache()
                let start = Date()
                let audio = try await SpeechRenderer.shared.render(request)
                let seconds = Double(audio.frameLength) / audio.format.sampleRate
                check("an Apple voice speaks a line", seconds > 0.3, String(format: "%.2f s of audio", seconds))
                check("the line is cached", SpeechRenderer.shared.isCached(request))
                print("SPEECH first line in \(Int(Date().timeIntervalSince(start) * 1000)) ms, \(String(format: "%.1f", seconds)) s")
            } catch {
                check("an Apple voice speaks a line", false, error.localizedDescription)
            }
        }
        // The player goes through segments in order and stops at the end (silently in tests).
        var heard: [String] = []
        let two = Array(segs.filter { $0.lineID != nil }.prefix(2))
        SpeechPlayer.shared.play(two, resolver: resolver) { heard.append($0.id) }
        let until = Date().addingTimeInterval(20)
        while SpeechPlayer.shared.isPlaying, Date() < until { try? await Task.sleep(for: .milliseconds(100)) }
        check("Play plays each line in order, then stops", heard == two.map(\.id) && !SpeechPlayer.shared.isPlaying,
              "\(heard.count) of \(two.count)")
        check("playing leaves no error", SpeechPlayer.shared.failure == nil, SpeechPlayer.shared.failure ?? "")

        let dir = FileManager.default.temporaryDirectory.appending(path: "iv-audio-selftest")
        try? FileManager.default.removeItem(at: dir)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let few = Array(segs.prefix(6))
        do {
            let urls = try await AudioExporter.write([AudioExporter.Part(name: "Sample", segments: few)], to: dir, format: .m4a, resolver: resolver) { _, _, _ in }
            let size = urls.first.flatMap { try? $0.resourceValues(forKeys: [.fileSizeKey]).fileSize } ?? 0
            check("Export Audio writes an M4A file", size > 2000, "\(size) bytes")
        } catch {
            check("Export Audio writes an M4A file", false, error.localizedDescription)
        }
        try? FileManager.default.removeItem(at: dir)
        SpeechRenderer.shared.clearCache()
    }

    /// Qwen3-TTS and Orpheus speak real lines (APP_SELFTEST_VOICES=1, with the models in
    /// APP_VOICE_MODELS). APP_SELFTEST_AUDIO_OUT keeps the audio for listening.
    private static func modelVoices(_ doc: InnerVoiceDocument, undo: UndoManager) async {
        let out = ProcessInfo.processInfo.environment["APP_SELFTEST_AUDIO_OUT"].map { URL(fileURLWithPath: $0) }
        if let out { try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true) }
        SpeechRenderer.shared.clearCache()
        for engine in [VoiceEngine.orpheus, .qwen] {
            guard ModelSpeech.isInstalled(engine) else {
                check("\(engine.title) is downloaded", false, VoicePack.folder.path)
                continue
            }
            doc.setVoiceEngine(engine, undoManager: undo)
            let speaking = doc.project.characters.filter { (doc.index.stats[$0.id]?.lines ?? 0) > 0 }
            check("\(engine.title): casting gives every speaking character a voice", speaking.allSatisfy { $0.voice(for: engine) != nil })
            let resolver = VoiceResolver(project: doc.project)
            let segs = SpeechScript.segments(chapter: 0, project: doc.project, index: doc.index, includeNarration: true)
            // A narration piece and the first two lines of the most talkative character.
            let top = speaking.max { (doc.index.stats[$0.id]?.lines ?? 0) < (doc.index.stats[$1.id]?.lines ?? 0) }
            let picks = Array(segs.filter { $0.lineID == nil && $0.text.count > 20 }.prefix(1)) + Array(segs.filter { $0.speakerID == top?.id }.prefix(2))
            for (n, seg) in picks.enumerated() {
                guard let request = resolver.request(for: seg) else { continue }
                do {
                    let start = Date()
                    let audio = try await SpeechRenderer.shared.render(request)
                    let seconds = Double(audio.frameLength) / audio.format.sampleRate
                    let took = Date().timeIntervalSince(start)
                    let what = seg.lineID == nil ? "narration" : "a line (\(seg.tone.rawValue))"
                    check("\(engine.title) speaks \(what)", seconds > 0.5 && seconds < 60, String(format: "%.1f s of audio in %.1f s", seconds, took))
                    print("VOICE \(engine.rawValue) #\(n) \(seg.sound.map { "<\($0.rawValue)> " } ?? "")\(seg.text.prefix(60)) — \(String(format: "%.1f s audio, %.1f s", seconds, took))")
                    if let out { SpeechRenderer.write(audio, to: out.appending(path: "\(engine.rawValue)-\(n).caf")) }
                } catch {
                    check("\(engine.title) speaks a line", false, error.localizedDescription)
                }
            }
            // A long narration paragraph is read to the end, not cut off after a dozen seconds.
            let long = Array(repeating: "The lamp turned slowly above the rocks, and the keeper counted every pass of the beam until morning came.", count: 4).joined(separator: " ")
            if let narrator = resolver.narrator {
                do {
                    let audio = try await SpeechRenderer.shared.render(SpeechRequest(text: long, choice: narrator, tone: .neutral, kind: nil))
                    let seconds = Double(audio.frameLength) / audio.format.sampleRate
                    let words = Double(long.split(separator: " ").count)
                    check("\(engine.title) reads a long paragraph to the end", words / seconds > 1.5 && words / seconds < 4.5,
                          String(format: "%.0f words in %.1f s", words, seconds))
                    if let out { SpeechRenderer.write(audio, to: out.appending(path: "\(engine.rawValue)-long.caf")) }
                } catch {
                    check("\(engine.title) reads a long paragraph to the end", false, error.localizedDescription)
                }
            }
            if let seg = picks.last, let request = resolver.request(for: seg) {
                let start = Date()
                _ = try? await SpeechRenderer.shared.render(request)
                check("\(engine.title): a line plays at once the second time", Date().timeIntervalSince(start) < 0.5)
            }
        }
        // Mix Engines: each character can have a voice from any engine; two engines take turns.
        if ModelSpeech.isInstalled(.qwen), ModelSpeech.isInstalled(.orpheus) {
            doc.setVoiceEngine(.mixed, undoManager: undo)
            let speaking = doc.project.characters.filter { (doc.index.stats[$0.id]?.lines ?? 0) > 0 }
            let engines = Set(speaking.compactMap { $0.voice(for: .mixed)?.choice.engine })
            check("Mix Engines: every speaking character gets a voice from a real engine",
                  speaking.allSatisfy { $0.voice(for: .mixed) != nil } && !engines.contains(.mixed), engines.map(\.rawValue).sorted().joined(separator: ", "))
            let mara = speaking.first { $0.name.hasPrefix("Mara") }, tom = speaking.first { $0.name.hasPrefix("Tom") }
            if let mara, let tom, let orph = VoiceInfo.orpheus.first(where: { $0.gender == .female }), let preset = VoiceInfo.qwenPresets.first(where: { $0.voiceID == "preset:elder" }) {
                doc.setVoice(orph.choice, for: mara.id, undoManager: undo)
                doc.setVoice(preset.choice, for: tom.id, undoManager: undo)
                check("Mix Engines: a chosen voice is kept with the mixed casting",
                      doc.project.character(mara.id)?.voice(for: .mixed)?.choice.engine == .orpheus && doc.project.character(tom.id)?.voice(for: .mixed)?.choice.engine == .qwen)
                SpeechRenderer.shared.clearCache()
                let segs = SpeechScript.segments(chapter: 0, project: doc.project, index: doc.index, includeNarration: false)
                let turns = Array(segs.filter { $0.speakerID == mara.id || $0.speakerID == tom.id }.prefix(4))
                let player = SpeechPlayer.shared
                var heard: [UUID?] = []
                let start = Date()
                player.play(turns, resolver: VoiceResolver(project: doc.project)) { heard.append($0.speakerID) }
                while player.isPlaying, Date().timeIntervalSince(start) < 180 { try? await Task.sleep(for: .milliseconds(100)) }
                let took = Date().timeIntervalSince(start)
                print(String(format: "VOICE mixed table read: %d lines (Orpheus and Qwen3-TTS taking turns) in %.1f s", heard.count, took))
                check("Mix Engines: Orpheus and Qwen3-TTS take turns in one table read",
                      heard.count == turns.count && player.failure == nil && Set(heard.compactMap { $0 }).count == 2, player.failure ?? "\(heard.count) of \(turns.count)")
            }
        }

        // Play from a cold start, as a person would: models unloaded, nothing cached.
        for engine in [VoiceEngine.qwen, .orpheus] where ModelSpeech.isInstalled(engine) {
            doc.setVoiceEngine(engine, undoManager: undo)
            await ModelSpeech.unload()
            QwenSamples.forget()
            SpeechRenderer.shared.clearCache()
            guard let c = doc.project.characters.first(where: { $0.voice(for: engine) != nil && (doc.index.stats[$0.id]?.lines ?? 0) > 0 }),
                  let v = c.voice(for: engine) else { continue }
            let player = SpeechPlayer.shared
            let start = Date()
            PlayCommand.audition(v.choice, character: c, document: doc)
            var started: Double?
            while Date().timeIntervalSince(start) < 120 {
                if started == nil, player.lastStarted?.id == "audition", !player.preparing { started = Date().timeIntervalSince(start) }
                if !player.isPlaying { break }
                try? await Task.sleep(for: .milliseconds(100))
            }
            let total = Date().timeIntervalSince(start)
            print(String(format: "VOICE cold start %@: first sound after %.1f s, done after %.1f s", engine.rawValue, started ?? -1, total))
            check("\(engine.title): Play works from a cold start", started != nil && player.failure == nil && !player.isPlaying,
                  String(format: "started after %.1f s, done after %.1f s%@", started ?? -1, total, player.failure.map { ", " + $0 } ?? ""))
        }
        doc.setVoiceEngine(.apple, undoManager: undo)
        await ModelSpeech.unload()
        SpeechRenderer.shared.clearCache()
    }

    /// The voice pack download, with the small audio codec only (APP_SELFTEST_DOWNLOAD=1
    /// and APP_VOICE_MODELS set to an empty folder).
    private static func voiceDownload() async {
        let repo = "mlx-community/snac_24khz"
        let pack = VoicePack(engine: .orpheus, repos: [repo], bytes: 80_000_000, summary: "test")
        let manager = VoiceModelManager.shared
        check("the codec isn't there before the download", !VoicePack.isDownloaded(repo), VoicePack.folder.path)
        var progress: [Double] = []
        let watch = Task { @MainActor in
            while !Task.isCancelled {
                if case .downloading(let p)? = manager.states[.orpheus] { progress.append(p) }
                try? await Task.sleep(for: .milliseconds(50))
            }
        }
        let start = Date()
        manager.download(pack)
        await manager.waitForDownload(.orpheus)
        watch.cancel()
        let failed: String? = if case .failed(let m)? = manager.states[.orpheus] { m } else { nil }
        check("a voice pack downloads", VoicePack.isDownloaded(repo) && failed == nil,
              failed ?? String(format: "%.1f s", Date().timeIntervalSince(start)))
        check("the download shows progress", progress.count >= 2 && progress.last ?? 0 > 0.5, "\(progress.count) updates, last \(progress.last ?? 0)")
        check("a finished download leaves no state", manager.states[.orpheus] == nil)
        manager.remove(pack)
        let left = (try? FileManager.default.contentsOfDirectory(atPath: VoicePack.folder.path))?.filter { !$0.hasPrefix(".") && $0 != "mlx-audio" } ?? []
        check("Remove takes the pack and its download cache away", !VoicePack.isDownloaded(repo) && left.isEmpty, left.joined(separator: ", "))
    }

    /// ElevenLabs against a fake server inside the app: setup, voices, the book's permission
    /// (nothing is sent without it), audio tags, errors.
    private static func elevenLabs(_ doc: InnerVoiceDocument, undo: UndoManager) async {
        let eleven = ElevenLabs.shared
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [FakeElevenLabs.self]
        eleven.session = URLSession(configuration: config)
        FakeElevenLabs.reset()
        check("ElevenLabs is off until it's set up", !eleven.isSetUp && !EngineSwitches.shared.isOn(.elevenlabs) && !EngineSwitches.shared.choices.contains(.elevenlabs))
        let account = try? await eleven.check(key: "test-key")
        check("Check Key reads the plan, and sends no book text", account?.limit == 100_000 && FakeElevenLabs.speechRequests.isEmpty)
        eleven.save(key: "test-key", consent: true)
        EngineSwitches.shared.set(.elevenlabs, on: true)
        let voices = (try? await eleven.refreshVoices()) ?? []
        check("ElevenLabs voices are listed with their labels", voices.count == 2 && voices.contains { $0.gender == .female && $0.accent == "British" })
        check("ElevenLabs is offered once it's set up", EngineSwitches.shared.choices.contains(.elevenlabs))

        // Set up, but this book hasn't allowed it: nothing may be sent.
        doc.setVoiceEngine(.elevenlabs, undoManager: undo)
        check("a book that hasn't allowed ElevenLabs uses an engine on this Mac", doc.project.engineInUse != .elevenlabs)
        let segs = SpeechScript.segments(chapter: 0, project: doc.project, index: doc.index, includeNarration: true)
        let lines = Array(segs.filter { $0.lineID != nil }.prefix(2))
        let r0 = VoiceResolver(project: doc.project)
        for seg in lines { _ = try? await SpeechRenderer.shared.render(r0.request(for: seg)!) }
        if let v = voices.first {
            _ = try? await SpeechRenderer.shared.render(SpeechRequest(text: "Test.", choice: v.choice, tone: .neutral, kind: .spoken, cloudAllowed: false))
        }
        check("without the book's permission, no text is sent to ElevenLabs", FakeElevenLabs.speechRequests.isEmpty, "\(FakeElevenLabs.speechRequests.count) requests")

        // The book allows it.
        doc.setCloudVoicesAllowed(true, undoManager: undo)
        doc.setVoiceEngine(.elevenlabs, undoManager: undo)
        check("once allowed, the book uses ElevenLabs", doc.project.engineInUse == .elevenlabs)
        let speaking = doc.project.characters.filter { (doc.index.stats[$0.id]?.lines ?? 0) > 0 }
        check("casting gives every character an ElevenLabs voice", speaking.allSatisfy { $0.voice(for: .elevenlabs)?.choice.engine == .elevenlabs })
        SpeechRenderer.shared.clearCache()
        let r = VoiceResolver(project: doc.project)
        if let seg = segs.first(where: { $0.lineID != nil && $0.tone != .neutral }) ?? lines.first, let req = r.request(for: seg) {
            do {
                let audio = try await SpeechRenderer.shared.render(req)
                let body = FakeElevenLabs.speechRequests.last ?? [:]
                let text = body["text"] as? String ?? ""
                check("a line is sent to eleven_v4 with its tone as an audio tag", body["model_id"] as? String == "eleven_v4"
                      && (seg.tone == .neutral || text.hasPrefix(seg.tone.audioTag ?? "")), text.prefix(30).description)
                check("ElevenLabs audio comes back and plays", Double(audio.frameLength) / audio.format.sampleRate > 0.4)
            } catch {
                check("a line is sent to eleven_v4", false, error.localizedDescription)
            }
        }
        // Errors say what to do.
        FakeElevenLabs.status = 401
        SpeechRenderer.shared.clearCache()
        if let seg = lines.last, let req = r.request(for: seg) {
            do { _ = try await SpeechRenderer.shared.render(req); check("a wrong key says so", false) } catch {
                check("a wrong key says so", error.localizedDescription.contains("API key"), error.localizedDescription)
            }
        }
        FakeElevenLabs.status = 200
        // Stop sending this book.
        doc.setCloudVoicesAllowed(false, undoManager: undo)
        let before = FakeElevenLabs.speechRequests.count
        SpeechRenderer.shared.clearCache()
        let r2 = VoiceResolver(project: doc.project)
        for seg in lines { _ = try? await SpeechRenderer.shared.render(r2.request(for: seg)!) }
        check("after Stop Sending This Book, nothing more is sent", FakeElevenLabs.speechRequests.count == before && doc.project.engineInUse != .elevenlabs)
        eleven.disconnect()
        check("Disconnect turns ElevenLabs off", !eleven.isSetUp && !EngineSwitches.shared.choices.contains(.elevenlabs))
        doc.setVoiceEngine(.apple, undoManager: undo)
        eleven.session = .shared
        SpeechRenderer.shared.clearCache()
    }

    private static func run() async {
        let project = SeedData.project()
        let doc = InnerVoiceDocument(project: project)
        let undo = UndoManager()
        let index = doc.index
        func line(_ prefix: String) -> Line? { doc.project.lines.first { doc.project.text(of: $0, index: doc.index).hasPrefix(prefix) } }
        func name(_ l: Line?) -> String { doc.project.character(l?.speakerID)?.name ?? "nobody" }

        check("sample book has 3 chapters", project.chapters.count == 3, "\(project.chapters.map(\.title))")
        check("dialogue finder found the lines", project.lines.count >= 30, "\(project.lines.count)")
        check("characters were found", project.characters.count >= 4, project.characters.map(\.name).joined(separator: ", "))

        // Rules, before any AI.
        var fresh = ProjectBuilder.make(from: ImportedBook(title: "t", chapters: project.chapters.map {
            Chapter(title: $0.title, paragraphs: $0.paragraphs.map { Paragraph(text: $0.text, style: $0.style, italic: $0.italic) })
        }), fileName: "t.md")
        let freshIndex = BookIndex(fresh)
        func freshLine(_ p: String) -> Line? { fresh.lines.first { fresh.text(of: $0, index: freshIndex).hasPrefix(p) } }
        check("“said Tom Hale” assigns Tom by rule", fresh.character(freshLine("You're late")?.speakerID)?.name == "Tom Hale",
              fresh.character(freshLine("You're late")?.speakerID)?.name ?? "nobody")
        check("“Mara said” assigns Mara by rule", fresh.character(freshLine("The path was")?.speakerID)?.name.hasPrefix("Mara") == true)
        check("a quote across paragraphs continues", freshLine("She was his sister")?.continuesPrevious == true)
        check("italic text is a thought", line("He hasn't asked")?.kind == .thought)
        check("a block quote is written text", line("My dear girl")?.kind == .written)
        check("dash dialogue in a paragraph is still found", line("—Da says if")?.kind == .spoken)
        _ = RuleAttribution.apply(to: &fresh)

        // Assign, undo, redo.
        if let l = line("Slow down"), let pip = doc.project.characters.first(where: { $0.name.hasPrefix("Pip") }) {
            let before = l.speakerID
            doc.assign([l.id], to: pip.id, undoManager: undo)
            check("assigning a line changes its speaker", doc.project.lines.first { $0.id == l.id }?.speakerID == pip.id)
            check("an assigned line is confirmed", doc.project.lines.first { $0.id == l.id }?.source == .user)
            undo.undo()
            check("Undo puts the speaker back", doc.project.lines.first { $0.id == l.id }?.speakerID == before)
        }

        // Exchange.
        if let l = line("Then you'll be"), let mara = doc.project.characters.first(where: { $0.name.hasPrefix("Mara") }) {
            let prev = l.speakerID
            doc.assign([l.id], to: mara.id, undoManager: undo)
            let proposals = Exchange.proposals(in: doc.project, after: l.id, previousSpeaker: prev)
            check("fixing one line of a back-and-forth proposes the rest", !proposals.isEmpty, "\(proposals.count)")
            undo.undo()
        }

        // Merge.
        let extra = doc.addCharacter(named: "Thomas", undoManager: undo)
        if let tom = doc.project.characters.first(where: { $0.name == "Tom Hale" }), let l = line("Aye.") {
            doc.assign([l.id], to: extra, undoManager: undo)
            doc.merge(extra, into: tom.id, undoManager: undo)
            check("merge moves lines", doc.project.lines.first { $0.id == l.id }?.speakerID == tom.id)
            check("merge keeps the other name as an alias", doc.project.character(tom.id)?.aliases.contains("Thomas") == true)
            check("merge removes the merged character", doc.project.character(extra) == nil)
        }

        // The reading view keeps each highlight on its own line after lines come and go.
        do {
            let (_, built) = ManuscriptTextView.Coordinator.build(project: doc.project, index: doc.index, fontSize: 16)
            let revision = doc.lineRevision
            if let first = doc.project.lines.first(where: { $0.kind == .spoken }) {
                doc.removeLines([first.id], undoManager: undo)
                check("removing a line remaps the reading view", doc.lineRevision != revision)
                let entries = ManuscriptTextView.Coordinator.entries(project: doc.project, index: doc.index, paragraphRanges: built.paragraphRanges)
                let mapped = entries.allSatisfy { e in
                    guard let li = doc.index.lineIndexByID[e.lineID], let start = built.paragraphRanges[doc.project.lines[li].paragraphID]?.location else { return false }
                    return e.range.location == start + doc.project.lines[li].range.location
                }
                check("after Mark as Narration every highlight is its own line's", mapped && entries.count == doc.project.lines.count)
                undo.undo()
                check("undoing it brings the line back", doc.project.lines.contains { $0.id == first.id })
            }
            // Delete Text: an ISBN-like run inside a paragraph, and a whole chapter; Undo restores both.
            if let para = doc.project.chapters.first?.paragraphs.first(where: { ($0.text as NSString).length > 40 }) {
                let before = doc.project
                let textRevision = doc.textRevision
                doc.deleteText(cuts: [para.id: [TextSpan(location: 0, length: 10)]], undoManager: undo)
                let after = doc.project.chapters.first?.paragraphs.first { $0.id == para.id }?.text ?? ""
                check("Delete Text removes the words from the project", after.count < para.text.count && doc.textRevision != textRevision)
                check("Delete Text keeps every line inside its paragraph", doc.project.lines.allSatisfy { l in
                    doc.index.lineIndexByID[l.id] != nil && (doc.project.text(of: l, index: doc.index).isEmpty == false)
                })
                undo.undo()
                check("Undo brings deleted text back", doc.project == before)
                if let last = doc.project.chapters.last {
                    doc.deleteText(cuts: [:], chapters: [last.id], undoManager: undo)
                    check("Delete Chapter removes it and its lines", !doc.project.chapters.contains { $0.id == last.id }
                          && doc.project.lines.allSatisfy { l in !last.paragraphs.contains { $0.id == l.paragraphID } })
                    undo.undo()
                    check("Undo brings a deleted chapter back", doc.project == before)
                }
            }
            // Pins: pinned speakers head the list, A to Z by first name; the menu's search narrows it.
            if doc.project.characters.count >= 3 {
                let chosen = Array(doc.project.characters.sorted(by: Project.byFirstName).suffix(2))
                for c in chosen { doc.setPinned(c.id, true, undoManager: undo) }
                let order = doc.project.speakerOrder
                check("pinned speakers come first, A to Z", order.pinned.map(\.id) == chosen.map(\.id)
                      && order.others.map(\.firstName) == order.others.map(\.firstName).sorted { $0.localizedStandardCompare($1) == .orderedAscending })
                let menu = SpeakerMenu(project: doc.project, current: nil, assign: { _ in }, narration: {}, pin: { _, _ in })
                let target = order.others.last!
                menu.filter(String(target.firstName.prefix(3)))
                check("typing in the speaker menu narrows it", menu.firstMatch?.id == target.id || menu.firstMatch.map { $0.matches(String(target.firstName.prefix(3))) } == true)
                menu.filter("zzqx")
                check("a search with no match says so", menu.firstMatch == nil)
                for _ in chosen { undo.undo() }
                check("Undo unpins", doc.project.speakerOrder.pinned.isEmpty)
            }
            // Make Line over an existing line keeps book order.
            if let target = doc.project.lines.dropFirst(3).first(where: { $0.range.length > 4 }) {
                let span = TextSpan(location: target.range.location, length: target.range.length - 1)
                doc.makeLine(paragraphID: target.paragraphID, range: span, speaker: nil, undoManager: undo)
                let order = doc.project.lines.map { (doc.index.positions[$0.paragraphID]!.global, $0.range.location) }
                check("Make Line keeps lines in book order", zip(order, order.dropFirst()).allSatisfy { $0 <= $1 })
                undo.undo()
            }
        }

        // Saving.
        if let data = try? InnerVoiceDocument.encoder.encode(doc.project),
           let back = try? InnerVoiceDocument.decoder.decode(Project.self, from: data) {
            check("a project saves and reads back the same", back == doc.project)
        } else {
            check("a project saves and reads back the same", false)
        }
        // The file on disk: one zip with the project, the manuscript and the samples.
        let sample = InnerVoiceDocument.Snapshot(project: doc.project, audio: ["Kerensa.m4a": Data([1, 2, 3])],
                                                 original: ("Penhallow.docx", Data(repeating: 7, count: 4096)))
        if let file = try? InnerVoiceDocument.archive(sample), let back = try? ProjectArchive.read(file),
           let project = try? InnerVoiceDocument.decoder.decode(Project.self, from: back.projectJSON) {
            check("a project file is one zip that reads back the same",
                  ProjectArchive.looksLikeArchive(file) && project == doc.project
                  && back.audio == sample.audio && back.original?.name == "Penhallow.docx" && back.original?.data == sample.original?.data)
        } else {
            check("a project file is one zip that reads back the same", false)
        }

        // Revised manuscript.
        var book = ImportedBook(title: "t", chapters: doc.project.chapters.map { c in
            Chapter(title: c.title, paragraphs: c.paragraphs.map { Paragraph(text: $0.text, style: $0.style, italic: $0.italic) })
        })
        book.chapters[1].paragraphs[1].text = book.chapters[1].paragraphs[1].text.replacingOccurrences(of: "avoiding me", with: "avoiding me for weeks")
        book.chapters[0].paragraphs.insert(Paragraph(text: "Rain hammered the glass."), at: 1)
        let removedText = book.chapters[2].paragraphs.remove(at: 3).text
        let result = RevisionMerger.merge(doc.project, with: book, fileName: "v2.docx")
        check("revision finds the changed paragraph", result.record.changed.count == 1, "\(result.record.changed.count)")
        check("revision finds the added paragraph", result.record.added.count == 1, "\(result.record.added.count)")
        check("revision finds the removed paragraph", result.record.removed.contains { $0.text == removedText })
        let keptIndex = BookIndex(result.project)
        let keptLate = result.project.lines.first { result.project.text(of: $0, index: keptIndex).hasPrefix("You're late") }
        check("revision keeps tags on unchanged paragraphs", keptLate?.speakerID == line("You're late")?.speakerID)
        let edited = result.project.lines.first { result.project.text(of: $0, index: keptIndex).hasPrefix("You have been avoiding") }
        check("revision keeps the speaker of an edited line", edited?.speakerID != nil && edited?.speakerID == line("You have been avoiding")?.speakerID)
        check("revision keeps recorded marks on unchanged paragraphs", !result.project.recorded.isEmpty)

        // Exports.
        let dir = FileManager.default.temporaryDirectory.appending(path: "InnerVoice-selftest")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        for (kind, format, ext) in [(Exporter.Kind.narratorScript, Exporter.Format.pdf, "pdf"), (.narratorScript, .word, "docx"),
                                    (.manuscript, .pdf, "pdf"), (.cast, .csv, "csv"), (.pronunciations, .pdf, "pdf")] {
            let url = dir.appending(path: "\(kind.rawValue).\(ext)")
            try? FileManager.default.removeItem(at: url)
            var o = Exporter.Options()
            o.kind = kind
            o.format = format
            o.narratorID = doc.project.narrators.first?.id
            do {
                try Exporter.write(o, document: doc, to: url)
                let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
                check("export \(kind.title) as \(format.rawValue)", size > 500, "\(size) bytes at \(url.path)")
            } catch {
                check("export \(kind.title) as \(format.rawValue)", false, error.localizedDescription)
            }
        }

        // Model advice.
        check("8 GB Macs get a small model", ModelCatalog.recommendedID(memoryGB: 8) == "qwen3-4b")
        if let big = ModelCatalog.model(id: "gemma-4-31b") {
            check("a 31B model is too large for 16 GB", ModelCatalog.advice(for: big, memoryGB: 16).fit == .tooLarge)
            check("a 31B model is recommended for 64 GB", ModelCatalog.advice(for: big, memoryGB: 64).fit == .recommended)
        }
        check("revealed-late facts exist in the sample", {
            guard let tom = doc.project.characters.first(where: { $0.name == "Tom Hale" }),
                  let first = doc.index.stats[tom.id]?.firstLine else { return false }
            return tom.facts.contains { f in f.sourceParagraphID.flatMap { doc.index.positions[$0] }.map { $0.chapter > first.chapter } ?? false }
        }())
        _ = index

        await voices(doc, undo: undo)
        if ProcessInfo.processInfo.environment["APP_SELFTEST_VOICES"] != nil { await modelVoices(doc, undo: undo) }
        if ProcessInfo.processInfo.environment["APP_SELFTEST_DOWNLOAD"] != nil { await voiceDownload() }
        await elevenLabs(doc, undo: undo)

        if let path = ProcessInfo.processInfo.environment["APP_SELFTEST_MODEL"] {
            await liveModel(path: path)
        }
    }

    /// Tags the sample book with a real model and scores it against the right answers.
    private static func liveModel(path: String) async {
        var project = SeedData.project()
        for i in project.lines.indices where project.lines[i].source == .ai {
            project.lines[i].source = .none
            project.lines[i].speakerID = nil
            project.lines[i].confidence = 0
        }
        let doc = InnerVoiceDocument(project: project)
        let toneAsks = doc.project.chapters.indices.map { TonePrompt.chunks(project: doc.project, index: doc.index, chapterIndex: $0).reduce(0) { $0 + $1.labels.count } }
        print("LIVE tone questions per chapter before tagging: \(toneAsks)")
        let manager = ModelManager.shared
        manager.selection = path == "apple" ? "apple" : "file:" + path
        let start = Date()
        doc.tagging.tagSpeakers(undoManager: nil)
        while doc.tagging.isRunning { try? await Task.sleep(for: .milliseconds(200)) }
        let seconds = Date().timeIntervalSince(start)
        if let failure = doc.tagging.failure { check("live model tags the sample", false, failure); return }
        let p = doc.project
        let index = doc.index
        func id(_ n: String) -> UUID { p.characters.first { $0.name.hasPrefix(n) }?.id ?? UUID() }
        var right = 0, total = 0, wrong: [String] = []
        for l in p.lines where l.source == .ai {
            let text = p.text(of: l, index: index)
            guard let truth = SeedData.answer(for: text, tom: id("Tom"), mara: id("Mara"), edda: id("Edda"), pip: id("Pip")) else { continue }
            total += 1
            if l.speakerID == truth { right += 1 } else { wrong.append("“\(text.prefix(24))” → \(p.character(l.speakerID)?.name ?? "?")") }
        }
        let score = total == 0 ? 0 : Double(right) / Double(total)
        print("LIVE \(manager.selectionName): \(right)/\(total) lines right (\(Int(score * 100))%) in \(Int(seconds)) s")
        for w in wrong { print("  wrong: \(w)") }
        check("live model tags most sample lines right", score >= 0.6, "\(Int(score * 100))%")
        // Tones: the AI read the lines the words left open.
        let byAI = p.lines.filter { $0.toneSource == .ai }
        let counts = Dictionary(grouping: p.lines.compactMap(\.tone), by: { $0 }).mapValues(\.count)
        print("LIVE tones: \(byAI.count) read by the AI; " + counts.sorted { $0.value > $1.value }.map { "\($0.key.rawValue) \($0.value)" }.joined(separator: ", "))
        for l in p.lines where l.toneSource == .ai && (l.tone ?? .neutral) != .neutral {
            print("  tone: “\(p.text(of: l, index: index).prefix(40))” → \(l.tone!.rawValue) \(Int((l.toneConfidence ?? 0) * 100))%")
        }
        check("live model reads tones", byAI.count >= 5, "\(byAI.count)")
        check("tones are tagged with a date", p.tonesTagged != nil)
        manager.unloadNow()
    }
}


/// A stand-in for api.elevenlabs.io in the self-test: a plan, two voices, and half a
/// second of tone for each speech request (whose JSON body is kept for checking).
nonisolated final class FakeElevenLabs: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var speechRequests: [[String: Any]] = []
    nonisolated(unsafe) static var status = 200

    static func reset() { speechRequests = []; status = 200 }

    override class func canInit(with request: URLRequest) -> Bool { request.url?.host == "api.elevenlabs.io" }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let url = request.url!
        var body = Data()
        var code = Self.status
        if url.path.hasPrefix("/v1/text-to-speech/") {
            var data = request.httpBody ?? Data()
            if data.isEmpty, let stream = request.httpBodyStream {
                stream.open()
                var buf = [UInt8](repeating: 0, count: 65536)
                while stream.hasBytesAvailable { let n = stream.read(&buf, maxLength: buf.count); if n <= 0 { break }; data.append(buf, count: n) }
                stream.close()
            }
            Self.speechRequests.append((try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:])
            if code == 200 {
                var pcm = Data()
                for i in 0..<12_000 { var v = Int16(sin(Double(i) / 24_000 * 2 * .pi * 220) * 8000).littleEndian; pcm.append(Data(bytes: &v, count: 2)) }
                body = pcm
            } else {
                body = Data(#"{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}"#.utf8)
            }
        } else if url.path == "/v1/user/subscription" {
            body = Data(#"{"tier":"creator","character_count":1200,"character_limit":100000}"#.utf8)
        } else if url.path == "/v2/voices" {
            body = Data(#"{"voices":[{"voice_id":"v1","name":"Alice - clear narrator","labels":{"gender":"female","age":"middle_aged","accent":"british","description":"clear"}},{"voice_id":"v2","name":"Bram","labels":{"gender":"male","age":"old","accent":"american"}}],"has_more":false}"#.utf8)
        } else {
            code = 404
        }
        let response = HTTPURLResponse(url: url, statusCode: code, httpVersion: "HTTP/1.1", headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
