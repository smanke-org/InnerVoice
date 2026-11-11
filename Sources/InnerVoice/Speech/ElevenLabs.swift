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

    /// The account's voices (premade and the person's own), from the last fetch. Cloned
    /// voices come first.
    private(set) var voices: [VoiceInfo] = []
    /// Voices cloned from recordings (here or on elevenlabs.io). Never given out by casting:
    /// a real person's voice goes only where the narrator puts it.
    private(set) var clonedIDs: Set<String> = []
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
            clonedIDs = Set(saved.filter { $0.cloned == true }.map(\.id))
        }
    }

    /// Clones first, then by name; saved for the next launch.
    private func setVoices(_ list: [VoiceInfo], cloned: Set<String>) {
        clonedIDs = cloned
        voices = list.sorted { a, b in
            let ca = cloned.contains(a.voiceID), cb = cloned.contains(b.voiceID)
            if ca != cb { return ca }
            return a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending
        }
        if !SystemHooks.isTestRun, let data = try? JSONEncoder().encode(voices.map { SavedVoice($0, cloned: cloned.contains($0.voiceID)) }) {
            UserDefaults.standard.set(data, forKey: Self.voicesKey)
        }
    }

    /// Voices casting may choose from: every voice except clones.
    func castable(_ list: [VoiceInfo]) -> [VoiceInfo] {
        clonedIDs.isEmpty ? list : list.filter { $0.engine != .elevenlabs || !clonedIDs.contains($0.voiceID) }
    }

    /// Voice cloning is offered only to a book that uses ElevenLabs: set up, allowed for
    /// this book, and the book's engine (or one of Mix Engines') is ElevenLabs.
    static func cloningOffered(for project: Project) -> Bool {
        guard shared.isSetUp, project.showsVoices, project.settings.cloudVoicesAllowed != nil else { return false }
        let engine = project.engineInUse
        return engine == .elevenlabs || (engine == .mixed && EngineSwitches.shared.isOn(.elevenlabs))
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
        clonedIDs = []
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
        var cloned: Set<String> = []
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
                guard var info = Self.voice(from: v) else { continue }
                if Self.isClone(v) {
                    cloned.insert(info.voiceID)
                    info.detail = "Cloned"
                }
                found.append(info)
            }
            token = (obj["has_more"] as? Bool ?? false) ? obj["next_page_token"] as? String : nil
        } while token != nil && found.count < 1000
        setVoices(found, cloned: cloned)
        return voices
    }

    /// Instant and Professional Voice Cloning both make a voice from a real person's recordings.
    static func isClone(_ v: [String: Any]) -> Bool {
        ["cloned", "professional"].contains((v["category"] as? String)?.lowercased() ?? "")
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
        setVoices(voices.filter { $0.voiceID != id } + [info], cloned: clonedIDs)
        ActivityStore.shared.record(ActivityKey.elevenLabsCharacters, count: text.count)
        return info
    }

    // MARK: Voice Cloning

    /// What the plan allows: Instant Voice Cloning needs a paid plan (Starter or above), and
    /// each clone takes one of the plan's custom voice slots.
    struct CloneAllowance: Sendable, Equatable {
        var canClone: Bool
        var slotsUsed: Int
        var slotLimit: Int
        var tier: String
        var slotsFull: Bool { slotLimit > 0 && slotsUsed >= slotLimit }
    }

    static let plansPage = URL(string: "https://elevenlabs.io/pricing")!
    /// ElevenLabs needs at least 10 seconds; 1 to 2 minutes clones best, and past 3 adds little.
    static let cloneMinimumSeconds: Double = 10
    static let cloneBestSeconds: ClosedRange<Double> = 60...120
    static let cloneMostSeconds: Double = 180

    /// Reads the plan (no book text, no audio).
    func cloneAllowance() async throws -> CloneAllowance {
        guard let key = apiKey else { throw SpeechError.engineUnavailable(.elevenlabs) }
        var req = URLRequest(url: Self.host.appending(path: "v1/user/subscription"))
        req.setValue(key, forHTTPHeaderField: "xi-api-key")
        let (data, response) = try await send(req)
        try Self.throwIfFailed(response, data)
        let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        let tier = obj["tier"] as? String ?? ""
        return CloneAllowance(canClone: obj["can_use_instant_voice_cloning"] as? Bool ?? (tier.lowercased() != "free"),
                              slotsUsed: obj["voice_slots_used"] as? Int ?? 0,
                              slotLimit: obj["voice_limit"] as? Int ?? 0,
                              tier: tier)
    }

    /// Makes a voice from recordings of one person (Instant Voice Cloning) and saves it under
    /// the narrator's name for it in their ElevenLabs voices, where every project (and
    /// elevenlabs.io) can use it. The recordings and the name leave the Mac here, and only
    /// after the narrator has confirmed they have the right to clone this voice. No book text
    /// is sent.
    func cloneVoice(name: String, samples: [URL], removeBackgroundNoise: Bool, labels: [String: String], rightsConfirmed: Bool) async throws -> VoiceInfo {
        guard rightsConfirmed else { throw ElevenLabsError.cloneNeedsConsent }
        guard let key = apiKey, consented != nil else { throw SpeechError.engineUnavailable(.elevenlabs) }
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { throw ElevenLabsError.cloneNeedsName }
        guard !samples.isEmpty else { throw ElevenLabsError.cloneNeedsSample }
        var form = MultipartForm()
        form.field("name", name)
        form.field("description", "Cloned in InnerVoice")
        form.field("remove_background_noise", removeBackgroundNoise ? "true" : "false")
        if !labels.isEmpty, let json = try? JSONSerialization.data(withJSONObject: labels), let s = String(data: json, encoding: .utf8) {
            form.field("labels", s)
        }
        for url in samples {
            let data = try await Task.detached { try Data(contentsOf: url) }.value
            form.file("files", filename: url.lastPathComponent, contentType: Self.audioType(url), data: data)
        }
        var req = URLRequest(url: Self.host.appending(path: "v1/voices/add"))
        req.httpMethod = "POST"
        req.timeoutInterval = 300
        req.setValue(key, forHTTPHeaderField: "xi-api-key")
        req.setValue(form.contentType, forHTTPHeaderField: "Content-Type")
        req.httpBody = form.finish()
        let (data, response) = try await send(req)
        try Self.throwIfFailed(response, data)
        let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        guard let id = obj["voice_id"] as? String else { throw ElevenLabsError.server(0, "The voice wasn’t saved") }
        var info = Self.voice(from: ["voice_id": id, "name": name, "labels": labels])
            ?? VoiceInfo(engine: .elevenlabs, voiceID: id, name: name, gender: .neutral, age: .adult, accent: "American")
        info.name = name
        info.detail = "Cloned"
        setVoices(voices.filter { $0.voiceID != id } + [info], cloned: clonedIDs.union([id]))
        ActivityStore.shared.record(ActivityKey.voicesCloned)
        return info
    }

    /// Renames a voice in the narrator's ElevenLabs account (every project sees the new name).
    func renameVoice(_ id: String, to name: String) async throws {
        guard let key = apiKey else { throw SpeechError.engineUnavailable(.elevenlabs) }
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { throw ElevenLabsError.cloneNeedsName }
        var form = MultipartForm()
        form.field("name", name)
        var req = URLRequest(url: Self.host.appending(path: "v1/voices/\(id)/edit"))
        req.httpMethod = "POST"
        req.setValue(key, forHTTPHeaderField: "xi-api-key")
        req.setValue(form.contentType, forHTTPHeaderField: "Content-Type")
        req.httpBody = form.finish()
        let (data, response) = try await send(req)
        try Self.throwIfFailed(response, data)
        setVoices(voices.map { v in
            guard v.voiceID == id else { return v }
            var r = v
            r.name = name
            return r
        }, cloned: clonedIDs)
    }

    /// Deletes a voice from the narrator's ElevenLabs account, which frees its voice slot.
    /// Characters that used it, in any book, need another voice.
    func deleteVoice(_ id: String) async throws {
        guard let key = apiKey else { throw SpeechError.engineUnavailable(.elevenlabs) }
        var req = URLRequest(url: Self.host.appending(path: "v1/voices/\(id)"))
        req.httpMethod = "DELETE"
        req.setValue(key, forHTTPHeaderField: "xi-api-key")
        let (data, response) = try await send(req)
        try Self.throwIfFailed(response, data)
        setVoices(voices.filter { $0.voiceID != id }, cloned: clonedIDs.subtracting([id]))
    }

    nonisolated static func audioType(_ url: URL) -> String {
        switch url.pathExtension.lowercased() {
        case "mp3": "audio/mpeg"
        case "wav", "wave": "audio/wav"
        case "m4a", "mp4", "aac": "audio/mp4"
        case "flac": "audio/flac"
        case "ogg", "oga": "audio/ogg"
        case "aif", "aiff": "audio/aiff"
        case "caf": "audio/x-caf"
        default: "application/octet-stream"
        }
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
        case _ where status.contains("voice_limit") || status.contains("voice_slots"): throw ElevenLabsError.voiceSlotsFull
        case _ where status.contains("voice_cloning") || status.contains("can_not_use_instant"): throw ElevenLabsError.cloningNeedsPaidPlan
        default:
            if status.contains("quota") { throw ElevenLabsError.outOfCredits }
            throw ElevenLabsError.server(http.statusCode, message)
        }
    }

    /// A saved voice list entry (VoiceInfo isn't Codable).
    private struct SavedVoice: Codable {
        var id, name, gender, age, accent, detail: String
        /// nil in lists saved before cloning.
        var cloned: Bool?
        init(_ v: VoiceInfo, cloned: Bool) {
            id = v.voiceID; name = v.name; gender = v.gender.rawValue; age = v.age.rawValue; accent = v.accent; detail = v.detail
            self.cloned = cloned
        }
        var info: VoiceInfo {
            VoiceInfo(engine: .elevenlabs, voiceID: id, name: name, gender: VoiceGender(rawValue: gender) ?? .neutral,
                      age: VoiceAge(rawValue: age) ?? .adult, accent: accent, quality: 3, detail: detail)
        }
    }
}

enum ElevenLabsError: LocalizedError {
    case bookNotAllowed, badKey, outOfCredits, busy, offline, descriptionTooShort
    case cloneNeedsConsent, cloneNeedsName, cloneNeedsSample, cloningNeedsPaidPlan, voiceSlotsFull
    case server(Int, String)

    var errorDescription: String? {
        switch self {
        case .bookNotAllowed: "This book hasn’t been allowed to use ElevenLabs. Choose ElevenLabs on the Voices page to allow it."
        case .badKey: "ElevenLabs didn’t accept the API key. Check it in Settings › Voices."
        case .outOfCredits: "Your ElevenLabs plan is out of credits for this month. Add credits at elevenlabs.io, or use a voice engine on this Mac."
        case .busy: "ElevenLabs is busy. Wait a moment, then play the line again."
        case .descriptionTooShort: "There isn’t enough to design a voice from. Add a few words about how this character sounds to their Voice Notes."
        case .cloneNeedsConsent: "Confirm that you have the right and the consent to clone this voice."
        case .cloneNeedsName: "Give the voice a name."
        case .cloneNeedsSample: "Add a recording of the voice first."
        case .cloningNeedsPaidPlan: "Voice cloning needs a paid ElevenLabs plan (Starter or above). Change plans at elevenlabs.io, then try again."
        case .voiceSlotsFull: "All of your ElevenLabs plan’s voice slots are in use. Delete a voice you no longer need in Settings › Voices, or at elevenlabs.io."
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

/// A multipart/form-data body, for ElevenLabs' voice endpoints (they take files).
struct MultipartForm {
    let boundary = "InnerVoice-" + UUID().uuidString
    private var body = Data()

    var contentType: String { "multipart/form-data; boundary=\(boundary)" }

    mutating func field(_ name: String, _ value: String) {
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
    }

    mutating func file(_ name: String, filename: String, contentType: String, data: Data) {
        let safe = filename.replacingOccurrences(of: "\"", with: "'").replacingOccurrences(of: "\r", with: "").replacingOccurrences(of: "\n", with: "")
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"; filename=\"\(safe)\"\r\nContent-Type: \(contentType)\r\n\r\n".utf8))
        body.append(data)
        body.append(Data("\r\n".utf8))
    }

    func finish() -> Data {
        body + Data("--\(boundary)--\r\n".utf8)
    }
}
