@preconcurrency import AVFoundation
import CryptoKit
import InnerVoiceKit
import MLX
import MLXAudioCore
import MLXAudioTTS
import NaturalLanguage

/// Qwen3-TTS and Orpheus, on Apple's MLX (Apple silicon only).
///
/// Orpheus has eight fixed voices and acts from the words; the speech tag's sighs, laughs
/// and gasps are added as its sound tags.
///
/// Qwen3-TTS gives each character a voice designed from their summary. The design model
/// first records a short sample in that voice (once, kept in Voice Models/Samples); every
/// line is then spoken by the clone model from that sample, so the voice stays the same
/// from line to line.
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
        for (n, piece) in pieces.enumerated() {
            try Task.checkCancellation()
            if n > 0 { samples += gap }
            switch engine {
            case .apple: throw SpeechError.engineUnavailable(engine)
            case .orpheus:
                var r = request
                r.text = piece
                if n > 0 { r.sound = nil }
                samples += try await MLXSpeech.shared.orpheus(text: OrpheusText.make(r), voice: request.choice.voiceID,
                                                              seed: seed(piece + request.choice.voiceID))
            case .qwen:
                let design = request.choice.design ?? VoiceResolver.fallback(.qwen)?.design ?? ""
                let sample = try await QwenSamples.sample(design: design, tone: QwenSamples.sampleTone(for: request))
                samples += try await MLXSpeech.shared.qwenClone(text: piece, reference: sample.audio, referenceText: sample.text,
                                                                seed: seed(piece + design))
            }
        }
        return try buffer(level(samples), volume: request.kind == .thought ? 0.85 : 1)
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
    static func level(_ samples: [Float], targetDB: Float = -20) -> [Float] {
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
        guard engine != .apple else { return }
        Task { @MainActor in
            guard isInstalled(engine) else { return }
            await MLXSpeech.shared.preload(engine)
        }
    }

    /// The same text in the same voice always comes out the same.
    static func seed(_ s: String) -> UInt64 {
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
}

/// What Orpheus is given to say: the text with a sound from the speech tag in front
/// ("she sighed" → <sigh>), which it performs.
enum OrpheusText {
    static func make(_ request: SpeechRequest) -> String {
        guard let sound = request.sound else { return request.text }
        return "<\(sound)> " + request.text
    }
}

/// Qwen3-TTS voice samples: a few seconds in the designed voice that every line is cloned
/// from. Made once per design (and tone), then kept.
@MainActor
enum QwenSamples {
    struct Sample: Sendable {
        var audio: [Float]
        var text: String
    }

    /// What the sample says: plain, unhurried and long enough to carry the voice.
    static let sampleText = "I have lived in this town for a long time, and I know every road that leads out of it."

    static var folder: URL {
        let dir = VoicePack.folder.appending(path: "Samples")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    private static var memory: [String: Sample] = [:]

    /// The tone the line's sample is recorded in. Narration is always even.
    static func sampleTone(for request: SpeechRequest) -> Tone {
        request.kind == nil ? .neutral : request.tone
    }

    static func key(design: String, tone: Tone) -> String {
        let s = "v1\u{1F}" + design + "\u{1F}" + tone.rawValue
        return SHA256.hash(data: Data(s.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    static func sample(design: String, tone: Tone) async throws -> Sample {
        let k = key(design: design, tone: tone)
        if let s = memory[k] { return s }
        let file = folder.appending(path: k + ".caf")
        if let b = SpeechRenderer.read(file), let data = b.floatChannelData {
            let s = Sample(audio: Array(UnsafeBufferPointer(start: data[0], count: Int(b.frameLength))), text: sampleText)
            memory[k] = s
            return s
        }
        let prompt = tone == .neutral ? design : design + " Speaking " + tone.direction + "."
        let audio = try await MLXSpeech.shared.qwenDesign(text: sampleText, description: prompt, seed: ModelSpeech.seed(design))
        SpeechRenderer.write(try ModelSpeech.buffer(audio), to: file)
        let s = Sample(audio: audio, text: sampleText)
        memory[k] = s
        return s
    }

    static func forget() { memory = [:] }
}

/// Runs the models, one generation at a time (they share the GPU), and keeps only one
/// engine's models in memory.
actor MLXSpeech {
    static let shared = MLXSpeech()

    private var orpheusModel: LlamaTTSModel?
    private var designModel: Qwen3TTSModel?
    private var cloneModel: Qwen3TTSModel?
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
        designModel = nil
        cloneModel = nil
        Memory.clearCache()
    }

    private static func load<M>(_ repo: String, as: M.Type) async throws -> M {
        Memory.cacheLimit = 512 * 1024 * 1024
        let model = try await TTS.loadModel(modelRepo: repo, cache: VoicePack.cache)
        guard let m = model as? M else { throw SpeechError.failed("unexpected model in \(repo)") }
        return m
    }

    func preload(_ engine: VoiceEngine) async {
        await acquire(); defer { release() }
        do {
            switch engine {
            case .apple: break
            case .orpheus:
                if orpheusModel == nil {
                    designModel = nil; cloneModel = nil
                    orpheusModel = try await Self.load(VoicePack.orpheus.repos[0], as: LlamaTTSModel.self)
                }
            case .qwen:
                orpheusModel = nil
                if cloneModel == nil { cloneModel = try await Self.load(VoicePack.qwenCloneRepo, as: Qwen3TTSModel.self) }
                if designModel == nil { designModel = try await Self.load(VoicePack.qwenDesignRepo, as: Qwen3TTSModel.self) }
            }
        } catch {}
    }

    func orpheus(text: String, voice: String, seed: UInt64) async throws -> [Float] {
        await acquire(); defer { release() }
        try Task.checkCancellation()
        if orpheusModel == nil {
            designModel = nil; cloneModel = nil
            orpheusModel = try await Self.load(VoicePack.orpheus.repos[0], as: LlamaTTSModel.self)
        }
        let model = orpheusModel!
        var p = model.defaultGenerationParameters
        p.seed = seed
        MLXRandom.seed(seed)
        let audio = try await model.generate(text: text, voice: voice, refAudio: nil, refText: nil, language: nil, generationParameters: p)
        return audio.asArray(Float.self)
    }

    func qwenDesign(text: String, description: String, seed: UInt64) async throws -> [Float] {
        await acquire(); defer { release() }
        try Task.checkCancellation()
        if designModel == nil {
            orpheusModel = nil
            designModel = try await Self.load(VoicePack.qwenDesignRepo, as: Qwen3TTSModel.self)
        }
        let model = designModel!
        var p = model.defaultGenerationParameters
        p.seed = seed
        p.maxTokens = 1200
        MLXRandom.seed(seed)
        let audio = try await model.generate(text: text, voice: description, refAudio: nil, refText: nil, language: "English", generationParameters: p)
        return audio.asArray(Float.self)
    }

    func qwenClone(text: String, reference: [Float], referenceText: String, seed: UInt64) async throws -> [Float] {
        await acquire(); defer { release() }
        try Task.checkCancellation()
        if cloneModel == nil {
            orpheusModel = nil
            cloneModel = try await Self.load(VoicePack.qwenCloneRepo, as: Qwen3TTSModel.self)
        }
        let model = cloneModel!
        var p = model.defaultGenerationParameters
        p.seed = seed
        p.maxTokens = 2400
        MLXRandom.seed(seed)
        let ref = MLXArray(reference).materialized()
        let audio = try await model.generate(text: text, voice: nil, refAudio: ref, refText: referenceText, language: "English", generationParameters: p)
        return audio.asArray(Float.self)
    }
}
