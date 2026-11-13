@preconcurrency import AVFoundation
import CryptoKit
import InnerVoiceKit
import MLX
import MLXAudioCodecs
import MLXAudioCore
import MLXAudioTTS
import NaturalLanguage
import Observation

/// Chatterbox and Orpheus, on Apple's MLX (Apple silicon only).
///
/// Orpheus has eight fixed voices and acts from the words; the speech tag's sighs, laughs
/// and gasps are added as its sound tags.
///
/// Chatterbox (Resemble AI, MIT) clones every voice from a short clip: a stock voice's clip
/// ships with the app (`ChatterboxClips`), and a voice cloned on this Mac uses the
/// narrator's recording. The tone sets its emotion exaggeration
/// (`ChatterboxDelivery`). A shade, or casting's age pitch, shifts the clip before cloning,
/// so the voice stays the same person.
enum ModelSpeech {
    @MainActor
    static func isInstalled(_ engine: VoiceEngine) -> Bool { VoiceModelManager.shared.isInstalled(engine) }

    @MainActor
    static func render(_ request: SpeechRequest) async throws -> AVAudioPCMBuffer {
        let engine = request.choice.engine
        guard isInstalled(engine) else { throw SpeechError.engineUnavailable(engine) }
        // The models stop after a dozen seconds or so: long paragraphs go a few sentences at a time.
        let pieces = chunks(request.text)
        var samples: [Float] = []
        let gap = [Float](repeating: 0, count: Int(SpeechAudio.format.sampleRate * 0.18))
        var volume: Float = request.kind == .thought ? 0.85 : 1
        for (n, piece) in pieces.enumerated() {
            try Task.checkCancellation()
            if n > 0 { samples += gap }
            switch engine {
            case .apple, .mixed, .elevenlabs: throw SpeechError.engineUnavailable(engine)
            case .orpheus:
                var r = request
                r.text = piece
                if n > 0 { r.sound = nil }
                samples += try await MLXSpeech.shared.orpheus(text: OrpheusText.make(r), voice: request.choice.voiceID,
                                                              seed: seed(piece + request.choice.voiceID))
            case .chatterbox:
                // A shade (Casting Style) and casting's age pitch shift the clip; the clone keeps the voice.
                let semitones = (request.shade?.pitch ?? 0) + ChatterboxVoice.semitones(pitch: request.choice.pitch)
                let reference = try ChatterboxClips.reference(for: request.choice, semitones: semitones)
                let delivery = ChatterboxDelivery.for(request.tone, narration: request.kind == nil)
                if n == 0 { volume *= Float(delivery.volume) }
                let said = try await MLXSpeech.shared.chatterbox(text: piece, reference: reference, delivery: delivery,
                                                                 seed: seed(piece + request.choice.voiceID))
                // A short line can run on into made-up speech after a pause: cut it there.
                samples += ChatterboxRunOn.trim(said, sampleRate: SpeechAudio.format.sampleRate, text: piece)
            }
        }
        return try buffer(level(samples), volume: volume)
    }

    /// Splits text into runs of whole sentences of at most about `limit` characters.
    nonisolated static func chunks(_ text: String, limit: Int = 200) -> [String] {
        guard text.count > limit else { return [text] }
        let tokenizer = NLTokenizer(unit: .sentence)
        tokenizer.string = text
        var sentences: [String] = []
        tokenizer.enumerateTokens(in: text.startIndex..<text.endIndex) { r, _ in
            let s = text[r].trimmingCharacters(in: .whitespacesAndNewlines)
            if !s.isEmpty { sentences.append(s) }
            return true
        }
        var out: [String] = []
        var current = ""
        for s in sentences.flatMap({ $0.count > limit ? splitLong($0, limit: limit) : [$0] }) {
            if current.isEmpty { current = s } else if current.count + 1 + s.count <= limit { current += " " + s } else { out.append(current); current = s }
        }
        if !current.isEmpty { out.append(current) }
        return out.isEmpty ? [text] : out
    }

    /// A sentence too long on its own breaks at commas, semicolons and dashes, or at words.
    nonisolated private static func splitLong(_ s: String, limit: Int) -> [String] {
        var out: [String] = []
        var current = ""
        for word in s.split(separator: " ") {
            let w = String(word)
            if !current.isEmpty, current.count + 1 + w.count > limit || (current.count > limit / 2 && current.last.map { ",;:—".contains($0) } == true) {
                out.append(current)
                current = w
            } else {
                current = current.isEmpty ? w : current + " " + w
            }
        }
        if !current.isEmpty { out.append(current) }
        return out
    }

    /// Brings every line to the same loudness (the models vary by 10 dB or more from one
    /// line to the next), without letting the peaks clip.
    nonisolated static func level(_ samples: [Float], targetDB: Float = -20) -> [Float] {
        guard !samples.isEmpty else { return samples }
        // Loudness of the speech itself: the louder half of 20 ms frames, so pauses don't count.
        let frame = 480
        var energies: [Float] = []
        var i = 0
        while i < samples.count {
            let end = min(samples.count, i + frame)
            var e: Float = 0
            for k in i..<end { e += samples[k] * samples[k] }
            energies.append(e / Float(end - i))
            i = end
        }
        energies.sort()
        let loud = energies[(energies.count / 2)...]
        let rms = sqrt(loud.reduce(0, +) / Float(max(1, loud.count)))
        guard rms > 1e-5 else { return samples }
        var gain = powf(10, targetDB / 20) / rms
        let peak = samples.reduce(0) { max($0, abs($1)) }
        if peak * gain > 0.95 { gain = 0.95 / peak }
        return samples.map { $0 * gain }
    }

    /// Loads the engine's models ahead of the first line (they take several seconds).
    static func prepare(_ engine: VoiceEngine) {
        Task { @MainActor in
            let wanted = engine == .mixed ? [VoiceEngine.chatterbox, .orpheus] : [engine]
            let ready = wanted.filter { $0 != .apple && EngineSwitches.shared.usable($0) }
            guard !ready.isEmpty else { return }
            await MLXSpeech.shared.preload(ready)
        }
    }

    /// The same text in the same voice always comes out the same.
    nonisolated static func seed(_ s: String) -> UInt64 {
        SHA256.hash(data: Data(s.utf8)).prefix(8).reduce(0) { $0 << 8 | UInt64($1) }
    }

    static func buffer(_ samples: [Float], volume: Float = 1) throws -> AVAudioPCMBuffer {
        guard !samples.isEmpty, let b = AVAudioPCMBuffer(pcmFormat: SpeechAudio.format, frameCapacity: AVAudioFrameCount(samples.count)) else {
            throw SpeechError.failed("the voice made no sound")
        }
        b.frameLength = AVAudioFrameCount(samples.count)
        let dst = b.floatChannelData![0]
        for i in samples.indices { dst[i] = samples[i] * volume }
        return b
    }

    static func unload() async { await MLXSpeech.shared.unload() }
    static func unload(_ engine: VoiceEngine) async { await MLXSpeech.shared.unload(engine) }
}

/// What Orpheus is given to say: the text with a sound from the speech tag in front
/// ("she sighed" → <sigh>), which it performs.
enum OrpheusText {
    static func make(_ request: SpeechRequest) -> String {
        guard let sound = request.sound else { return request.text }
        return "<\(sound)> " + request.text
    }
}

/// The clips Chatterbox clones its voices from: a stock voice's clip from the app
/// (Resources/StockVoices), a cloned voice's recording, each shifted by a shade's semitones
/// when asked (made once, kept in memory). Every voice has a clip: the Chatterbox model
/// InnerVoice downloads has no built-in voice of its own.
@MainActor
enum ChatterboxClips {
    /// What every stock clip says: plain, unhurried and long enough to carry the voice.
    static let passage = "I have lived in this town for a long time, and I know every road that leads out of it. "
        + "In the spring the river runs high, and the whole valley smells of rain."

    private static var memory: [String: [Float]] = [:]

    /// The folder of stock clips inside the app.
    static var stockFolder: URL? { Bundle.main.url(forResource: "StockVoices", withExtension: nil) }

    /// The stock clip for a voice name ("tara"), or nil when the app has none.
    static func stockClipURL(_ name: String) -> URL? {
        stockFolder.map { $0.appending(path: name + ".m4a") }.flatMap { FileManager.default.fileExists(atPath: $0.path) ? $0 : nil }
    }

    /// The clip a voice is cloned from, shifted `semitones`.
    static func reference(for choice: VoiceChoice, semitones: Int) throws -> [Float] {
        let key = choice.voiceID + "\u{1F}\(semitones)"
        if let hit = memory[key] { return hit }
        let clip: [Float]
        if LocalClones.isClone(choice) {
            clip = try LocalClones.shared.sample(for: choice.voiceID).audio
        } else {
            let name = ChatterboxVoice.stockName(choice.voiceID) ?? choice.voiceID
            guard let url = stockClipURL(name), let b = SpeechRenderer.read(url), let data = b.floatChannelData else {
                throw SpeechError.voiceMissing(VoiceCatalog.info(for: choice)?.name ?? name.capitalized)
            }
            clip = Array(UnsafeBufferPointer(start: data[0], count: Int(b.frameLength)))
        }
        let shifted = try ShadeAudio.apply(clip, semitones: semitones, pace: 0)
        memory[key] = shifted
        return shifted
    }

    static func forget() { memory = [:] }
}

/// Runs the models, one generation at a time (they share the GPU), and keeps only one
/// engine's models in memory.
actor MLXSpeech {
    static let shared = MLXSpeech()

    private var orpheusModel: LlamaTTSModel?
    private var chatterboxModel: ChatterboxModel?
    private var busy = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    private func acquire() async {
        if !busy { busy = true; return }
        await withCheckedContinuation { waiters.append($0) }
    }

    private func release() {
        if waiters.isEmpty { busy = false } else { waiters.removeFirst().resume() }
    }

    func unload() async {
        await acquire(); defer { release() }
        orpheusModel = nil
        chatterboxModel = nil
        Memory.clearCache()
    }

    private static func load<M>(_ repo: String, as: M.Type) async throws -> M {
        Memory.cacheLimit = 512 * 1024 * 1024
        let model = try await TTS.loadModel(modelRepo: repo, cache: VoicePack.cache)
        guard let m = model as? M else { throw SpeechError.failed("unexpected model in \(repo)") }
        return m
    }

    /// Macs with room for both keep Chatterbox and Orpheus loaded together (about 7 GB), so a
    /// project that mixes engines doesn't reload a model between lines. Others keep one.
    private let keepBoth = ProcessInfo.processInfo.physicalMemory >= 32 * 1_073_741_824

    private func loadOrpheus() async throws -> LlamaTTSModel {
        if let m = orpheusModel { return m }
        if !keepBoth { chatterboxModel = nil }
        await SpeechModelActivity.begin(.orpheus)
        defer { Task { await SpeechModelActivity.end(.orpheus) } }
        let m = try await Self.load(VoicePack.orpheus.repos[0], as: LlamaTTSModel.self)
        orpheusModel = m
        return m
    }

    private func loadChatterbox() async throws -> ChatterboxModel {
        if let m = chatterboxModel { return m }
        if !keepBoth { orpheusModel = nil }
        await SpeechModelActivity.begin(.chatterbox)
        defer { Task { await SpeechModelActivity.end(.chatterbox) } }
        Memory.cacheLimit = 512 * 1024 * 1024
        // Loaded from its folder, never fetched: the speech tokenizer it clones with comes from
        // mlx-audio's default cache, which `VoicePack.pinHubCache` points at the same folder.
        let m = try await ChatterboxModel.fromModelDirectory(VoicePack.directory(VoicePack.chatterboxRepo), hfToken: nil)
        if m.s3Tokenizer == nil {
            m.s3Tokenizer = try S3TokenizerV2.fromModelDirectory(VoicePack.directory(VoicePack.chatterboxTokenizerRepo))
        }
        chatterboxModel = m
        return m
    }

    /// Unloads one engine's models (it was turned off).
    func unload(_ engine: VoiceEngine) async {
        await acquire(); defer { release() }
        switch engine {
        case .orpheus: orpheusModel = nil
        case .chatterbox: chatterboxModel = nil
        case .apple, .mixed, .elevenlabs: break
        }
        Memory.clearCache()
    }

    func preload(_ engines: [VoiceEngine]) async {
        await acquire(); defer { release() }
        for engine in engines {
            do {
                switch engine {
                case .orpheus: _ = try await loadOrpheus()
                case .chatterbox: _ = try await loadChatterbox()
                case .apple, .mixed, .elevenlabs: break
                }
            } catch {
                SpeechLog.write("loading \(engine.rawValue) failed: \(error)")
            }
        }
    }

    func orpheus(text: String, voice: String, seed: UInt64) async throws -> [Float] {
        await acquire(); defer { release() }
        try Task.checkCancellation()
        let model = try await loadOrpheus()
        var p = model.defaultGenerationParameters
        p.seed = seed
        MLXRandom.seed(seed)
        let audio = try await model.generate(text: text, voice: voice, refAudio: nil, refText: nil, language: nil, generationParameters: p)
        return audio.asArray(Float.self)
    }

    /// One piece of text in a voice cloned from `reference` (24 kHz mono), said the way
    /// `delivery` asks. nil: the voice in `defaultVoice`, which only the stock-clip maker
    /// sets (the model InnerVoice downloads has none).
    func chatterbox(text: String, reference: [Float]?, delivery: ChatterboxDelivery, seed: UInt64,
                    defaultVoice: URL? = nil) async throws -> [Float] {
        await acquire(); defer { release() }
        try Task.checkCancellation()
        let model = try await loadChatterbox()
        if let defaultVoice {
            // Chatterbox's default voice (the conds.safetensors Resemble ships with Chatterbox
            // Turbo): for making the "Chatterbox" stock clip once, never at speaking time.
            let w = try MLX.loadArrays(url: defaultVoice)
            if let spk = w["t3.speaker_emb"], let x = w["gen.embedding"] {
                model.defaultConditioning = DefaultConditioning(
                    speakerEmb: spk, condPromptSpeechTokens: w["t3.cond_prompt_speech_tokens"] ?? MLXArray.zeros([1, 0]).asType(.int32),
                    emotionAdv: w["t3.emotion_adv"] ?? MLXArray(Float(0.5)).reshaped([1, 1, 1]), xVector: x,
                    promptToken: w["gen.prompt_token"] ?? MLXArray.zeros([1, 0]).asType(.int32),
                    promptTokenLen: w["gen.prompt_token_len"] ?? MLXArray([Int32(0)]),
                    promptFeat: w["gen.prompt_feat"] ?? MLXArray.zeros([1, 0, 80]), promptFeatLen: MLXArray([Int32(0)]))
            }
        }
        model.emotionAdvOverride = Float(delivery.exaggeration)
        model.cfgWeightOverride = Float(delivery.cfgWeight)
        var p = model.defaultGenerationParameters
        p.seed = seed
        MLXRandom.seed(seed)
        let ref = reference.map { MLXArray($0).materialized() }
        let audio = try await model.generate(text: text, voice: nil, refAudio: ref, refText: nil, language: nil, generationParameters: p)
        return audio.asArray(Float.self)
    }
}

/// Which AI voice engine is loading its models right now, for the progress bar in every
/// project window (loading takes several seconds and happens in the background).
@Observable
@MainActor
final class SpeechModelActivity {
    static let shared = SpeechModelActivity()
    private(set) var loading: [VoiceEngine: Int] = [:]

    var current: VoiceEngine? { loading.first { $0.value > 0 }?.key }

    static func begin(_ e: VoiceEngine) { shared.loading[e, default: 0] += 1 }
    static func end(_ e: VoiceEngine) { shared.loading[e] = max(0, (shared.loading[e] ?? 1) - 1) }
}
