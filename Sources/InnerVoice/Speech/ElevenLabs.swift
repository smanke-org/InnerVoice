@preconcurrency import AVFoundation
import Foundation
import InnerVoiceKit
import Observation
import Security

/// ElevenLabs, the one cloud voice engine (eleven_v4). Unlike everything else in InnerVoice,
/// it sends text off the Mac: each line or piece of narration it speaks goes to ElevenLabs'
/// servers. So it's off until the narrator sets it up (an API key and a clear yes), and each
/// book must allow it too (`ProjectSettings.cloudVoicesAllowed`). Nothing is sent otherwise.
@Observable
@MainActor
final class ElevenLabs {
    static let shared = ElevenLabs()

    static let model = "eleven_v4"
    static let host = URL(string: "https://api.elevenlabs.io")!
    static let keysPage = URL(string: "https://elevenlabs.io/app/settings/api-keys")!
    static let signUpPage = URL(string: "https://elevenlabs.io/sign-up")!
    static let privacyPage = URL(string: "https://elevenlabs.io/privacy-policy")!
    private static let consentKey = "elevenlabs.consent"
    private static let voicesKey = "elevenlabs.voices"

    /// The account's voices (premade and the person's own), from the last fetch.
    private(set) var voices: [VoiceInfo] = []
    /// Bumped when the key or consent changes.
    private(set) var revision = 0

    /// Requests go through this session; the self-test swaps in a fake server.
    @ObservationIgnored var session: URLSession = .shared
    /// Test runs keep the key in memory and never touch the person's keychain.
    @ObservationIgnored private var testKey: String?

    private init() {
        if let data = UserDefaults.standard.data(forKey: Self.voicesKey), !SystemHooks.isTestRun,
           let saved = try? JSONDecoder().decode([SavedVoice].self, from: data) {
            voices = saved.map(\.info)
        }
    }

    // MARK: Setup

    /// Set up: a key, and the narrator's yes to sending text to ElevenLabs.
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

    @ObservationIgnored private var testConsent: Date?

    /// The key and the yes, read once and kept in memory. Views ask whether ElevenLabs is set
    /// up many times on every redraw (every engine check does), and each keychain read is a
    /// round trip to another process (about 0.8 ms): on a big cast that froze the window for
    /// a few hundred milliseconds at a time. `save` keeps the copies current.
    @ObservationIgnored private var cachedKey: String??
    @ObservationIgnored private var cachedConsent: Date??
    /// Keychain reads so far, for the self-test.
    @ObservationIgnored private(set) var keychainReads = 0

    var apiKey: String? {
        _ = revision
        if SystemHooks.isTestRun { return testKey }
        if let k = cachedKey { return k }
        keychainReads += 1
        let k = Keychain.read(account: "elevenlabs")
        cachedKey = .some(k)
        return k
    }

    func save(key: String, consent: Bool) {
        let key = key.trimmingCharacters(in: .whitespacesAndNewlines)
        if SystemHooks.isTestRun {
            testKey = key.isEmpty ? nil : key
            testConsent = consent ? .now : nil
        } else {
            if key.isEmpty { Keychain.delete(account: "elevenlabs") } else { Keychain.write(key, account: "elevenlabs") }
            let date = consent ? Date() : nil
            UserDefaults.standard.set(date, forKey: Self.consentKey)
            cachedKey = .some(key.isEmpty ? nil : key)
            cachedConsent = .some(date)
        }
        revision += 1
    }

    /// Forgets the key, the yes and the voice list. Books keep their permission, but nothing
    /// is sent until ElevenLabs is set up again.
    func disconnect() {
        save(key: "", consent: false)
        voices = []
        UserDefaults.standard.removeObject(forKey: Self.voicesKey)
    }

    // MARK: Account

    struct Account: Sendable {
        var tier: String
        var used: Int
        var limit: Int
    }

    /// Checks a key and returns the plan's credits. Sends no text from the book.
    func check(key: String) async throws -> Account {
        var req = URLRequest(url: Self.host.appending(path: "v1/user/subscription"))
        req.setValue(key.trimmingCharacters(in: .whitespacesAndNewlines), forHTTPHeaderField: "xi-api-key")
        let (data, response) = try await send(req)
        try Self.throwIfFailed(response, data)
        let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        return Account(tier: obj["tier"] as? String ?? "", used: obj["character_count"] as? Int ?? 0, limit: obj["character_limit"] as? Int ?? 0)
    }

    /// Fetches the account's voices (names and labels only; no book text is sent).
    @discardableResult
    func refreshVoices() async throws -> [VoiceInfo] {
        guard let key = apiKey else { throw SpeechError.engineUnavailable(.elevenlabs) }
        var found: [VoiceInfo] = []
        var token: String?
        repeat {
            var c = URLComponents(url: Self.host.appending(path: "v2/voices"), resolvingAgainstBaseURL: false)!
            c.queryItems = [URLQueryItem(name: "page_size", value: "100")] + (token.map { [URLQueryItem(name: "next_page_token", value: $0)] } ?? [])
            var req = URLRequest(url: c.url!)
            req.setValue(key, forHTTPHeaderField: "xi-api-key")
            let (data, response) = try await send(req)
            try Self.throwIfFailed(response, data)
            let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
            for v in obj["voices"] as? [[String: Any]] ?? [] {
                if let info = Self.voice(from: v) { found.append(info) }
            }
            token = (obj["has_more"] as? Bool ?? false) ? obj["next_page_token"] as? String : nil
        } while token != nil && found.count < 1000
        voices = found.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        if !SystemHooks.isTestRun, let data = try? JSONEncoder().encode(voices.map(SavedVoice.init)) {
            UserDefaults.standard.set(data, forKey: Self.voicesKey)
        }
        return voices
    }

    static func voice(from v: [String: Any]) -> VoiceInfo? {
        guard let id = v["voice_id"] as? String, let name = v["name"] as? String else { return nil }
        let labels = v["labels"] as? [String: String] ?? [:]
        let gender: VoiceGender = switch labels["gender"]?.lowercased() {
        case "female"?: .female
        case "male"?: .male
        default: .neutral
        }
        let age: VoiceAge = switch labels["age"]?.lowercased() {
        case "young"?: .young
        case "old"?, "older"?, "middle_aged"?, "middle aged"?: (labels["age"]?.lowercased().hasPrefix("middle") == true ? .adult : .older)
        default: .adult
        }
        let accent = (labels["accent"] ?? "American").split(separator: "-").map { $0.capitalized }.joined(separator: " ")
        let detail = [labels["description"], labels["use_case"] ?? labels["use case"]].compactMap { $0 }
            .map { $0.replacingOccurrences(of: "_", with: " ").capitalized }.joined(separator: ", ")
        // Shortened names like "Rachel - calm narrator" keep the first part.
        let short = name.components(separatedBy: " - ").first ?? name
        return VoiceInfo(engine: .elevenlabs, voiceID: id, name: short, gender: gender, age: age, accent: accent, quality: 3, detail: detail)
    }

    // MARK: Voice Design

    static let designModel = "eleven_ttv_v3"
    /// ElevenLabs takes a description of 20 to 1,000 characters.
    static let designLimit = 1000

    /// Designs a new voice from a description (a character's Voice Notes) and saves it in
    /// the person's ElevenLabs voices. The description and the name leave the Mac here, only
    /// when the book allows ElevenLabs. Uses credits and one of the plan's voice slots.
    func designVoice(name: String, description: String, labels: [String: String], allowed: Bool) async throws -> VoiceInfo {
        guard allowed else { throw ElevenLabsError.bookNotAllowed }
        guard let key = apiKey, consented != nil else { throw SpeechError.engineUnavailable(.elevenlabs) }
        let text = String(description.trimmingCharacters(in: .whitespacesAndNewlines).prefix(Self.designLimit))
        guard text.count >= 20 else { throw ElevenLabsError.descriptionTooShort }
        func post(_ path: String, _ body: [String: Any]) async throws -> [String: Any] {
            var req = URLRequest(url: Self.host.appending(path: path))
            req.httpMethod = "POST"
            req.timeoutInterval = 180
            req.setValue(key, forHTTPHeaderField: "xi-api-key")
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await send(req)
            try Self.throwIfFailed(response, data)
            return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        }
        // 1. Previews of voices that fit the description; the first is kept.
        let design = try await post("v1/text-to-voice/design", [
            "voice_description": text, "model_id": Self.designModel, "auto_generate_text": true,
            "seed": Self.seed(text),
        ])
        guard let generated = (design["previews"] as? [[String: Any]])?.first?["generated_voice_id"] as? String else {
            throw ElevenLabsError.server(0, "No voice came back")
        }
        try Task.checkCancellation()
        // 2. Saved as a voice of the person's own.
        let saved = try await post("v1/text-to-voice", [
            "voice_name": name, "voice_description": text, "generated_voice_id": generated, "labels": labels,
        ])
        guard let id = saved["voice_id"] as? String else { throw ElevenLabsError.server(0, "The voice wasn’t saved") }
        var info = Self.voice(from: ["voice_id": id, "name": name, "labels": labels]) ?? VoiceInfo(engine: .elevenlabs, voiceID: id, name: name, gender: .neutral, age: .adult, accent: "American")
        info.detail = "Designed from Voice Notes"
        voices.removeAll { $0.voiceID == id }
        voices.append(info)
        voices.sort { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        if !SystemHooks.isTestRun, let data = try? JSONEncoder().encode(voices.map(SavedVoice.init)) {
            UserDefaults.standard.set(data, forKey: Self.voicesKey)
        }
        ActivityStore.shared.record(ActivityKey.elevenLabsCharacters, count: text.count)
        return info
    }

    /// The same text in the same voice comes out the same. Kept under 2^31: some ElevenLabs
    /// endpoints reject larger seeds (422), which made about half of all requests fail.
    nonisolated static func seed(_ s: String) -> Int { Int(ModelSpeech.seed(s) % 2_147_483_647) }

    /// Text with nothing to say ("…", a lone dash) is rejected by ElevenLabs: not sent.
    nonisolated static func hasWords(_ s: String) -> Bool { s.contains { $0.isLetter || $0.isNumber } }

    // MARK: Speech

    /// What ElevenLabs is sent: the text, with v4 audio tags for the tone and any sound the
    /// speech tag asks for.
    static func text(for request: SpeechRequest) -> String {
        var tags: [String] = []
        if request.kind != nil, let tag = request.tone.audioTag { tags.append(tag) }
        if let sound = request.sound { tags.append(sound.audioTag) }
        return (tags + [request.text]).joined(separator: " ")
    }

    /// One line (or piece of narration) in a voice. The text leaves the Mac here, and only
    /// here, and only when the book allows it.
    func render(_ request: SpeechRequest, allowed: Bool) async throws -> AVAudioPCMBuffer {
        guard allowed else { throw ElevenLabsError.bookNotAllowed }
        guard let key = apiKey, consented != nil else { throw SpeechError.engineUnavailable(.elevenlabs) }
        var pieces: [Data] = []
        let gapCount = Int(SpeechAudio.format.sampleRate * 0.18)
        for piece in ModelSpeech.chunks(Self.text(for: request), limit: 2000) where Self.hasWords(piece.replacingOccurrences(of: #"\[[^\]]*\]"#, with: "", options: .regularExpression)) {
            try Task.checkCancellation()
            var c = URLComponents(url: Self.host.appending(path: "v1/text-to-speech/\(request.choice.voiceID)"), resolvingAgainstBaseURL: false)!
            c.queryItems = [URLQueryItem(name: "output_format", value: "pcm_24000")]
            var req = URLRequest(url: c.url!)
            req.httpMethod = "POST"
            req.timeoutInterval = 90
            req.setValue(key, forHTTPHeaderField: "xi-api-key")
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            var body: [String: Any] = ["text": piece, "model_id": Self.model, "seed": Self.seed(piece + request.choice.voiceID)]
            if let rate = request.choice.rate, rate != 1 { body["voice_settings"] = ["speed": min(1.2, max(0.7, rate))] }
            req.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await send(req)
            try Self.throwIfFailed(response, data)
            ActivityStore.shared.record(ActivityKey.elevenLabsCharacters, count: piece.count)
            pieces.append(data)
        }
        if pieces.isEmpty { return try ModelSpeech.buffer([Float](repeating: 0, count: Int(SpeechAudio.format.sampleRate * 0.2))) }
        // Decoding and leveling seconds of audio happens off the main thread, so the window
        // stays responsive while lines arrive.
        let leveled = await Task.detached(priority: .userInitiated) { () -> [Float] in
            var samples: [Float] = []
            for (n, data) in pieces.enumerated() {
                if n > 0 { samples += [Float](repeating: 0, count: gapCount) }
                samples += ElevenLabs.pcm16(data)
            }
            return ModelSpeech.level(samples)
        }.value
        return try ModelSpeech.buffer(leveled, volume: request.kind == .thought ? 0.85 : 1)
    }

    /// 16-bit little-endian mono PCM to floats.
    nonisolated static func pcm16(_ data: Data) -> [Float] {
        data.withUnsafeBytes { raw in
            let n = raw.count / 2
            var out = [Float](repeating: 0, count: n)
            for i in 0..<n { out[i] = Float(Int16(littleEndian: raw.loadUnaligned(fromByteOffset: i * 2, as: Int16.self))) / 32768 }
            return out
        }
    }

    private func send(_ req: URLRequest) async throws -> (Data, URLResponse) {
        do {
            return try await session.data(for: req)
        } catch let e as URLError where e.code != .cancelled {
            throw ElevenLabsError.offline
        }
    }

    static func throwIfFailed(_ response: URLResponse, _ data: Data) throws {
        guard let http = response as? HTTPURLResponse else { throw ElevenLabsError.server(0, "") }
        guard !(200..<300).contains(http.statusCode) else { return }
        let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        let detail = obj?["detail"] as? [String: Any]
        let status = (detail?["status"] as? String ?? "").lowercased()
        // A 422 explains itself as a list: [{"loc": [...], "msg": "...", "type": "..."}].
        let list = (obj?["detail"] as? [[String: Any]])?.compactMap { item -> String? in
            guard let msg = item["msg"] as? String else { return nil }
            let field = (item["loc"] as? [Any])?.last.map { "\($0)" }
            return field.map { "\($0): \(msg)" } ?? msg
        }.joined(separator: "; ")
        let message = detail?["message"] as? String ?? (obj?["detail"] as? String) ?? list ?? ""
        SpeechLog.write("ElevenLabs \(http.statusCode) \(response.url?.path ?? ""): \(status) \(message.prefix(300))")
        switch http.statusCode {
        // A key without a permission (keys are scoped) says which one: show ElevenLabs' words.
        case 401 where status.contains("permission"): throw ElevenLabsError.server(401, message)
        case 401: throw status.contains("quota") ? ElevenLabsError.outOfCredits : ElevenLabsError.badKey
        case 402: throw ElevenLabsError.outOfCredits
        case 429: throw ElevenLabsError.busy
        default:
            if status.contains("quota") { throw ElevenLabsError.outOfCredits }
            throw ElevenLabsError.server(http.statusCode, message)
        }
    }

    /// A saved voice list entry (VoiceInfo isn't Codable).
    private struct SavedVoice: Codable {
        var id, name, gender, age, accent, detail: String
        init(_ v: VoiceInfo) {
            id = v.voiceID; name = v.name; gender = v.gender.rawValue; age = v.age.rawValue; accent = v.accent; detail = v.detail
        }
        var info: VoiceInfo {
            VoiceInfo(engine: .elevenlabs, voiceID: id, name: name, gender: VoiceGender(rawValue: gender) ?? .neutral,
                      age: VoiceAge(rawValue: age) ?? .adult, accent: accent, quality: 3, detail: detail)
        }
    }
}

enum ElevenLabsError: LocalizedError {
    case bookNotAllowed, badKey, outOfCredits, busy, offline, descriptionTooShort
    case server(Int, String)

    var errorDescription: String? {
        switch self {
        case .bookNotAllowed: "This book hasn’t been allowed to use ElevenLabs. Choose ElevenLabs on the Voices page to allow it."
        case .badKey: "ElevenLabs didn’t accept the API key. Check it in Settings › Voices."
        case .outOfCredits: "Your ElevenLabs plan is out of credits for this month. Add credits at elevenlabs.io, or use a voice engine on this Mac."
        case .busy: "ElevenLabs is busy. Wait a moment, then play the line again."
        case .descriptionTooShort: "There isn’t enough to design a voice from. Add a few words about how this character sounds to their Voice Notes."
        case .offline: "Unable to reach ElevenLabs. Check the internet connection, or use a voice engine on this Mac."
        case .server(let code, let message): "ElevenLabs couldn’t make the audio (\(code))\(message.isEmpty ? "" : ": " + message)."
        }
    }
}

/// The ElevenLabs key in the login keychain.
enum Keychain {
    private static let service = "com.smanke.InnerVoice"

    static func read(account: String) -> String? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func write(_ value: String, account: String) {
        delete(account: account)
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecValueData as String: Data(value.utf8),
                                kSecAttrLabel as String: "InnerVoice ElevenLabs API key"]
        SecItemAdd(q as CFDictionary, nil)
    }

    static func delete(account: String) {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
        SecItemDelete(q as CFDictionary)
    }
}
