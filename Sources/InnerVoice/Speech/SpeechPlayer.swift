@preconcurrency import AVFoundation
import InnerVoiceKit
import Observation

/// Which voice says each segment of a project, on the project's engine: the character's
/// cast voice, or the narrator's for narration and lines nobody is cast for.
struct VoiceResolver {
    var engine: VoiceEngine
    var characters: [UUID: VoiceChoice]
    var narrator: VoiceChoice?

    @MainActor
    init(project: Project, engine: VoiceEngine? = nil) {
        let e = engine ?? project.settings.engine
        self.engine = e
        var map: [UUID: VoiceChoice] = [:]
        for c in project.characters { if let v = c.voice(for: e) { map[c.id] = v.choice } }
        characters = map
        narrator = project.settings.narratorVoice(for: e)?.choice ?? Self.fallback(e)
    }

    /// A voice when nothing is cast yet: the best Apple voice, Orpheus's first, or a plain
    /// Qwen3-TTS design.
    @MainActor
    static func fallback(_ engine: VoiceEngine) -> VoiceChoice? {
        switch engine {
        case .apple: VoiceCatalog.appleVoices().first.map { VoiceChoice(engine: .apple, voiceID: $0.voiceID) }
        case .orpheus: VoiceChoice(engine: .orpheus, voiceID: "tara")
        case .qwen: VoiceChoice(engine: .qwen, voiceID: "design", design: "A calm, clear audiobook narrator with a warm, measured voice.")
        }
    }

    func choice(for segment: SpeechSegment) -> VoiceChoice? {
        segment.speakerID.flatMap { characters[$0] } ?? narrator
    }

    func request(for segment: SpeechSegment) -> SpeechRequest? {
        guard let choice = choice(for: segment) else { return nil }
        return SpeechRequest(text: segment.text, choice: choice, tone: segment.speakerID == nil && segment.lineID == nil ? .neutral : segment.tone,
                             kind: segment.lineID == nil ? nil : segment.kind)
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
    var failure: String?

    @ObservationIgnored private let engine = AVAudioEngine()
    @ObservationIgnored private let node = AVAudioPlayerNode()
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var started = false

    private init() {
        engine.attach(node)
        engine.connect(node, to: engine.mainMixerNode, format: SpeechAudio.format)
        // Test runs play silently (snapshots open Rehearse, which speaks its cue).
        if SystemHooks.isTestRun, ProcessInfo.processInfo.environment["INNERVOICE_SPEECH_AUDIBLE"] == nil {
            engine.mainMixerNode.outputVolume = 0
        }
    }

    /// Plays the segments. `onSegment` hears each one as it starts (to follow along in the
    /// manuscript).
    func play(_ segments: [SpeechSegment], resolver: VoiceResolver, onSegment: ((SpeechSegment) -> Void)? = nil) {
        stop()
        guard !segments.isEmpty else { return }
        failure = nil
        isPlaying = true
        preparing = true
        task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.isPlaying = false
                self.preparing = false
                self.current = nil
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
                    let buffer = try await made[i]!.value.buffer
                    made[i] = nil
                    try Task.checkCancellation()
                    self.preparing = false
                    let seg = segments[i]
                    self.current = seg
                    self.lastStarted = seg
                    onSegment?(seg)
                    await self.node.scheduleBuffer(buffer, completionCallbackType: .dataPlayedBack)
                    try Task.checkCancellation()
                    if seg.pauseAfter > 0 {
                        await self.node.scheduleBuffer(SpeechAudio.silence(seg.pauseAfter), completionCallbackType: .dataPlayedBack)
                    }
                }
            } catch is CancellationError {
            } catch {
                if !Task.isCancelled { self.failure = error.localizedDescription }
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
    static func audition(_ choice: VoiceChoice, character: BookCharacter?, document: InnerVoiceDocument, tone: Tone? = nil) {
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
        let seg = SpeechSegment(id: "audition", paragraphID: UUID(), lineID: character == nil ? nil : UUID(), speakerID: nil,
                                text: SpeechScript.apply([], to: text), tone: sampleTone, kind: .spoken, pauseAfter: 0)
        var resolver = VoiceResolver(project: project, engine: choice.engine)
        resolver.narrator = choice
        resolver.characters = [:]
        SpeechPlayer.shared.play([seg], resolver: resolver)
    }
}
