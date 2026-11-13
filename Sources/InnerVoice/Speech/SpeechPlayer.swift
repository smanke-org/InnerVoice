@preconcurrency import AVFoundation
import InnerVoiceKit
import Observation

/// Which voice says each segment of a project, on the project's engine: the character's
/// cast voice, or the narrator's for narration and lines nobody is cast for.
struct VoiceResolver {
    var engine: VoiceEngine
    var characters: [UUID: VoiceChoice]
    var narrator: VoiceChoice?
    /// Engines that can speak now (on and downloaded).
    var usable: Set<VoiceEngine>

    /// `index`: the book's index if the caller has it (the window does); otherwise one is
    /// made only when the Casting Style sorts characters by gender.
    @MainActor
    init(project: Project, engine: VoiceEngine? = nil, index: BookIndex? = nil) {
        let e = engine ?? project.engineInUse
        self.engine = e
        // ElevenLabs only for a book that allows its text to be sent.
        let usable = Set(VoiceEngine.engines.filter { EngineSwitches.shared.usable($0) && (!$0.isCloud || project.settings.cloudVoicesAllowed != nil) })
        var map: [UUID: VoiceChoice] = [:]
        for c in project.characters {
            if let v = c.voice(for: e)?.choice { map[c.id] = Self.withNotes(v, c) }
        }
        // Casting Style (docs/CASTING-STYLE.md): a character read by the narrator or a shared
        // voice (Men, Women, Other) speaks in that voice; Own Voice keeps their own.
        var roleChoices: [VoiceRole: VoiceChoice] = [:]
        for r in VoiceRole.sharedRoles {
            if let v = project.settings.roleVoice(r, for: e)?.choice, usable.contains(v.engine) { roleChoices[r] = v }
        }
        let readers = project.settings.castingStyle == nil ? [:] : CastingStyleRules.readers(project, index: index)
        if project.settings.castingStyle != nil {
            for (id, r) in readers {
                switch r {
                case .own: break
                case .narrator: map[id] = nil
                case .men, .women, .other: map[id] = roleChoices[r]
                }
            }
        }
        characters = map
        cloudAllowed = project.settings.cloudVoicesAllowed != nil
        usageBook = project.usageKey
        self.usable = usable
        writtenByNarrator = UserDefaults.standard.object(forKey: "voices.writtenByNarrator") as? Bool ?? true
        // The narrator's voice: one the narrator chose; else, in a first-person book, the
        // POV character's; else the one casting picked; else any voice that works.
        let n = project.settings.narratorVoice(for: e)
        if let n, n.source == .user, usable.contains(n.choice.engine) {
            narrator = n.choice
        } else if let pov = Self.povNarrator(project: project, engine: e), let v = map[pov.id] {
            narrator = v
        } else if let n, usable.contains(n.choice.engine) {
            narrator = n.choice
        } else {
            narrator = Self.fallback(e) ?? Self.fallback(.apple)
        }
        // A two-person book: the men's or women's voice reads the narration too.
        let narrationReader = CastingStyleRules.reader(of: .narrator, settings: project.settings)
        if narrationReader != .narrator, let v = roleChoices[narrationReader] { narrator = v }
        // The narrator playing a character shades their voice for them (One Voice, and anyone
        // else the Casting Style gives the narrator): a man a little lower, a woman higher…
        if readers.values.contains(.narrator) {
            // The narrator's own gender: from the voice list, or a designed voice's description.
            let narratorGender = narrator.flatMap { n in VoiceCatalog.info(for: n)?.gender ?? n.design.flatMap(VoiceCasting.gender(ofDescription:)) }
                .flatMap { $0 == .neutral ? nil : CharacterGender($0) }
            shades = CastingStyleRules.shades(project, index: index ?? BookIndex(project), narratorGender: narratorGender)
        }
    }

    /// Casting Style shades for characters the narrator plays, by character.
    var shades: [UUID: Shade] = [:]
    /// An audition of a shade (Voices page): every line of it is said with this shade.
    var auditionShade: Shade?

    /// The shade a segment is said with: the speaker's, when the narrator's voice says their
    /// spoken or thought line (not narration, and not letters the narrator reads out).
    func shade(for segment: SpeechSegment, choice: VoiceChoice) -> Shade? {
        guard segment.lineID != nil, segment.kind != .written else { return nil }
        if let auditionShade { return auditionShade }
        guard let id = segment.speakerID, choice == narrator else { return nil }
        return shades[id]
    }

    /// Qwen3-TTS designs a voice from a description: the character's Voice Notes go with it,
    /// so what the narrator writes there shapes how they sound. (ElevenLabs gets the notes
    /// once, when it designs a voice for them; other engines can't take a description.)
    static func withNotes(_ v: VoiceChoice, _ c: BookCharacter?) -> VoiceChoice {
        guard let c, v.engine == .qwen, v.voiceID == "design" else { return v }
        var out = v
        out.design = VoiceDescription.design(base: v.design ?? "", character: c)
        return out
    }

    /// The book allows its text to go to ElevenLabs.
    var cloudAllowed = false
    var usageBook = ""

    /// Whether letters, signs and other written text are read by the narrator
    /// (Settings › Voices) rather than the character who wrote them.
    var writtenByNarrator = true

    /// The character whose voice reads the narration of a first-person book: the one marked
    /// as the book's narrator, unless the narrator has a voice of their own choosing.
    @MainActor
    static func povNarrator(project: Project, engine: VoiceEngine? = nil) -> BookCharacter? {
        let e = engine ?? project.engineInUse
        if project.settings.narratorVoice(for: e)?.source == .user { return nil }
        guard let pov = project.character(project.settings.povCharacterID), let v = pov.voice(for: e)?.choice,
              !v.isNarrator, EngineSwitches.shared.usable(v.engine), !v.engine.isCloud || project.settings.cloudVoicesAllowed != nil else { return nil }
        return pov
    }

    /// A voice when nothing is cast yet: the best Apple voice, Orpheus's first, or a plain
    /// Qwen3-TTS design.
    @MainActor
    static func fallback(_ engine: VoiceEngine) -> VoiceChoice? {
        switch engine {
        case .apple: VoiceCatalog.appleVoices().first.map { VoiceChoice(engine: .apple, voiceID: $0.voiceID) }
        case .orpheus: VoiceChoice(engine: .orpheus, voiceID: "tara")
        case .qwen: VoiceInfo.qwenPresets.first?.choice
        case .elevenlabs: ElevenLabs.shared.voices.first?.choice
        case .mixed: EngineSwitches.shared.enabled.first.flatMap { $0 == .mixed ? nil : fallback($0) }
        }
    }

    func choice(for segment: SpeechSegment) -> VoiceChoice? {
        if segment.lineID != nil, segment.kind == .written, writtenByNarrator { return narrator }
        // "Same as Narrator", and a voice from an engine that's turned off or not downloaded,
        // use the narrator's voice.
        if let id = segment.speakerID, let c = characters[id], !c.isNarrator, usable.contains(c.engine) { return c }
        return narrator
    }

    func request(for segment: SpeechSegment) -> SpeechRequest? {
        guard let choice = choice(for: segment) else { return nil }
        return SpeechRequest(text: segment.text, choice: choice, tone: segment.speakerID == nil && segment.lineID == nil ? .neutral : segment.tone,
                             kind: segment.lineID == nil ? nil : segment.kind, sound: segment.sound, cloudAllowed: cloudAllowed,
                             shade: shade(for: segment, choice: choice),
                             // Delivery hints are ElevenLabs' audio tags: never sent to another engine.
                             delivery: choice.engine == .elevenlabs && segment.lineID != nil ? segment.delivery : nil,
                             usageBook: usageBook)
    }
}

/// Plays segments one after another, making the next ones while the current one plays.
/// One player for the whole app: starting something new stops what was playing.
@Observable
@MainActor
final class SpeechPlayer {
    static let shared = SpeechPlayer()

    private(set) var current: SpeechSegment?
    /// The last segment that started playing (kept after it ends, for tests).
    @ObservationIgnored private(set) var lastStarted: SpeechSegment?
    private(set) var isPlaying = false
    /// "Making Mara’s voice…" while the first audio is being made.
    private(set) var preparing = false
    /// The engine making the first audio (for "Getting Qwen3-TTS ready…").
    private(set) var preparingEngine: VoiceEngine?
    var failure: String?

    @ObservationIgnored private let engine = AVAudioEngine()
    @ObservationIgnored private let node = AVAudioPlayerNode()
    /// Play's speed (Settings › Voices › Pauses), without changing the voices' pitch.
    @ObservationIgnored private let timePitch = AVAudioUnitTimePitch()
    var speed: Double = 1 { didSet { timePitch.rate = Float(min(2, max(0.5, speed))) } }
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var started = false

    private init() {
        engine.attach(node)
        engine.attach(timePitch)
        engine.connect(node, to: timePitch, format: SpeechAudio.format)
        engine.connect(timePitch, to: engine.mainMixerNode, format: SpeechAudio.format)
        speed = PauseStore.shared.speed
        // Test runs play silently (snapshots play lines and voices).
        if SystemHooks.isTestRun, ProcessInfo.processInfo.environment["INNERVOICE_SPEECH_AUDIBLE"] == nil {
            engine.mainMixerNode.outputVolume = 0
        }
    }

    /// Plays the segments. `onSegment` hears each one as it starts (to follow along in the
    /// manuscript).
    func play(_ segments: [SpeechSegment], resolver: VoiceResolver, onSegment: ((SpeechSegment) -> Void)? = nil,
              onFinish: (() -> Void)? = nil) {
        stop()
        guard !segments.isEmpty else { return }
        failure = nil
        isPlaying = true
        preparing = true
        preparingEngine = segments.lazy.compactMap { resolver.request(for: $0)?.choice.engine }.first
        task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.isPlaying = false
                self.preparing = false
                self.current = nil
                // Played to the end (not stopped): Try It in Settings starts over.
                if !Task.isCancelled, self.failure == nil { onFinish?() }
            }
            do {
                try self.startEngine()
                var made: [Int: Task<RenderedAudio, Error>] = [:]
                @MainActor func make(_ i: Int) {
                    guard i < segments.count, made[i] == nil else { return }
                    let seg = segments[i]
                    guard let request = resolver.request(for: seg) else {
                        made[i] = Task { RenderedAudio(buffer: SpeechAudio.silence(0.05)) }
                        return
                    }
                    made[i] = Task { @MainActor in RenderedAudio(buffer: try await SpeechRenderer.shared.render(request)) }
                }
                for i in segments.indices {
                    try Task.checkCancellation()
                    make(i); make(i + 1); make(i + 2)
                    // Without the voice's own leading and trailing silence: the pauses are the narrator's.
                    let buffer = SpeechAudio.trimmed(try await made[i]!.value.buffer)
                    made[i] = nil
                    try Task.checkCancellation()
                    self.preparing = false
                    let seg = segments[i]
                    self.current = seg
                    self.lastStarted = seg
                    onSegment?(seg)
                    await self.node.scheduleBuffer(buffer, completionCallbackType: .dataPlayedBack)
                    if !Task.isCancelled { ActivityStore.shared.record(ActivityKey.secondsPlayed, count: Int((Double(buffer.frameLength) / buffer.format.sampleRate).rounded())) }
                    try Task.checkCancellation()
                    // Looked up now, so a change in Settings › Voices › Pauses is heard on the next gap.
                    let gap = seg.pause == .none ? seg.pauseAfter : PauseSettings.current.seconds(seg.pause)
                    if gap > 0 {
                        await self.node.scheduleBuffer(SpeechAudio.silence(gap), completionCallbackType: .dataPlayedBack)
                    }
                }
            } catch is CancellationError {
            } catch {
                if !Task.isCancelled {
                    self.failure = error.localizedDescription
                    SpeechLog.write("play failed (\(self.preparingEngine?.rawValue ?? "?")): \(error)")
                }
            }
        }
    }

    func stop() {
        task?.cancel()
        task = nil
        if node.isPlaying { node.stop() }
        isPlaying = false
        preparing = false
        current = nil
    }

    private func startEngine() throws {
        if !engine.isRunning {
            engine.prepare()
            try engine.start()
        }
        if !node.isPlaying { node.play() }
    }
}

/// Speech errors, for diagnosing "nothing plays": NSLog plus
/// ~/Library/Logs/InnerVoice/speech.log. Never the book's text.
nonisolated enum SpeechLog {
    static func write(_ message: String) {
        NSLog("InnerVoice speech: %@", message)
        // Test runs (the fake ElevenLabs server's errors) never write the person's log.
        guard !SystemHooks.isTestRun else { return }
        let dir = URL.libraryDirectory.appending(path: "Logs/InnerVoice")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let file = dir.appending(path: "speech.log")
        let line = "\(Date().formatted(.iso8601)) \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?") \(message)\n"
        if let h = try? FileHandle(forWritingTo: file) {
            h.seekToEndOfFile(); h.write(Data(line.utf8)); try? h.close()
        } else {
            try? Data(line.utf8).write(to: file)
        }
    }
}

/// What the Play commands play, from wherever the narrator is.
@MainActor
enum PlayCommand {
    /// Reads narration aloud in table reads (Settings › Voices).
    static var readNarration: Bool {
        UserDefaults.standard.object(forKey: "voices.readNarration") as? Bool ?? true
    }

    /// The selected line, or the paragraph it is in when nothing is selected.
    static func playLine(document: InnerVoiceDocument, workspace: Workspace) {
        VoiceCastingRun.castIfNeeded(document: document)
        let project = document.project
        let index = document.index
        guard let id = workspace.selectedLines.first, let i = index.lineIndexByID[id],
              let p = project.paragraph(project.lines[i].paragraphID, index: index) else { return }
        let segs = SpeechScript.segments(for: p, project: project, index: index, includeNarration: false)
            .filter { workspace.selectedLines.contains($0.lineID ?? UUID()) }
        SpeechPlayer.shared.play(segs, resolver: VoiceResolver(project: project))
    }

    /// From the selected line (or the top of the chapter) to the end of the book, as a
    /// table read: each line in its character's voice, narration as set in Settings.
    static func playFromHere(document: InnerVoiceDocument, workspace: Workspace) {
        VoiceCastingRun.castIfNeeded(document: document)
        let project = document.project
        let index = document.index
        var start: UUID?
        var chapter = min(workspace.visibleChapter, max(0, project.chapters.count - 1))
        if let id = workspace.selectedLines.first, let i = index.lineIndexByID[id], let pos = index.positions[project.lines[i].paragraphID] {
            start = project.lines[i].paragraphID
            chapter = pos.chapter
        }
        guard project.chapters.indices.contains(chapter) else { return }
        var segs = SpeechScript.segments(chapter: chapter, from: start, chapters: project.chapters.count - chapter, project: project,
                                         index: index, includeNarration: readNarration)
        // Start at the selected line, not the narration before it in its paragraph.
        if let id = workspace.selectedLines.first, let k = segs.firstIndex(where: { $0.lineID == id }) { segs.removeFirst(k) }
        SpeechPlayer.shared.play(segs, resolver: VoiceResolver(project: project)) { seg in
            // Follow along in the manuscript.
            if workspace.page == .manuscript, UserDefaults.standard.object(forKey: "voices.followAlong") as? Bool ?? true {
                workspace.selectedLines = seg.lineID.map { [$0] } ?? []
                workspace.reveal = Workspace.Reveal(paragraphID: seg.paragraphID, lineID: seg.lineID)
            }
        }
    }

    /// One sample line in a voice, for choosing voices: the character's own words when
    /// they have lines.
    static func audition(_ choice: VoiceChoice, character: BookCharacter?, document: InnerVoiceDocument, tone: Tone? = nil, shade: Shade? = nil) {
        let project = document.project
        let index = document.index
        var text = "The light was never mine to leave you. It belongs to you now."
        var sampleTone = tone ?? .neutral
        if let c = character {
            // A line of theirs of a good length, said plainly unless asked otherwise.
            let theirs = project.lines.filter { $0.speakerID == c.id && $0.kind == .spoken }
            let pick = theirs.first { l in
                let n = Words.count(project.text(of: l, index: index))
                return n >= 6 && n <= 30 && (tone == nil ? (l.tone ?? .neutral) == .neutral : true)
            } ?? theirs.first
            if let pick {
                text = project.text(of: pick, index: index)
                if tone == nil { sampleTone = pick.tone ?? .neutral }
            } else {
                text = "My name is \(c.firstName)."
            }
        } else {
            text = "Chapter One. The storm came in off the sea just after dark."
        }
        var seg = SpeechSegment(id: "audition", paragraphID: UUID(), lineID: character == nil ? nil : UUID(), speakerID: nil,
                                text: SpeechScript.apply([], to: text), tone: sampleTone, kind: .spoken, pauseAfter: 0)
        seg.pause = .none
        var resolver = VoiceResolver(project: project, engine: choice.isNarrator ? nil : choice.engine, index: index)
        if !choice.isNarrator { resolver.narrator = VoiceResolver.withNotes(choice, character) }
        resolver.auditionShade = shade
        resolver.characters = [:]
        SpeechPlayer.shared.play([seg], resolver: resolver)
    }

    /// A Pronunciations entry said in the book's narrator voice, the way a line would be:
    /// the sentence where it's first used (with every entry applied), or the word alone.
    static func hear(_ p: Pronunciation, document: InnerVoiceDocument) {
        let project = document.project
        let index = document.index
        var text = p.term
        if let pid = p.firstParagraphID, let para = project.paragraph(pid, index: index) {
            let ns = para.text as NSString
            let hit = ns.range(of: p.term, options: p.matchCase ? [] : [.caseInsensitive])
            if hit.location != NSNotFound {
                var sentence = ns.range(of: ".", options: .backwards, range: NSRange(location: 0, length: hit.location))
                let start = sentence.location == NSNotFound ? 0 : sentence.location + 1
                sentence = ns.range(of: ".", range: NSRange(location: NSMaxRange(hit), length: ns.length - NSMaxRange(hit)))
                let end = sentence.location == NSNotFound ? ns.length : NSMaxRange(sentence)
                let s = ns.substring(with: NSRange(location: start, length: end - start)).trimmingCharacters(in: .whitespacesAndNewlines)
                if Words.count(s) <= 40 { text = s }
            }
        }
        var sayings = SpeechScript.sayings(project)
        if !sayings.contains(where: { $0.term == p.term }), !p.sayItAs.isEmpty {
            sayings.insert(.init(p.term, p.sayItAs, matchCase: p.matchCase), at: 0)
        }
        var seg = SpeechSegment(id: "pronunciation", paragraphID: UUID(), lineID: nil, speakerID: nil,
                                text: SpeechScript.apply(sayings, to: text), tone: .neutral, kind: nil, pauseAfter: 0)
        seg.pause = .none
        var resolver = VoiceResolver(project: project, engine: nil, index: index)
        resolver.characters = [:]
        SpeechPlayer.shared.play([seg], resolver: resolver)
    }
}
