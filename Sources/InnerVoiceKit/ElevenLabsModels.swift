import Foundation

/// An ElevenLabs text-to-speech model the narrator can choose (Settings › Voices ›
/// ElevenLabs). The list comes from the account (`GET /v1/models`), so newer models show
/// up on their own; `known` stands in until the first fetch.
public struct ElevenLabsModel: Codable, Hashable, Identifiable, Sendable {
    public var id: String
    public var name: String
    public var detail: String
    /// Credits per character (ElevenLabs' `character_cost_multiplier`): 1 for most models,
    /// 0.5 for Flash and Turbo.
    public var creditsPerCharacter: Double
    /// The longest text one request may carry.
    public var maxCharacters: Int

    public init(id: String, name: String, detail: String = "", creditsPerCharacter: Double = 1, maxCharacters: Int = 2000) {
        self.id = id; self.name = name; self.detail = detail
        self.creditsPerCharacter = creditsPerCharacter; self.maxCharacters = maxCharacters
    }

    public static let defaultID = "eleven_v4"

    /// Audio tags ([angry], [whispers], [sighs]) work only in the v3 family and later; other
    /// models would read them aloud, so they're left out.
    public var supportsAudioTags: Bool { Self.supportsAudioTags(id) }

    public static func supportsAudioTags(_ id: String) -> Bool {
        guard let r = id.range(of: #"^eleven_v(\d+)"#, options: .regularExpression) else { return false }
        return (Int(id[r].dropFirst("eleven_v".count)) ?? 0) >= 3
    }

    /// Credits for `characters` characters of text.
    public func credits(characters: Int) -> Int { Int((Double(characters) * creditsPerCharacter).rounded(.up)) }

    /// "1 credit per character" / "0.5 credits per character".
    public var rateText: String {
        let n = creditsPerCharacter == creditsPerCharacter.rounded() ? String(Int(creditsPerCharacter)) : String(creditsPerCharacter)
        return "\(n) credit\(creditsPerCharacter == 1 ? "" : "s") per character"
    }

    /// Before the first fetch (and if it fails).
    public static let known: [ElevenLabsModel] = [
        .init(id: "eleven_v4", name: "Eleven v4", detail: "Most expressive; understands tones and sounds", creditsPerCharacter: 1, maxCharacters: 5000),
        .init(id: "eleven_v3", name: "Eleven v3", detail: "Expressive; understands tones and sounds", creditsPerCharacter: 1, maxCharacters: 5000),
        .init(id: "eleven_multilingual_v2", name: "Eleven Multilingual v2", detail: "Steady and natural; no tones or sounds", creditsPerCharacter: 1, maxCharacters: 10000),
        .init(id: "eleven_turbo_v2_5", name: "Eleven Turbo v2.5", detail: "Faster and half the credits; no tones or sounds", creditsPerCharacter: 0.5, maxCharacters: 40000),
        .init(id: "eleven_flash_v2_5", name: "Eleven Flash v2.5", detail: "Fastest and half the credits; no tones or sounds", creditsPerCharacter: 0.5, maxCharacters: 40000),
    ]

    private struct Reply: Decodable {
        struct Rates: Decodable { var character_cost_multiplier: Double? }
        var model_id: String
        var name: String?
        var description: String?
        var can_do_text_to_speech: Bool?
        var maximum_text_length_per_request: Int?
        var model_rates: Rates?
    }

    /// The account's text-to-speech models from `GET /v1/models`, newest generation first
    /// (v-numbered models by number, then the rest as ElevenLabs lists them).
    public static func parse(_ data: Data) -> [ElevenLabsModel]? {
        guard let list = try? JSONDecoder().decode([Reply].self, from: data) else { return nil }
        let models = list.filter { $0.can_do_text_to_speech ?? false }.map { r -> ElevenLabsModel in
            let fallback = known.first { $0.id == r.model_id }
            let detail = fallback?.detail ?? (supportsAudioTags(r.model_id) ? "Understands tones and sounds" : "No tones or sounds")
            return ElevenLabsModel(id: r.model_id, name: r.name ?? fallback?.name ?? r.model_id, detail: detail,
                                   creditsPerCharacter: r.model_rates?.character_cost_multiplier ?? fallback?.creditsPerCharacter ?? 1,
                                   maxCharacters: r.maximum_text_length_per_request ?? fallback?.maxCharacters ?? 2000)
        }
        func generation(_ id: String) -> Int {
            guard let r = id.range(of: #"^eleven_v(\d+)$"#, options: .regularExpression) else { return 0 }
            return Int(id[r].dropFirst("eleven_v".count)) ?? 0
        }
        let sorted = models.enumerated().sorted { a, b in
            let ga = generation(a.element.id), gb = generation(b.element.id)
            return ga != gb ? ga > gb : a.offset < b.offset
        }.map(\.element)
        return sorted.isEmpty ? nil : sorted
    }
}
