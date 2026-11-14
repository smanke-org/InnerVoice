@preconcurrency import AVFoundation
import Foundation
import InnerVoiceKit
import Observation

/// Google's Gemini TTS, the second cloud voice engine. Like ElevenLabs it sends text off the
/// Mac (to Google), so it's off until the narrator sets it up (a Gemini API key and a clear
/// yes) and each book must allow Google separately (`ProjectSettings.googleVoicesAllowed`).
/// Nothing is sent otherwise. Request/response shapes live in the Kit (`GeminiTTS`).
@Observable
@MainActor
final class GeminiVoices {
    static let shared = GeminiVoices()

    static let keysPage = URL(string: "https://aistudio.google.com/apikey")!
    static let privacyPage = URL(string: "https://ai.google.dev/gemini-api/terms")!
    private static let consentKey = "gemini.consent"
    private static let modelKey = "gemini.model"
    static let keychainAccount = "gemini"

    /// Bumped when the key or consent changes.
    private(set) var revision = 0

    /// Requests go through this session; the self-test swaps in a fake server.
    @ObservationIgnored var session: URLSession = .shared
    @ObservationIgnored private var testKey: String?
    @ObservationIgnored private var testConsent: Date?
    /// Read once and kept in memory: views ask `isSetUp` on every redraw (see ElevenLabs).
    @ObservationIgnored private var cachedKey: String??
    @ObservationIgnored private var cachedConsent: Date??

    var voices: [VoiceInfo] { GeminiTTS.voices }

    /// Flash or Flash-Lite (Settings › Voices); Flash unless the narrator picks otherwise.
    var model: GeminiTTS.Model {
        get {
            _ = revision
            if SystemHooks.isTestRun { return testModel }
            return UserDefaults.standard.string(forKey: Self.modelKey).flatMap(GeminiTTS.Model.init) ?? .flash
        }
        set {
            if SystemHooks.isTestRun { testModel = newValue } else { UserDefaults.standard.set(newValue.rawValue, forKey: Self.modelKey) }
            revision += 1
        }
    }
    @ObservationIgnored private var testModel: GeminiTTS.Model = .flash

    // MARK: Setup

    /// Set up: a key, and the narrator's yes to sending text to Google.
    var isSetUp: Bool {
        _ = revision
        return apiKey != nil && consented != nil
    }

    var consented: Date? {
        _ = revision
        if SystemHooks.isTestRun { return testConsent }
        if let c = cachedConsent { return c }
        let c = UserDefaults.standard.object(forKey: Self.consentKey) as? Date
        cachedConsent = .some(c)
        return c
    }

    var apiKey: String? {
        _ = revision
        if SystemHooks.isTestRun { return testKey }
        if let k = cachedKey { return k }
        let k = Keychain.read(account: Self.keychainAccount)
        cachedKey = .some(k)
        return k
    }

    func save(key: String, consent: Bool) {
        let key = key.trimmingCharacters(in: .whitespacesAndNewlines)
        if SystemHooks.isTestRun {
            testKey = key.isEmpty ? nil : key
            testConsent = consent ? .now : nil
        } else {
            if key.isEmpty { Keychain.delete(account: Self.keychainAccount) } else { Keychain.write(key, account: Self.keychainAccount) }
            let date = consent ? Date() : nil
            UserDefaults.standard.set(date, forKey: Self.consentKey)
            cachedKey = .some(key.isEmpty ? nil : key)
            cachedConsent = .some(date)
        }
        revision += 1
    }

    /// Forgets the key and the yes. Books keep their permission, but nothing is sent until
    /// Gemini TTS is set up again.
    func disconnect() { save(key: "", consent: false) }

    /// Checks a key by listing the voices (sends no text from the book).
    func check(key: String) async throws {
        var c = URLComponents(url: GeminiTTS.host.appending(path: "voices"), resolvingAgainstBaseURL: false)!
        c.queryItems = [URLQueryItem(name: "type", value: "prebuilt"), URLQueryItem(name: "page_size", value: "1")]
        var req = URLRequest(url: c.url!)
        req.timeoutInterval = 30
        req.setValue(key.trimmingCharacters(in: .whitespacesAndNewlines), forHTTPHeaderField: "x-goog-api-key")
        let (data, response) = try await send(req)
        try Self.throwIfFailed(response, data)
    }

    // MARK: Speech

    /// One line (or piece of narration) in a voice. The text leaves the Mac here, and only
    /// here, and only when the book allows Google.
    func render(_ request: SpeechRequest, allowed: Bool) async throws -> AVAudioPCMBuffer {
        guard allowed else { throw GeminiError.bookNotAllowed }
        guard let key = apiKey, consented != nil else { throw SpeechError.engineUnavailable(.gemini) }
        let model = model
        let style = request.kind == nil ? nil : GeminiTTS.style(tone: request.tone, kind: request.kind, delivery: request.delivery)
        let text = ([request.sound.map(GeminiTTS.tag)].compactMap { $0 } + [request.text]).joined(separator: " ")
        var pieces: [Data] = []
        let gapCount = Int(SpeechAudio.format.sampleRate * 0.18)
        for piece in ModelSpeech.chunks(text, limit: 1500) where ElevenLabs.hasWords(piece.replacingOccurrences(of: #"<[^>]*>"#, with: "", options: .regularExpression)) {
            try Task.checkCancellation()
            var req = URLRequest(url: GeminiTTS.host.appending(path: "interactions"))
            req.httpMethod = "POST"
            req.timeoutInterval = 120
            req.setValue(key, forHTTPHeaderField: "x-goog-api-key")
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = GeminiTTS.requestBody(text: piece, voice: request.choice.voiceID, style: style, model: model)
            let (data, response) = try await send(req)
            try Self.throwIfFailed(response, data)
            let pcm: Data
            do {
                pcm = GeminiTTS.pcm16(fromWAVOrRaw: try GeminiTTS.audio(fromResponse: data))
            } catch GeminiTTS.ResponseError.noAudio(let said) {
                throw GeminiError.noAudio(said)
            }
            ActivityStore.shared.record(ActivityKey.geminiCharacters, count: piece.count)
            var usage = CloudUsageRecord(day: CloudUsageRecord.day(), book: request.usageBook ?? "", service: .google, model: model.rawValue)
            usage.characters = piece.count
            usage.audioSeconds = Double(pcm.count / 2) / 24_000
            usage.requests = 1
            CloudUsageStore.shared.add(usage)
            pieces.append(pcm)
        }
        if pieces.isEmpty { return try ModelSpeech.buffer([Float](repeating: 0, count: Int(SpeechAudio.format.sampleRate * 0.2))) }
        let leveled = await Task.detached(priority: .userInitiated) { () -> [Float] in
            var samples: [Float] = []
            for (n, data) in pieces.enumerated() {
                if n > 0 { samples += [Float](repeating: 0, count: gapCount) }
                samples += ElevenLabs.pcm16(data)
            }
            return ModelSpeech.level(samples)
        }.value
        // Gemini has no speed setting: the narrator's pace is stretched afterwards
        // (SpeechRenderer's shading).
        return try ModelSpeech.buffer(leveled, volume: request.kind == .thought ? 0.85 : 1)
    }

    private func send(_ req: URLRequest) async throws -> (Data, URLResponse) {
        do {
            return try await session.data(for: req)
        } catch let e as URLError where e.code != .cancelled {
            throw GeminiError.offline
        }
    }

    static func throwIfFailed(_ response: URLResponse, _ data: Data) throws {
        guard let http = response as? HTTPURLResponse else { throw GeminiError.server(0, "") }
        guard !(200..<300).contains(http.statusCode) else { return }
        let e = GeminiTTS.errorMessage(data)
        let message = e?.message ?? String(decoding: data.prefix(300), as: UTF8.self)
        let status = e?.status ?? ""
        switch http.statusCode {
        case 400 where message.localizedCaseInsensitiveContains("API key"), 401: throw GeminiError.badKey
        case 403 where status == "PERMISSION_DENIED": throw GeminiError.badKey
        case 429: throw message.localizedCaseInsensitiveContains("quota") ? GeminiError.outOfQuota : GeminiError.busy
        case 500..<600: throw GeminiError.busy
        default: throw GeminiError.server(http.statusCode, message)
        }
    }
}

enum GeminiError: LocalizedError, Equatable {
    case bookNotAllowed, badKey, outOfQuota, busy, offline
    case noAudio(String?)
    case server(Int, String)

    var errorDescription: String? {
        switch self {
        case .bookNotAllowed: "This book hasn’t been allowed to use Gemini TTS. Choose Gemini TTS on the Voices page to allow it."
        case .badKey: "Google didn’t accept the Gemini API key. Check it in Settings › Voices."
        case .outOfQuota: "Your Gemini API quota is used up for now. Wait, raise the limit in Google AI Studio, or use a voice engine on this Mac."
        case .busy: "Gemini TTS is busy. Wait a moment, then play the line again."
        case .offline: "Unable to reach Google. Check the internet connection, or use a voice engine on this Mac."
        case .noAudio(let said): "Gemini TTS answered without audio\(said.map { ": " + $0 } ?? ""). Try rewording the line or another voice."
        case .server(let code, let message): "Gemini TTS couldn’t make the audio (\(code))\(message.isEmpty ? "" : ": " + message)."
        }
    }
}
