@preconcurrency import AVFoundation
import CryptoKit
import InnerVoiceKit

/// One thing to say: text, a voice and how to say it.
struct SpeechRequest: Hashable, Sendable {
    var text: String
    var choice: VoiceChoice
    var tone: Tone
    var kind: LineKind?
    /// A sigh or laugh from the speech tag (Orpheus and ElevenLabs).
    var sound: VocalSound? = nil
    /// The book allows its text to go to a cloud engine (ElevenLabs). Not part of the cache key.
    var cloudAllowed = false
    /// Casting Style: how the narrator's voice changes for the character saying this
    /// (pitch and pace; `ShadeAudio`). nil or neutral: the voice as it is.
    var shade: Shade? = nil

    /// The file name of the rendered audio in the cache. Version 1 of the rendering.
    var cacheKey: String {
        var parts = ["v1", choice.engine.rawValue, choice.voiceID, choice.design ?? "", String(choice.rate ?? 1), String(choice.pitch ?? 1),
                     tone.rawValue, kind?.rawValue ?? "narration", text]
        if choice.engine == .orpheus || choice.engine == .elevenlabs, let sound { parts.append(sound.rawValue) }
        // Unshaded audio keeps the key it always had, so the cache stays warm.
        if let shade, shade.pitch != 0 || shade.pace != 0 { parts.append("shade \(shade.pitch) \(shade.pace)") }
        return SHA256.hash(data: Data(parts.joined(separator: "\u{1F}").utf8)).map { String(format: "%02x", $0) }.joined()
    }
}

enum SpeechError: LocalizedError {
    case engineUnavailable(VoiceEngine)
    case voiceMissing(String)
    case failed(String)

    var errorDescription: String? {
        switch self {
        case .engineUnavailable(let e): e.isCloud
            ? "\(e.title) isn’t set up. Set it up in Settings › Voices, or choose another engine on the Voices page."
            : "\(e.title) isn’t downloaded yet. Download it in Settings › Voices, or choose another engine on the Voices page."
        case .voiceMissing(let name): "The voice “\(name)” isn’t on this Mac anymore. Choose another on the Voices page."
        case .failed(let why): "Unable to speak the line: \(why)"
        }
    }
}

/// Rendered audio handed between tasks. The buffer isn't changed after it's made.
struct RenderedAudio: @unchecked Sendable {
    let buffer: AVAudioPCMBuffer
}

/// Audio in the app's one format: 24 kHz mono, 32-bit float (what Qwen3-TTS and Orpheus
/// make; Apple's voices are converted).
nonisolated enum SpeechAudio {
    static let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 24_000, channels: 1, interleaved: false)!

    /// The audio without the silence a voice leaves before and after the words, so the
    /// pauses between pieces are what the narrator set. Keeps 20 ms at each end.
    static func trimmed(_ buffer: AVAudioPCMBuffer, threshold: Float = 0.008) -> AVAudioPCMBuffer {
        guard let data = buffer.floatChannelData, buffer.frameLength > 0 else { return buffer }
        let n = Int(buffer.frameLength)
        let x = data[0]
        var first = 0, last = n - 1
        while first < n, abs(x[first]) < threshold { first += 1 }
        while last > first, abs(x[last]) < threshold { last -= 1 }
        guard first < last else { return buffer }
        let pad = Int(buffer.format.sampleRate * 0.02)
        first = max(0, first - pad)
        last = min(n - 1, last + pad)
        let count = last - first + 1
        guard count < n, let out = AVAudioPCMBuffer(pcmFormat: buffer.format, frameCapacity: AVAudioFrameCount(count)) else { return buffer }
        out.frameLength = AVAudioFrameCount(count)
        for c in 0..<Int(buffer.format.channelCount) {
            memcpy(out.floatChannelData![c], data[c].advanced(by: first), count * MemoryLayout<Float>.size)
        }
        return out
    }

    static func silence(_ seconds: Double) -> AVAudioPCMBuffer {
        let frames = AVAudioFrameCount(max(1, seconds * format.sampleRate))
        let b = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames)!
        b.frameLength = frames
        return b
    }

    /// Joins buffers in any format into one buffer in `format`.
    static func convert(_ buffers: [AVAudioPCMBuffer]) throws -> AVAudioPCMBuffer {
        guard let first = buffers.first else { return silence(0.05) }
        let total = buffers.reduce(0) { $0 + Int($1.frameLength) }
        let ratio = format.sampleRate / first.format.sampleRate
        let capacity = AVAudioFrameCount(Double(total) * ratio + 4096)
        guard let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity),
              let converter = AVAudioConverter(from: first.format, to: format) else { throw SpeechError.failed("unsupported audio format") }
        var queue = buffers
        var error: NSError?
        let status = converter.convert(to: out, error: &error) { _, inputStatus in
            if queue.isEmpty {
                inputStatus.pointee = .endOfStream
                return nil
            }
            inputStatus.pointee = .haveData
            return queue.removeFirst()
        }
        if status == .error { throw SpeechError.failed(error?.localizedDescription ?? "conversion failed") }
        return out
    }
}

/// Makes the audio for a request, from the cache when it was made before. Rendered audio
/// lives in Application Support (not the project), so projects stay small and replaying a
/// line is instant.
@MainActor
final class SpeechRenderer {
    static let shared = SpeechRenderer()

    let cacheFolder: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        // "Inner Voice" keeps its name with the models (see ModelManager).
        let dir = base.appending(path: SystemHooks.isTestRun ? "Inner Voice/Speech (test)" : "Inner Voice/Speech")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    private var pruned = false

    func render(_ request: SpeechRequest) async throws -> AVAudioPCMBuffer {
        let text = request.text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return SpeechAudio.silence(0.05) }
        let file = cacheFolder.appending(path: request.cacheKey + ".caf")
        if let cached = Self.read(file) { return cached }
        var buffer: AVAudioPCMBuffer
        switch request.choice.engine {
        case .apple: buffer = try await AppleSpeech.render(request)
        case .qwen, .orpheus: buffer = try await ModelSpeech.render(request)
        case .elevenlabs: buffer = try await ElevenLabs.shared.render(request, allowed: request.cloudAllowed)
        case .mixed: throw SpeechError.engineUnavailable(.mixed)
        }
        // Shading: Apple's voices did it themselves; Qwen3-TTS shifted its voice sample, so
        // only the pace is left; Orpheus and ElevenLabs shift the finished audio.
        if let s = request.shade, s.pitch != 0 || s.pace != 0 {
            switch request.choice.engine {
            case .apple, .mixed: break
            case .qwen: buffer = try ShadeAudio.apply(buffer, semitones: 0, pace: s.pace)
            case .orpheus, .elevenlabs: buffer = try ShadeAudio.apply(buffer, semitones: s.pitch, pace: s.pace)
            }
        }
        Self.write(buffer, to: file)
        pruneIfNeeded()
        return buffer
    }

    func isCached(_ request: SpeechRequest) -> Bool {
        FileManager.default.fileExists(atPath: cacheFolder.appending(path: request.cacheKey + ".caf").path)
    }

    static func read(_ url: URL) -> AVAudioPCMBuffer? {
        guard let f = try? AVAudioFile(forReading: url, commonFormat: .pcmFormatFloat32, interleaved: false),
              let b = AVAudioPCMBuffer(pcmFormat: f.processingFormat, frameCapacity: AVAudioFrameCount(f.length)) else { return nil }
        do { try f.read(into: b) } catch { return nil }
        return b
    }

    static func write(_ buffer: AVAudioPCMBuffer, to url: URL) {
        do {
            let f = try AVAudioFile(forWriting: url, settings: buffer.format.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
            try f.write(from: buffer)
        } catch {
            try? FileManager.default.removeItem(at: url)
        }
    }

    /// Keeps the cache under about 1 GB, removing the oldest audio first.
    private func pruneIfNeeded() {
        guard !pruned else { return }
        pruned = true
        let folder = cacheFolder
        Task.detached(priority: .background) {
            let keys: [URLResourceKey] = [.fileSizeKey, .contentAccessDateKey]
            guard let files = try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys) else { return }
            var items = files.compactMap { url -> (URL, Int, Date)? in
                guard let v = try? url.resourceValues(forKeys: Set(keys)) else { return nil }
                return (url, v.fileSize ?? 0, v.contentAccessDate ?? .distantPast)
            }
            var total = items.reduce(0) { $0 + $1.1 }
            guard total > 1_000_000_000 else { return }
            items.sort { $0.2 < $1.2 }
            for (url, size, _) in items where total > 700_000_000 {
                try? FileManager.default.removeItem(at: url)
                total -= size
            }
        }
    }

    func clearCache() {
        try? FileManager.default.removeItem(at: cacheFolder)
        try? FileManager.default.createDirectory(at: cacheFolder, withIntermediateDirectories: true)
    }

    var cacheSize: Int {
        let files = (try? FileManager.default.contentsOfDirectory(at: cacheFolder, includingPropertiesForKeys: [.fileSizeKey])) ?? []
        return files.reduce(0) { $0 + ((try? $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
    }
}

/// Apple's voices, through AVSpeechSynthesizer's write-to-buffer. They can't act, so a
/// tone only nudges speed, pitch and loudness.
enum AppleSpeech {
    /// (rate, pitch, volume) for each tone.
    static func nudge(_ tone: Tone) -> (Float, Float, Float) {
        switch tone {
        case .neutral: (1, 1, 1)
        case .excited: (1.08, 1.08, 1)
        case .confused: (0.95, 1.03, 1)
        case .sad: (0.88, 0.94, 0.85)
        case .angry: (1.06, 0.97, 1)
        case .whispered: (0.9, 1, 0.5)
        case .scared: (1.1, 1.08, 0.9)
        case .tender: (0.92, 1, 0.85)
        case .sarcastic: (0.94, 0.97, 1)
        }
    }

    /// Collects a synthesizer's buffers on its own thread.
    private final class Collector: NSObject, AVSpeechSynthesizerDelegate, @unchecked Sendable {
        let lock = NSLock()
        var buffers: [AVAudioPCMBuffer] = []
        var done = false
        var continuation: CheckedContinuation<[AVAudioPCMBuffer], Error>?

        func add(_ b: AVAudioPCMBuffer) {
            lock.lock(); defer { lock.unlock() }
            // The synthesizer reuses its buffers: keep a copy.
            if let copy = AVAudioPCMBuffer(pcmFormat: b.format, frameCapacity: b.frameLength) {
                copy.frameLength = b.frameLength
                let bytes = Int(b.frameLength) * Int(b.format.streamDescription.pointee.mBytesPerFrame)
                for (dst, src) in zip(UnsafeMutableAudioBufferListPointer(copy.mutableAudioBufferList), UnsafeMutableAudioBufferListPointer(b.mutableAudioBufferList)) {
                    if let d = dst.mData, let s = src.mData { memcpy(d, s, min(bytes, Int(src.mDataByteSize))) }
                }
                buffers.append(copy)
            }
        }

        func finish() {
            lock.lock()
            guard !done else { lock.unlock(); return }
            done = true
            let c = continuation
            let result = buffers
            lock.unlock()
            c?.resume(returning: result)
        }

        // Some voices end without the empty last buffer; the delegate hears the end anyway.
        nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
            DispatchQueue.global().asyncAfter(deadline: .now() + 0.2) { self.finish() }
        }

        nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didCancel utterance: AVSpeechUtterance) {
            finish()
        }
    }

    @MainActor
    static func render(_ request: SpeechRequest) async throws -> AVAudioPCMBuffer {
        guard let voice = AVSpeechSynthesisVoice(identifier: request.choice.voiceID) else {
            throw SpeechError.voiceMissing(request.choice.voiceID.components(separatedBy: ".").last ?? request.choice.voiceID)
        }
        var (r, p, v) = nudge(request.tone)
        // A shade (Casting Style): pitch in semitones, pace in steps.
        if let s = request.shade {
            p *= Float(ShadeAudio.pitchMultiplier(s.pitch))
            r *= Float(ShadeAudio.paceRate(s.pace))
        }
        let u = AVSpeechUtterance(string: request.text)
        u.voice = voice
        u.rate = min(AVSpeechUtteranceMaximumSpeechRate,
                     max(AVSpeechUtteranceMinimumSpeechRate, AVSpeechUtteranceDefaultSpeechRate * Float(request.choice.rate ?? 1) * r))
        u.pitchMultiplier = min(2, max(0.5, Float(request.choice.pitch ?? 1) * p))
        u.volume = v * (request.kind == .thought ? 0.85 : 1)
        let synth = AVSpeechSynthesizer()
        let collector = Collector()
        synth.delegate = collector
        let buffers: [AVAudioPCMBuffer] = try await withCheckedThrowingContinuation { cont in
            collector.continuation = cont
            synth.write(u) { buffer in
                guard let pcm = buffer as? AVAudioPCMBuffer else { return }
                if pcm.frameLength == 0 { collector.finish() } else { collector.add(pcm) }
            }
        }
        withExtendedLifetime(synth) {}
        return try SpeechAudio.convert(buffers)
    }
}

