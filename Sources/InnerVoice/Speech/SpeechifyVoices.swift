@preconcurrency import AVFoundation
import Foundation
import InnerVoiceKit
import Observation

/// Speechify's API, the third cloud voice engine. Like ElevenLabs and Gemini TTS it sends
/// text off the Mac (to Speechify), so it's off until the narrator sets it up (an API key and
/// a clear yes) and each book must allow Speechify separately
/// (`ProjectSettings.speechifyVoicesAllowed`). Nothing is sent otherwise. Request/response
/// shapes live in the Kit (`SpeechifyTTS`).
@Observable
@MainActor
final class SpeechifyVoices {
    static let shared = SpeechifyVoices()

    static let keysPage = URL(string: "https://platform.speechify.ai/api-keys")!
    static let termsPage = URL(string: "https://speechify.ai/terms")!
    private static let consentKey = "speechify.consent"
    private static let voiceListKey = "speechify.voiceList"
    static let keychainAccount = "speechify"

    /// Bumped when the key, consent or voice list changes.
    private(set) var revision = 0

    /// Requests go through this session; the self-test swaps in a fake server.
    @ObservationIgnored var session: URLSession = .shared
    @ObservationIgnored private var testKey: String?
    @ObservationIgnored private var testConsent: Date?
    @ObservationIgnored private var testPages: [Data] = []
    /// Read once and kept in memory: views ask `isSetUp` on every redraw (see ElevenLabs).
    @ObservationIgnored private var cachedKey: String??
    @ObservationIgnored private var cachedConsent: Date??
    @ObservationIgnored private var cachedVoices: [VoiceInfo]?
    @ObservationIgnored private var refreshing = false

    /// Speechify's ready-made English voices, as last fetched (kept between runs). Fetched
    /// again in the background when there are none yet.
    var voices: [VoiceInfo] {
        _ = revision
        if let v = cachedVoices { return v }
        let pages = SystemHooks.isTestRun ? testPages : (UserDefaults.standard.array(forKey: Self.voiceListKey) as? [Data] ?? [])
        let v = pages.flatMap { (try? SpeechifyTTS.voices(fromResponse: $0).voices) ?? [] }
            .sorted { ($0.quality, $1.name) > ($1.quality, $0.name) }
        cachedVoices = v
        if v.isEmpty, isSetUp, !refreshing {
            Task { try? await refreshVoices() }
        }
        return v
    }

    // MARK: Setup

    /// Set up: a key, and the narrator's yes to sending text to Speechify.
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
    /// Speechify is set up again.
    func disconnect() { save(key: "", consent: false) }

    /// Checks a key by listing the voices (sends no text from the book), and keeps the list.
    func check(key: String) async throws {
        try await refreshVoices(key: key.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    /// Fetches the voice list (every page of shared English voices).
    func refreshVoices(key: String? = nil) async throws {
        guard let key = key ?? apiKey else { return }
        refreshing = true
        defer { refreshing = false }
        var pages: [Data] = []
        var cursor: String?
        repeat {
            var c = URLComponents(url: SpeechifyTTS.host.appending(path: "voices"), resolvingAgainstBaseURL: false)!
            c.queryItems = [URLQueryItem(name: "type", value: "shared"), URLQueryItem(name: "locale", value: "en"),
                            URLQueryItem(name: "limit", value: "200")] + (cursor.map { [URLQueryItem(name: "cursor", value: $0)] } ?? [])
            var req = URLRequest(url: c.url!)
            req.timeoutInterval = 30
            req.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            let (data, response) = try await send(req)
            try Self.throwIfFailed(response, data)
            pages.append(data)
            cursor = try SpeechifyTTS.voices(fromResponse: data).next
        } while cursor != nil && pages.count < 20
        if SystemHooks.isTestRun { testPages = pages } else { UserDefaults.standard.set(pages, forKey: Self.voiceListKey) }
        cachedVoices = nil
        revision += 1
    }

    // MARK: Speech

    /// One line (or piece of narration) in a voice. The text leaves the Mac here, and only
    /// here, and only when the book allows Speechify.
    func render(_ request: SpeechRequest, allowed: Bool) async throws -> AVAudioPCMBuffer {
        guard allowed else { throw SpeechifyError.bookNotAllowed }
        guard let key = apiKey, consented != nil else { throw SpeechError.engineUnavailable(.speechify) }
        var pieces: [Data] = []
        let gapCount = Int(SpeechAudio.format.sampleRate * 0.18)
        for piece in ModelSpeech.chunks(request.text, limit: SpeechifyTTS.chunkLimit) where ElevenLabs.hasWords(piece) {
            try Task.checkCancellation()
            var req = URLRequest(url: SpeechifyTTS.host.appending(path: "audio/speech"))
            req.httpMethod = "POST"
            req.timeoutInterval = 120
            req.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = SpeechifyTTS.requestBody(text: piece, voice: request.choice.voiceID, tone: request.tone, kind: request.kind)
            // The free plan allows about one request a second: on "busy", wait and try again.
            var tries = 0
            var reply: (Data, URLResponse)
            while true {
                try await SpeechifyPace.shared.waitTurn()
                reply = try await send(req)
                if let http = reply.1 as? HTTPURLResponse, http.statusCode == 429 || http.statusCode == 503, tries < 4 {
                    tries += 1
                    await SpeechifyPace.shared.limited(retryAfter: Self.retryAfter(http))
                    continue
                }
                break
            }
            try Self.throwIfFailed(reply.1, reply.0)
            let audio: (audio: Data, billable: Int?)
            do { audio = try SpeechifyTTS.audio(fromResponse: reply.0) } catch { throw SpeechifyError.noAudio }
            let pcm = GeminiTTS.pcm16(fromWAVOrRaw: audio.audio)
            ActivityStore.shared.record(ActivityKey.speechifyCharacters, count: piece.count)
            var usage = CloudUsageRecord(day: CloudUsageRecord.day(), book: request.usageBook ?? "", service: .speechify, model: SpeechifyTTS.model)
            usage.characters = audio.billable ?? piece.count
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
        // The tone's pace and pitch are in the SSML; the narrator's speed and the voice's
        // shade are applied afterwards (SpeechRenderer).
        return try ModelSpeech.buffer(leveled, volume: request.kind == .thought ? 0.85 : 1)
    }

    private func send(_ req: URLRequest) async throws -> (Data, URLResponse) {
        do {
            return try await session.data(for: req)
        } catch let e as URLError where e.code != .cancelled {
            throw SpeechifyError.offline
        }
    }

    static func retryAfter(_ http: HTTPURLResponse) -> Double? {
        http.value(forHTTPHeaderField: "Retry-After").flatMap(Double.init)
    }

    static func throwIfFailed(_ response: URLResponse, _ data: Data) throws {
        guard let http = response as? HTTPURLResponse else { throw SpeechifyError.server(0, "") }
        guard !(200..<300).contains(http.statusCode) else { return }
        let e = SpeechifyTTS.errorMessage(data)
        let message = e?.message ?? String(decoding: data.prefix(300), as: UTF8.self)
        switch (http.statusCode, e?.code ?? "") {
        case (401, _), (_, "unauthorized"): throw SpeechifyError.badKey
        case (402, _), (_, "payment_required"): throw SpeechifyError.outOfCharacters
        case (429, _), (502...504, _): throw SpeechifyError.busy(retryAfter: retryAfter(http))
        default: throw SpeechifyError.server(http.statusCode, message)
        }
    }
}

/// Spaces Speechify requests: none at first; after a "busy", one request a second (the free
/// plan's limit), after waiting out Speechify's Retry-After.
actor SpeechifyPace {
    static let shared = SpeechifyPace()
    private(set) var interval: Double = 0
    private var next = ContinuousClock.now

    func waitTurn() async throws {
        let now = ContinuousClock.now
        let slot = max(now, next)
        next = slot + .seconds(interval)
        if slot > now { try await Task.sleep(until: slot, clock: .continuous) }
    }

    func limited(retryAfter: Double?) {
        interval = max(interval, 1.05)
        let resume = ContinuousClock.now + .seconds(min(30, retryAfter ?? 2))
        if resume > next { next = resume }
        SpeechLog.write("Speechify: busy; one request every \(String(format: "%.2f", interval)) s")
    }
}

enum SpeechifyError: LocalizedError, Equatable {
    case bookNotAllowed, badKey, outOfCharacters, offline, noAudio
    case busy(retryAfter: Double?)
    case server(Int, String)

    var errorDescription: String? {
        switch self {
        case .bookNotAllowed: "This book hasn’t been allowed to use Speechify. Choose Speechify on the Voices page to allow it."
        case .badKey: "Speechify didn’t accept the API key. Check it in Settings › Voices."
        case .outOfCharacters: "Your Speechify API plan has no characters left. Wait for next month’s allowance, change plans on the Speechify API platform, or use a voice engine on this Mac."
        case .busy: "Speechify is busy. Wait a moment, then play the line again."
        case .offline: "Unable to reach Speechify. Check the internet connection, or use a voice engine on this Mac."
        case .noAudio: "Speechify answered without audio. Try rewording the line or another voice."
        case .server(let code, let message): "Speechify couldn’t make the audio (\(code))\(message.isEmpty ? "" : ": " + message)."
        }
    }
}
