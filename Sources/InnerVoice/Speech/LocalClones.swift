@preconcurrency import AVFoundation
import InnerVoiceKit
import Observation
import Speech

/// Voices cloned on this Mac: a short clip of one real person's voice and exactly what it
/// says, from which Qwen3-TTS speaks every line. Nothing leaves the Mac. Kept in
/// Application Support/Inner Voice/Cloned Voices, so every project can use them; casting
/// never gives one out (a real person's voice goes only where the narrator puts it).
@Observable
@MainActor
final class LocalClones {
    static let shared = LocalClones()

    struct Clone: Codable, Hashable, Identifiable {
        var id: String
        var name: String
        var gender: VoiceGender
        /// What the clip says, word for word.
        var text: String
        var seconds: Double
        var created: Date

        var voiceID: String { LocalClones.prefix + id }
    }

    /// `VoiceChoice.voiceID` for a clone: "clone:" and its id.
    nonisolated static let prefix = "clone:"
    nonisolated static func isClone(_ choice: VoiceChoice) -> Bool { choice.engine == .qwen && choice.voiceID.hasPrefix(prefix) }

    private(set) var clones: [Clone] = []
    @ObservationIgnored private var samples: [String: QwenSamples.Sample] = [:]

    /// Test runs keep their clones in a temporary folder, never the person's own.
    static let folder: URL = {
        let dir = SystemHooks.isTestRun
            ? FileManager.default.temporaryDirectory.appending(path: "InnerVoice-test-clones-\(ProcessInfo.processInfo.processIdentifier)")
            : URL.applicationSupportDirectory.appending(path: "Inner Voice/Cloned Voices")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    private static var listFile: URL { folder.appending(path: "voices.json") }
    private static func audioFile(_ id: String) -> URL { folder.appending(path: id + ".caf") }

    private init() {
        if let data = try? Data(contentsOf: Self.listFile), let list = try? JSONDecoder().decode([Clone].self, from: data) {
            clones = list.filter { FileManager.default.fileExists(atPath: Self.audioFile($0.id).path) }
        }
    }

    private func save() {
        if let data = try? JSONEncoder().encode(clones) { try? data.write(to: Self.listFile, options: .atomic) }
    }

    /// The clones as voices for the Qwen3-TTS lists, newest first.
    var voices: [VoiceInfo] {
        clones.sorted { $0.created > $1.created }.map(Self.info)
    }

    static func info(_ c: Clone) -> VoiceInfo {
        VoiceInfo(engine: .qwen, voiceID: c.voiceID, name: c.name, gender: c.gender, age: .adult, accent: "",
                  quality: 3, detail: "Cloned on this Mac")
    }

    func clone(for voiceID: String) -> Clone? {
        guard voiceID.hasPrefix(Self.prefix) else { return nil }
        let id = String(voiceID.dropFirst(Self.prefix.count))
        return clones.first { $0.id == id }
    }

    /// Saves a new clone from its clip (24 kHz mono) and what the clip says.
    @discardableResult
    func add(name: String, gender: VoiceGender, audio: [Float], text: String) throws -> Clone {
        let c = Clone(id: UUID().uuidString, name: name.trimmingCharacters(in: .whitespacesAndNewlines), gender: gender,
                      text: text.trimmingCharacters(in: .whitespacesAndNewlines),
                      seconds: Double(audio.count) / SpeechAudio.format.sampleRate, created: .now)
        let file = Self.audioFile(c.id)
        SpeechRenderer.write(try ModelSpeech.buffer(audio), to: file)
        guard FileManager.default.fileExists(atPath: file.path) else { throw CloneError.couldNotSave }
        clones.append(c)
        samples[c.id] = QwenSamples.Sample(audio: audio, text: c.text)
        save()
        ActivityStore.shared.record(ActivityKey.voicesCloned)
        return c
    }

    func rename(_ id: String, to name: String) {
        let n = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !n.isEmpty, let i = clones.firstIndex(where: { $0.id == id }) else { return }
        clones[i].name = n
        save()
    }

    /// Removes the clone and its clip. Books that used it say the voice is missing.
    func delete(_ id: String) {
        clones.removeAll { $0.id == id }
        samples[id] = nil
        try? FileManager.default.removeItem(at: Self.audioFile(id))
        save()
    }

    /// The clip every line is spoken from.
    func sample(for voiceID: String) throws -> QwenSamples.Sample {
        guard let c = clone(for: voiceID) else { throw SpeechError.voiceMissing("a cloned voice deleted from this Mac") }
        if let s = samples[c.id] { return s }
        guard let b = SpeechRenderer.read(Self.audioFile(c.id)), let data = b.floatChannelData else {
            throw SpeechError.voiceMissing(c.name)
        }
        let s = QwenSamples.Sample(audio: Array(UnsafeBufferPointer(start: data[0], count: Int(b.frameLength))), text: c.text)
        samples[c.id] = s
        return s
    }

    enum CloneError: LocalizedError {
        case couldNotSave, unreadable(String), tooShort

        var errorDescription: String? {
            switch self {
            case .couldNotSave: "InnerVoice couldn’t save the voice on this Mac."
            case .unreadable(let name): "InnerVoice can’t read \(name) as audio."
            case .tooShort: "There isn’t enough speech in the recordings. Record at least \(Int(CloneClip.shortest)) seconds of the voice."
            }
        }
    }
}

/// Turning recordings into a clone's clip: read and join them at 24 kHz mono, trim the
/// silence, find what's said on this Mac, and cut at a pause.
enum CloneAudio {
    /// The recordings joined, each with its silence trimmed.
    nonisolated static func load(_ urls: [URL]) throws -> [Float] {
        var all: [Float] = []
        for url in urls {
            let samples = try read(url)
            let trimmed = trim(samples)
            if trimmed.isEmpty { continue }
            if !all.isEmpty { all += [Float](repeating: 0, count: Int(SpeechAudio.format.sampleRate * 0.4)) }
            all += trimmed
        }
        return all
    }

    nonisolated static func read(_ url: URL) throws -> [Float] {
        guard let file = try? AVAudioFile(forReading: url) else { throw LocalClones.CloneError.unreadable(url.lastPathComponent) }
        let source = file.processingFormat
        guard let input = AVAudioPCMBuffer(pcmFormat: source, frameCapacity: AVAudioFrameCount(file.length)) else {
            throw LocalClones.CloneError.unreadable(url.lastPathComponent)
        }
        try file.read(into: input)
        let target = SpeechAudio.format
        guard let converter = AVAudioConverter(from: source, to: target) else { throw LocalClones.CloneError.unreadable(url.lastPathComponent) }
        let capacity = AVAudioFrameCount(Double(input.frameLength) * target.sampleRate / source.sampleRate) + 4096
        guard let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { throw LocalClones.CloneError.unreadable(url.lastPathComponent) }
        nonisolated(unsafe) var given = false
        var error: NSError?
        converter.convert(to: output, error: &error) { _, status in
            if given { status.pointee = .endOfStream; return nil }
            given = true
            status.pointee = .haveData
            return input
        }
        if let error { throw error }
        guard let data = output.floatChannelData else { return [] }
        return Array(UnsafeBufferPointer(start: data[0], count: Int(output.frameLength)))
    }

    /// Without the silence before the first word and after the last (20 ms windows below
    /// -45 dB), keeping a tenth of a second at each end.
    nonisolated static func trim(_ s: [Float]) -> [Float] {
        let window = Int(SpeechAudio.format.sampleRate * 0.02)
        guard s.count > window else { return [] }
        func loud(_ i: Int) -> Bool {
            var e: Float = 0
            for k in i..<min(s.count, i + window) { e += s[k] * s[k] }
            return (e / Float(window)).squareRoot() > 0.0056
        }
        var first = 0
        while first < s.count, !loud(first) { first += window }
        var last = s.count - window
        while last > first, !loud(last) { last -= window }
        guard first < last else { return [] }
        let pad = Int(SpeechAudio.format.sampleRate * 0.1)
        return Array(s[max(0, first - pad)..<min(s.count, last + window + pad)])
    }

    /// What's said in the first half minute, recognized on this Mac only (never Apple's
    /// servers), with each word's time. nil when on-device recognition isn't available or
    /// the narrator said no; then they type it.
    static func transcribe(_ samples: [Float]) async -> (text: String, words: [CloneClip.Word])? {
        guard await authorized(), let recognizer = SFSpeechRecognizer(locale: Locale(identifier: "en-US")),
              recognizer.isAvailable, recognizer.supportsOnDeviceRecognition else { return nil }
        let file = FileManager.default.temporaryDirectory.appending(path: "InnerVoice-clone-\(UUID().uuidString).caf")
        defer { try? FileManager.default.removeItem(at: file) }
        guard let buffer = try? ModelSpeech.buffer(Array(samples.prefix(Int(SpeechAudio.format.sampleRate * 30)))) else { return nil }
        SpeechRenderer.write(buffer, to: file)
        let request = SFSpeechURLRecognitionRequest(url: file)
        request.requiresOnDeviceRecognition = true
        request.shouldReportPartialResults = false
        request.addsPunctuation = true
        // Read the words in the callback, so only plain values cross back.
        let found: (text: String, words: [CloneClip.Word])? = await withCheckedContinuation { continuation in
            let once = Once()
            recognizer.recognitionTask(with: request) { result, error in
                if let result, result.isFinal {
                    let best = result.bestTranscription
                    let words = best.segments.map {
                        CloneClip.Word(start: $0.timestamp, end: $0.timestamp + $0.duration, textEnd: $0.substringRange.location + $0.substringRange.length)
                    }
                    let text = best.formattedString
                    once.run { continuation.resume(returning: words.isEmpty ? nil : (text, words)) }
                } else if error != nil {
                    once.run { continuation.resume(returning: nil) }
                }
            }
        }
        return found
    }

    private static func authorized() async -> Bool {
        switch SFSpeechRecognizer.authorizationStatus() {
        case .authorized: return true
        case .notDetermined:
            return await withCheckedContinuation { c in
                SFSpeechRecognizer.requestAuthorization { c.resume(returning: $0 == .authorized) }
            }
        default: return false
        }
    }

    /// Resumes a continuation once, whichever callback comes first.
    private final class Once: @unchecked Sendable {
        private var done = false
        private let lock = NSLock()
        func run(_ body: () -> Void) {
            lock.lock()
            defer { lock.unlock() }
            guard !done else { return }
            done = true
            body()
        }
    }
}
