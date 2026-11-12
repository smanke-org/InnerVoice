import AppKit
@preconcurrency import AVFoundation
import InnerVoiceKit
import SwiftUI

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

        // Casting Style (docs/CASTING-STYLE.md): who reads each character, and nothing lost.
        do {
            let engine = doc.project.engineInUse
            let segs = SpeechScript.segments(chapter: 0, chapters: doc.project.chapters.count, project: doc.project, index: doc.index, includeNarration: true)
            if let mara = doc.project.characters.first(where: { $0.name.hasPrefix("Mara") }),
               let tom = doc.project.characters.first(where: { $0.name.hasPrefix("Tom") }),
               let maraLine = segs.first(where: { $0.speakerID == mara.id && $0.kind == .spoken }),
               let tomLine = segs.first(where: { $0.speakerID == tom.id && $0.kind == .spoken }) {
                let voicesBefore = doc.project.characters.map(\.voices)
                doc.setGender(.female, for: [mara.id], undoManager: undo)
                doc.setGender(.male, for: [tom.id], undoManager: undo)
                doc.setCastingStyle(.shared, undoManager: undo)
                let men = doc.project.settings.roleVoice(.men, for: engine)?.choice
                let women = doc.project.settings.roleVoice(.women, for: engine)?.choice
                let r = VoiceResolver(project: doc.project, index: doc.index)
                check("Narrator + Two Voices casts different men's and women's voices", men != nil && women != nil && men != women && men != r.narrator)
                check("men are read in the men's voice and women in the women's", r.choice(for: tomLine) == men && r.choice(for: maraLine) == women)
                doc.setRole(.own, for: tom.id, undoManager: undo)
                let own = doc.project.character(tom.id)?.voice(for: engine)?.choice
                check("Own Voice keeps a character's own voice in a shared style",
                      own != nil && VoiceResolver(project: doc.project, index: doc.index).choice(for: tomLine) == own.map { VoiceResolver.withNotes($0, doc.project.character(tom.id)) })
                doc.setNarrationRole(.women, undoManager: undo)
                check("a two-person book reads the narration in the women's voice", VoiceResolver(project: doc.project, index: doc.index).narrator == women)
                doc.setCastingStyle(.oneVoice, undoManager: undo)
                let one = VoiceResolver(project: doc.project, index: doc.index)
                check("One Voice reads every character in the narrator's voice", one.choice(for: maraLine) == one.narrator)
                let tomShade = one.request(for: tomLine)?.shade?.pitch ?? 0
                let maraShade = one.request(for: maraLine)?.shade?.pitch ?? 0
                check("One Voice shades a man's lines lower than a woman's", tomShade < maraShade)
                if let narration = segs.first(where: { $0.lineID == nil }) {
                    check("the narration itself is never shaded", one.request(for: narration)?.shade == nil)
                }
                if let shaded = one.request(for: maraLine), shaded.shade != nil {
                    var plain = shaded
                    plain.shade = nil
                    check("a shaded line is cached apart from the unshaded one", shaded.cacheKey != plain.cacheKey)
                }
                // (Tom has a voice of his own from above, so Mara.)
                doc.setShade(Shade(pitch: 3, pace: -1), for: mara.id, undoManager: undo)
                check("a shade you set wins", VoiceResolver(project: doc.project, index: doc.index).request(for: maraLine)?.shade == Shade(pitch: 3, pace: -1))
                for _ in 0..<7 { undo.undo() }
                check("undoing the Casting Style edits leaves every voice and gender as it was",
                      doc.project.settings.castingStyle == nil && doc.project.characters.map(\.voices) == voicesBefore
                      && doc.project.characters.allSatisfy { $0.gender == nil && $0.role == nil })
            } else {
                check("the sample book has Mara and Tom lines for the Casting Style checks", false)
            }
        }

        // Casting Style in Narration Tools: Three Narrators and their scripts, Just Me cues.
        do {
            if let mara = doc.project.characters.first(where: { $0.name.hasPrefix("Mara") }),
               let tom = doc.project.characters.first(where: { $0.name.hasPrefix("Tom") }),
               let tomLine = doc.project.lines.first(where: { $0.speakerID == tom.id && $0.kind == .spoken }),
               let maraLine = doc.project.lines.first(where: { $0.speakerID == mara.id && $0.kind == .spoken }) {
                let narratorsBefore = doc.project.narrators
                // The sample casts Tom and Mara to narrators by hand, and a hand-made choice
                // wins; clear them so their parts decide.
                check("a narrator you cast by hand wins over the part", doc.project.character(tom.id)?.narratorID == nil
                      || doc.effectiveNarrator(for: tom)?.id == doc.project.character(tom.id)?.narratorID)
                doc.change("Clear Narrators", undoManager: undo) { p in
                    for i in p.characters.indices where [tom.id, mara.id].contains(p.characters[i].id) { p.characters[i].narratorID = nil }
                }
                doc.setGender(.female, for: [mara.id], undoManager: undo)
                doc.setGender(.male, for: [tom.id], undoManager: undo)
                doc.setCastingStyle(.shared, undoManager: undo)
                doc.setUpThreeNarrators(undoManager: undo)
                let men = doc.project.narrators.first { $0.role == .men }
                check("Set Up Narrators adds a narrator for the narration, the men and the women",
                      [VoiceRole.narrator, .men, .women].allSatisfy { r in doc.project.narrators.contains { $0.role == r } })
                check("Three Narrators: Tom is read by the men’s narrator",
                      men != nil && doc.project.character(tom.id).flatMap { doc.effectiveNarrator(for: $0) }?.id == men?.id)
                var options = Exporter.Options()
                options.kind = .narratorScript
                options.narratorID = men?.id
                options.includeCues = false  // a cue is the line before, often Mara's
                let script = Exporter.build(options, document: doc).string
                let tomText = doc.project.text(of: tomLine, index: doc.index), maraText = doc.project.text(of: maraLine, index: doc.index)
                check("the men’s Narrator Script has Tom’s lines and not Mara’s", script.contains(tomText) && !script.contains(maraText))
                doc.setCastingStyle(.oneVoice, undoManager: undo)
                doc.setNarratorGender(.female, undoManager: undo)
                let cues = doc.narrationShades()
                check("Just Me: a woman narrating shades Tom lower and leaves Mara in her own voice",
                      (cues[tom.id]?.pitch ?? 0) < 0 && cues[mara.id] == nil)
                check("Just Me: Tom’s cue shows in the margin as an arrow", cues[tom.id].flatMap(MarginOverlay.arrow) != nil)
                for _ in 0..<7 { undo.undo() }
                check("undoing leaves the narrators as they were", doc.project.narrators == narratorsBefore && doc.project.settings.castingStyle == nil)
            }
        }

        // Shading audio: a 200 Hz tone shifted 12 steps up comes out near 400 Hz, and a pace
        // step changes its length by 8%.
        do {
            let rate = SpeechAudio.format.sampleRate
            let tone = (0..<Int(rate)).map { Float(sin(2 * Double.pi * 200 * Double($0) / rate)) * 0.5 }
            func hz(_ x: [Float]) -> Double {
                let mid = x.dropFirst(x.count / 4).prefix(x.count / 2)
                let crossings = zip(mid, mid.dropFirst()).filter { $0 < 0 && $1 >= 0 }.count
                return Double(crossings) / (Double(mid.count) / rate)
            }
            let up = (try? ShadeAudio.apply(tone, semitones: 12, pace: 0)) ?? []
            let slow = (try? ShadeAudio.apply(tone, semitones: 0, pace: -1)) ?? []
            check("a shade of 12 steps doubles the pitch (\(Int(hz(up))) Hz)", abs(hz(up) - 400) < 20)
            let stretch = Double(slow.count) / Double(tone.count)
            check("a slower pace step makes it about 8% longer (\(String(format: "%.3f", stretch))×)", abs(stretch - 1 / 0.92) < 0.03)
        }

        // Pauses: exported audio grows by exactly the extra pause time; voices' own silence is trimmed.
        do {
            let resolver = VoiceResolver(project: doc.project)
            let few = Array(SpeechScript.segments(chapter: 0, project: doc.project, index: doc.index, includeNarration: true).prefix(6))
            let dir = FileManager.default.temporaryDirectory.appending(path: "iv-pause-selftest")
            try? FileManager.default.removeItem(at: dir)
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            func length(_ settings: PauseSettings, _ name: String) async -> Double {
                PauseStore.shared.settings = settings
                let urls = (try? await AudioExporter.write([AudioExporter.Part(name: name, segments: few)], to: dir, format: .wav, resolver: resolver) { _, _, _ in }) ?? []
                guard let u = urls.first, let f = try? AVAudioFile(forReading: u) else { return 0 }
                return Double(f.length) / f.processingFormat.sampleRate
            }
            let tight = await length(.tight, "tight"), relaxed = await length(.relaxed, "relaxed")
            let expected = few.reduce(0.0) { $0 + PauseSettings.relaxed.seconds($1.pause) - PauseSettings.tight.seconds($1.pause) }
            check("Export Audio uses the pause settings exactly", tight > 1 && abs((relaxed - tight) - expected) < 0.05,
                  String(format: "tight %.2f s, relaxed %.2f s, expected +%.2f s", tight, relaxed, expected))
            PauseStore.shared.settings = .standard
            try? FileManager.default.removeItem(at: dir)
            if let seg = few.first(where: { !$0.text.isEmpty }), let req = resolver.request(for: seg), let raw = try? await SpeechRenderer.shared.render(req) {
                let t = SpeechAudio.trimmed(raw)
                check("a voice's silence before and after the words is trimmed", t.frameLength < raw.frameLength && t.frameLength > raw.frameLength / 3,
                      "\(raw.frameLength) → \(t.frameLength) frames")
            }
            var finished = false
            SpeechPlayer.shared.play(Array(few.prefix(1)), resolver: resolver, onFinish: { finished = true })
            let until = Date().addingTimeInterval(15)
            while !finished, Date() < until { try? await Task.sleep(for: .milliseconds(100)) }
            check("Play tells Try It when it reaches the end", finished)
        }

        // Activity: reviewing guesses counts kept and changed, per AI model; numbers only.
        do {
            let store = ActivityStore.shared
            store.resetForTest()
            let guesses = doc.project.lines.filter { $0.speakerID != nil && $0.source != .user }.prefix(2)
            if guesses.count == 2, let other = doc.project.characters.first(where: { $0.id != guesses.last!.speakerID }) {
                doc.change("", undoManager: nil) { p in
                    for i in p.lines.indices where guesses.contains(where: { $0.id == p.lines[i].id }) { p.lines[i].source = .ai; p.lines[i].aiModel = "Test Model" }
                }
                doc.assign([guesses.first!.id], to: guesses.first!.speakerID, undoManager: undo)
                doc.assign([guesses.last!.id], to: other.id, undoManager: undo)
                let t = store.log.lifetime
                let acc = ModelAccuracy.all(in: t).first { $0.model == "Test Model" }
                check("confirming a guess counts as kept, changing it as changed",
                      t[ActivityKey.guessesKept] == 1 && t[ActivityKey.guessesChanged] == 1, "\(t)")
                check("AI accuracy is kept per model", acc?.kept == 1 && acc?.changed == 1 && acc?.rate == 0.5)
                undo.undo(); undo.undo(); undo.undo()
            }
            if let l = doc.project.lines.first(where: { $0.toneSource == .rule || $0.toneSource == .ai }), let t = l.tone {
                doc.setTone(t, for: [l.id], undoManager: undo)
                check("confirming a tone counts as kept", store.log.lifetime[ActivityKey.tonesKept] == 1)
                undo.undo()
            }
            let saved = (try? JSONEncoder().encode(store.log)).map { String(decoding: $0, as: UTF8.self) } ?? ""
            let words = doc.project.chapters.flatMap(\.paragraphs).flatMap { $0.text.split(separator: " ") }.filter { $0.count > 6 }.prefix(50)
            check("Activity stores numbers only, nothing from the book", !words.contains { saved.contains($0) })
            store.resetForTest()
        }

        // Project types: each shows its own pages; nothing is deleted when switching.
        do {
            let all = Workspace.Page.allCases.filter { $0 != .revisions }
            var p = doc.project
            check("a project that hasn't been asked shows every page", all.allSatisfy { $0.isShown(in: p) })
            p.settings.purpose = .narration
            check("Narration Tools hides Voices and Pronunciations and keeps the Teleprompter and Index",
                  !Workspace.Page.voices.isShown(in: p) && !Workspace.Page.pronunciations.isShown(in: p)
                    && ([.teleprompter, .index, .review, .cast] as [Workspace.Page]).allSatisfy { $0.isShown(in: p) })
            p.settings.purpose = .aiVoices
            check("Produce with AI Voices hides the Teleprompter and Index and keeps Voices and Pronunciations",
                  Workspace.Page.voices.isShown(in: p) && Workspace.Page.pronunciations.isShown(in: p)
                    && !([.teleprompter, .index] as [Workspace.Page]).contains { $0.isShown(in: p) } && Workspace.Page.review.isShown(in: p))
            let before = doc.project.lines.count
            doc.updateSettings("Set Project Type", undoManager: undo) { $0.purpose = .aiVoices }
            doc.updateSettings("Change Project Type", undoManager: undo) { $0.purpose = .narration }
            check("switching project type keeps everything", doc.project.lines.count == before && doc.project.settings.purpose == .narration)
            undo.undo(); undo.undo()
            check("Undo takes the project type back", doc.project.settings.purpose == nil)
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

    /// The setup sheet's Hear It clips (Resources/Samples/casting-<style>.m4a): the opening
    /// of the sample book in each Casting Style, with Qwen3-TTS (APP_MAKE_CASTING_SAMPLES=<folder>,
    /// with APP_SELFTEST_VOICES).
    private static func castingSamples(_ doc: InnerVoiceDocument, undo: UndoManager, to dir: URL) async {
        guard ModelSpeech.isInstalled(.qwen), let storyteller = VoiceInfo.qwenPresets.first(where: { $0.name == "Warm Storyteller" }) else { return }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        doc.setVoiceEngine(.qwen, undoManager: undo)
        doc.setVoice(storyteller.choice, for: nil, undoManager: undo)
        var steps = 2
        for c in doc.project.characters {
            if let g = VoiceCasting.profile(c, project: doc.project, index: doc.index).gender, g != .neutral {
                doc.setGender(CharacterGender(g), for: [c.id], undoManager: undo)
                steps += 1
            }
        }
        let segs = Array(SpeechScript.segments(chapter: 0, project: doc.project, index: doc.index, includeNarration: true).prefix(10))
        for style in CastingStyle.allCases {
            doc.setCastingStyle(style, undoManager: undo)
            steps += 1
            let resolver = VoiceResolver(project: doc.project, index: doc.index)
            do {
                let urls = try await AudioExporter.write([AudioExporter.Part(name: "casting-" + style.rawValue, segments: segs)], to: dir, format: .m4a, resolver: resolver) { _, _, _ in }
                check("Hear It sample for \(style.title(for: .aiVoices))", !urls.isEmpty, urls.first?.lastPathComponent ?? "")
            } catch {
                check("Hear It sample for \(style.title(for: .aiVoices))", false, error.localizedDescription)
            }
        }
        for _ in 0..<steps { undo.undo() }
    }

    /// One Voice with Qwen3-TTS (APP_SELFTEST_VOICES): the opening of the sample book read by
    /// the Warm Storyteller, shaded and unshaded, saved in APP_SELFTEST_AUDIO_OUT for listening.
    private static func oneVoiceTableRead(_ doc: InnerVoiceDocument, undo: UndoManager) async {
        guard ModelSpeech.isInstalled(.qwen), let storyteller = VoiceInfo.qwenPresets.first(where: { $0.name == "Warm Storyteller" }) else { return }
        let out = ProcessInfo.processInfo.environment["APP_SELFTEST_AUDIO_OUT"].map { URL(fileURLWithPath: $0) }
        doc.setVoiceEngine(.qwen, undoManager: undo)
        doc.setVoice(storyteller.choice, for: nil, undoManager: undo)
        doc.setCastingStyle(.oneVoice, undoManager: undo)
        let segs = Array(SpeechScript.segments(chapter: 0, project: doc.project, index: doc.index, includeNarration: true).prefix(16))
        let shaded = VoiceResolver(project: doc.project, index: doc.index)
        let lines = segs.filter { $0.lineID != nil && $0.kind != .written }
        check("Qwen3-TTS One Voice: the Warm Storyteller plays every character", lines.allSatisfy { shaded.choice(for: $0) == shaded.narrator })
        check("Qwen3-TTS One Voice: men's lines are shaded lower", lines.contains { (shaded.request(for: $0)?.shade?.pitch ?? 0) < 0 })
        var plain = shaded
        plain.shades = [:]
        for (name, resolver) in [("onevoice-qwen-shaded", shaded), ("onevoice-qwen-unshaded", plain)] {
            do {
                let dir = out ?? FileManager.default.temporaryDirectory.appending(path: "iv-onevoice")
                try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                let start = Date()
                let urls = try await AudioExporter.write([AudioExporter.Part(name: name, segments: segs)], to: dir, format: .wav, resolver: resolver) { _, _, _ in }
                check("Qwen3-TTS One Voice reads the opening \(name.hasSuffix("unshaded") ? "unshaded" : "shaded")", !urls.isEmpty,
                      String(format: "%.0f s", Date().timeIntervalSince(start)))
                for (seg, n) in zip(lines, 0...) where n < 8 {
                    let s = resolver.request(for: seg)?.shade
                    if name.hasSuffix("-shaded") { print("SHADE \(doc.project.character(seg.speakerID)?.firstName ?? "?"): \(s?.cue ?? "narrator's own") — \(seg.text.prefix(50))") }
                }
            } catch {
                check("Qwen3-TTS One Voice reads the opening", false, error.localizedDescription)
            }
        }
        for _ in 0..<3 { undo.undo() }
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
            // A voice designed from long Voice Notes still speaks (the description is capped).
            if engine == .qwen, var c = top {
                c.voiceNotes = "Gravelly and slow, a voice worn down by sea air. Speaks from the chest; rarely raises it.\n\n"
                    + "Voice brief:\nPitch: Low, rough at the bottom\nPace: Slow, long pauses before answers\nTexture: Hoarse, weathered\nAttitude: Guarded, dry humor\nContrast: Deeper and slower than anyone around him\n\n"
                    + "From the book:\nAge: In his seventies\nAccent: Cornish\nVoice: Rasping\nManner: Stubborn; kind underneath"
                let choice = VoiceResolver.withNotes(VoiceChoice(engine: .qwen, voiceID: "design", design: "An older man with a deep voice."), c)
                do {
                    let audio = try await SpeechRenderer.shared.render(SpeechRequest(text: "I told you the tide would turn before morning, and it did.", choice: choice, tone: .neutral, kind: .spoken))
                    let seconds = Double(audio.frameLength) / audio.format.sampleRate
                    check("Qwen3-TTS speaks a voice designed from Voice Notes", seconds > 1.5 && seconds < 20,
                          String(format: "%d-character description, %.1f s of audio", choice.design?.count ?? 0, seconds))
                    if let out { SpeechRenderer.write(audio, to: out.appending(path: "qwen-notes.caf")) }
                } catch {
                    check("Qwen3-TTS speaks a voice designed from Voice Notes", false, error.localizedDescription)
                }
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
    /// Use Voices from Another Project: an earlier book saved to /tmp is read back without
    /// opening it, its voices go to the matched characters as one undo step.
    private static func seriesVoices(_ doc: InnerVoiceDocument, undo: UndoManager) {
        var earlier = doc.project
        earlier.title = "Saltwater Hymns"
        guard let tom = earlier.characters.firstIndex(where: { $0.name.hasPrefix("Tom") }) else { return }
        let voice = VoiceChoice(engine: .orpheus, voiceID: "leo")
        earlier.characters[tom].setVoice(CastVoice(choice: voice, confidence: 1, source: .user), for: .orpheus)
        earlier.characters[tom].voiceNotes = "Low and slow, from the earlier book."
        let url = URL(fileURLWithPath: "/tmp/innervoice-selftest-series.innervoice")
        do {
            try InnerVoiceDocument.archive(.init(project: earlier, audio: [:], original: nil)).write(to: url)
            let read = try InnerVoiceDocument.readProject(at: url)
            try? FileManager.default.removeItem(at: url)
            let matches = SeriesVoices.suggest(from: read, oldIndex: BookIndex(read), to: doc.project)
            let before = doc.project
            doc.change("Use Voices from \(read.title)", undoManager: undo) { SeriesVoices.apply(matches, from: read, to: &$0, options: .init(engine: false)) }
            let now = doc.project.characters.first { $0.name.hasPrefix("Tom") }
            check("Use Voices from Another Project reads the earlier book and gives Tom his voice and notes",
                  read.title == "Saltwater Hymns" && now?.voice(for: .orpheus)?.choice == voice && now?.voice(for: .orpheus)?.source == .user
                  && now?.voiceNotes == "Low and slow, from the earlier book.")
            undo.undo()
            // (Earlier steps in this run share the undo group, so only the characters are compared.)
            check("one Undo takes the carried-over voices back", doc.project.characters == before.characters)
        } catch {
            check("Use Voices from Another Project reads the earlier book", false, error.localizedDescription)
        }
    }

    /// A click on a name in the margin opens Assign To for every line in that paragraph, and
    /// one pick confirms or changes them all; a click on a line itself still means that line.
    private static func marginClickInTheView() async {
        let doc = InnerVoiceDocument(project: SeedData.project())
        let undo = UndoManager()
        let workspace = Workspace()
        let look = ReadingLook(fontSize: 16, serif: true, spacing: .single, margin: .initials)
        var asked: [UUID] = []
        let host = NSHostingView(rootView: ManuscriptTextView(document: doc, workspace: workspace, undoManager: undo,
                                                              onAssignRequest: { ids, _, _ in asked = ids }, onVisibleChapter: { _ in }, look: look))
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 600), styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        host.layoutSubtreeIfNeeded()
        try? await Task.sleep(for: .milliseconds(300))
        defer { window.close() }
        func find(_ v: NSView) -> ReadingTextView? { (v as? ReadingTextView) ?? v.subviews.lazy.compactMap(find).first }
        guard let tv = find(host), let c = tv.coordinator else { check("the manuscript view is there to click", false); return }
        let project = doc.project
        let byParagraph = Dictionary(grouping: project.lines, by: \.paragraphID)
        guard let para = project.chapters.flatMap(\.paragraphs).first(where: { (byParagraph[$0.id]?.count ?? 0) >= 2 }),
              let paraLines = byParagraph[para.id], let first = paraLines.first,
              let point = c.marginPoint(of: first.id) else { check("the sample has a paragraph with two lines", false); return }
        let expected = project.lines.filter { $0.paragraphID == para.id }.map(\.id)
        check("a click on a name in the margin opens Assign To for the whole paragraph",
              c.marginClick(at: point) && asked == expected && workspace.selectedLines == expected, "\(asked.count) of \(expected.count) lines")
        asked = []
        c.click(at: c.layout.entries.first { $0.lineID == paraLines[1].id }!.range.location + 1)
        check("a click on a line in the text still opens Assign To for that line alone", asked == [paraLines[1].id])
        check("a click in the text beside the margin is not a margin click", !c.marginClick(at: NSPoint(x: tv.textContainerOrigin.x + 20, y: point.y)))
        check("a right-click on a name in the margin offers the whole paragraph",
              c.marginMenu(at: point)?.items.first?.title.contains("\(expected.count) lines in this paragraph") == true || expected.count == 1,
              c.marginMenu(at: point)?.items.first?.title ?? "no menu")
        // ⌘-click picks several paragraphs without asking; a click on one of them then
        // asks once for them all, in book order.
        if let paraB = project.chapters.flatMap(\.paragraphs).first(where: { $0.id != para.id && (byParagraph[$0.id]?.isEmpty == false) }),
           let firstB = byParagraph[paraB.id]?.first, let pointB = c.marginPoint(of: firstB.id) {
            let linesB = project.lines.filter { $0.paragraphID == paraB.id }.map(\.id)
            workspace.selectedLines = []
            asked = []
            c.marginClick(at: point, adding: true)
            c.marginClick(at: pointB, adding: true)
            let both = Set(expected + linesB)
            check("⌘-click on two margin names picks both paragraphs and asks nothing yet", Set(workspace.selectedLines) == both && asked.isEmpty)
            c.marginClick(at: pointB)
            check("a click on a picked paragraph's name opens Assign To for every picked line, in book order",
                  asked == project.lines.map(\.id).filter { both.contains($0) }, "\(asked.count) of \(both.count)")
            c.marginClick(at: point, adding: true)
            check("⌘-click on a picked paragraph's name takes it out", Set(workspace.selectedLines) == Set(linesB))
            c.toggle(at: c.layout.entries.first { $0.lineID == paraLines[1].id }!.range.location + 1)
            check("⌘-click on a line adds just that line", Set(workspace.selectedLines) == Set(linesB + [paraLines[1].id]))
        } else {
            check("the sample has a second paragraph with lines", false)
        }
        // One pick: the whole paragraph is the narrator's, with one speaker.
        guard let other = project.characters.first(where: { $0.id != first.speakerID }) else { return }
        doc.assign(expected, to: other.id, undoManager: undo)
        let after = doc.project.lines.filter { $0.paragraphID == para.id }
        check("one pick changes every line in the paragraph", after.count == expected.count && after.allSatisfy { $0.speakerID == other.id && $0.source == .user })
        undo.undo()
    }

    /// Delete Text the way the narrator does it: select text in the real manuscript view of
    /// a window, then press Delete or choose Delete Text from the right-click menu. The text
    /// must leave the project AND the screen.
    private static func deleteTextInTheView() async {
        let doc = InnerVoiceDocument(project: SeedData.project())
        let undo = UndoManager()
        let workspace = Workspace()
        let look = ReadingLook(fontSize: 16, serif: true, spacing: .single, margin: .initials)
        let host = NSHostingView(rootView: ManuscriptTextView(document: doc, workspace: workspace, undoManager: undo,
                                                              onAssignRequest: { _, _, _ in }, onVisibleChapter: { _ in }, look: look))
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 600), styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        host.layoutSubtreeIfNeeded()
        try? await Task.sleep(for: .milliseconds(300))
        func find(_ v: NSView) -> ReadingTextView? { (v as? ReadingTextView) ?? v.subviews.lazy.compactMap(find).first }
        guard let tv = find(host), let c = tv.coordinator else { check("the manuscript view is there to delete from", false); return }
        func screen() -> String { tv.string }
        func settle() async { host.layoutSubtreeIfNeeded(); try? await Task.sleep(for: .milliseconds(200)); host.layoutSubtreeIfNeeded() }

        // Cases: words inside a paragraph; a whole chapter heading alone; across two paragraphs.
        let chapter = doc.project.chapters[0]
        guard let para = chapter.paragraphs.first(where: { ($0.text as NSString).length > 40 }),
              let range = c.layout.paragraphRanges[para.id] else { check("the sample has a long paragraph", false); return }
        let words = (para.text as NSString).substring(to: 12)
        // 1. The Delete key.
        tv.setSelectedRange(NSRange(location: range.location, length: 12))
        if let key = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: window.windowNumber,
                                      context: nil, characters: "\u{7F}", charactersIgnoringModifiers: "\u{7F}", isARepeat: false, keyCode: 51) {
            tv.keyDown(with: key)
        }
        await settle()
        let projectText = doc.project.chapters[0].paragraphs.first { $0.id == para.id }?.text ?? ""
        check("the Delete key deletes selected text from the project", !projectText.hasPrefix(words), "still starts “\(projectText.prefix(20))”")
        check("the Delete key deletes selected text from the screen", (screen() as NSString).range(of: para.text).location == NSNotFound
              && (screen() as NSString).range(of: projectText).location != NSNotFound)
        undo.undo()
        await settle()
        check("Undo puts deleted text back on the screen", (screen() as NSString).range(of: para.text).location != NSNotFound)

        // 2. The right-click menu.
        guard let r2 = c.layout.paragraphRanges[para.id] else { return }
        tv.setSelectedRange(NSRange(location: r2.location, length: 12))
        let menu = c.menu(for: r2.location + 3, selection: tv.selectedRange())
        if let item = menu.items.first(where: { $0.title == "Delete Text" }), let action = item.action {
            NSApp.sendAction(action, to: item.target, from: item)
        } else {
            check("the right-click menu offers Delete Text", false)
        }
        await settle()
        check("Delete Text in the menu deletes it from the screen", (screen() as NSString).range(of: para.text).location == NSNotFound)
        undo.undo()
        await settle()

        // 3. A chapter heading alone (front matter often imports as a heading: "Copyright",
        // an ISBN). The first chapter's text stays with no heading; a later one joins the
        // chapter before it.
        guard let start = c.layout.chapterStarts.first else { return }
        let title = chapter.title
        tv.setSelectedRange(NSRange(location: start, length: (title as NSString).length))
        tv.delete(nil)
        await settle()
        check("deleting the first chapter's heading leaves its text with no heading",
              doc.project.chapters.first?.title == "" && doc.project.chapters.first?.paragraphs.count == chapter.paragraphs.count
              && (screen() as NSString).range(of: title).location == NSNotFound, "“\(doc.project.chapters.first?.title ?? "")”")
        undo.undo()
        await settle()
        let second = doc.project.chapters[1]
        if c.layout.chapterStarts.count > 1 {
            let s2 = c.layout.chapterStarts[1]
            tv.setSelectedRange(NSRange(location: s2, length: (second.title as NSString).length))
            tv.delete(nil)
            await settle()
            check("deleting a later chapter's heading joins its text to the chapter before",
                  doc.project.chapters.count == 2 && doc.project.chapters[0].paragraphs.count == chapter.paragraphs.count + second.paragraphs.count)
            undo.undo()
            await settle()
        }
        // 4. Part of a heading ("Chapter 1: The Storm" → "Chapter 1").
        if let colon = title.firstIndex(of: ":") {
            let keep = title[..<colon]
            let s1 = c.layout.chapterStarts[0]
            let cut = (String(keep) as NSString).length
            tv.setSelectedRange(NSRange(location: s1 + cut, length: (title as NSString).length - cut))
            tv.delete(nil)
            await settle()
            check("deleting part of a heading keeps the rest", doc.project.chapters.first?.title == String(keep), doc.project.chapters.first?.title ?? "")
            undo.undo()
            await settle()
        }
        check("Undo after heading deletes restores the book", doc.project.chapters.count == 3 && doc.project.chapters[0].title == title)
        window.close()
    }

    /// What views ask on every redraw (which engines are on and downloaded, the engine in use,
    /// the voice lists and names) must be cheap: these used to read the keychain, list the
    /// model folders and ask the system for its voices each time, and the window lagged.
    private static func redrawCost(_ doc: InnerVoiceDocument) {
        _ = VoiceCatalog.voices(for: .apple)
        let start = Date()
        for _ in 0..<500 {
            _ = EngineSwitches.shared.choices
            _ = doc.project.engineInUse
            _ = EngineSwitches.shared.usable(.qwen)
            _ = EngineSwitches.shared.usable(.orpheus)
            _ = VoiceCatalog.voices(for: .mixed, project: doc.project)
            for c in doc.project.characters { if let v = c.voice(for: .apple) { _ = VoiceCatalog.name(of: v.choice); _ = VoiceCatalog.info(for: v.choice) } }
        }
        let ms = Date().timeIntervalSince(start) / 500 * 1000
        print(String(format: "REDRAW voice questions: %.3f ms a redraw", ms))
        check("what a redraw asks about voices is cheap", ms < 1, String(format: "%.3f ms a redraw", ms))
    }

    /// Clone a Voice: offered only to a book that uses ElevenLabs, sends the recordings and
    /// the name (never book text), only after the narrator confirms the right to clone; the
    /// clone is listed first, never cast on its own, and can be renamed and deleted.
    /// Clone a Voice on this Mac: the clip is kept in a test folder, listed with the
    /// Qwen3-TTS voices, never cast on its own, and can be renamed and deleted. With the
    /// models (APP_SELFTEST_VOICES), a clip made from an Apple voice is cloned and speaks a
    /// line; APP_SELFTEST_TRANSCRIBE also finds its words with on-device recognition.
    private static func localClones(_ doc: InnerVoiceDocument, undo: UndoManager) async {
        let store = LocalClones.shared
        check("test runs keep cloned voices in a test folder", LocalClones.folder.path.contains("InnerVoice-test-clones"), LocalClones.folder.path)
        let rate = SpeechAudio.format.sampleRate
        let tone = (0..<Int(rate * 6)).map { Float(sin(Double($0) * 2 * .pi * 180 / rate) * 0.2) }
        guard let c = try? store.add(name: "Test Voice", gender: .female, audio: tone, text: "Testing one two three.") else {
            check("a voice cloned on this Mac is saved", false); return
        }
        let info = LocalClones.info(c)
        check("a voice cloned on this Mac is saved and listed first with Qwen3-TTS",
              VoiceCatalog.voices(for: .qwen).first?.voiceID == c.voiceID && VoiceCatalog.info(for: info.choice)?.name == "Test Voice"
              && VoiceCatalog.name(of: info.choice) == "Test Voice")
        check("casting never gives out a voice cloned on this Mac", !ElevenLabs.shared.castable(VoiceCatalog.voices(for: .qwen)).contains { $0.voiceID == c.voiceID })
        check("its clip and words are read back", (try? store.sample(for: c.voiceID))?.text == "Testing one two three.")
        store.rename(c.id, to: "Renamed")
        check("renaming a cloned voice changes its name everywhere", VoiceCatalog.name(of: info.choice) == "Renamed")
        // A clip cut at a pause, from a long recording, with the words it says.
        let clip = CloneAudio.trim([Float](repeating: 0, count: Int(rate)) + tone + [Float](repeating: 0, count: Int(rate)))
        check("silence before and after a recording is trimmed", abs(Double(clip.count) / rate - 6.2) < 0.1, String(format: "%.2f s", Double(clip.count) / rate))

        if ProcessInfo.processInfo.environment["APP_SELFTEST_VOICES"] != nil, ModelSpeech.isInstalled(.qwen),
           let apple = VoiceCatalog.appleVoices().first(where: { $0.quality >= 2 }) ?? VoiceCatalog.appleVoices().first {
            // The "recording": an Apple voice reading two sentences, written to a file.
            let said = "The train was late again, so I walked. It was the kind of autumn morning that makes you forgive the whole year."
            do {
                let source = try await SpeechRenderer.shared.render(SpeechRequest(text: said, choice: apple.choice, tone: .neutral, kind: .spoken))
                let file = FileManager.default.temporaryDirectory.appending(path: "InnerVoice-test-recording.caf")
                SpeechRenderer.write(source, to: file)
                let audio = try CloneAudio.load([file])
                var words = said
                if ProcessInfo.processInfo.environment["APP_SELFTEST_TRANSCRIBE"] != nil {
                    let found = await CloneAudio.transcribe(audio)
                    check("the words in a recording are found on this Mac", found.map { $0.text.lowercased().contains("autumn morning") } == true, found?.text ?? "none")
                    if let found, let span = CloneClip.span(found.words) { words = CloneClip.text(found.text, upTo: span.textEnd) }
                }
                let made = try store.add(name: "Apple Reader", gender: apple.gender, audio: ModelSpeech.level(audio), text: words)
                let start = Date()
                let line = try await SpeechRenderer.shared.render(SpeechRequest(text: "I told you the tide would turn before morning, and it did.",
                                                                                choice: LocalClones.info(made).choice, tone: .neutral, kind: .spoken))
                let seconds = Double(line.frameLength) / line.format.sampleRate
                check("a voice cloned on this Mac speaks a line", seconds > 1.5 && seconds < 20,
                      String(format: "%.1f s of audio in %.1f s", seconds, Date().timeIntervalSince(start)))
                if let out = ProcessInfo.processInfo.environment["APP_SELFTEST_AUDIO_OUT"].map({ URL(fileURLWithPath: $0) }) {
                    SpeechRenderer.write(source, to: out.appending(path: "clone-source.caf"))
                    SpeechRenderer.write(line, to: out.appending(path: "clone-line.caf"))
                }
                store.delete(made.id)
                try? FileManager.default.removeItem(at: file)
            } catch {
                check("a voice cloned on this Mac speaks a line", false, error.localizedDescription)
            }
        }
        store.delete(c.id)
        check("deleting a cloned voice removes it and its clip", !store.clones.contains { $0.id == c.id } && (try? store.sample(for: c.voiceID)) == nil
              && VoiceCatalog.name(of: info.choice) == "Missing voice")
    }

    private static func voiceCloning(_ doc: InnerVoiceDocument, undo: UndoManager) async {
        let eleven = ElevenLabs.shared
        check("Clone a Voice is offered to a book that uses ElevenLabs", ElevenLabs.cloningOffered(for: doc.project))
        doc.setVoiceEngine(.apple, undoManager: undo)
        let offeredOnApple = ElevenLabs.cloningOffered(for: doc.project)
        doc.setVoiceEngine(.elevenlabs, undoManager: undo)
        let notAllowed = InnerVoiceDocument(project: SeedData.project())
        notAllowed.setVoiceEngine(.elevenlabs, undoManager: nil)
        check("Clone a Voice isn't offered to a book on another engine, or one that hasn't allowed ElevenLabs",
              !offeredOnApple && !ElevenLabs.cloningOffered(for: notAllowed.project))

        let allowance = try? await eleven.cloneAllowance()
        check("the plan's voice slots are read", allowance == ElevenLabs.CloneAllowance(canClone: true, slotsUsed: 3, slotLimit: 10, tier: "creator"))

        let sample = FileManager.default.temporaryDirectory.appending(path: "InnerVoice-selftest-sample-\(UUID().uuidString).m4a")
        try? Data("fake audio for the self-test".utf8).write(to: sample)
        defer { try? FileManager.default.removeItem(at: sample) }
        FakeElevenLabs.cloneRequests = []
        let refused = try? await eleven.cloneVoice(name: "Ellen", samples: [sample], removeBackgroundNoise: false, labels: [:], rightsConfirmed: false)
        check("nothing is sent until the right to clone is confirmed", refused == nil && FakeElevenLabs.cloneRequests.isEmpty)

        do {
            let v = try await eleven.cloneVoice(name: "Ellen, warm and low", samples: [sample], removeBackgroundNoise: false, labels: [:], rightsConfirmed: true)
            let sent = FakeElevenLabs.cloneRequests.first ?? ""
            let bookText = doc.project.lines.prefix(20).map { doc.project.text(of: $0, index: doc.index) }.filter { $0.count > 12 }
            check("cloning sends the name and the recording", sent.contains("name=\"name\"\r\n\r\nEllen, warm and low") && sent.contains("fake audio for the self-test")
                  && sent.contains("filename=\"\(sample.lastPathComponent)\"") && sent.contains("Content-Type: audio/mp4"), String(sent.prefix(200)))
            check("cloning sends no book text", !bookText.isEmpty && !bookText.contains { sent.contains($0) })
            check("the clone is saved under its name and listed first", v.voiceID == "clone1" && v.name == "Ellen, warm and low"
                  && eleven.clonedIDs.contains("clone1") && eleven.voices.prefix(2).allSatisfy { eleven.clonedIDs.contains($0.voiceID) })
            check("other projects see the clone (it's in the account's voice list)", VoiceCatalog.voices(for: .elevenlabs).contains { $0.voiceID == "clone1" })
            doc.castVoicesAgain(undoManager: undo)
            let given = doc.project.characters.compactMap { $0.voice(for: .elevenlabs)?.choice.voiceID } + [doc.project.settings.narratorVoice(for: .elevenlabs)?.choice.voiceID].compactMap { $0 }
            check("casting never gives out a cloned voice", !given.isEmpty && !given.contains { eleven.clonedIDs.contains($0) }, given.joined(separator: ","))
            if let c = doc.project.characters.first(where: { (doc.index.stats[$0.id]?.lines ?? 0) > 0 }) {
                doc.setVoice(v.choice, for: c.id, undoManager: undo)
                doc.castVoicesAgain(undoManager: undo)
                check("a clone the narrator gives stays", doc.project.character(c.id)?.voice(for: .elevenlabs)?.choice.voiceID == "clone1")
                undo.undo(); undo.undo()
            }
            try await eleven.renameVoice("clone1", to: "Ellen")
            check("renaming a clone changes its name everywhere", FakeElevenLabs.editRequests.last?.contains("Ellen") == true && eleven.voices.contains { $0.voiceID == "clone1" && $0.name == "Ellen" })
            try await eleven.deleteVoice("clone1")
            check("deleting a clone removes it", FakeElevenLabs.deleted == ["clone1"] && !eleven.voices.contains { $0.voiceID == "clone1" } && !eleven.clonedIDs.contains("clone1"))
        } catch {
            check("cloning a voice works", false, error.localizedDescription)
        }

        // A free plan: said before anything is sent, and in plain words if it's tried anyway.
        FakeElevenLabs.freePlan = true
        defer { FakeElevenLabs.freePlan = false }
        let free = try? await eleven.cloneAllowance()
        check("a free plan can't clone, and the sheet says so up front", free?.canClone == false)
        do {
            _ = try await eleven.cloneVoice(name: "Ellen", samples: [sample], removeBackgroundNoise: false, labels: [:], rightsConfirmed: true)
            check("cloning on a free plan explains the plan", false)
        } catch {
            check("cloning on a free plan explains the plan", error.localizedDescription.contains("paid ElevenLabs plan"), error.localizedDescription)
        }
    }

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
        check("ElevenLabs voices are listed with their labels", voices.count == 3 && voices.contains { $0.gender == .female && $0.accent == "British" })
        check("a voice cloned at elevenlabs.io is marked Cloned and listed first", eleven.clonedIDs == ["webclone"] && voices.first?.voiceID == "webclone" && voices.first?.detail == "Cloned")
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
        // Voice Design: a character's Voice Notes become a voice of their own, only when allowed.
        if var c = speaking.first {
            c.voiceNotes = "Husky and unhurried, a smoker's rasp. Drops her voice when she lies."
            let description = VoiceDescription.design(base: "A woman with a low voice.", character: c, limit: ElevenLabs.designLimit)
            let designed = VoiceResolver.withNotes(VoiceChoice(engine: .qwen, voiceID: "design", design: "A woman."), c)
            let preset = VoiceInfo.qwenPresets[0].choice
            check("Qwen3-TTS designs from the Voice Notes too (a ready-made voice stays as it is)",
                  designed.design?.contains("smoker's rasp") == true && VoiceResolver.withNotes(preset, c) == preset, designed.design ?? "")
            let blocked = try? await eleven.designVoice(name: "Test", description: description, labels: [:], allowed: false)
            check("Voice Design sends nothing for a book that hasn't allowed ElevenLabs", blocked == nil && FakeElevenLabs.designRequests.isEmpty)
            do {
                let v = try await eleven.designVoice(name: c.name + " (InnerVoice)", description: description, labels: ["gender": "female"], allowed: true)
                let sent = FakeElevenLabs.designRequests.first?["voice_description"] as? String ?? ""
                check("Voice Design sends the character's Voice Notes to ElevenLabs", sent.contains("smoker's rasp") && FakeElevenLabs.designRequests.first?["model_id"] as? String == ElevenLabs.designModel, sent)
                check("the designed voice is saved and listed", v.voiceID == "designed1" && FakeElevenLabs.designRequests.count == 2
                      && eleven.voices.contains { $0.voiceID == "designed1" && $0.gender == .female })
            } catch {
                check("Voice Design sends the character's Voice Notes to ElevenLabs", false, error.localizedDescription)
            }
        }
        await voiceCloning(doc, undo: undo)
        // Errors say what to do.
        FakeElevenLabs.status = 401
        SpeechRenderer.shared.clearCache()
        if let seg = lines.last, let req = r.request(for: seg) {
            do { _ = try await SpeechRenderer.shared.render(req); check("a wrong key says so", false) } catch {
                check("a wrong key says so", error.localizedDescription.contains("API key"), error.localizedDescription)
            }
        }
        // A 422 says why, in ElevenLabs' words.
        FakeElevenLabs.status = 422
        SpeechRenderer.shared.clearCache()
        if let seg = lines.last, let req = r.request(for: seg) {
            do { _ = try await SpeechRenderer.shared.render(req); check("a 422 says why", false) } catch {
                check("a 422 says why", error.localizedDescription.contains("seed: Input should be"), error.localizedDescription)
            }
        }
        FakeElevenLabs.status = 200
        // Seeds stay under 2^31, and text with nothing to say isn't sent.
        let seedsOK = ["a", "Mara", "The lamp turned slowly", "x\u{1F}y"].allSatisfy { (0..<2_147_483_647).contains(ElevenLabs.seed($0)) }
        let sentBefore = FakeElevenLabs.speechRequests.count
        if let v = voices.first {
            _ = try? await ElevenLabs.shared.render(SpeechRequest(text: "…", choice: v.choice, tone: .neutral, kind: .spoken, cloudAllowed: true), allowed: true)
        }
        check("seeds fit ElevenLabs, and “…” alone isn't sent", seedsOK && FakeElevenLabs.speechRequests.count == sentBefore)
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
            await deleteTextInTheView()
            await marginClickInTheView()
            // Steps: the sample is tagged and summarized with lines to review, so Review is next;
            // skipping it moves on, and Undo brings it back.
            let steps = GuidedStepsBar.steps(doc)
            check("the next step on the sample is Review", steps.current?.step == .review, "\(steps.current?.step.rawValue ?? "none")")
            doc.updateSettings("Skip Step", undoManager: undo) { $0.markStep(.review, done: true) }
            let after = GuidedStepsBar.steps(doc)
            check("skipping a step moves to the next one and says Skipped",
                  after.current?.step == .voices && after.all.first { $0.step == .review }?.isSkipped == true)
            undo.undo()
            check("Undo brings a skipped step back", GuidedStepsBar.steps(doc).current?.step == .review)
            let card = GuidedStepsBar.card(for: .review, steps: steps, document: doc, page: .manuscript)
            check("the Review step offers Start Review", card.action?.title == "Start Review" && card.title.contains("3 lines"), card.title)
            // Colors: Automatic by default, from each character's gender; a picked color stays.
            if let tom = doc.project.characters.first(where: { $0.name == "Tom Hale" }),
               let mara = doc.project.characters.first(where: { $0.name == "Mara Quinn" }) {
                check("automatic colors: blue-green for Tom, pink-purple for Mara",
                      tom.colorIsAutomatic && CharacterPalette.family(of: tom.colorHex) == .masculine
                      && CharacterPalette.family(of: mara.colorHex) == .feminine, "\(tom.colorHex) \(mara.colorHex)")
                doc.setColor(tom.id, hex: "#F59E0B", undoManager: undo)
                let picked = doc.project.character(tom.id)!
                check("a picked color sticks", picked.colorHex == "#F59E0B" && !picked.colorIsAutomatic)
                var p = doc.project
                CharacterPalette.recolorAutomatic(&p, index: doc.index)
                check("recoloring leaves a picked color alone", p.character(tom.id)?.colorHex == "#F59E0B")
                doc.setColor(tom.id, hex: nil, undoManager: undo)
                let back = doc.project.character(tom.id)!
                check("Automatic gives Tom a masculine color again", back.colorIsAutomatic && CharacterPalette.family(of: back.colorHex) == .masculine, back.colorHex)
                undo.undo(); undo.undo()
                check("Undo restores the color", doc.project.character(tom.id)?.colorHex == tom.colorHex && doc.project.character(tom.id)?.colorChosen == tom.colorChosen)
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
        if ProcessInfo.processInfo.environment["APP_SELFTEST_VOICES"] != nil {
            await modelVoices(doc, undo: undo)
            await oneVoiceTableRead(doc, undo: undo)
            if let dir = ProcessInfo.processInfo.environment["APP_MAKE_CASTING_SAMPLES"] { await castingSamples(doc, undo: undo, to: URL(fileURLWithPath: dir)) }
        }
        if ProcessInfo.processInfo.environment["APP_SELFTEST_DOWNLOAD"] != nil { await voiceDownload() }
        await elevenLabs(doc, undo: undo)
        await localClones(doc, undo: undo)
        redrawCost(doc)
        seriesVoices(doc, undo: undo)

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

        // Deleting a chapter while Tag Speakers runs: the run used to keep chapter positions
        // and crashed past the end of the list (0.2.5). Now it skips the chapter that's gone.
        var shorter = SeedData.project()
        for i in shorter.lines.indices where shorter.lines[i].source == .ai {
            shorter.lines[i].source = .none
            shorter.lines[i].speakerID = nil
        }
        let cutDoc = InnerVoiceDocument(project: shorter)
        let chapterCount = cutDoc.project.chapters.count
        cutDoc.tagging.tagSpeakers(undoManager: nil)
        while cutDoc.tagging.isRunning, cutDoc.tagging.status?.message.hasPrefix("Tagging") != true {
            try? await Task.sleep(for: .milliseconds(20))
        }
        if let last = cutDoc.project.chapters.last?.id, chapterCount > 1 {
            cutDoc.change("Delete Chapter", undoManager: nil) { $0.deleteText(cuts: [:], chapters: [last]) }
        }
        while cutDoc.tagging.isRunning { try? await Task.sleep(for: .milliseconds(200)) }
        check("Tag Speakers survives a chapter deleted mid-run",
              cutDoc.tagging.failure == nil && cutDoc.project.chapters.count == chapterCount - 1,
              "\(chapterCount) → \(cutDoc.project.chapters.count) chapters; \(cutDoc.tagging.finished ?? cutDoc.tagging.failure ?? "")")

        // Build Summaries for the whole cast: both bars move, voice notes are written, one Undo.
        let undo = UndoManager()
        let notesBefore = doc.project.characters.map(\.voiceNotes)
        var sawCount = 0, sawCharacterMid = false, sawOverall: Set<Int> = [], sawBriefs = false
        doc.tagging.buildSummaries(undoManager: undo)
        let summariesStart = Date()
        while doc.tagging.isRunning {
            if let s = doc.tagging.status, s.job == .summaries {
                sawCount = max(sawCount, s.characterCount)
                if s.characterProgress > 0, s.characterProgress < 1 { sawCharacterMid = true }
                sawOverall.insert(Int(s.progress * 10))
                if s.phase == "Writing voice briefs" { sawBriefs = true }
            }
            try? await Task.sleep(for: .milliseconds(100))
        }
        print(String(format: "LIVE summaries in %.0f s: %@", Date().timeIntervalSince(summariesStart), doc.tagging.finished ?? doc.tagging.failure ?? ""))
        check("Build Summaries shows a bar for the run and for each character", sawCount > 1 && sawCharacterMid && sawOverall.count > 2,
              "characters \(sawCount), overall steps \(sawOverall.count)")
        let speakingNow = doc.project.characters.filter { (doc.index.stats[$0.id]?.lines ?? 0) > 0 }
        let briefed = speakingNow.filter { $0.voiceNotes.contains(VoiceBrief.heading) }
        check("Build Summaries writes a voice brief for each speaking character", sawBriefs && briefed.count == speakingNow.count,
              "\(briefed.count) of \(speakingNow.count)")
        let contrasts = briefed.filter { c in
            VoiceBriefPrompt.partners(of: c, project: doc.project, index: doc.index).contains { c.voiceNotes.contains("Contrast:") && c.voiceNotes.contains($0.firstName) }
        }
        check("voice briefs set characters apart from their scene partners by name", contrasts.count >= max(1, briefed.count / 2),
              "\(contrasts.count) of \(briefed.count)")
        let withNotes = doc.project.characters.filter { $0.voiceNotes.contains(VoiceNotesFromBook.heading) }
        check("Build Summaries writes voice notes from the book", !withNotes.isEmpty, "\(withNotes.count) characters")
        for c in speakingNow { print("NOTES \(c.name):\n\(c.voiceNotes)") }
        undo.undo()
        check("one Undo takes the facts and voice notes back", doc.project.characters.map(\.voiceNotes) == notesBefore)
        manager.unloadNow()
    }
}


/// A stand-in for api.elevenlabs.io in the self-test: a plan, two voices, and half a
/// second of tone for each speech request (whose JSON body is kept for checking).
nonisolated final class FakeElevenLabs: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var speechRequests: [[String: Any]] = []
    nonisolated(unsafe) static var designRequests: [[String: Any]] = []
    nonisolated(unsafe) static var status = 200
    nonisolated(unsafe) static var cloneRequests: [String] = []
    nonisolated(unsafe) static var editRequests: [String] = []
    nonisolated(unsafe) static var deleted: [String] = []
    nonisolated(unsafe) static var freePlan = false

    static func reset() { speechRequests = []; designRequests = []; status = 200; cloneRequests = []; editRequests = []; deleted = []; freePlan = false }

    private func rawBody() -> String {
        var data = request.httpBody ?? Data()
        if data.isEmpty, let stream = request.httpBodyStream {
            stream.open()
            var buf = [UInt8](repeating: 0, count: 65536)
            while stream.hasBytesAvailable { let n = stream.read(&buf, maxLength: buf.count); if n <= 0 { break }; data.append(buf, count: n) }
            stream.close()
        }
        return String(decoding: data, as: UTF8.self)
    }

    private func bodyJSON() -> [String: Any] {
        var data = request.httpBody ?? Data()
        if data.isEmpty, let stream = request.httpBodyStream {
            stream.open()
            var buf = [UInt8](repeating: 0, count: 65536)
            while stream.hasBytesAvailable { let n = stream.read(&buf, maxLength: buf.count); if n <= 0 { break }; data.append(buf, count: n) }
            stream.close()
        }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
    }

    override class func canInit(with request: URLRequest) -> Bool { request.url?.host == "api.elevenlabs.io" }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let url = request.url!
        var body = Data()
        var code = Self.status
        if url.path == "/v1/text-to-voice/design" {
            Self.designRequests.append(bodyJSON())
            body = Data(#"{"previews":[{"audio_base_64":"","generated_voice_id":"gen1","media_type":"audio/mpeg","duration_secs":1}],"text":"A sample."}"#.utf8)
        } else if url.path == "/v1/text-to-voice" {
            Self.designRequests.append(bodyJSON())
            body = Data(#"{"voice_id":"designed1","name":"Designed"}"#.utf8)
        } else if url.path.hasPrefix("/v1/text-to-speech/") {
            Self.speechRequests.append(bodyJSON())
            if code == 200 {
                var pcm = Data()
                for i in 0..<12_000 { var v = Int16(sin(Double(i) / 24_000 * 2 * .pi * 220) * 8000).littleEndian; pcm.append(Data(bytes: &v, count: 2)) }
                body = pcm
            } else if code == 422 {
                body = Data(#"{"detail":[{"loc":["body","seed"],"msg":"Input should be less than or equal to 2147483647","type":"less_than_equal"}]}"#.utf8)
            } else {
                body = Data(#"{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}"#.utf8)
            }
        } else if url.path == "/v1/voices/add" {
            Self.cloneRequests.append(rawBody())
            if Self.freePlan {
                code = 403
                body = Data(#"{"detail":{"status":"can_not_use_instant_voice_cloning","message":"Your subscription does not include instant voice cloning."}}"#.utf8)
            } else {
                body = Data(#"{"voice_id":"clone1","requires_verification":false}"#.utf8)
            }
        } else if url.path.hasPrefix("/v1/voices/"), url.path.hasSuffix("/edit") {
            Self.editRequests.append(rawBody())
            body = Data(#"{"status":"ok"}"#.utf8)
        } else if url.path.hasPrefix("/v1/voices/"), request.httpMethod == "DELETE" {
            Self.deleted.append(url.lastPathComponent)
            body = Data(#"{"status":"ok"}"#.utf8)
        } else if url.path == "/v1/user/subscription" {
            body = Data((Self.freePlan
                ? #"{"tier":"free","character_count":1200,"character_limit":10000,"can_use_instant_voice_cloning":false,"voice_limit":3,"voice_slots_used":0}"#
                : #"{"tier":"creator","character_count":1200,"character_limit":100000,"can_use_instant_voice_cloning":true,"voice_limit":10,"voice_slots_used":3}"#).utf8)
        } else if url.path == "/v2/voices" {
            body = Data(#"{"voices":[{"voice_id":"v1","name":"Alice - clear narrator","labels":{"gender":"female","age":"middle_aged","accent":"british","description":"clear"}},{"voice_id":"v2","name":"Bram","labels":{"gender":"male","age":"old","accent":"american"}},{"voice_id":"webclone","name":"Web Clone","category":"cloned","labels":{}}],"has_more":false}"#.utf8)
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
