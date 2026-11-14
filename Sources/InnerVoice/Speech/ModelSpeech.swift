@preconcurrency import AVFoundation
import CryptoKit
import InnerVoiceKit
import MLX
import MLXAudioCodecs
import MLXAudioCore
import MLXAudioTTS
import NaturalLanguage
import Observation

/// Pocket TTS, Qwen3-TTS, Chatterbox and Orpheus, on Apple's MLX (Apple silicon only).
///
/// Orpheus has eight fixed voices and acts from the words; the speech tag's sighs, laughs
/// and gasps are added as its sound tags.
///
/// Pocket TTS (Kyutai, CC BY 4.0) clones every voice from a short clip: a stock voice's clip
/// ships with the app (`PocketClips`), and a voice cloned on this Mac uses the narrator's
/// recording. The tone sets its sampling temperature, and a pitch, speed and loudness
/// around it (`PocketDelivery`). A shade, or casting's age pitch, shifts the clip before
/// cloning, so the voice stays the same person.
///
/// Qwen3-TTS (Alibaba's Qwen team, Apache 2.0; it runs here like the others and sends
/// nothing anywhere) gives each character a voice designed from their summary and Voice
/// Notes. The design model first records a short sample in that voice and tone (once, kept
/// in Voice Models/Samples); every line is then spoken by the base model from that sample,
/// so the voice stays the same from line to line. A voice cloned on this Mac is spoken from
/// the narrator's own clip and its words.
///
/// Chatterbox (Resemble AI, MIT; parts from Alibaba research, labeled in the UI) clones
/// from the same clips as Pocket TTS plus its own default voice. The tone sets its emotion
/// exaggeration and CFG weight (`ChatterboxDelivery`); a short line's run-on after a pause
/// is cut (`ChatterboxRunOn`).
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
            case .apple, .mixed, .elevenlabs, .gemini: throw SpeechError.engineUnavailable(engine)
            case .chatterbox:
                // Same clips as Pocket TTS; a shade and casting's age pitch shift the clip.
                let semitones = (request.shade?.pitch ?? 0) + PocketVoice.semitones(pitch: request.choice.pitch)
                let reference = try PocketClips.reference(for: request.choice, semitones: semitones)
                let delivery = ChatterboxDelivery.for(request.tone, narration: request.kind == nil)
                if n == 0 { volume *= Float(delivery.volume) }
                let said = try await MLXSpeech.shared.chatterbox(text: piece, reference: reference, delivery: delivery,
                                                                 seed: seed(piece + request.choice.voiceID))
                samples += ChatterboxRunOn.trim(said, sampleRate: SpeechAudio.format.sampleRate, text: piece)
            case .orpheus:
                var r = request
                r.text = piece
                if n > 0 { r.sound = nil }
                samples += try await MLXSpeech.shared.orpheus(text: OrpheusText.make(r), voice: request.choice.voiceID,
                                                              seed: seed(piece + request.choice.voiceID))
            case .pocket:
                // A shade (Casting Style), casting's age pitch and the tone shift the clip; the
                // clone keeps the voice. (The tone's speed is stretched by `SpeechRenderer`.)
                let delivery = PocketDelivery.for(request.tone, narration: request.kind == nil)
                let semitones = (request.shade?.pitch ?? 0) + PocketVoice.semitones(pitch: request.choice.pitch) + delivery.semitones
                let reference = try PocketClips.reference(for: request.choice, semitones: semitones)
                if n == 0 { volume *= Float(delivery.volume) }
                let said = try await MLXSpeech.shared.pocket(text: piece, reference: reference, delivery: delivery,
                                                             seed: seed(piece + request.choice.voiceID))
                // No run-on cut: Pocket TTS stops when the line is said (short lines tested
                // 2026-11-13, at the even and the angry temperature; self-test `pocketVoices`).
                samples += said
            case .qwen:
                // A shade (Casting Style) and the narrator's pitch shift the sample; the clone
                // keeps the voice. (Speed is stretched by `SpeechRenderer`.)
                let semitones = (request.shade?.pitch ?? 0) + PocketVoice.semitones(pitch: request.choice.pitch)
                let sample: QwenSamples.Sample
                let seedKey: String
                if LocalClones.isClone(request.choice) {
                    // A voice cloned on this Mac: every line from the narrator's own clip.
                    let clip = try LocalClones.shared.sample(for: request.choice.voiceID)
                    sample = try QwenSamples.shaded(QwenSamples.Sample(audio: clip.audio, text: clip.text),
                                                    key: request.choice.voiceID, semitones: semitones)
                    seedKey = request.choice.voiceID
                } else {
                    let design = request.choice.design ?? VoiceResolver.fallback(.qwen)?.design ?? ""
                    let tone = QwenSamples.sampleTone(for: request)
                    sample = try QwenSamples.shaded(try await QwenSamples.sample(design: design, tone: tone),
                                                    key: QwenSamples.key(design: design, tone: tone), semitones: semitones)
                    seedKey = design
                }
                samples += try await MLXSpeech.shared.qwenClone(text: piece, reference: sample.audio, referenceText: sample.text,
                                                                seed: seed(piece + seedKey))
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
            let wanted = engine == .mixed ? [VoiceEngine.pocket, .qwen, .orpheus] : [engine]
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

/// The clips Pocket TTS clones its voices from: a stock voice's clip from the app
/// (Resources/StockVoices), a cloned voice's recording, each shifted by a shade's semitones
/// when asked (made once, kept in memory). Every voice has a clip, Alba included (Pocket
/// TTS's own voices also come as ready-made embeddings, but a clip can be shaded).
@MainActor
enum PocketClips {
    /// What the Orpheus-made stock clips say: plain, unhurried and long enough to carry the
    /// voice. (Alba's clip is her own recording.)
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
            let name = PocketVoice.stockName(choice.voiceID) ?? choice.voiceID
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

/// Qwen3-TTS voice samples: a few seconds in the designed voice that every line is cloned
/// from. Made once per design (and tone), then kept in Voice Models/Samples.
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

    /// The sample with its pitch shifted `semitones` (a shade, the narrator's pitch), kept
    /// beside the sample so it's made once. 0 is the sample itself.
    static func shaded(_ sample: Sample, key: String, semitones: Int) throws -> Sample {
        guard semitones != 0 else { return sample }
        let k = key + "-shade\(semitones)"
        if let s = memory[k] { return s }
        let file = folder.appending(path: k + ".caf")
        if let b = SpeechRenderer.read(file), let data = b.floatChannelData {
            let s = Sample(audio: Array(UnsafeBufferPointer(start: data[0], count: Int(b.frameLength))), text: sample.text)
            memory[k] = s
            return s
        }
        let audio = try ShadeAudio.apply(sample.audio, semitones: semitones, pace: 0)
        SpeechRenderer.write(try ModelSpeech.buffer(audio), to: file)
        let s = Sample(audio: audio, text: sample.text)
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
    private var pocketModel: PocketTTSModel?
    private var qwenDesignModel: Qwen3TTSModel?
    private var qwenCloneModel: Qwen3TTSModel?
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
        pocketModel = nil
        qwenDesignModel = nil
        qwenCloneModel = nil
        chatterboxModel = nil
        Memory.clearCache()
    }

    private static func load<M>(_ repo: String, as: M.Type) async throws -> M {
        Memory.cacheLimit = 512 * 1024 * 1024
        let model = try await TTS.loadModel(modelRepo: repo, cache: VoicePack.cache)
        guard let m = model as? M else { throw SpeechError.failed("unexpected model in \(repo)") }
        return m
    }

    /// What stays loaded together, so a project that mixes engines doesn't reload a model
    /// between lines. Pocket TTS is small (about 1 GB while it speaks): Macs with 16 GB or
    /// more keep it beside a bigger engine. Orpheus (about 4 GB), Chatterbox (about 4 GB) and
    /// Qwen3-TTS (its two models, about 6 GB) stay loaded together only on Macs with 32 GB or
    /// more; under that, only one of the three big engines stays loaded.
    private let memoryGB = ProcessInfo.processInfo.physicalMemory / 1_073_741_824

    /// Makes room for `engine` by dropping what this Mac can't keep beside it.
    private func makeRoom(for engine: VoiceEngine) {
        if memoryGB < 16 {
            if engine != .pocket { pocketModel = nil }
            if engine != .orpheus { orpheusModel = nil }
            if engine != .qwen { qwenDesignModel = nil; qwenCloneModel = nil }
            if engine != .chatterbox { chatterboxModel = nil }
        } else if memoryGB < 32, engine != .pocket {
            if engine != .orpheus { orpheusModel = nil }
            if engine != .qwen { qwenDesignModel = nil; qwenCloneModel = nil }
            if engine != .chatterbox { chatterboxModel = nil }
        }
    }

    private func loadOrpheus() async throws -> LlamaTTSModel {
        if let m = orpheusModel { return m }
        makeRoom(for: .orpheus)
        await SpeechModelActivity.begin(.orpheus)
        defer { Task { await SpeechModelActivity.end(.orpheus) } }
        let m = try await Self.load(VoicePack.orpheus.repos[0], as: LlamaTTSModel.self)
        orpheusModel = m
        return m
    }

    /// Qwen3-TTS's models are loaded from their folders, never fetched: the weights, text
    /// tokenizer and speech tokenizer all come with the pack (`fromModelDirectory` reads
    /// nothing else).
    private func loadQwen(_ repo: String) async throws -> Qwen3TTSModel {
        makeRoom(for: .qwen)
        await SpeechModelActivity.begin(.qwen)
        defer { Task { await SpeechModelActivity.end(.qwen) } }
        Memory.cacheLimit = 512 * 1024 * 1024
        return try await Qwen3TTSModel.fromModelDirectory(VoicePack.directory(repo))
    }

    private func loadQwenDesign() async throws -> Qwen3TTSModel {
        if let m = qwenDesignModel { return m }
        let m = try await loadQwen(VoicePack.qwenDesignRepo)
        qwenDesignModel = m
        return m
    }

    private func loadQwenClone() async throws -> Qwen3TTSModel {
        if let m = qwenCloneModel { return m }
        let m = try await loadQwen(VoicePack.qwenCloneRepo)
        qwenCloneModel = m
        return m
    }

    private func loadPocket() async throws -> PocketTTSModel {
        if let m = pocketModel { return m }
        makeRoom(for: .pocket)
        await SpeechModelActivity.begin(.pocket)
        defer { Task { await SpeechModelActivity.end(.pocket) } }
        Memory.cacheLimit = 512 * 1024 * 1024
        // Loaded from its folder, never fetched: the weights, the Mimi codec and the text
        // tokenizer (tokenizer.json) all come with the pack. The config also names a
        // tokenizer at an hf:// address; mlx-audio skips those and reads the local file.
        let m = try await PocketTTSModel.fromModelDirectory(VoicePack.directory(VoicePack.pocketRepo))
        pocketModel = m
        return m
    }

    private func loadChatterbox() async throws -> ChatterboxModel {
        if let m = chatterboxModel { return m }
        makeRoom(for: .chatterbox)
        await SpeechModelActivity.begin(.chatterbox)
        defer { Task { await SpeechModelActivity.end(.chatterbox) } }
        Memory.cacheLimit = 512 * 1024 * 1024
        // Loaded from its folder, never fetched: the speech tokenizer comes from the pack too
        // (mlx-audio's default cache is pinned to the voice models by `VoicePack.pinHubCache`,
        // and it's set from the pack's own folder when the loader didn't find it).
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
        case .pocket: pocketModel = nil
        case .qwen: qwenDesignModel = nil; qwenCloneModel = nil
        case .chatterbox: chatterboxModel = nil
        case .apple, .mixed, .elevenlabs, .gemini: break
        }
        Memory.clearCache()
    }

    func preload(_ engines: [VoiceEngine]) async {
        await acquire(); defer { release() }
        for engine in engines {
            do {
                switch engine {
                case .orpheus: _ = try await loadOrpheus()
                case .pocket: _ = try await loadPocket()
                case .qwen: _ = try await loadQwenClone(); _ = try await loadQwenDesign()
                case .chatterbox: _ = try await loadChatterbox()
                case .apple, .mixed, .elevenlabs, .gemini: break
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

    /// One piece of text in Chatterbox, in a voice cloned from `reference` (24 kHz mono),
    /// said the way `delivery` asks.
    func chatterbox(text: String, reference: [Float], delivery: ChatterboxDelivery, seed: UInt64) async throws -> [Float] {
        await acquire(); defer { release() }
        try Task.checkCancellation()
        let model = try await loadChatterbox()
        model.emotionAdvOverride = Float(delivery.exaggeration)
        model.cfgWeightOverride = Float(delivery.cfgWeight)
        var p = model.defaultGenerationParameters
        p.seed = seed
        MLXRandom.seed(seed)
        let ref = MLXArray(reference).materialized()
        let audio = try await model.generate(text: text, voice: nil, refAudio: ref, refText: nil, language: nil, generationParameters: p)
        return audio.asArray(Float.self)
    }

    /// One piece of text in a voice cloned from `reference` (24 kHz mono), at the tone's
    /// temperature (`delivery`; its pitch, speed and loudness are applied around the model).
    func pocket(text: String, reference: [Float], delivery: PocketDelivery, seed: UInt64) async throws -> [Float] {
        await acquire(); defer { release() }
        try Task.checkCancellation()
        let model = try await loadPocket()
        var p = model.defaultGenerationParameters
        p.temperature = Float(delivery.temperature)
        p.seed = seed
        // Pocket TTS draws its noise from MLX's global generator: seeded, the same line in
        // the same voice comes out the same.
        MLXRandom.seed(seed)
        let ref = MLXArray(reference).materialized()
        let audio = try await model.generate(text: text, voice: nil, refAudio: ref, refText: nil, language: nil, generationParameters: p)
        return audio.asArray(Float.self)
    }

    /// A few seconds in a voice described in words (Qwen3-TTS VoiceDesign).
    func qwenDesign(text: String, description: String, seed: UInt64) async throws -> [Float] {
        await acquire(); defer { release() }
        try Task.checkCancellation()
        let model = try await loadQwenDesign()
        var p = model.defaultGenerationParameters
        p.seed = seed
        p.maxTokens = 1200
        MLXRandom.seed(seed)
        let audio = try await model.generate(text: text, voice: description, refAudio: nil, refText: nil, language: "English", generationParameters: p)
        return audio.asArray(Float.self)
    }

    /// One piece of text in the voice of `reference` (24 kHz mono) and its words (Qwen3-TTS Base).
    func qwenClone(text: String, reference: [Float], referenceText: String, seed: UInt64) async throws -> [Float] {
        await acquire(); defer { release() }
        try Task.checkCancellation()
        let model = try await loadQwenClone()
        var p = model.defaultGenerationParameters
        p.seed = seed
        p.maxTokens = 2400
        MLXRandom.seed(seed)
        let ref = MLXArray(reference).materialized()
        let audio = try await model.generate(text: text, voice: nil, refAudio: ref, refText: referenceText, language: "English", generationParameters: p)
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
