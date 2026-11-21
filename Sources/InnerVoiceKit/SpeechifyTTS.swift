import Foundation

/// Speechify's text-to-speech API (api.speechify.ai, the third cloud voice engine): request
/// and response shapes, the voice list, and how a tone becomes SSML. The app's
/// `SpeechifyVoices` sends the requests, only for a book that allows Speechify.
///
/// The current models (Simba 3.x) ignore Speechify's emotion tag, so a tone is acted with
/// SSML prosody (pace, pitch, loudness) instead; delivery hints and vocal sounds aren't sent.
public enum SpeechifyTTS {
    public static let host = URL(string: "https://api.speechify.ai/v1/")!
    /// The recommended model (48 languages).
    public static let model = "simba-3.5"
    /// Dollars per million characters for Simba 3.5 at the Starter plan's rate (speechify.ai,
    /// November 2026: $20; 3.0 and 3.2 are $10). Only for the export's estimate.
    public static let dollarsPerMillionCharacters = 20.0

    public static func estimatedDollars(characters: Int) -> Double {
        Double(characters) * dollarsPerMillionCharacters / 1_000_000
    }

    /// /v1/audio/speech takes up to 2,000 spoken characters a request.
    public static let chunkLimit = 1800

    // MARK: Voices

    struct VoiceList: Decodable {
        struct Voice: Decodable {
            let id: String
            let display_name: String
            let gender: String?
            let locale: String?
            let type: String?
            let tags: [String]?
            let staff_pick_rank: Int?
        }
        let voices: [Voice]
        let next_cursor: String?
        let has_more: Bool?
    }

    /// One page of GET /v1/voices: the shared (ready-made) English voices, and the cursor for
    /// the next page. Personal voices (clones) are left out: they're never cast.
    public static func voices(fromResponse data: Data) throws -> (voices: [VoiceInfo], next: String?) {
        let list = try JSONDecoder().decode(VoiceList.self, from: data)
        let voices = list.voices.filter { ($0.type ?? "shared") == "shared" }.map { v in
            let tags = (v.tags ?? []).map { $0.lowercased() }
            let age: VoiceAge = tags.contains { $0.contains("child") || $0.contains("kid") } ? .child
                : tags.contains { $0.contains("old") || $0.contains("senior") || $0.contains("elder") } ? .older
                : tags.contains { $0.contains("young") || $0.contains("teen") } ? .young : .adult
            let detail = (v.tags ?? []).filter { !$0.contains(":") }.prefix(3).map { $0.prefix(1).uppercased() + $0.dropFirst() }
            return VoiceInfo(engine: .speechify, voiceID: v.id, name: v.display_name,
                             gender: v.gender == "female" ? .female : v.gender == "male" ? .male : .neutral,
                             age: age, accent: accent(v.locale ?? ""), quality: v.staff_pick_rank == nil ? 1 : 2,
                             detail: detail.joined(separator: ", "))
        }
        return (voices, (list.has_more ?? false) ? list.next_cursor : nil)
    }

    /// "en-GB" → "British".
    public static func accent(_ locale: String) -> String {
        let region = locale.split(separator: "-").dropFirst().first.map(String.init)?.uppercased() ?? ""
        return [
            "US": "American", "GB": "British", "AU": "Australian", "CA": "Canadian", "IE": "Irish",
            "IN": "Indian", "NZ": "New Zealand", "ZA": "South African", "SG": "Singaporean", "PH": "Filipino",
        ][region] ?? (locale.isEmpty ? "English" : locale)
    }

    // MARK: Speech

    /// How a tone is acted with SSML prosody (rate %, pitch %, volume); nil for even reading.
    public static func prosody(tone: Tone?, kind: LineKind?) -> (rate: Int, pitch: Int, volume: String?)? {
        if kind == .thought, tone == nil || tone == .neutral { return (-3, 0, "soft") }
        switch tone {
        case .excited?: return (8, 6, "loud")
        case .confused?: return (-5, 2, nil)
        case .sad?: return (-10, -5, "soft")
        case .angry?: return (5, 3, "loud")
        case .whispered?: return (-8, -2, "x-soft")
        case .scared?: return (7, 5, nil)
        case .tender?: return (-7, -2, "soft")
        case .sarcastic?: return (-4, -1, nil)
        case .neutral?, nil: return nil
        }
    }

    /// The text as SSML: escaped, with the tone's prosody around it. Narration (kind nil)
    /// is always read evenly.
    public static func ssml(_ text: String, tone: Tone?, kind: LineKind?) -> String {
        let escaped = text
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
        guard kind != nil, let p = prosody(tone: tone, kind: kind) else { return "<speak>\(escaped)</speak>" }
        func signed(_ n: Int) -> String { n >= 0 ? "+\(n)%" : "\(n)%" }
        let volume = p.volume.map { " volume=\"\($0)\"" } ?? ""
        return "<speak><prosody rate=\"\(signed(p.rate))\" pitch=\"\(signed(p.pitch))\"\(volume)>\(escaped)</prosody></speak>"
    }

    /// The JSON for POST /v1/audio/speech: WAV at 24 kHz.
    public static func requestBody(text: String, voice: String, tone: Tone?, kind: LineKind?) -> Data {
        let body: [String: Any] = [
            "input": ssml(text, tone: tone, kind: kind),
            "voice_id": voice,
            "model": model,
            "audio_format": "wav",
            "output_format": "wav_24000",
        ]
        return (try? JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])) ?? Data()
    }

    public enum ResponseError: Error, Equatable { case noAudio }

    /// The audio file from a reply ({"audio_data": base64, "billable_characters_count": n}).
    public static func audio(fromResponse data: Data) throws -> (audio: Data, billable: Int?) {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let b64 = json["audio_data"] as? String, let audio = Data(base64Encoded: b64), !audio.isEmpty
        else { throw ResponseError.noAudio }
        return (audio, json["billable_characters_count"] as? Int)
    }

    /// Speechify's error from an error reply ({"error": {"code", "message"}}).
    public static func errorMessage(_ data: Data) -> (code: String, message: String)? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let e = json["error"] as? [String: Any] else { return nil }
        return (e["code"] as? String ?? "", e["message"] as? String ?? "")
    }
}
