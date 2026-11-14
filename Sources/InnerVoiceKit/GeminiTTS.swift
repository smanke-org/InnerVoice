import Foundation

/// Google's Gemini TTS (the cloud engine "gemini"): its voices, how a line's tone becomes a
/// spoken direction, and the request/response JSON of the Interactions API
/// (`POST v1beta/interactions`, checked against Google's docs 2026-11-14). Pure logic, so
/// the Mac and the tests share it; the network call lives in the app (`GeminiVoices`).
public enum GeminiTTS {
    public static let host = URL(string: "https://generativelanguage.googleapis.com/v1beta/")!

    /// The two TTS models. Flash acts best; Flash-Lite costs about two thirds as much.
    public enum Model: String, CaseIterable, Identifiable, Sendable {
        case flash = "gemini-3.8-flash-tts"
        case flashLite = "gemini-3.8-flash-lite-tts"

        public var id: String { rawValue }
        public var title: String {
            switch self {
            case .flash: "Gemini 3.8 Flash TTS (most expressive)"
            case .flashLite: "Gemini 3.8 Flash-Lite TTS (lower cost)"
            }
        }

        /// Dollars per second of audio on Google's paid Standard tier (25 audio tokens a
        /// second; text input is a rounding error beside it). Through 2026; Google doubles
        /// these on 2027-01-01.
        public func dollarsPerSecond(on date: Date = .now) -> Double {
            let perMillion: Double = switch self {
            case .flash: 9
            case .flashLite: 6
            }
            return perMillion * (date >= priceRise ? 2 : 1) * 25 / 1_000_000
        }
    }

    static let priceRise = DateComponents(calendar: Calendar(identifier: .gregorian), timeZone: TimeZone(identifier: "UTC"),
                                          year: 2027, month: 1, day: 1).date!

    /// Rough seconds of audio for a piece of text (about 15 characters a second for a
    /// narrated book), for cost estimates.
    public static func estimatedSeconds(characters: Int) -> Double { Double(characters) / 15 }

    /// Google's 30 prebuilt voices, with the one-word character Google gives each. All
    /// speak every language Gemini TTS does.
    public static let voices: [VoiceInfo] = [
        ("Zephyr", VoiceGender.female, "Bright"), ("Puck", .male, "Upbeat"), ("Charon", .male, "Informative"),
        ("Kore", .female, "Firm"), ("Fenrir", .male, "Excitable"), ("Leda", .female, "Youthful"),
        ("Orus", .male, "Firm"), ("Aoede", .female, "Breezy"), ("Callirrhoe", .female, "Easy-going"),
        ("Autonoe", .female, "Bright"), ("Enceladus", .male, "Breathy"), ("Iapetus", .male, "Clear"),
        ("Umbriel", .male, "Easy-going"), ("Algieba", .male, "Smooth"), ("Despina", .female, "Smooth"),
        ("Erinome", .female, "Clear"), ("Algenib", .male, "Gravelly"), ("Rasalgethi", .male, "Informative"),
        ("Laomedeia", .female, "Upbeat"), ("Achernar", .female, "Soft"), ("Alnilam", .male, "Firm"),
        ("Schedar", .male, "Even"), ("Gacrux", .female, "Mature"), ("Pulcherrima", .female, "Forward"),
        ("Achird", .male, "Friendly"), ("Zubenelgenubi", .male, "Casual"), ("Vindemiatrix", .female, "Gentle"),
        ("Sadachbia", .male, "Lively"), ("Sadaltager", .male, "Knowledgeable"), ("Sulafat", .female, "Warm"),
    ].map { name, gender, detail in
        VoiceInfo(engine: .gemini, voiceID: name, name: name, gender: gender,
                  age: name == "Gacrux" ? .older : name == "Leda" ? .young : .adult,
                  accent: "Multilingual", quality: 2, detail: detail)
    }

    /// The direction Gemini gets for a line ("speaking-style" text, not read aloud):
    /// the tone, a thought's inner voice, and the narrator's or Claude's delivery hint.
    /// nil for an even narration line, so the voice reads it plainly.
    public static func style(tone: Tone?, kind: LineKind?, delivery: String?) -> String? {
        var parts: [String] = []
        if let tone, tone != .neutral { parts.append(tone.direction) }
        if kind == .thought { parts.append("quietly, as a private thought") }
        if let hint = delivery.flatMap(DeliveryPrompt.clean) { parts.append(hint) }
        return parts.isEmpty ? nil : parts.joined(separator: ", ")
    }

    /// The inline tag Gemini reads as a sound ("<sigh>"), put before the line.
    public static func tag(_ sound: VocalSound) -> String {
        switch sound {
        case .laugh: "<laugh>"
        case .chuckle: "<chuckle>"
        case .sigh: "<sigh>"
        case .gasp: "<gasp>"
        case .groan: "<groan>"
        case .sniffle: "<sob>"
        case .yawn: "<yawn>"
        case .cough: "<cough>"
        }
    }

    /// The request body for one piece of text in one voice: WAV back (24 kHz mono 16-bit).
    public static func requestBody(text: String, voice: String, style: String?, model: Model) -> Data {
        var content: [String: Any] = ["type": "text", "text": text]
        if let style { content["annotations"] = [["type": "speech_metadata", "style": style]] }
        let body: [String: Any] = [
            "model": model.rawValue,
            "input": [["type": "user_input", "content": [content]]],
            "response_format": ["type": "audio"],
            "generation_config": ["speech_config": [["voice": voice]]],
            "store": false,
        ]
        return (try? JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])) ?? Data()
    }

    public enum ResponseError: Error, Equatable {
        case noAudio(String?)
    }

    /// The audio from a reply: the last audio part of the model's output, base64 WAV (or raw
    /// 16-bit PCM). Google's text when it answered without audio (a refusal, say).
    public static func audio(fromResponse data: Data) throws -> Data {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw ResponseError.noAudio(nil) }
        let steps = root["steps"] as? [[String: Any]] ?? []
        var audio: Data?
        var text: String?
        for step in steps where (step["type"] as? String) == "model_output" {
            for part in step["content"] as? [[String: Any]] ?? [] {
                if (part["type"] as? String) == "audio", let b64 = part["data"] as? String, let d = Data(base64Encoded: b64) {
                    audio = d
                } else if let t = part["text"] as? String {
                    text = t
                }
            }
        }
        guard let audio, !audio.isEmpty else { throw ResponseError.noAudio(text) }
        return audio
    }

    /// 16-bit little-endian samples from a WAV file (its "data" chunk) or raw PCM.
    public static func pcm16(fromWAVOrRaw data: Data) -> Data {
        guard data.count > 12, data.prefix(4) == Data("RIFF".utf8), data[8..<12] == Data("WAVE".utf8) else { return data }
        var i = 12
        while i + 8 <= data.count {
            let id = data[i..<i + 4]
            let size = Int(data[i + 4]) | Int(data[i + 5]) << 8 | Int(data[i + 6]) << 16 | Int(data[i + 7]) << 24
            let start = i + 8
            if id == Data("data".utf8) { return data[start..<min(data.count, start + max(0, size))] }
            i = start + size + (size & 1)
        }
        return Data()
    }

    /// Google's error message from an error reply ({"error": {"message", "status"}}).
    public static func errorMessage(_ data: Data) -> (message: String, status: String)? {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let e = root["error"] as? [String: Any] else { return nil }
        return ((e["message"] as? String) ?? "", (e["status"] as? String) ?? "")
    }
}
